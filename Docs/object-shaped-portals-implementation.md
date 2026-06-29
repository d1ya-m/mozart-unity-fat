# Object-Shaped Portals — Full Implementation Report

Branch: `object-shaped-portals`
Authors of changes this period: implementation + on-device debugging across 2026-06-22 → 2026-06-25.
Status: **WORKING** — three modes (Idle / Add / Delete), green laser on valid
regions, red on invalid, and portals generate in the picked objects' silhouettes;
multiple portals can be added/removed individually.

> **Update 2026-06-25:** Dynamic occlusion is now **integrated into this branch**
> (it was previously isolated on `dynamic-occlusion`). The portal content shader
> on this branch now performs Environment-Depth occlusion against the *portal
> opening* depth (not the virtual room), so real objects in front of a portal hide
> portal content. See the new **§13 — Dynamic Occlusion (Environment Depth)**.
> ⚠️ This supersedes the earlier statements in §4.3 that occlusion was untouched.

---

## 1. Goal & Concept

**Goal:** Let a user in a Quest 3 headset point a controller at a real object in
the lab and turn *that object's silhouette* into a portal (a hole that shows
portal content / passthrough), instead of a single hardcoded object mask.

**Why "clusters":** The room is captured as one big scene mesh (`mesh-3hz-4.obj`,
~1.47M lines). To address individual objects, the mesh is segmented offline into
"clusters" (one `.obj` per object). At runtime the user points at the room; we
find which cluster they aimed at and turn that cluster into the portal mask.

**Portal rendering theory (pre-existing in the codebase, unchanged):**
- `StencilMask` shader writes a stencil value (6) where the mask mesh is, and is
  itself invisible.
- `Custom/PortalContentUnlit` renders portal content only where stencil == 6.
- `SelectivePassthrough` renders passthrough where stencil != 6.
- Net effect: the masked object's shape becomes a window (portal); everything
  else stays passthrough.
- **Layer 9 = portalMask, Layer 10 = portalContent.**

The whole job of the new code is: **pick the right cluster mesh and feed it into
this existing stencil pipeline as the mask.**

---

## 2. Offline Segmentation (Python)

Two segmenters now exist; both emit the same output contract (`clusterN.obj` +
`clusters.json`), so the runtime is unchanged either way.

**Original — `export_clusters.py` (plain RANSAC + DBSCAN):**
1. Load `mesh-3hz-4.obj`, sample 100k points.
2. **RANSAC** plane removal ×6 (`distance_threshold=0.03`) — strips floor/walls/
   ceiling.
3. **DBSCAN** (`eps=0.15, min_points=20`) — groups remaining points by proximity.
4. Per cluster: crop the mesh to the bounding box, **QEM** simplify
   (`simplify_quadric_decimation(3000)`), export `clusterN.obj`.
5. Write `clusters.json` manifest (`{count, indices[]}`) + `centres.txt` (debug).
- **Known flaw:** clusters by *distance only*, so **objects that touch merge** into
  one cluster (see §14).

**Current — `export_clusters_normals.py` (normals-augmented DBSCAN, §14.7):**
Same RANSAC strip and same crop→QEM→`clusterN.obj`/`clusters.json` backend, but
DBSCAN runs on a **6-D position+normal feature** so the cluster boundary lands on
the normal discontinuity where two objects touch — splitting them instead of
merging. Zero new dependencies (DBSCAN on an Open3D KD-tree). First run: **33
clusters** (vs ~14 from plain DBSCAN). See §14.7 for the full rationale.

**Environment note:** open3d supports Python 3.8–3.11. Use `py -3.11 <script>.py`.
`visualize_clusters.py` previews clusters in distinct colours.

> ⚠️ **`centres.txt` is NOT read by the runtime** (see §6). The runtime reads
> `clusters.json` for indices and derives positions from the loaded cluster
> colliders/meshes. `centres.txt` is kept only as a debug artifact.

---

## 3. New Runtime Files

| File | Purpose |
|---|---|
| `Assets/Scripts/Debug/ObjectPicker.cs` | **Main script.** Ray-pick (layer-11 collider) → cluster → portal mask; Idle/Add/Remove multi-portal. |
| `Assets/Scripts/Debug/HardcodedObjectMask.cs` | Milestone-1 sanity check: loads a single fixed `objectmask/object.obj` as a portal mask. Kept disabled in scene. |
| `Assets/Scripts/Debug/EnvDepthProbe.cs` | Diagnostic: logs Environment-Depth pipeline state (`IsSupported`/permission/`IsDepthAvailable`/`_EnvironmentDepthTexture`) and requests `USE_SCENE`. Added for the occlusion work (§13). |
| `Assets/StreamingAssets/clusters/*` | `clusterN.obj`, `clusters.json` (manifest), `centres.txt`, `export_clusters.py`, `export_clusters_normals.py` (normals-augmented, §14.7), `visualize_clusters.py`, source mesh `mesh-3hz-4.{obj,mtl,jpg}`. |
| `Assets/StreamingAssets/objectmask/object.obj` | Single-object mask for the hardcoded test. |

