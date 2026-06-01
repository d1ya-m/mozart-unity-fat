using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Downloads, caches, and rebuilds scene meshes from the external mesh server.
/// </summary>
public class MeshDownloadManager : Singleton<MeshDownloadManager>
{
    public const string DefaultSceneMeshId = "lab";

    [SerializeField] private string meshServerBaseUrl = "http://butcluster.ddns.net:8000";
    [SerializeField] private bool flipObjXAxisForUnity = true;

    private readonly Dictionary<string, GameObject> loadedMeshes = new();
    private readonly Dictionary<string, List<MeshPart>> meshParts = new();
    private readonly Dictionary<string, SceneMeshState> sceneStates = new();

    public event Action<string, GameObject> MeshLoaded;
    public event Action<string, string> MeshLoadFailed;
    public event Action<string, List<AvailableMeshInfo>> SceneBindingMissing;

    public string MeshServerBaseUrl => meshServerBaseUrl;

    public async Task<GameObject> LoadSceneMeshAsync(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            Debug.LogError("[MeshDownloadManager] Scene ID is missing.");
            return null;
        }

        try
        {
            var binding = await GetSceneBindingAsync(sceneId);
            if (binding == null)
            {
                throw new Exception("Binding response is null.");
            }

            binding = await EnsureSceneBindingAsync(sceneId, binding, notifyWhenMissing: true);
            if (binding == null)
            {
                return null;
            }

            var state = GetOrCreateSceneState(sceneId);
            state.SceneId = sceneId;
            state.CurrentRevision = binding.CurrentRevision;
            state.ETag = binding.ETag;
            state.DownloadUrl = ToAbsoluteUrl(binding.DownloadUrl);
            state.SourceMeshId = binding.SourceMeshId;
            state.RelativePosition = binding.GetRelativePositionOrDefault();
            state.RelativeRotation = binding.GetRelativeRotationOrDefault();

            string localObjPath = await EnsureCurrentAssetCachedAsync(sceneId, binding);
            if (string.IsNullOrWhiteSpace(localObjPath) || !File.Exists(localObjPath))
            {
                throw new Exception("Current mesh asset is not cached locally.");
            }

            byte[] objBytes = await File.ReadAllBytesAsync(localObjPath);
            string objUrl = new Uri(localObjPath).AbsoluteUri;
            GameObject meshObject = await LoadMeshFromBytes(sceneId, objBytes, objUrl);
            if (meshObject == null)
            {
                throw new Exception("Failed to load OBJ asset from cache.");
            }

            ReplaceLoadedMesh(sceneId, meshObject);
            state.LocalObjPath = localObjPath;

            MeshLoaded?.Invoke(sceneId, meshObject);
            return meshObject;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MeshDownloadManager] Failed to load scene mesh '{sceneId}': {ex.Message}");
            MeshLoadFailed?.Invoke(sceneId, ex.Message);
            return null;
        }
    }

    public async Task<List<AvailableMeshInfo>> GetAvailableMeshesAsync()
    {
        string endpoint = $"{GetMeshServerBaseUrl().TrimEnd('/')}/v1/meshes";
        var response = await SendJsonRequestAsync(endpoint, UnityWebRequest.kHttpVerbGET);
        if (response == null || string.IsNullOrWhiteSpace(response.Text))
        {
            return new List<AvailableMeshInfo>();
        }

        var root = JObject.Parse(response.Text);
        return root["items"]?.ToObject<List<AvailableMeshInfo>>() ?? new List<AvailableMeshInfo>();
    }

    public async Task<SceneBindingInfo> BindSceneMeshAsync(string sceneId, string meshId)
    {
        string endpoint = $"{GetMeshServerBaseUrl().TrimEnd('/')}/v1/scenes/{sceneId}/bind-mesh";
        var payload = new JObject
        {
            ["mesh_id"] = meshId
        }.ToString(Formatting.None);

        var response = await SendJsonRequestAsync(endpoint, UnityWebRequest.kHttpVerbPOST, payload);
        if (response == null || string.IsNullOrWhiteSpace(response.Text))
        {
            return null;
        }

        return JsonConvert.DeserializeObject<SceneBindingInfo>(response.Text);
    }

    public async Task<SceneBindingInfo> BindSceneMeshWithDefaultFallbackAsync(string sceneId, string meshId)
    {
        var binding = await BindSceneMeshAsync(sceneId, meshId);
        if (binding != null && binding.Bound)
        {
            return binding;
        }

        Debug.LogWarning($"[MeshDownloadManager] Scene mesh bind failed for '{meshId}', falling back to '{DefaultSceneMeshId}'.");
        return await BindSceneMeshAsync(sceneId, DefaultSceneMeshId);
    }

    public async Task<GameObject> RebuildSceneMeshFromBoxesAsync(string sceneId, IReadOnlyList<Transform> boxTransforms, Transform meshTransform)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            Debug.LogError("[MeshDownloadManager] Cannot rebuild mesh without scene ID.");
            return null;
        }

        if (meshTransform == null)
        {
            Debug.LogError("[MeshDownloadManager] Cannot rebuild mesh without loaded mesh transform.");
            return null;
        }

        var binding = await GetSceneBindingAsync(sceneId);
        binding = await EnsureSceneBindingAsync(sceneId, binding, notifyWhenMissing: false);
        if (binding == null)
        {
            Debug.LogWarning($"[MeshDownloadManager] Scene '{sceneId}' has no mesh binding.");
            return null;
        }

        string endpoint = $"{GetMeshServerBaseUrl().TrimEnd('/')}/v1/scenes/{sceneId}/rebuild-from-boxes";
        string payload = BuildRebuildRequestJson(binding.CurrentRevision, boxTransforms, meshTransform);
        var response = await SendJsonRequestAsync(endpoint, UnityWebRequest.kHttpVerbPOST, payload);
        if (response == null || string.IsNullOrWhiteSpace(response.Text))
        {
            return null;
        }

        var rebuildResponse = JsonConvert.DeserializeObject<RebuildFromBoxesResponse>(response.Text);
        if (rebuildResponse == null)
        {
            return null;
        }

        var state = GetOrCreateSceneState(sceneId);
        state.CurrentRevision = rebuildResponse.NewRevision;
        state.ETag = rebuildResponse.ETag;
        state.DownloadUrl = ToAbsoluteUrl(rebuildResponse.DownloadUrl);

        return await LoadSceneMeshAsync(sceneId);
    }

    public async Task<SceneBindingInfo> GetSceneBindingAsync(string sceneId)
    {
        string endpoint = $"{GetMeshServerBaseUrl().TrimEnd('/')}/v1/scenes/{sceneId}/binding";
        var response = await SendJsonRequestAsync(endpoint, UnityWebRequest.kHttpVerbGET);
        if (response == null || string.IsNullOrWhiteSpace(response.Text))
        {
            return null;
        }

        return JsonConvert.DeserializeObject<SceneBindingInfo>(response.Text);
    }

    private async Task<SceneBindingInfo> EnsureSceneBindingAsync(string sceneId, SceneBindingInfo binding, bool notifyWhenMissing)
    {
        if (binding != null && binding.Bound)
        {
            return binding;
        }

        Debug.LogWarning($"[MeshDownloadManager] Scene '{sceneId}' has no mesh binding, falling back to '{DefaultSceneMeshId}'.");
        binding = await BindSceneMeshAsync(sceneId, DefaultSceneMeshId);
        if (binding != null && binding.Bound)
        {
            return binding;
        }

        if (notifyWhenMissing)
        {
            var availableMeshes = await GetAvailableMeshesAsync();
            SceneBindingMissing?.Invoke(sceneId, availableMeshes);
            MeshLoadFailed?.Invoke(sceneId, "Scene mesh binding missing");
        }

        return null;
    }

    public bool TryGetCachedSceneTransform(string sceneId, out Vector3 relativePosition, out Quaternion relativeRotation)
    {
        if (sceneStates.TryGetValue(sceneId, out var state))
        {
            relativePosition = state.RelativePosition;
            relativeRotation = state.RelativeRotation;
            return true;
        }

        relativePosition = Vector3.zero;
        relativeRotation = Quaternion.identity;
        return false;
    }

    public async Task<SceneMeshTransformInfo> UpdateSceneMeshTransformAsync(string sceneId, Vector3 relativePosition, Quaternion relativeRotation)
    {
        string endpoint = $"{GetMeshServerBaseUrl().TrimEnd('/')}/v1/scenes/{sceneId}/mesh-transform";
        var payload = new JObject
        {
            ["relative_position"] = new JArray(relativePosition.x, relativePosition.y, relativePosition.z),
            ["relative_rotation_quat_xyzw"] = new JArray(relativeRotation.x, relativeRotation.y, relativeRotation.z, relativeRotation.w)
        }.ToString(Formatting.None);

        var response = await SendJsonRequestAsync(endpoint, UnityWebRequest.kHttpVerbPUT, payload);
        if (response == null || string.IsNullOrWhiteSpace(response.Text))
        {
            return null;
        }

        var transformInfo = JsonConvert.DeserializeObject<SceneMeshTransformInfo>(response.Text);
        if (transformInfo == null)
        {
            return null;
        }

        var state = GetOrCreateSceneState(sceneId);
        state.RelativePosition = transformInfo.GetRelativePositionOrDefault();
        state.RelativeRotation = transformInfo.GetRelativeRotationOrDefault();
        return transformInfo;
    }

    /// <summary>
    /// Legacy direct download by URL. Kept for existing code paths outside scene-based loading.
    /// </summary>
    public async Task<GameObject> LoadMeshFromServer(string meshName, string serverUrl = null)
    {
        if (loadedMeshes.ContainsKey(meshName))
        {
            Debug.LogWarning($"Mesh '{meshName}' already loaded");
            return loadedMeshes[meshName];
        }

        try
        {
            string downloadUrl = serverUrl ?? $"http://butcluster.ddns.net:6789/mesh/{meshName}";
            byte[] meshData = await DownloadBytesAsync(downloadUrl);

            if (meshData == null || meshData.Length == 0)
            {
                throw new Exception("Downloaded mesh data is empty");
            }

            GameObject meshObject = await LoadMeshFromBytes(meshName, meshData, downloadUrl);
            if (meshObject == null)
            {
                throw new Exception("Failed to load mesh from bytes");
            }

            ReplaceLoadedMesh(meshName, meshObject);
            MeshLoaded?.Invoke(meshName, meshObject);
            return meshObject;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MeshDownloadManager] Failed to load mesh '{meshName}': {ex.Message}");
            MeshLoadFailed?.Invoke(meshName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Legacy single-box cut against uploaded mesh bytes. Kept for compatibility.
    /// </summary>
    public async Task<GameObject> CutLoadedMeshByBoxAsync(string meshName, Transform boxTransform)
    {
        Debug.LogWarning("[MeshDownloadManager] CutLoadedMeshByBoxAsync is legacy. Prefer scene-based rebuild-from-boxes.");
        await Task.Yield();
        return loadedMeshes.TryGetValue(meshName, out var meshObject) ? meshObject : null;
    }

    public void RegisterMeshPart(string meshName, string partName, GameObject partObject)
    {
        if (!meshParts.ContainsKey(meshName))
        {
            meshParts[meshName] = new List<MeshPart>();
        }

        meshParts[meshName].Add(new MeshPart
        {
            Name = partName,
            GameObject = partObject,
            Renderer = partObject.GetComponent<MeshRenderer>(),
            CreatedAt = DateTime.Now
        });
    }

    public async Task DeleteMeshPart(string meshName, string partName)
    {
        if (!meshParts.ContainsKey(meshName))
        {
            Debug.LogWarning($"Mesh '{meshName}' not found");
            return;
        }

        var partList = meshParts[meshName];
        var part = partList.Find(p => p.Name == partName);
        if (part == null)
        {
            Debug.LogWarning($"Part '{partName}' not found in mesh '{meshName}'");
            return;
        }

        if (part.GameObject != null)
        {
            Destroy(part.GameObject);
        }

        partList.Remove(part);
        await Task.Yield();
    }

    public void ClearAllMeshes()
    {
        foreach (var meshEntry in loadedMeshes)
        {
            if (meshEntry.Value != null)
            {
                Destroy(meshEntry.Value);
            }
        }

        loadedMeshes.Clear();
        meshParts.Clear();
        sceneStates.Clear();
    }

    public Dictionary<string, GameObject> GetLoadedMeshes() => loadedMeshes;

    public List<MeshPart> GetMeshParts(string meshName)
    {
        return meshParts.ContainsKey(meshName) ? meshParts[meshName] : new List<MeshPart>();
    }

    private SceneMeshState GetOrCreateSceneState(string sceneId)
    {
        if (!sceneStates.TryGetValue(sceneId, out var state))
        {
            state = new SceneMeshState { SceneId = sceneId };
            sceneStates[sceneId] = state;
        }

        return state;
    }

    private void ReplaceLoadedMesh(string key, GameObject meshObject)
    {
        if (loadedMeshes.TryGetValue(key, out var existingMesh) && existingMesh != null)
        {
            Destroy(existingMesh);
        }

        loadedMeshes[key] = meshObject;
        if (!meshParts.ContainsKey(key))
        {
            meshParts[key] = new List<MeshPart>();
        }
    }

    private async Task<string> EnsureCurrentAssetCachedAsync(string sceneId, SceneBindingInfo binding)
    {
        string sceneCacheDir = GetSceneCacheDirectory(sceneId);
        string assetDir = Path.Combine(sceneCacheDir, "current");
        string metaPath = Path.Combine(sceneCacheDir, "cache-meta.json");
        Directory.CreateDirectory(assetDir);

        CacheMetadata cacheMeta = LoadCacheMetadata(metaPath);
        string entryUrl = ToAbsoluteUrl(binding.DownloadUrl);
        Debug.Log($"[MeshDownloadManager] Caching mesh for scene '{sceneId}' from {entryUrl}");
        string localObjPath = Path.Combine(assetDir, Path.GetFileName(new Uri(entryUrl).LocalPath));

        using var request = UnityWebRequest.Get(entryUrl);
        if (cacheMeta != null &&
            !string.IsNullOrWhiteSpace(cacheMeta.ETag) &&
            cacheMeta.Revision == binding.CurrentRevision &&
            File.Exists(localObjPath))
        {
            request.SetRequestHeader("If-None-Match", cacheMeta.ETag);
        }

        var asyncOp = request.SendWebRequest();
        while (!asyncOp.isDone)
        {
            await Task.Yield();
        }

        if (request.responseCode == 304 && File.Exists(localObjPath))
        {
            Debug.Log($"[MeshDownloadManager] Using cached OBJ for scene '{sceneId}': {localObjPath}");
            return localObjPath;
        }

        if (request.result != UnityWebRequest.Result.Success)
        {
            throw new Exception($"Failed to download current OBJ asset: {request.error}");
        }

        byte[] objBytes = request.downloadHandler.data;
        await File.WriteAllBytesAsync(localObjPath, objBytes);
        Debug.Log($"[MeshDownloadManager] Downloaded OBJ for scene '{sceneId}' to {localObjPath} ({objBytes?.Length ?? 0} bytes)");

        string objContent = Encoding.UTF8.GetString(objBytes);
        string mtlFileName = ExtractMtlFileName(objContent);
        if (!string.IsNullOrWhiteSpace(mtlFileName))
        {
            string mtlUrl = new Uri(new Uri(entryUrl), mtlFileName).AbsoluteUri;
            string localMtlPath = Path.Combine(assetDir, mtlFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(localMtlPath) ?? assetDir);

            byte[] mtlBytes = await DownloadBytesAsync(mtlUrl);
            if (mtlBytes != null && mtlBytes.Length > 0)
            {
                await File.WriteAllBytesAsync(localMtlPath, mtlBytes);

                string textureFileName = ParseMtlForMapKd(Encoding.UTF8.GetString(mtlBytes));
                if (!string.IsNullOrWhiteSpace(textureFileName))
                {
                    string textureUrl = new Uri(new Uri(mtlUrl), textureFileName).AbsoluteUri;
                    string localTexturePath = Path.Combine(assetDir, textureFileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(localTexturePath) ?? assetDir);

                    byte[] textureBytes = await DownloadBytesAsync(textureUrl);
                    if (textureBytes != null && textureBytes.Length > 0)
                    {
                        await File.WriteAllBytesAsync(localTexturePath, textureBytes);
                    }
                }
            }
        }

        string etag = request.GetResponseHeader("ETag");
        var newMeta = new CacheMetadata
        {
            SceneId = sceneId,
            Revision = binding.CurrentRevision,
            ETag = string.IsNullOrWhiteSpace(etag) ? binding.ETag : etag,
            LocalObjPath = localObjPath
        };
        await File.WriteAllTextAsync(metaPath, JsonConvert.SerializeObject(newMeta));

        return localObjPath;
    }

    private static CacheMetadata LoadCacheMetadata(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<CacheMetadata>(File.ReadAllText(metaPath));
        }
        catch
        {
            return null;
        }
    }

    private string GetSceneCacheDirectory(string sceneId)
    {
        return Path.Combine(Application.persistentDataPath, "scene-mesh-cache", sceneId);
    }

    private async Task<HttpResponseData> SendJsonRequestAsync(string url, string method, string jsonBody = null)
    {
        Debug.Log($"[MeshDownloadManager] Sending {method} request to {url}");
        using var request = new UnityWebRequest(url, method);
        request.downloadHandler = new DownloadHandlerBuffer();

        if (!string.IsNullOrWhiteSpace(jsonBody))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.SetRequestHeader("Content-Type", "application/json");
        }

        var asyncOp = request.SendWebRequest();
        while (!asyncOp.isDone)
        {
            await Task.Yield();
        }

        if (request.result == UnityWebRequest.Result.Success || request.responseCode == 304)
        {
            Debug.Log($"[MeshDownloadManager] Request succeeded ({method} {url}) -> {request.responseCode}");
            return new HttpResponseData
            {
                StatusCode = request.responseCode,
                Text = request.downloadHandler?.text,
                ETag = request.GetResponseHeader("ETag")
            };
        }

        string errorBody = request.downloadHandler?.text;
        Debug.LogError($"[MeshDownloadManager] Request failed ({method} {url}): {request.responseCode} {request.error} {errorBody}");
        return null;
    }

    private async Task<byte[]> DownloadBytesAsync(string url)
    {
        Debug.Log($"[MeshDownloadManager] Downloading bytes from {url}");
        using var request = UnityWebRequest.Get(url);
        var asyncOp = request.SendWebRequest();
        while (!asyncOp.isDone)
        {
            await Task.Yield();
        }

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[MeshDownloadManager] Download failed: {url} -> {request.error}");
            return null;
        }

        return request.downloadHandler.data;
    }

    private async Task<GameObject> LoadMeshFromBytes(string meshName, byte[] meshData, string sourceUrl)
    {
        string extension = Path.GetExtension(meshName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".obj";
        }

        switch (extension)
        {
            case ".obj":
                return await LoadObjMesh(meshName, meshData, sourceUrl);
            default:
                throw new Exception($"Unsupported runtime mesh format: {extension}");
        }
    }

    private async Task<GameObject> LoadObjMesh(string meshName, byte[] meshData, string sourceUrl)
    {
        try
        {
            string objContent = Encoding.UTF8.GetString(meshData);
            var (vertices, triangles, normals, uvs) = ParseObjContent(objContent, flipObjXAxisForUnity);

            Texture2D diffuseTexture = null;
            string mtlFileName = ExtractMtlFileName(objContent);
            if (!string.IsNullOrWhiteSpace(mtlFileName) && !string.IsNullOrWhiteSpace(sourceUrl))
            {
                try
                {
                    string mtlUrl = new Uri(new Uri(sourceUrl), mtlFileName).AbsoluteUri;
                    byte[] mtlData = await LoadBytesFromSourceAsync(mtlUrl);
                    if (mtlData != null && mtlData.Length > 0)
                    {
                        string textureFileName = ParseMtlForMapKd(Encoding.UTF8.GetString(mtlData));
                        if (!string.IsNullOrWhiteSpace(textureFileName))
                        {
                            string textureUrl = new Uri(new Uri(mtlUrl), textureFileName).AbsoluteUri;
                            byte[] textureData = await LoadBytesFromSourceAsync(textureUrl);
                            if (textureData != null && textureData.Length > 0)
                            {
                                diffuseTexture = new Texture2D(2, 2);
                                diffuseTexture.LoadImage(textureData);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MeshDownloadManager] Failed to load MTL/texture: {ex.Message}");
                }
            }

            Mesh mesh = new Mesh
            {
                name = meshName
            };

            // Large room scans commonly exceed 65k vertices, which requires a 32-bit index buffer.
            if (vertices.Length > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            if (normals.Length > 0)
            {
                mesh.normals = normals;
            }
            else
            {
                mesh.RecalculateNormals();
            }

            if (uvs.Length > 0)
            {
                mesh.uv = uvs;
            }

            GameObject meshObject = new GameObject(meshName);
            MeshFilter meshFilter = meshObject.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;

            MeshRenderer renderer = meshObject.AddComponent<MeshRenderer>();
            Material material = CreateRuntimeMeshMaterial(diffuseTexture);
            renderer.material = material;

            MeshCollider collider = meshObject.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = false;

            return meshObject;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MeshDownloadManager] Failed to parse OBJ: {ex.Message}");
            return null;
        }
    }

    private static string ExtractMtlFileName(string objContent)
    {
        foreach (string line in objContent.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(7).Trim();
            }
        }

        return null;
    }

    private string ParseMtlForMapKd(string mtlContent)
    {
        foreach (string line in mtlContent.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("map_Kd ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    return parts[1].Trim();
                }
            }
        }

        return null;
    }

    private static (Vector3[], int[], Vector3[], Vector2[]) ParseObjContent(string objContent, bool flipXAxisForUnity)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvCoords = new List<Vector2>();

        var meshVertices = new List<Vector3>();
        var meshNormals = new List<Vector3>();
        var meshUvs = new List<Vector2>();
        var triangles = new List<int>();
        var vertexMap = new Dictionary<string, int>();

        string[] lines = objContent.Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
            {
                continue;
            }

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "v":
                    if (parts.Length >= 4)
                    {
                        positions.Add(ConvertObjVectorToUnity(new Vector3(
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)),
                            flipXAxisForUnity));
                    }
                    break;
                case "vn":
                    if (parts.Length >= 4)
                    {
                        normals.Add(ConvertObjVectorToUnity(new Vector3(
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)),
                            flipXAxisForUnity));
                    }
                    break;
                case "vt":
                    if (parts.Length >= 3)
                    {
                        uvCoords.Add(new Vector2(
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    break;
                case "f":
                    ParseFace(parts, positions, normals, uvCoords, meshVertices, meshNormals, meshUvs, triangles, vertexMap, flipXAxisForUnity);
                    break;
            }
        }

        return (
            meshVertices.ToArray(),
            triangles.ToArray(),
            meshNormals.Count == meshVertices.Count ? meshNormals.ToArray() : Array.Empty<Vector3>(),
            meshUvs.Count == meshVertices.Count ? meshUvs.ToArray() : Array.Empty<Vector2>());
    }

    private static void ParseFace(
        string[] parts,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector2> uvCoords,
        List<Vector3> meshVertices,
        List<Vector3> meshNormals,
        List<Vector2> meshUvs,
        List<int> triangles,
        Dictionary<string, int> vertexMap,
        bool reverseWinding)
    {
        var faceIndices = new List<int>();
        for (int i = 1; i < parts.Length; i++)
        {
            string token = parts[i];
            if (!vertexMap.TryGetValue(token, out int meshIndex))
            {
                string[] components = token.Split('/');
                int positionIndex = ParseObjIndex(components, 0, positions.Count);
                int uvIndex = ParseObjIndex(components, 1, uvCoords.Count);
                int normalIndex = ParseObjIndex(components, 2, normals.Count);

                meshIndex = meshVertices.Count;
                meshVertices.Add(positionIndex >= 0 ? positions[positionIndex] : Vector3.zero);
                meshUvs.Add(uvIndex >= 0 ? uvCoords[uvIndex] : Vector2.zero);
                meshNormals.Add(normalIndex >= 0 ? normals[normalIndex] : Vector3.zero);
                vertexMap[token] = meshIndex;
            }

            faceIndices.Add(meshIndex);
        }

        for (int i = 1; i < faceIndices.Count - 1; i++)
        {
            if (reverseWinding)
            {
                triangles.Add(faceIndices[0]);
                triangles.Add(faceIndices[i + 1]);
                triangles.Add(faceIndices[i]);
            }
            else
            {
                triangles.Add(faceIndices[0]);
                triangles.Add(faceIndices[i]);
                triangles.Add(faceIndices[i + 1]);
            }
        }
    }

    private static Vector3 ConvertObjVectorToUnity(Vector3 source, bool flipXAxisForUnity)
    {
        if (!flipXAxisForUnity)
        {
            return source;
        }

        return new Vector3(-source.x, source.y, source.z);
    }

    private static int ParseObjIndex(string[] components, int componentIndex, int count)
    {
        if (components.Length <= componentIndex || string.IsNullOrWhiteSpace(components[componentIndex]))
        {
            return -1;
        }

        if (!int.TryParse(components[componentIndex], out int rawIndex))
        {
            return -1;
        }

        if (rawIndex > 0)
        {
            return rawIndex - 1;
        }

        if (rawIndex < 0)
        {
            return count + rawIndex;
        }

        return -1;
    }

    private string BuildRebuildRequestJson(int baseRevision, IReadOnlyList<Transform> boxTransforms, Transform meshTransform)
    {
        var boxesArray = new JArray();
        for (int i = 0; i < boxTransforms.Count; i++)
        {
            Transform boxTransform = boxTransforms[i];
            if (boxTransform == null)
            {
                continue;
            }

            Matrix4x4 relativeMatrix = meshTransform.worldToLocalMatrix * boxTransform.localToWorldMatrix;
            if (flipObjXAxisForUnity)
            {
                // Runtime OBJ import mirrors the original mesh on X to match Unity view space.
                // The cutter service operates on the original OBJ basis, so convert the OBB
                // transform back into that basis before decomposing it to center/rotation/size.
                Matrix4x4 objBasisFlip = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
                relativeMatrix = objBasisFlip * relativeMatrix * objBasisFlip;
            }

            DecomposeBoxRelativeTransform(relativeMatrix, out Vector3 centerLocal, out Quaternion rotationLocal, out Vector3 size);

            boxesArray.Add(new JObject
            {
                ["id"] = boxTransform.name,
                ["center"] = new JArray(centerLocal.x, centerLocal.y, centerLocal.z),
                ["rotation_quat_xyzw"] = new JArray(rotationLocal.x, rotationLocal.y, rotationLocal.z, rotationLocal.w),
                ["size"] = new JArray(size.x, size.y, size.z)
            });
        }

        var root = new JObject
        {
            ["base_revision"] = baseRevision,
            ["boxes"] = boxesArray,
            ["space"] = "mesh_local",
            ["remove_rule"] = "intersects",
            ["weld_vertices"] = true,
            ["recalc_normals"] = false
        };

        return root.ToString(Formatting.None);
    }

    private static void DecomposeBoxRelativeTransform(
        Matrix4x4 relativeMatrix,
        out Vector3 centerLocal,
        out Quaternion rotationLocal,
        out Vector3 sizeLocal)
    {
        centerLocal = relativeMatrix.MultiplyPoint3x4(Vector3.zero);

        Vector3 axisX = new Vector3(relativeMatrix.m00, relativeMatrix.m10, relativeMatrix.m20);
        Vector3 axisY = new Vector3(relativeMatrix.m01, relativeMatrix.m11, relativeMatrix.m21);
        Vector3 axisZ = new Vector3(relativeMatrix.m02, relativeMatrix.m12, relativeMatrix.m22);

        float sizeX = axisX.magnitude;
        float sizeY = axisY.magnitude;
        float sizeZ = axisZ.magnitude;

        if (sizeX < 1e-6f) axisX = Vector3.right;
        else axisX /= sizeX;

        if (sizeY < 1e-6f) axisY = Vector3.up;
        else axisY /= sizeY;

        if (sizeZ < 1e-6f) axisZ = Vector3.forward;
        else axisZ /= sizeZ;

        // Preserve a proper right-handed basis even if some parent in the chain uses mirrored scaling.
        if (Vector3.Dot(Vector3.Cross(axisX, axisY), axisZ) < 0f)
        {
            axisX = -axisX;
        }

        rotationLocal = Quaternion.LookRotation(axisZ, axisY);
        sizeLocal = new Vector3(Mathf.Abs(sizeX), Mathf.Abs(sizeY), Mathf.Abs(sizeZ));
    }

    private string ToAbsoluteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            return absoluteUri.AbsoluteUri;
        }

        string baseUrl = GetMeshServerBaseUrl().TrimEnd('/') + "/";
        string relativeUrl = url.TrimStart('/');
        string resolvedUrl = new Uri(new Uri(baseUrl), relativeUrl).AbsoluteUri;
        Debug.Log($"[MeshDownloadManager] Resolved relative URL '{url}' against '{baseUrl}' -> {resolvedUrl}");
        return resolvedUrl;
    }

    private string GetMeshServerBaseUrl()
    {
        return string.IsNullOrWhiteSpace(meshServerBaseUrl)
            ? "http://butcluster.ddns.net:8000"
            : meshServerBaseUrl;
    }

    private async Task<byte[]> LoadBytesFromSourceAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            string localPath = uri.LocalPath;
            if (!File.Exists(localPath))
            {
                Debug.LogWarning($"[MeshDownloadManager] Local file does not exist: {localPath}");
                return null;
            }

            Debug.Log($"[MeshDownloadManager] Loading local file bytes from {localPath}");
            return await File.ReadAllBytesAsync(localPath);
        }

        return await DownloadBytesAsync(url);
    }

    private static Material CreateRuntimeMeshMaterial(Texture2D diffuseTexture)
    {
        Shader shader = ResolveRuntimeMeshShader();
        Material material = new Material(shader);

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", Color.white);
        }
        else if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", Color.white);
        }

        if (diffuseTexture != null)
        {
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", diffuseTexture);
            }
            else if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", diffuseTexture);
            }

            material.mainTexture = diffuseTexture;
        }

        return material;
    }

    private static Shader ResolveRuntimeMeshShader()
    {
        if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset ||
            GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset)
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit != null)
            {
                return urpLit;
            }
        }

        Shader standard = Shader.Find("Standard");
        if (standard != null)
        {
            return standard;
        }

        Shader fallback = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (fallback != null)
        {
            return fallback;
        }

        throw new Exception("No compatible runtime shader found for dynamically loaded mesh.");
    }

    [Serializable]
    public class AvailableMeshInfo
    {
        [JsonProperty("mesh_id")] public string MeshId;
        [JsonProperty("label")] public string Label;
        [JsonProperty("format")] public string Format;
        [JsonProperty("entry_file")] public string EntryFile;
    }

    [Serializable]
    public class SceneBindingInfo
    {
        [JsonProperty("scene_id")] public string SceneId;
        [JsonProperty("bound")] public bool Bound;
        [JsonProperty("source_mesh_id")] public string SourceMeshId;
        [JsonProperty("current_revision")] public int CurrentRevision;
        [JsonProperty("etag")] public string ETag;
        [JsonProperty("download_url")] public string DownloadUrl;
        [JsonProperty("relative_position")] public float[] RelativePosition;
        [JsonProperty("relative_rotation_quat_xyzw")] public float[] RelativeRotationQuatXyzw;

        public Vector3 GetRelativePositionOrDefault()
        {
            return RelativePosition != null && RelativePosition.Length >= 3
                ? new Vector3(RelativePosition[0], RelativePosition[1], RelativePosition[2])
                : Vector3.zero;
        }

        public Quaternion GetRelativeRotationOrDefault()
        {
            return RelativeRotationQuatXyzw != null && RelativeRotationQuatXyzw.Length >= 4
                ? new Quaternion(RelativeRotationQuatXyzw[0], RelativeRotationQuatXyzw[1], RelativeRotationQuatXyzw[2], RelativeRotationQuatXyzw[3])
                : Quaternion.identity;
        }
    }

    [Serializable]
    public class SceneMeshTransformInfo
    {
        [JsonProperty("scene_id")] public string SceneId;
        [JsonProperty("relative_position")] public float[] RelativePosition;
        [JsonProperty("relative_rotation_quat_xyzw")] public float[] RelativeRotationQuatXyzw;
        [JsonProperty("updated_at")] public string UpdatedAt;

        public Vector3 GetRelativePositionOrDefault()
        {
            return RelativePosition != null && RelativePosition.Length >= 3
                ? new Vector3(RelativePosition[0], RelativePosition[1], RelativePosition[2])
                : Vector3.zero;
        }

        public Quaternion GetRelativeRotationOrDefault()
        {
            return RelativeRotationQuatXyzw != null && RelativeRotationQuatXyzw.Length >= 4
                ? new Quaternion(RelativeRotationQuatXyzw[0], RelativeRotationQuatXyzw[1], RelativeRotationQuatXyzw[2], RelativeRotationQuatXyzw[3])
                : Quaternion.identity;
        }
    }

    [Serializable]
    private class RebuildFromBoxesResponse
    {
        [JsonProperty("new_revision")] public int NewRevision;
        [JsonProperty("etag")] public string ETag;
        [JsonProperty("download_url")] public string DownloadUrl;
    }

    [Serializable]
    private class SceneMeshState
    {
        public string SceneId;
        public string SourceMeshId;
        public int CurrentRevision;
        public string ETag;
        public string DownloadUrl;
        public string LocalObjPath;
        public Vector3 RelativePosition;
        public Quaternion RelativeRotation;
    }

    [Serializable]
    private class CacheMetadata
    {
        public string SceneId;
        public int Revision;
        public string ETag;
        public string LocalObjPath;
    }

    private class HttpResponseData
    {
        public long StatusCode;
        public string Text;
        public string ETag;
    }
}

/// <summary>
/// Represents a tracked part of a mesh for deletion.
/// </summary>
public class MeshPart
{
    public string Name;
    public GameObject GameObject;
    public MeshRenderer Renderer;
    public DateTime CreatedAt;

    public void Hide() => Renderer.enabled = false;
    public void Show() => Renderer.enabled = true;
}
