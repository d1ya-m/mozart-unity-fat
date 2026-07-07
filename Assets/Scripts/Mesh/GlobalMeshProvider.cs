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
    private bool _subscribed;
    private bool _captured;   // becomes true once a valid global mesh is captured

    private void Awake()
    {
        Debug.Log("[KFGMP] Awake — script is alive");
    }

    private void OnEnable()
    {
        Debug.Log("[KFGMP] OnEnable");
        TrySubscribe();
    }

    private void Start()
    {
        // MRUK.Instance may not exist yet at OnEnable (it initialises on its own
        // schedule). Poll from Start so we (a) prove the script runs even if the
        // SceneLoadedEvent never fires, and (b) subscribe as soon as MRUK appears,
        // then just try to capture the mesh directly if the scene is already loaded.
        Debug.Log("[KFGMP] Start — beginning MRUK poll");
        StartCoroutine(PollForMruk());
    }

    // Subscribe to MRUK's SceneLoadedEvent once MRUK exists. Idempotent.
    private void TrySubscribe()
    {
        if (_subscribed || MRUK.Instance == null)
        {
            if (MRUK.Instance == null)
                Debug.Log("[KFGMP] MRUK.Instance is null (not ready yet).");
            return;
        }
        MRUK.Instance.SceneLoadedEvent.AddListener(OnSceneLoaded);
        _subscribed = true;
        Debug.Log("[KFGMP] Subscribed to MRUK.SceneLoadedEvent.");
    }

    // Poll for MRUK for up to ~20s. Once MRUK exists, subscribe and also try a direct
    // capture (in case the room is already loaded and we missed the event).
    private IEnumerator PollForMruk()
    {
        for (int i = 0; i < 40; i++)   // 40 * 0.5s = 20s
        {
            if (MRUK.Instance == null)
            {
                Debug.Log($"[KFGMP] poll {i}: MRUK.Instance still null...");
                yield return new WaitForSeconds(0.5f);
                continue;
            }

            TrySubscribe();

            // If a room already exists, capture directly (don't wait for the event).
            if (!_captured && FindFirstObjectByType<MRUKRoom>() != null)
            {
                Debug.Log("[KFGMP] MRUKRoom already present — capturing directly.");
                if (_waitCoroutine == null)
                    _waitCoroutine = StartCoroutine(WaitForGlobalMesh());
                yield break;
            }
            if (_captured) yield break;

            Debug.Log($"[KFGMP] poll {i}: MRUK ready, waiting for a room...");
            yield return new WaitForSeconds(0.5f);
        }
        Debug.LogWarning("[KFGMP] Gave up polling for MRUK/room after ~20s.");
    }

    private void OnDisable()
    {
        if (_subscribed && MRUK.Instance != null)
        {
            MRUK.Instance.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
            _subscribed = false;
        }

        if (_waitCoroutine != null)
        {
            StopCoroutine(_waitCoroutine);
            _waitCoroutine = null;
        }
    }

    private void OnSceneLoaded()
    {
        Debug.Log("[KFGMP] SceneLoadedEvent fired");
        if (_captured) return;   // already have the mesh; ignore later scene events
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
                _captured = true;
                Debug.Log("[KFGMP] Global mesh captured.");
                _waitCoroutine = null;
                yield break;
            }

            Debug.Log("[KFGMP] Waiting for GLOBAL_MESH...");
            yield return new WaitForSeconds(retryInterval);
            elapsed += retryInterval;
        }

        Debug.LogWarning("[KFGMP] Timed out waiting for GLOBAL_MESH.");
        _waitCoroutine = null;
    }

    // A room mesh must have at least this many triangles to be accepted. The MRUK
    // GLOBAL_MESH is tens of thousands of tris; plane-anchor / UI meshes are tiny or
    // zero-triangle. This rejects the "205 verts, 0 tris" decoy we saw on-device.
    private const int MinGlobalMeshTriangles = 1000;

    public bool TryCaptureGlobalMesh()
    {
        var room = FindFirstObjectByType<MRUKRoom>();

        if (room == null)
        {
            Debug.LogWarning("[KFGMP] No MRUKRoom found.");
            return false;
        }

        // Pick the MeshFilter that best matches the GLOBAL_MESH:
        //   1. Prefer a mesh whose object is named "..._EffectMesh" AND has triangles
        //      (MRUK names the global-mesh GameObject "<anchorName>_EffectMesh").
        //   2. Otherwise the mesh with the MOST triangles.
        // A candidate MUST have >= MinGlobalMeshTriangles triangles to qualify, so the
        // tiny 0-triangle decoy meshes are never selected.
        MeshFilter best = null;
        int bestTris = 0;
        bool bestNameMatch = false;

        foreach (var mf in FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
        {
            var m = mf.sharedMesh;
            if (m == null) continue;

            int tris = m.triangles.Length / 3;
            if (tris < MinGlobalMeshTriangles) continue;   // reject tiny/decoy meshes

            bool nameMatch = mf.name.IndexOf("EffectMesh", StringComparison.OrdinalIgnoreCase) >= 0
                          || mf.name.IndexOf("GlobalMesh", StringComparison.OrdinalIgnoreCase) >= 0
                          || mf.name.IndexOf("GLOBAL_MESH", StringComparison.OrdinalIgnoreCase) >= 0;

            // A name match always beats a non-match; among equal match-status, more
            // triangles wins.
            bool better = best == null
                || (nameMatch && !bestNameMatch)
                || (nameMatch == bestNameMatch && tris > bestTris);
            if (better)
            {
                best = mf; bestTris = tris; bestNameMatch = nameMatch;
            }
        }

        if (best == null)
        {
            Debug.LogWarning($"[KFGMP] No global mesh yet (no MeshFilter with >= {MinGlobalMeshTriangles} tris).");
            return false;
        }

        RoomMesh = best.sharedMesh;
        RoomMeshTransform = best.transform;

        Debug.Log(
            $"[KFGMP] Global mesh: {RoomMesh.vertexCount} verts, {RoomMesh.triangles.Length / 3} tris " +
            $"(object='{best.name}', nameMatch={bestNameMatch})");

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

        Debug.Log($"[KFGMP] Wrote {path}");
    }
}