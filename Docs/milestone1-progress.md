# Milestone 1 Progress — Object-Shaped Portal Mask

## Session Date: 2026-06-18 / 2026-06-19

---

## What We Are Building

MOZART is a diminished-reality app on Meta Quest 3. It overlays a scanned room mesh through a stencil-based portal (stencil value 6). Today the portal window is a user-drawn bounding box (a rectangle). The goal is to make the portal window **object-shaped** — so only the actual surfaces of an object are hidden, and natural gaps (e.g. between table legs) stay see-through.

### Two Tasks
- **Task 1 (object-shaped mask):** Replace the box stencil with the object's actual geometry
- **Task 2 (gap fill):** Fill the hole left behind with nearby floor/wall geometry — NOT STARTED YET

### Milestone 1 (this sprint): Hard-coded object mask, no real-time picking
### Milestone 2 (next sprint): Real-time ray-pick → segment → swap mask

---

## Architecture (confirmed from code)

- **Stencil mask shader:** `Assets/Materials/Shaders/StencilMask.shader` — writes stencil 6, ColorMask 0, ZWrite On, invisible
- **Portal content shader:** `Assets/Materials/Shaders/PortalContentUnlit.shader` — renders only where stencil == 6
- **Passthrough shader:** `Assets/Materials/Shaders/SelectivePassthroughStencil.shader` — renders only where stencil != 6
- **The dual-role object:** `Assets/Prefabs/PortalMask.prefab` IS the `CollisionBoxPrefab` — same object writes stencil AND is sent to server as cut volume
- **StencilMask material:** `Assets/Materials/StencilMask.mat` — _StencilID = 6, layer 9 (portalMask)
- **Scene mesh:** downloaded as OBJ from `http://butcluster.ddns.net:8000`, cached locally, loaded by `MeshDownloadManager`
- **Server cut:** `POST /v1/scenes/{id}/rebuild-from-boxes` — cuts OBB volumes from the mesh
- **No fill logic exists anywhere** — Task 2 is confirmed absent from codebase
- **PortalBoxBinder.cs does NOT exist** — was listed in scoped map but not in repo

### Key files
- `Assets/Scripts/Managers/GameManager.cs` — cross-cutting spine
- `Assets/Scripts/Mesh/MeshDownloadManager.cs` — mesh download, OBJ parse, server cut
- `Assets/Scripts/Managers/CollisionObjectBinding.cs` — box data model
- `Assets/Scripts/UI/SceneEditorMainMenu.cs` — triggers rebuild-from-boxes
- `Assets/Scripts/Debug/HardcodedObjectMask.cs` — NEW (we created this)
- `Assets/Scripts/Debug/ObjectPicker.cs` — NEW (we created this)

### Coordinate frame
- OBJ is right-handed; Unity is left-handed
- MeshDownloadManager flips X axis (`flipObjXAxisForUnity = true`) and reverses winding on load
- To convert Unity hit point → OBJ space: `new Vector3(-localHit.x, localHit.y, localHit.z)`

---

## Cached Mesh Location

```
C:\Users\bambu\AppData\LocalLow\FIT VUT\FeasibilityAnalysisTool\scene-mesh-cache\scn_848c5fc51a7a45c484d183f9c46eed5a\current\
  mesh-3hz-4.obj   (58 MB)
  mesh-3hz-4.mtl
  mesh-3hz-4.jpg
```

Scene ID: `scn_848c5fc51a7a45c484d183f9c46eed5a`

---

## Steps Completed

### Phase 0 — Fix compile errors
- **Problem:** Unity opened in Safe Mode — `MeshImporter.cs` referenced `TriLibCore` and `IContextualizedError` which were missing
- **Cause:** `TriLib` and `SimpleCollada` packages were on the `learning` branch but not on `object-shaped-portals`
- **Fix:**
  ```
  git checkout learning -- Assets/TriLib
  git checkout learning -- Assets/TriLib.meta
  git checkout learning -- Assets/SimpleCollada
  git checkout learning -- Assets/SimpleCollada.meta
  git commit -m "chore: restore TriLib and SimpleCollada from learning branch"
  ```
- **Result:** Unity exited Safe Mode, compiled successfully
- **Remaining import warnings:** missing demo textures/models from TriLib and SimpleCollada — harmless, ignore them

---

### Phase 1 — Python segmentation (offline)

**Script location:** `Assets/StreamingAssets/clusters/export_clusters.py`

**What it does:**
1. Loads `mesh-3hz-4.obj`
2. RANSAC strips 6 dominant planes (floor, walls, ceiling) — removed ~64k points
3. DBSCAN clusters remaining ~37k points (`eps=0.15`, `min_points=20`)
4. Exports each cluster with >200 points as `clusterN.obj` (simplified to 3000 triangles)
5. Saves `centres.txt` with cluster ID and centroid coordinates

