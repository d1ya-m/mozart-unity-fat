using System;
using System.Threading.Tasks;
using Arcor2.ClientSdk.ClientServices.Managers;
using UnityEngine;

public class MatActionObjectBinding : MonoBehaviour
{
    public ActionObjectManager ActionObjectManager { get; private set; }
    public string ActionObjectId { get; private set; }

    private bool _initialized;
    private bool _isPersisting;

    private Vector3 _savedLocalPosition;
    private Quaternion _savedLocalRotation;

    public void Initialize(ActionObjectManager actionObjectManager, Transform origin)
    {
        if (actionObjectManager == null || origin == null)
        {
            return;
        }

        ActionObjectManager = actionObjectManager;
        ActionObjectId = actionObjectManager.Data?.Meta?.Id;
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

            bool poseChanged =
                Vector3.Distance(localPosition, _savedLocalPosition) > 0.0005f ||
                Quaternion.Angle(localRotation, _savedLocalRotation) > 0.1f;

            if (poseChanged)
            {
                var pose = new Arcor2.ClientSdk.Communication.OpenApi.Models.Pose(
                    position: DataHelper.Vector3ToPosition(TransformConvertor.UnityToROS(localPosition)),
                    orientation: DataHelper.QuaternionToOrientation(TransformConvertor.UnityToROS(localRotation)));

                await ActionObjectManager.UpdatePoseAsync(pose);
            }

            CaptureSavedState(origin);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to persist MAT action object '{ActionObjectId}': {ex}");
        }
        finally
        {
            _isPersisting = false;
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
    }
}
