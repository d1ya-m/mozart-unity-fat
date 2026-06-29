# Object-Shaped Portals & Dynamic Occlusion — Internship Report (Draft)

**Project:** MOZART (Meta Quest 3 mixed-reality, Unity 6000.3 / URP)
**Branch:** `object-shaped-portals` (base: `master`, merge-base `30b6381`)
**Period:** 2026-06-22 → 2026-06-29
**Author:** Diya Moogi (CS intern)

> **How to read this draft.** Every claim is tagged for how it was verified:
> **[FACT]** — confirmed directly in the current code/config on this branch (with
> `file:line`). **[LIKELY]** — stated in the repo's own implementation doc
> (`Docs/object-shaped-portals-implementation.md`) and consistent with the code,
> but describes on-device behaviour or history I cannot re-run here. **[MISSING]** —
> not recoverable from the repo; flagged for you to fill in manually.
>
> ⚠️ **One correction the draft makes vs. the existing doc:** the implementation
> doc's §5/§6 describe cluster picking by *nearest bounding-box centre*
> (`FindNearestCluster`, `validClusterDistance`, `centres.txt`). That code **no
> longer exists** — the final `ObjectPicker.cs` picks by **per-cluster MeshCollider
> raycast** on a dedicated layer. This report documents the code as it actually is
> and notes where the doc lags. **[FACT — see §3a]**

---

## Abstract

This work extends MOZART's portal system from a single hard-coded object mask to
**user-selected, object-shaped portals**: in-headset, the user points a controller
at a real object and turns *that object's silhouette* into a portal, with multiple
portals addable/removable at once. The room scene mesh is segmented offline into
per-object "cluster" meshes; at runtime the user's aim is resolved to a cluster by
raycasting against per-cluster mesh colliders on a dedicated layer, and the picked
cluster is fed into the pre-existing stencil portal pipeline. A second strand
integrates **dynamic occlusion** via Meta's Environment Depth API so real objects
in front of a portal hide its content; this required correcting a reference-depth
bug ("the cupboard bug") so occlusion works for arbitrary opening shapes, not just
cuboids. The portal-picking feature is reported working on-device; occlusion is
partially working with documented remaining issues. A parallel offline-segmentation
improvement (normals-augmented DBSCAN) was also implemented to reduce object
merges. **[FACT/LIKELY — detailed per section]**

---

## Background

**Pre-existing portal pipeline (not authored here, unchanged in concept).** [LIKELY,
per implementation doc §1; layer numbers are project config]
- `Custom/StencilMask` writes stencil value 6 where a mask mesh is and is itself
  invisible.
- `Custom/PortalContentUnlit` renders portal content only where stencil == 6.
- `SelectivePassthrough` renders passthrough where stencil != 6.
- Net effect: the masked mesh's shape becomes a window. **Layer 9 = portalMask,
  layer 10 = portalContent.** [FACT — `TagManager.asset` layer list; `portalMaskLayer = 9`
  default in `ObjectPicker.cs:10` and `HardcodedObjectMask.cs:8`]

The portal-content material is created from the room mesh's materials by
`GameManager.CreatePortalMaterialSet(...)` using `Shader.Find("Custom/PortalContentUnlit")`.
**[FACT — `Assets/Scripts/Managers/GameManager.cs:961-963`, called at `:935`]** This
detail matters for occlusion (§3f): the portal-content shader runs on the *virtual
room mesh*, so its interpolated world position is deep inside the virtual scene.

**The new feature's job:** pick the right cluster mesh and feed it into this
existing stencil pipeline as the mask, for any number of objects at once.

---

## Step 1 — Diff against the base branch

Base branch is **`master`** (not `main`); `origin/HEAD → origin/master`.
**[FACT — `git branch -a`]**

Commits unique to the branch **[FACT — `git log master..object-shaped-portals`]**:

