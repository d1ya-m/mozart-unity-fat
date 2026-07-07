# Stage 1 — On-Device Keyframe Capture: Progress Report

**Branch:** `object-shaped-portals`
**Dates:** 2026-07-03
**Status:** Steps 1–3 **VERIFIED WORKING**. Step 4 (depth) **code complete, one bug
open** — diagnosis narrowed, debug instrumentation in place, awaiting next build.

> **Where to resume:** jump to [§7 Next Session — Start Here](#7-next-session--start-here).

---

## 0. Context — why this exists

The object-shaped-portals feature currently segments a **stale, pre-scanned** room
mesh (`mesh-3hz-4.obj` from the mesh server). The goal of this new pipeline is to
let the **headset capture the room live** and reconstruct a fresh mesh, so portals
track the *current* room, not an old scan. See
[object-shaped-portals-implementation.md](object-shaped-portals-implementation.md)
for the downstream portal system this feeds.

### The four-stage plan (only Stage 1 is in progress)

```
[1] CAPTURE (Quest, C#)  ─→  [2] RECONSTRUCT (PC, Open3D TSDF)
    keyframe depth + pose        RGB-D/pose → fresh room mesh.obj
          │                                 │
          ▼                                 ▼
[4] PORTALS (unchanged)  ←─  [3] SEGMENT (existing lccp_open3d.py)
    ObjectPicker + stencil       → clusterN.obj + clusters.json
```

- **Stage 1** (this doc): capture keyframes = `{depth image, camera pose, intrinsics}`,
  choosing keyframes by distance/angle novelty so redundant views are skipped.
- **Stages 2–4** reuse existing code (Open3D already a dependency; segmentation and
  runtime portals unchanged). ~80% of the overall pipeline is reuse.

### Decisions locked for the first cut

- **Depth-only** (no RGB) — reuses the proven `EnvironmentDepth` pipeline; untextured
  mesh is fine for LCCP segmentation. RGB is a later add.
- **`adb pull` to PC** — dev-only transport for now. User-friendly WiFi upload to the
  mesh server is a later step (server is the mentor's; access to be coordinated).
- **Depth method:** *global-shader depth blit* — a blit shader samples the SDK's OWN
  global `_EnvironmentDepthTexture` via its OWN `SampleEnvironmentDepthLinear`, so the
  metric conversion is the SDK's, not a reimplementation. No `internal` SDK APIs.

---

## 1. Files added / changed this stage

| File | What | Status |
|---|---|---|
| `Assets/Scripts/Debug/KeyframeCaptureManager.cs` | **Main new script.** Steps 1–4. | steps 1–3 done; step 4 debugging |
| `Assets/Materials/Shaders/EnvDepthCapture.shader` | Metric-depth blit (samples SDK global depth). | done; has debug modes |
| `captures/verify_poses.py` | Plots captured camera trajectory in Open3D to check the pose conversion. | done, used to verify §4 |
| `captures/verify_depth.py` | Prints per-frame depth min/median/max in metres. | done, used to diagnose §5 |
| `captures/` (pulled sessions) | On-device output pulled for verification (git-ignored / scratch). | — |

Nothing else in the project was modified. The capture component lives on its **own**
`KeyframeCapture` GameObject in `Assets/Scenes/current.unity` (kept separate from
`ObjectPicker`, which is the unrelated portal-selection script).

---

## 2. The build/test loop (how to iterate)

**Testing MUST be a built APK** — per §6 Bug B of the portals doc, OVR/XR poses are
NOT delivered over Quest Link in the editor. No in-editor iteration for this.

- **Package name:** `cz.fitvut.fat`
- **On-device output path:** `/sdcard/Android/data/cz.fitvut.fat/files/captures/<sessionId>/`
  (a fresh timestamped folder per run; the exact path is logged at startup).

**Read logs (Git Bash):**
```bash
ADB="/c/Program Files/Unity/Hub/Editor/6000.3.10f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
"$ADB" logcat -s Unity | grep -E "KFCAP"
```

**Pull captures — use PowerShell, NOT Git Bash.** Git Bash's MSYS mangles the
`/sdcard/...` path (rewrites it to `C:/Program Files/Git/sdcard/...`). PowerShell:
```powershell
$adb = "C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
& $adb pull /sdcard/Android/data/cz.fitvut.fat/files/captures/<SESSION> "$env:USERPROFILE\Documents\BRNO_internship\mozart-unity-fat\captures\<SESSION>"
```
> If Git Bash is unavoidable, prefix with `MSYS_NO_PATHCONV=1` or double the leading
> slash (`//sdcard/...`).

> ⚠️ Pulling the whole `captures` DIR (not a specific session) nests it as
> `captures/captures/...` — pull a specific `<SESSION>` folder to avoid that.

---

## 3. Steps 1–2 — Keyframe selection (VERIFIED ✅)

**Step 1 (pose heartbeat):** every second, logs the head (CenterEye) world pose via
`XR.InputTracking.GetLocalPosition/Rotation(XRNode.CenterEye)` transformed to world
through `OVRCameraRig.trackingSpace` — the same convention `ObjectPicker` uses.

**Step 2 (novelty selection):** the core "normalize distance/angle" logic
(`IsNovelViewpoint`, `KeyframeCaptureManager.cs`):

```csharp
// A viewpoint is NOVEL unless some existing keyframe is BOTH close in position
// AND similar in view angle ("already covered from here").
bool closeInPosition = Vector3.Distance(pos, kf.Position) < positionThresholdMeters; // 0.3 m
bool similarAngle     = Vector3.Angle(forward, kf.Forward) < angleThresholdDegrees;  // 20°
if (closeInPosition && similarAngle) return false; // skip
```

**Verified on-device (logcat):**
- Turning in place → `angle` test fires → captures (different look direction).
- Walking / crouching → `distance` test fires → captures.
- Standing still → same viewpoint → **skipped** (skip counter climbs, keyframe count
  holds). Exactly the redundant-frame rejection the mentor asked for.

**Fix applied — reject uninitialised pose:** the first frame(s) after launch report
the identity pose `(0,0,0)` before head tracking acquires. A guard
(`_headTrackingReady`, skips until `eyeLocal.sqrMagnitude >= 1e-4`) prevents writing a
bogus origin frame. Confirmed: `frame_0000` now starts at a real ~1.1 m eye height.

**Tunables** (Inspector): `positionThresholdMeters=0.3`, `angleThresholdDegrees=20`,
`maxKeyframes=300`. These are a bit eager for a small area (24 frames/min while mostly
turning in place) — fine for testing; raise them for a full room walk.

---

## 4. Step 3 — Pose + intrinsics → Open3D JSON (VERIFIED ✅)

Each accepted keyframe writes `frame_XXXX.json` in the **Open3D
`PinholeCameraParameters`** schema (byte-compatible with the project's existing
`ScreenCamera_*.json`), plus a rewritten `manifest.json`.

- **Intrinsic:** built from the head camera's vertical FOV + capture resolution.
  Observed `fx=fy≈213.9`, `cx=cy=255.5` for 512×512 (⇒ ~100° VFOV, matches Quest).
- **Extrinsic (the #1 risk):** Unity is left-handed and the mesh pipeline additionally
  X-flips on import (`MeshDownloadManager.flipObjXAxisForUnity`); Open3D/OpenCV cameras
  are right-handed (Y-down, Z-into-scene). `BuildOpen3DExtrinsic` converts:
  `C = flipX(-1,1,1) * camToWorld * flipYZ(1,-1,-1)`, then `extrinsic = C.inverse`,
  emitted **column-major** (Open3D convention).
- **Safety net:** each JSON also stores the RAW Unity pose (`unity_position`,
  `unity_rotation_quat_xyzw`). If the extrinsic is ever wrong, the conversion can be
  recomputed in Python **without re-scanning**.

**Verified via `verify_poses.py`** (Open3D viewer + printed positions): the converted
camera trajectory matched the physical scan — a tight cluster at origin during
head-turns, then a clean back-left crouch at the end. Positions track the raw Unity
values (no mirror/axis swap), and camera axes are coherent (not random). **The
coordinate conversion is correct** — the biggest Stage-1 risk is retired.

```powershell
cd captures
py -3.11 verify_poses.py <SESSION>            # converted (Open3D) trajectory
py -3.11 verify_poses.py <SESSION> --unity    # raw Unity positions (fallback check)
```

---

## 5. Step 4 — Metric depth PNG (CODE COMPLETE, 1 BUG OPEN ⚠️)

### 5.1 What it does

At each keyframe: blit `EnvDepthCapture.shader` (samples the SDK's global
`_EnvironmentDepthTexture`, outputs **linear metres**) into an `RFloat` RenderTexture
→ `AsyncGPUReadback` → encode a **16-bit grayscale PNG** `frame_XXXX.depth.png` with
depth in **millimetres** (`depth_scale = 0.001` recorded in the manifest). PNG encoder
is hand-rolled (Unity has no 16-bit-gray encoder); it is correct (valid PNGs load fine
in Pillow/Open3D).

Manifest now records `has_depth`, `depth_scale`, and pairs each `frame_XXXX.json` with
`frame_XXXX.depth.png`.

### 5.2 The open bug — depth is a CONSTANT 0.13 m

`verify_depth.py` on every captured frame reports:
```
frame_0000.depth.png: size=(512,512) valid=100.0% min=0.13m median=0.13m max=0.13m
```
Every pixel of every frame is a constant **0.13 m** (all PNGs identical at 1745 bytes).
The full pipeline runs (blit → readback → PNG all succeed, no errors) — but the value
is uniform, i.e. the shader is **sampling something that isn't real per-pixel depth**.

### 5.3 What has been RULED OUT

- ❌ **Permission** — `USE_SCENE` is `granted=true` (checked via
  `adb shell dumpsys package cz.fitvut.fat`; that's why no permission dialog appeared —
  it was granted in earlier `EnvDepthProbe`/`ObjectPicker` work). Not the cause.
- ❌ **Depth availability / timing** — a gate was added
  (`_depthManager.IsDepthAvailable`) plus a per-frame warning. On the latest run
  (`2026-07-03_16-34-22`), **zero** "depth NOT available" warnings across 26 keyframes,
  yet depth was still constant 0.13 m. So availability is fine.
- ❌ **Global not bound** — confirmed in SDK source
  (`EnvironmentDepthManager.cs:374` `Shader.SetGlobalTexture(_EnvironmentDepthTexture)`,
  `:324` sets `_EnvironmentDepthZBufferParams` globally). The globals ARE set.

### 5.4 Leading hypothesis

**Stereo/array texture sampling in a manual blit.** `_EnvironmentDepthTexture` is a
`Tex2DArray` (2 slices, one per eye) and the SDK's `SampleEnvironmentDepthLinear` uses
`SAMPLE_TEXTURE2D_X` (relies on `unity_StereoEyeIndex` / stereo-configured sampling). A
plain `Graphics.Blit` runs **outside** stereo rendering, so that macro likely reads a
default/unbound value → the constant 0.13 m. (Constant-but-nonzero, not garbage, fits
"sampler returns a default".)

### 5.5 Debug instrumentation already added (ready for next build)

`EnvDepthCapture.shader` now has a `_DebugMode` selector, driven by the Inspector field
**`Depth Debug Mode`** on `KeyframeCaptureManager`:

| Mode | Output | Purpose |
|---|---|---|
| 0 | metric metres (normal) | final output |
| 1 | RAW depth sample (0..1, pre-linearisation) | is the TEXTURE being read? |
| 2 | `UV.x` gradient | does the fullscreen quad rasterise at all? |

**Decision table (read back with `verify_depth.py`):**

| Mode 2 (UV.x) | Mode 1 (raw) | Conclusion / fix |
|---|---|---|
| gradient | constant | **Texture not read** (stereo/array). Fix: sample the array slice explicitly (e.g. `_EnvironmentDepthTexture` as `Texture2DArray`, sample slice 0), or render the capture inside a stereo-aware pass. |
| gradient | varies | Texture read OK; **ZBufferParams/linearisation** wrong. Fix the metric conversion. |
| constant | — | **Blit/quad not rasterising** across the RT. Fix RT/blit setup. |

---

## 6. Overall Stage-1 status

| Step | Status |
|---|---|
| 1 — Pose heartbeat | ✅ verified |
| 2 — Keyframe selection (distance/angle) | ✅ verified |
| 3 — Pose + intrinsics → Open3D JSON; coordinate conversion | ✅ verified |
| 4 — Depth readback → metric 16-bit PNG | ⚠️ code done, constant-0.13 m bug open (§5) |
| 5 — Wire together + `manifest.json` + ToolMenu "Capture" button | ⬜ not started |

`manifest.json` is already written (from step 3/4); the remaining part of step 5 is the
in-headset **ToolMenu "Capture" toggle** (reuse the §10b Add/Remove-Portal button
pattern) so a user can start/stop scanning and see a "Keyframes: N (skipped M)" label.

---

## 7. Next Session — Start Here

**Immediate task: close the step-4 depth bug (§5).**

1. **One build, run the debug modes.** Build & Run. In the `KeyframeCapture` Inspector:
   - Set **`Depth Debug Mode = 2`**, short scan → pull → `verify_depth.py`. Expect a
     0→1 gradient (proves the quad rasterises).
   - Set **`Depth Debug Mode = 1`**, short scan → pull → `verify_depth.py`. Does the
     RAW sample VARY or is it CONSTANT?
2. **Apply the fix from the §5.5 decision table** based on what mode 1 shows. Most
   likely path (raw constant): make the shader sample the depth **texture array slice
   explicitly** instead of via `SAMPLE_TEXTURE2D_X`, so a non-stereo blit reads slice 0.
3. **Re-verify:** `verify_depth.py` should show VARYING metres (e.g.
   `min≈0.2 median≈1.4 max≈5.0`, high valid%) instead of constant 0.13 m.
4. **Set `Depth Debug Mode = 0`** for real capture once fixed.

**Then finish Stage 1:**
5. Step 5 — add the ToolMenu "Capture" toggle + on-headset keyframe/skip counter.
6. Optionally raise the novelty thresholds for a real room walk.

**Then Stage 2 (separate):** feed a captured session (depth PNGs + poses) into an
Open3D TSDF-fusion script on the PC to produce a fresh room mesh, then run the existing
`lccp_open3d.py` on it.

### Key facts to remember
- Package `cz.fitvut.fat`; output under `.../files/captures/<sessionId>/`.
- **Pull with PowerShell** (Git Bash mangles the path).
- **Build an APK to test** (no Link for poses).
- `verify_poses.py` (trajectory) and `verify_depth.py` (metric depth) are the two
  verification tools; both take a session folder as arg.
- Depth is a stereo `Tex2DArray`; the metric conversion reuses the SDK's own
  `SampleEnvironmentDepthLinear` (do NOT reimplement nearZ/farZ — those are `internal`).
