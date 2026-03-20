using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DefaultExecutionOrder(-900)]
public class EditModeManager : Singleton<EditModeManager>
{
    [SerializeField] private bool startInEditMode;
    [SerializeField] private float rotationSpeedDegPerSec = 90f;
    [SerializeField] private float scaleSpeed = 1.0f;
    [SerializeField] private float minUniformScale = 0.05f;
    [SerializeField] private float maxUniformScale = 5.0f;
    [Header("Fallback Manipulation")]
    [SerializeField] private bool enableCollisionBoxFallbackManipulation = true;
    [SerializeField] private bool autoSelectClosestCollisionBox = true;
    [SerializeField] private float collisionBoxSelectionRayLength = 20f;
    [SerializeField] private float collisionBoxAutoSelectDistance = 2.5f;
    [SerializeField] private float collisionBoxMoveSensitivity = 1.0f;
    [SerializeField] private float collisionBoxRotateSensitivity = 1.0f;

    public bool IsEditMode { get; private set; }
    public bool IsMatEditMode { get; private set; }
    public bool IsAnyEditMode => IsEditMode || IsMatEditMode;
    public GameObject SelectedObject { get; private set; }

    public event Action<bool> EditModeChanged;
    public event Action<bool> MatEditModeChanged;
    public event Action<GameObject> SelectedObjectChanged;

    private bool _fallbackMoveGripActive;
    private Vector3 _fallbackMoveGripStartControllerPosition;
    private Vector3 _fallbackMoveGripStartObjectPosition;
    private bool _fallbackRotateGripActive;
    private Quaternion _fallbackRotateGripStartControllerRotation = Quaternion.identity;
    private Quaternion _fallbackRotateGripStartObjectRotation = Quaternion.identity;

    private void Awake()
    {
        RegisterExistingEditableObjects();
        SetEditMode(startInEditMode);
    }

    private void Update()
    {
        if (!IsAnyEditMode)
        {
            return;
        }

        if (enableCollisionBoxFallbackManipulation)
        {
            if (IsEditMode)
            {
                UpdateCollisionBoxFallbackSelection();
                UpdateCollisionBoxFallbackManipulation();
            }
            else if (IsMatEditMode)
            {
                UpdateMatFallbackSelection();
                UpdateMatFallbackManipulation();
            }
        }

        if (SelectedObject == null)
        {
            SetSelectedObject(null);
            return;
        }

        float rotateInput = 0f;
        float scaleInput = 0f;

        rotateInput += OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick).x;
        scaleInput += OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).y;

#if UNITY_EDITOR
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.cKey.wasPressedThisFrame)
            {
                ToggleEditMode();
            }

            if (keyboard.mKey.wasPressedThisFrame)
            {
                ToggleMatEditMode();
            }

            if (keyboard.qKey.isPressed)
            {
                rotateInput -= 1f;
            }

            if (keyboard.eKey.isPressed)
            {
                rotateInput += 1f;
            }

            if (keyboard.zKey.isPressed)
            {
                scaleInput -= 1f;
            }

            if (keyboard.xKey.isPressed)
            {
                scaleInput += 1f;
            }
        }
