"""
LCCP-style object segmentation (pure Open3D, no extra installs).

WHY THIS EXISTS
---------------
The original export_clusters.py uses RANSAC plane removal + DBSCAN. DBSCAN
clusters by PROXIMITY, so two objects that TOUCH (cup on table, chair against
wall, leg on floor) share continuous geometry and get merged into one cluster.
No eps/min_points tuning fixes that - distance is the wrong signal.

This script uses the LCCP idea (Locally Convex Connected Patches): when two
objects touch, the junction between them is a CONCAVE crease, while a single
object's interior is locally CONVEX. So we:
  1. Over-segment the point cloud into many small patches (supervoxel-like).
  2. Build an adjacency graph between neighbouring patches.
  3. Merge two adjacent patches ONLY if their shared boundary is CONVEX.
     Merging stops at concave seams -> touching objects fall apart there.

The mesh-crop -> QEM simplify -> clusterN.obj -> centres.txt/clusters.json
BACKEND is kept byte-for-byte identical to export_clusters.py, so the Unity
runtime sees the exact same output contract. Only the *labelling* changes.

Run:  py -3.11 export_clusters_lccp.py
(open3d only supports Python 3.8-3.11, same as the original.)
"""

import open3d as o3d
import numpy as np
import os
import json
import glob

# ---------------------------------------------------------------------------
# Tunables. The single most important knob is CONVEXITY_TOLERANCE (below).
# ---------------------------------------------------------------------------
NUM_POINTS          = 100000   # points sampled from the mesh (matches original)
VOXEL_SIZE          = 0.04     # patch size for over-segmentation (metres). Smaller
                               # = finer patches, more faithful seams, slower.
KNN_NEIGHBOURS      = 12       # neighbours used for patch adjacency / normals
CONVEXITY_TOLERANCE = 0.05     # flat-surface tolerance for the convex test.
                               # RAISE (e.g. 0.1) if single objects get over-split
                               # (merges slightly-concave seams too); LOWER toward
                               # 0 if touching objects still merge (splits more
                               # eagerly). Must stay small and POSITIVE.
MIN_CLUSTER_POINTS  = 200      # same threshold the original used to drop specks


def estimate_patch_normals(pcd):
    """Per-point normals, oriented consistently (needed for the convexity test)."""
    pcd.estimate_normals(
        search_param=o3d.geometry.KDTreeSearchParamKNN(knn=KNN_NEIGHBOURS))
    pcd.orient_normals_consistent_tangent_plane(KNN_NEIGHBOURS)
    return pcd


def is_convex(p_i, n_i, p_j, n_j):
    """
    LCCP convexity criterion between two patch centroids (p) with outward
    normals (n). Returns True if the boundary between the patches is CONVEX
    (same object -> merge), False if CONCAVE (object seam -> keep split).

    Geometric intuition: walk from patch i toward patch j along
    d_hat = normalize(p_j - p_i). On a CONVEX surface (a single object bulging
    outward) the outward normal rotates TOWARD d_hat as you go, so
        dot(n_j, d_hat) > dot(n_i, d_hat)  ->  dot(n_i,d) - dot(n_j,d) < 0.
    On a CONCAVE seam (an inside corner where two objects meet, e.g. floor->wall
    or box->table) the normals close in and that difference is POSITIVE.
    Flat/coplanar patches give exactly 0.

      convex  <=>  dot(n_i, d_hat) - dot(n_j, d_hat) <= tolerance

    CONVEXITY_TOLERANCE is a small positive flat-surface slack: larger merges
    more (fewer splits), smaller splits more aggressively.
    """
    d = p_j - p_i
    dist = np.linalg.norm(d)
    if dist < 1e-9:
        return True
    d_hat = d / dist
    return (np.dot(n_i, d_hat) - np.dot(n_j, d_hat)) <= CONVEXITY_TOLERANCE


class UnionFind:
    """Standard union-find so convex-connected patches collapse into one label."""
    def __init__(self, n):
        self.parent = list(range(n))

    def find(self, x):
        while self.parent[x] != x:
            self.parent[x] = self.parent[self.parent[x]]
            x = self.parent[x]
        return x

    def union(self, a, b):
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.parent[rb] = ra


