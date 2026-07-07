# Live HMD Room-Scan → Segmentation → Object-Shaped Portals

**Report for the internship / the GitHub issue on geometry-based (non-bounding-box)
portal masks.**
Branch: `mruk-dynamic-portals` · Period: 2026-07-06 → 2026-07-07 · Quest 3, Unity 6000.3.10f1.

This documents **Path A** — a fully working live pipeline where the headset scans the
room, the mesh is segmented into per-object clusters, and those clusters drive the
object-shaped portals — and its **limitations**, which led to shipping **Path B** as the
reliable default (bundled clean clusters drive portals; the live scan is captured,
stored, and segmented as a demonstrated capability).

---

## 1. The problem this addresses (from the GitHub issue)

The original portal system masked a **user-drawn bounding box**. Removing a table hid
the whole box volume — including the empty space between the legs that should stay
visible. The issue asks for the portal mask to follow the **actual object geometry**, so
gaps stay transparent, and to let the user pick an object from a **scene mesh** (coarse
HMD mesh or fine external-camera mesh) and compare the two sources.

**Status against the acceptance criteria:**

| Criterion | Result |
|---|---|
| User selects a real object from a scene mesh | ✅ `ObjectPicker` raycasts a per-cluster `MeshCollider` (green/red laser) |
| Portal follows object geometry, not a box | ✅ mask = the cluster mesh via `Custom/StencilMask`, not a box |
| Gaps (between legs) stay visible | ✅ stencil is written only on the cluster surface; gaps aren't masked |
| Works with ≥1 mesh source | ✅ **both**: external-camera mesh (bundled) AND live HMD/MRUK mesh |
| Compare coarse HMD vs fine external mesh | ✅ documented in §6 |
| More precise than bounding box | ✅ object silhouette vs box volume |

