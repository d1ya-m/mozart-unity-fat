using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Example usage of the dynamic mesh system.
/// Shows how to integrate mesh downloading into your scene workflow.
/// </summary>
public class MeshUsageExample : MonoBehaviour
{
    [SerializeField] private string roomMeshName = "lab_mesh";
    [SerializeField] private string factoryMeshName = "factory";
    
    private MeshDownloadManager meshManager;

    private void Start()
    {
        meshManager = MeshDownloadManager.Instance;
        
        // Subscribe to mesh events
        meshManager.MeshLoaded += OnMeshLoaded;
        meshManager.MeshLoadFailed += OnMeshLoadFailed;
        
        // Example: Load multiple meshes
        LoadMeshesAsync();
    }

    /// <summary>
    /// Example 1: Load a single mesh and display it
    /// </summary>
    private async Task LoadMeshesAsync()
    {
        Debug.Log("Starting mesh loading...");
        
        // Load lab mesh
        GameObject labMesh = await meshManager.LoadMeshFromServer(roomMeshName);
        if (labMesh != null)
        {
            labMesh.transform.position = Vector3.zero;
            Debug.Log("Lab mesh loaded successfully");
        }
        
        // Load factory mesh (with custom server URL)
        GameObject factoryMesh = await meshManager.LoadMeshFromServer(
            factoryMeshName,
            "http://custom-server:8080/mesh/factory.obj"
        );
    }

    /// <summary>
    /// Example 2: Delete a mesh part and notify server
    /// </summary>
    public async void DeleteMeshPartExample()
    {
        // Delete a specific part
        await meshManager.DeleteMeshPart(roomMeshName, "wall_section_01");
        
        // Check remaining parts
        List<MeshPart> remainingParts = meshManager.GetMeshParts(roomMeshName);
        Debug.Log($"Remaining mesh parts: {remainingParts.Count}");
    }

    /// <summary>
    /// Example 3: Interact with mesh parts (show/hide)
    /// </summary>
    public void ToggleMeshPartVisibility(string partName, bool visible)
    {
        var parts = meshManager.GetMeshParts(roomMeshName);
        var part = parts.Find(p => p.Name == partName);
        
        if (part != null)
        {
            if (visible)
                part.Show();
            else
                part.Hide();
        }
    }

    /// <summary>
    /// Example 4: Register custom mesh parts during dynamic creation
    /// </summary>
    public void CreateAndRegisterCustomMeshPart()
    {
        // Create a custom mesh part (e.g., wall section)
        GameObject wallSection = new GameObject("custom_wall_01");
        Mesh customMesh = CreateSimpleMesh();
        
        MeshFilter filter = wallSection.AddComponent<MeshFilter>();
        filter.mesh = customMesh;
        
        MeshRenderer renderer = wallSection.AddComponent<MeshRenderer>();
        renderer.material = new Material(Shader.Find("Standard"));
        
        // Register it for tracking
        meshManager.RegisterMeshPart(roomMeshName, "custom_wall_01", wallSection);
    }

    /// <summary>
    /// Example 5: Reload mesh if it was updated on server
    /// </summary>
    public async void ReloadMeshFromServer()
    {
        Debug.Log("Reloading mesh from server...");
        
        // Clear old mesh
        var oldMeshes = meshManager.GetLoadedMeshes();
        if (oldMeshes.ContainsKey(roomMeshName))
        {
            Destroy(oldMeshes[roomMeshName]);
        }
        
        // Reload
        GameObject updatedMesh = await meshManager.LoadMeshFromServer(roomMeshName);
        if (updatedMesh != null)
        {
            Debug.Log("Mesh reloaded successfully");
        }
    }

    /// <summary>
    /// Event handler: Mesh loaded successfully
    /// </summary>
    private void OnMeshLoaded(string meshName, GameObject meshObject)
    {
        Debug.Log($"✓ Mesh loaded: {meshName}");
        
        // Apply materials, physics, or other setup here
        ApplyMeshMaterials(meshObject);
    }

    /// <summary>
    /// Event handler: Mesh failed to load
    /// </summary>
    private void OnMeshLoadFailed(string meshName, string error)
    {
        Debug.LogError($"✗ Failed to load mesh {meshName}: {error}");
        
        // Show error UI or fallback
        ShowErrorUI(meshName, error);
    }

    /// <summary>
    /// Apply materials and settings to loaded mesh
    /// </summary>
    private void ApplyMeshMaterials(GameObject meshObject)
    {
        var renderers = meshObject.GetComponentsInChildren<MeshRenderer>();
        
        foreach (var renderer in renderers)
        {
            // Apply environment shader
            Material envMaterial = new Material(Shader.Find("Standard"));
            envMaterial.color = Color.white;
            renderer.material = envMaterial;
            
            // Enable shadows
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }
        
        // Add collider if needed
        if (!meshObject.GetComponent<Collider>())
        {
            MeshCollider collider = meshObject.AddComponent<MeshCollider>();
            collider.convex = false;
        }
    }

    /// <summary>
    /// Show error UI (placeholder)
    /// </summary>
    private void ShowErrorUI(string meshName, string error)
    {
        // TODO: Display error message to user
        Debug.LogWarning($"Please show error UI: {meshName} - {error}");
    }

    /// <summary>
    /// Helper: Create a simple test mesh
    /// </summary>
    private Mesh CreateSimpleMesh()
    {
        Mesh mesh = new Mesh();
        
        mesh.vertices = new Vector3[]
        {
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(1, 1, 0),
            new Vector3(0, 1, 0)
        };
        
        mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateNormals();
        
        return mesh;
    }

    private void OnDestroy()
    {
        if (meshManager != null)
        {
            meshManager.MeshLoaded -= OnMeshLoaded;
            meshManager.MeshLoadFailed -= OnMeshLoadFailed;
        }
    }
}
