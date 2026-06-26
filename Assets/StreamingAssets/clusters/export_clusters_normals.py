"""
Normals-augmented DBSCAN object segmentation (pure Open3D + numpy, no extra installs).

WHY THIS EXISTS
---------------
The shipping export_clusters.py clusters with plain DBSCAN on POSITION only. Two
objects that physically touch (cup on table, chair leg on floor, boxes side by
side) have continuous geometry across the contact, so a position-only DBSCAN sees
one connected blob and MERGES them into a single cluster. A merge = one selectable
object = one portal, and it is unrecoverable in the Unity app. No eps value fixes
it: distance is the wrong signal.

This script keeps everything else the same but augments the DBSCAN feature with
the SURFACE NORMAL. Where two objects touch there is a normal DISCONTINUITY (a
crease where the normal direction jumps). By clustering on the 6-D feature

    [ x, y, z, w*nx, w*ny, w*nz ]

points across that crease become far apart in feature space even though they are
close in 3D, so the cluster boundary lands on the seam and the objects split.

Unlike the LCCP ports (export_clusters_lccp.py / lccp_open3d.py), there is NO
per-edge convex/concave decision and NO adjacency graph to mis-build: a few noisy
normals are outvoted by density rather than fusing two whole objects. The failure
mode here is OVER-split (recoverable via multi-pick), never merge (unrecoverable).

The RANSAC plane pre-strip and the mesh-crop -> QEM simplify -> clusterN.obj ->
centres.txt/clusters.json BACKEND are kept byte-for-byte identical to
export_clusters.py, so the Unity runtime sees the exact same output contract.
Only the *labelling* changes.

DBSCAN is implemented here directly on an Open3D KD-tree (same algorithm sklearn
uses internally) to avoid adding scikit-learn as a dependency. open3d + numpy are
already installed.

Run:  py -3.11 export_clusters_normals.py
(open3d supports Python 3.8-3.11 only, same as the original.)
"""

import open3d as o3d
import numpy as np
import os
import json
import glob

# ---------------------------------------------------------------------------
# Tunables.
# ---------------------------------------------------------------------------
NUM_POINTS        = 100000  # points sampled from the mesh (matches original)
NUM_PLANES        = 6       # dominant planes RANSAC strips (floor/walls/ceiling)
PLANE_DIST        = 0.03    # RANSAC plane inlier distance (m), same as original

EPS               = 0.15    # DBSCAN neighbourhood radius in the COMBINED feature
                            # space. Same scale as the original position eps.
MIN_POINTS        = 20      # DBSCAN core-point threshold (same as original)

NORMAL_WEIGHT     = 0.25    # THE NEW IMPORTANT KNOB (`w` above). Units: metres-
                            # equivalent applied to the unit normal. Higher =
                            # normals matter more = splits HARDER at creases
                            # (more over-split, the recoverable direction). Lower
                            # toward 0 = behaves like the original position-only
                            # DBSCAN (more merges). Try 0.1 / 0.2 / 0.3.

KNN_NORMALS       = 30      # neighbours for normal estimation on the cloud
MIN_CLUSTER_POINTS = 200    # drop specks (same threshold the original used)
BBOX_PAD          = 1.2     # crop padding (same as original; lower to 1.0 if box
                            # padding fills gaps between e.g. chair legs)


def dbscan_features(features, eps, min_points):
    """
    DBSCAN on an arbitrary N-D feature array, using an Open3D KD-tree for the
    radius neighbour queries. Returns a per-row label array; -1 means noise.

    This is the standard DBSCAN algorithm (Ester et al.): expand a cluster from
    every unvisited core point by radius-searching its neighbours. It matches
    sklearn's DBSCAN(eps, min_samples=min_points) semantics but needs no sklearn.

    Open3D's KDTreeFlann indexes 3-D points only, so the tree is built on the
    POSITION part as a cheap candidate prefilter (any point within eps in the
    full feature space is necessarily within eps in position, since position is
    a sub-vector). Candidates are then refined against the full 6-D distance in
    numpy. This keeps it exact without an N^2 scan.
    """
    n = len(features)
    labels = np.full(n, -2, dtype=np.int64)  # -2 = unvisited, -1 = noise
    eps2 = eps * eps
    cluster = 0

    pos = np.ascontiguousarray(features[:, :3])
    pos_cloud = o3d.geometry.PointCloud()
    pos_cloud.points = o3d.utility.Vector3dVector(pos)
    kdt = o3d.geometry.KDTreeFlann(pos_cloud)

    def region_query(i):
        # position-prefilter candidates within eps, then refine in full feature space
        _, cand, _ = kdt.search_radius_vector_3d(pos[i], eps)
        if len(cand) == 0:
            return np.empty(0, dtype=np.int64)
        cand = np.asarray(cand, dtype=np.int64)
        diff = features[cand] - features[i]
        within = np.einsum("ij,ij->i", diff, diff) <= eps2
        return cand[within]

    from collections import deque
    for i in range(n):
        if labels[i] != -2:
            continue
        neigh = region_query(i)
        if len(neigh) < min_points:
            labels[i] = -1  # noise (may later be claimed as a border point)
            continue
        labels[i] = cluster
        queue = deque(x for x in neigh if x != i)
        while queue:
            j = queue.popleft()
            if labels[j] == -1:
                labels[j] = cluster  # border point of this cluster
            if labels[j] != -2:
                continue
            labels[j] = cluster
            jneigh = region_query(j)
            if len(jneigh) >= min_points:
                queue.extend(jneigh)
        cluster += 1

    return labels