---

## 4. Changes to the ORIGINAL codebase (complete list)

> **Updated 2026-06-29 from the actual branch diff** (`git diff
> master...object-shaped-portals`). The original "only two files" claim is
> obsolete — the occlusion work (§13) and its enabling settings touched several
> more. Full verified list (excludes vendored `SimpleCollada`/`TriLib`/
> `CompositionLayers` restored to fix compile errors):
>
> | File | Change | Why |
> |---|---|---|
> | `Assets/Scripts/Mesh/MeshDownloadManager.cs` | +7 log lines (§4.1) | Diagnose slow OBJ parse |
> | `Assets/Materials/Shaders/PortalContentUnlit.shader` | occlusion vs opening depth (§13.4) | Cupboard-bug fix |
> | `Assets/Settings/Mobile_RPAsset.asset` | `RequireDepthTexture 0→1`, `MSAA 2→4` | Populate `_CameraDepthTexture`; AA |
> | `Assets/Plugins/Android/AndroidManifest.xml` | +`USE_SCENE` permission | Environment Depth |
> | `Packages/manifest.json` | +`com.unity.xr.meta-openxr 2.5.0` | XR/OpenXR Meta support |
> | `ProjectSettings/TagManager.asset` | +user layer `ClusterPick` (11) | Pick-only raycast layer |
> | `ProjectSettings/ProjectSettings.asset` | `activeInputHandler 1→2`; +define symbols; `runInBackground 0→1` (§4.2) | Input spam fix; XR input; run unfocused |
> | `ProjectSettings/QualitySettings.asset` | `pixelLightCount 2→1`, `antiAliasing 2→4` | Mobile-tier perf/quality |
> | `ProjectSettings/OculusProjectConfig.asset` | `handTrackingSupport 0→1` | Enable hand tracking |
> | `ProjectSettings/EditorBuildSettings.asset` | +ARFoundation simulation settings | Added with XR sim packages |
> | `Assets/Scenes/current.unity` | Scene wiring of the new GameObjects/UI | Hook scripts/UI into the scene |
>
> Rationale for `MSAA`/`pixelLightCount`/`handTracking` is not separately
> documented — confirm from personal notes if the report needs it.

### 4.1 `Assets/Scripts/Mesh/MeshDownloadManager.cs` (+7 lines)
Added **progress logging** to `ParseObjContent` so we could diagnose the slow
1.47M-line parse (purely diagnostic, no behaviour change):
```csharp
Debug.Log($"[MeshDownloadManager] Parsing OBJ: {lines.Length} lines total");
int lineIndex = 0;
foreach (string rawLine in lines) {
    if (++lineIndex % 100000 == 0)
        Debug.Log($"[MeshDownloadManager] Parsed {lineIndex}/{lines.Length} lines...");
    ...
}
```

### 4.2 `ProjectSettings/ProjectSettings.asset` (1 line)
`activeInputHandler: 1 → 2` (Input Handling = **Both**). Originally changed to
silence `InvalidOperationException` Input-System spam that was flooding the main
thread and making the editor appear to hang during mesh parse. On Android this
triggers an "unsupported" build dialog — click **Yes** (it does not affect
OVRInput).

### 4.3 NOT changed (verified)
- **`StencilMask` and `SelectivePassthrough` shaders** are unchanged — only
  `PortalContentUnlit.shader` was modified (for occlusion, §13). The stencil
  mechanism itself is untouched.
- **`GameManager`, `SpatialAnchorOriginManager`, MRUK, the OVR camera rig** are not
  modified by this feature. (`GameManager.CreatePortalMaterialSet` is *used* by the
  occlusion reasoning but not edited.)

> Earlier drafts said "no dynamic occlusion / no shader change." That is obsolete:
> occlusion **is** implemented here and **does** modify `PortalContentUnlit.shader`
> + `Mobile_RPAsset` (see §4 table and §13).

---

## 5. ObjectPicker.cs — How It Works (current, final design)

> **Updated 2026-06-29 to match the code.** The earlier nearest-centre design
> (`FindNearestCluster`, `validClusterDistance`, `_clusterWorldCentres`,
> `ShowCluster`, `clusterCount`) **no longer exists** — those symbols are absent
> from `ObjectPicker.cs`. The final design picks by **per-cluster MeshCollider
> raycast on a dedicated layer**. This section now describes the real code with
> `file:line` references.

### Pipeline per frame (`Update`, `ObjectPicker.cs:261-344`)
1. **`GetPointerRay()`** (`:351-362`) — ray from the right controller.
   `trackingSpace.TransformPoint(OVRInput.GetLocalControllerPosition(RTouch))` +
   `trackingSpace.rotation * localRot`. Matches the project convention
   (`SpatialAnchorOriginManager`, `GameManager`); head-gaze fallback if no tracking
   space (`FindTrackingSpaceTransform` `:364-372`).
2. **Layer-masked raycast** (`:291-292`) —
   `Physics.Raycast(ray, out hit, rayLength, 1 << clusterPickLayer)`. The ray hits
   **only** the cluster pick-colliders (layer 11), ignoring the room mesh / portals
   / UI.
