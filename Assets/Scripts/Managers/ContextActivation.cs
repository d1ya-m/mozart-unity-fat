using System.Collections.Generic;
using UnityEngine;

public class ContextActivation : MonoBehaviour
{
    [Header("Empty list means: any room/runtime")]
    [SerializeField] private List<AppRoom> allowedRooms = new List<AppRoom>();
    [SerializeField] private List<AppRuntime> allowedRuntimes = new List<AppRuntime>();
    [SerializeField] private bool invertResult;

    [Header("Targets")]
    [SerializeField] private GameObject[] gameObjectsToToggle;
    [SerializeField] private Behaviour[] behavioursToToggle;

    private AppContextManager _context;

    private void OnEnable()
    {
        _context = AppContextManager.Instance;
        if (_context == null)
        {
            return;
        }

        _context.ContextChanged += OnContextChanged;
        Apply();
    }

    private void Start()
    {
        Apply();
    }

    private void OnDisable()
    {
        if (_context != null)
        {
            _context.ContextChanged -= OnContextChanged;
        }
    }

    [ContextMenu("Apply Context")]
    public void Apply()
    {
        var context = AppContextManager.Instance;
        if (context == null)
        {
            return;
        }

        bool roomAllowed = allowedRooms.Count == 0 || allowedRooms.Contains(context.CurrentRoom);
        bool runtimeAllowed = allowedRuntimes.Count == 0 || allowedRuntimes.Contains(context.CurrentRuntime);

        bool shouldEnable = roomAllowed && runtimeAllowed;
        if (invertResult)
        {
            shouldEnable = !shouldEnable;
        }

        if (gameObjectsToToggle != null)
        {
            foreach (var target in gameObjectsToToggle)
            {
                if (target != null)
                {
                    target.SetActive(shouldEnable);
                }
            }
        }

        if (behavioursToToggle != null)
        {
            foreach (var behaviour in behavioursToToggle)
            {
                if (behaviour != null)
                {
                    behaviour.enabled = shouldEnable;
                }
            }
        }
    }

    private void OnContextChanged(AppRoom _, AppRuntime __)
    {
        Apply();
    }
}