# ===========================================================================
# SEGMENTATION
# ===========================================================================
mesh = o3d.io.read_triangle_mesh("mesh-3hz-4.obj")
mesh.compute_vertex_normals()
pcd = mesh.sample_points_uniformly(number_of_points=NUM_POINTS)

# Per-point normals on the sampled cloud (sampling carries mesh normals, but we
# re-estimate + orient so the normal feature is locally consistent).
pcd.estimate_normals(search_param=o3d.geometry.KDTreeSearchParamKNN(knn=KNN_NORMALS))
pcd.orient_normals_consistent_tangent_plane(KNN_NORMALS)

# RANSAC: strip dominant planes (floor, walls, ceiling) -- identical to original.
remaining = pcd
for _ in range(NUM_PLANES):
    plane, inliers = remaining.segment_plane(
        distance_threshold=PLANE_DIST, ransac_n=3, num_iterations=1000)
    remaining = remaining.select_by_index(inliers, invert=True)

# Build the 6-D feature: position + weighted normal. The weight controls how
# strongly a normal discontinuity (object seam) splits the cluster.
pts = np.asarray(remaining.points)
nrm = np.asarray(remaining.normals)
features = np.hstack([pts, NORMAL_WEIGHT * nrm])

labels = dbscan_features(features, eps=EPS, min_points=MIN_POINTS)
max_label = int(labels.max()) if labels.size else -1
print(f"Found {max_label + 1} clusters "
      f"(eps={EPS}, normal_weight={NORMAL_WEIGHT}, min_points={MIN_POINTS})")

# ===========================================================================
# BACKEND  -  identical to export_clusters.py from here down
# ===========================================================================
# Remove any stale cluster files from a previous run so the output is clean
# and contiguous (cluster0, cluster1, ...). This prevents leftover high-index
# files from a previous mesh confusing the Unity loader.
for old in glob.glob("cluster*.obj"):
    os.remove(old)

# `saved` is the running, CONTIGUOUS index used for filenames. The DBSCAN label
# `i` is sparse (gaps), so we do NOT use it for naming.
centres = []
saved = 0
for i in range(max_label + 1):
    idx = np.where(labels == i)[0]
    if len(idx) < MIN_CLUSTER_POINTS:
        continue
    cluster_pcd = remaining.select_by_index(idx)

    # Crop the mesh to this cluster, simplify, and skip if it produced no geometry.
    bbox = cluster_pcd.get_axis_aligned_bounding_box()
    bbox.scale(BBOX_PAD, bbox.get_center())
    cropped = mesh.crop(bbox)
    cropped = cropped.simplify_quadric_decimation(3000)
    if len(cropped.triangles) == 0:
        continue

    # Only record the centre AFTER we know this cluster will actually be saved,
    # and name the file with the contiguous index `saved`.
    centre = np.asarray(cluster_pcd.points).mean(axis=0)
    o3d.io.write_triangle_mesh(f"cluster{saved}.obj", cropped)
    centres.append((saved, centre))
    print(f"Saved cluster{saved}.obj (dbscan label {i}), centre={centre}")
    saved += 1

# centres.txt: still written for debugging/visualization (kept for reference).
with open("centres.txt", "w") as f:
    for idx, c in centres:
        f.write(f"{idx},{c[0]},{c[1]},{c[2]}\n")

# clusters.json: the MANIFEST the Unity loader reads. Lists exactly which cluster
# indices exist, so Unity never has to guess a count.
manifest = {"count": saved, "indices": [idx for idx, _ in centres]}
with open("clusters.json", "w") as f:
    json.dump(manifest, f)

print(f"Done. {saved} clusters saved (contiguous 0..{saved - 1}), "
      f"manifest written to clusters.json.")