3. **Collider → cluster** (`:294-296`) — `_colliderToCluster.TryGetValue(hit.collider,
   out cid)`. The collider you hit **is** the object: exact per-shape containment,
   no centre-distance threshold. `validAim = pickedCluster >= 0` (`:297`).
4. **Laser** (`:301-304`) — shown **only in Add/Remove mode** (hidden in Idle);
   green (`hitColor`) when on a cluster, red (`missColor`) otherwise.
5. **On right/left index trigger** (`:330-343`) — in Add mode `AddPortal(cid)`, in
   Remove mode `RemovePortal(cid)`; Idle ignores the trigger.

### Cluster preloading (the key design — `PreloadClustersCoroutine` `:152-218`)
Runs once when the scene mesh loads:
- **Reads the manifest** `clusters/clusters.json` via `UnityWebRequest`
  (`LoadManifest` `:222-237`) to know exactly which indices exist — no hardcoded
  count.
- For each index, loads `clusterN.obj` via `MeshDownloadManager.LoadMeshFromServer`
  with a unique key `cluster_preload_{id}`, one per frame to avoid a hitch
  (`:170-211`).
- Parents each under `ServerSceneMesh` at local identity, **disables the loader's
  own collider**, and adds a **dedicated child `ClusterPick_{id}` carrying a
  `MeshCollider` (`convex = false`) permanently on `clusterPickLayer` (11)**
  (`:188-201`). The pick collider's layer is independent of how the cluster
  renders, so toggling a portal never moves it off the pick layer.
- Registers `_colliderToCluster[mc] = id` and `_clusterObjects[id] = obj`
  (`:201-202`), then keeps the GameObject **active with its renderer OFF**
  (`:205-207`) — invisible yet raycastable.

> Why this matters: matching is the collider on the rendered object, so
> **match-space == render-space by construction** — there is no cached centre to go
> stale (this is the Bug F fix, §6).

### Showing / hiding a cluster as a portal (`AddPortal` `:460-494`, `RemovePortal` `:498-513`)
- `AddPortal` turns the cluster's **renderer on** and sets its material:
  - `debugVisibleClusters == true` → bright orange `Unlit/Color`, `_ZTest Always`,
    `renderQueue 5000`, renderer on layer 0 (always-visible debug aid).
  - `debugVisibleClusters == false` → `stencilMaskMaterial` + renderer on
    `portalMaskLayer` (9) → **the cluster becomes a portal** via the existing
    stencil pipeline (same path `HardcodedObjectMask` uses).
  - Adds the id to `_activePortals`. The pick collider (layer 11, separate child)
    is untouched, so the portal can still be picked for removal.
- `RemovePortal` turns the renderer back off and removes the id from
  `_activePortals`; the GameObject + pick collider stay active.

### Inspector fields (`:9-47`)
- `stencilMaskMaterial` = `StencilMask.mat`; `portalMaskLayer` = 9.
- `clusterPickLayer` = 11 (the `ClusterPick` user layer).
- `debugVisibleClusters` — orange (debug) vs portal (real). **Off = portal.**
- `_addToggle` / `_removeToggle`, `addPortalSubLabel` / `removePortalSubLabel` —
  ToolMenu mode toggles + their live ON/off sublabels (§10b).
- `verboseLogging` — gates the `DIAG`/`PICK`/`Preload` logs.
- `statusText`, `statusPanel`, `statusPanelFollowsView` — optional in-headset
  status (largely superseded by `adb logcat`).
- Laser width forced in code (`0.005`), colours `hitColor`/`missColor`.

> **Removed vs. earlier design:** `validClusterDistance`, `clusterCount`,
> `FindNearestCluster`, `_clusterWorldCentres`, `ShowCluster`, and any runtime read
> of `centres.txt` are **gone** — replaced by manifest-driven preload + collider
> raycast.

---

## 6. Debugging Journey — Bugs, Theory, Results

This was an extended on-device debugging effort. Each fix below includes the
symptom, the root-cause theory, and the measured result.

### Bug A — Laser invisible
- **Symptom:** no laser at all.
- **Cause:** `Default-Line` material missing / on wrong hierarchy object; width
  curve near 0.
- **Fix:** create an `Unlit/Color` material in code (ignores scene depth → visible
  over passthrough) and force width in code (`startWidth/endWidth`).
- **Result:** laser visible everywhere it should be.

### Bug B — Controller pose always (0,0,0) in editor
- **Symptom:** `rawCtrl=(0,0,0)`, laser stuck at world origin.
- **Theory→Confirmed:** **Quest Link does NOT deliver OVRInput controller poses
  to the editor.** Verified by building an APK: on-device `rawCtrl` is real.
- **Fix:** use `trackingSpace.TransformPoint(OVRInput…)` (project convention) and
  **always test from a built APK + `adb logcat`, not Link.**
- **Result:** correct controller ray on device.

### Bug C — `clusters=0` on device (trigger dead)
- **Symptom:** clusters never loaded; `if (!_clustersLoaded) return;` killed the
  trigger.
