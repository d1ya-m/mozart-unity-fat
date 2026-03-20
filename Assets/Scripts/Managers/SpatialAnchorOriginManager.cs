using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class SpatialAnchorOriginManager : MonoBehaviour
{
    private const string DefaultAnchorUuidPlayerPrefsKey = "spatial_anchor.origin.uuid";

    [Header("Tracked Roots")]
    [SerializeField] private Transform origin;
    [SerializeField] private Transform sceneMeshOrigin;

    [Header("Lifecycle")]
    [SerializeField] private bool autoLoadSavedAnchorOnStart = true;
    [SerializeField] private bool detachOriginFromParentWhenAnchored = true;
    [SerializeField] private int loadRetryCount = 20;
    [SerializeField] private float loadRetryIntervalSeconds = 1.0f;

    [Header("Origin Edit Mode")]
    [SerializeField] private Vector3 originMarkerScale = new Vector3(0.12f, 0.12f, 0.12f);
    [SerializeField] private Color originMarkerColor = new Color(0.1f, 0.9f, 0.9f, 0.85f);
    [SerializeField] private int originMarkerLayer = 8;
    [SerializeField] private float originMoveSpeed = 0.5f;
    [SerializeField] private float originVerticalSpeed = 0.5f;
    [SerializeField] private float originRotateSpeedDegPerSec = 90f;
    [SerializeField] private float originGripMoveSensitivity = 1.0f;
    [SerializeField] private float originGripRotateSensitivity = 1.0f;
    [SerializeField] private float originSlowMultiplier = 0.25f;
    [SerializeField] private float originFastMultiplier = 2.0f;

    [Header("Storage")]
    [SerializeField] private string anchorUuidPlayerPrefsKey = DefaultAnchorUuidPlayerPrefsKey;

    private OVRSpatialAnchor _boundAnchor;
    private bool _isBusy;
    private bool _isOriginAnchorEditMode;
    private GameObject _originMarker;
    private Material _originMarkerMaterial;
    private bool _originMoveGripActive;
    private Vector3 _originMoveGripStartControllerPosition;
    private Vector3 _originMoveGripStartOriginPosition;
    private bool _originRotateGripActive;
    private Quaternion _originRotateGripStartControllerRotation = Quaternion.identity;
    private Quaternion _originRotateGripStartOriginRotation = Quaternion.identity;

    public bool UsesSpatialAnchorForOrigin => true;
    public bool HasSavedAnchor => TryGetSavedAnchorUuid(out _);
    public Guid CurrentAnchorUuid => _boundAnchor != null && _boundAnchor.Created ? _boundAnchor.Uuid : Guid.Empty;
    public bool IsOriginAnchorEditMode => _isOriginAnchorEditMode;

    public event Action<bool> OriginAnchorEditModeChanged;

    private async void Start()
    {
        ResolveReferences();
        if (!autoLoadSavedAnchorOnStart)
        {
            return;
        }

        await TryLoadSavedAnchorWithRetriesAsync();
    }

    private void Update()
    {
        if (!_isOriginAnchorEditMode || origin == null)
        {
            return;
        }

        EnsureOriginMarker();
        ApplyOriginEditInput();
    }

    private void OnDisable()
    {
        SetOriginAnchorEditMode(false);
    }

    private void OnDestroy()
    {
        ReleaseOriginMarker();
    }

    [ContextMenu("Create Anchor At Origin")]
    public void CreateAnchorAtOriginContextMenu()
    {
        _ = CreateAnchorAtOriginAsync(replaceExistingAnchor: false);
    }

    [ContextMenu("Replace Anchor At Origin")]
    public void ReplaceAnchorAtOriginContextMenu()
    {
        _ = CreateAnchorAtOriginAsync(replaceExistingAnchor: true);
    }

    [ContextMenu("Reload Saved Anchor")]
    public void ReloadSavedAnchorContextMenu()
    {
        _ = TryLoadSavedAnchorWithRetriesAsync(forceReload: true);
    }

    [ContextMenu("Clear Saved Anchor")]
    public void ClearSavedAnchorContextMenu()
    {
        _ = ClearSavedAnchorAsync();
    }

    [ContextMenu("Toggle Origin Anchor Edit Mode")]
    public void ToggleOriginAnchorEditModeContextMenu()
    {
        ToggleOriginAnchorEditMode();
    }

    public async Task<bool> TryLoadSavedAnchorWithRetriesAsync(bool forceReload = false)
    {
        ResolveReferences();

        if (!TryGetSavedAnchorUuid(out Guid savedUuid))
        {
            Debug.Log("[SpatialAnchorOriginManager] No saved spatial anchor UUID found.");
            return false;
        }

        if (!forceReload &&
            _boundAnchor != null &&
            _boundAnchor.Created &&
            _boundAnchor.Uuid == savedUuid)
        {
            return true;
        }

        for (int attempt = 1; attempt <= Mathf.Max(1, loadRetryCount); attempt++)
        {
            bool loaded = await TryLoadSavedAnchorOnceAsync(savedUuid, forceReload);
            if (loaded)
            {
                return true;
            }

            if (attempt < loadRetryCount)
            {
                await Task.Delay(Mathf.RoundToInt(loadRetryIntervalSeconds * 1000f));
            }
        }

        Debug.LogWarning($"[SpatialAnchorOriginManager] Failed to load saved anchor {savedUuid} after {loadRetryCount} attempts.");
        return false;
    }

    public void ToggleOriginAnchorEditMode()
    {
        SetOriginAnchorEditMode(!_isOriginAnchorEditMode);
    }

    public void SetOriginAnchorEditMode(bool enabled)
    {
        ResolveReferences();
        if (_isOriginAnchorEditMode == enabled)
        {
            return;
        }

        _isOriginAnchorEditMode = enabled;
        ResetOriginEditGripState();

        if (_isOriginAnchorEditMode)
        {
            ReleaseRuntimeAnchorComponentForEditing();
            DetachOriginFromParentIfNeeded();
            EnsureSceneMeshHierarchy();
            EnsureOriginMarker();
            Debug.Log("[SpatialAnchorOriginManager] Origin anchor edit mode enabled. Runtime anchor binding suspended.");
        }
        else
        {
            UpdateOriginMarkerVisibility();
        }

        OriginAnchorEditModeChanged?.Invoke(_isOriginAnchorEditMode);
    }

    public Task<bool> SaveOriginAnchorAsync(bool replaceExistingAnchor = true)
    {
        return CreateAnchorAtOriginAsync(replaceExistingAnchor);
    }

    public Task<bool> ReloadOriginAnchorAsync(bool forceReload = true)
    {
        return TryLoadSavedAnchorWithRetriesAsync(forceReload);
    }

    public async Task<bool> CreateAnchorAtOriginAsync(bool replaceExistingAnchor)
    {
        ResolveReferences();
        if (_isBusy)
        {
            Debug.LogWarning("[SpatialAnchorOriginManager] Busy. Ignoring create-anchor request.");
            return false;
        }

        if (origin == null)
        {
            Debug.LogError("[SpatialAnchorOriginManager] Origin is not assigned.");
            return false;
        }

        if (TryGetSavedAnchorUuid(out _) && !replaceExistingAnchor)
        {
            Debug.LogWarning("[SpatialAnchorOriginManager] A saved anchor already exists. Use replace to overwrite it.");
            return false;
        }

        _isBusy = true;
        try
        {
            if (replaceExistingAnchor)
            {
                await ClearSavedAnchorCoreAsync();
            }
            else
            {
                await ReleaseRuntimeAnchorComponentAsync();
            }

            DetachOriginFromParentIfNeeded();

            var anchor = origin.gameObject.AddComponent<OVRSpatialAnchor>();
            bool localized = await anchor.WhenLocalizedAsync();
            if (!localized)
            {
                Debug.LogError("[SpatialAnchorOriginManager] Failed to localize newly created spatial anchor.");
                Destroy(anchor);
                return false;
            }

            var saveResult = await anchor.SaveAnchorAsync();
            if (!saveResult.Success)
            {
                Debug.LogError($"[SpatialAnchorOriginManager] Failed to save spatial anchor: {saveResult.Status}");
                Destroy(anchor);
                return false;
            }

            SaveAnchorUuid(anchor.Uuid);
            _boundAnchor = anchor;
            EnsureSceneMeshHierarchy();
            SetOriginAnchorEditMode(false);
            Debug.Log($"[SpatialAnchorOriginManager] Created and saved spatial anchor {anchor.Uuid}.");
            return true;
        }
        finally
        {
            _isBusy = false;
        }
    }

    public async Task<bool> ClearSavedAnchorAsync()
    {
        ResolveReferences();
        if (_isBusy)
        {
            Debug.LogWarning("[SpatialAnchorOriginManager] Busy. Ignoring clear-anchor request.");
            return false;
        }

        _isBusy = true;
        try
        {
            await ClearSavedAnchorCoreAsync();
            Debug.Log("[SpatialAnchorOriginManager] Cleared saved spatial anchor.");
            return true;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task<bool> TryLoadSavedAnchorOnceAsync(Guid savedUuid, bool forceReload)
    {
        ResolveReferences();
        if (origin == null)
        {
            Debug.LogError("[SpatialAnchorOriginManager] Origin is not assigned.");
            return false;
        }

        if (_isBusy)
        {
            return false;
        }

        _isBusy = true;
        try
        {
            if (forceReload)
            {
                await ReleaseRuntimeAnchorComponentAsync();
            }

            using var unboundAnchors = new ListScope<OVRSpatialAnchor.UnboundAnchor>();
            var loadResult = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(
                new[] { savedUuid },
                unboundAnchors.Items,
                (Action<List<OVRSpatialAnchor.UnboundAnchor>, int>)null);
            if (!loadResult.Success || unboundAnchors.Items.Count == 0)
            {
                Debug.LogWarning($"[SpatialAnchorOriginManager] LoadUnboundAnchorsAsync failed for {savedUuid}: {loadResult.Status}");
                return false;
            }

            var unboundAnchor = unboundAnchors.Items[0];
            bool localized = unboundAnchor.Localized || await unboundAnchor.LocalizeAsync();
            if (!localized)
            {
                Debug.LogWarning($"[SpatialAnchorOriginManager] Failed to localize saved spatial anchor {savedUuid}.");
                return false;
            }

            if (!unboundAnchor.TryGetPose(out Pose worldPose))
            {
                Debug.LogWarning($"[SpatialAnchorOriginManager] Saved spatial anchor {savedUuid} localized but pose was unavailable.");
                return false;
            }

            DetachOriginFromParentIfNeeded();
            origin.SetPositionAndRotation(worldPose.position, worldPose.rotation);

            var runtimeAnchor = origin.GetComponent<OVRSpatialAnchor>();
            if (runtimeAnchor == null)
            {
                runtimeAnchor = origin.gameObject.AddComponent<OVRSpatialAnchor>();
            }

            unboundAnchor.BindTo(runtimeAnchor);
            _boundAnchor = runtimeAnchor;
            EnsureSceneMeshHierarchy();
            UpdateOriginMarkerVisibility();
            Debug.Log($"[SpatialAnchorOriginManager] Loaded and bound saved spatial anchor {savedUuid}.");
            return true;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task ReleaseRuntimeAnchorComponentAsync()
    {
        ResolveReferences();

        if (_boundAnchor != null)
        {
            Destroy(_boundAnchor);
            _boundAnchor = null;
            await Task.Yield();
        }

        if (origin != null)
        {
            var existingAnchor = origin.GetComponent<OVRSpatialAnchor>();
            if (existingAnchor != null)
            {
                Destroy(existingAnchor);
                await Task.Yield();
            }
        }
    }

    private void ReleaseRuntimeAnchorComponentForEditing()
    {
        ResolveReferences();
        _boundAnchor = null;

        if (origin == null)
        {
            return;
        }

        var existingAnchors = origin.GetComponents<OVRSpatialAnchor>();
        for (int i = 0; i < existingAnchors.Length; i++)
        {
            if (existingAnchors[i] == null)
            {
                continue;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                DestroyImmediate(existingAnchors[i]);
            }
            else
            {
                Destroy(existingAnchors[i]);
            }
#else
            Destroy(existingAnchors[i]);
#endif
        }
    }

    private async Task ClearSavedAnchorCoreAsync()
    {
        bool hasSavedUuid = TryGetSavedAnchorUuid(out Guid savedUuid);

        if (_boundAnchor != null && _boundAnchor.Created)
        {
            var eraseResult = await _boundAnchor.EraseAnchorAsync();
            Debug.Log($"[SpatialAnchorOriginManager] Erase current anchor result: {eraseResult.Status}");
        }
        else if (hasSavedUuid)
        {
            var eraseResult = await OVRSpatialAnchor.EraseAnchorsAsync(Array.Empty<OVRSpatialAnchor>(), new[] { savedUuid });
            Debug.Log($"[SpatialAnchorOriginManager] Erase saved anchor by UUID result: {eraseResult.Status}");
        }

        if (hasSavedUuid)
        {
            PlayerPrefs.DeleteKey(anchorUuidPlayerPrefsKey);
            PlayerPrefs.Save();
        }

        await ReleaseRuntimeAnchorComponentAsync();
    }

    private void ResolveReferences()
    {
        if (origin == null && GameManager.Instance != null)
        {
            origin = GameManager.Instance.Origin;
        }

        if (sceneMeshOrigin == null && GameManager.Instance != null)
        {
            sceneMeshOrigin = GameManager.Instance.SceneMeshOrigin;
        }
    }

    private void EnsureSceneMeshHierarchy()
    {
        if (origin != null && sceneMeshOrigin != null && sceneMeshOrigin.parent != origin)
        {
            sceneMeshOrigin.SetParent(origin, true);
        }
    }

    private void EnsureOriginMarker()
    {
        if (origin == null)
        {
            return;
        }

        if (_originMarker == null)
        {
            _originMarker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _originMarker.name = "OriginAnchorMarker";
            _originMarker.transform.SetParent(origin, false);

            Collider markerCollider = _originMarker.GetComponent<Collider>();
            if (markerCollider != null)
            {
                Destroy(markerCollider);
            }

            var renderer = _originMarker.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("Unlit/Color");
                if (shader != null)
                {
                    _originMarkerMaterial = new Material(shader);
                    _originMarkerMaterial.color = originMarkerColor;
                    renderer.sharedMaterial = _originMarkerMaterial;
                }
            }
        }

        _originMarker.transform.localPosition = Vector3.zero;
        _originMarker.transform.localRotation = Quaternion.identity;
        _originMarker.transform.localScale = originMarkerScale;
        _originMarker.layer = originMarkerLayer;
        UpdateOriginMarkerVisibility();
    }

    private void UpdateOriginMarkerVisibility()
    {
        if (_originMarker != null)
        {
            _originMarker.SetActive(_isOriginAnchorEditMode);
        }
    }

    private void ReleaseOriginMarker()
    {
        if (_originMarker != null)
        {
            Destroy(_originMarker);
            _originMarker = null;
        }

        if (_originMarkerMaterial != null)
        {
            Destroy(_originMarkerMaterial);
            _originMarkerMaterial = null;
        }
    }

    private void ApplyOriginEditInput()
    {
        float sensitivityMultiplier = GetOriginEditSensitivityMultiplier();
        bool leftGripHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger);
        bool rightGripHeld = OVRInput.Get(OVRInput.Button.SecondaryHandTrigger);

        if (leftGripHeld)
        {
            UpdateOriginGripMove(sensitivityMultiplier);
        }
        else
        {
            _originMoveGripActive = false;
        }

        if (rightGripHeld)
        {
            UpdateOriginGripRotation(sensitivityMultiplier);
        }
        else
        {
            _originRotateGripActive = false;
        }

        Vector2 planarInput = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        Vector2 secondaryInput = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        float rotateYawInput = secondaryInput.x;
        float verticalInput = secondaryInput.y;

#if UNITY_EDITOR
#if ENABLE_INPUT_SYSTEM
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.aKey.isPressed) planarInput.x -= 1f;
            if (keyboard.dKey.isPressed) planarInput.x += 1f;
            if (keyboard.sKey.isPressed) planarInput.y -= 1f;
            if (keyboard.wKey.isPressed) planarInput.y += 1f;
            if (keyboard.rKey.isPressed) verticalInput += 1f;
            if (keyboard.fKey.isPressed) verticalInput -= 1f;
            if (keyboard.qKey.isPressed) rotateYawInput -= 1f;
            if (keyboard.eKey.isPressed) rotateYawInput += 1f;
        }
#endif
#endif

        Transform cameraTransform = Camera.main != null ? Camera.main.transform : null;
        Vector3 forward = cameraTransform != null ? cameraTransform.forward : Vector3.forward;
        Vector3 right = cameraTransform != null ? cameraTransform.right : Vector3.right;
        forward.y = 0f;
        right.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }
        if (right.sqrMagnitude < 0.0001f)
        {
            right = Vector3.right;
        }

        forward.Normalize();
        right.Normalize();

        Vector3 horizontalDelta = (right * planarInput.x + forward * planarInput.y) *
            (originMoveSpeed * sensitivityMultiplier * Time.deltaTime);
        Vector3 verticalDelta = Vector3.up * (verticalInput * originVerticalSpeed * sensitivityMultiplier * Time.deltaTime);
        float yawDelta = rotateYawInput * originRotateSpeedDegPerSec * sensitivityMultiplier * Time.deltaTime;

        origin.position += horizontalDelta + verticalDelta;
        if (Mathf.Abs(yawDelta) > 0.001f)
        {
            origin.Rotate(Vector3.up, yawDelta, Space.World);
        }
    }

    private float GetOriginEditSensitivityMultiplier()
    {
        bool slowHeld = OVRInput.Get(OVRInput.Button.One);
        bool fastHeld = OVRInput.Get(OVRInput.Button.Two);

#if UNITY_EDITOR
#if ENABLE_INPUT_SYSTEM
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null)
        {
            slowHeld |= keyboard.leftShiftKey.isPressed;
            fastHeld |= keyboard.leftCtrlKey.isPressed;
        }
