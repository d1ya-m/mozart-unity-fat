using Meta.XR.MRUtilityKit;
using UnityEngine;

public class LayerApplier : MonoBehaviour
{
    public void GetRoomObjectAndApplyLayer()
    {
        MRUKRoom mrukComponent = FindFirstObjectByType<MRUKRoom>();
        GameObject mrukObject = mrukComponent.gameObject;

        ApplyLayer(mrukObject, "portalContent");
    }

    private void ApplyLayer(GameObject obj, string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        obj.layer = layer;

        foreach (Transform child in obj.transform) ApplyLayer(child.gameObject, layerName);
    }
}
