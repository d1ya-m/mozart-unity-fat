# Progress Log — 2026-06-23 (Object-Shaped Portal — On-Device Debugging)

Branch: `object-shaped-portals`
Continues from: `Docs/milestone-progress-2026-06-22.md`

---

## TL;DR

Got the **entire object-picking pipeline working on-device** (Quest 3 standalone APK):
mesh loads → controller tracking works → ray picks the correct cluster → cluster
mesh loads and applies. **Two things remain broken:**

1. The applied cluster mesh renders in the **wrong place** (below floor / behind
   user) — a **coordinate-space mismatch between `centres.txt` and the cluster
   `.obj` files** when brought into Unity. This is the last real bug.
2. The applied cluster currently uses a debug **orange** material, not the actual
   **portal** (stencil) effect — that's the real end goal, deferred until #1 works.

The biggest lesson: **editor + Quest Link does NOT deliver OVRInput controller
poses.** All real testing must be done with a **built APK + `adb logcat`.**

---

## What got FIXED today ✅

### 1. Laser not visible
- `Default-Line` material had no/duplicate issues; replaced with a code-created
  `Unlit/Color` material set in `Start()` (always visible, even over passthrough).
- LineRenderer width forced in code (`startWidth/endWidth = 0.01`) so the curve
  editor is irrelevant.

### 2. Controller tracking returned (0,0,0)
- **Root cause: Quest Link does not feed OVRInput controller poses in the editor.**
  Confirmed via on-device logs: `rawCtrl` is real on-device (e.g. `(0.24,0.72,0.08)`)
  but was always `(0,0,0)` over Link.
- `GetPointerRay()` now uses the project convention:
  `trackingSpace.TransformPoint(OVRInput.GetLocalControllerPosition(RTouch))`
  (matches `SpatialAnchorOriginManager` / `GameManager`).
- **Conclusion: always test object-shaped portals from a BUILT APK, not Link.**

### 3. `clusters=0` on device (trigger did nothing)
- **Root cause: `File.Exists` / `File.ReadAllLines` DO NOT work on Android** —
  StreamingAssets is inside the compressed APK.
- `LoadCentres()` rewritten as a coroutine using `UnityWebRequest.Get` (works
  inside the APK). Now logs `Loaded 19 cluster centres.` and DIAG shows
  `clusters=19`.
- Same `File.Exists` check removed from `LoadClusterMask` (MeshDownloadManager
  reads the cluster `.obj` via UnityWebRequest `file://` URL, which works).

### 4. Only the FIRST pick worked ("Mesh 'object_mask' already loaded")
- **Root cause:** `MeshDownloadManager.LoadMeshFromServer` caches by key and
  returns the cached object (or null) if the key already exists. Reusing
  `"object_mask"` made every pick after the first fail → "Failed to load cluster
  mesh" → old orange destroyed, new one never loaded.
- **Fix:** unique key per pick: `object_mask_{clusterId}_{Time.frameCount}`.

### 5. Laser was green everywhere (no "valid object" signal)
- Green used to just mean "ray hit any collider" — but the room mesh is
  everywhere, so it was always green.
- Now green = **nearest cluster centre within `validClusterDistance` (1.0 m)** of
  the hit; red otherwise. Trigger rejects picks beyond the threshold.

### 6. On-device debugging method established
- `adb logcat -s Unity | grep PICK` (adb at
  `C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe`)
- This is the ONLY reliable way to see what's happening on-device.

---

## What is STILL BROKEN ❌ (start here tomorrow)

### THE BUG: cluster mesh renders in the wrong location

On-device PICK + PICKMASK logs prove the mismatch:

```
PICK     ... nearestCluster=7 ... centre/worldCentre=(-3.58, 1.47, -0.30)  (near the hit — match OK)
PICKMASK cluster 7 ... boundsCenter=(1.94, -2.91, 0.53)  (where the mesh ACTUALLY renders)
```

- The **match** (FindNearestCluster) places the centre near the ray hit → correct.
- The **actual cluster mesh** renders at a totally different point
  (`boundsCenter` Y = **-2.91** vs matched Y = **+1.47**). So the orange appears
  below the floor / behind the user — which is why "no orange is seen."

**This means `centres.txt` and the cluster `.obj` files end up in DIFFERENT
coordinate spaces once inside Unity**, even though `export_clusters.py` derives
both from the same `mesh-3hz-4.obj`.

### Where the mismatch comes from (analysis, to verify tomorrow)

- `export_clusters.py`:
  - `centres.txt` = mean of **point-cloud** points (`cluster_pcd.points.mean`).
  - `clusterN.obj` = `mesh.crop(bbox)` of the **original mesh**.
  - Both *should* be the same OBJ space — verify this assumption first.
- Unity side, the two are brought in DIFFERENTLY:
  - `LoadCentres()` reads raw text and only negates X (`x = -parse`).
  - Cluster `.obj` goes through `MeshDownloadManager.ParseObjContent` which:
    - negates X per-vertex (`ConvertObjVectorToUnity`, line ~819), AND
    - applies an `objBasisFlip = Scale(-1,1,1)` matrix (line ~864).
  - The **Y sign flip** in the rendered mesh (Y becomes negative) suggests an
    OBJ Y-up vs Unity convention handling in ParseObjContent that the raw
    centre read does NOT replicate.

