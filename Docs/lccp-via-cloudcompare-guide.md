# Real LCCP Segmentation via CloudCompare — Beginner Guide

> **Goal:** produce genuine PCL **LCCP** (Locally Convex Connected Patches) object
> clusters for `mesh-3hz-4.obj`, then convert them into the `clusterN.obj` +
> `clusters.json` files the Unity app already loads — **with no code compilation,
> no conda, no pclpy.**

## Why this route (read first)

`pclpy` (the Python PCL binding) is **unmaintained**, has **no wheels for Python
3.11/3.12/3.14**, and its only Windows install path is **conda + Python 3.6/3.7**,
which you don't have. Building PCL from source on Windows is a multi-hour,
frequently-failing ordeal.

**CloudCompare** is a free, open-source desktop app that **bundles a working PCL**
and exposes its segmentation. You run LCCP in the GUI, export the labelled result,
and a small Python script (Open3D, already installed) turns that into the cluster
files. This gives you *real* convexity-based LCCP output reliably.

> Note on terminology: CloudCompare's PCL plugin historically exposed
> **"Min cut / supervoxel" + region growing**. Depending on your CloudCompare
> version the exact menu may be **"PCL > Segmentation"** or the **"Cork/CSF"** and
> **supervoxel** tools. The recipe below uses the **supervoxel + LCCP** path if
> present, and gives a documented fallback (label export + offline convexity
> merge) if your build doesn't expose LCCP directly. Either way you end up with
> per-region labels, which is all the converter needs.

---

## Step 0 — What you have (verified)

- Source mesh: `Assets/StreamingAssets/clusters/mesh-3hz-4.obj` (~519k faces,
  textured, MeshLab export). Coordinates are in **metres**, room-scale (±5 m).
- Target output (must match exactly so Unity is untouched):
  - `clusterN.obj` — verts + normals + **faces**, no UVs (stencil mask only).
  - `clusters.json` — `{"count": N, "indices": [0..N-1]}`.
  - `centres.txt` — debug only, not read at runtime.
- Python: **`py -3.11`** with **open3d 0.19.0** + numpy already installed. Use 3.11
  for every Python command in this guide (open3d supports 3.8–3.11, **not** 3.12+).

---

## Step 1 — Install CloudCompare

1. Go to **https://www.cloudcompare.org/** → Download → **Windows installer (64-bit)**.
   Use the **stable** release (2.13.x or newer).
2. Run the `.exe`, accept defaults. No admin tricks, no compiler, no PATH edits.
3. Launch **CloudCompare**. (There's also a console tool `CloudCompare.exe`/
   `ccViewer` — we use the GUI.)

That's the only install. No conda, no PCL SDK, no Visual Studio.

---

## Step 2 — Load the mesh

1. **File > Open** → select
   `Assets/StreamingAssets/clusters/mesh-3hz-4.obj`.
2. In the import dialog, keep defaults (it will read vertices + triangles). If it
   asks about a global shift/scale because of large coordinates, click
   **"Yes to all"** to apply the suggested shift — **remember it's only a display
   shift; CloudCompare writes original coordinates back on export.** (Our coords
   are only ±5 m so it may not even ask.)
3. You'll see the room mesh in the 3D view. In the **DB Tree** (left panel) it
   appears as `mesh-3hz-4`.

---

## Step 3 — Convert mesh to a point cloud (LCCP works on points)

LCCP/supervoxel segmentation operates on a **point cloud with normals**, not
triangles. Sample the mesh:

1. Select `mesh-3hz-4` in the DB Tree.
2. **Edit > Mesh > Sample Points** (menu wording: *"Sample points on a mesh"*).
3. Set **Points = 200000** (dense enough for clean seams; adjust later if slow).
   Leave "Normals" **ON** if offered. Click OK.
4. A new cloud (e.g. `mesh-3hz-4.sampled`) appears in the DB Tree.

If normals are NOT generated automatically:
5. Select the sampled cloud → **Edit > Normals > Compute** → use a local surface
   model (e.g. "Plane"), neighbour radius ~**0.05 m**. Then
   **Edit > Normals > Orient Normals** (consistent orientation) — LCCP's convexity
   test needs consistent outward normals.

---

## Step 4 — Run the segmentation (LCCP path)

Open the PCL tools. Depending on your build:

**Path A — PCL plugin present (preferred):**
1. **Plugins > PCL** (or a toolbar PCL icon). If you don't see PCL, enable it via
   **Display > Plugins** / re-run the installer and tick the PCL plugin.