- **Cause:** `File.Exists` / `File.ReadAllLines` **do not work on Android** —
  StreamingAssets is inside the compressed APK.
- **Fix:** read via `UnityWebRequest` (`file://` URL) — works inside the APK.
  Removed all `File.Exists` checks.
- **Result:** `clusters=19` loaded on device.

### Bug D — Only the first pick worked
- **Symptom:** `Mesh 'object_mask' already loaded` → every pick after the first
  failed; the previous orange got destroyed and nothing replaced it.
- **Cause:** `MeshDownloadManager` caches by key; reusing `"object_mask"` returned
  the cached/destroyed object.
- **Fix (interim):** unique key per load. **Final design:** preload each cluster
  once with its own key and just toggle visibility — no reload at all.
- **Result:** switching between clusters works reliably.

### Bug E — Laser green everywhere (no "valid" signal)
- **Symptom:** green even on bare walls.
- **Cause:** green meant "ray hit any collider"; the room mesh is everywhere.
- **Fix:** green only when nearest cluster ≤ `validClusterDistance`; trigger
  rejects far picks.
- **Result:** **green = on a real object, red = not.** (As required.)

### Bug F — THE BIG ONE: orange/portal appeared in the wrong place
- **Symptom:** pick "succeeded" but the mask rendered ~4 m away / below the floor;
  nothing visible where you pointed.
- **Evidence (on-device logs):**
  ```
  PICK     matched cluster 7 at world (-3.58, 1.47, -0.30)   (near the hit ✓)
  PICKMASK cluster 7 mesh renders at  ( 1.94,-2.91,  0.53)   (totally different ✗)
  ```
- **Root cause theory:** `centres.txt` (read as raw text, X-negated once) and the
  cluster `.obj` files (parsed through `ConvertObjVectorToUnity` X-flip **plus** an
  `objBasisFlip` Scale(-1,1,1) matrix in `ParseObjContent`) ended up in **different
  coordinate spaces** in Unity — even Y sign differed. Matching used one space,
  rendering used another, so they disagreed.
- **Fix (final design):** stop using `centres.txt` entirely. **Give each preloaded
  cluster its own `MeshCollider` (on layer 11) and pick by raycasting against those
  colliders** (`ObjectPicker.cs:188-201, 291-296`). Because the collider lives on
  the rendered object and moves with it, the hit is in render space automatically —
  match-space == render-space *by construction*, so they cannot disagree.
  (An interim version matched against each cluster's `renderer.bounds.center`; the
  final code dropped centres for exact collider containment.)
- **Result:** **the portal/orange now lands exactly where the laser points.** ✅

---

## 7. On-Device Debugging Method (reusable)

The world-space status panel proved unreliable in-headset; `adb logcat` was the
breakthrough. From Git Bash:
```bash
ADB="/c/Program Files/Unity/Hub/Editor/6000.3.10f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
"$ADB" logcat -s Unity | grep -E "PICK|PICKMASK|Preload|DIAG"
```
Key log tags added: `DIAG` (per-second state), `PICK` (trigger + match),
`PICKMASK` (what got shown + where), `Preload` (cluster loading).

---

## 8. Current Behaviour (verified working)

- ✅ Mesh downloads & parses on device.
- ✅ Right-controller laser tracks correctly.
- ✅ **Green laser on valid object regions, red on invalid** — shown **only in
  Add/Remove mode** (hidden in Idle).
- ✅ Trigger picks the correct cluster; rejects aim that isn't on an object.
- ✅ **Portal generates in the picked object's silhouette** (toggle
  `debugVisibleClusters` off; orange is the debug alternative).
- ✅ **Multiple portals at once** via ToolMenu Add/Remove Portal toggle buttons
  (§10b); each can be added/removed individually. Button sublabels show ON/off.

---

## 9. Limitations

1. **Static / pre-segmented:** clusters are computed offline from one scan. If the
   room or objects move, the scan and clusters are stale. No runtime
   re-segmentation.
2. ~~One active portal at a time~~ **RESOLVED (§10b):** multiple portals can now
   be active simultaneously, added/removed individually via the ToolMenu
   Add/Remove Portal toggle buttons.
3. ~~Nearest-centre matching is coarse~~ **RESOLVED:** picking is now exact
   per-shape containment via a per-cluster `MeshCollider` raycast (layer 11). There
   is no `validClusterDistance` threshold anymore.
4. ~~Sparse cluster indices vs `clusterCount`~~ **RESOLVED:** the runtime reads
   `clusters.json` and loads exactly the listed indices — no hardcoded count.
5. **All clusters preloaded up front:** ~19–33 small meshes is fine, but a larger
   scene with many/large clusters would increase load time and memory.
6. **Dynamic occlusion is partial, not absent:** it IS implemented on this branch
   (§13) but works accurately only for very-near objects; mid-distance and
   Add/Delete-mode alignment + edge flicker remain (§13.6). (Earlier text saying
   "no dynamic occlusion" is obsolete.)
7. **Link unusable for input:** all testing must be a built APK; no in-editor
   iteration for controller interactions.
