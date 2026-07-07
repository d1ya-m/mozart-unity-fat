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

> **Update 2026-07-06 — PART TWO added:** Work has started on letting the user
> **scan their own room live on the Quest** and reconstruct a fresh mesh to feed the
> segmentation → portal pipeline (instead of the pre-baked `mesh-3hz-4.obj`). Stage 1
> (on-device capture) **works**; Stage 2 (mesh reconstruction) has correct per-frame
> geometry but an **unresolved multi-frame alignment issue**; Stage 3 (auto-segment +
> wire in) is **not yet end-to-end**. See **PART TWO** (§S0–§S9) at the end of this
> document, including the current blocker (§S6), run instructions (§S7), and the
> cleanup checklist (§S8).

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

---

# PART TWO — Live Room Scanning → Fresh Mesh → Segmentation

> ⚠️ **SUPERSEDED by PART THREE (2026-07-07).** The depth-frame reconstruction described
> in this Part was **abandoned** (never resolved the alignment drift, §S6) and replaced by
> the headset's MRUK global mesh. `KeyframeCaptureManager` + the reconstruction scripts are
> now DEAD code (kept + documented, see §T7). Read Part Three for the final state; this Part
> is retained only as the record of the attempt.

> **Added 2026-07-06.** Everything above (Part One) assumes the room mesh is
> **pre-baked** offline (`mesh-3hz-4.obj`) and segmented once. Part Two is the new
> work: let the **user scan their own room on the Quest 3**, reconstruct a fresh
> mesh from that scan, and feed it into the same segmentation → portal pipeline.
> This part is **IN PROGRESS** — the capture (Stage 1) works and is verified; the
> mesh reconstruction (Stage 2) produces geometrically-correct-per-frame output but
> still has a **multi-frame alignment problem** (documented in §S6). Stage 3
> (auto-segment + wire into the portal UI) is **not yet wired end-to-end**.

## S0. Why this exists — the goal pipeline vs the current pipeline

### Current pipeline (Part One, working)
```
[offline, once]  dense room scan (external camera) ─► mesh-3hz-4.obj
                                                          │
                 export_clusters_normals.py  ◄────────────┘
                 (RANSAC strip + normals-DBSCAN)
                                                          │
                                    clusterN.obj + clusters.json
                                                          │
[runtime, Quest] ObjectPicker preloads clusters ─► point + Add/Delete ─► portals
```
The mesh is **static** and baked once. If the room changes, or you want a
different room, someone has to re-run the external-camera scan and the segmenter by
hand, then rebuild.

### Goal pipeline (Part Two, target)
```
[runtime, Quest]  user presses "Scan Room"
                     │  walks around; on-device capture writes per-keyframe
                     │  depth PNG + camera pose JSON
                     ▼
                  captures/<session>/  (frame_XXXX.json + .depth.png + manifest.json)
                     │  adb pull  (or, future: pushed to a server)
                     ▼
[PC / server]     reconstruct_clean.py   ─►  mesh.obj      (Stage 2)
                     ▼
                  export_clusters_normals.py  ─►  clusterN.obj + clusters.json   (Stage 3)
                     ▼
[runtime, Quest]  ObjectPicker loads THOSE clusters ─► Add/Delete portals as before
```
The **key difference**: the mesh comes from the user's own live scan instead of a
pre-baked file. Everything downstream (segmentation, ObjectPicker, Add/Delete
portal UI, stencil rendering, occlusion) is **unchanged** — it just consumes a
different `mesh.obj` / `clusterN.obj` set. That is the whole design intent: make
the *input* mesh dynamic without touching the proven *downstream*.

> **Where the two pipelines join:** Stage 2 must output an `.obj` that, once copied
> to `Assets/StreamingAssets/clusters/mesh-3hz-4.obj` (the name the segmenter reads,
> §2), makes `export_clusters_normals.py` produce `clusterN.obj` + `clusters.json`
> that `ObjectPicker` already knows how to preload (§5). No runtime code change is
> required for the clusters themselves — the Add/Delete portal buttons work with the
> new clusters exactly as they did with the old ones.

---

## S1. Stage 1 — On-device capture (`KeyframeCaptureManager.cs`) — WORKING

The capture script lives at `Assets/Scripts/Debug/KeyframeCaptureManager.cs`. It
runs on the Quest and, **while scanning is on**, writes one *keyframe* per novel
viewpoint as the user moves.

### What one keyframe is
Each accepted viewpoint produces two files under
`Application.persistentDataPath/captures/<sessionId>/`:
- **`frame_XXXX.depth.png`** — a 16-bit grayscale PNG. Each pixel is the metric
  depth in **millimetres** (so 0..65535 covers 0..65.5 m). Encoded by a hand-rolled
  PNG writer (`EncodeGray16Png`, with CRC32/Adler32/zlib) because Unity has no
  16-bit-gray encoder.
- **`frame_XXXX.json`** — the camera pose + intrinsics for that frame (schema below).

