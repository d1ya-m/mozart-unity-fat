using Oculus.Interaction;
using UnityEngine;


public class Floor : PointableElement
{
    [SerializeField]
    private RayInteractable RayInteractable;

    protected override void Start()
    {
        base.Start();
        WhenPointerEventRaised += OnPointerEventRaised;
    }


    private void OnDestroy()
    {
        WhenPointerEventRaised -= OnPointerEventRaised;
    }

    private void OnPointerEventRaised(PointerEvent evt)
    {
        if (GameManager.Instance.SelectingRectangle)
        {
            UnityEngine.Pose floorPose = GetFloorPose(evt);
            if (evt.Type == PointerEventType.Select)
            {
                GameManager.Instance.PointOnFloorSelected(floorPose);
            } else if (evt.Type == PointerEventType.Move)
            {
                GameManager.Instance.PointOnFloorHovered(floorPose);
            }
        }
        
    }

    private UnityEngine.Pose GetFloorPose(PointerEvent evt)
    {
        UnityEngine.Pose pose = evt.Pose;

        // Meta XR SDK: PointerEvent.Data is typically the originating interactor.
        // For ray interactions, CollisionInfo.Point is the actual hitpoint on the surface.
        if (evt.Data is RayInteractor rayInteractor && rayInteractor.CollisionInfo.HasValue)
        {
            pose.position = rayInteractor.CollisionInfo.Value.Point;
            return pose;
        }

        return pose;
    }


}