```
dc2de3e checkpoint
237f2f1 normal augmented ransac+dbscan improvement
982b798 checkpoint: object-shaped portals + dynamic occlusion using RANSAC/DBSCAN
fea2977 chore: restore SimpleCollada from learning branch to fix compile errors
1f3ddc1 chore: restore TriLib from learning branch to fix MeshImporter compile errors
```

> History is squashed into checkpoints; per-bug commits are not individually
> present, so the bug history below is reconstructed from code comments + the
> implementation doc, not from granular commits. **[FACT — only 5 commits exist]**

**Project files changed (excluding vendored `SimpleCollada`/`TriLib` restores and
binary assets)** **[FACT — `git diff master...object-shaped-portals --stat`]**:

| File | Lines | Responsibility |
|---|---|---|
| `Assets/Scripts/Debug/ObjectPicker.cs` | +526 (new) | **Main feature.** Ray-pick → cluster → portal; multi-portal modes; ToolMenu wiring. |
| `Assets/Scripts/Debug/HardcodedObjectMask.cs` | +55 (new) | Milestone-1 single fixed-object mask test. |
| `Assets/Scripts/Debug/EnvDepthProbe.cs` | +44 (new) | Diagnostic: logs Environment Depth pipeline state. |
| `Assets/Materials/Shaders/PortalContentUnlit.shader` | +59/−5 | Adds env-depth occlusion against the portal-opening depth. |
| `Assets/Scripts/Mesh/MeshDownloadManager.cs` | +7 | Diagnostic progress logging in OBJ parse. |
| `Assets/Settings/Mobile_RPAsset.asset` | +/−4 | `RequireDepthTexture 0→1`; `MSAA 2→4`. |
| `Assets/Plugins/Android/AndroidManifest.xml` | +2 | `USE_SCENE` permission for Environment Depth. |
| `Packages/manifest.json` | +1 | Adds `com.unity.xr.meta-openxr 2.5.0`. |
| `ProjectSettings/TagManager.asset` | +/−1 | Adds user layer `ClusterPick`. |
| `ProjectSettings/ProjectSettings.asset` | +/−9 | Input handler, define symbols, runInBackground. |
| `ProjectSettings/QualitySettings.asset` | +/−4 | `pixelLightCount 2→1`; `antiAliasing 2→4`. |
| `ProjectSettings/OculusProjectConfig.asset` | +/−1 | `handTrackingSupport 0→1`. |
| `ProjectSettings/EditorBuildSettings.asset` | +1 | ARFoundation simulation settings config object. |
| `Assets/Scenes/current.unity` | ~1174 | Scene wiring of the new GameObjects (binary-ish YAML; not line-audited). |
| `Assets/StreamingAssets/clusters/*` | (assets) | Cluster meshes, manifest, Python segmenters. |

**Vendored/noise excluded from the feature narrative** (restored to fix compile
errors, per commits `fea2977`/`1f3ddc1`): `Assets/SimpleCollada/**`,
`Assets/TriLib/**`, plus auto-generated `Assets/CompositionLayers/**`. **[FACT —
commit messages + diff stat]**

---

## Work Done

### a. ObjectPicker — the cluster-picking mechanism

**Problem.** Resolve "which real object is the user pointing at?" to one cluster
mesh, robustly and in the *same coordinate space* it will be rendered in.

**Approach (final, collider-raycast design).** Each cluster gets a dedicated
**MeshCollider on a `ClusterPick` layer (index 11)**; the pick ray is masked to
*only* that layer, so the collider hit **is** the picked object. **[FACT]**
- Pick layer field: `clusterPickLayer = 11`. **[FACT — `ObjectPicker.cs:32`]**
- Layer mask + raycast: `int pickLayerMask = 1 << clusterPickLayer; Physics.Raycast(ray, out hit, rayLength, pickLayerMask)`. **[FACT — `ObjectPicker.cs:291-292`]**
- Collider→cluster map resolves the hit: `_colliderToCluster.TryGetValue(hit.collider, out int cid)`. **[FACT — `ObjectPicker.cs:63, 294-296`]**
- A dedicated child `ClusterPick_{id}` carries the MeshCollider, kept on the pick
  layer independent of how the cluster *renders* — so toggling a portal's renderer
  layer never moves the pick collider. `mc.convex = false` (exact concave shape).
  **[FACT — `ObjectPicker.cs:193-201`]**

