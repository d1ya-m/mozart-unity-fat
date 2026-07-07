"""
LCCP-style segmentation in PURE Open3D (no PCL / conda needed).

This is a faithful reimplementation of the LCCP idea, fixing the two bugs that
broke the earlier attempt (export_clusters_lccp.py):

  BUG 1 (old): adjacency was a radius/k-NN search on patch centroids, which
               connected patches across gaps and concave seams. -> everything
               merged into one region.
  FIX:         use TRUE surface adjacency from the mesh triangles. Two patches
               are neighbours ONLY if a mesh triangle edge connects them.

  BUG 2 (old): union-find merges greedily; a single stray convex edge across a
               seam permanently fused two objects.
  FIX:         region-grow with a flood fill that explicitly STOPS at concave
               edges (never crosses them), so an object seam is a hard wall.

Pipeline:
  1. Load mesh, compute consistent vertex normals.
  2. Over-segment vertices into voxel patches (supervoxel-like).
  3. Build patch adjacency from mesh TRIANGLES (real surface connectivity).
  4. Classify each patch-patch edge convex/concave (extended convexity test).
  5. Region-grow across CONVEX edges only -> each region = one object.
  6. Visualize regions in distinct colours.

Run:  py -3.11 lccp_open3d.py            # uses mesh-3hz-4.obj, shows viewer
      py -3.11 lccp_open3d.py --selftest # runs synthetic touching-box test
"""

import open3d as o3d
import numpy as np
import sys
from collections import defaultdict, deque

# --- Tunables ---------------------------------------------------------------
VOXEL_SIZE          = 0.04   # supervoxel patch size (m). Smaller = finer seams.
CONVEXITY_TOLERANCE = 0.04   # flat-surface slack. RAISE to merge more (fewer
                             # splits), LOWER toward 0 to split more. Positive.
MIN_REGION_VERTS    = 50     # drop tiny regions (noise specks)


def build_patches(verts, voxel):
    """Assign each vertex to a voxel patch. Returns (patch_of_vertex, n_patches)."""
    keys = np.floor(verts / voxel).astype(np.int64)
    key_to_patch = {}
    patch_of_vertex = np.empty(len(verts), dtype=np.int64)
    for vi, key in enumerate(map(tuple, keys)):
        p = key_to_patch.get(key)
        if p is None:
            p = len(key_to_patch)
            key_to_patch[key] = p
        patch_of_vertex[vi] = p
    return patch_of_vertex, len(key_to_patch)


def patch_reps(verts, normals, patch_of_vertex, n_patches):
    """Per-patch centroid + averaged outward normal."""
    centroid = np.zeros((n_patches, 3))
    normal = np.zeros((n_patches, 3))
    count = np.zeros(n_patches)
    for vi in range(len(verts)):
        p = patch_of_vertex[vi]
        centroid[p] += verts[vi]
        normal[p] += normals[vi]
        count[p] += 1
    count = np.maximum(count, 1)[:, None]
    centroid /= count
    nlen = np.linalg.norm(normal, axis=1, keepdims=True)
    nlen = np.maximum(nlen, 1e-9)
    normal /= nlen
    return centroid, normal


def patch_adjacency(triangles, patch_of_vertex):
    """
    TRUE surface adjacency: two patches are neighbours iff some mesh triangle
    has vertices in both patches. This is the key fix - it follows the actual
    surface, so the graph never hops across a gap or seam.
    """
    adj = defaultdict(set)
    for tri in triangles:
        pa, pb, pc = (patch_of_vertex[tri[0]],
                      patch_of_vertex[tri[1]],
                      patch_of_vertex[tri[2]])
        for a, b in ((pa, pb), (pb, pc), (pa, pc)):
            if a != b:
                adj[a].add(b)
                adj[b].add(a)
    return adj


def is_convex(p_i, n_i, p_j, n_j, tol):
    """
    Extended convexity criterion. Convex (same object -> merge) when the outward
    normals fan OUTWARD along the line between the patches; concave (object seam
    -> keep apart) when they close inward.

      convex  <=>  dot(n_i, d_hat) - dot(n_j, d_hat) <= tol
    """
    d = p_j - p_i
    L = np.linalg.norm(d)
    if L < 1e-9:
        return True
    d_hat = d / L
    return (np.dot(n_i, d_hat) - np.dot(n_j, d_hat)) <= tol


