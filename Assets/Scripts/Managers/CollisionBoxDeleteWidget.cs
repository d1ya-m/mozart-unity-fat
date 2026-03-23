using System.Threading.Tasks;
using TMPro;
using UnityEngine;

public class CollisionBoxDeleteWidget : MonoBehaviour
{
    [SerializeField] private float widgetVerticalOffset = 0.12f;
    [SerializeField] private float widgetHorizontalOffset = 0.12f;
    [SerializeField] private float buttonWidth = 0.14f;
    [SerializeField] private float buttonHeight = 0.06f;
    [SerializeField] private float buttonDepth = 0.02f;
    [SerializeField] private float buttonSpacing = 0.02f;
    [SerializeField] private float raycastDistance = 12f;
    [SerializeField] private float confirmTimeoutSeconds = 3f;
    [SerializeField] private int widgetLayer = 8;
    [SerializeField] private Color deleteColor = new Color(0.8f, 0.2f, 0.18f, 0.95f);
    [SerializeField] private Color confirmColor = new Color(0.92f, 0.48f, 0.12f, 0.95f);
    [SerializeField] private Color cancelColor = new Color(0.2f, 0.6f, 0.85f, 0.95f);
    [SerializeField] private Color disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.9f);

    private EditModeManager _editModeManager;
    private GameObject _root;
    private GameObject _deleteButton;
    private GameObject _cancelButton;
    private Collider _deleteCollider;
    private Collider _cancelCollider;
    private MeshRenderer _deleteRenderer;
    private MeshRenderer _cancelRenderer;
    private TextMeshPro _deleteLabel;
    private TextMeshPro _cancelLabel;
    private Material _deleteMaterial;
    private Material _cancelMaterial;
    private CollisionObjectBinding _selectedBinding;
    private bool _confirmArmed;
    private float _confirmDeadline;
    private bool _isDeleting;

    private void Awake()
    {
        EnsureWidget();
        ApplyVisibility(false);
    }

    private void OnEnable()
    {
        _editModeManager = EditModeManager.Instance;
        if (_editModeManager != null)
        {
            _editModeManager.EditModeChanged += OnEditModeChanged;
            _editModeManager.SelectedObjectChanged += OnSelectedObjectChanged;
        }

        RefreshSelection();
    }

    private void OnDisable()
    {
        if (_editModeManager != null)
        {
            _editModeManager.EditModeChanged -= OnEditModeChanged;
            _editModeManager.SelectedObjectChanged -= OnSelectedObjectChanged;
        }

        ApplyVisibility(false);
    }

    private void OnDestroy()
    {
        if (_deleteMaterial != null)
        {
            Destroy(_deleteMaterial);
        }

        if (_cancelMaterial != null)
        {
            Destroy(_cancelMaterial);
        }
    }

    private void Update()
    {
        if (_confirmArmed && Time.time > _confirmDeadline)
        {
            ResetConfirmationState();
        }

        if (!ShouldShowWidget())
        {
            ApplyVisibility(false);
            return;
        }

        ApplyVisibility(true);
        UpdateButtonVisuals();
        HandleInput();
    }

    private void LateUpdate()
    {
        if (!ShouldShowWidget())
        {
            return;
        }

        UpdateWidgetTransform();
    }

    private void OnEditModeChanged(bool _)
    {
        RefreshSelection();
    }

    private void OnSelectedObjectChanged(GameObject _)
    {
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        _selectedBinding = _editModeManager != null && _editModeManager.SelectedObject != null
            ? _editModeManager.SelectedObject.GetComponent<CollisionObjectBinding>()
            : null;

        if (_selectedBinding == null)
        {
            ResetConfirmationState();
        }

        ApplyVisibility(ShouldShowWidget());
    }

    private bool ShouldShowWidget()
    {
        return _editModeManager != null &&
               _editModeManager.IsEditMode &&
               _selectedBinding != null &&
               _selectedBinding.gameObject != null &&
               _selectedBinding.gameObject.activeInHierarchy;
    }

    private void HandleInput()
    {
        if (!OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
        {
            return;
        }

        Ray ray = BuildRightControllerRay();
        if (_cancelButton.activeSelf && _cancelCollider != null && _cancelCollider.Raycast(ray, out _, raycastDistance))
        {
            ResetConfirmationState();
            return;
        }

        if (_deleteCollider != null && _deleteCollider.Raycast(ray, out _, raycastDistance))
        {
            if (!_confirmArmed)
            {
                _confirmArmed = true;
                _confirmDeadline = Time.time + confirmTimeoutSeconds;
                UpdateButtonVisuals();
                return;
            }

            _ = DeleteSelectedCollisionObjectAsync();
        }
    }

    private async Task DeleteSelectedCollisionObjectAsync()
    {
        if (_isDeleting || _selectedBinding == null)
        {
            return;
        }

        _isDeleting = true;
        UpdateButtonVisuals();

        bool removed = await _selectedBinding.RemoveAsync(force: true);
        if (removed)
        {
            GameObject target = _selectedBinding.gameObject;
            _editModeManager?.SetSelectedObject(null);
            Destroy(target);
        }

        _isDeleting = false;
        ResetConfirmationState();
        RefreshSelection();
    }

    private void EnsureWidget()
    {
        if (_root != null)
        {
            return;
        }

        _root = new GameObject("CollisionBoxDeleteWidgetRoot");
        _root.transform.SetParent(transform, false);
        SetLayerRecursively(_root, widgetLayer);

        (_deleteButton, _deleteCollider, _deleteRenderer, _deleteMaterial, _deleteLabel) =
            CreateButton("DeleteButton", "Delete", deleteColor);
        _deleteButton.transform.SetParent(_root.transform, false);
        _deleteButton.transform.localPosition = Vector3.zero;

        (_cancelButton, _cancelCollider, _cancelRenderer, _cancelMaterial, _cancelLabel) =
            CreateButton("CancelButton", "Cancel", cancelColor);
        _cancelButton.transform.SetParent(_root.transform, false);
        _cancelButton.transform.localPosition = new Vector3(buttonWidth + buttonSpacing, 0f, 0f);
    }

    private (GameObject buttonObject, Collider collider, MeshRenderer renderer, Material material, TextMeshPro label)
        CreateButton(string name, string labelText, Color color)
    {
        GameObject buttonObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        buttonObject.name = name;
        buttonObject.transform.localScale = new Vector3(buttonWidth, buttonHeight, buttonDepth);
        SetLayerRecursively(buttonObject, widgetLayer);

        Collider collider = buttonObject.GetComponent<Collider>();
        MeshRenderer renderer = buttonObject.GetComponent<MeshRenderer>();
        Material material = CreateButtonMaterial(color);
        renderer.sharedMaterial = material;

        GameObject labelObject = new GameObject("Label");
        labelObject.transform.SetParent(buttonObject.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 0f, buttonDepth * 0.6f);
        labelObject.transform.localRotation = Quaternion.identity;
        SetLayerRecursively(labelObject, widgetLayer);

        TextMeshPro label = labelObject.AddComponent<TextMeshPro>();
        label.text = labelText;
        label.fontSize = 2.8f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.rectTransform.sizeDelta = new Vector2(0.5f, 0.2f);
        label.enableWordWrapping = false;

        return (buttonObject, collider, renderer, material, label);
    }

    private void UpdateWidgetTransform()
    {
        if (_selectedBinding == null)
        {
            return;
        }

        Bounds bounds = GetTargetBounds(_selectedBinding.gameObject);
        Transform cameraTransform = Camera.main != null ? Camera.main.transform : null;
        Vector3 cameraRight = cameraTransform != null ? cameraTransform.right : Vector3.right;
        cameraRight.y = 0f;
        if (cameraRight.sqrMagnitude < 0.0001f)
        {
            cameraRight = Vector3.right;
        }

        cameraRight.Normalize();

        Vector3 targetPosition =
            bounds.center +
            Vector3.up * (bounds.extents.y + widgetVerticalOffset) +
            cameraRight * (bounds.extents.x + widgetHorizontalOffset);

        _root.transform.position = targetPosition;

        if (cameraTransform != null)
        {
            Vector3 lookDirection = cameraTransform.position - _root.transform.position;
            if (lookDirection.sqrMagnitude > 0.0001f)
            {
                _root.transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
            }
        }
    }

    private void UpdateButtonVisuals()
    {
        if (_deleteButton == null || _cancelButton == null)
        {
            return;
        }

        _cancelButton.SetActive(_confirmArmed && !_isDeleting);

        if (_isDeleting)
        {
            _deleteLabel.text = "Deleting";
            _deleteMaterial.color = disabledColor;
            _cancelMaterial.color = disabledColor;
            return;
        }

        _deleteLabel.text = _confirmArmed ? "Confirm" : "Delete";
        _deleteMaterial.color = _confirmArmed ? confirmColor : deleteColor;
        _cancelMaterial.color = cancelColor;
        _cancelLabel.text = "Cancel";
    }

    private void ResetConfirmationState()
    {
        _confirmArmed = false;
        _confirmDeadline = 0f;
        UpdateButtonVisuals();
    }

    private void ApplyVisibility(bool visible)
    {
        if (_root != null)
        {
            _root.SetActive(visible);
        }
    }

    private static Bounds GetTargetBounds(GameObject target)
    {
        Collider collider = target.GetComponent<Collider>();
        if (collider != null)
        {
            return collider.bounds;
        }

        Renderer renderer = target.GetComponentInChildren<Renderer>();
        if (renderer != null)
        {
            return renderer.bounds;
        }

        return new Bounds(target.transform.position, Vector3.one * 0.2f);
    }

    private Ray BuildRightControllerRay()
    {
        Vector3 controllerPosition = TransformControllerLocalToWorld(
            OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch));
        Quaternion controllerRotation = TransformControllerLocalToWorld(
            OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch));
        return new Ray(controllerPosition, controllerRotation * Vector3.forward);
    }

    private Vector3 TransformControllerLocalToWorld(Vector3 localPosition)
    {
        Transform trackingSpace = FindTrackingSpaceTransform();
        return trackingSpace.TransformPoint(localPosition);
    }

    private Quaternion TransformControllerLocalToWorld(Quaternion localRotation)
    {
        Transform trackingSpace = FindTrackingSpaceTransform();
        return trackingSpace.rotation * localRotation;
    }

    private static Material CreateButtonMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        Material material = new Material(shader);
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
        else if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }

        return material;
    }

    private static void SetLayerRecursively(GameObject target, int layer)
    {
        if (target == null)
        {
            return;
        }

        target.layer = layer;
        foreach (Transform child in target.transform)
        {
            if (child != null)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }
    }

    private Transform FindTrackingSpaceTransform()
    {
        var cameraRig = FindFirstObjectByType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.trackingSpace != null)
        {
            return cameraRig.trackingSpace;
        }

        if (Camera.main != null && Camera.main.transform.parent != null)
        {
            return Camera.main.transform.parent;
        }

        return Camera.main != null ? Camera.main.transform : transform;
    }
}