**Why this is exact:** picking by collider means match-space == render-space *by
construction* — there is no cached centre to drift (see §c). **[FACT — design
comment `ObjectPicker.cs:58-61, 287-290`]**

**Pointer ray** uses the project convention: controller pose from `OVRInput` in
tracking-space-local coords, transformed to world via the `OVRCameraRig`
`trackingSpace`; head-gaze fallback. **[FACT — `ObjectPicker.cs:351-362`,
`FindTrackingSpaceTransform` `:364-372`]**

**Validation.** Reported working on-device: trigger picks the correct cluster and
rejects aim not on an object; verified through `adb logcat` tags `DIAG`/`PICK`.
**[LIKELY — implementation doc §8; logging code is FACT at `ObjectPicker.cs:318-325`]**

> **Doc-vs-code note:** doc §5 still says picking uses `FindNearestCluster` /
> `validClusterDistance` / `renderer.bounds.center`. Those symbols are **absent**
> from the current code (`grep` returns nothing). The final design replaced
> nearest-centre with collider raycast. **[FACT — grep over `ObjectPicker.cs`]**

---

### b. Cluster preloading & Android file I/O

**Problem.** Load every cluster mesh once, in a way that works *inside the Android
APK* (StreamingAssets is compressed there, so `File.*` APIs fail).

**Approach.** `PreloadClustersCoroutine` **[FACT — `ObjectPicker.cs:152-218`]**:
1. Read the manifest `clusters/clusters.json` via **`UnityWebRequest`** with a
   `file://` URL — APK-safe; `JsonUtility` parses `{count, indices[]}`. **[FACT —
   `LoadManifest` `:222-237`, `ClusterManifest` `:239-240`]**
2. For each manifest index, load `clusterN.obj` through
   `MeshDownloadManager.LoadMeshFromServer` with a unique key `cluster_preload_{id}`
   (also `file://`-prefixed), one per frame to avoid a frame hitch. **[FACT —
   `:170-211`]**
3. Parent under `ServerSceneMesh` at local identity, disable the loader's own
   collider, attach the dedicated `ClusterPick_{id}` MeshCollider on layer 11,
   register it in `_clusterObjects` / `_colliderToCluster`, keep the GameObject
   **active but renderer-off** (invisible yet raycastable). **[FACT — `:180-211`]**

**Scene-change handling.** If a *different* scene mesh loads (`key != _loadedSceneKey`),
`ClearAllClusters()` destroys old clusters/portals and reloads. **[FACT —
`OnMeshLoaded` `:110-128`, `ClearAllClusters` `:131-140`]**

**Validation.** Loads reported as "clusters=19/33" on device; manifest-driven so
sparse indices are handled. **[LIKELY — doc §6/§8; current manifest has 33 indices,
FACT from `clusters.json`]**

---

### c. The coordinate-space bug (Bug F) and the layer-11 collider fix

**Problem (Bug F).** A pick "succeeded" but the portal/debug mesh rendered metres
away (e.g. matched at world `(-3.58, 1.47, -0.30)` but rendered at
`(1.94, -2.91, 0.53)`). **[LIKELY — on-device logs quoted in doc §6 Bug F]**

**Root cause.** `centres.txt` (read as text, X-negated once) and the cluster `.obj`
files (parsed with an X-flip **plus** a `Scale(-1,1,1)` basis-flip in
`ParseObjContent`) landed in **different coordinate spaces** — match used one,
render used the other. **[LIKELY — doc §6 Bug F; the matching code that caused it no
longer exists to cite]**

**Fix.** Stop using cached centres entirely. Give each cluster a MeshCollider that
*moves with the mesh* and pick by raycast (§a). Because the collider lives on the
rendered object, the hit point is in render space automatically — match and render
**cannot disagree**. **[FACT — design comments `ObjectPicker.cs:58-61, 287-290`;
`centres.txt` no longer read by runtime — no read path in code]**