### The fix direction (do NOT guess — verify with one of these)

**Option A (preferred — single source of truth):** Stop using `centres.txt`.
After loading each `clusterN.obj` in Unity, compute its centre from the loaded
mesh bounds (`renderer.bounds.center`) in the SAME space the mesh renders. Match
the ray hit against those. This guarantees match-space == render-space, killing
the mismatch entirely. (Downside: must load all 19 cluster meshes up front, or
lazily — but they're small, ~1.5k verts each.)

**Option B:** Make `centres.txt` go through the exact same transform chain as the
mesh. Requires replicating `ConvertObjVectorToUnity` + `objBasisFlip` on the
centre when read. Brittle; only do this if Option A is too slow.

**Verification step before coding either:** load ONE cluster (e.g. cluster7),
log both `renderer.bounds.center` (true render position) and the
`TransformPoint(_centres[7])` value. The delta between them is the exact
correction. (We already have this: render center (1.94,-2.91,0.53) vs matched
(-3.58,1.47,-0.30).)

---

## AFTER the placement bug: make it an actual PORTAL (the real goal)

Currently `debugVisibleClusters = true` paints the cluster bright orange
(`Unlit/Color`, ZTest Always, renderQueue 5000) for visibility. The real goal:

- Set `debugVisibleClusters = false` in the inspector → cluster gets
  `stencilMaskMaterial` (StencilMask.mat, stencil=6) and goes on `portalMaskLayer`
  (9).
- With the stencil pipeline (StencilMask writes 6, PortalContentUnlit renders
  where stencil==6, SelectivePassthrough renders where stencil!=6), the picked
  object's silhouette becomes a portal; the rest stays passthrough.
- Verify the StencilMask material + layers + portal camera setup are wired the
  same way the original hardcoded `object.obj` mask was.

---

## Side notes / open items

- **"Big cuboidal portal"** in the scene is from the original implementation
  (not our code). User asked to temporarily disable it as it may occlude clusters.
  Not yet identified — `EffectMesh` is the MRUK passthrough room mesh (do NOT
  disable). `Custom/PortalContentUnlit` is the scene-mesh portal render mode, not
  a separate rectangle. TODO: find the actual portal volume object in the scene.
- **Status panel (world-space Canvas)** still not reliably visible in headset.
  Worked around entirely by using `adb logcat`. Low priority — the StatusText
  vertex color was white-on-white (set to black) and a follow-camera script was
  added (`LateUpdate`), but on-device visibility unconfirmed.
- **Spatial anchor warning** `Failed to load saved anchor ... after 20 attempts`
  is a RED HERRING for picking — it only repositions the room Origin and does NOT
  gate head/controller tracking. Ignore it.
- **Active Input Handling = "Both"** triggers an "unsupported on Android" dialog
  at build — click **Yes** (ignore). It does not affect OVRInput. (Originally set
  to "Both" to silence editor Input System spam during mesh parse.)
- **Server session**: `Arcor2Exception: User registration failed... Username
  already exists` appeared once — stale session. Fixed by restarting the headset.
  If the mesh won't load, restart the headset to clear the stale ARCOR2 session.

---

## State of `ObjectPicker.cs` (key methods)

- `LoadCentres()` → coroutine + UnityWebRequest (Android-safe). X negated on read.
- `GetPointerRay()` → trackingSpace.TransformPoint(OVRInput RTouch) + fallback head gaze.
- `FindNearestCluster(worldPoint, out dist)` → maps each centre to world via
  `_sceneMeshTransform.TransformPoint(_centres[i])`, compares in WORLD space.
  **(This match works; the PLACEMENT in LoadClusterMask does not agree with it.)**
- `LoadClusterMask(clusterId)` → unique key, parents under `_sceneMeshTransform`
  at local identity, applies orange debug material (ZTest Always, queue 5000) or
  stencil mask material depending on `debugVisibleClusters`.
- DIAG / PICK / PICKMASK logs are all gated and readable via `adb logcat`.

---

## TODO TOMORROW (priority order)

1. **Fix the placement mismatch (Option A):** match against loaded cluster mesh
   bounds instead of `centres.txt`, so match-space == render-space. Verify orange
   appears exactly where the laser points.
2. Once placement is correct, **switch `debugVisibleClusters = false`** and verify
   the **portal/stencil** effect on the picked object (the real milestone goal).
3. Identify and (optionally) disable the original "big cuboidal portal" volume if
   it occludes the picked clusters.
4. Clean up: remove DIAG/PICK/PICKMASK verbose logs (or gate behind a debug flag),
   then **commit** the object-shaped-portals work.
5. (Later / separate branch) Part B: server `/segment` endpoint for runtime
   segmentation. Discuss `butcluster.ddns.net` ownership with mentor.
