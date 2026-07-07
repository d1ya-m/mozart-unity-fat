# MRUK Dynamic Portals — Complete Implementation Handoff

**Purpose:** This single file contains EVERYTHING needed to implement a live
"scan room → segment → place object-shaped portals" demo on the Meta Quest 3,
using the headset's own MRUK scene mesh. Hand this to ChatGPT (or follow it
yourself). It assumes no memory of prior chats.

**Repo root:** `C:\Users\bambu\Documents\BRNO_internship\mozart-unity-fat`
**Unity Editor:** `6000.3.10f1` · **Android package id:** `cz.fitvut.fat`
**adb path:** `C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe`

> Companion doc already in the repo: `DYNAMIC_MESH_DEMO_AUDIT.md` — read it for the
> full evidence behind every fact below.

---

## 0. THE GOAL IN ONE PARAGRAPH

Today the app cuts portals into a **pre-baked external mesh** (`mesh-3hz-4.obj` from
a server). We want the user to press **"Scan Room"**, use the **headset's own MRUK
scene mesh**, send it to a small segmentation server, get back per-object cluster
meshes, and cut portals into THOSE — all in one headset session. The portal
rendering, Add/Remove-portal buttons, and picking logic ALREADY WORK and must not be
broken; we only change where the cluster meshes come from.

---

## 1. VERIFIED FACTS (from source — trust these, don't re-derive)

### 1.1 How portals work today
- **Mask** = cluster geometry. `Custom/StencilMask` shader writes stencil value **6**
  (invisible, `ColorMask 0`, `Queue Geometry-1`, `ZWrite On`).
- **Content** = the textured server room mesh. `Custom/PortalContentUnlit` renders it
  only where `stencil == 6` (`Ref 6 Comp Equal`, `Queue Geometry+500`).
- **Passthrough** outside the mask via `Custom/SelectivePassthroughStencil`
  (`Ref 6 Comp NotEqual`).
- **Layers:** 9 = portalMask, 10 = portalContent, 11 = ClusterPick (raycast layer).
- Mask and content are **decoupled** — linked only by stencil value 6. So clusters can
  be colourless geometry (they are: 0 texcoords, no material).

### 1.2 What the segmentation runs on
- `Assets/StreamingAssets/clusters/export_clusters_normals.py` line 133:
  `mesh = o3d.io.read_triangle_mesh("mesh-3hz-4.obj")`.
- It does: RANSAC strip 6 planes → DBSCAN on 6-D [x,y,z, 0.25·nx,ny,nz] feature →
  per-cluster crop + QEM simplify(3000) → writes `clusterN.obj` + `clusters.json`
  (`{"count":N,"indices":[...]}`) + `centres.txt`.
- `mesh-3hz-4.obj` is the **external/offline textured** mesh (Meshlab, 270 972 verts,
  has `mtllib`+`.jpg`). It is NOT the headset mesh. **Clusters are mask-only.**

### 1.3 How ObjectPicker consumes clusters (the code to redirect later)
File: `Assets/Scripts/Debug/ObjectPicker.cs`
- `LoadManifest()` (~line 222-237): reads `clusters/clusters.json` from
  `Application.streamingAssetsPath` via `UnityWebRequest` (`file://`).
- `PreloadClustersCoroutine()` (~line 152-218): for each index, loads
  `clusters/cluster{id}.obj` via `MeshDownloadManager.LoadMeshFromServer(
  "cluster_preload_{id}", "file://…/cluster{id}.obj")`.
- Each cluster is parented under `_sceneMeshTransform` (currently the
  **`ServerSceneMesh`**) at **local identity**, renderer OFF, with a dedicated child
  `ClusterPick_{id}` carrying a `MeshCollider` on layer 11. Picking is by raycast
  against that collider → **match-space == render-space by construction**.
- Add/Remove portal via `SetAddMode(bool)`/`SetRemoveMode(bool)` wired to ToolMenu
  Toggle buttons; `AddPortal(id)`/`RemovePortal(id)` toggle the renderer + material.
- **These two URL reads are the ONLY things to change to load server clusters.**

### 1.4 MRUK (headset scan) — present but not producing a dense mesh
- MRUK is enabled in `current.unity`: component enabled, `DataSource: 2`
  (`DeviceWithPrefabFallback`), `LoadSceneOnStartup: 1`.