Plus one **`manifest.json`** listing every frame, the capture resolution, and the
depth scale (0.001 = mm→m).

### The JSON schema (per frame)
```jsonc
{
  "class_name": "PinholeCameraParameters",   // Open3D-compatible header
  "extrinsic":  [16 floats],                 // Open3D world->camera (OpenCV convention)
  "intrinsic":  { width, height, intrinsic_matrix:[9] },
  "unity_position": [x,y,z],                 // raw Unity head world pos
  "unity_rotation_quat_xyzw": [x,y,z,w],     // raw Unity head world rot
  "head_view":  [16 floats],   // NEW (§S6): world->eye from the Unity HEAD pose
  "depth_reprojection": [16],  // SDK's proj*view*trackingWorldToLocal (left eye)
  "tracking_space_local_to_world": [16],
  "depth_proj": [16],          // SDK depth-cam projection (eye->clip)
  "depth_view": [16],          // SDK depth-cam view (world->eye, depth cam's OWN pose)
  "depth_fov_tangents": [tanL, tanR, tanT, tanD],  // depth cam frustum half-angle tangents
  "depth_near_far": [near, far]
}
```
The many redundant fields exist because **we did not know up-front which coordinate
convention would reconstruct correctly**, so we recorded *all* of them at capture
time — that way we can re-derive the world points offline **without re-scanning**.
This turned out essential (§S6).

### How the depth-camera intrinsics/pose are obtained (C# reflection)
The Meta SDK does **not** expose the depth camera's true FOV or pose publicly. It
keeps them in an internal struct `Meta.XR.EnvironmentDepth.DepthFrameDesc` and an
internal static `EnvironmentDepthUtils.CalculateDepthCameraMatrices`. We reach them
with **`System.Reflection`** (`ResolveDepthReflection()`):
- `EnvironmentDepthManager.frameDescriptors` (internal field) → the per-eye
  `DepthFrameDesc` for the current depth frame.
- `CalculateDepthCameraMatrices(desc, out proj, out view)` (internal static) → the
  SDK's own `proj` and `view` matrices, written to the JSON as `depth_proj` /
  `depth_view`, with the frustum tangents as `depth_fov_tangents`.

Logcat confirms it resolved: `[KFCAP] Depth reflection resolved`. If the SDK ever
renames these, you'll instead see `Depth reflection incomplete` and the field names
must be re-checked.

> **Why not the render-camera FOV?** The *render* camera (what you see in VR) has a
> different (wider ~100°) FOV than the *depth* camera (~96°×100°, and off-centre —
> see §S6). Using the render FOV fanned every frame's points out ~2× and nothing
> aligned (the first big failure, 25 m mesh). The reflection path gets the depth
> camera's TRUE optics.

