using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;

public class EditableObject : MonoBehaviour
{
    [SerializeField] private bool autoDiscoverManipulationBehaviours = true;
    [SerializeField] private bool restrictManipulationToEditMode = false;
    [SerializeField] private List<Behaviour> manipulationBehaviours = new List<Behaviour>();

    private static readonly string[] ManipulationKeywords =
    {
        "Grab",
        "Interactable",
        "Transformer",
        "Pointable"
    };

    private EditModeManager _editModeManager;
    private readonly List<PointableElement> _pointableElements = new List<PointableElement>();

    private void Awake()
    {
        if (autoDiscoverManipulationBehaviours)
        {
            AutoDiscoverBehaviours();
        }
    }

    private void OnEnable()
    {
        _editModeManager = EditModeManager.Instance;
        if (_editModeManager != null)
        {
            _editModeManager.EditModeChanged += ApplyEditMode;
            _editModeManager.MatEditModeChanged += ApplyEditMode;
            ApplyCurrentEditMode();
        }

        _pointableElements.Clear();
        var pointables = GetComponentsInChildren<PointableElement>(true);
        for (int i = 0; i < pointables.Length; i++)
        {
            if (pointables[i] != null)
            {
                _pointableElements.Add(pointables[i]);
                pointables[i].WhenPointerEventRaised += OnPointerEventRaised;
            }
        }
    }

    private void OnDisable()
    {
        if (_editModeManager != null)
        {
            _editModeManager.EditModeChanged -= ApplyEditMode;
            _editModeManager.MatEditModeChanged -= ApplyEditMode;
        }

        for (int i = 0; i < _pointableElements.Count; i++)
        {
            if (_pointableElements[i] != null)
            {
                _pointableElements[i].WhenPointerEventRaised -= OnPointerEventRaised;
            }
        }

        _pointableElements.Clear();

        if (_editModeManager != null && _editModeManager.SelectedObject == gameObject)
        {
            _editModeManager.SetSelectedObject(null);
        }
    }

    [ContextMenu("Auto Discover Manipulation Behaviours")]
    public void AutoDiscoverBehaviours()
    {
        manipulationBehaviours.Clear();

        var allBehaviours = GetComponentsInChildren<Behaviour>(true);
        foreach (var behaviour in allBehaviours)
        {
            if (behaviour == null || behaviour == this)
            {
                continue;
            }

            string typeName = behaviour.GetType().Name;
            if (!ContainsKeyword(typeName))
            {
                continue;
            }

            manipulationBehaviours.Add(behaviour);
        }
    }

    public void RefreshManipulationBehaviours()
    {
        if (autoDiscoverManipulationBehaviours)
        {
            AutoDiscoverBehaviours();
        }

        if (_editModeManager != null)
        {
            ApplyCurrentEditMode();
        }
    }

    public void SetRestrictManipulationToEditMode(bool restrict)
    {
        restrictManipulationToEditMode = restrict;
        if (_editModeManager != null)
        {
            ApplyCurrentEditMode();
        }
    }

    private void ApplyCurrentEditMode()
    {
        if (_editModeManager == null)
        {
            return;
        }

        ApplyEditMode(_editModeManager.IsAnyEditMode);
    }

    public void ApplyEditMode(bool enabled)
    {
        bool shouldEnableManipulation = !restrictManipulationToEditMode || enabled;

        for (int i = 0; i < manipulationBehaviours.Count; i++)
        {
            var behaviour = manipulationBehaviours[i];
            if (behaviour != null)
            {
                behaviour.enabled = shouldEnableManipulation;
            }
        }
    }

    private static bool ContainsKeyword(string typeName)
    {
        for (int i = 0; i < ManipulationKeywords.Length; i++)
        {
            if (typeName.Contains(ManipulationKeywords[i]))
            {
                return true;
            }
        }

        return false;
    }

    private void OnPointerEventRaised(PointerEvent pointerEvent)
    {
        if (_editModeManager == null || !_editModeManager.IsEditMode)
        {
            return;
        }

        if (pointerEvent.Type == PointerEventType.Select)
        {
            _editModeManager.SetSelectedObject(gameObject);
        }
        else if (pointerEvent.Type == PointerEventType.Unselect)
        {
            var binding = GetComponent<CollisionObjectBinding>();
            if (binding != null && GameManager.Instance != null && GameManager.Instance.Origin != null)
            {
                _ = binding.PersistIfChangedAsync(GameManager.Instance.Origin);
            }
        }

        // Keep selection after unselect so user can rotate/scale after releasing grab.
    }
}