- `MozartSpatialBridge.cs` already loads the room from device
  (`MRUK.Instance.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound:true,…)`) and
  fires `MRUK.Instance.RoomCreatedEvent` / `SceneLoadedEvent`.
- **The scene's `EffectMesh` has `Labels: 7` = FLOOR|CEILING|WALL_FACE — NOT
  GLOBAL_MESH (1<<14 = 16384).** So no single dense room mesh is created today.
- MRUK's `EffectMesh.CreateGlobalMeshObject` (in the package) DOES create a
  `MeshFilter` whose `sharedMesh` is the dense room mesh when GLOBAL_MESH is enabled.
  So the headset CAN give a Unity `Mesh` — we just need a GLOBAL_MESH EffectMesh.

### 1.5 The abandoned experiment (safe to delete — nothing in-app reads it)
- `Assets/Scripts/Debug/KeyframeCaptureManager.cs` (Stage-1 depth capture)
- `Assets/Scripts/Debug/EnvDepthProbe.cs` (depth probe)
- `Assets/Materials/Shaders/EnvDepthCapture.shader`
- `captures/`, `scans/` (raw session data)
- `Assets/StreamingAssets/clusters/ScreenCamera_*.json`, `ScreenCapture_*.png`,
  `export_clusters_lccp.py`, `lccp_open3d.py`, `__pycache__/`
- Scene: the `KeyframeCapture` GameObject (has `capturing: 1` auto-start flag).
- **Only KeyframeCaptureManager writes `captures/`; NOTHING reads it.** Grep the repo
  for each type/filename before deleting (see Phase 1 checks).

### 1.6 THE #1 RISK — coordinate X-flip
`MeshDownloadManager.cs` (~line 812-820) `ConvertObjVectorToUnity` mirrors every
imported OBJ on X (`flipObjXAxisForUnity = true`, default). The MRUK mesh is ALREADY
in Unity space. If MRUK-sourced clusters go back through this same OBJ loader, they
get **double-mirrored** → picks land on empty space / portals render mirrored.
**You must pick ONE basis end to end** (see Phase 3 decision).

---

## 2. GIT SAFETY (do this FIRST, every phase)

```bash
cd /c/Users/bambu/Documents/BRNO_internship/mozart-unity-fat
git status                       # working tree is currently DIRTY (expected)
git checkout -b mruk-dynamic-portals
git add -A                       # stage everything incl. the depth experiment
git commit -m "chore: baseline before mruk-dynamic-portals (WIP depth experiment + docs)"
git rev-parse HEAD               # RECORD this hash in IMPLEMENTATION_NOTES.md
```
- Commit **after each phase** with the message given at the end of that phase.
- `Packages/mozart-unity-shared` is a **submodule** — `git add -A` records its pointer
  only; do NOT commit inside it.
