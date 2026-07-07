using System;
using System.Collections;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Provides the headset's MRUK Global Mesh.
/// This will later replace the old keyframe capture pipeline.
/// </summary>
public class GlobalMeshProvider : MonoBehaviour
{
    [SerializeField]
    private bool writeObjForInspection = true;

    public Mesh RoomMesh { get; private set; }

    public Transform RoomMeshTransform { get; private set; }

    public event Action<Mesh, Transform> RoomMeshReady;

    private Coroutine _waitCoroutine;

    private void OnEnable()
    {
        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.AddListener(OnSceneLoaded);
    }

    private void OnDisable()
    {
        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.RemoveListener(OnSceneLoaded);

        if (_waitCoroutine != null)
        {
            StopCoroutine(_waitCoroutine);
            _waitCoroutine = null;
        }
    }

    private void OnSceneLoaded()
    {
        if (_waitCoroutine != null)
            StopCoroutine(_waitCoroutine);

        _waitCoroutine = StartCoroutine(WaitForGlobalMesh());
    }

    private IEnumerator WaitForGlobalMesh()
    {
        const float timeout = 5f;
        const float retryInterval = 0.5f;

        float elapsed = 0f;

        while (elapsed < timeout)
        {
            if (TryCaptureGlobalMesh())
            {
                Debug.Log("[GlobalMeshProvider] Global mesh captured.");
                _waitCoroutine = null;
                yield break;
            }

            Debug.Log("[GlobalMeshProvider] Waiting for GLOBAL_MESH...");
            yield return new WaitForSeconds(retryInterval);
            elapsed += retryInterval;
        }

        Debug.LogWarning("[GlobalMeshProvider] Timed out waiting for GLOBAL_MESH.");
        _waitCoroutine = null;
    }

    public bool TryCaptureGlobalMesh()
    {
        var room = FindFirstObjectByType<MRUKRoom>();

        if (room == null)
        {
            Debug.LogWarning("[GlobalMeshProvider] No MRUKRoom found.");
            return false;
        }

        MeshFilter best = null;

        foreach (var mf in FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
        {
            if (mf.sharedMesh == null)
                continue;

            if (mf.name.Contains("GlobalMesh") ||
                mf.name.Contains("GLOBAL_MESH") ||
                best == null ||
                mf.sharedMesh.vertexCount > best.sharedMesh.vertexCount)
            {
                best = mf;
            }
        }

        if (best == null || best.sharedMesh == null)
        {
            Debug.LogWarning("[GlobalMeshProvider] No Global Mesh MeshFilter found.");
            return false;
        }

        RoomMesh = best.sharedMesh;
        RoomMeshTransform = best.transform;

        Debug.Log(
            $"[GlobalMeshProvider] Global mesh: {RoomMesh.vertexCount} verts, {RoomMesh.triangles.Length / 3} tris");

        if (writeObjForInspection)
            WriteObj(RoomMesh, RoomMeshTransform);

        RoomMeshReady?.Invoke(RoomMesh, RoomMeshTransform);

        return true;
    }

    private static void WriteObj(Mesh mesh, Transform t)
    {
        var sb = new System.Text.StringBuilder();

        var vertices = mesh.vertices;
        var triangles = mesh.triangles;

        foreach (var v in vertices)
        {
            var world = t.TransformPoint(v);
            sb.AppendLine($"v {world.x} {world.y} {world.z}");
        }

        for (int i = 0; i < triangles.Length; i += 3)
        {
            sb.AppendLine(
                $"f {triangles[i] + 1} {triangles[i + 1] + 1} {triangles[i + 2] + 1}");
        }

        string path = System.IO.Path.Combine(
            Application.persistentDataPath,
            "mruk_global_mesh.obj");

        System.IO.File.WriteAllText(path, sb.ToString());

        Debug.Log($"[GlobalMeshProvider] Wrote {path}");
    }
}