def lccp_labels(pcd):
    """
    Returns a per-point integer label array (like DBSCAN's output), produced by
    convexity-based region merging instead of density clustering.
    """
    # --- 1. Over-segment into voxel patches -------------------------------
    # Each occupied voxel becomes one patch; we use its centroid + averaged
    # normal as the patch representative for the convexity test.
    pts = np.asarray(pcd.points)
    nrm = np.asarray(pcd.normals)
    keys = np.floor(pts / VOXEL_SIZE).astype(np.int64)

    patch_of_point = {}          # voxel key -> patch index
    patch_points   = []          # patch index -> list of point indices
    for pidx, key in enumerate(map(tuple, keys)):
        if key not in patch_of_point:
            patch_of_point[key] = len(patch_points)
            patch_points.append([])
        patch_points[patch_of_point[key]].append(pidx)

    n_patches = len(patch_points)
    patch_centroid = np.zeros((n_patches, 3))
    patch_normal   = np.zeros((n_patches, 3))
    for pi, idxs in enumerate(patch_points):
        patch_centroid[pi] = pts[idxs].mean(axis=0)
        nsum = nrm[idxs].sum(axis=0)
        nlen = np.linalg.norm(nsum)
        patch_normal[pi] = nsum / nlen if nlen > 1e-9 else np.array([0, 0, 1.0])

    print(f"Over-segmented into {n_patches} patches (voxel={VOXEL_SIZE} m)")

    # --- 2. Patch adjacency (k-NN between patch centroids) -----------------
    patch_cloud = o3d.geometry.PointCloud()
    patch_cloud.points = o3d.utility.Vector3dVector(patch_centroid)
    kdt = o3d.geometry.KDTreeFlann(patch_cloud)

    # Adjacency radius: patches within ~1.8 voxels are considered neighbours.
    adj_radius = VOXEL_SIZE * 1.8

    # --- 3. Merge across convex boundaries only ---------------------------
    uf = UnionFind(n_patches)
    for pi in range(n_patches):
        _, nbrs, _ = kdt.search_radius_vector_3d(patch_centroid[pi], adj_radius)
        for pj in nbrs:
            if pj <= pi:
                continue
            if is_convex(patch_centroid[pi], patch_normal[pi],
                         patch_centroid[pj], patch_normal[pj]):
                uf.union(pi, pj)

    # --- 4. Project patch labels back onto points -------------------------
    # Remap union-find roots to contiguous 0..K-1 labels.
    root_to_label = {}
    labels = np.empty(len(pts), dtype=np.int64)
    for pi, idxs in enumerate(patch_points):
        root = uf.find(pi)
        if root not in root_to_label:
            root_to_label[root] = len(root_to_label)
        labels[idxs] = root_to_label[root]

    print(f"Convexity merging produced {len(root_to_label)} regions")
    return labels


# ===========================================================================
# MAIN  -  labelling above, BACKEND below is identical to export_clusters.py
# ===========================================================================
mesh = o3d.io.read_triangle_mesh("mesh-3hz-4.obj")
mesh.compute_vertex_normals()
pcd = mesh.sample_points_uniformly(number_of_points=NUM_POINTS)
pcd = estimate_patch_normals(pcd)

# NOTE: unlike the RANSAC version we do NOT strip planes first. LCCP separates
# the floor/walls from objects naturally (their junction is concave), and large
# planar regions simply become their own large clusters. If you prefer to drop
# them, filter clusters by bounding-box flatness after the fact, or re-enable a
# RANSAC pre-pass here.
labels = lccp_labels(pcd)
max_label = labels.max()
print(f"Found {max_label + 1} regions")

# Remove any stale cluster files from a previous run so the output is clean
# and contiguous (cluster0, cluster1, ...). This prevents leftover high-index
# files from a previous mesh confusing the Unity loader.
for old in glob.glob("cluster*.obj"):
    os.remove(old)

# `saved` is the running, CONTIGUOUS index used for filenames. The region label
# `i` is sparse (gaps), so we do NOT use it for naming.
centres = []
saved = 0
for i in range(max_label + 1):
    idx = np.where(labels == i)[0]
    if len(idx) < MIN_CLUSTER_POINTS:
        continue
    cluster_pcd = pcd.select_by_index(idx)

    # Crop the mesh to this cluster, simplify, and skip if it produced no geometry.
    bbox = cluster_pcd.get_axis_aligned_bounding_box()
    bbox.scale(1.2, bbox.get_center())
    cropped = mesh.crop(bbox)
    cropped = cropped.simplify_quadric_decimation(3000)
    if len(cropped.triangles) == 0:
        continue

    # Only record the centre AFTER we know this cluster will actually be saved,
    # and name the file with the contiguous index `saved`.
    centre = np.asarray(cluster_pcd.points).mean(axis=0)
    o3d.io.write_triangle_mesh(f"cluster{saved}.obj", cropped)
    centres.append((saved, centre))
    print(f"Saved cluster{saved}.obj (region {i}), centre={centre}")
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
