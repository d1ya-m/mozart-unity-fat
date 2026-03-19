using Arcor2.ClientSdk.ClientServices.Managers;
using UnityEngine;

public class GrabbableMat : ActionObject
{
    public ActionObjectManager ActionObjectManager { get; private set; }

    public override void Initialize(ActionObjectManager actionObject)
    {
        ActionObjectManager = actionObject;

        Transform origin = GameManager.Instance != null && GameManager.Instance.Origin != null
            ? GameManager.Instance.Origin
            : transform.parent;

        if (origin == null)
        {
            return;
        }

        var binding = GetComponent<MatActionObjectBinding>();
        if (binding == null)
        {
            binding = gameObject.AddComponent<MatActionObjectBinding>();
        }

        binding.Initialize(actionObject, origin);
    }
}