8. **Coordinate handling is implicit:** correctness relies on cluster meshes being
   parented at local identity under `ServerSceneMesh`. If the mesh attach logic
   changes, matching could break again.
9. **Spatial-anchor warning** (`Failed to load saved anchor`) is unresolved but
   harmless to picking (it only repositions the room origin).

---

## 10. Potential Improvements

### UI / interaction
- **Add/remove specific portals** instead of single-switching: a small in-headset
  UI (buttons on the wrist/menu panel) to *add* the aimed cluster as a portal and
  *remove* a specific one, keeping multiple portals active simultaneously.
  - Implementation sketch: change `_activeCluster` (single int) to a
    `HashSet<int> _activePortals`; "Add" = `SetActive(true)` + keep, "Remove" =
    `SetActive(false)`; a "Clear all" button.
- **Hover highlight:** lightly tint the cluster the laser is currently over
  (before pressing trigger) so the user previews what they'll select.
- **Confirm/toggle:** trigger toggles a cluster's portal on/off rather than
  replacing, enabling multi-select naturally.

### Matching quality
- Replace nearest-centre with **per-cluster collider containment** (raycast
  against each cluster's own mesh collider) for precise picking, especially with
  adjacent objects.
- Auto-detect `clusterCount` by probing files / shipping a manifest, instead of a
  hardcoded number.

### Pipeline / robustness
- **Runtime segmentation server endpoint** (`/segment`) so clusters update with
  the live scan instead of being baked offline — addresses the static limitation
  and supports dynamic objects. (Discuss `butcluster.ddns.net` ownership / Part B.)
- Optimize `ParseObjContent` (string-keyed vertex map → struct/packed key, parse
  on a background thread) to speed the 1.47M-line parse.
- Lazy-load clusters on first pick (with caching) if scenes get large.

### Visual
- Combine with **dynamic occlusion** (separate branch) so portal content respects
  real-world depth.
- Smooth transitions / animation when a portal appears or switches.

---

## 10b. Multi-Portal Mode + ToolMenu Buttons (IMPLEMENTED)

This replaces the original single-portal "switch between clusters" behaviour with
**multiple simultaneous portals**, controlled by two buttons added to the existing
ToolMenu panel.

### Interaction model (Option C — toggle modes)
Three modes: **Idle, Add, Remove** (default Idle).
- **"Add Portal" button** → toggles Add mode on/off (click again returns to Idle).
- **"Remove Portal" button** → toggles Remove mode on/off.
- The two are **mutually exclusive**: turning one on turns the other off.
- **Trigger behaviour by mode:**
  - *Idle:* trigger does nothing; **laser is hidden.**
  - *Add + green laser:* trigger → that cluster becomes a portal (added to the
    active set, stays on alongside any others).
  - *Remove + green laser:* trigger → that portal disappears.
- **Laser only appears in Add/Remove mode** (green = ray hits a cluster collider,
  red = not). Hidden in Idle so the aiming guide only shows when actionable.
- Multiple portals stay active at once (no more single-switching).

### Code (ObjectPicker.cs)
- `enum PickMode { Idle, Add, Remove }` + `_mode`; `HashSet<int> _activePortals`.
- `SetAddMode(bool)` / `SetRemoveMode(bool)` — public, **driven by each Toggle's
  `On Value Changed (Boolean)` dynamic bool**. The toggle's on/off state sets the
  mode directly (no desync). Each clears the other toggle for mutual exclusion.
- `_suppressToggleCallback` guard — **fixes a toggle race**: when one mode turns
  the other toggle off programmatically, that fires the other's callback which
  would reset mode to Idle and clobber the just-set mode. The guard makes the
  programmatic uncheck skip the mode logic. (Symptom before the fix: clicking
  Remove bounced straight back to Idle.)
- `AddPortal(int)` / `RemovePortal(int)` — show/hide the preloaded cluster and
  update `_activePortals`.
- Inspector refs: `_addToggle`, `_removeToggle` (for mutual exclusion);
  `addPortalSubLabel`, `removePortalSubLabel` (TMP_Text sublabels showing
  "ON ..." / "off"); `verboseLogging` flag now gates DIAG/PICK/PICKMASK logs.

### ToolMenu button wiring (Editor)
The ToolMenu buttons are **Toggle** components (prefab
`TextTileButton_IconAndLabel_Regular`), NOT plain Buttons — they fire
`On Value Changed (Boolean)`, mirroring how the existing **Origin Anchor** button
drives its sublabel.

Path to the buttons:
`ToolMenu → PanelInteractable → CanvasPanel → UIBackplate+VerticalLayoutGroup →
Horizontal → Content → Scroll View → Viewport → Visuals → {AddPortal, DeletePortal}`
(duplicated from an existing button, e.g. SaveScene).

Wiring per button (`On Value Changed (Boolean)`):
| Button | Object slot | Function (**Dynamic bool**) |
|---|---|---|
| AddPortal | `ObjectPicker` | `SetAddMode` |
| DeletePortal | `ObjectPicker` | `SetRemoveMode` |

- Main label = the button's `Label`; status sublabel = its `Label (1)` (the
  prefab's secondary TMP_Text, same element OriginAnchor uses).
- On the `ObjectPicker` inspector: assign `Add Toggle`=AddPortal,
  `Remove Toggle`=DeletePortal, and the two sublabel fields to each button's
  `Label (1)`. Leave the `Label (1)` text blank — code fills it at runtime.

> ⚠️ **Common wiring bug:** if the DeletePortal toggle is wired to `SetAddMode`
> (or its object slot points at the AddPortal toggle), clicking Delete will toggle
> the *Add* button instead. Each toggle must call its OWN method from the
> **Dynamic bool** section (not Static Parameters), with the object slot =
> `ObjectPicker`, and exactly one entry in the list.

---

## 11. File / Setting Inventory (for committing)

**New (untracked) — part of this feature:**
- `Assets/Scripts/Debug/ObjectPicker.cs` (+ `.meta`)
- `Assets/Scripts/Debug/HardcodedObjectMask.cs` (+ `.meta`)
- `Assets/StreamingAssets/clusters/` (clusterN.obj, centres.txt, *.py, source mesh)
- `Assets/StreamingAssets/objectmask/object.obj`

**Modified original files:**
- `Assets/Scripts/Mesh/MeshDownloadManager.cs` (+7 diagnostic log lines)
- `Assets/Scenes/current.unity` (scene wiring: ObjectPicker GameObject,
  LineRenderer, status Canvas)
- `ProjectSettings/ProjectSettings.asset` (`activeInputHandler` 1→2 / "Both")

**NOT part of this feature (do not commit here):** TriLib/SimpleCollada,
Oculus/XR auto-generated config, `dynamic-occlusion` work, `_Recovery/`.

---

## 12. TODO before merge
1. Decide orange-debug vs portal default (`debugVisibleClusters` off for the real
   feature).
2. ✅ DONE — verbose logs gated behind the `verboseLogging` inspector flag.
3. ✅ DONE — runtime reads `clusters.json` for the index list; no hardcoded
   `clusterCount` to keep in sync.
4. ✅ DONE — add/remove multi-portal UI implemented via ToolMenu Add/Delete Portal
   toggle buttons (see §10b). Remaining: confirm DeletePortal toggle is wired to
   `SetRemoveMode` (not `SetAddMode`).
5. Commit only the feature files (§11); keep dynamic-occlusion separate.
   - ⚠️ **Revised 2026-06-25:** dynamic occlusion is now *intentionally* part of
     this branch (§13), so this TODO no longer applies — the shader/RPAsset
     occlusion changes are committed together with the feature here.

---

## 13. Dynamic Occlusion (Environment Depth) — IMPLEMENTED on this branch

**Added 2026-06-25.** Real-world objects in front of a portal now hide the portal
content, using Meta's Quest 3 Environment Depth API. This was ported from the
`dynamic-occlusion` branch and then **corrected** so it works for the
*object-shaped* (arbitrary-mesh) portals this branch uses, not just cuboids.

### 13.1 Concept — what occlusion compares against

Meta's occlusion macro answers: *"is the real-world surface at this pixel closer
to the eye than a given reference depth?"* If yes → the pixel is revealed
(passthrough / real world shows); if no → portal content stays.

The **reference depth** is everything. There are three candidate references:

| Reference used | Question it asks | Result |
|---|---|---|
| Virtual room surface (`input.posWorld`) | "real object in front of the virtual *room*?" | ❌ **the cupboard bug** — real objects with a distant virtual wall behind them punch holes |
| Ray-box front face (dynamic-occlusion branch) | "real object in front of the portal *box*?" | ✅ correct, but **only for cuboid openings** |
| **Portal opening's own depth (this branch)** | "real object in front of the portal *opening*?" | ✅ correct **for any opening shape** |

### 13.2 The cupboard bug (why the naive port was wrong)

The portal content material is applied to the **virtual room mesh**
(`GameManager.CreatePortalMaterialSet`), so `input.posWorld` is a point deep in
the virtual scene. Comparing real depth against *that* meant: a real cupboard
with a far virtual wall behind it counted as "in front" → `occ=0` → **hole in the
portal**. Observed on-device: top row of cupboards punched holes, bottom row
(nearer virtual geometry behind it) did not — same real depth, different *virtual*
depth behind them. Confirmed with shader debug modes (occ / virtual depth / env
depth / direct compare).

### 13.3 The fix — use the portal *opening* depth (shape-agnostic)

The portal opening is the **stencil mask mesh** (`Custom/StencilMask`,
`Queue Geometry-1`, `ZWrite On`) — for object-shaped portals this is the picked
**cluster mesh** on layer 9. Because it renders **before** the portal content
(`Geometry+500`) with `ZWrite On`, the mask's front-face depth is already written
into the camera depth texture at every portal pixel — **for any shape** (cube,
sphere, arbitrary `cluster.obj`). The fix:

1. Sample that opening depth from `_CameraDepthTexture` at the current pixel.
2. Reconstruct the opening's world position from it.
3. Feed **that** world position (not `input.posWorld`) into the occlusion macro.

This generalises ray-box: instead of *computing* the front face with box math
(which only works for cuboids), it *reads* the rasterised front face from the
depth buffer (works for any rasterised geometry). No per-shape math, no C# box
binder, no `PortalBoxBinder`.

### 13.4 Files changed for occlusion

| File | Change |
|---|---|
| `Assets/Materials/Shaders/PortalContentUnlit.shader` | `Blend One Zero` → `Blend SrcAlpha OneMinusSrcAlpha`; added `#pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION`; included `EnvironmentOcclusionURP.hlsl` + URP `DeclareDepthTexture.hlsl`; added `_EnvDepthBias`; **occlusion reference switched from `input.posWorld` to the opening world-pos reconstructed from `_CameraDepthTexture`** via `GetNormalizedScreenSpaceUV` + `ComputeWorldSpacePosition(UNITY_MATRIX_I_VP)`. |
| `Assets/Settings/Mobile_RPAsset.asset` | `m_RequireDepthTexture: 0 → 1` so `_CameraDepthTexture` is populated. (Active "Mobile" quality tier uses this asset — verified.) |

Occlusion only runs inside `#if defined(HARD_OCCLUSION)||defined(SOFT_OCCLUSION)`;
the keyword is toggled globally by `EnvironmentDepthManager` on `OVRCameraRig`
(`OcclusionShadersMode = Soft/Hard`). With neither keyword, the portal renders
with no occlusion (safe fallback).

### 13.5 Soft vs hard occlusion (already wired)

Soft occlusion uses the continuous `[0,1]` value as alpha (`col.a *= saturate(occ)`
+ alpha blending) instead of a hard `clip(occ-0.5)`, giving smooth edges that
follow object silhouettes. Hard occlusion returns binary 0/1. Both supported via
the keyword; Soft is the intended mode.

### 13.6 Current behaviour & limitations (on-device, 2026-06-25)

- ✅ Objects **very close to the headset** (e.g. a hand) occlude the portal
  cleanly, with a clean boundary in idle mode.
- ⚠️ **Objects between the headset and the portal — right in front of, or midway
  to, the portal — are NOT occluded accurately.** They often fail to appear in
  front of the portal as they should, and only do so partially / at certain
  distances.
- ⚠️ In **Add/Delete portal mode**, the occlusion "hole" for a hand / dynamic
  object can appear **misaligned / in the wrong position**.
- ⚠️ Residual **edge flicker / wobble** remains even when occlusion is otherwise
  correct.

### 13.7 Suspected causes of the remaining occlusion issues

- **World-pos reconstruction in stereo** — the opening depth is reconstructed from
  `_CameraDepthTexture` using screen UV + `UNITY_MATRIX_I_VP`. In single-pass
  instanced stereo this is finicky; an incorrect screen-UV or per-eye viewport
  produces a **displaced** reference point, which would explain both the
  "midway objects not occluded at certain distances" and the "hole misaligned in
  Add/Delete mode" symptoms. (`GetNormalizedScreenSpaceUV` was adopted to address
  the viewport scaling, but on-device verification is still pending.)
