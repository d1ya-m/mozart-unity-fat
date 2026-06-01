using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arcor2.ClientSdk.ClientServices.Enums;
using UnityEngine;

/// <summary>
/// Loads and rebuilds the scene-linked background mesh from the external mesh server.
/// </summary>
public class BackgroundMesh : MonoBehaviour
{
    [SerializeField] private CommunicationManager communicationManager;
    [SerializeField] private string sceneIdOverride;
    [SerializeField] private bool autoLoadOnStart = true;

    private MeshDownloadManager meshDownloadManager;
    private GameObject loadedMesh;

    public event Action<string, List<MeshDownloadManager.AvailableMeshInfo>> BindingMissing;

    public string CurrentSceneId => ResolveSceneId();
    public Transform LoadedMeshTransform => loadedMesh != null ? loadedMesh.transform : null;

    private void Start()
    {
        meshDownloadManager = MeshDownloadManager.Instance;
        if (communicationManager == null)
        {
            communicationManager = CommunicationManager.Instance;
        }

        if (meshDownloadManager != null)
        {
            meshDownloadManager.SceneBindingMissing += OnSceneBindingMissing;
        }

        if (autoLoadOnStart)
        {
            _ = LoadMeshAsync();
        }
    }

    private void OnDestroy()
    {
        if (meshDownloadManager != null)
        {
            meshDownloadManager.SceneBindingMissing -= OnSceneBindingMissing;
        }

        if (loadedMesh != null)
        {
            Destroy(loadedMesh);
        }
    }

    public async Task<bool> LoadMeshAsync()
    {
        if (meshDownloadManager == null)
        {
            meshDownloadManager = MeshDownloadManager.Instance;
        }

        string sceneId = ResolveSceneId();
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            Debug.LogWarning("[BackgroundMesh] Scene ID is not available yet.");
            return false;
        }

        GameObject newMesh = await meshDownloadManager.LoadSceneMeshAsync(sceneId);
        if (newMesh == null)
        {
            return false;
        }

        AttachLoadedMesh(newMesh);
        Debug.Log($"[BackgroundMesh] Scene mesh loaded for {sceneId}");
        return true;
    }

    public async Task<List<MeshDownloadManager.AvailableMeshInfo>> GetAvailableMeshesAsync()
    {
        if (meshDownloadManager == null)
        {
            meshDownloadManager = MeshDownloadManager.Instance;
        }

        return await meshDownloadManager.GetAvailableMeshesAsync();
    }

    public async Task<bool> BindSceneToMeshAsync(string meshId)
    {
        if (meshDownloadManager == null)
        {
            meshDownloadManager = MeshDownloadManager.Instance;
        }

        string sceneId = ResolveSceneId();
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return false;
        }

        var binding = await meshDownloadManager.BindSceneMeshWithDefaultFallbackAsync(sceneId, meshId);
        if (binding == null || !binding.Bound)
        {
            return false;
        }

        return await LoadMeshAsync();
    }

    public async Task<bool> ReloadMeshAsync()
    {
        return await LoadMeshAsync();
    }

    public async Task<bool> RebuildFromCollisionBoxesAsync(IReadOnlyList<Transform> boxTransforms)
    {
        if (meshDownloadManager == null)
        {
            meshDownloadManager = MeshDownloadManager.Instance;
        }

        string sceneId = ResolveSceneId();
        if (string.IsNullOrWhiteSpace(sceneId) || loadedMesh == null)
        {
            return false;
        }

        GameObject rebuiltMesh = await meshDownloadManager.RebuildSceneMeshFromBoxesAsync(sceneId, boxTransforms, loadedMesh.transform);
        if (rebuiltMesh == null)
        {
            return false;
        }

        AttachLoadedMesh(rebuiltMesh);
        return true;
    }

    private void AttachLoadedMesh(GameObject newMesh)
    {
        loadedMesh = newMesh;
        loadedMesh.transform.SetParent(transform, false);
        loadedMesh.transform.localPosition = Vector3.zero;
        loadedMesh.transform.localRotation = Quaternion.identity;
        loadedMesh.transform.localScale = Vector3.one;
    }

    private void OnSceneBindingMissing(string sceneId, List<MeshDownloadManager.AvailableMeshInfo> availableMeshes)
    {
        if (!string.Equals(sceneId, ResolveSceneId(), StringComparison.Ordinal))
        {
            return;
        }

        BindingMissing?.Invoke(sceneId, availableMeshes);
    }

    private string ResolveSceneId()
    {
        if (!string.IsNullOrWhiteSpace(sceneIdOverride))
        {
            return sceneIdOverride;
        }

        if (communicationManager == null)
        {
            communicationManager = CommunicationManager.Instance;
        }

        return communicationManager?.Arcor2Session?.NavigationId;
    }
}