- Never delete outside git. Deleting a Unity asset means deleting its `.meta` too.
- Rules: don't modify `ObjectPicker.cs`, StencilMask/PortalContentUnlit/
  SelectivePassthrough shaders, GameManager portal logic, MRUK, or MeshDownloadManager
  EXCEPT the named redirect points in Phase 4. Don't delete `mesh-3hz-4.{obj,mtl,jpg}`
  or the bundled `clusterN.obj`/`clusters.json` (they're the fallback).

---

## 3. PHASE 1 — Delete the abandoned experiment

**Reference-check each file before deleting** (must show zero external references):
```bash
# For a script, grep its TYPE NAME across scripts + scenes + prefabs:
grep -rn "KeyframeCaptureManager" Assets --include=*.cs --include=*.unity --include=*.prefab
grep -rn "EnvDepthProbe"          Assets --include=*.cs --include=*.unity --include=*.prefab
# Expect: only the file itself + the scene GameObject we're removing.
```

**Delete (with `.meta`):**
```bash
git rm Assets/Scripts/Debug/KeyframeCaptureManager.cs Assets/Scripts/Debug/KeyframeCaptureManager.cs.meta
git rm Assets/Scripts/Debug/EnvDepthProbe.cs Assets/Scripts/Debug/EnvDepthProbe.cs.meta
git rm Assets/Materials/Shaders/EnvDepthCapture.shader Assets/Materials/Shaders/EnvDepthCapture.shader.meta
git rm -r captures scans
git rm Assets/StreamingAssets/clusters/ScreenCamera_*.json Assets/StreamingAssets/clusters/ScreenCamera_*.json.meta
git rm Assets/StreamingAssets/clusters/ScreenCapture_*.png Assets/StreamingAssets/clusters/ScreenCapture_*.png.meta
git rm Assets/StreamingAssets/clusters/export_clusters_lccp.py Assets/StreamingAssets/clusters/export_clusters_lccp.py.meta
git rm Assets/StreamingAssets/clusters/lccp_open3d.py Assets/StreamingAssets/clusters/lccp_open3d.py.meta
git rm -r Assets/StreamingAssets/clusters/__pycache__ Assets/StreamingAssets/clusters/__pycache__.meta
```
> ⚠️ VERIFY first that `ScreenCamera_*`/`ScreenCapture_*` are truly unused:
> `grep -rn "ScreenCamera\|ScreenCapture" Assets --include=*.cs`. If a script reads
> them, keep them. (Audit says they're unrelated debug artifacts.)

**Scene cleanup (`Assets/Scenes/current.unity`) — do in the Unity Editor, safest:**
1. Open the scene. In Hierarchy, find the **`KeyframeCapture`** GameObject → delete it.
2. Find the ToolMenu "Scan Room" button that was wired to
   `KeyframeCaptureManager.SetScanMode` — either delete that button or clear its
   `On Value Changed` call (we re-add a Scan Room button in Phase 5).
3. Save the scene. Unity auto-cleans the dangling MonoBehaviour references.
> The scene had `capturing: 1` serialized on that GameObject (auto-start). Deleting
> the GameObject removes it. If you keep KeyframeCapture for any reason instead, set
> `capturing` to false in the Inspector.

**Log every deletion in `CLEANUP_LOG.md`** (filename + "grep showed 0 refs").

```bash
git add -A
git commit -m "cleanup: remove abandoned depth-capture + reconstruction experiment"
```

**Plain-language:** we removed the broken depth-scanning experiment and its data.
Nothing in the running app used it, so the app behaves exactly as before.

---

## 4. PHASE 2 — Headset global mesh (NEW EffectMesh, non-destructive)

**Do NOT edit the existing Floor/Ceiling/Wall EffectMesh.** Add a SECOND one for the
dense global mesh only.

**In the Unity Editor:**
1. In Hierarchy, find the existing `EffectMesh` (under the MRUK object). Duplicate it,
   rename the copy `EffectMesh_GlobalMesh`.
2. On the copy's `EffectMesh` component: set **Labels = GLOBAL_MESH only** (untick
   Floor/Ceiling/Wall; tick "Global Mesh"). Disable its renderer (uncheck the
   MeshRenderer, or clear MeshMaterial) so it's invisible geometry.
3. Leave the original EffectMesh untouched.

**Add `Assets/Scripts/Mesh/GlobalMeshProvider.cs`** (the mesh-source seam — a future
COLMAP/SfM source can implement the same shape without touching anything else):

```csharp
using System;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// The single seam for "where the room mesh comes from". Today it grabs the MRUK
/// GLOBAL_MESH. A future COLMAP/OpenMVS source can replace THIS class only.
/// On room load it finds the GLOBAL_MESH MeshFilter and exposes the Mesh + its
/// world transform. Logs vertex/triangle counts; optionally writes an OBJ to
/// persistentDataPath for off-device inspection.
/// </summary>
public class GlobalMeshProvider : MonoBehaviour
{
    [SerializeField] private bool writeObjForInspection = true;

    public Mesh RoomMesh { get; private set; }
    public Transform RoomMeshTransform { get; private set; }
    public event Action<Mesh, Transform> RoomMeshReady;

    private void OnEnable()
    {
        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.AddListener(OnSceneLoaded);
    }
    private void OnDisable()
    {
        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
    }

    private void OnSceneLoaded() => TryCaptureGlobalMesh();

    // Call this after MRUK has loaded a room (also safe to call from the Scan button).
    public bool TryCaptureGlobalMesh()
    {
        // The GLOBAL_MESH EffectMesh creates a GameObject with a MeshFilter whose
        // sharedMesh is the dense room mesh. Find the largest MeshFilter under MRUK
        // that isn't a plane. Simplest robust heuristic: pick the MeshFilter with the
        // most vertices among children of any MRUKRoom.
        var room = FindFirstObjectByType<MRUKRoom>();
        if (room == null) { Debug.LogWarning("[GlobalMeshProvider] No MRUKRoom yet."); return false; }

        MeshFilter best = null;
        foreach (var mf in FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
        {
            if (mf.sharedMesh == null) continue;
            if (mf.name.Contains("GlobalMesh") || mf.name.Contains("GLOBAL_MESH")
                || best == null || mf.sharedMesh.vertexCount > best.sharedMesh.vertexCount)
                best = mf;
        }
        if (best == null || best.sharedMesh == null)
        { Debug.LogWarning("[GlobalMeshProvider] No global mesh MeshFilter found. Is a GLOBAL_MESH EffectMesh active?"); return false; }

        RoomMesh = best.sharedMesh;
        RoomMeshTransform = best.transform;
        Debug.Log($"[GlobalMeshProvider] Global mesh: {RoomMesh.vertexCount} verts, {RoomMesh.triangles.Length/3} tris, transform={RoomMeshTransform.name}");

        if (writeObjForInspection) WriteObj(RoomMesh, RoomMeshTransform);
        RoomMeshReady?.Invoke(RoomMesh, RoomMeshTransform);
        return true;
    }

    // Writes the mesh in WORLD space as OBJ to persistentDataPath for adb pull + inspection.
    private static void WriteObj(Mesh mesh, Transform t)
    {
        var sb = new System.Text.StringBuilder();
        Vector3[] v = mesh.vertices; int[] tris = mesh.triangles;
        foreach (var p in v) { var w = t.TransformPoint(p); sb.AppendLine($"v {w.x} {w.y} {w.z}"); }
        for (int i = 0; i < tris.Length; i += 3)
            sb.AppendLine($"f {tris[i]+1} {tris[i+1]+1} {tris[i+2]+1}");
        string path = System.IO.Path.Combine(Application.persistentDataPath, "mruk_global_mesh.obj");
        System.IO.File.WriteAllText(path, sb.ToString());
        Debug.Log($"[GlobalMeshProvider] Wrote {path}");
    }
}
```
Add this component to a new GameObject `GlobalMeshProvider` in the scene.

**Verify:** run on device; logcat should show
`[GlobalMeshProvider] Global mesh: N verts …`. Pull the OBJ:
`adb pull /sdcard/Android/data/cz.fitvut.fat/files/mruk_global_mesh.obj .` and open it
in MeshLab to confirm it looks like your room. Confirm the original Floor/Ceiling/Wall
EffectMesh still renders as before.

```bash
git add -A
git commit -m "feat: add global-mesh EffectMesh + runtime provider"
```

**Plain-language:** we told the headset to build a full 3D mesh of the room (invisible),
and added a small component that grabs it and can save it for us to inspect.

---

## 5. PHASE 3 — Segmentation client + off-device server

### 5.1 COORDINATE DECISION (write it in IMPLEMENTATION_NOTES.md)
The MRUK mesh is in Unity space. The OBJ loader X-flips imports. **Decision (recommended):
export the MRUK mesh WITHOUT flipping, and load MRUK-sourced clusters with the flip
BYPASSED**, so no mirror happens on either leg (identity round-trip). Concretely: add a
loader overload/flag so cluster loading for MRUK clusters uses `flipObjXAxisForUnity =
false`. (The alternative — pre-flip on export so the loader's flip cancels — also works;
pick one and be consistent.)

### 5.2 Client: `Assets/Scripts/Mesh/MeshSegmentationClient.cs`
```csharp
using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Sends the room mesh (OBJ) to a segmentation server and receives a clusters.json +
/// a base URL for clusterN.obj. On any failure, callers fall back to the bundled
/// StreamingAssets clusters so the app still runs.
/// </summary>
public class MeshSegmentationClient : MonoBehaviour
{
    [Tooltip("Segmentation server base URL. TODO: set to your laptop's LAN IP:port, " +
             "e.g. http://192.168.1.50:5000  (must be reachable from the Quest over Wi-Fi).")]
    [SerializeField] private string segmentServerUrl = "http://REPLACE_ME:5000";

    [Serializable] public class SegmentResult { public int count; public int[] indices; public string base_url; }

    // Serialize a Unity mesh to OBJ text in WORLD space, NO X-flip (see coord decision).
    public static string MeshToObj(Mesh mesh, Transform t)
    {
        var sb = new StringBuilder();
        var v = mesh.vertices; var tris = mesh.triangles;
        foreach (var p in v) { var w = t.TransformPoint(p); sb.Append("v ").Append(w.x).Append(' ').Append(w.y).Append(' ').Append(w.z).Append('\n'); }
        for (int i = 0; i < tris.Length; i += 3)
            sb.Append("f ").Append(tris[i]+1).Append(' ').Append(tris[i+1]+1).Append(' ').Append(tris[i+2]+1).Append('\n');
        return sb.ToString();
    }

    // POST the OBJ, get back SegmentResult. onDone(null) on failure.
    public IEnumerator Segment(Mesh mesh, Transform t, Action<SegmentResult> onDone)
    {
        string obj = MeshToObj(mesh, t);
        byte[] body = Encoding.UTF8.GetBytes(obj);
        using var req = new UnityWebRequest($"{segmentServerUrl.TrimEnd('/')}/segment", "POST");
        req.uploadHandler = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "text/plain");
        req.timeout = 120;
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success)
        { Debug.LogError($"[MeshSegmentationClient] /segment failed: {req.error}"); onDone(null); yield break; }
        try { onDone(JsonUtility.FromJson<SegmentResult>(req.downloadHandler.text)); }
        catch (Exception e) { Debug.LogError($"[MeshSegmentationClient] bad response: {e.Message}"); onDone(null); }
    }
}
```

### 5.3 Server scaffold: `tools/segmentation_server/`
`tools/segmentation_server/app.py`:
```python
# Off-device. Run on a laptop on the SAME Wi-Fi as the Quest.
# POST /segment  (body = OBJ text) -> runs export_clusters_normals.py -> returns
# {"count":N,"indices":[...],"base_url":"http://<ip>:5000/clusters/"} and serves clusterN.obj.
import os, subprocess, tempfile, shutil, json
from flask import Flask, request, jsonify, send_from_directory

app = Flask(__name__)
WORK = os.path.join(tempfile.gettempdir(), "seg_work")
os.makedirs(WORK, exist_ok=True)

# Path to the EXISTING segmenter (adjust to your checkout):
SEG_DIR = r"C:\Users\bambu\Documents\BRNO_internship\mozart-unity-fat\Assets\StreamingAssets\clusters"

@app.route("/segment", methods=["POST"])
def segment():
    obj = request.get_data(as_text=True)
    # The segmenter reads mesh-3hz-4.obj from its own dir; write the incoming mesh there.
    open(os.path.join(SEG_DIR, "mesh-3hz-4.obj"), "w").write(obj)
    # NOTE: incoming mesh has no .mtl/.jpg; segmenter only needs geometry. OK.
    subprocess.run(["py", "-3.11", "export_clusters_normals.py"], cwd=SEG_DIR, check=True)
    manifest = json.load(open(os.path.join(SEG_DIR, "clusters.json")))
    ip = request.host.split(":")[0]
    manifest["base_url"] = f"http://{ip}:5000/clusters/"
    return jsonify(manifest)

@app.route("/clusters/<path:fn>")
def clusters(fn):
    return send_from_directory(SEG_DIR, fn)

if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5000)
```
`tools/segmentation_server/README.md`: "Install `pip install flask`. Run
`py -3.11 app.py`. Find your laptop IP (`ipconfig`). Set `segmentServerUrl` in the
Unity `MeshSegmentationClient` to `http://<that-ip>:5000`. Quest and laptop must be on
the same Wi-Fi. TODO: this overwrites the bundled `mesh-3hz-4.obj` each call — copy the
originals aside first if you want the pre-baked fallback intact."

> ⚠️ **TODO / caveat:** the segmenter is hardcoded to read/write `mesh-3hz-4.obj` and
> `clusterN.obj` in the SAME folder as the bundled fallback. For safety, make the server
> work in a COPY of the clusters dir so it never clobbers the shipped fallback assets.

```bash
git add -A
git commit -m "feat: mesh segmentation client + server scaffold"
```

**Plain-language:** the app can now send the room mesh to a small program on your
laptop, which chops it into per-object pieces and sends them back. If the laptop isn't
reachable, the app keeps using the built-in pieces.

---

## 6. PHASE 4 — Point ObjectPicker at server clusters + PROVE coordinates

**Edit ONLY these two reads in `Assets/Scripts/Debug/ObjectPicker.cs`** (keep
StreamingAssets as a fallback mode — add a `[SerializeField] string clusterBaseUrl`
that, when empty, uses the old `file://` path):

- In `LoadManifest()` (~line 224): if `clusterBaseUrl` is set, GET
  `{clusterBaseUrl}clusters.json` instead of the streamingAssetsPath file.
- In `PreloadClustersCoroutine()` (~line 172-175): if `clusterBaseUrl` is set, load
  `{clusterBaseUrl}cluster{id}.obj` instead of the local file.
- **Parent clusters under the GLOBAL_MESH transform** (from `GlobalMeshProvider.
  RoomMeshTransform`) — NOT `ServerSceneMesh` — at local identity (line ~180-183 keeps
  identity; just change the parent).
- For MRUK clusters, load with the **X-flip bypassed** (per Phase 3 decision). Add a
  loader path/flag; do not change the existing behaviour for StreamingAssets clusters.

**REQUIRED VERIFICATION (highest-risk — do not skip):**
- **(a) Round-trip test:** export a known mesh (e.g. a labelled cube) → server → reload;
  assert vertex positions match within epsilon. Proves the basis/flip is consistent.
- **(b) Pick test:** add a debug log per trigger printing the aimed ray vs the hit
  cluster's world centroid; confirm the pick lands on the aimed object, NOT mirrored.
- **If either fails, STOP** — the X-flip decision is wrong; flip the other choice.

```bash
git add -A
git commit -m "feat: load MRUK-sourced clusters into ObjectPicker with verified coordinates"
```

**Plain-language:** the portal picker now uses the per-object pieces from your scan,
placed exactly where you're pointing. We test that pointing at a chair selects the
chair (not a mirror-image offset).

---

## 7. PHASE 5 — Portal content + "Scan Room" button

**Content:** add a virtual-environment or skybox object on the **portalContent** layer
using `Custom/PortalContentUnlit` (it already does `Stencil Ref 6 Comp Equal`). This is
what shows INSIDE the portals, so you don't need the room to be texture-matched. Don't
touch the mask path.

**Button flow — add `Assets/Scripts/Mesh/ScanRoomFlow.cs`:**
```csharp
using System.Collections;
using Meta.XR.MRUtilityKit;
using UnityEngine;

public class ScanRoomFlow : MonoBehaviour
{
    public enum ScanMode { ReloadExisting, FreshSpaceSetup }
    [SerializeField] private ScanMode mode = ScanMode.ReloadExisting;
    [SerializeField] private GlobalMeshProvider meshProvider;
    [SerializeField] private MeshSegmentationClient segmenter;
    [SerializeField] private ObjectPicker objectPicker; // to hand it the returned base_url

    // Wire this to the "Scan Room" ToolMenu button (onClick / onValueChanged).
    public void OnScanRoomPressed() => StartCoroutine(ScanRoutine());

    private IEnumerator ScanRoutine()
    {
        Debug.Log($"[ScanRoomFlow] Scan pressed (mode={mode}).");
        var load = MRUK.Instance.LoadSceneFromDevice(
            requestSceneCaptureIfNoDataFound: mode == ScanMode.FreshSpaceSetup);
        while (!load.IsCompleted) yield return null;

        if (!meshProvider.TryCaptureGlobalMesh())
        { Debug.LogWarning("[ScanRoomFlow] No global mesh."); yield break; }

        bool done = false;
        yield return segmenter.Segment(meshProvider.RoomMesh, meshProvider.RoomMeshTransform, res =>
        {
            done = true;
            if (res == null) { Debug.LogWarning("[ScanRoomFlow] Segmentation failed; keeping bundled clusters."); return; }
            // TODO: call an ObjectPicker method that sets clusterBaseUrl = res.base_url and
            // re-runs its preload (add a public ReloadClustersFromUrl(string) on ObjectPicker
            // that clears current clusters and restarts PreloadClustersCoroutine).
            Debug.Log($"[ScanRoomFlow] Segmented into {res.count} clusters at {res.base_url}");
        });
        while (!done) yield return null;
    }
}
```
Wire the existing/new "Scan Room" button's event to `ScanRoomFlow.OnScanRoomPressed`.
Keep Add/Remove Portal toggles exactly as they are. Ensure the button is NOT connected
to any deleted capture code.

```bash
git add -A
git commit -m "feat: scan-room button flow + virtual portal content"
```

**Plain-language:** pressing "Scan Room" loads your room, meshes it, sends it to be
segmented, and loads the pieces as portal shapes. Portals show a virtual scene inside.

---

## 8. PHASE 6 — Wrap up

- In Unity: **Console must show no compile errors**; check the scene/prefabs for
  dangling (missing-script) references — fix or remove cleanly.
- Finalize `CLEANUP_LOG.md` (every deletion + its grep evidence) and
  `IMPLEMENTATION_NOTES.md` (new components; the mesh-source seam = `GlobalMeshProvider`;
  the chosen coordinate convention; how to run `tools/segmentation_server`; the two
  decisions — `ScanMode` default `ReloadExisting`, coord basis = no-flip both legs — and
  a MANUAL TEST CHECKLIST).

```bash
git add -A
git commit -m "docs: implementation notes + cleanup log"
```

### MANUAL TEST CHECKLIST (on Quest)
1. Laptop: `py -3.11 tools/segmentation_server/app.py`; note IP; set `segmentServerUrl`.
2. Build & Run to Quest (same Wi-Fi).
3. Press **Scan Room** → logcat shows global-mesh verts, then "Segmented into N clusters".
4. Press **Add Portal**, point at an object → green laser → trigger → portal appears in
   the object's silhouette, showing virtual content.
5. Press **Remove Portal**, point at it → trigger → portal disappears.
6. If clusters look mirrored/offset → the X-flip decision (Phase 3) is wrong; flip it.

---

## 9. THE TWO KEY DECISIONS (record current values)
1. **`ScanMode`** default = `ReloadExisting` (reuse the device's existing room scan).
   Use `FreshSpaceSetup` only to force a new Space Setup.
2. **Coordinate basis** = NO X-flip on either leg: export MRUK mesh unflipped, load its
   clusters with `flipObjXAxisForUnity` bypassed. (Alternative: pre-flip on export to
   cancel the loader flip — pick one, stay consistent.)

## 10. FUTURE SfM SWAP (why the seam matters)
To later replace MRUK with a COLMAP/OpenMVS photogrammetry mesh, implement a new
provider exposing the same `(Mesh RoomMesh, Transform RoomMeshTransform, event
RoomMeshReady)` shape as `GlobalMeshProvider`, and point `ScanRoomFlow` at it. Nothing
in segmentation, ObjectPicker, or the button flow needs to change.

---

## 11. FILE-BY-FILE CHANGE SUMMARY
| Phase | Action | File |
|---|---|---|
| 0 | baseline commit | (git) |
| 1 | delete | KeyframeCaptureManager.cs, EnvDepthProbe.cs, EnvDepthCapture.shader, captures/, scans/, Screen*/lccp*/pycache under clusters/ |
| 1 | edit (Editor) | current.unity — remove KeyframeCapture GameObject + its button wiring |
| 2 | add (Editor) | EffectMesh_GlobalMesh (duplicate, GLOBAL_MESH label, renderer off) |
| 2 | add | Assets/Scripts/Mesh/GlobalMeshProvider.cs |
| 3 | add | Assets/Scripts/Mesh/MeshSegmentationClient.cs, tools/segmentation_server/{app.py,README.md} |
| 4 | edit (named points only) | ObjectPicker.cs — 2 URL reads + parent transform + flip bypass |
| 5 | add | Assets/Scripts/Mesh/ScanRoomFlow.cs, a portalContent virtual-scene object |
| 6 | docs | CLEANUP_LOG.md, IMPLEMENTATION_NOTES.md |

**Do NOT touch:** StencilMask/PortalContentUnlit/SelectivePassthrough shaders,
GameManager portal logic, MRUK core, MeshDownloadManager (except the Phase-4 flip flag),
and the bundled `mesh-3hz-4.{obj,mtl,jpg}` + existing `clusterN.obj`/`clusters.json`.