**Validation.** "Portal/orange now lands exactly where the laser points."
**[LIKELY — doc §6 Bug F result]**

---

### d. Laser validity logic (green/red)

**Problem.** Show the user whether their aim is actionable.

**Approach (current code).** The laser is drawn by `UpdateLaser`; colour is green
(`hitColor`) when the ray hits a registered cluster collider, red (`missColor`)
otherwise. The laser is shown **only in Add/Remove mode** (hidden in Idle). **[FACT
— validity `validAim = pickedCluster >= 0` `:297`; laser gating `:301-304`;
colouring `UpdateLaser` `:391-403`]**

**Known limitation — superseded threshold.** Bug E (doc §6) describes an earlier
`validClusterDistance` (1.0 m) centre-distance threshold for "valid". The current
code has **no distance threshold** — validity is exact collider containment, which
is strictly better (no centre-vs-containment mismatch). **[FACT — no
`validClusterDistance` in code; LIKELY that earlier versions used it, per doc]**

> So the doc's stated limitation "nearest-centre matching is coarse / blunt 1.0 m
> threshold" (doc §9.3) **no longer applies** to the final code. **[FACT]**

---

### e. Multi-portal mode (Idle/Add/Remove) + ToolMenu wiring + the toggle race

**Problem.** Allow several portals at once, controlled by in-headset buttons,
without the two mode buttons desyncing.

**Approach.** `enum PickMode { Idle, Add, Remove }`, `_mode`, and a
`HashSet<int> _activePortals`. **[FACT — `ObjectPicker.cs:69-71`]**
- `SetAddMode(bool)` / `SetRemoveMode(bool)` are public, driven by each ToolMenu
  **Toggle's `On Value Changed (Boolean)`** (dynamic bool). Each turns the other
  toggle off for mutual exclusivity. **[FACT — `:416-441`]**
- **The toggle race + fix.** Programmatically turning the *other* toggle off fires
  its callback, which would reset `_mode` to Idle and clobber the mode just set
  (symptom: clicking Remove bounced back to Idle). A `_suppressToggleCallback`
  guard makes the programmatic uncheck skip the mode logic. **[FACT — guard field
  `:413`, used `:418, 422-424, 432, 434-439`; symptom per doc §10b]**
- `AddPortal(int)` turns the cluster's renderer on (debug orange `Unlit/Color`
  `ZTest Always`, or the stencil mask material → portal); the pick collider stays
  on layer 11 untouched. `RemovePortal(int)` turns the renderer back off. **[FACT —
  `:460-494`, `:498-513`]**
- Button sublabels show live ON/off state. **[FACT — `RefreshModeStatus` `:443-454`]**

**Editor wiring** (AddPortal→`SetAddMode`, DeletePortal→`SetRemoveMode`, dynamic
bool) and the common mis-wiring warning are documented in implementation doc §10b.
**[LIKELY — scene wiring lives in `current.unity` YAML, not line-audited here]**

**Validation.** Multiple portals add/remove individually on-device. **[LIKELY — doc
§8]**

---

### f. Dynamic occlusion (Environment Depth) — the cupboard bug and its fix

> **This area is the best-documented in the repo AND the most code-verifiable** —
> the shader diff contains the full reasoning inline. The shader *math* is
> recoverable; on-device *behaviour/limitations* are doc-only. **[FACT for code,
> LIKELY for behaviour]**

**Problem.** Real objects in front of a portal should hide portal content. Meta's
occlusion macro asks "is the real-world surface here closer than a *reference
depth*?" — so the reference depth is everything.

**The cupboard bug.** Portal content runs on the **virtual room mesh** (§Background,
`GameManager.cs:961`), so the shader's `input.posWorld` is deep in the virtual
scene. Using it as the reference asked "is the real object in front of the virtual
*wall*?" — a real cupboard with a distant virtual wall behind it counted as "in
front" and punched a hole in the portal. **[FACT — root cause explained in the
shader comment, `PortalContentUnlit.shader` frag block; LIKELY for the on-device
"top row holes, bottom row not" observation, doc §13.2]**