**Key parameters that worked:**
- `distance_threshold=0.03` for RANSAC plane fitting
- `eps=0.15` for DBSCAN (NOTE: `eps=0.05` produced 0 clusters — too small)
- `min_points=20` for DBSCAN

**Output:** 40 clusters found, 11 exported (others too small or no triangles):
```
cluster1.obj  — centre [ 2.38 -0.71 -0.08]
cluster2.obj  — centre [ 2.36  2.69 -0.44]
cluster3.obj  — centre [ 1.09 -3.86 -0.32]
cluster4.obj  — centre [-2.11 -2.83  0.44]
cluster5.obj  — centre [ 4.13 -3.45 -0.11]
cluster7.obj  — centre [-2.12  2.49  0.05]
cluster8.obj  — centre [-0.38  2.63 -0.05]
cluster9.obj  — centre [-1.83 -4.43 -0.39]
cluster10.obj — centre [-0.45 -4.14 -0.57]
cluster12.obj — centre [ 2.99 -2.02 -0.57]
cluster19.obj — centre [-0.15 -0.40 -0.47]
centres.txt   — all 40 cluster centroids
```

**Files are in:** `Assets/StreamingAssets/clusters/`

**Single object test (Milestone 1 hard-code):**
- `cluster12.obj` was selected manually and also saved as `Assets/StreamingAssets/objectmask/object.obj`
- No hole filling needed (mesh was clean)
- Simplified from 15,750 → 2,999 triangles

---

### Phase 2 — Unity scripts (created)

#### HardcodedObjectMask.cs
**Location:** `Assets/Scripts/Debug/HardcodedObjectMask.cs`

**What it does:** Waits for the scene mesh to load, then loads `objectmask/object.obj` from StreamingAssets and attaches it as a child of the scene mesh with StencilMask material on layer 9.

**Status:** ✅ TESTED AND WORKING
- Console showed: `[HardcodedObjectMask] Object mask loaded and applied.`
- Game view showed room mesh rendering correctly

**Inspector setup:**
- Obj Relative Path: `objectmask/object.obj`
- Stencil Mask Material: `StencilMask` (Assets/Materials/StencilMask.mat)
- Portal Mask Layer: 9

---

#### ObjectPicker.cs
**Location:** `Assets/Scripts/Debug/ObjectPicker.cs`

**What it does:**
- On right controller trigger (or Spacebar in editor) fires a ray from camera/controller
- Ray hits scene mesh MeshCollider
- Converts hit point to OBJ space (flip X)
- Finds nearest cluster centroid from `centres.txt`
- Loads matching `clusterN.obj` and swaps it onto the mask renderer

**Status:** ⚠️ PARTIALLY WORKING
- ✅ `centres.txt` loads correctly: `[ObjectPicker] Loaded 40 cluster centres.`
- ✅ Script initializes on startup
- ❌ Scene mesh not yet loaded when tested → `No scene mesh yet, waiting for mesh.`
- ❌ Spacebar/trigger pick not fully tested — need a scene to be open in ARCOR2 UI first

**Why it didn't fully test:** The scene mesh only loads AFTER a scene is opened in the ARCOR2 UI. In editor testing without opening a scene, the mesh never downloads so the collider is never available.

**Inspector setup:**
- Stencil Mask Material: `StencilMask`
- Portal Mask Layer: 9
- Ray Length: 100

**Important fix already applied:** `GameManager.AttachServerSceneMesh` disables all colliders on the scene mesh (`SetCollidersEnabledRecursively(serverSceneMesh, false)`). `ObjectPicker` re-enables them via `EnableMeshCollider()` when the mesh is found.

---

### Phase 3 — Scene setup

**Hierarchy objects:**
- `ObjectMaskTest` — has `HardcodedObjectMask` script — currently **DISABLED** (greyed out)
- `ObjectPickerTest` — has `ObjectPicker` script — currently **ACTIVE**

---

## Git State When Work Was Paused

### What is committed and pushed to remote
- TriLib files restored from `learning` branch ✅
- SimpleCollada files restored from `learning` branch ✅

### What is stashed (WIP, not committed)
All work-in-progress was stashed with:
```
git stash push -u -m "object-shaped-portals WIP before dynamic-occlusion PR"
```

This stash includes:
- `Assets/Scripts/Debug/HardcodedObjectMask.cs`
- `Assets/Scripts/Debug/HardcodedObjectMask.cs.meta`
- `Assets/Scripts/Debug/ObjectPicker.cs`
- `Assets/Scripts/Debug/ObjectPicker.cs.meta`
- `Assets/StreamingAssets/` (all cluster OBJs, centres.txt, object.obj, export_clusters.py, mesh files)
- `Assets/Scenes/current.unity` (scene changes — ObjectPickerTest and ObjectMaskTest GameObjects)
- Various Unity config file modifications

### To resume Monday
```
git stash pop
```
This restores everything exactly as it was left.

