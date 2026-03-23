using System;
using System.Threading.Tasks;
using Arcor2.ClientSdk.ClientServices.Managers;
using Arcor2.ClientSdk.ClientServices.Models;
using UnityEngine;

public class CollisionObjectBinding : MonoBehaviour
{
    public ActionObjectManager ActionObjectManager { get; private set; }
    public ObjectTypeManager ObjectTypeManager { get; private set; }

    public string ActionObjectId { get; private set; }
    public string ObjectTypeId { get; private set; }
    public string BoxModelId { get; private set; }

    private bool _initialized;
    private bool _isPersisting;

    private Vector3 _savedLocalPosition;
    private Quaternion _savedLocalRotation;
    private Vector3 _savedLocalScale;

    public void Initialize(ActionObjectManager actionObjectManager, Transform origin)
    {
        if (actionObjectManager == null)
        {
            return;
        }

        ActionObjectManager = actionObjectManager;
        ObjectTypeManager = actionObjectManager.ObjectType;
        ActionObjectId = actionObjectManager.Data?.Meta?.Id;
        ObjectTypeId = actionObjectManager.ObjectType?.Id;
        BoxModelId = actionObjectManager.ObjectType?.Data?.Meta?.ObjectModel?.Box?.Id;

        CaptureSavedState(origin);
        _initialized = true;
    }

    public async Task PersistIfChangedAsync(Transform origin)
    {
        if (!_initialized || _isPersisting || origin == null || ActionObjectManager == null)
        {
            return;
        }

        _isPersisting = true;
        try
        {
            Vector3 localPosition = origin.InverseTransformPoint(transform.position);
            Quaternion localRotation = Quaternion.Inverse(origin.rotation) * transform.rotation;
            Vector3 localScale = transform.localScale;

            bool poseChanged =
                Vector3.Distance(localPosition, _savedLocalPosition) > 0.0005f ||
                Quaternion.Angle(localRotation, _savedLocalRotation) > 0.1f;

            bool scaleChanged = Vector3.Distance(localScale, _savedLocalScale) > 0.0005f;

            if (poseChanged)
            {
                await TryUpdatePoseAsync(localPosition, localRotation);
            }

            if (scaleChanged)
            {
                await TryUpdateBoxScaleAsync(localScale);
            }

            CaptureSavedState(origin);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to persist collision object '{ActionObjectId}': {ex}");
        }
        finally
        {
            _isPersisting = false;
        }
    }

    public async Task<bool> RemoveAsync(bool force = true)
    {
        if (ActionObjectManager == null)
        {
            return false;
        }

        try
        {
            await ActionObjectManager.RemoveAsync(force);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to remove collision object '{ActionObjectId}': {ex}");
            return false;
        }
    }

    private void CaptureSavedState(Transform origin)
    {
        if (origin == null)
        {
            return;
        }

        _savedLocalPosition = origin.InverseTransformPoint(transform.position);
        _savedLocalRotation = Quaternion.Inverse(origin.rotation) * transform.rotation;
        _savedLocalScale = transform.localScale;
    }

    private async Task TryUpdatePoseAsync(Vector3 localPosition, Quaternion localRotation)
    {
        var pose = new Arcor2.ClientSdk.Communication.OpenApi.Models.Pose(
            position: DataHelper.Vector3ToPosition(TransformConvertor.UnityToROS(localPosition)),
            orientation: DataHelper.QuaternionToOrientation(TransformConvertor.UnityToROS(localRotation)));

        await ActionObjectManager.UpdatePoseAsync(pose);
    }

    private async Task TryUpdateBoxScaleAsync(Vector3 localScale)
    {
        if (ObjectTypeManager == null)
        {
            return;
        }

        var model = new BoxCollisionModel(
            (decimal)localScale.x,
            (decimal)localScale.y,
            (decimal)localScale.z);
        await ObjectTypeManager.UpdateObjectModel(model);
    }
}