#endif
#endif

        if (Mathf.Abs(rotateInput) > 0.001f)
        {
            SelectedObject.transform.Rotate(
                Vector3.up,
                rotateInput * rotationSpeedDegPerSec * Time.deltaTime,
                Space.World);
        }

        bool allowUniformScale = SelectedObject.GetComponent<CollisionObjectBinding>() != null;
        if (allowUniformScale && Mathf.Abs(scaleInput) > 0.001f)
        {
            float factor = 1f + (scaleInput * scaleSpeed * Time.deltaTime);
            factor = Mathf.Max(0.01f, factor);

            Vector3 scaled = SelectedObject.transform.localScale * factor;
            scaled.x = Mathf.Clamp(scaled.x, minUniformScale, maxUniformScale);
            scaled.y = Mathf.Clamp(scaled.y, minUniformScale, maxUniformScale);
            scaled.z = Mathf.Clamp(scaled.z, minUniformScale, maxUniformScale);
            SelectedObject.transform.localScale = scaled;
        }
    }

    public void ToggleEditMode()
    {
        SetEditMode(!IsEditMode);
    }

    public void SetEditMode(bool enabled)
    {
        if (IsEditMode == enabled)
        {
            return;
        }

        if (enabled && IsMatEditMode)
        {
            SetMatEditModeInternal(false);
        }

        SetEditModeInternal(enabled);
    }

    public void ToggleMatEditMode()
    {
        SetMatEditMode(!IsMatEditMode);
    }

    public void SetMatEditMode(bool enabled)
    {
        if (IsMatEditMode == enabled)
        {
            return;
        }

        if (enabled && IsEditMode)
        {
            SetEditModeInternal(false);
        }

        SetMatEditModeInternal(enabled);
    }

    public void SetSelectedObject(GameObject selected)
    {
        if (SelectedObject == selected)
        {
            return;
        }

        SelectedObject = selected;
        SelectedObjectChanged?.Invoke(SelectedObject);
    }

    public EditableObject RegisterEditable(GameObject target)
    {
        if (target == null)
        {
            return null;
        }

        var editable = target.GetComponent<EditableObject>();
        if (editable == null)
        {
            editable = target.AddComponent<EditableObject>();
        }

        editable.SetRestrictManipulationToEditMode(true);
        editable.ApplyEditMode(IsAnyEditMode);
        return editable;
    }

    private void RegisterExistingEditableObjects()
    {
        var actionObjects = FindObjectsOfType<ActionObject>(true);
        for (int i = 0; i < actionObjects.Length; i++)
        {
            if (actionObjects[i] != null)
            {
                RegisterEditable(actionObjects[i].gameObject);
            }
        }
    }

    private void RefreshEditableRegistrations()
    {
        var editableObjects = FindObjectsOfType<EditableObject>(true);
        for (int i = 0; i < editableObjects.Length; i++)
        {
            if (editableObjects[i] != null)
            {
                editableObjects[i].RefreshManipulationBehaviours();
            }
        }

        var collisionBindings = FindObjectsOfType<CollisionObjectBinding>(true);
        for (int i = 0; i < collisionBindings.Length; i++)
        {
            if (collisionBindings[i] != null)
            {
                if (collisionBindings[i].GetComponent<CollisionBoxEditOverlay>() == null)
                {
                    collisionBindings[i].gameObject.AddComponent<CollisionBoxEditOverlay>();
                }

                RegisterEditable(collisionBindings[i].gameObject);
            }
        }
    }

    private void SetEditModeInternal(bool enabled)
    {
        if (IsEditMode == enabled)
        {
            return;
        }

        IsEditMode = enabled;
        ResetFallbackManipulationState();
        RefreshEditableRegistrations();
        if (!enabled)
        {
            SetSelectedObject(null);
        }

        EditModeChanged?.Invoke(IsEditMode);
    }

    private void SetMatEditModeInternal(bool enabled)
    {
        if (IsMatEditMode == enabled)
        {
            return;
        }

        IsMatEditMode = enabled;
        ResetFallbackManipulationState();
        RefreshEditableRegistrations();
        if (!enabled)
        {
            SetSelectedObject(null);
        }

        MatEditModeChanged?.Invoke(IsMatEditMode);
    }

    private void ResetFallbackManipulationState()
    {
        _fallbackMoveGripActive = false;
        _fallbackRotateGripActive = false;
    }

    private void UpdateCollisionBoxFallbackSelection()
    {
        if (autoSelectClosestCollisionBox && !_fallbackMoveGripActive && !_fallbackRotateGripActive)
        {
            _ = FindClosestCollisionBoxToRightController();
        }

        if (!OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
        {
            return;
        }

        if (autoSelectClosestCollisionBox && !_fallbackMoveGripActive && !_fallbackRotateGripActive)
        {
            var closest = FindClosestCollisionBoxToRightController();
            if (closest != null)
            {
                SetSelectedObject(closest.gameObject);
                return;
            }
        }

        Ray ray = BuildRightControllerRay();
        RaycastHit[] hits = Physics.RaycastAll(ray, collisionBoxSelectionRayLength, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
        {
            return;
        }

        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            var hitObject = hits[i].collider != null
                ? hits[i].collider.GetComponentInParent<CollisionObjectBinding>()
                : null;
            if (hitObject != null)
            {
                SetSelectedObject(hitObject.gameObject);
                return;
            }
        }
    }

    private CollisionObjectBinding FindClosestCollisionBoxToRightController()
    {
        var bindings = FindObjectsOfType<CollisionObjectBinding>(true);
        if (bindings == null || bindings.Length == 0)
        {
            return null;
        }

        Vector3 controllerPosition = TransformControllerLocalToWorld(
            OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch));

        CollisionObjectBinding closest = null;
        float closestDistance = float.MaxValue;
        for (int i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            if (binding == null || !binding.gameObject.activeInHierarchy)
            {
                continue;
            }

            float distance = Vector3.Distance(controllerPosition, binding.transform.position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = binding;
            }
        }

        return closestDistance <= collisionBoxAutoSelectDistance ? closest : null;
    }

    private void UpdateCollisionBoxFallbackManipulation()
    {
        if (SelectedObject == null || SelectedObject.GetComponent<CollisionObjectBinding>() == null)
        {
            ResetFallbackManipulationState();
            return;
        }

        UpdateSelectedObjectFallbackManipulation(PersistSelectedCollisionObject);
    }

    private void UpdateMatFallbackSelection()
    {
        if (autoSelectClosestCollisionBox && !_fallbackMoveGripActive && !_fallbackRotateGripActive)
        {
            _ = FindClosestMatObjectToRightController();
        }

        if (!OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
        {
            return;
        }

        if (autoSelectClosestCollisionBox && !_fallbackMoveGripActive && !_fallbackRotateGripActive)
        {
            var closest = FindClosestMatObjectToRightController();
            if (closest != null)
            {
                SetSelectedObject(closest.gameObject);
                return;
            }
        }

        Ray ray = BuildRightControllerRay();
        RaycastHit[] hits = Physics.RaycastAll(ray, collisionBoxSelectionRayLength, ~0, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0)
        {
            return;
        }

        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            var hitObject = hits[i].collider != null
                ? hits[i].collider.GetComponentInParent<MatActionObjectBinding>()
                : null;
            if (hitObject != null)
            {
                SetSelectedObject(hitObject.gameObject);
                return;
            }
        }
    }

    private MatActionObjectBinding FindClosestMatObjectToRightController()
    {
        var bindings = FindObjectsOfType<MatActionObjectBinding>(true);
        if (bindings == null || bindings.Length == 0)
        {
            return null;
        }

        Vector3 controllerPosition = TransformControllerLocalToWorld(
            OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch));

        MatActionObjectBinding closest = null;
        float closestDistance = float.MaxValue;
        for (int i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            if (binding == null || !binding.gameObject.activeInHierarchy)
            {
                continue;
            }

            float distance = Vector3.Distance(controllerPosition, binding.transform.position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = binding;
            }
        }

        return closestDistance <= collisionBoxAutoSelectDistance ? closest : null;
    }

    private void UpdateMatFallbackManipulation()
    {
        if (SelectedObject == null || SelectedObject.GetComponent<MatActionObjectBinding>() == null)
        {
            ResetFallbackManipulationState();
            return;
        }

        UpdateSelectedObjectFallbackManipulation(PersistSelectedMatObject);
    }

    private void UpdateSelectedObjectFallbackManipulation(Action persistAction)
    {
        if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger))
        {
            Vector3 controllerPosition = TransformControllerLocalToWorld(
                OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch));

            if (!_fallbackMoveGripActive)
            {
                _fallbackMoveGripActive = true;
                _fallbackMoveGripStartControllerPosition = controllerPosition;
                _fallbackMoveGripStartObjectPosition = SelectedObject.transform.position;
            }
            else
            {
                Vector3 controllerDelta = controllerPosition - _fallbackMoveGripStartControllerPosition;
                SelectedObject.transform.position =
                    _fallbackMoveGripStartObjectPosition + controllerDelta * collisionBoxMoveSensitivity;
            }
        }
        else
        {
            if (_fallbackMoveGripActive)
            {
                persistAction?.Invoke();
            }

            _fallbackMoveGripActive = false;
        }

        if (OVRInput.Get(OVRInput.Button.SecondaryHandTrigger))
        {
            Quaternion controllerRotation = TransformControllerLocalToWorld(
                OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch));

            if (!_fallbackRotateGripActive)
            {
                _fallbackRotateGripActive = true;
                _fallbackRotateGripStartControllerRotation = controllerRotation;
                _fallbackRotateGripStartObjectRotation = SelectedObject.transform.rotation;
            }
            else
            {
                Quaternion controllerDelta = controllerRotation * Quaternion.Inverse(_fallbackRotateGripStartControllerRotation);
                Quaternion targetRotation = controllerDelta * _fallbackRotateGripStartObjectRotation;
                SelectedObject.transform.rotation = Quaternion.Slerp(
                    _fallbackRotateGripStartObjectRotation,
                    targetRotation,
                    Mathf.Max(0f, collisionBoxRotateSensitivity));
            }
        }
        else
        {
            if (_fallbackRotateGripActive)
            {
                persistAction?.Invoke();
            }

            _fallbackRotateGripActive = false;
        }
    }

    private void PersistSelectedCollisionObject()
    {
        var binding = SelectedObject != null ? SelectedObject.GetComponent<CollisionObjectBinding>() : null;
        if (binding != null && GameManager.Instance != null && GameManager.Instance.Origin != null)
        {
            _ = binding.PersistIfChangedAsync(GameManager.Instance.Origin);
        }
    }

    private void PersistSelectedMatObject()
    {
        var binding = SelectedObject != null ? SelectedObject.GetComponent<MatActionObjectBinding>() : null;
        if (binding != null && GameManager.Instance != null && GameManager.Instance.Origin != null)
        {
            _ = binding.PersistIfChangedAsync(GameManager.Instance.Origin);
        }
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
        Transform trackingSpace = GameManager.Instance != null && GameManager.Instance.Origin != null
            ? GameManager.Instance.Origin
            : (Camera.main != null ? Camera.main.transform : transform);
        return trackingSpace.TransformPoint(localPosition);
    }

    private Quaternion TransformControllerLocalToWorld(Quaternion localRotation)
    {
        Transform trackingSpace = GameManager.Instance != null && GameManager.Instance.Origin != null
            ? GameManager.Instance.Origin
            : (Camera.main != null ? Camera.main.transform : transform);
        return trackingSpace.rotation * localRotation;
    }
}
