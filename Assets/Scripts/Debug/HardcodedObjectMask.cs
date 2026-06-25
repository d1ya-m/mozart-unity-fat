using System.IO;
using UnityEngine;

public class HardcodedObjectMask : MonoBehaviour
{
    [SerializeField] private string objRelativePath = "objectmask/object.obj";
    [SerializeField] private Material stencilMaskMaterial;
    [SerializeField] private int portalMaskLayer = 9;

    private bool _started;

    private void OnEnable()
    {
        var mdm = MeshDownloadManager.Instance;
        if (mdm != null) mdm.MeshLoaded += OnMeshLoaded;
    }

    private void OnDisable()
    {
        var mdm = MeshDownloadManager.Instance;
        if (mdm != null) mdm.MeshLoaded -= OnMeshLoaded;
    }

    private async void OnMeshLoaded(string key, GameObject sceneMesh)
    {
        if (_started || key == "object_mask" || sceneMesh == null) return;
        _started = true;

        string path = Path.Combine(Application.streamingAssetsPath, objRelativePath);
        if (!path.Contains("://")) path = "file://" + path;

        GameObject obj = await MeshDownloadManager.Instance.LoadMeshFromServer("object_mask", path);
        if (obj == null) { Debug.LogError("[HardcodedObjectMask] Failed to load object.obj"); return; }

        obj.transform.SetParent(sceneMesh.transform, false);
        obj.transform.localPosition = Vector3.zero;
        obj.transform.localRotation = Quaternion.identity;
        obj.transform.localScale = Vector3.one;

        var r = obj.GetComponent<MeshRenderer>();
        if (r != null && stencilMaskMaterial != null) r.material = stencilMaskMaterial;

        var col = obj.GetComponent<Collider>();
        if (col != null) col.enabled = false;

        SetLayerRecursively(obj, portalMaskLayer);
        Debug.Log("[HardcodedObjectMask] Object mask loaded and applied.");
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) SetLayerRecursively(c.gameObject, layer);
    }
}
