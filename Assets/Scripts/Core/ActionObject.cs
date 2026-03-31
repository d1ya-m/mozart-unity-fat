using Oculus.Interaction;
using UnityEngine;


public abstract class ActionObject : MonoBehaviour
{
    private void Start()
    {
        var obj = GetComponent<PointableElement>();
        if (obj != null)
        {
            obj.WhenPointerEventRaised += Obj_WhenPointerEventRaised;
        }
    }

    private void Obj_WhenPointerEventRaised(PointerEvent obj)
    {
        var editModeManager = EditModeManager.Instance;
        var matBinding = GetComponent<MatActionObjectBinding>();

        if (obj.Type == PointerEventType.Select)
        {
            bool allowSelection = editModeManager != null &&
                (editModeManager.IsEditMode || (editModeManager.IsMatEditMode && matBinding != null));
            if (allowSelection)
            {
                editModeManager.SetSelectedObject(gameObject);
            }
        }

        if (obj.Type == PointerEventType.Unselect)
        {
            if (editModeManager != null && editModeManager.IsMatEditMode && matBinding != null && GameManager.Instance != null && GameManager.Instance.Origin != null)
            {
                _ = matBinding.PersistIfChangedAsync(GameManager.Instance.Origin);
            }
        }
    }



    public abstract void Initialize(Arcor2.ClientSdk.ClientServices.Managers.ActionObjectManager actionObject);
}
