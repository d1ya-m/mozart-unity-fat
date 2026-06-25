# Progress Log — 2026-06-22 (Object-Shaped Portal Mask)

Branch: `object-shaped-portals`
Continues from: `Docs/milestone1-progress.md`

---

## Summary of Today

Two main efforts today:
1. **Cleaned up the `dynamic-occlusion` PR** (removed 500+ stray files) and pushed it.
2. **Recreated the object-shaped-portal files** (lost from a previous session) and
   attempted the first **end-to-end lab test** with the Quest 3 over Link.

The lab test got the scene to start loading but **the scene mesh never finished
parsing** within the time available. Root cause identified (see below). A progress
log was added to diagnose it — needs to be run tomorrow.

---

## Part 1 — Dynamic Occlusion PR Cleanup (DONE)

### Problem
The PR for `dynamic-occlusion` showed **581 files changed, 30,926 additions**.
That was far too large for the feature.

### Diagnosis
- `git log master..dynamic-occlusion` → only 2 commits (correct)
- But the first commit `ad7c1f9` "implement dynamic occlusion" had **570 files**
- It had accidentally swept in `Assets/TriLib/` and `Assets/SimpleCollada/`
  (added on a different branch to fix a compile error) plus auto-generated
  Meta XR package config files.

### Fix applied
```
git reset HEAD~1
Remove-Item -Recurse -Force Assets/TriLib  Assets/TriLib.meta
Remove-Item -Recurse -Force Assets/SimpleCollada  Assets/SimpleCollada.meta
git add -A
git commit -m "implement dynamic occlusion"
```
Also removed `Docs/milestone1-progress.md` from that branch (unrelated).

### Result
- Down to **47 files, ~1778 additions** — reasonable for a feature that pulls in
  a new SDK.
- Force-pushed: `git push origin dynamic-occlusion --force`
- PR created.

### Note on TriLib / SimpleCollada
- These are NOT committed on `master` or `dynamic-occlusion`.
- `MeshImporter.cs` on master references `TriLibCore` but the folder isn't tracked
  → TriLib is expected to be present locally (came from `origin/learning`).
- To restore them locally without committing:
  ```
  git checkout origin/learning -- Assets/TriLib  Assets/TriLib.meta
  git checkout origin/learning -- Assets/SimpleCollada  Assets/SimpleCollada.meta
  git restore --staged Assets/TriLib Assets/SimpleCollada (and .meta)
  ```
- Their missing demo textures throw harmless "Could not create asset" errors —
  **ignore them**, they don't affect compilation or the feature.

### PR description (for reference)
> Adds soft/hard occlusion of portal content using Meta's Environment Depth API.
> Requires Meta XR SDK with Environment Depth support. Unity auto-downloads
> packages on first open via manifest.json. Tested over Quest Link.

---

## Part 2 — Recreating Object-Shaped Portal Files (DONE)

The `ObjectPicker.cs`, `HardcodedObjectMask.cs`, and `StreamingAssets/clusters`
files were lost (never stashed in a prior session). All recreated today.

### Python environment
- **open3d does NOT support Python 3.14** (which was the default `python`).
- Installed **Python 3.11** alongside. Always run scripts with:
  ```
  py -3.11 export_clusters.py
  py -3.11 visualize_clusters.py
  ```
- `py -3.11 -m pip install open3d` succeeded.

### Files recreated
| File | Status |
|---|---|
| `Assets/Scripts/Debug/HardcodedObjectMask.cs` | ✅ recreated |
| `Assets/Scripts/Debug/ObjectPicker.cs` | ✅ recreated + UI feedback added |
| `Assets/StreamingAssets/clusters/export_clusters.py` | ✅ recreated |
| `Assets/StreamingAssets/clusters/visualize_clusters.py` | ✅ created (debug viz) |
| `Assets/StreamingAssets/clusters/mesh-3hz-4.{obj,mtl,jpg}` | ✅ copied from cache |
| `Assets/StreamingAssets/clusters/cluster0..26.obj + centres.txt` | ✅ generated |
| `Assets/StreamingAssets/objectmask/object.obj` | ✅ (cluster12 copy) |

Source mesh cache location:
```
C:\Users\bambu\AppData\LocalLow\FIT VUT\FeasibilityAnalysisTool\scene-mesh-cache\scn_848c5fc51a7a45c484d183f9c46eed5a\current\
```

### Segmentation output
`export_clusters.py` ran successfully → **40 clusters found, 19 exported**
(those with enough points/triangles). `centres.txt` has the centroids in OBJ space.

### Visualizing clusters
`visualize_clusters.py` opens all cluster OBJs together in Open3D. Note:
`paint_uniform_color` shows them grey on meshes; switched to per-cluster
`vertex_colors` to colour them. Use this to map cluster number → real object
before going to the lab.

---

## Part 3 — UI Feedback Added to ObjectPicker (DONE)

Added a **laser pointer** and **world-space status text** so the user can see
what's happening through the headset.

### Scene setup (in `current.unity`)
- `ObjectPickerTest` GameObject:
  - `ObjectPicker.cs` script (child object `ObjectPicker` actually holds it)
  - `LineRenderer` component — width 0.005, green color, 2 positions
- `Canvas` (World Space, scale 0.001, ~300x100) with child `StatusText`
  (TextMeshPro).
- `ObjectMaskTest` (HardcodedObjectMask) — **kept DISABLED** so only the picker runs.

### Inspector wiring (ObjectPicker)
- Stencil Mask Material → `StencilMask.mat`
- Portal Mask Layer → 9
- Ray Length → 100
- Status Text → `StatusText`
- Hit Color → green, Miss Color → red