### Depth blit shader (`Assets/Materials/Shaders/EnvDepthCapture.shader`)
The SDK's environment-depth texture is a `Texture2DArray` (per-eye slices). We blit
slice 0 (left eye) into an `RFloat` render target, linearising the raw device depth
to **metres** using `_EnvironmentDepthZBufferParams`. A subtle bug here (§S5, "0.13 m
constant") was that the standard `SAMPLE_TEXTURE2D_X` macro fails in a non-stereo
blit; the fix was to declare an explicit `Texture2DArray<float>` under a *distinct*
name (`_EnvDepthArray`, bound from C# via `SetTexture`) and sample slice 0 directly.

### Capture control — button/toggle (Stage 5)
- `capturing` **starts OFF**. Scanning begins only when the user turns it on.
- **`SetScanMode(bool on)`** — public, wired to the ToolMenu **"Scan Room" Toggle**
  `On Value Changed (Boolean)` (dynamic bool), exactly like the Add/Delete portal
  toggles (§10b). Turning it ON clears the keyframe list + skip counter and logs
  `Scan STARTED`; OFF logs `Scan STOPPED. Captured N keyframes`.
- **Controller shortcut** — right **A** (`OVRInput.Button.One`) or left **X**
  (`Button.Three`) toggles scanning too, so you can start/stop even when the
  ToolMenu panel is hidden (it is a head-follow panel and disappears when you look
  away). The controller path drives `_scanToggle.isOn` so the UI and controller
  never desync (falls back to calling `SetScanMode` directly if no toggle is wired).
- `RefreshScanLabel()` updates the button sublabel live:
  `press to start` → `scanning… N frames` → `done — N frames`.

> **Required behaviour (per the brief):** the scan must happen **only** when the
> user presses the Scan Room button, and stop when they press it again. That is now
> the case (`capturing = false` by default; toggled solely by the button/controller).
> An earlier debugging build had `capturing = true` (auto-start) to iterate faster;
> that has been reverted.

### Tracking-glitch guards (added 2026-07-06)
When the headset briefly loses tracking (taken off, or moved too fast), the head
pose snaps to near-origin `(0,0,~0)` and then jumps back. Those frames corrupt the
cloud. Two guards in `Update()` reject them **at capture time**:
- **Near-origin**: `headPos.sqrMagnitude < 0.01` (< 10 cm from world origin) → skip.
- **Jump**: `Distance(headPos, lastKeyframePos) > 2 m` in one frame → skip + warn
  `[KFCAP] Tracking glitch — pos jumped >2m, skipping frame.`

A second, matching filter runs offline in Python (`_good_indices`, §S3) as a safety
net for any that slip through.

---

## S2. Stage 1 verified behaviour (on-device)

From `adb logcat -s Unity | grep KFCAP`, a good run shows:
```
[KFCAP] Session '2026-07-06_20-08-00' writing to: /storage/.../captures/2026-07-06_20-08-00
[KFCAP] Depth reflection resolved — will capture clean proj/view per frame.
[KFCAP] Head tracking initialised — capture enabled.
[KFCAP] Scan STARTED by user.
[KFCAP] CAPTURE #0 pos=(-0.04,1.48,-0.04) fwd=(-0.03,0.03,1.00) (skipped so far=0)
[KFCAP] CAPTURE #1 ...
...
[KFCAP] Scan STOPPED. Captured 133 keyframes.
```
- ✅ Pose tracking, novelty selection, per-frame JSON + depth PNG all write.
- ✅ `depth_proj` / `depth_view` / `depth_fov_tangents` / `head_view` present in JSON.
- ✅ Glitch guard fires on real tracking loss.
- ✅ 130+ keyframes captured in a ~2-minute scan.

---

## S3. Stage 2 — Offline reconstruction (Python, `captures/`)

The reconstruction scripts live in the repo-root `captures/` folder (NOT in
StreamingAssets — they are a PC-side tool, not shipped in the app).

| File | Role |
|---|---|
| `captures/unproject_clean.py` | The core: turns one keyframe (depth PNG + pose) into 3D world points. Also a `main()` that prints alignment/planarity diagnostics for a session. |
| `captures/reconstruct_clean.py` | Full pipeline: unproject every good frame → merge → denoise → Poisson mesh → write `<session>/mesh.obj`. |
| `captures/reconstruct_tsdf.py`, `reconstruct_icp.py` | Older/alternative reconstructions (TSDF-merge, ICP-refined). Superseded by `reconstruct_clean.py`; kept for reference (candidates for deletion, §S8). |
| `captures/test_reproj.py` + many `diag_*/check_*/solve_*` scripts | Diagnostic scratch scripts from the debugging journey (§S6). **All disposable** (§S8). |

### The unprojection concept (what "unproject" means)
A depth pixel says "there is a surface `z` metres away along this pixel's viewing
ray." To place that surface in the shared world:
1. **Pixel → eye-space ray.** For pixel `(u,v)`, the ray direction in the camera's
   own frame is set by the **FOV tangents**:
   ```
   ray_x = -tanL + (u+0.5)/W * (tanL + tanR)
   ray_y = -tanD + (v+0.5)/H * (tanD + tanT)
   ```
   (linear interpolation across the frustum: left edge → `-tanL`, right edge →
   `+tanR`, etc.)
2. **Scale by depth.** Eye-space point = `(z·ray_x, z·ray_y, −z)` (camera looks
   down −Z, right-handed).
3. **Eye → world.** `world = inv(view) · eye`.

We deliberately use the **raw FOV tangents** (`depth_fov_tangents`), NOT the
`depth_proj` matrix, because the proj matrix bakes the frustum offset into `a`/`b`
terms whose sign convention we could not pin down (§S6 "Attempt with proj matrix").
The tangents are unambiguous physical values, so the ray math is provably right.

### The `main()` diagnostics
`py -3.11 unproject_clean.py <session>` prints, for the first few good frame pairs:
- **`extentA`** — bounding box of one frame's cloud (a single wall seen from 1–2 m
  should be a few metres, not 8 m).
- **`overlap`** — median nearest-neighbour distance between two adjacent frames'
  clouds. **This is the alignment metric**: adjacent frames should overlap within
  ~2–5 cm. Large overlap = misalignment.
- **`merged bbox`** + **plane list** — RANSAC planes on the merged cloud with their
  normals, labelled floor/ceil (n≈(0,1,0)) / wall (n·y≈0) / tilt.

### The glitch filter (`_good_indices`)
Drops frames whose `unity_position` is < 10 cm from origin, or that jump > 2 m from
the last accepted frame. Prints e.g. `skip frame 39: jump 1.2m from last good`.

### The Y clamp
After unprojecting, points with world `Y < −0.1` or `Y > 4.5` m are discarded — a
few bad frames cast rays far below the floor / above the ceiling, blowing up the
bounding box. (A blunt instrument; see §S6 for why it's needed and §S8 for the
principled replacement.)

---

## S4. Stage 2 pipeline (`reconstruct_clean.py`)

```
for each good frame:
    pts = unproject_clean(...)          # depth PNG + pose -> world points
    voxel_down_sample(0.03)             # thin to 3 cm grid
merge all frames
voxel_down_sample(0.03)
remove_statistical_outlier(20, 2.0)     # drop flyers
estimate_normals + orient_consistent
Poisson(depth=9) -> mesh
trim lowest-5%-density verts            # remove Poisson's balloon skin
write <session>/mesh.obj
```
The Poisson step turns the point cloud into a watertight-ish triangle mesh (the
format the segmenter needs).

---

## S5. Errors encountered & fixed (Stage 1 + 2), with the concept behind each

| # | Symptom | Root cause | Fix | Concept |
|---|---|---|---|---|
| 1 | Depth PNG all one value (0.13 m / 0.22 m) | `SAMPLE_TEXTURE2D_X` macro needs the stereo keyword, absent in a plain blit → sampled empty texture | Declare explicit `Texture2DArray<float> _EnvDepthArray`, bind from C# via `SetTexture`, sample slice 0 | Stereo texture macros silently no-op outside a stereo pass |
| 2 | Shader compile: `redefinition of _EnvironmentDepthTexture` | Our name clashed with the SDK global | Use a distinct name `_EnvDepthArray` | Global shader names are a flat namespace |
| 3 | Reconstruction 25×11×21 m, hundreds of fragments | Used the **render** camera FOV (~100°, wide) not the **depth** camera FOV | Capture depth cam's own proj/view via reflection | Depth cam ≠ render cam optics |
| 4 | `AttributeError: ndarray has no attribute 'ptp'` | NumPy removed `.ptp()` method | `np.ptp(x)` | API change |
| 5 | Tracking glitch frames (pos `(0,0,0)` then wild jump) corrupt cloud | Headset removed / fast motion loses tracking | Near-origin + jump guards in C# **and** Python | IMU/inside-out tracking drops out; must be filtered |
| 6 | `depth_near_far = [0.1, Infinity]` breaks matrix inverse | Depth cam uses an **infinite far plane**; `proj_inv` at z_ndc=+1 divides by zero | Don't decode via proj matrix; use FOV tangents | Reversed-Z / infinite-far projection is singular at the far plane |
| 7 | Every unprojected pixel 34 cm off | `proj` matrix `a`/`b` (principal-point offset) sign convention unknown; the depth cam is **off-centre** (optical axis at pixel (194,306), not (256,256)) | Bypass proj matrix, use raw `depth_fov_tangents` | Asymmetric frustum: NDC (0,0) is NOT the optical axis |
| 8 | Nested `captures/captures/...` folders on pull | `adb pull <dir> <dest>` where `<dest>` already contains a `captures` folder nests it | Pull to a fresh dir; or reconstruct against the deepest `2026-...` folder | `adb pull` copies the source *folder* into dest |
| 9 | Merged mesh only floor/ceiling, XZ bbox 13×12 m | Multi-frame **rotation** drift (see §S6) — per-frame geometry correct, frames don't stack in X/Z | **Open** — mitigated with `head_view`; not fully solved | Small per-frame yaw error smears walls across metres |

---

## S6. Stage 2 CURRENT BLOCKER — multi-frame alignment (rotation drift)

This is the open problem at the time of writing. **Read this before touching
Stage 2.**

### What is proven CORRECT
- **Single-frame planarity is high (31–46%+ on the dominant plane).** One frame's
  depth unprojects to clean flat surfaces with sensible normals — a frame facing a
  wall gives a plane with normal along that wall's axis. → the **per-pixel depth →
  world math is right.**
- **Camera positions are right.** `inv(depth_view)·[0,0,0,1]` matches the logged
  head position to a few cm.
- **The room HEIGHT is right.** Merged `Y` extent comes out ~3.5–4.3 m consistently
  — every frame agrees on floor-to-ceiling distance.

### What is WRONG
- **The horizontal (X/Z) extent is far too big (13×12 m for a small room), and
  adjacent-frame `overlap` is inconsistent** (some pairs 1–2 cm ✅, others 50–60 cm
  ❌). Frames that see the *same* wall place it at *different* world X/Z. Only
  floor/ceiling planes survive RANSAC on the merged cloud because those are the only
  surfaces all frames happen to agree on (they share the vertical axis).

### Diagnosis
The **rotation** in the per-frame `view` matrix drifts between frames. The Y axis
(gravity) is stable across frames (hence correct height), but yaw/heading is not —
a small per-frame heading error rotates each wall by a few degrees, and at 2–4 m
range that smears the wall across metres in world X/Z. The camera *position* is
fine; the camera *orientation* used for unprojection is slightly off per frame,
and the errors don't cancel.

### Attempts (chronological)
1. **`depth_reprojection` composite matrix decode** — tried to recover camera
   centre + ray directions from the SDK's `proj*view*trackingWorldToLocal`.
   Unstable: the infinite far plane (#6) makes the w=0 direction trick degenerate;
   forwards collapsed to −Y.
2. **`depth_proj` + `depth_view` separately (via reflection)** — clean matrices, but
   the `proj` principal-point terms `a`/`b` have a sign/convention we couldn't pin
   down (#7): even the centre pixel came out 34 cm off.
3. **Raw FOV tangents + `depth_view`** — fixed the per-pixel error (single-frame
   planarity jumped to 30–46%, height correct). **Adjacent overlap improved to
   1–2 cm for many pairs** — but some pairs still 50 cm, and merged XZ still 13 m.
   → the *depth camera's own pose* (`depth_view`, from `DepthFrameDesc.createPose`)
   is not perfectly time-synced with the head motion; its rotation drifts.
4. **`head_view` (CURRENT)** — write a view matrix built from the **Unity head
   pose** (which the POSE logs prove is smooth and correct) and prefer it over
   `depth_view` in `unproject_clean`. Rationale: the head rotation is the reliable
   signal; combine it with the depth FOV tangents for ray directions. **Requires a
   fresh build + scan to test** (the field is new). Not yet verified to close the
   gap.

### The two candidate real fixes (if `head_view` alone isn't enough)
- **ICP refinement** (`reconstruct_icp.py` exists): use each frame's captured pose
  only as an *initial guess*, then register each frame onto the growing model with
  point-to-plane ICP before merging. This is the standard robust-scanner approach
  (KinectFusion-style) and directly corrects residual per-frame drift. Earlier ICP
  attempts helped but weren't conclusive because the *input* unprojection was still
  wrong then (#7); worth revisiting now that per-frame geometry is correct.
- **Pose-graph / global registration** (Open3D `pipelines.registration` +
  `global_optimization`) if sequential ICP accumulates drift over a full loop.

### The Y clamp is a band-aid
The `-0.1 < Y < 4.5` clamp (§S3) hides a few wildly-wrong frames rather than fixing
them. Once alignment is solved it should be removed or replaced with a principled
per-frame reject (e.g. reject a frame whose cloud centroid is implausibly far from
the camera). Tracked in §S8.

---

## S7. How to run the whole thing (step by step)

### A. Scan (Quest)
1. Build & Run `current.unity` to the Quest (`File ▸ Build And Run`).
2. Put the headset on. **Press the "Scan Room" button** in the ToolMenu (or the
   controller **A** / **X** button). Logcat shows `Scan STARTED`.
3. Scan slowly, **keeping your head roughly level** (walls need horizontal gaze;
   don't spend the whole scan looking down):
   - Face each wall directly from ~1–1.5 m, pan slowly L↔R and up/down (~10 s each).
   - Briefly look at floor and ceiling.
   - Look into each corner.
   - Aim for **80–130 keyframes** (watch `CAPTURE #N` in logcat).
4. **Press "Scan Room" again** (or A/X) to stop. Logcat: `Scan STOPPED. Captured N`.

### B. Pull (PowerShell — Git Bash mangles `/sdcard/` paths)
```powershell
$adb = "C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
& $adb pull "/sdcard/Android/data/cz.fitvut.fat/files/captures" "C:\Users\bambu\Documents\BRNO_internship\mozart-unity-fat\scans"
```
> ⚠️ `adb pull` copies the whole `captures` *folder* into the destination. Pulling
> repeatedly into the same place creates nested `captures/captures/...` (bug #8).
> Pull into a **fresh** `scans` dir, then find the newest `2026-...` session inside.

### C. Reconstruct (PowerShell — open3d needs Python 3.11)
```powershell
cd "C:\Users\bambu\Documents\BRNO_internship\mozart-unity-fat\captures"
# sanity check alignment first:
py -3.11 unproject_clean.py "..\scans\captures\<SESSION>"
# then build the mesh:
py -3.11 reconstruct_clean.py "..\scans\captures\<SESSION>" --maxd 4.0 --voxel 0.03
```
Good output: adjacent-frame `overlap` ≤ ~5 cm; `merged bbox` ~ room-sized
(e.g. 4×3×3 m); RANSAC planes include **walls** (n·y ≈ 0), not only floor/ceiling.
Writes `<SESSION>/mesh.obj`.

### D. Segment (Stage 3 — the join point)
Copy the reconstructed mesh to the name the segmenter reads, then run it:
```powershell
copy "..\scans\captures\<SESSION>\mesh.obj" "..\Assets\StreamingAssets\clusters\mesh-3hz-4.obj"
cd "..\Assets\StreamingAssets\clusters"
py -3.11 export_clusters_normals.py
```
This regenerates `clusterN.obj` + `clusters.json` in place (§2). No runtime change:
`ObjectPicker` reads `clusters.json` and preloads whatever indices it lists (§5).

### E. Use the portals (Quest)
Rebuild & Run. In the ToolMenu, use **Add Portal** / **Delete Portal** exactly as
before (§10b) — they now operate on the clusters from *your* scan. Point at an
object (green laser = on a cluster), pull the trigger to add/remove its portal.

> **Not yet automated:** steps B–D are currently manual (pull, reconstruct,
> segment, copy). The goal is to chain them — see §S8 "automation" — but the
> alignment blocker (§S6) must be fixed first, otherwise the auto-produced clusters
> would be garbage.

---

## S8. Cleanup checklist — what to remove once Stage 2 works and is final

When the alignment problem (§S6) is solved and the pipeline is accepted, do this
before merging so the branch is clean:

### Python (`captures/`) — DELETE the scratch scripts
These were one-off diagnostics from the §S6 journey and have **no place in the
final pipeline**:
- `test_reproj.py`, `diag_structure.py`, `debug_align.py`, `measure_align.py`,
  `icp_check.py`, `check_extrinsic.py`, `check_relative.py`, `solve_convention.py`,
  `test_depth_orient.py`, `find_pair.py`, `solve_focal.py`, `inspect_mesh.py`,
  `test_depth_interp.py`, `show_cloud.py`, and any other `diag_*/check_*/solve_*`.
- **Keep only** the winning path: `unproject_clean.py` + `reconstruct_clean.py`
  (and `reconstruct_icp.py` **iff** ICP became the alignment fix; otherwise delete
  it and `reconstruct_tsdf.py` too).

### Python — remove band-aids from the survivors
- Delete the **Y clamp** in `unproject_clean.py` (§S6) once alignment is fixed, or
  replace it with a principled per-frame reject.
- Fold the glitch filter's magic numbers (10 cm / 2 m) into named constants.
- Delete `unproject_clean.mat_colmajor`'s unused branches and any dead
  `depth_proj`/`a`/`b` code now that the tangent path won.

### C# (`KeyframeCaptureManager.cs`) — trim redundant JSON fields
Once we know **which** view convention wins (`head_view` vs `depth_view`), stop
writing the losers to shrink each JSON and remove confusion:
- If `head_view` wins: drop `depth_reprojection`, `depth_proj`, `depth_view`,
  `depth_near_far` (keep `depth_fov_tangents` + `head_view` + `intrinsic`).
- If `depth_view` wins: drop `head_view`, `depth_reprojection`, `depth_near_far`.
- Keep `unity_position`/`unity_rotation_quat_xyzw` only if still used for the glitch
  filter; otherwise drop.
- Gate all the verbose `[KFCAP]` per-frame logs behind `verboseLogging` (already
  done for POSE; verify CAPTURE lines too if logcat gets noisy).
- Reduce `maxKeyframes` default from 300 to a realistic cap (~150) if desired.

### C# — remove debug affordances
- The **controller A/X toggle** can stay (it's genuinely useful) but should be
  behind a `[SerializeField] bool allowControllerToggle` if a demo must avoid
  accidental toggles.
- Remove the `EnvDepthCapture.shader` debug modes if any remain (the doc says
  they were already stripped to Mode 4 — verify).

### Repo hygiene
- The pulled `scans/` and `captures/<session>/` scan data are **large and
  disposable** — add to `.gitignore`, don't commit raw scans.
- Only commit: `KeyframeCaptureManager.cs`, `EnvDepthCapture.shader`,
  `unproject_clean.py`, `reconstruct_clean.py`, and the scene wiring for the Scan
  Room button. Keep the ~1 MB of `ScreenCamera_*`/`ScreenCapture_*` debug artifacts
  out of `clusters/` unless they're actually used.

### Automation (only after §S6 is fixed)
- Wrap steps B–D (§S7) in a single `run_pipeline.ps1 <session>` that pulls,
  reconstructs, copies to `mesh-3hz-4.obj`, and segments — so a scan turns into
  clusters with one command.
- Longer term (Part B / server): have the Quest POST the session to the segmentation
  server (`/segment` endpoint, §10 "Runtime segmentation server endpoint") so the
  loop closes without a PC in the middle.

---

## S9. Files changed / added in Part Two (for committing)

**New:**
- `captures/unproject_clean.py` — tangent-based unprojection + diagnostics.
- `captures/reconstruct_clean.py` — Stage 2 mesh pipeline.
- (scratch diagnostics in `captures/` — **do not commit**, see §S8.)

**Modified:**
- `Assets/Scripts/Debug/KeyframeCaptureManager.cs` — reflection-based depth params,
  `head_view` field, glitch guards, Scan Room toggle + controller A/X toggle,
  `capturing=false` default.
- `Assets/Materials/Shaders/EnvDepthCapture.shader` — explicit `Texture2DArray`
  slice-0 sampling, metric linearisation.
- `Assets/Scenes/current.unity` — Scan Room button wiring (`SetScanMode`), sublabel.

**Do NOT commit:** `scans/`, pulled `captures/<session>/` raw data (large, §S8).

---

# PART THREE — MRUK Dynamic Portals (SUPERSEDES Part Two)

> **Added 2026-07-07. Branch `mruk-dynamic-portals`.** Part Two's depth-frame
> reconstruction (`KeyframeCaptureManager` → depth PNGs → offline Poisson) was
> **abandoned** — it never resolved the multi-frame alignment drift (§S6). It is
> replaced by using the **headset's own MRUK global scene mesh** as the live room
> source. Part Two's code is now DEAD (kept + documented, not deleted). This part is
> the real, working final state.
>
> Full standalone report: `Docs/hmd-scan-pipeline-report.md`. Coordinate/seam details:
> `IMPLEMENTATION_NOTES.md`. Removals: `CLEANUP_LOG.md`.

## T1. The three pipelines now in the codebase

| Pipeline | Source of clusters | State | Drives portals? |
|---|---|---|---|
| **A — Live MRUK scan** | Headset MRUK global mesh → server segmentation → clusters | Present, **inactive by default** | Only if `ScanRoomFlow.drivePortalsFromScan = true` |
| **B — Prebaked external mesh** | Bundled `clusterN.obj` (from `mesh-3hz-4.obj`) | **Active (default)** | ✅ Yes |
| **C — Depth capture (Part Two)** | On-device depth PNG + offline reconstruction | **DEAD** (documented, unused) | No |

**Shipped default = Path B** (reliable). Path A is a toggleable, documented deliverable
whose cluster quality is gated by the coarse HMD mesh (see §T6 limitations).

## T2. Goal pipeline (Path A) — what actually runs

```
Quest "Scan Room" button  →  ScanRoomFlow.OnScanRoomPressed()  (ONE press, not a toggle)
  1. MRUK: OVRScene.RequestSpaceSetup() (walk-around) OR LoadSceneFromDevice (reuse)
  2. GlobalMeshProvider.TryCaptureGlobalMesh()  → MRUK GLOBAL_MESH (~50 636 verts) + Transform
        (also written to persistentDataPath/mruk_global_mesh.obj)
  3. MeshSegmentationClient.Segment()  → POST OBJ to laptop Flask server
        tools/segmentation_server/app.py runs export_clusters_normals.py → clusterN.obj + clusters.json
  4. ObjectPicker.ReloadClustersFromUrl()  → download clusters, parent UNDER the MRUK mesh transform
  5. Add/Remove Portal (UNCHANGED)  → StencilMask on the picked cluster
```

In **Path B** (default) steps 1–3 still run (the scan is captured, stored, and segmented for
demonstration/logging), but step 4 is **skipped** — the bundled clusters keep driving portals.

## T3. New runtime files (the mesh-source seam)

| File | Purpose |
|---|---|
| `Assets/Scripts/Mesh/GlobalMeshProvider.cs` | **The mesh-source seam.** Grabs the MRUK `GLOBAL_MESH` MeshFilter at runtime; exposes `RoomMesh`, `RoomMeshTransform`, `RoomMeshReady`. Selects the real room mesh by requiring ≥1000 triangles + `*_EffectMesh` name (rejects the 0-tri decoy). A future COLMAP/SfM source implements the same shape and nothing downstream changes. |
| `Assets/Scripts/Mesh/MeshSegmentationClient.cs` | Serializes the mesh to OBJ (world space, **no X-flip**) and POSTs to `{segmentServerUrl}/segment`; returns `{count, indices, base_url}`. Falls back to bundled clusters on failure. |
| `Assets/Scripts/Mesh/ScanRoomFlow.cs` | Orchestrates the button flow. `ScanMode` (FreshWalkAroundScan / ReloadExisting) + `drivePortalsFromScan` (Path A vs B). |
| `tools/segmentation_server/app.py` + `README.md` | Off-device Flask server wrapping `export_clusters_normals.py`. Works in a temp copy so it never clobbers the bundled fallback. Serves `/segment` + `/clusters/<file>`. |

## T4. Changes to existing files (minimal, additive)

| File | Change | Safe because |
|---|---|---|
| `ObjectPicker.cs` | +`clusterBaseUrl`, +`serverClustersSkipXFlip`, +`_serverClusterParent`, +`_loadGeneration`; `LoadManifest`/`PreloadClustersCoroutine` use the server URL when set; +public `ReloadClustersFromUrl(baseUrl, meshTransform)`; +`PICKCHECK` coord log | When `clusterBaseUrl` is empty (default) the original bundled path runs unchanged |
| `MeshDownloadManager.cs` | +optional `bool? overrideFlipX = null` threaded through `LoadMeshFromServer`→`LoadMeshFromBytes`→`LoadObjMesh` | Defaults to the existing `flipObjXAxisForUnity`; all existing callers unaffected |
| `export_clusters_normals.py` | Reverted to clean defaults after tuning experiments (see §T6) | Values match the original; external-mesh clusters stay clean |
| `current.unity` | Scene wiring of GlobalMeshProvider/MeshSegmentationClient/ScanRoomFlow + Scan Room button → `ScanRoomFlow.OnScanRoomPressed` | Editor-only wiring |

**Unchanged (protected):** StencilMask / PortalContentUnlit / SelectivePassthrough shaders,
GameManager portal logic, MRUK core, and the bundled `mesh-3hz-4.{obj,mtl,jpg}` +
`clusterN.obj`/`clusters.json`.

## T5. Key decisions (verified)

- **Coordinate convention — NO X-flip both legs.** MRUK mesh is already in Unity space; it
  is exported unflipped and its clusters load with the flip bypassed (identity round-trip).
  Verified: (a) all segmented clusters lie inside the source mesh bbox (no mirror); (b)
  on-device `PICKCHECK` hit→centroid distances were **0.16–0.63 m** (correct, not mirrored).
- **Cache keys per reload** — server clusters use `cluster_preload_{generation}_{id}` so they
  don't collide with the bundled load's cache (that collision caused "Preload cluster0 failed
  → 0 clusters"; fixed).
- **GLOBAL_MESH selection** — ≥1000 tris + `*_EffectMesh` name (rejects the 205-vert/0-tri
  plane-anchor decoy).
- **Segmentation off-device** — Open3D can't run on Quest; a laptop Flask server wraps the
  existing segmenter. Chosen over on-device C# RANSAC/DBSCAN (perf) and MRUK
  `DestructibleGlobalMesh` (geometric chunks, not per-object).

## T6. Limitations (why Path B is the default)

The Quest MRUK mesh is **coarse and bumpy**, so RANSAC plane removal (which strips
floor/walls/ceiling before object clustering) is unreliable — a see-saw with no clean middle:

| Segmenter settings | Result on the same room |
|---|---|
| Original (6 planes @3cm, eps 0.15) | ~33 clusters incl. ~6 full-height wall/floor slabs |
| Aggressive (12 planes @6cm, eps 0.13) | 10 object-sized clusters, walls gone — but many objects dropped |
| Aggressive on a smaller scan | 4 clusters — over-stripped, room mostly empty |

Also: scan-to-scan mesh variability (23 vs 4 clusters across presses), no colour/texture on
the HMD mesh, and weakness on thin/concave/incomplete geometry. Full analysis + the
HMD-vs-external-mesh comparison table: `Docs/hmd-scan-pipeline-report.md` §5–§6.

**Conclusion:** the external mesh gives clean, stable clusters and the only textured content;
the HMD mesh is fully dynamic (any room) but its coarse geometry makes automatic per-object
segmentation unreliable without per-scan tuning. So portals ship on the external-mesh clusters
(Path B); the live HMD scan is a demonstrated, toggleable capability (Path A).

## T7. Dead code (Path C — kept, documented, unused)

The earlier depth-frame experiment remains in the repo but **nothing runs it**:
- `Assets/Scripts/Debug/KeyframeCaptureManager.cs` — depth PNG + pose capture (the source of
  the leftover **"N frames" label** on the Scan Room button; cosmetic — the button itself
  calls `ScanRoomFlow.OnScanRoomPressed`).
- `Assets/Scripts/Debug/EnvDepthProbe.cs`, `Assets/Materials/Shaders/EnvDepthCapture.shader`,
  and the inactive `KeyframeCapture` GameObject in `current.unity`.

> ⚠️ The current **Scan Room button is NOT this experiment.** It is one-press (no on/off
> toggle) and stores ONE OBJ (`mruk_global_mesh.obj`), not depth PNGs. The PNG/toggle
> behaviour belonged to the dead `KeyframeCaptureManager`.

Removed in cleanup: `export_clusters_lccp.py`, `lccp_open3d.py` (abandoned LCCP experiment),
`mruk_global_mesh.obj` (regenerated artifact, now gitignored). See `CLEANUP_LOG.md`.

## T8. How to run

**Path B (default, reliable):** Build & Run. Portals use bundled clusters. Scan Room stores +
segments the HMD mesh (logcat `[SCANFLOW] PATH B: ...`).

**Path A (live pipeline):**
1. Laptop: `pip install flask`; `py -3.11 tools/segmentation_server/app.py`.
2. Unity: `MeshSegmentationClient.segmentServerUrl = http://<laptop-ip>:5000`; same Wi-Fi.
3. Unity: `ScanRoomFlow.drivePortalsFromScan = true`.
4. Build & Run; press Scan Room; watch `[SCANFLOW]/[SEGCLIENT]/[ObjectPicker]`; then
   Add/Remove Portal on the scanned objects.

## T9. Relationship to the object-shaped-portal GitHub issue

This work satisfies the issue's core ask — portal masks that follow **real object geometry**
(the cluster mesh) instead of a bounding box, so gaps (e.g. between table legs) stay visible —
for **both** mesh sources, and provides the requested **coarse-HMD vs fine-external** mesh
comparison (§T6, and `hmd-scan-pipeline-report.md` §6). The geometry-based mask was already
met by the existing cluster→StencilMask path (Part One §5, §13.3); Part Three made the cluster
**source** dynamic (the user's own live scan) and characterised its quality trade-off.
