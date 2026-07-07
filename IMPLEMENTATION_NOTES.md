# Implementation Notes — MRUK Dynamic Portals

Branch: `mruk-dynamic-portals`
Baseline commit (parent of this work): `36e8217` (docs: internship report draft …)
Branch baseline commit: `f79e792` (chore: baseline before mruk-dynamic-portals)

Goal: live "scan room → segment → place/remove object-shaped portals" using the
headset's own MRUK global mesh instead of the pre-baked external mesh.

---

## Components added (the mesh-source seam for a future SfM swap)

| File | Role |
|---|---|
| `Assets/Scripts/Mesh/GlobalMeshProvider.cs` | **The mesh-source seam.** Grabs the MRUK GLOBAL_MESH at runtime, exposes `RoomMesh` + `RoomMeshTransform` + `RoomMeshReady` event. A future COLMAP/OpenMVS source implements the SAME shape and nothing downstream changes. |
| `Assets/Scripts/Mesh/MeshSegmentationClient.cs` | Serializes the room mesh to OBJ (no X-flip) and POSTs to the segmentation server; returns `{count,indices[],base_url}`. Falls back to bundled clusters on failure. |
| `tools/segmentation_server/app.py` | Off-device Flask server wrapping `export_clusters_normals.py`. Works in a temp copy so it never clobbers the bundled fallback. |
| `tools/segmentation_server/README.md` | How to run the server + point the Quest at it. |

Still TODO (later phases): `ScanRoomFlow.cs` (button flow), ObjectPicker redirect
(Phase 4), virtual portal content (Phase 5).

---

## Phase 2 status — DONE and verified on device

The MRUK GLOBAL_MESH is produced by a dedicated `EffectMesh_GlobalMesh` (Labels =
GLOBAL_MESH). `GlobalMeshProvider` reliably captures it:
```
[KFGMP] Global mesh: 50636 verts, 101912 tris (object='GLOBAL_MESH_EffectMesh', nameMatch=True)
```
- Selection rejects decoy meshes (requires >= 1000 triangles; the earlier "205 verts,
  0 tris" plane-anchor mesh is no longer picked).
- Captures exactly once (`_captured` guard) even though both the SceneLoadedEvent and
  the MRUK poll can trigger.
- Writes `Application.persistentDataPath/mruk_global_mesh.obj` for inspection; pulled
  and confirmed it looks like the real room.

---

## THE COORDINATE DECISION (critical — audit §G3)

**Chosen: NO X-flip on either leg (identity round-trip).**

- The MRUK mesh is ALREADY in Unity space.
- `MeshSegmentationClient.MeshToObj` exports it in **world space with NO X-flip**.
- The segmenter preserves coordinates (crop + simplify only).
- **Phase 4 requirement:** when ObjectPicker loads the returned `clusterN.obj`, it must
  load them with `MeshDownloadManager.flipObjXAxisForUnity` **BYPASSED** (the loader
  flips X by default for the external-server mesh; MRUK clusters must NOT be flipped or
  they land mirrored). This is the #1 risk and is verified in Phase 4 with a pick test.

Alternative (not chosen): pre-flip X on export so the loader's flip cancels. Either
works; we picked no-flip-both-legs because it's the least surprising.

---

## THE TWO DECISIONS (current values)

1. **ScanMode** (ScanRoomFlow) — default **`FreshWalkAroundScan`**: pressing "Scan Room"
   launches the Quest **Space Setup** wizard (`OVRScene.RequestSpaceSetup()`) so the user
   physically WALKS AROUND and scans the room, then the app auto-loads + segments the
   fresh result. Alternative `ReloadExisting` skips the wizard and reuses the existing
   scan (fast, for repeat testing). NOTE: Space Setup is Meta's system UI — the app
   pauses during it and resumes on finish/cancel; we can't restyle that wizard.
2. **Coordinate basis** — NO X-flip both legs (see above).

---

## How to run the segmentation server
See `tools/segmentation_server/README.md`. Summary: `pip install flask`,
`py -3.11 tools/segmentation_server/app.py`, find laptop IP via `ipconfig`, set
`MeshSegmentationClient.segmentServerUrl` = `http://<laptop-ip>:5000`, same Wi-Fi.

---

## PHASE 5 — EDITOR WIRING (manual, do in Unity)

### A. Add the ScanRoomFlow component
1. In the Hierarchy, create an empty GameObject `ScanRoomFlow` (or reuse an existing
   manager object). Add the **ScanRoomFlow** script.
2. In its Inspector, assign the 3 references:
   - `Mesh Provider` = the `GlobalMeshProvider` GameObject.
   - `Segmenter`     = a GameObject with **MeshSegmentationClient** (add the script to
     any object, e.g. GlobalMeshProvider, and set its `Segment Server Url` =
     `http://147.229.183.37:5000`  — YOUR laptop IP; re-check with `ipconfig`).
   - `Object Picker` = the `ObjectPicker` GameObject.
   - `Mode` = `ReloadExisting` (default).

### B. Rewire the existing "Scan Room" button (IMPORTANT)
The scene still has a "Scan Room" button wired to the OLD
`KeyframeCaptureManager.SetScanMode` (that script is slated for deletion in Phase 1).
Repoint it:
1. Select the "Scan Room" button in the ToolMenu.
2. In its `On Click` / `On Value Changed`, remove the `KeyframeCaptureManager.SetScanMode`
   call.
3. Add a call → object = `ScanRoomFlow`, function = `ScanRoomFlow.OnScanRoomPressed`.

### C. Portal content (what shows INSIDE the portals)
Add a virtual-scene object so portals aren't empty:
1. Create a GameObject (e.g. a large inverted sphere / skybox quad / a small virtual
   room) positioned around the play area.
2. Put it on the **portalContent** layer (layer 10).
3. Give its renderer a material using **Custom/PortalContentUnlit** (it already does
   `Stencil Ref 6 Comp Equal`, so it only shows where a portal mask wrote stencil 6).
   - Simplest: duplicate an existing PortalContentUnlit material, or create a new
     material with that shader and a colour/texture you like.
4. This shows through any portal, independent of the real room's texture.

> The MASK path is unchanged — Add/Remove Portal still stencils the cluster silhouette;
> this content object just fills what's revealed.

---

## MANUAL TEST CHECKLIST (fill in as phases complete)
- [x] Phase 2: KFGMP logs show one clean `50636 verts` capture; OBJ looks like the room.
- [x] Phase 3: server tested on the REAL room mesh → `{"count":31,"indices":[0..30]}`,
      POST /segment returned 200. Laptop IP = 147.229.183.37 (use `http://147.229.183.37:5000`).
- [x] Phase 4 (a) round-trip coord check PASSED: source MRUK mesh bbox
      X[-8.01,1.33] Y[-0.04,2.79] Z[-4.90,4.31]; all 31 clusters lie INSIDE it (no
      mirror/offset). Confirms the no-X-flip decision is correct through the server.
- [ ] Phase 4 (b) ON-DEVICE pick test: trigger on a cluster, read the `[ObjectPicker]
      PICKCHECK … hit->centroid=Xm` log. Small distance (<~0.5m) = correct; large or
      mirrored centroid = X-flip wrong (flip `serverClustersSkipXFlip`).
- [ ] Phase 5: Scan Room button → segment → portals appear with virtual content.