**The fix (shape-agnostic).** Use the **portal opening's own depth** as the
reference instead of `input.posWorld`. The opening is the stencil mask mesh
(`Queue Geometry-1`, `ZWrite On`), which renders *before* the portal content, so
its front-face depth is already in `_CameraDepthTexture` at every portal pixel —
for *any* shape. The shader now: **[FACT — `PortalContentUnlit.shader` frag,
verified in diff]**
1. `float2 screenUV = GetNormalizedScreenSpaceUV(input.positionHCS);` — handles
   render-target scale + per-eye stereo viewport (doing it by hand sampled a
   shifted pixel and displaced the reference).
2. `float openingRawDepth = SampleSceneDepth(screenUV);`
3. `float3 openingWorld = ComputeWorldSpacePosition(screenUV, openingRawDepth, UNITY_MATRIX_I_VP);`
4. `float occ = META_DEPTH_GET_OCCLUSION_VALUE_WORLDPOS(openingWorld, _EnvDepthBias);`
5. `col.a *= saturate(occ); clip(col.a - 0.001);`

Supporting shader changes **[FACT — diff]**: `Blend One Zero → Blend SrcAlpha
OneMinusSrcAlpha` (so occluded pixels fade/clip); `#pragma multi_compile _
HARD_OCCLUSION SOFT_OCCLUSION` (occlusion off = safe fallback); includes
`DeclareDepthTexture.hlsl` + Meta's `EnvironmentOcclusionURP.hlsl`; new
`_EnvDepthBias` property (default `0.015`); `META_DEPTH_VERTEX_OUTPUT` /
`META_DEPTH_INITIALIZE_VERTEX_OUTPUT` plumbing.

Enabling change: `Mobile_RPAsset` `m_RequireDepthTexture: 0 → 1` so
`_CameraDepthTexture` is populated. **[FACT — `Mobile_RPAsset.asset` diff]**

**Soft vs hard.** Soft uses the continuous occlusion value as alpha (smooth edges);
hard is binary. Keyword toggled globally by `EnvironmentDepthManager` on the rig.
**[LIKELY — doc §13.5; the keyword + alpha path are FACT in the shader]**

**Validation & known limitations (on-device).** **[LIKELY — doc §13.6]**
- ✅ Objects very close to the headset (a hand) occlude cleanly in idle mode.
- ⚠️ Objects *between* headset and portal often not occluded accurately / only at
  certain distances.
- ⚠️ In Add/Delete mode the occlusion hole can appear misaligned.
- ⚠️ Residual edge flicker/wobble.

**Suspected causes** (stereo world-pos reconstruction being finicky; mask possibly
not reliably in `_CameraDepthTexture`; inherent ~30 Hz depth-sensor limits). **[LIKELY
— doc §13.7]**

> **[MISSING]** The exact derivation of *why* `ComputeWorldSpacePosition` with
> `UNITY_MATRIX_I_VP` is or isn't correct per-eye in single-pass-instanced stereo
> is **not fully recoverable from the code alone** — the doc lists it as a
> *suspected* cause with on-device verification "still pending." Do not present the
> stereo-reconstruction reasoning as confirmed; it is a hypothesis. There is also a
> reference to a `PortalOccPrepass` / `PortalBoxBinder` on the `dynamic-occlusion`
> branch that is **not on this branch** — fill in from that branch if needed.

---

## Evaluation / Known Limitations

**Working (reported):** mesh download/parse on device; controller laser tracking;
green/red laser in Add/Remove mode; correct cluster pick + rejection; portal in the
picked object's silhouette; multiple simultaneous portals. **[LIKELY — doc §8;
underlying code FACT]**

**Limitations** (reconciled with current code):
1. **Static segmentation** — clusters are offline from one scan; no runtime
   re-segmentation. **[FACT — design]**