The geometry-based mask requirement was **already met** by the existing cluster pipeline.
The new work made the cluster source **dynamic** (the user's own live scan) and produced
the HMD-vs-external comparison.

---

## 2. Architecture (Path A — the live pipeline)

```
Quest "Scan Room" button
  │  ScanRoomFlow.OnScanRoomPressed()
  ▼
1. MRUK Space Setup / LoadSceneFromDevice     (OVRScene.RequestSpaceSetup / MRUK)
      → the headset's GLOBAL_MESH room mesh
  ▼
2. GlobalMeshProvider.TryCaptureGlobalMesh()  (grabs the MRUK global MeshFilter)
      → Mesh (≈50 636 verts) + world Transform, also written to mruk_global_mesh.obj
  ▼
3. MeshSegmentationClient.Segment()           (POST OBJ → laptop Flask server)
      → tools/segmentation_server/app.py runs export_clusters_normals.py
      → clusters.json + clusterN.obj served back
  ▼
4. ObjectPicker.ReloadClustersFromUrl()       (download clusters, parent under MRUK mesh)
      → per-cluster MeshCollider on the ClusterPick layer
  ▼
5. Add/Remove Portal (unchanged)              (StencilMask on the picked cluster)
```

### Components added
| File | Role |
|---|---|
| `Assets/Scripts/Mesh/GlobalMeshProvider.cs` | **Mesh-source seam.** Captures the MRUK GLOBAL_MESH; exposes `RoomMesh`, `RoomMeshTransform`, `RoomMeshReady`. A future COLMAP/SfM source can implement the same shape. |
| `Assets/Scripts/Mesh/MeshSegmentationClient.cs` | Serializes the mesh to OBJ (no X-flip) and POSTs to the segmentation server; returns `{count, indices, base_url}`. |
| `Assets/Scripts/Mesh/ScanRoomFlow.cs` | Orchestrates the button flow; `drivePortalsFromScan` selects Path A vs Path B. |
| `tools/segmentation_server/app.py` + `README.md` | Off-device Flask server wrapping the existing `export_clusters_normals.py`; works in a temp copy so it never clobbers the bundled fallback. |

### Components modified (minimally, additively)
- `ObjectPicker.cs` — added `clusterBaseUrl` + `ReloadClustersFromUrl()` so clusters can
  come from a server URL and be parented under the MRUK mesh; a per-reload cache
  generation so downloads don't collide with the bundled load; a `PICKCHECK` coordinate
  log.
- `MeshDownloadManager.cs` — one optional `overrideFlipX` parameter (default preserves
  existing behaviour) so MRUK-sourced clusters skip the X-flip.

Nothing in the portal render path (StencilMask / PortalContentUnlit / SelectivePassthrough
/ GameManager portal logic / MRUK) was changed.

---

## 3. Key technical decisions

**Why a server (not on-device segmentation).** Segmentation uses Open3D + NumPy (Python),
which cannot run on the Quest. The mesh is sent to a laptop on the same Wi-Fi. This is the
`/segment` endpoint. On-device C# RANSAC/DBSCAN was considered but rejected (CPU-heavy on
Quest; MRUK's own `DestructibleGlobalMesh` only chunks geometrically, not per-object).

**Coordinate convention (the #1 risk).** The OBJ loader X-flips imports (for the external
mesh). The MRUK mesh is already in Unity space, so MRUK clusters are exported **without a
flip** and loaded with the flip **bypassed** — an identity round-trip. Verified: (a) all
segmented clusters land inside the source mesh's bounding box (no mirror), and (b) on-device
`PICKCHECK` showed hit→cluster-centroid distances of **0.16–0.63 m** (small = correct pick,
not mirrored).

**The GLOBAL_MESH selection.** MRUK produces several MeshFilters; the room mesh is chosen by
requiring ≥1000 triangles and preferring the `*_EffectMesh` name, which rejected a decoy
"205 verts / 0 tris" plane-anchor mesh seen on-device.

---

## 4. What works (verified on device)

From `adb logcat`:
```
[SCANFLOW] Scan pressed (mode=ReloadExisting)
[SCANFLOW] Global mesh: 50636 verts.
[SEGCLIENT] POST http://<laptop>:5000/segment (3.5 MB, 50636 verts)
[SEGCLIENT] Segmented into 23 clusters
[ObjectPicker] Reloading clusters ... under 'GLOBAL_MESH_EffectMesh'
[ObjectPicker] Preload complete: 23 clusters.
[ObjectPicker] AddPortal cluster 4. active=1
[ObjectPicker] PICKCHECK ... hit->centroid=0.29m
```
- ✅ Headset scans → 50 636-vert / 101 912-tri room mesh, confirmed visually (looks like the room).
- ✅ Mesh POSTed and segmented **live** into per-object clusters.
- ✅ Clusters downloaded, parented under the MRUK mesh, picked by collider.
- ✅ Add/Remove Portal works on the live clusters; coordinates correct (no mirror).

**The full live loop runs end-to-end.** That is the core achievement.

---

## 5. Limitations (the important part for the report)

### 5.1 Coarse HMD mesh → unreliable clusters
The Quest MRUK global mesh is a **low-resolution, bumpy** reconstruction. RANSAC plane
removal (which strips floor/walls/ceiling before object clustering) assumes fairly flat
planes. On the bumpy HMD mesh:

- **Loose settings** (strip few planes, wide DBSCAN): walls/floor are **not fully removed**
  → several clusters are large wall/floor slabs (measured 4.55 × 2.77 × 2.77 m — full room
  height) rather than objects.
- **Aggressive settings** (strip 12 planes @ 6 cm, tighter DBSCAN): walls are removed, but
  **too many real objects are also dropped** — one scan collapsed to only 4 tiny clusters,
  most of the room unhittable.

This is a **see-saw with no clean middle** on the HMD mesh: strict enough to remove walls
= drops objects; loose enough to keep objects = keeps wall blobs. Measured cluster counts on
the same room across settings:

| Settings | Result |
|---|---|
| Original (6 planes @3cm, eps 0.15) | ~33 clusters incl. ~6 wall/floor slabs |
| Aggressive (12 planes @6cm, eps 0.13) | 10 clusters, object-sized, walls gone — but many objects dropped |
| Aggressive on a smaller scan | 4 clusters — over-stripped, room mostly empty |

### 5.2 Scan-to-scan variability
Two "Scan Room" presses in the same room produced **different** meshes (50 636 verts vs a
smaller partial scan) depending on head coverage and Space Setup state, so the same segmenter
settings gave different cluster counts (23 vs 4). The live pipeline's output is only as
stable as the underlying MRUK scan.

### 5.3 No colour/texture on the HMD mesh
The MRUK mesh is untextured gray geometry. It gives correct object **shapes** (good for the
mask) but cannot provide textured **content** to show inside a portal — unlike the external
camera mesh, which is photo-textured. For diminished reality the content is the existing
Alternate Scene (background behind the object), not a texture from the HMD mesh.

### 5.4 System-UI dependence & minor UX
- `OVRScene.RequestSpaceSetup()` hands off to Meta's system Space Setup UI (cannot be
  restyled); the app pauses and resumes.