- **Mask reaching the depth texture** — if the cluster/mask mesh (layer 9) is not
  reliably written into `_CameraDepthTexture` (URP depth prepass layer/render-type
  filtering), the reference depth is wrong and occlusion fails. Symptom would be
  the portal going fully solid or fully transparent. Fallback: a dedicated mask
  depth prepass (the `PortalOccPrepass` pass scaffolded on `dynamic-occlusion`).
- **Inherent Environment Depth limits** (documented separately): ~30 Hz depth
  sensor vs 72 Hz display → stale depth during head motion; left/right eye depth
  reprojection disagreement near edges; "flying pixels" at depth discontinuities;
  unreliable returns on metallic/thin geometry. These cause the edge flicker and
  cannot be removed by a reference-point fix — they need temporal smoothing.

### 13.8 Next steps for occlusion (not yet done)

1. **Add a debug visualization mode** to output the reconstructed opening depth as
   colour, to confirm on-device whether the reference point tracks the opening
   correctly (isolates the stereo-reconstruction hypothesis).
2. If the mask isn't in `_CameraDepthTexture`, switch to a **dedicated mask depth
   prepass** instead of sampling the shared camera depth.
3. **Temporal smoothing** (EMA / history blend of `occ`) to reduce edge flicker —
   the only fix for the inherent sensor noise; shape-agnostic.