def region_grow(adj, centroid, normal, n_patches, tol):
    """
    Flood fill across CONVEX edges only. Each connected component of
    convex-linked patches is one object. Concave edges are never crossed.
    """
    label = np.full(n_patches, -1, dtype=np.int64)
    current = 0
    for seed in range(n_patches):
        if label[seed] != -1:
            continue
        # BFS from this seed, only stepping over convex edges.
        q = deque([seed])
        label[seed] = current
        while q:
            p = q.popleft()
            for nb in adj.get(p, ()):
                if label[nb] != -1:
                    continue
                if is_convex(centroid[p], normal[p], centroid[nb], normal[nb], tol):
                    label[nb] = current
                    q.append(nb)
        current += 1
    return label, current


def segment(mesh, voxel=VOXEL_SIZE, tol=CONVEXITY_TOLERANCE):
    """Returns per-vertex region label array."""
    mesh.compute_vertex_normals()
    verts = np.asarray(mesh.vertices)
    normals = np.asarray(mesh.vertex_normals)
    tris = np.asarray(mesh.triangles)

    patch_of_vertex, n_patches = build_patches(verts, voxel)
    centroid, normal = patch_reps(verts, normals, patch_of_vertex, n_patches)
    adj = patch_adjacency(tris, patch_of_vertex)
    patch_label, n_regions = region_grow(adj, centroid, normal, n_patches, tol)

    vertex_label = patch_label[patch_of_vertex]
    return vertex_label, n_regions


def colourize_and_show(mesh, vertex_label):
    """Paint each region a distinct colour and open the Open3D viewer."""
    rng = np.random.default_rng(0)
    labels = np.unique(vertex_label)
    palette = {l: rng.random(3) for l in labels}
    colours = np.array([palette[l] for l in vertex_label])
    mesh.vertex_colors = o3d.utility.Vector3dVector(colours)
    print(f"Showing {len(labels)} regions. Close the window to exit.")
    o3d.visualization.draw_geometries([mesh])


def selftest():
    """Two boxes TOUCHING on a floor. LCCP should give ~3 regions, not 1."""
    def box(s, t):
        m = o3d.geometry.TriangleMesh.create_box(*s)
        m.translate(t)
        return m.subdivide_midpoint(3)  # dense, clean normals
    floor = box((2, 0.05, 2), (-1, -0.05, -1))
    b1 = box((0.4, 0.4, 0.4), (-0.5, 0.0, 0.0))
    b2 = box((0.4, 0.4, 0.4), (-0.1, 0.0, 0.0))  # shares a face with b1
    scene = floor + b1 + b2
    labels, n = segment(scene)
    big = [l for l in np.unique(labels) if (labels == l).sum() >= MIN_REGION_VERTS]
    print(f"[selftest] regions total={n}, sizeable={len(big)} (expect ~3)")
    if 3 <= len(big) <= 6:
        print("[selftest] PASS - touching objects were separated.")
    else:
        print("[selftest] FAIL - check VOXEL_SIZE / CONVEXITY_TOLERANCE.")
    colourize_and_show(scene, labels)


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    mesh = o3d.io.read_triangle_mesh("mesh-3hz-4.obj")
    if len(mesh.vertices) == 0:
        print("ERROR: mesh-3hz-4.obj not found or empty (run from the clusters/ dir).")
        return
    print(f"Loaded mesh: {len(mesh.vertices)} verts, {len(mesh.triangles)} tris")
    labels, n = segment(mesh)
    sizes = {l: int((labels == l).sum()) for l in np.unique(labels)}
    big = {l: s for l, s in sizes.items() if s >= MIN_REGION_VERTS}
    print(f"Total regions={n}, sizeable (>= {MIN_REGION_VERTS} verts)={len(big)}")
    colourize_and_show(mesh, labels)


if __name__ == "__main__":
    main()
