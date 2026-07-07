"""
Off-device segmentation server for the MOZART MRUK dynamic-portals demo.

Runs on a LAPTOP on the same Wi-Fi as the Quest. The headset POSTs its MRUK room
mesh as OBJ text; this server runs the EXISTING export_clusters_normals.py on it and
returns the cluster manifest + serves the clusterN.obj files.

IMPORTANT: it works in a fresh COPY of the clusters folder each call, so it never
overwrites the bundled fallback assets (mesh-3hz-4.obj / the shipped clusterN.obj).

Run:
    pip install flask
    py -3.11 app.py
Then set `segmentServerUrl` in the Unity MeshSegmentationClient to http://<this-laptop-ip>:5000

Endpoints:
    POST /segment   body = OBJ text  ->  {"count":N,"indices":[...],"base_url":"http://<ip>:5000/clusters/"}
    GET  /clusters/<file>            ->  serves clusterN.obj / clusters.json from the work dir
    GET  /health                     ->  "ok"
"""
import os
import shutil
import subprocess
import json
import tempfile
from flask import Flask, request, jsonify, send_from_directory, abort

app = Flask(__name__)

# --- Paths -----------------------------------------------------------------
# The bundled clusters folder that contains export_clusters_normals.py. We COPY the
# script out of here into a temp work dir; we never write back into this folder.
# TODO: adjust if you move the repo.
REPO_CLUSTERS_DIR = os.path.abspath(os.path.join(
    os.path.dirname(__file__), "..", "..",
    "Assets", "StreamingAssets", "clusters"))

SEGMENTER = "export_clusters_normals.py"

# Fresh work dir per server process (cleared on each /segment call).
WORK_DIR = os.path.join(tempfile.gettempdir(), "mozart_seg_work")

# Python launcher for the segmenter. open3d needs Python 3.11.
PY = ["py", "-3.11"]


def _prepare_work_dir(obj_text: str):
    """Reset the work dir and drop in the segmenter + the incoming mesh as mesh-3hz-4.obj."""
    if os.path.isdir(WORK_DIR):
        shutil.rmtree(WORK_DIR, ignore_errors=True)
    os.makedirs(WORK_DIR, exist_ok=True)

    src = os.path.join(REPO_CLUSTERS_DIR, SEGMENTER)
    if not os.path.isfile(src):
        raise FileNotFoundError(f"Segmenter not found: {src}")
    shutil.copy2(src, os.path.join(WORK_DIR, SEGMENTER))

    # The segmenter hardcodes read_triangle_mesh("mesh-3hz-4.obj"), so name it that.
    # No .mtl/.jpg needed — it only uses geometry.
    with open(os.path.join(WORK_DIR, "mesh-3hz-4.obj"), "w", encoding="utf-8") as f:
        f.write(obj_text)


@app.route("/health")
def health():
    return "ok"


@app.route("/segment", methods=["POST"])
def segment():
    obj_text = request.get_data(as_text=True)
    if not obj_text or "v " not in obj_text:
        abort(400, "Body must be OBJ text with vertices.")

    try:
        _prepare_work_dir(obj_text)
    except Exception as e:
        abort(500, f"Failed to prepare work dir: {e}")

    # Run the existing segmenter in the work dir.
    proc = subprocess.run(
        PY + [SEGMENTER],
        cwd=WORK_DIR,
        capture_output=True, text=True)
    if proc.returncode != 0:
        # Surface the Python error to the client log for debugging.
        return jsonify({"error": "segmenter failed",
                        "stdout": proc.stdout[-2000:],
                        "stderr": proc.stderr[-2000:]}), 500

    manifest_path = os.path.join(WORK_DIR, "clusters.json")
    if not os.path.isfile(manifest_path):
        return jsonify({"error": "no clusters.json produced",
                        "stdout": proc.stdout[-2000:]}), 500

    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    # Tell the client where to fetch the cluster OBJs from (this same server).
    ip = request.host.split(":")[0]
    port = request.host.split(":")[1] if ":" in request.host else "5000"
    manifest["base_url"] = f"http://{ip}:{port}/clusters/"
    return jsonify(manifest)


@app.route("/clusters/<path:fn>")
def clusters(fn):
    # Only serve from the work dir (clusterN.obj / clusters.json).
    return send_from_directory(WORK_DIR, fn)


if __name__ == "__main__":
    print(f"[seg-server] clusters source dir: {REPO_CLUSTERS_DIR}")
    print(f"[seg-server] work dir:            {WORK_DIR}")
    print("[seg-server] listening on 0.0.0.0:5000  (set Unity segmentServerUrl to http://<this-ip>:5000)")
    app.run(host="0.0.0.0", port=5000)
