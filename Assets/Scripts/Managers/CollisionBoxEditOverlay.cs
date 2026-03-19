using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class CollisionBoxEditOverlay : MonoBehaviour
{
    [SerializeField] private Color normalColor = new Color(0.15f, 0.8f, 1f, 0.18f);
    [SerializeField] private Color selectedColor = new Color(1f, 0.55f, 0.1f, 0.32f);
    [SerializeField] private float selectedScaleMultiplier = 1.03f;

    private GameObject _overlayObject;
    private Material _overlayMaterial;
    private EditModeManager _editModeManager;

    private void Awake()
    {
        EnsureOverlay();
        ApplyVisualState(false, false);
    }

    private void OnEnable()
    {
        _editModeManager = EditModeManager.Instance;
        if (_editModeManager != null)
        {
            _editModeManager.EditModeChanged += OnEditModeChanged;
            _editModeManager.SelectedObjectChanged += OnSelectedObjectChanged;
            ApplyCurrentState();
        }
    }

    private void OnDisable()
    {
        if (_editModeManager != null)
        {
            _editModeManager.EditModeChanged -= OnEditModeChanged;
            _editModeManager.SelectedObjectChanged -= OnSelectedObjectChanged;
        }

        ApplyVisualState(false, false);
    }

    private void OnDestroy()
    {
        if (_overlayMaterial != null)
        {
            Destroy(_overlayMaterial);
        }
    }

    private void OnEditModeChanged(bool _)
    {
        ApplyCurrentState();
    }

    private void OnSelectedObjectChanged(GameObject selectedObject)
    {
        ApplyCurrentState(selectedObject);
    }

    private void ApplyCurrentState()
    {
        ApplyCurrentState(_editModeManager != null ? _editModeManager.SelectedObject : null);
    }

    private void ApplyCurrentState(GameObject selectedObject)
    {
        bool isEditMode = _editModeManager != null && _editModeManager.IsEditMode;
        bool isSelected = selectedObject == gameObject;
        ApplyVisualState(isEditMode, isSelected);
    }

    private void ApplyVisualState(bool visible, bool selected)
    {
        EnsureOverlay();
        if (_overlayObject == null || _overlayMaterial == null)
        {
            return;
        }

        _overlayObject.SetActive(visible);
        _overlayMaterial.color = selected ? selectedColor : normalColor;
        _overlayObject.transform.localScale = Vector3.one * (selected ? selectedScaleMultiplier : 1f);
    }

    private void EnsureOverlay()
    {
        if (_overlayObject != null && _overlayMaterial != null)
        {
            return;
        }

        if (_overlayObject == null)
        {
            _overlayObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _overlayObject.name = "EditOverlay";
            _overlayObject.transform.SetParent(transform, false);
            _overlayObject.transform.localPosition = Vector3.zero;
            _overlayObject.transform.localRotation = Quaternion.identity;
            _overlayObject.transform.localScale = Vector3.one;

            var collider = _overlayObject.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var overlayFilter = _overlayObject.GetComponent<MeshFilter>();
            var overlayRenderer = _overlayObject.GetComponent<MeshRenderer>();
            if (overlayFilter == null || overlayRenderer == null)
            {
                return;
            }

            _overlayMaterial = CreateOverlayMaterial();
            overlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            overlayRenderer.receiveShadows = false;
            overlayRenderer.lightProbeUsage = LightProbeUsage.Off;
            overlayRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            overlayRenderer.material = _overlayMaterial;
        }
    }

    private static Material CreateOverlayMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        var material = new Material(shader);
        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_Blend"))
        {
            material.SetFloat("_Blend", 0f);
        }

        if (material.HasProperty("_SrcBlend"))
        {
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        }

        if (material.HasProperty("_DstBlend"))
        {
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 0f);
        }

        material.renderQueue = (int)RenderQueue.Transparent;
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetOverrideTag("RenderType", "Transparent");
        return material;
    }
}
