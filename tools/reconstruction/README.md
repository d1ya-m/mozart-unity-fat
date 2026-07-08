# Offline Reconstruction — RGB dataset → 3D mesh (COLMAP + OpenMVS)

Turns a `QuestDatasetRecorder` dataset (RGB images + 6D camera poses + intrinsics) into a
dense, textured 3D mesh using **Structure-from-Motion / Multi-View Stereo**. Because the
headset already recorded the camera poses, COLMAP triangulates with **known poses** (no pose
estimation) — faster, more accurate, and metrically scaled.

This is the offline PC step between capturing a room (Scan Room) and segmenting it into
object-shaped-portal clusters.

```
dataset_<sessionId>/  (images + poses + intrinsics)
        │  unity_to_colmap.py     -> COLMAP known-pose model
        │  COLMAP                  -> feature match + triangulate + undistort
        │  OpenMVS                 -> dense point cloud + mesh + texture
        ▼
   textured.obj  ── segment with export_clusters_normals.py ──►  portal clusters
```

## Why COLMAP + OpenMVS (vs the MRUK mesh)

| | MRUK headset mesh | COLMAP + OpenMVS |
|---|---|---|
| Source | Quest depth sensors | The RGB photos you captured |
| Detail | Coarse / bumpy | Dense / sharp |
| Texture | None (gray) | Full photo texture |
| Segmentation quality | Poor (bumpy walls break RANSAC) | Good (clean planes) |
| Uses your 6D poses | n/a | Yes — known-pose triangulation |

COLMAP was chosen specifically because it **supports known 6D camera poses as input**
(via a manually-written `cameras.txt`/`images.txt` model + `point_triangulator`), which is
exactly what the headset provides. OpenMVS then densifies + meshes + textures.

## Install the tools (one-time, PC)

- **COLMAP** — https://colmap.github.io (Windows: download the pre-built binaries, add the
  folder to PATH so `colmap` works in a terminal). A CUDA GPU speeds it up but CPU works.
- **OpenMVS** — https://github.com/cdcseacave/openMVS (download/build the binaries; add them to
  PATH so `InterfaceCOLMAP`, `DensifyPointCloud`, `ReconstructMesh`, `RefineMesh`,
  `TextureMesh` work).
- **Python 3.11 + numpy** — `py -3.11 -m pip install numpy`.

Verify: `colmap -h` and `DensifyPointCloud -h` both print help.

## Run

```powershell
cd tools/reconstruction
py -3.11 run_reconstruction.py <path-to>/dataset_<sessionId> [--out <workdir>] [--skip-refine]
```

- `--skip-refine` skips the slow `RefineMesh` step (faster, slightly lower quality).
- Output: `<workdir>/openmvs/textured.obj` (+ `.mtl` + texture).

Then segment it (from the clusters folder):

```powershell
copy <workdir>\openmvs\textured.obj Assets\StreamingAssets\clusters\mesh-3hz-4.obj
cd Assets\StreamingAssets\clusters
py -3.11 export_clusters_normals.py
```

## Coordinate conversion (what unity_to_colmap.py does)

- Unity `frames.csv` stores each camera's **camera→world** pose (left-handed: +X right, +Y up,
  +Z forward).
- COLMAP `images.txt` needs the **world→camera** pose (right-handed OpenCV: +X right, +Y down,
  +Z forward), Hamilton quaternion `qw qx qy qz` + translation.
- Conversion: build Unity `camToWorld`, flip the camera Y and Z axes
  (`* diag(1,-1,-1)`) to reach the OpenCV camera basis, invert to world→camera, and write
  `qw qx qy qz tx ty tz`.
- Intrinsics come from `calibration/left_camera.json` (PINHOLE `fx fy cx cy` at the frame
  resolution). The image width/height are taken from `frames.csv` (the actual JPG size).

## Notes / TODO
- Capture with good overlap (move slowly; ~60–80% overlap between frames) for a solid
  reconstruction. The 250 ms capture interval gives dense coverage.
- If COLMAP's `sequential_matcher` misses loop closures on a full room loop, switch to
  `exhaustive_matcher` (slower, more robust) in `run_reconstruction.py`.
- The reconstructed mesh is in the headset's world/metric frame (poses were metric), so it is
  already scaled correctly for Unity.
