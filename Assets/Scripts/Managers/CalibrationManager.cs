using Arcor2.ClientSdk.ClientServices.Enums;
using Arcor2.ClientSdk.Communication.OpenApi.Models;
using Meta.XR;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class CalibrationManager : MonoBehaviour
{
    public CommunicationManager CommunicationManager;
    [SerializeField] private PassthroughCameraAccess m_passthroughCameraAccess;
    bool calibrating = false;
    public Transform calibrationCube;
    private Matrix4x4? displayMatrix;
    private Matrix4x4? projectionMatrix;

    private Matrix4x4 ARCameraTransformMatrix;

    private void Awake()
    {
        EnsurePassthroughCameraAccess();
    }

    public void Calibrate()
    {
        if (!calibrating)
            CaptureAndSendImageRoutine();
    }

    private async void CaptureAndSendImageRoutine()
    {
        var cameraAccess = EnsurePassthroughCameraAccess();
        if (cameraAccess == null)
        {
            Debug.LogError("PassthroughCameraAccess is not available.");
            return;
        }

        calibrating = true;
        while (!cameraAccess.IsPlaying)
        {
            await Task.Yield();
        }

        try
        {
            var tex = await CaptureFrameAsync(cameraAccess);
            if (tex == null)
            {
                Debug.LogError("Failed to capture passthrough camera frame.");
                return;
            }

            try
            {
                string imageString = Encoding.GetEncoding("iso-8859-1").GetString(tex.EncodeToJPG());
                var cameraPose = cameraAccess.GetCameraPose();
                ARCameraTransformMatrix = Matrix4x4.TRS(cameraPose.position, cameraPose.rotation, Vector3.one);

                var intrinsics = cameraAccess.Intrinsics;
                CameraParameters CameraParameters = new CameraParameters(
                    cx: (decimal)intrinsics.PrincipalPoint.x,
                    cy: (decimal)intrinsics.PrincipalPoint.y,
                    distCoefs: new List<decimal>() { 0, 0, 0, 0 },
                    fx: (decimal)intrinsics.FocalLength.x,
                    fy: (decimal)intrinsics.FocalLength.y);

                GetCameraPoseResponse response = await CommunicationManager.Arcor2Session
                    .GetUnderlyingArcor2Client()
                    .GetCameraPoseAsync(new GetCameraPoseRequestArgs(
                        cameraParameters: CameraParameters,
                        image: imageString,
                        true));

                if (!response.Result)
                {
                    return;
                }

                var markerEstimatedPose = response.Data;
                Arcor2.ClientSdk.Communication.OpenApi.Models.Pose markerPose = markerEstimatedPose.Pose;

                Vector3 markerPositionReceived = new(
                    (float)markerPose.Position.X,
                    (float)markerPose.Position.Y,
                    (float)markerPose.Position.Z);
                Quaternion markerRotationReceived = new(
                    (float)markerPose.Orientation.X,
                    (float)markerPose.Orientation.Y,
                    (float)markerPose.Orientation.Z,
                    (float)markerPose.Orientation.W);

                Matrix4x4 markerMatrix = Matrix4x4.TRS(markerPositionReceived, markerRotationReceived, Vector3.one);

                Vector3 markerPosition = OpenCVToUnity(GetPositionFromMatrix(markerMatrix));
                Quaternion markerRotation = OpenCVToUnity(GetQuaternionFromMatrix(markerMatrix));

                calibrationCube.localPosition = ARCameraTransformMatrix.MultiplyPoint3x4(markerPosition);
                calibrationCube.localRotation = GetQuaternionFromMatrix(ARCameraTransformMatrix) * markerRotation;
            }
            finally
            {
                Destroy(tex);
            }
        }
        finally
        {
            calibrating = false;
        }
    }

    private PassthroughCameraAccess EnsurePassthroughCameraAccess()
    {
        if (m_passthroughCameraAccess != null)
        {
            return m_passthroughCameraAccess;
        }

        m_passthroughCameraAccess = FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
        if (m_passthroughCameraAccess != null)
        {
            return m_passthroughCameraAccess;
        }

        var cameraAccessObject = new GameObject("PassthroughCameraAccess");
        cameraAccessObject.transform.SetParent(transform, false);
        m_passthroughCameraAccess = cameraAccessObject.AddComponent<PassthroughCameraAccess>();
        m_passthroughCameraAccess.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        return m_passthroughCameraAccess;
    }

    private static async Task<Texture2D> CaptureFrameAsync(PassthroughCameraAccess cameraAccess)
    {
        var sourceTexture = cameraAccess.GetTexture();
        if (sourceTexture == null)
        {
            return null;
        }

        var request = AsyncGPUReadback.Request(sourceTexture, 0, TextureFormat.RGBA32);
        while (!request.done)
        {
            await Task.Yield();
        }

        if (request.hasError)
        {
            Debug.LogError("AsyncGPUReadback failed for passthrough camera frame.");
            return null;
        }

        var resolution = cameraAccess.CurrentResolution;
        var tex = new Texture2D(resolution.x, resolution.y, TextureFormat.RGBA32, false);
        NativeArray<byte> rawData = request.GetData<byte>();
        tex.LoadRawTextureData(rawData);
        tex.Apply();
        return tex;
    }

    public static Vector3 OpenCVToUnity(Vector3 position)
    {
        return new Vector3(position.x, -position.y, position.z);
    }

    public static Quaternion OpenCVToUnity(Quaternion rotation)
    {
        return new Quaternion(-rotation.x, rotation.y, -rotation.z, rotation.w);
    }

    public static Matrix4x4 OpenCVToUnity(Matrix4x4 m)
    {
        m.m10 *= -1;
        m.m11 *= -1;
        m.m12 *= -1;
        m.m13 *= -1;

        return m;
    }

    public static Vector3 GetPositionFromMatrix(Matrix4x4 m)
    {
        return m.GetColumn(3);
    }

    public static Quaternion GetQuaternionFromMatrix(Matrix4x4 m)
    {
        if (m.GetColumn(2) == Vector4.zero)
        {
            Debug.Log("QuaternionFromMatrix got zero matrix.");
            return Quaternion.identity;
        }
        return Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
    }
}