---

## 14. Segmentation Upgrade Plan — RANSAC+DBSCAN → learned / convexity-based

**Context (2026-06-25).** Clusters are produced offline by
`Assets/StreamingAssets/clusters/export_clusters.py` (RANSAC plane removal ×6 →
DBSCAN on the remaining points → per-cluster crop + QEM simplify). This runs after
a build/run when a new mesh is detected. The known weakness: **objects that touch
get merged into one cluster**, because RANSAC only removes planes and DBSCAN
clusters by *proximity* — touching objects share continuous geometry and fuse.

### 14.1 Why proximity-based clustering can't fix merges

DBSCAN/Euclidean clustering separates objects only when there is *empty space*
between them. Two objects in contact (cup on table, chair against wall, leg on
floor) have zero gap → always merged. No `eps`/`min_points` tuning fixes this;
the *signal* (distance) is wrong for the problem.

### 14.2 Candidate better algorithms (mesh/point-cloud, no RGB needed)

| Method | Signal | Splits touching objects? | RGB? | Effort |
|---|---|---|---|---|
| DBSCAN / Euclidean (current) | distance/density gaps | ❌ | No | — |
| Region growing (normals+curvature) | normal continuity | ⚠️ partial | No | small |
| **LCCP / CPC** (PCL) | merges across **convex** seams, cuts at **concave** | ✅ | No | small–med |
| **Mask3D / SoftGroup** | learned 3D **instance** segmentation (ScanNet/S3DIS) | ✅ semantic | No | high (GPU) |
| Point Transformer v3 / MinkowskiNet | learned semantic features | ✅ | No | high (GPU) |
| SAM3D / Point-SAM | promptable zero-shot 3D | ✅ | No | med–high |

