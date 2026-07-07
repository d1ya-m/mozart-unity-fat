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

1. **ScanMode** (Phase 5, not yet built) — default `ReloadExisting` (reuse the device's
   existing room scan); `FreshSpaceSetup` forces a new Space Setup capture.
2. **Coordinate basis** — NO X-flip both legs (see above).

---

## How to run the segmentation server
See `tools/segmentation_server/README.md`. Summary: `pip install flask`,
`py -3.11 tools/segmentation_server/app.py`, find laptop IP via `ipconfig`, set
`MeshSegmentationClient.segmentServerUrl` = `http://<laptop-ip>:5000`, same Wi-Fi.

---

## MANUAL TEST CHECKLIST (fill in as phases complete)
- [x] Phase 2: KFGMP logs show one clean `50636 verts` capture; OBJ looks like the room.
- [ ] Phase 3: `curl http://localhost:5000/health` → ok; POST an OBJ → clusters returned.
- [ ] Phase 4: round-trip vertex match within epsilon; pick lands on aimed object (not mirrored).
- [ ] Phase 5: Scan Room button → segment → portals appear with virtual content.
