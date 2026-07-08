"""
Convert a QuestDatasetRecorder dataset (RGB images + 6D Unity poses + intrinsics) into a
COLMAP "known-pose" model (cameras.txt, images.txt, points3D.txt), so COLMAP can triangulate
with the poses already provided (no pose estimation needed).

Unity pose in frames.csv is CAMERA->WORLD (left-handed, +X right, +Y up, +Z forward).
COLMAP images.txt needs WORLD->CAMERA (right-handed, +X right, +Y DOWN, +Z forward),
Hamilton quaternion (qw, qx, qy, qz) + translation (tx, ty, tz).

Conversion:
  1. Unity cam->world = TRS(pos, quat).
  2. Convert to a right-handed camera basis (flip Y and Z of the CAMERA axes):
     C = camToWorld * diag(1,-1,-1)   -> now +Y down, +Z forward (OpenCV/COLMAP convention)
  3. Invert to world->camera; extract R (as qw,qx,qy,qz) and t.

Usage:
  py -3.11 unity_to_colmap.py <dataset_dir> <output_colmap_dir>
"""
import os, sys, csv, json
import numpy as np


def quat_xyzw_to_matrix(x, y, z, w):
    """Unity quaternion (x,y,z,w) -> 3x3 rotation matrix (left-handed, Unity convention)."""
    n = x*x + y*y + z*z + w*w
    if n < 1e-12:
        return np.eye(3)
    s = 2.0 / n
    xx, yy, zz = x*x*s, y*y*s, z*z*s
    xy, xz, yz = x*y*s, x*z*s, y*z*s
    wx, wy, wz = w*x*s, w*y*s, w*z*s
    return np.array([
        [1-(yy+zz),   xy-wz,     xz+wy],
        [xy+wz,       1-(xx+zz), yz-wx],
        [xz-wy,       yz+wx,     1-(xx+yy)],
    ])


def matrix_to_quat_wxyz(R):
    """3x3 rotation -> (qw, qx, qy, qz)."""
    t = np.trace(R)
    if t > 0:
        s = np.sqrt(t + 1.0) * 2
        qw = 0.25 * s
        qx = (R[2, 1] - R[1, 2]) / s
        qy = (R[0, 2] - R[2, 0]) / s
        qz = (R[1, 0] - R[0, 1]) / s
    elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = np.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2]) * 2
        qw = (R[2, 1] - R[1, 2]) / s; qx = 0.25 * s
        qy = (R[0, 1] + R[1, 0]) / s; qz = (R[0, 2] + R[2, 0]) / s
    elif R[1, 1] > R[2, 2]:
        s = np.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2]) * 2
        qw = (R[0, 2] - R[2, 0]) / s; qx = (R[0, 1] + R[1, 0]) / s
        qy = 0.25 * s;                qz = (R[1, 2] + R[2, 1]) / s
    else:
        s = np.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1]) * 2
        qw = (R[1, 0] - R[0, 1]) / s; qx = (R[0, 2] + R[2, 0]) / s
        qy = (R[1, 2] + R[2, 1]) / s; qz = 0.25 * s
    return qw, qx, qy, qz


def main():
    if len(sys.argv) < 3:
        print("Usage: py -3.11 unity_to_colmap.py <dataset_dir> <output_colmap_dir>")
        sys.exit(1)
    dataset = sys.argv[1]
    out = sys.argv[2]
    sparse = os.path.join(out, "sparse_manual")
    os.makedirs(sparse, exist_ok=True)

    calib = json.load(open(os.path.join(dataset, "calibration", "left_camera.json")))

    # Flip matrix: Unity camera basis -> OpenCV/COLMAP camera basis (Y down, Z forward).
    flipYZ = np.diag([1.0, -1.0, -1.0])

    rows = list(csv.DictReader(open(os.path.join(dataset, "poses", "frames.csv"))))
    if not rows:
        print("No frames in frames.csv"); sys.exit(1)

    # Use the per-frame image resolution (matches the JPGs).
    W = int(rows[0]["width"]); H = int(rows[0]["height"])
    fx, fy, cx, cy = calib["fx"], calib["fy"], calib["cx"], calib["cy"]

    # cameras.txt — one PINHOLE camera shared by all frames.
    with open(os.path.join(sparse, "cameras.txt"), "w") as f:
        f.write("# Camera list: CAMERA_ID, MODEL, WIDTH, HEIGHT, PARAMS[fx,fy,cx,cy]\n")
        f.write(f"1 PINHOLE {W} {H} {fx} {fy} {cx} {cy}\n")

    # images.txt — one line per image with the WORLD->CAMERA pose (qw qx qy qz tx ty tz).
    with open(os.path.join(sparse, "images.txt"), "w") as f:
        f.write("# Image list: IMAGE_ID QW QX QY QZ TX TY TZ CAMERA_ID NAME\n")
        f.write("# (each image followed by an empty POINTS2D line)\n")
        for i, r in enumerate(rows, start=1):
            pos = np.array([float(r["px"]), float(r["py"]), float(r["pz"])])
            R_uc2w = quat_xyzw_to_matrix(float(r["qx"]), float(r["qy"]), float(r["qz"]), float(r["qw"]))

            # cam->world (4x4) in Unity, then convert camera basis to OpenCV.
            camToWorld = np.eye(4)
            camToWorld[:3, :3] = R_uc2w
            camToWorld[:3, 3] = pos
            C = camToWorld @ np.block([[flipYZ, np.zeros((3, 1))], [np.zeros((1, 3)), np.ones((1, 1))]])

            world2cam = np.linalg.inv(C)
            R = world2cam[:3, :3]; t = world2cam[:3, 3]
            qw, qx, qy, qz = matrix_to_quat_wxyz(R)

            name = os.path.basename(r["image"])
            f.write(f"{i} {qw} {qx} {qy} {qz} {t[0]} {t[1]} {t[2]} 1 {name}\n")
            f.write("\n")  # empty 2D-points line (COLMAP triangulates them)

    # points3D.txt — empty; point_triangulator fills it.
    open(os.path.join(sparse, "points3D.txt"), "w").close()

    print(f"Wrote COLMAP model to {sparse}")
    print(f"  cameras.txt: 1 PINHOLE {W}x{H} fx={fx:.1f} fy={fy:.1f} cx={cx:.1f} cy={cy:.1f}")
    print(f"  images.txt:  {len(rows)} images with known world->camera poses")
    print(f"  Copy the JPGs to {os.path.join(out, 'images')} and run the reconstruction (see README).")


if __name__ == "__main__":
    main()
