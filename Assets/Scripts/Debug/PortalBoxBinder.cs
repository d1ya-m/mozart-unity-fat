using UnityEngine;

// Attach this to the portal window mask object (the invisible cube that uses
// StencilMask material). Each frame it sends the cube's position, rotation and
// size to PortalContentUnlit.shader so the shader can ray-cast against it.
[RequireComponent(typeof(Renderer))]
public class PortalBoxBinder : MonoBehaviour
{
    static readonly int WorldToLocalID = Shader.PropertyToID("_PortalWorldToLocal");
    static readonly int CenterID       = Shader.PropertyToID("_PortalBoxCenter");
    static readonly int ExtentsID      = Shader.PropertyToID("_PortalBoxExtents");

    Renderer rend;

    void OnEnable()
    {
        rend = GetComponent<Renderer>();
    }

    void LateUpdate()
    {
        if (rend == null) return;
        Bounds local = rend.localBounds;   // mesh AABB in the cube's local space
        Shader.SetGlobalMatrix(WorldToLocalID, transform.worldToLocalMatrix);
        Shader.SetGlobalVector(CenterID, local.center);
        Shader.SetGlobalVector(ExtentsID, local.extents);
    }
}
