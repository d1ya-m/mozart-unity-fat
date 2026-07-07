# Segmentation Server (off-device)

Runs on a **laptop** on the same Wi-Fi as the Quest. The headset sends its MRUK room
mesh here; this server segments it with the existing `export_clusters_normals.py` and
returns per-object cluster meshes.

It works in a **temp copy** of the clusters folder, so it never overwrites the bundled
fallback assets (`Assets/StreamingAssets/clusters/mesh-3hz-4.obj`, shipped `clusterN.obj`).

## One-time setup
```
pip install flask
```
Open3D must be available under Python 3.11 (the segmenter uses it):
```
py -3.11 -c "import open3d; print(open3d.__version__)"
```

## Run
```
cd tools/segmentation_server
py -3.11 app.py
```
You'll see it listening on `0.0.0.0:5000`.

## Point the Quest at it
1. Find the laptop's LAN IP: run `ipconfig` (Windows) → look for the IPv4 address on
   your Wi-Fi adapter, e.g. `192.168.1.50`.
2. In Unity, select the GameObject with **MeshSegmentationClient** and set
   `Segment Server Url` = `http://192.168.1.50:5000` (use YOUR ip).
3. Quest and laptop MUST be on the same Wi-Fi network.

## Test it without the headset
```
curl http://localhost:5000/health           # -> ok
curl -X POST --data-binary "@some_room.obj" -H "Content-Type: text/plain" \
     http://localhost:5000/segment           # -> {"count":N,"indices":[...],"base_url":...}
```

## Endpoints
| Method | Path | Body / returns |
|---|---|---|
| POST | `/segment` | body = OBJ text → `{"count":N,"indices":[...],"base_url":"http://<ip>:5000/clusters/"}` |
| GET | `/clusters/<file>` | serves `clusterN.obj` / `clusters.json` from the work dir |
| GET | `/health` | `ok` |

## Notes / TODO
- The segmenter is CPU-heavy; a room-scale mesh can take several seconds. The Unity
  client timeout is 120 s.
- `REPO_CLUSTERS_DIR` in `app.py` is resolved relative to the repo. If you move the
  server, update that path.
- The incoming mesh has no `.mtl`/`.jpg` — the segmenter only needs geometry, so that
  is fine.
- The work dir is `%TEMP%/mozart_seg_work` and is wiped on each `/segment` call.
