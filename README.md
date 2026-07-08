# MOZART Feasibility Analysis Tool

This application is part of the [MOZART project](https://mozart-robotics.eu/).

It serves as a Feasibility Analysis Tool designed to support the presentation and evaluation of innovative food packaging concepts and the handling of fragile and fresh food items in realistic production environments.

The application runs on **Meta Quest 3** and uses a **Diminish Reality** approach to combine the physical environment with virtual industrial content. This makes it possible to demonstrate and assess how future robotic and mixed reality workflows could be applied in real manufacturing settings.

## Focus

- Presentation of innovative food packaging concepts
- Exploration of handling scenarios for fragile and fresh food items
- Feasibility analysis in a real production context
- Mixed reality visualization on Meta Quest 3 using the Diminish Reality concept

## Object-Shaped Portals — generating the object clusters

Object-shaped portals mask a portal in the shape of a real object's geometry (instead of a
bounding box). The per-object shapes ("clusters") are produced **offline** by segmenting a
room mesh, and `ObjectPicker` loads them at runtime.

The generated cluster files are **not committed** — they can be recreated on demand from the
source mesh (and from any newer mesh once available). Only the segmentation script and the
source mesh are in the repository.

### What lives in `Assets/StreamingAssets/clusters/`

| Kept in the repo | Generated (not committed — recreate with the script) |
|---|---|
| `export_clusters_normals.py` — the segmenter (RANSAC plane removal + normals-augmented DBSCAN) | `clusterN.obj` — one mesh per detected object |
| `export_clusters.py` — original plain RANSAC+DBSCAN variant | `clusters.json` — the manifest `ObjectPicker` reads (`{count, indices[]}`) |
| `visualize_clusters.py` — preview clusters in distinct colours | `centres.txt` — per-cluster centres (debug only) |
| `mesh-3hz-4.{obj,mtl,jpg}` — the source room mesh to segment | |

### How to generate the clusters

Requires **Python 3.11** with **Open3D** and **NumPy** (Open3D supports Python 3.8–3.11).

```bash
# one-time: install the dependencies
py -3.11 -m pip install open3d numpy

# segment the mesh -> writes clusterN.obj + clusters.json (+ centres.txt) in this folder
cd Assets/StreamingAssets/clusters
py -3.11 export_clusters_normals.py
```

The script reads `mesh-3hz-4.obj`, removes the dominant planes (floor / walls / ceiling) with
RANSAC, clusters the remaining points with DBSCAN on a position+normal feature (so objects
that touch are split at the normal discontinuity), crops + simplifies each cluster's mesh, and
writes the outputs **in place** — exactly where `ObjectPicker` loads them from. After running
it, Build & Run in Unity and the portals use the freshly generated clusters. No files need to
be moved.

**Segmenting a different / newer mesh:** replace `mesh-3hz-4.obj` with the new mesh (same
filename), or edit the input filename near the top of `export_clusters_normals.py`, then re-run.
The room mesh itself is normally downloaded at runtime from the external mesh server via
`MeshDownloadManager`; a copy (`mesh-3hz-4.obj`) is kept here so the segmentation can be run
offline.

`visualize_clusters.py` can be run afterwards to preview the generated clusters in distinct
colours for a sanity check.

## Room Scanning (RGB dataset capture)

The **Scan Room** button records an RGB image dataset on the headset for later offline 3D
reconstruction (e.g. COLMAP / OpenMVS). Press to start, press again to stop; a live counter
shows the number of captured photos.

Each session is written to `Application.persistentDataPath/dataset_<sessionId>/` on the
device (`/sdcard/Android/data/cz.fitvut.fat/files/dataset_<sessionId>/`):

- `frames/left_000001.jpg …` — RGB photos
- `poses/frames.csv` — per-frame camera position, rotation, and timestamp
- `calibration/left_camera.json` — camera intrinsics (fx, fy, cx, cy) at the frame resolution,
  plus the raw sensor values and lens offset
- `manifest.json` — session id and frame count

Copy a dataset to a PC with `adb`:

```bash
adb shell ls /sdcard/Android/data/cz.fitvut.fat/files/       # list sessions
adb pull /sdcard/Android/data/cz.fitvut.fat/files/dataset_<sessionId> .
```

The reconstruction of a mesh from these images is future work; the mesh would then feed the
same segmentation pipeline as the object-shaped portals above.

## Dependencies not committed (download separately)

Following the project convention, third-party Asset Store libraries are not committed —
install them from the Unity Asset Store before building:

- **TriLib** and **SimpleCollada** (both required by `MeshImporter.cs`).