#endif
#endif

        if (slowHeld)
        {
            return originSlowMultiplier;
        }

        if (fastHeld)
        {
            return originFastMultiplier;
        }

        return 1f;
    }

    private void UpdateOriginGripMove(float sensitivityMultiplier)
    {
        Transform trackingSpace = FindTrackingSpaceTransform();
        if (trackingSpace == null)
        {
            return;
        }

        Vector3 controllerPosition = OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch);
        Vector3 worldControllerPosition = trackingSpace.TransformPoint(controllerPosition);

        if (!_originMoveGripActive)
        {
            _originMoveGripActive = true;
            _originMoveGripStartControllerPosition = worldControllerPosition;
            _originMoveGripStartOriginPosition = origin.position;
            return;
        }

        Vector3 controllerDelta = worldControllerPosition - _originMoveGripStartControllerPosition;
        origin.position = _originMoveGripStartOriginPosition + (controllerDelta * originGripMoveSensitivity * sensitivityMultiplier);
    }

    private void UpdateOriginGripRotation(float sensitivityMultiplier)
    {
        Transform trackingSpace = FindTrackingSpaceTransform();
        if (trackingSpace == null)
        {
            return;
        }

        Quaternion controllerRotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        Quaternion worldControllerRotation = trackingSpace.rotation * controllerRotation;

        if (!_originRotateGripActive)
        {
            _originRotateGripActive = true;
            _originRotateGripStartControllerRotation = worldControllerRotation;
            _originRotateGripStartOriginRotation = origin.rotation;
            return;
        }

        Quaternion controllerDelta = worldControllerRotation * Quaternion.Inverse(_originRotateGripStartControllerRotation);
        Quaternion targetRotation = controllerDelta * _originRotateGripStartOriginRotation;
        origin.rotation = Quaternion.Slerp(
            _originRotateGripStartOriginRotation,
            targetRotation,
            Mathf.Max(0f, originGripRotateSensitivity * sensitivityMultiplier));
    }

    private void ResetOriginEditGripState()
    {
        _originMoveGripActive = false;
        _originRotateGripActive = false;
    }

    private static Transform FindTrackingSpaceTransform()
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

        return null;
    }

    private void DetachOriginFromParentIfNeeded()
    {
        if (!detachOriginFromParentWhenAnchored || origin == null || origin.parent == null)
        {
            return;
        }

        origin.SetParent(null, true);
    }

    private bool TryGetSavedAnchorUuid(out Guid uuid)
    {
        uuid = Guid.Empty;
        if (string.IsNullOrWhiteSpace(anchorUuidPlayerPrefsKey) || !PlayerPrefs.HasKey(anchorUuidPlayerPrefsKey))
        {
            return false;
        }

        string rawValue = PlayerPrefs.GetString(anchorUuidPlayerPrefsKey, string.Empty);
        return Guid.TryParse(rawValue, out uuid) && uuid != Guid.Empty;
    }

    private void SaveAnchorUuid(Guid uuid)
    {
        PlayerPrefs.SetString(anchorUuidPlayerPrefsKey, uuid.ToString());
        PlayerPrefs.Save();
    }

    private sealed class ListScope<T> : IDisposable
    {
        public List<T> Items { get; } = new List<T>();

        public void Dispose()
        {
            Items.Clear();
        }
    }
}