### To verify stash is there
```
git stash list
```
Should show: `stash@{0}: On object-shaped-portals: object-shaped-portals WIP before dynamic-occlusion PR`

---

## What Needs to Happen Monday

### Step 1 — Restore WIP
```
git stash pop
```

### Step 2 — Test ObjectPicker in the lab
1. Go to lab with Quest + laptop
2. Connect Quest via USB, open Oculus app, enable Link
3. Open Unity, hit Play
4. Put on headset — navigate to a scene in the ARCOR2 UI (open a scene from the list)
5. Wait for scene mesh to load — Console should show `[ObjectPicker] Scene mesh found in Update.`
6. Press right controller trigger while pointing at an object
7. Expected Console output:
   ```
   [ObjectPicker] Scene mesh found in Update.
   [ObjectPicker] Hit point (...), nearest cluster X (dist Y)
   [ObjectPicker] Cluster X mask applied.
   ```
8. Verify in headset: the object's silhouette should be carved out, gaps should show passthrough

### Step 3 — Verify alignment
- Does the stencil mask line up with the real physical object?
- Do gaps (e.g. between table legs) show real passthrough?
- Is the portal content (textured room mesh) visible inside the silhouette?

### Step 4 — If alignment is off
- The scene mesh alignment can be adjusted using the alignment controls already in the app
- Check `CalibrationManager.cs` and `SpatialAnchorOriginManager.cs` for anchor-based alignment

### Step 5 — Plan Milestone 2 with mentor
Discuss:
- **Server access:** The mesh server at `butcluster.ddns.net:8000` is owned by the `robofit` team (BUT). A new `/segment` endpoint needs to be added for runtime segmentation. Ask mentor for server access or who to contact.
- **Object-shaped cut:** Currently the server cuts a box volume (`/rebuild-from-boxes`). For M2 the cut should also be object-shaped. This needs a new server endpoint that accepts a mesh instead of an OBB.

---

## Known Issues / Warnings

| Issue | Severity | Notes |
|---|---|---|
| TriLib demo textures missing | Low | Harmless import warnings, not needed |
| SimpleCollada demo .dae missing | Low | Harmless import warnings, not needed |
| `SpatialAnchorOriginManager` failing to load anchor | Medium | Pre-existing, affects alignment in lab |
| `Arcor2Exception: Username already exists` | Low | Pre-existing registration quirk |
| `FirstPersonLocomotor: ground not found` | Low | Pre-existing, no ground collider |
| 6 outstanding Meta XR Project Setup fixes | Low | Recommended but not blocking |

---

## If ObjectPicker Breaks Monday — Debug Checklist

1. Console shows `[ObjectPicker] Loaded 40 cluster centres.`? → centres.txt found ✅
2. Console shows `Scene mesh found`? → mesh loaded and collider enabled ✅
3. After trigger: `Hit point` logged? → ray hitting collider ✅
4. After trigger: `Ray hit nothing`? → collider not enabled or ray too short → check Ray Length = 100
5. `cluster X not found`? → the clusterX.obj file missing from StreamingAssets/clusters/
6. Mask applied but invisible? → check StencilMask material assigned + layer = 9

---

## File Summary

| File | Status | Purpose |
|---|---|---|
| `Assets/Scripts/Debug/HardcodedObjectMask.cs` | ✅ Created, tested | Hard-coded single object mask |
| `Assets/Scripts/Debug/ObjectPicker.cs` | ✅ Created, partial test | Ray-pick → swap mask |
| `Assets/StreamingAssets/objectmask/object.obj` | ✅ | Single test object (cluster12) |
| `Assets/StreamingAssets/clusters/cluster*.obj` | ✅ | 11 object meshes |
| `Assets/StreamingAssets/clusters/centres.txt` | ✅ | 40 cluster centroids |
| `Assets/StreamingAssets/clusters/export_clusters.py` | ✅ | Segmentation script |
| `Assets/StreamingAssets/clusters/mesh-3hz-4.*` | ✅ | Source mesh (obj+mtl+jpg) |

---

## Part B — Server Endpoint (TODO this weekend)

Write a Python Flask/FastAPI endpoint that:
- Accepts `POST /v1/scenes/{sceneId}/segment` with body `{"point": [x, y, z]}`
- Runs the same RANSAC + DBSCAN segmentation
- Finds the cluster nearest to the given point
- Returns the cluster as an OBJ file

This replaces the local file load in `ObjectPicker.cs` — one line change:
```csharp
// Current (local):
string path = Path.Combine(Application.streamingAssetsPath, $"clusters/cluster{nearestId}.obj");

// Future (server):
string path = $"http://butcluster.ddns.net:8000/v1/scenes/{sceneId}/segment?x={hit.x}&y={hit.y}&z={hit.z}";
```

**NOT STARTED YET** — write this weekend, deploy with mentor's help Monday.
