import open3d as o3d
import os
import numpy as np

colours = [
    [1,0,0], [0,1,0], [0,0,1], [1,1,0], [1,0,1], [0,1,1],
    [1,0.5,0], [0.5,0,1], [0,1,0.5], [0.5,1,0], [1,0,0.5]
]

geometries = []
for i, f in enumerate(sorted(os.listdir("."))):
    if f.startswith("cluster") and f.endswith(".obj"):
        m = o3d.io.read_triangle_mesh(f)
        colour = colours[i % len(colours)]
        m.vertex_colors = o3d.utility.Vector3dVector(
            np.tile(colour, (len(m.vertices), 1))
        )
        geometries.append(m)
        print(f"Loaded {f} → colour {colour}")

o3d.visualization.draw_geometries(geometries)