### Behaviour
- Green laser when pointing at the mesh, red when missing.
- Status text shows: "Point at an object and pull trigger" → "Loading cluster X..."
  → "Cluster X applied" (or error messages).
- Trigger: right index trigger on device; Spacebar in editor.

---

## Part 4 — First Lab Test (PARTIAL — BLOCKED)

### What worked ✅
- Quest 3 connected over Link.
- App connected to server `butcluster.ddns.net:8000`.
- ARCOR2 scene opened; MRUK room loaded (TABLE, COUCH, WALL_FACE, etc.).
- `[ObjectPicker] Loaded 19 cluster centres.` ✅
- Mesh download began: GET binding → 200, resolved OBJ URL, used cached OBJ,
  `Loading local file bytes...` ✅

### What did NOT work ❌
- **The scene mesh never finished parsing.** No `ServerSceneMesh` ever appeared
  in the Hierarchy, so `[ObjectPicker] Scene mesh found` never fired and we could
  not test picking at all.

### Side issues found & fixed
- **Input System spam:** `InvalidOperationException: You are trying to read Input
  using UnityEngine.Input ... switched to Input System package` fired ~15000×.
  Fixed via **Project Settings → Player → Active Input Handling = Both**
  (was "Input System Package (New)"). Requires editor restart.
- **App kept pausing:** `OnApplicationPause(true)` every time the headset was
  removed (proximity sensor) — this interrupts loading. Must keep headset on /
  set down face-up.

---

## ROOT CAUSE — Why the mesh load hangs

In `Assets/Scripts/Mesh/MeshDownloadManager.cs`:

- `LoadObjMesh` (line ~569) is declared `async` but `ParseObjContent` (line ~685)
  has **no `await` inside the loop** → it runs **synchronously on the main thread**
  and freezes the editor for the whole parse.
- The real bottleneck is `ParseFace` (line ~755): it uses a
  `Dictionary<string,int> vertexMap` with **string keys** and does a `token.Split('/')`
  + dictionary lookup **for every face vertex**. For a 58 MB mesh that's millions
  of string operations single-threaded — minutes in the editor.
- Because the main thread is frozen, **Quest Link reports unresponsive frames**,
  which is what triggers all the `OnApplicationPause`/focus spam. The pause is a
  *symptom*, not the cause.

---

## DIAGNOSTIC ADDED (run this first tomorrow)

Added progress logging to `ParseObjContent` in `MeshDownloadManager.cs`:
```csharp
Debug.Log($"[MeshDownloadManager] Parsing OBJ: {lines.Length} lines total");
int lineIndex = 0;
foreach (string rawLine in lines)
{
    if (++lineIndex % 100000 == 0)
        Debug.Log($"[MeshDownloadManager] Parsed {lineIndex}/{lines.Length} lines...");
    ...
}
```

**Tomorrow step 1:** Stop Play → let it recompile → Play → open scene.
Read the console:
- Numbers ticking up steadily → it's working, just slow; wait it out and time it.
- Stuck on one number → genuinely frozen on that chunk.
- "lines total" prints but never any "Parsed N" → mesh is < 100k lines, so the
  freeze is somewhere else (investigate `mesh.vertices`/`MeshCollider` assignment).

---

## WHAT TO TRY NEXT (in priority order)

1. **Run the diagnostic** (above) to learn exactly how long the parse takes /
   whether it's stuck. Time it end to end.

2. **If it's just slow (minutes but completes):**
   - Test in a **built APK** instead of editor+Link — IL2CPP build is far faster
     than the Mono editor for this kind of CPU work, OR
   - Just be patient and keep headset on / set face-up so it doesn't pause.

3. **If we want it genuinely fast — optimize the parser** (separate, optional task):
   - Replace `Dictionary<string,int>` string-keyed vertexMap with a struct key
     (e.g. `(int pos, int uv, int normal)` or a `long` packed key) to kill the
     per-vertex string hashing.
   - Avoid `token.Split('/')` per vertex; parse indices with spans/manual scan.
   - Consider parsing on a background thread (`Task.Run`) and only touching Unity
     `Mesh`/`GameObject` APIs back on the main thread.
   - NOTE: this is a core-file change — discuss with mentor before committing,
     and it should be its own branch/PR, not mixed into object-shaped-portals.

4. **Once the mesh loads → finish the actual M2A test:**
   - Confirm `ServerSceneMesh` appears and `[ObjectPicker] Scene mesh found`.
   - Point + right trigger at an object.
   - Expect: `Hit point (...), nearest cluster X` → `Cluster X applied`.
   - In headset: object silhouette carved as portal, gaps show passthrough.

---

## OPEN QUESTIONS FOR MENTOR

- The mesh-parse performance: is there a known faster loader, or is editor+Link
  just expected to be slow? Should we always test object-shaped portals from a
  build instead of Link?
- Server `/segment` endpoint for runtime segmentation (Milestone 2B) — who owns
  `butcluster.ddns.net` and can we add an endpoint? (robofit / BUT team.)
- Scan freshness: current approach needs an up-to-date scan. Discussed options:
  rescan, runtime server segmentation, Quest scene understanding (`OVRSceneManager`,
  not currently in project), or a hybrid. Hybrid is a future research direction.

---

## GIT STATE

- `dynamic-occlusion` — cleaned up, pushed, PR open.
- `object-shaped-portals` — current working branch. Has the recreated scripts,
  StreamingAssets, scene wiring, and the diagnostic edit to `MeshDownloadManager.cs`.
- Remember to **commit today's work** before switching branches (or stash):
  ```
  git stash push -u -m "object-shaped-portals WIP 2026-06-22"
  git stash pop   # to restore
  ```
