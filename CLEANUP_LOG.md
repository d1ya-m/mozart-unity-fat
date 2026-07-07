# Cleanup Log — branch `mruk-dynamic-portals`

Records what was removed and the evidence it was safe.

## Deleted

| Item | Evidence it was safe | Commit |
|---|---|---|
| `Assets/StreamingAssets/clusters/export_clusters_lccp.py` (+ .meta) | Abandoned LCCP segmentation experiment. `export_clusters_normals.py` is the active segmenter (used by both the server and the bundled clusters). No script/scene references the LCCP file. | cleanup commit |
| `Assets/StreamingAssets/clusters/lccp_open3d.py` (+ .meta) | Same LCCP experiment. Not referenced anywhere. | cleanup commit |
| `mruk_global_mesh.obj` (repo root) | A pulled debug artifact (the HMD scan OBJ). The app regenerates it each scan at `persistentDataPath/mruk_global_mesh.obj`; it was never meant to be committed. | cleanup commit |

## Deliberately KEPT (documented as dead)

Per the decision to keep the abandoned depth-capture experiment as clearly-labelled dead
files rather than delete them (see `Docs/hmd-scan-pipeline-report.md` §10 "Dead code"):

| Item | Status |
|---|---|
| `Assets/Scripts/Debug/KeyframeCaptureManager.cs` | **DEAD** — Stage-1 depth-PNG capture from the abandoned reconstruction experiment (Path C). Nothing reads its output. Still referenced by an inactive `KeyframeCapture` GameObject in `current.unity`. |
| `Assets/Scripts/Debug/EnvDepthProbe.cs` | **DEAD** — depth-probe diagnostic from the same experiment. Zero code references. |
| `Assets/Materials/Shaders/EnvDepthCapture.shader` | **DEAD** — depth blit shader used only by KeyframeCaptureManager. |

> If a reviewer prefers these removed: `git rm` the three files + their `.meta`, delete the
> `KeyframeCapture` GameObject in `current.unity` (Unity Editor), and re-point the "Scan
> Room" button label away from `KeyframeCaptureManager` (it currently shows a leftover
> frame counter — cosmetic only; the button itself calls `ScanRoomFlow.OnScanRoomPressed`).

## Kept (active / deliverable)

- `export_clusters_normals.py` (active segmenter), `export_clusters.py` (original fallback),
  `visualize_clusters.py` (debug helper).
- Path A pipeline: `GlobalMeshProvider.cs`, `MeshSegmentationClient.cs`, `ScanRoomFlow.cs`,
  `tools/segmentation_server/` — inactive by default (`drivePortalsFromScan=false`) but the
  documented deliverable.
- Bundled clusters + `mesh-3hz-4.{obj,mtl,jpg}` — the reliable portal source (Path B).

## Recommended `.gitignore` additions (not yet applied)
```
mruk_global_mesh.obj
captures/
scans/
```
