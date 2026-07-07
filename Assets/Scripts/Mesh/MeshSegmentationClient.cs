using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Sends the headset room mesh (OBJ text) to an off-device segmentation server and
/// receives a clusters manifest + a base URL from which clusterN.obj can be fetched.
///
/// The server (tools/segmentation_server) runs the existing export_clusters_normals.py
/// on a laptop on the same Wi-Fi. On ANY failure this returns null via the callback so
/// the caller can fall back to the bundled StreamingAssets clusters and the app still
/// runs.
///
/// COORDINATE CONVENTION (see IMPLEMENTATION_NOTES.md): the MRUK mesh is already in
/// Unity space. We export it WITHOUT any X-flip, and the returned clusters are loaded
/// with the OBJ loader's X-flip BYPASSED (Phase 4), so the round-trip is identity and
/// picks land where you aim.
/// </summary>
public class MeshSegmentationClient : MonoBehaviour
{
    [Tooltip("Segmentation server base URL. TODO: set to your laptop's LAN IP:port, e.g. " +
             "http://192.168.1.50:5000 (the laptop running tools/segmentation_server/app.py, " +
             "on the SAME Wi-Fi as the Quest).")]
    [SerializeField] private string segmentServerUrl = "http://REPLACE_ME:5000";

    [Tooltip("Request timeout in seconds. Segmentation on a room-scale mesh can take a while.")]
    [SerializeField] private int timeoutSeconds = 120;

    /// <summary>Server response: how many clusters, their indices, and where to fetch them.</summary>
    [Serializable]
    public class SegmentResult
    {
        public int count;
        public int[] indices;
        public string base_url;   // e.g. "http://192.168.1.50:5000/clusters/"
    }

    public string ServerUrl => segmentServerUrl;

    /// <summary>
    /// Serialize a Unity mesh to OBJ text in WORLD space, NO X-flip.
    /// Vertices are transformed by the mesh's transform so the OBJ is in world/Unity
    /// coordinates — the same space clusters will be parented in on return.
    /// </summary>
    public static string MeshToObj(Mesh mesh, Transform t)
    {
        var sb = new StringBuilder();
        Vector3[] v = mesh.vertices;
        int[] tris = mesh.triangles;

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var p in v)
        {
            Vector3 w = t != null ? t.TransformPoint(p) : p;
            sb.Append("v ").Append(w.x.ToString(ci)).Append(' ')
              .Append(w.y.ToString(ci)).Append(' ')
              .Append(w.z.ToString(ci)).Append('\n');
        }
        for (int i = 0; i < tris.Length; i += 3)
        {
            sb.Append("f ").Append(tris[i] + 1).Append(' ')
              .Append(tris[i + 1] + 1).Append(' ')
              .Append(tris[i + 2] + 1).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// POST the mesh OBJ to {segmentServerUrl}/segment. Calls onDone(result) on success,
    /// onDone(null) on any failure (network, timeout, bad response).
    /// </summary>
    public IEnumerator Segment(Mesh mesh, Transform t, Action<SegmentResult> onDone)
    {
        if (mesh == null)
        {
            Debug.LogError("[SEGCLIENT] Segment called with null mesh.");
            onDone(null);
            yield break;
        }

        string obj = MeshToObj(mesh, t);
        byte[] body = Encoding.UTF8.GetBytes(obj);
        string url = $"{segmentServerUrl.TrimEnd('/')}/segment";
        Debug.Log($"[SEGCLIENT] POST {url} ({body.Length} bytes, {mesh.vertexCount} verts)");

        using var req = new UnityWebRequest(url, "POST")
        {
            uploadHandler = new UploadHandlerRaw(body),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = timeoutSeconds
        };
        req.SetRequestHeader("Content-Type", "text/plain");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[SEGCLIENT] /segment failed: {req.responseCode} {req.error}");
            onDone(null);
            yield break;
        }

        SegmentResult result = null;
        try { result = JsonUtility.FromJson<SegmentResult>(req.downloadHandler.text); }
        catch (Exception e)
        {
            Debug.LogError($"[SEGCLIENT] Bad response JSON: {e.Message}\n{req.downloadHandler.text}");
            onDone(null);
            yield break;
        }

        if (result == null || result.indices == null || result.indices.Length == 0)
        {
            Debug.LogError($"[SEGCLIENT] Response had no clusters: {req.downloadHandler.text}");
            onDone(null);
            yield break;
        }

        Debug.Log($"[SEGCLIENT] Segmented into {result.count} clusters. base_url={result.base_url}");
        onDone(result);
    }
}