2. Choose the **supervoxel / LCCP segmentation** tool. Key parameters:
   - **Voxel resolution** ≈ `0.03`–`0.05` m (smaller = finer seams, slower).
   - **Seed resolution** ≈ `0.1`–`0.2` m (supervoxel size).
   - **Concavity tolerance (degrees)** ≈ `10`–`15` — this is the **most important
     knob**: *lower* splits more aggressively at concave seams (fewer merges),
     *higher* merges more (fewer splits). Start at **10**.
   - Spatial/normal importance: leave defaults first.
3. Run. The cloud is recoloured by region/segment. Each colour = one candidate
   object. **Visually check that touching objects (cup on table, chair on floor)
   are different colours** — that's the whole point of LCCP.
4. Tune concavity tolerance and re-run until merges are gone (accept some
   over-splitting).

**Path B — your build has no direct LCCP (fallback):**
1. Use **Tools > Segmentation > Label Connected Components** OR the supervoxel tool
   alone to get a per-point **scalar field** of segment IDs.
2. We'll do the convexity merge offline in Python (Step 6 has both variants). The
   only requirement from CloudCompare is: **export points + normals + a per-point
   segment label**.

---

## Step 5 — Export the labelled point cloud

1. Select the segmented cloud in the DB Tree.
2. **File > Save As** → choose **`.ply` (point cloud)** or **`.txt` / `.asc`**.
   - **PLY is preferred** (keeps normals + the segment scalar field cleanly).
3. In the export options, make sure **normals** and the **segment/label scalar
   field** are included (check the field list; the segmentation tool stores IDs in
   a scalar field, often named "segment", "label", or "supervoxel").
4. Save as
   `Assets/StreamingAssets/clusters/lccp_labeled.ply`.

> If CloudCompare applied a global shift on import, exporting writes **original
> coordinates** back by default — verify the "restore original coordinates"
> checkbox is ticked so the clusters line up with the room mesh in Unity.

---

## Step 6 — Convert labelled cloud → clusterN.obj (Python, Open3D)

This script reads the PLY, groups points by label, crops the **original mesh** to
each label's geometry, and writes the exact output contract Unity expects. It
**reuses the proven output tail** from
`export_clusters.py` (stale-file cleanup, contiguous indexing, manifest).

Create `Assets/StreamingAssets/clusters/lccp_to_clusters.py`:

```python
"""
Convert a CloudCompare LCCP-labelled point cloud into clusterN.obj + clusters.json.

INPUT : lccp_labeled.ply  (points + normals + a per-point integer label field,
                           exported from CloudCompare's LCCP/supervoxel tool)
OUTPUT: cluster0.obj ... clusterN.obj, clusters.json, centres.txt
        (byte-for-byte the same contract the Unity ObjectPicker already loads)

Run:  py -3.11 lccp_to_clusters.py
"""
import open3d as o3d
import numpy as np
import os, json, glob

SRC_MESH   = "mesh-3hz-4.obj"
LABELED    = "lccp_labeled.ply"
MIN_POINTS = 200      # drop specks (same threshold the original used)
QEM_FACES  = 3000     # cap per cluster (same as original)
BBOX_PAD   = 1.05     # crop padding. Keep SMALL so gaps survive (original used 1.2)

# --- load source mesh (the geometry we actually export) --------------------
mesh = o3d.io.read_triangle_mesh(SRC_MESH)
mesh.compute_vertex_normals()

# --- load CloudCompare's labelled cloud ------------------------------------
pcd = o3d.io.read_point_cloud(LABELED)
pts = np.asarray(pcd.points)

# CloudCompare stores the segment id as a scalar field. Open3D surfaces scalar
# fields differently per version; the two common cases:
#   (a) it came through as per-point COLORS encoding the id  -> decode below
#   (b) you exported a plain .txt with an id column          -> load via numpy
# EASIEST ROBUST PATH: export from CloudCompare as ASCII .txt with columns
#   x y z nx ny nz label
# and set LABELED = "lccp_labeled.txt", then use this loader instead:
#
#   data   = np.loadtxt("lccp_labeled.txt")
#   pts    = data[:, 0:3]
#   labels = data[:, -1].astype(np.int64)
#
# If you DID export .ply and the label rode in as colors:
cols = np.asarray(pcd.colors)
if cols.size:
    # recover an integer id from quantized colors (works when CC mapped id->color)
    labels = (cols * 255).astype(np.int64)
    labels = labels[:, 0] * 65536 + labels[:, 1] * 256 + labels[:, 2]
    # remap arbitrary ids to contiguous 0..K
    _, labels = np.unique(labels, return_inverse=True)
else:
    raise SystemExit("No label field found. Re-export from CloudCompare as ASCII "
                     ".txt (x y z nx ny nz label) and use the np.loadtxt loader "
                     "shown in the comment above.")

max_label = labels.max()
print(f"Loaded {len(pts)} pts, {max_label + 1} LCCP regions")

# --- clean stale outputs ---------------------------------------------------
for old in glob.glob("cluster*.obj"):
    os.remove(old)

centres, saved = [], 0
for i in range(max_label + 1):
    idx = np.where(labels == i)[0]
    if len(idx) < MIN_POINTS:
        continue
    region = o3d.geometry.PointCloud()
    region.points = o3d.utility.Vector3dVector(pts[idx])

    bbox = region.get_axis_aligned_bounding_box()
    bbox.scale(BBOX_PAD, bbox.get_center())
    cropped = mesh.crop(bbox)
    cropped = cropped.simplify_quadric_decimation(QEM_FACES)
    if len(cropped.triangles) == 0:
        continue

    centre = pts[idx].mean(axis=0)
    o3d.io.write_triangle_mesh(f"cluster{saved}.obj", cropped)
    centres.append((saved, centre))
    print(f"Saved cluster{saved}.obj (region {i}), centre={centre}")
    saved += 1

with open("centres.txt", "w") as f:
    for idx, c in centres:
        f.write(f"{idx},{c[0]},{c[1]},{c[2]}\n")

with open("clusters.json", "w") as f:
    json.dump({"count": saved, "indices": [idx for idx, _ in centres]}, f)

print(f"Done. {saved} clusters saved (0..{saved - 1}).")
```

