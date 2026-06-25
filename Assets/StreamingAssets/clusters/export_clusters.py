import open3d as o3d
import numpy as np
import os
import json
import glob

mesh = o3d.io.read_triangle_mesh("mesh-3hz-4.obj")
mesh.compute_vertex_normals()
pcd = mesh.sample_points_uniformly(number_of_points=100000)

# RANSAC: strip 6 dominant planes (floor, walls, ceiling)
remaining = pcd
for _ in range(6):
    plane, inliers = remaining.segment_plane(
        distance_threshold=0.03, ransac_n=3, num_iterations=1000)
    remaining = remaining.select_by_index(inliers, invert=True)

# DBSCAN: cluster remaining points into objects
labels = np.array(remaining.cluster_dbscan(eps=0.15, min_points=20))
max_label = labels.max()
print(f"Found {max_label + 1} clusters")

# Remove any stale cluster files from a previous run so the output is clean
# and contiguous (cluster0, cluster1, ...). This prevents leftover high-index
# files from a previous mesh confusing the Unity loader.
for old in glob.glob("cluster*.obj"):
    os.remove(old)

# `saved` is the running, CONTIGUOUS index used for filenames. The DBSCAN label
# `i` is sparse (gaps), so we do NOT use it for naming - that was the bug.
centres = []
saved = 0
for i in range(max_label + 1):
    idx = np.where(labels == i)[0]
    if len(idx) < 200:
        continue
    cluster_pcd = remaining.select_by_index(idx)

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