Note: learned 3D segmentation (Mask3D, Point Transformer, sparse-conv nets) is
"CNN object segmentation" that runs **directly on geometry** — it does **not**
need camera RGB. The pipeline currently has mesh only (no RGB), so image-based SAM
/ Mask R-CNN are not directly applicable; their 3D counterparts are.

### 14.3 Recommendation

- **For "just split touching objects" (no labels):** **LCCP / CPC** is the
  best-suited choice — it targets the exact cause (concave contact seams),
  deterministic, one main knob (concavity threshold), minimal setup.
- **For semantic per-object instances / max accuracy (GPU available):** **Mask3D**
  (ScanNet-pretrained), heavier setup but real labelled instances.

### 14.4 Open question — which mesh feeds segmentation

There are conceptually two meshes: a **dense** reconstruction (external camera) and
a **coarse** scene mesh (Meta headset). The Unity repo only consumes the external
mesh server's output and does **not** reveal which one `export_clusters.py` runs
on. This must be confirmed in the server-side script (which `.obj` it loads).
**Any better algorithm should run on the DENSE mesh** — the coarse headset mesh
fuses touching objects into blobs *before* clustering, and the concavity signal
LCCP relies on is only present in dense geometry.

### 14.5 Plan for trying SAM3D / a new model

The segmentation script is a normal Python script (mesh input, GPU available) —
swapping the algorithm is a standalone change, no Unity-side change required to
*produce* clusters; the runtime only needs the same `clusterN.obj` output format.
Steps:
1. **Checkpoint the current working state** (this branch) before changing anything
   — so RANSAC+DBSCAN behaviour can be restored if the new model regresses.
2. Keep `export_clusters.py` (RANSAC) as the known-good fallback (e.g. copy to
   `export_clusters_ransac_backup.py`).
3. Add the new segmentation script alongside, producing the **same** `clusterN.obj`
   + `centres.txt` (or manifest) output contract the runtime expects.
4. Compare cluster counts / merge behaviour against the RANSAC baseline on the
   dense mesh before switching the runtime over.

### 14.6 Checkpoint — restore commands

A git tag `checkpoint-ransac-seg` was created at the working RANSAC+DBSCAN state
(2026-06-25) before any SAM3D / segmentation changes. To restore to this point:

```bash
# Option A — discard ALL changes since the checkpoint and restore exactly:
git reset --hard checkpoint-ransac-seg

# Option B — safer, keep current work on a separate branch first:
git checkout -b sam3d-attempt      # save current state here
git checkout object-shaped-portals
git reset --hard checkpoint-ransac-seg

# Option C — just branch off the checkpoint without touching current branch:
git checkout -b restore-point checkpoint-ransac-seg
```

> ⚠️ Option A is destructive — any uncommitted changes are lost permanently.
> If you have uncommitted work at restore time, stash it first:
> `git stash` before `git reset --hard`, then `git stash pop` after.

### 14.7 Checkpoint — normals-augmented DBSCAN (2026-06-26)

Marker commit `dc2de3e` ("checkpoint") sits on top of `237f2f1`
("normal augmented ransac+dbscan improvement"), which added
`export_clusters_normals.py` (RANSAC strip + DBSCAN on the 6-D
position+normal feature) and the regenerated `clusterN.obj` / `clusters.json`.
To return to this known-good state:

```bash
# Option A — discard ALL changes since the checkpoint and restore exactly:
git reset --hard dc2de3e

# Option B — safer, keep current work on a separate branch first:
git checkout -b wip-after-normals   # save current state here
git checkout object-shaped-portals
git reset --hard dc2de3e

# Option C — just branch off the checkpoint without touching current branch:
git checkout -b restore-normals dc2de3e
```

> ⚠️ Option A is destructive — any uncommitted changes are lost permanently.
> If you have uncommitted work at restore time, `git stash` first, then
> `git stash pop` after. (Hashes are stable as long as history isn't rewritten;
> `git log --oneline` will show them if they ever change.)