2. ~~Coarse nearest-centre matching~~ **superseded** — final code uses exact
   collider containment. **[FACT — §d]**
3. **All clusters preloaded up front** — fine for ~19–33 small meshes; larger
   scenes would cost load time/memory. **[FACT — `PreloadClustersCoroutine`]**
4. **Occlusion partial** — accurate for very-near objects; mid-distance and
   Add/Delete-mode alignment issues + edge flicker remain. **[LIKELY — doc §13.6]**
5. **Link unusable for input** — controller poses don't arrive over Quest Link;
   all interaction testing requires a built APK. **[LIKELY — doc Bug B]**
6. **Implicit coordinate handling** — correctness relies on clusters parented at
   local identity under `ServerSceneMesh`. **[FACT — `:180-183`]**

---

## Future Work

- **Occlusion:** add a debug viz of the reconstructed opening depth; if the mask
  isn't reliably in `_CameraDepthTexture`, use a dedicated mask depth prepass;
  temporal smoothing (EMA) for flicker. **[LIKELY — doc §13.8]**
- **Segmentation:** the offline merge problem (touching objects fuse under plain
  DBSCAN) is addressed by the newly-added **normals-augmented DBSCAN**
  (`export_clusters_normals.py`), with the SAM-hybrid as the documented escalation.
  **[FACT — script exists on branch]**
- **Pipeline:** auto-detect cluster count via the manifest (already done — runtime
  reads `clusters.json`); optimise the 1.47M-line OBJ parse; lazy-load clusters for
  large scenes. **[FACT/LIKELY]**

---

## Step 3 — Consolidated change-list

| File | Change | Why | Tag |
|---|---|---|---|
| `Assets/Scripts/Debug/ObjectPicker.cs` | **New (526 lines).** Ray-pick via layer-11 collider mask → collider→cluster map → portal; Idle/Add/Remove modes; `_suppressToggleCallback` race fix; manifest+UnityWebRequest preload. | Core feature: user-selected object-shaped portals, multi-portal. | FACT |
| `Assets/Scripts/Debug/HardcodedObjectMask.cs` | **New (55 lines).** Loads one fixed `objectmask/object.obj` as a portal mask via `MeshDownloadManager`. | Milestone-1 sanity check for the mask pipeline. | FACT |
| `Assets/Scripts/Debug/EnvDepthProbe.cs` | **New (44 lines).** Logs `IsSupported`/permission/`IsDepthAvailable`/`_EnvironmentDepthTexture`; requests `USE_SCENE`. | Diagnose which link of the env-depth pipeline is missing. | FACT |
| `Assets/Materials/Shaders/PortalContentUnlit.shader` | Occlusion reference switched from `input.posWorld` to opening world-pos from `_CameraDepthTexture`; alpha blend; occlusion keywords; `_EnvDepthBias`; Meta depth macros. | Fix the cupboard bug; shape-agnostic dynamic occlusion. | FACT |
| `Assets/Scripts/Mesh/MeshDownloadManager.cs` | +7 lines progress logging in `ParseObjContent`. | Diagnose the slow 1.47M-line parse (no behaviour change). | FACT |
| `Assets/Settings/Mobile_RPAsset.asset` | `RequireDepthTexture 0→1`; `MSAA 2→4`. | Populate `_CameraDepthTexture` for occlusion; AA quality. | FACT |
| `Assets/Plugins/Android/AndroidManifest.xml` | +`com.oculus.permission.USE_SCENE`. | Required for Environment Depth. | FACT |
| `Packages/manifest.json` | +`com.unity.xr.meta-openxr: 2.5.0`. | XR/OpenXR Meta support (env depth / input). | FACT |
| `ProjectSettings/TagManager.asset` | + user layer `ClusterPick` (index 11). | Dedicated layer so the pick raycast ignores room/portal/UI. | FACT |
| `ProjectSettings/ProjectSettings.asset` | `activeInputHandler 1→2` (Both); +define symbols `USE_INPUT_SYSTEM_POSE_CONTROL;USE_STICK_CONTROL_THUMBSTICKS`; `runInBackground 0→1`. | Silence Input-System exception spam during parse; new XR input plugin; keep running unfocused. | FACT (handler/why per doc) |
| `ProjectSettings/QualitySettings.asset` | `pixelLightCount 2→1`; `antiAliasing 2→4`. | Perf/quality tuning for the Mobile tier. | FACT (values); MISSING (rationale not documented) |
| `ProjectSettings/OculusProjectConfig.asset` | `handTrackingSupport 0→1`. | Enable hand tracking. | FACT (value); MISSING (rationale not documented) |
| `ProjectSettings/EditorBuildSettings.asset` | + ARFoundation simulation settings config object. | Added with `meta-openxr`/XR sim packages. | FACT (value); LIKELY (rationale) |
| `Assets/Scenes/current.unity` | Scene wiring: ObjectPicker GameObject, LineRenderer, status Canvas, ToolMenu Add/Delete toggles, EnvDepthProbe. | Hook the new scripts/UI into the running scene. | LIKELY (YAML not line-audited) |
| `Assets/StreamingAssets/clusters/*` | Cluster `.obj`s, `clusters.json` manifest, `export_clusters.py` (RANSAC+DBSCAN), `export_clusters_normals.py` (new normals-augmented), `visualize_clusters.py`, source `mesh-3hz-4.*`. | Offline segmentation input/output for the runtime. | FACT |
| `Assets/SimpleCollada/**`, `Assets/TriLib/**`, `Assets/CompositionLayers/**` | Restored/auto-generated. | Fix compile errors (commits `fea2977`/`1f3ddc1`); **not part of the feature.** | FACT |