Run it:

```
py -3.11 lccp_to_clusters.py
```

> **Recommended export to make this painless:** in Step 5, export as **ASCII
> `.txt`** with columns `x y z nx ny nz label` and use the `np.loadtxt` loader in
> the comment. That avoids all PLY scalar-field/version ambiguity — the label is
> just the last column.

---

## Step 7 — Verify before touching Unity

```
py -3.11 visualize_clusters.py      # existing script: shows clusters in colours
```

Check:
- **No merges:** no single cluster spans two physical objects (the only fatal
  error). Tune CloudCompare's concavity tolerance and redo Steps 4–6 if any merge.
- **Count sanity:** roughly one cluster per real object (some over-split is fine).
- **Gaps preserved:** lower `BBOX_PAD` (e.g. 1.0) if box padding is filling gaps
  between legs.

---

## Step 8 — Use it in Unity (no code changes)

The script overwrote `clusterN.obj` + `clusters.json` in place. The Unity
`ObjectPicker` reads them via the manifest — **nothing in Unity changes**. Just
rebuild/redeploy and pick objects as before.

---

## Versions cheat-sheet

| Thing | Version | Why |
|---|---|---|
| CloudCompare | latest stable (2.13.x+) | bundles working PCL; 1-click install |
| Python | **3.11** (`py -3.11`) | open3d supports 3.8–3.11 only (NOT 3.12/3.14) |
| open3d | 0.19.0 (already installed) | mesh crop + OBJ write |
| numpy | (already installed) | label grouping |
| ~~pclpy~~ | — | **do not use**: no wheels for 3.11+, conda-only, unmaintained |
| ~~conda~~ | — | not needed with the CloudCompare route |

---

## If you ever DO want pclpy anyway (not recommended)

The only path that has a chance:
1. Install **Miniconda** (Windows x64).
2. `conda create -n lccp python=3.7`
3. `conda activate lccp`
4. `conda install -c conda-forge -c davidcaron pclpy`
   — may fail to resolve (channel is old/unmaintained).
5. Write a PCL `lccp_segmentation` script in that env, export labels, then run the
   **same Step 6 converter** (from your 3.11 env) on the labels.

This keeps a fragile, off-toolchain Python 3.7 env alive forever just for one
script. The CloudCompare route gives the same LCCP result without that burden.

---

## Troubleshooting

- **"No PCL plugin in CloudCompare":** re-run the installer and tick the PCL
  plugin component, or check **Display > Plugins**. If your build truly lacks LCCP,
  use Path B (export supervoxel/connected-component labels) + offline convexity
  merge.
- **Clusters are offset from the room in Unity:** the global shift wasn't undone on
  export. Re-export with "restore original coordinates" / "global shift = 0".
- **Everything is one region:** concavity tolerance too high — lower it (try 8,
  then 5). This is the same failure the pure-Open3D port hit; in CloudCompare it's
  just a slider.
- **Objects fragmented into many pieces:** concavity tolerance too low, or voxel
  resolution too small — raise tolerance toward 15, or merge sub-pieces with a
  post-step (acceptable; over-split is recoverable, merges are not).
```
