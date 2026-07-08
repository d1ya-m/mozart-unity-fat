"""
Full offline reconstruction from a QuestDatasetRecorder dataset:
  RGB images + 6D poses  ->  COLMAP (known-pose triangulation)  ->  OpenMVS (dense + mesh + texture)
  ->  textured OBJ  ->  ready for segmentation (export_clusters_normals.py).

This wraps the standard COLMAP + OpenMVS command-line tools. It uses the poses your headset
already recorded, so COLMAP does NOT estimate camera positions (faster + metrically scaled).

Prerequisites (install separately — see README):
  - COLMAP           (https://colmap.github.io)         -> `colmap` on PATH
  - OpenMVS binaries (https://github.com/cdcseacave/openMVS) -> InterfaceCOLMAP, DensifyPointCloud,
                     ReconstructMesh, RefineMesh, TextureMesh on PATH
  - Python 3.11 + numpy

Usage:
  py -3.11 run_reconstruction.py <dataset_dir> [--out <workdir>] [--skip-refine]

Output: <workdir>/textured.obj (+ .mtl + texture) — feed this to the segmenter.
"""
import os, sys, shutil, subprocess, glob


def sh(cmd, cwd=None):
    print(f"\n>>> {' '.join(cmd)}")
    r = subprocess.run(cmd, cwd=cwd)
    if r.returncode != 0:
        raise SystemExit(f"FAILED ({r.returncode}): {' '.join(cmd)}")


def arg(flag, default=None):
    return sys.argv[sys.argv.index(flag) + 1] if flag in sys.argv else default


def main():
    if len(sys.argv) < 2:
        print("Usage: py -3.11 run_reconstruction.py <dataset_dir> [--out <workdir>] [--skip-refine]")
        sys.exit(1)

    dataset = os.path.abspath(sys.argv[1])
    work = os.path.abspath(arg("--out", os.path.join(dataset, "reconstruction")))
    skip_refine = "--skip-refine" in sys.argv
    here = os.path.dirname(os.path.abspath(__file__))

    colmap_dir = os.path.join(work, "colmap")
    images_dir = os.path.join(colmap_dir, "images")
    sparse_manual = os.path.join(colmap_dir, "sparse_manual")
    sparse_tri = os.path.join(colmap_dir, "sparse_triangulated")
    dense_dir = os.path.join(colmap_dir, "dense")
    mvs_dir = os.path.join(work, "openmvs")
    db = os.path.join(colmap_dir, "database.db")

    for d in (colmap_dir, images_dir, sparse_tri, mvs_dir):
        os.makedirs(d, exist_ok=True)

    # 1) Unity dataset -> COLMAP known-pose model (cameras.txt/images.txt/points3D.txt).
    sh(["py", "-3.11", os.path.join(here, "unity_to_colmap.py"), dataset, colmap_dir])

    # 2) Copy the RGB frames where COLMAP expects them.
    for jpg in glob.glob(os.path.join(dataset, "frames", "*.jpg")):
        shutil.copy2(jpg, images_dir)
    print(f"Copied {len(glob.glob(os.path.join(images_dir, '*.jpg')))} images.")

    # 3) COLMAP: features + matching + KNOWN-POSE triangulation (no mapper/pose estimation).
    sh(["colmap", "feature_extractor", "--database_path", db, "--image_path", images_dir,
        "--ImageReader.camera_model", "PINHOLE", "--ImageReader.single_camera", "1"])
    sh(["colmap", "sequential_matcher", "--database_path", db])
    sh(["colmap", "point_triangulator", "--database_path", db, "--image_path", images_dir,
        "--input_path", sparse_manual, "--output_path", sparse_tri])

    # 4) Undistort into the dense workspace (COLMAP format) for OpenMVS.
    sh(["colmap", "image_undistorter", "--image_path", images_dir,
        "--input_path", sparse_tri, "--output_path", dense_dir, "--output_type", "COLMAP"])

    # 5) OpenMVS: interface -> densify -> mesh -> (refine) -> texture.
    sh(["InterfaceCOLMAP", "-i", dense_dir, "-o", os.path.join(mvs_dir, "scene.mvs")])
    sh(["DensifyPointCloud", "-i", "scene.mvs", "-o", "dense.mvs", "-w", mvs_dir])
    sh(["ReconstructMesh", "-i", "dense.mvs", "-o", "mesh.mvs", "-w", mvs_dir])
    mesh_in = "mesh.mvs"
    if not skip_refine:
        sh(["RefineMesh", "-i", "mesh.mvs", "-o", "refined.mvs", "-w", mvs_dir])
        mesh_in = "refined.mvs"
    sh(["TextureMesh", "-i", mesh_in, "-o", "textured.obj", "-w", mvs_dir])

    final = os.path.join(mvs_dir, "textured.obj")
    print(f"\n=== DONE ===\nTextured mesh: {final}")
    print("Next: segment it with export_clusters_normals.py "
          "(rename/point it at this OBJ), then use the clusters as portals.")


if __name__ == "__main__":
    main()