### Dependencies added
- **`com.unity.xr.meta-openxr` 2.5.0** (Packages/manifest.json). **[FACT]**
- Implicit: Meta Core SDK env-depth shader includes
  (`com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl`)
  and `EnvironmentDepthManager` API — already vendored in `Library/PackageCache`.
  **[FACT — referenced in shader/`EnvDepthProbe.cs:3`]**

### Removed
- No runtime use of `centres.txt` (still shipped as a segmentation artifact but no
  read path in code). **[FACT]**
- Earlier nearest-centre picking code (`FindNearestCluster`, `validClusterDistance`,
  `_clusterWorldCentres`) — removed in favour of collider raycast. **[FACT — grep]**

### Tests / validation performed
- **On-device via `adb logcat`** with tags `DIAG`/`PICK`/`PICKMASK`/`Preload`
  (Quest Link cannot deliver controller input, so APK builds were the test loop).
  **[LIKELY — doc §7; logging code FACT at `ObjectPicker.cs:318-325`]**
- **Shader debug modes** (occ / virtual depth / env depth / direct compare) used to
  confirm the cupboard bug. **[LIKELY — doc §13.2; the debug-mode shader variants
  are not all present in the committed shader — MISSING for exact modes]**
- **Segmentation visual check** via `visualize_clusters.py` (count merges).
  **[FACT — script exists]**
- ⚠️ **[MISSING]** No automated/unit tests exist on this branch; all validation is
  manual/on-device. State this plainly to your mentor.

---

## Gaps flagged for manual completion

1. **[MISSING]** Stereo world-pos reconstruction correctness (§f) is a *hypothesis*,
   not confirmed — on-device verification pending.
2. **[MISSING]** Rationale for some setting changes (`MSAA 2→4`, `pixelLightCount
   2→1`, `handTrackingSupport`) is not documented anywhere in the repo; confirm with
   your own notes.
3. **[MISSING]** `current.unity` scene wiring was not line-audited (large YAML);
   the wiring description relies on implementation doc §10b.
4. **[MISSING]** `PortalOccPrepass` / `PortalBoxBinder` referenced as fallbacks live
   on the `dynamic-occlusion` branch, not here.
5. The implementation doc's §5/§6 (nearest-centre picking) is **stale** relative to
   the final collider-raycast code — this report corrects it; you may want to update
   the doc too.