- The head-follow ToolMenu panel can slip out of view during scanning.
- The "Scan Room" button label still shows a leftover frame counter from the removed
  depth-capture experiment (cosmetic; the button itself works).

### 5.5 Thin / concave / incomplete structures
Thin legs and concave shapes are exactly where the bumpy HMD mesh + RANSAC + DBSCAN are
weakest: thin structures fall below the point/plane thresholds and get dropped or merged;
incomplete scans leave holes that split one object into several clusters. The
normals-augmented DBSCAN helps split touching objects at creases but cannot recover geometry
the scan never captured.

---

## 6. HMD mesh vs external-camera mesh (the issue's comparison deliverable)

| Aspect | Coarse HMD (MRUK) mesh | Fine external-camera mesh (`mesh-3hz-4.obj`) |
|---|---|---|
| Source | On-device, live, no external rig | Offline, external camera + photogrammetry (Meshlab) |
| Resolution | ~50 k verts, bumpy | ~271 k verts, clean |
| Texture/colour | None (gray geometry) | Photo-textured (`.jpg`) |
| RANSAC plane removal | Unreliable (bumpy walls) | Clean (flat walls stripped well) |
| Cluster quality | See-saw: wall blobs or over-stripped | ~33 stable object clusters |
| Availability | Any room, instantly | Only the one pre-scanned room |
| Alignment to real room | Native (already in headset space) | Needs the server scene-mesh alignment |
| Best use | Dynamic "scan any room" demo; shapes only | Reliable object-shaped portals + textured content |

**Conclusion:** the external mesh gives materially better cluster quality and the only
usable textured content, but is static (one pre-scanned room). The HMD mesh is fully dynamic
and works in any room, but its coarse geometry makes automatic per-object segmentation
unreliable without per-scan tuning. **For a robust demo, the external-mesh clusters drive the
portals (Path B); the HMD scan is a demonstrated dynamic capability whose quality is
gated by the sensor mesh.**

---

## 7. Path B — what shipped as the default

`ScanRoomFlow.drivePortalsFromScan = false` (default):
- **Portals use the bundled clean external-mesh clusters** (reliable, as before).
- **Scan Room still captures + stores** the HMD room mesh (`persistentDataPath/mruk_global_mesh.obj`)
  and segments it (logged) as a demonstration of the live pipeline — without disrupting the
  reliable portal source.
- Setting `drivePortalsFromScan = true` switches to the full live Path A.

The segmenter tunables were reverted to their clean defaults (tuned for the external mesh).

---

## 8. Future work to make Path A reliable
1. **Mesh cleanup before segmentation** — smoothing / plane-snapping the HMD mesh so RANSAC
   strips walls cleanly without over-aggressive settings.
2. **Use MRUK's labelled planes directly** — MRUK already classifies FLOOR/CEILING/WALL
   anchors; subtract those *labelled* surfaces instead of RANSAC-guessing them, then DBSCAN
   only the remainder.
3. **Adaptive segmentation parameters** — pick `PLANE_DIST`/`eps` from the mesh's measured
   roughness instead of fixed constants.
4. **RGB capture + textured HMD mesh** — capture the passthrough camera during the scan to
   texture the mesh (removes limitation 5.3), enabling textured content and possibly
   image-assisted segmentation.
5. **Learned 3D instance segmentation** (Mask3D / SoftGroup / Point Transformer v3) on the
   point cloud instead of RANSAC+DBSCAN, for robustness to bumpy geometry and thin structures.

---

## 9. How to run / reproduce

**Path B (default, reliable):** just Build & Run. Portals use bundled clusters. Pressing
Scan Room stores + segments the HMD mesh (see logcat `[SCANFLOW] PATH B: ...`).

**Path A (live pipeline):**
1. Laptop: `pip install flask`; `py -3.11 tools/segmentation_server/app.py`.
2. Set `MeshSegmentationClient.segmentServerUrl` = `http://<laptop-ip>:5000`; same Wi-Fi.
3. Set `ScanRoomFlow.drivePortalsFromScan = true`.
4. Build & Run; press Scan Room; watch `[SCANFLOW]/[SEGCLIENT]/[ObjectPicker]` in logcat;
   then Add/Remove Portal on the scanned objects.

See `IMPLEMENTATION_NOTES.md` for the coordinate convention, the mesh-source seam, and the
per-phase commit history on branch `mruk-dynamic-portals`.
