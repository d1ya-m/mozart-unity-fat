using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Meta.XR.EnvironmentDepth;

/// <summary>
/// STAGE 1 (steps 1-3): On-device keyframe capture — SELECTION + POSE PERSISTENCE.
///
/// This component walks toward the "capture depth + pose as the user moves"
/// pipeline. Implemented so far, each verifiable on-device via `adb logcat`:
///
///   STEP 1 — Pose tracking: every second, log the head (CenterEye) world pose,
///            using the SAME tracking-space convention ObjectPicker/GameManager use
///            (OVRCameraRig.trackingSpace).
///
///   STEP 2 — Keyframe selection ("normalize distance/angle"): a viewpoint is
///            rejected only if some existing keyframe is BOTH close in position AND
///            similar in view angle ("already covered from here").
///
///   STEP 3 — Pose persistence (THIS STEP): each accepted keyframe is written to
///            captures/&lt;sessionId&gt;/frame_XXXX.json in Open3D's
///            PinholeCameraParameters schema (intrinsic + world-to-camera extrinsic),
///            byte-compatible with the existing ScreenCamera_*.json files so Stage 2
///            can load it with o3d.io.read_pinhole_camera_parameters. A manifest.json
///            lists every frame. NO depth image yet (that is step 4).
///
///            ⚠️ COORDINATE CONVERSION is the #1 risk here: Unity is left-handed
///            (X-right, Y-up, Z-forward) and the mesh pipeline additionally X-flips
///            on import (MeshDownloadManager.flipObjXAxisForUnity). Open3D/OpenCV
///            cameras are right-handed (X-right, Y-DOWN, Z-forward-into-scene). The
///            extrinsic is built to match; the RAW Unity pose is ALSO written into
///            every JSON (unity_position / unity_rotation_quat_xyzw) so if the
///            extrinsic is wrong we can recompute it in Python WITHOUT re-scanning.
///
/// Output on device:  Application.persistentDataPath/captures/&lt;sessionId&gt;/
/// Pull it with (Git Bash):
///   "$ADB" pull /sdcard/Android/data/&lt;package&gt;/files/captures ./captures
/// (the exact path is logged once at Start under the KFCAP tag).
///
/// Read the logs with (Git Bash), matching the project's §7 debugging method:
///   "$ADB" logcat -s Unity | grep -E "KFCAP"
///
/// NOTE (§6 Bug B): OVR poses are NOT delivered over Quest Link in the editor.
/// Test from a built APK on-device, not from Play mode over Link.
/// </summary>
public class KeyframeCaptureManager : MonoBehaviour
{
    [Header("Keyframe selection thresholds")]
    [Tooltip("Capture a new keyframe once the head has moved at least this far (metres) " +
             "from every existing keyframe — OR turned past the angle threshold below. " +
             "Larger = fewer keyframes.")]
    [SerializeField] private float positionThresholdMeters = 0.3f;

    [Tooltip("Capture a new keyframe once the view direction differs by at least this " +
             "many degrees from every existing keyframe — OR moved past the position " +
             "threshold above. Larger = fewer keyframes.")]
    [SerializeField] private float angleThresholdDegrees = 20f;

    [Tooltip("Safety cap so a long walk can't capture unbounded keyframes.")]
    [SerializeField] private int maxKeyframes = 300;

    [Header("Capture control")]
    [Tooltip("Master switch. Driven by the ToolMenu 'Scan Room' toggle via SetScanMode(). " +
             "Starts OFF — the user must press the Scan Room button (or controller A/X) " +
             "to begin, and press again to stop. Can also be toggled here for testing.")]
    [SerializeField] private bool capturing = false;

    [Tooltip("The ToolMenu 'Scan Room' Toggle (TextTileButton.Button). Assign in Inspector.")]
    [SerializeField] private Toggle _scanToggle;

    [Tooltip("The sublabel TMP text on the 'Scan Room' button — shows live keyframe count.")]
    [SerializeField] private TMPro.TMP_Text _scanSublabel;

    [Tooltip("When ON, prints verbose KFCAP logs (read via adb logcat). Warnings/errors " +
             "always print regardless.")]
    [SerializeField] private bool verboseLogging = true;

    [Header("Depth/intrinsics")]
    [Tooltip("Resolution the depth image is captured at. The intrinsic matrix is " +
             "built for this pixel size, so keep it consistent. 512x512 is a safe " +
             "default for the Quest environment-depth texture.")]
    [SerializeField] private int captureWidth = 512;
    [SerializeField] private int captureHeight = 512;

    [Tooltip("STEP 4: also capture a metric depth PNG per keyframe. Turn OFF to fall " +
             "back to pose-only capture (steps 1-3).")]
    [SerializeField] private bool captureDepth = true;

    [Tooltip("The Custom/EnvDepthCapture shader. If left empty it is looked up by " +
             "name (Shader.Find), which requires it to be included in the build.")]
    [SerializeField] private Shader depthCaptureShader;

    // Depth PNGs are 16-bit; we store depth in MILLIMETRES (metres * 1000) so the
    // 0..65535 range covers 0..65.5 m at 1 mm resolution — recorded in the manifest
    // as depth_scale so Stage 2 divides back to metres.
    private const float DepthScaleToMillimetres = 1000f;

    // One captured viewpoint. Step 3 adds nothing to memory (files are written at
    // capture time); we still keep the pose so the novelty test can compare.
    private struct Keyframe
    {
        public Vector3 Position;   // head world position at capture
        public Vector3 Forward;    // head world forward (normalised) at capture
        public Quaternion Rotation;
    }

    private readonly List<Keyframe> _keyframes = new List<Keyframe>();

    // The OVRCameraRig.trackingSpace transform — OVR poses are reported in this
    // space and transformed to world through it. Same source ObjectPicker uses.
    private Transform _trackingSpace;

    // The camera we read the field-of-view from to build the pinhole intrinsic.
    private Camera _headCamera;

    // Where this session's frame_*.json + manifest.json are written.
    private string _sessionDir;
    private string _sessionId;

    // Becomes true once the headset reports a real (non-identity) eye pose, so the
    // first uninitialised (0,0,0) frame is never captured.
    private bool _headTrackingReady;

    // Depth capture (step 4)
    private Material _depthCaptureMaterial;
    private RenderTexture _depthRT;    // RFloat, metres, blit target
    private bool _depthReady;          // shader/material/RT all set up OK
    private EnvironmentDepthManager _depthManager; // to gate on IsDepthAvailable
    const string ScenePerm = "com.oculus.permission.USE_SCENE";

    // Diagnostics
    private float _lastPoseLogTime;
    private int _skippedCount;

    private void Start()
    {
        _trackingSpace = FindTrackingSpaceTransform();
        if (_trackingSpace != null)
            VLog($"trackingSpace found: {_trackingSpace.name}");
        else
            Debug.LogWarning("[KFCAP] trackingSpace not found — falling back to Camera.main. " +
                             "Poses may be wrong until an OVRCameraRig exists.");

        _headCamera = Camera.main;
        if (_headCamera == null)
            Debug.LogWarning("[KFCAP] Camera.main not found — intrinsics will use a default FOV.");

        // Fresh session folder per run so captures never mix across runs.
        _sessionId = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _sessionDir = Path.Combine(Application.persistentDataPath, "captures", _sessionId);
        Directory.CreateDirectory(_sessionDir);

        // Log the exact on-device path so you know what to `adb pull`.
        Debug.Log($"[KFCAP] Session '{_sessionId}' writing to: {_sessionDir}");

        if (captureDepth)
            SetupDepthCapture();

        VLog($"Ready. posThresh={positionThresholdMeters}m angleThresh={angleThresholdDegrees}° " +
             $"maxKeyframes={maxKeyframes} capturing={capturing} capture={captureWidth}x{captureHeight} " +
             $"depth={(_depthReady ? "ON" : "off")}");
    }

    private void SetupDepthCapture()
    {
        Shader shader = depthCaptureShader != null ? depthCaptureShader : Shader.Find("Custom/EnvDepthCapture");
        if (shader == null)
        {
            Debug.LogError("[KFCAP] Custom/EnvDepthCapture shader not found — depth capture disabled. " +
                           "Assign it in the inspector or add it to Always-Included Shaders.");
            return;
        }

        _depthCaptureMaterial = new Material(shader);
        _depthRT = new RenderTexture(captureWidth, captureHeight, 0, GraphicsFormat.R32_SFloat)
        {
            name = "KFCAP_DepthRT",
            enableRandomWrite = false
        };
        _depthRT.Create();

        // The environment-depth texture is only populated once EnvironmentDepthManager
        // is enabled AND USE_SCENE is granted AND IsDepthAvailable has flipped true.
        // Blitting before then samples an empty texture -> a constant near value
        // (the "all pixels 0.13 m" symptom). Find the manager and request permission.
        _depthManager = FindFirstObjectByType<EnvironmentDepthManager>();
        if (_depthManager == null)
            Debug.LogError("[KFCAP] No EnvironmentDepthManager in scene — depth will be empty. " +
                           "Add one (Meta Building Block: Environment Depth Occlusion).");
        else if (!_depthManager.enabled)
            Debug.LogWarning("[KFCAP] EnvironmentDepthManager is DISABLED — enabling it for capture.");

        if (_depthManager != null) _depthManager.enabled = true;
        if (!Permission.HasUserAuthorizedPermission(ScenePerm))
        {
            Debug.Log("[KFCAP] Requesting USE_SCENE permission (needed for env depth)...");
            Permission.RequestUserPermission(ScenePerm);
        }

        ResolveDepthReflection(); // resolve SDK internals for clean proj/view capture

        _depthReady = true;
        VLog($"Depth capture initialised. manager={(_depthManager != null)} " +
             $"supported={EnvironmentDepthManager.IsSupported}");
    }

    private void OnDestroy()
    {
        if (_depthCaptureMaterial != null) Destroy(_depthCaptureMaterial);
        if (_depthRT != null) { _depthRT.Release(); Destroy(_depthRT); }
    }

    private void Update()
    {
        // Read the current head (CenterEye) world pose every frame.
        if (!TryGetHeadPose(out Vector3 headPos, out Quaternion headRot))
            return;

        // Reject frames before head tracking is initialised. On the first frame(s)
        // CenterEye reports the identity pose (position exactly (0,0,0), looking
        // down +Z) — writing that would put a bogus camera at the origin in
        // reconstruction. A real headset pose has a non-trivial height (~1.1 m eye
        // level), so a near-zero local eye position means "not tracking yet".
        if (!_headTrackingReady)
        {
            Vector3 eyeLocal = UnityEngine.XR.InputTracking.GetLocalPosition(UnityEngine.XR.XRNode.CenterEye);
            if (eyeLocal.sqrMagnitude < 1e-4f) // < 1 cm from tracking-space origin
                return; // still the uninitialised identity pose — skip this frame
            _headTrackingReady = true;
            VLog("Head tracking initialised — capture enabled.");
        }

        Vector3 headForward = headRot * Vector3.forward;

        // --- STEP 1: pose heartbeat (once per second) ---------------------------
        // Proves on-device that we read a sane, moving camera pose. Independent of
        // capturing on/off so you can sanity-check tracking even before scanning.
        if (Time.time - _lastPoseLogTime >= 1f)
        {
            _lastPoseLogTime = Time.time;
            VLog($"POSE pos=({headPos.x:F2},{headPos.y:F2},{headPos.z:F2}) " +
                 $"fwd=({headForward.x:F2},{headForward.y:F2},{headForward.z:F2}) " +
                 $"keyframes={_keyframes.Count} skipped={_skippedCount}");
        }

        // Controller shortcut: right A button or left X button toggles scanning.
        // Works even when the ToolMenu panel is hidden. Routes through SetScanMode
        // so the session counter resets on start, exactly like the UI button. Also
        // syncs the UI toggle's visual state so button and controller never desync.
        if (OVRInput.GetDown(OVRInput.Button.One) || OVRInput.GetDown(OVRInput.Button.Three))
        {
            bool next = !capturing;
            Debug.Log($"[KFCAP] Scan toggled via controller — capturing={next}");
            if (_scanToggle != null)
                _scanToggle.isOn = next;   // fires onValueChanged -> SetScanMode
            else
                SetScanMode(next);         // no UI wired: call directly
        }

        if (!capturing)
            return;

        // --- Tracking sanity guards --------------------------------------------
        // Reject poses near the origin (headset removed / tracking lost).
        if (headPos.sqrMagnitude < 0.01f)  // < 10 cm from world origin
        {
            _skippedCount++;
            return;
        }
        // Reject implausible jumps from the last accepted keyframe (> 2 m in one frame).
        if (_keyframes.Count > 0 &&
            Vector3.Distance(headPos, _keyframes[_keyframes.Count - 1].Position) > 2.0f)
        {
            Debug.LogWarning($"[KFCAP] Tracking glitch — pos jumped >2m, skipping frame.");
            _skippedCount++;
            return;
        }

        // --- STEP 2: keyframe selection ----------------------------------------
        if (_keyframes.Count >= maxKeyframes)
            return; // safety cap reached — stop capturing silently

        if (IsNovelViewpoint(headPos, headForward))
        {
            AcceptKeyframe(headPos, headForward, headRot);
        }
        else
        {
            _skippedCount++;
            // Deliberately NOT logged per-frame (would flood logcat) — the skip
            // count is surfaced in the once-per-second POSE line above.
        }
    }

    /// <summary>
    /// STEP 2 core: a viewpoint is NOVEL unless some existing keyframe already
    /// covers it — i.e. is BOTH within positionThreshold AND within angleThreshold.
    /// Far enough away OR pointing a different direction ⇒ novel ⇒ capture.
    /// </summary>
    private bool IsNovelViewpoint(Vector3 pos, Vector3 forward)
    {
        foreach (Keyframe kf in _keyframes)
        {
            bool closeInPosition = Vector3.Distance(pos, kf.Position) < positionThresholdMeters;
            bool similarAngle = Vector3.Angle(forward, kf.Forward) < angleThresholdDegrees;

            // Already covered from a nearby spot looking a similar direction.
            if (closeInPosition && similarAngle)
                return false;
        }
        return true;
    }

    private void AcceptKeyframe(Vector3 pos, Vector3 forward, Quaternion rot)
    {
        int index = _keyframes.Count;
        _keyframes.Add(new Keyframe { Position = pos, Forward = forward, Rotation = rot });

        // The SDK publishes the DEPTH camera's own reprojection matrix as a global
        // matrix array (left eye = slice 0, the one we sample). This is
        //   proj * view * trackingSpaceWorldToLocal
        // built from the depth camera's TRUE fov + its OWN create-pose (synced to the
        // depth frame). It maps a TRACKING-SPACE-LOCAL world point -> depth clip space.
        // Capturing THIS (instead of the render-camera FOV + head pose) fixes both the
        // wrong-focal fan-out and the depth/head timing smear. We invert it offline to
        // unproject each depth pixel correctly. See EnvironmentDepthManager.OnBeforeRender.
        Matrix4x4 depthReproj = GetDepthReprojectionMatrix();

        // Clean depth-camera params (proj/view/fov) read from the SDK's internal
        // DepthFrameDesc via reflection — the reliable path for offline unprojection.
        DepthCamParams depthCam = GetDepthCamParams();

        // STEP 3: persist the pose + intrinsic to frame_XXXX.json, then refresh the
        // manifest so the session is always consistent even if the app is killed.
        WriteFrameJson(index, pos, rot, depthReproj, depthCam);

        // STEP 4: capture the metric depth PNG for this keyframe (best-effort — a
        // failed depth read still leaves a valid pose-only frame). Only blit when the
        // SDK reports depth is actually available; otherwise we'd sample an empty
        // texture and get a constant value (the 0.13 m bug).
        if (captureDepth && _depthReady)
        {
            bool available = _depthManager != null && _depthManager.IsDepthAvailable;
            if (available)
                CaptureDepthPng(index);
            else
                Debug.LogWarning($"[KFCAP] frame {index}: depth NOT available yet " +
                                 $"(manager={_depthManager != null} perm={Permission.HasUserAuthorizedPermission(ScenePerm)}) " +
                                 "— skipping depth PNG for this frame.");
        }

        WriteManifest();
        RefreshScanLabel();

        // Always log accepted keyframes (even without verbose) — this is the signal
        // that proves the selection logic is firing as the user moves.
        Debug.Log($"[KFCAP] CAPTURE #{index} " +
                  $"pos=({pos.x:F2},{pos.y:F2},{pos.z:F2}) " +
                  $"fwd=({forward.x:F2},{forward.y:F2},{forward.z:F2}) " +
                  $"(skipped so far={_skippedCount})");
    }

    // ---------------------------------------------------------------------------
    // STEP 3: pose → Open3D PinholeCameraParameters JSON
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Writes captures/&lt;session&gt;/frame_XXXX.json in the same schema as the
    /// project's ScreenCamera_*.json (Open3D PinholeCameraParameters): a 3x3
    /// column-major intrinsic and a 4x4 column-major world-to-camera extrinsic.
    /// Extra unity_* fields carry the raw Unity pose so the conversion can be
    /// re-derived in Python if the extrinsic proves wrong (see class summary).
    /// </summary>
    private void WriteFrameJson(int index, Vector3 worldPos, Quaternion worldRot,
                                Matrix4x4 depthReproj, DepthCamParams depthCam)
    {
        BuildIntrinsic(out float fx, out float fy, out float cx, out float cy);
        float[] extrinsic = BuildOpen3DExtrinsic(worldPos, worldRot); // 16, column-major

        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("\t\"class_name\" : \"PinholeCameraParameters\",\n");

        // extrinsic: 16 floats, column-major (Open3D convention).
        sb.Append("\t\"extrinsic\" : \n\t[\n");
        for (int i = 0; i < 16; i++)
            sb.Append("\t\t").Append(F(extrinsic[i])).Append(i < 15 ? ",\n" : "\n");
        sb.Append("\t],\n");

        // intrinsic: column-major 3x3 = [fx,0,0, 0,fy,0, cx,cy,1].
        sb.Append("\t\"intrinsic\" : \n\t{\n");
        sb.Append("\t\t\"height\" : ").Append(captureHeight).Append(",\n");
        sb.Append("\t\t\"intrinsic_matrix\" : \n\t\t[\n");
        float[] K = { fx, 0f, 0f, 0f, fy, 0f, cx, cy, 1f };
        for (int i = 0; i < 9; i++)
            sb.Append("\t\t\t").Append(F(K[i])).Append(i < 8 ? ",\n" : "\n");
        sb.Append("\t\t],\n");
        sb.Append("\t\t\"width\" : ").Append(captureWidth).Append("\n");
        sb.Append("\t},\n");

        // Raw Unity pose (NOT part of the Open3D schema; ignored by Open3D readers,
        // kept so we can recompute the extrinsic offline without re-scanning).
        sb.Append("\t\"unity_position\" : [")
          .Append(F(worldPos.x)).Append(", ").Append(F(worldPos.y)).Append(", ").Append(F(worldPos.z)).Append("],\n");
        sb.Append("\t\"unity_rotation_quat_xyzw\" : [")
          .Append(F(worldRot.x)).Append(", ").Append(F(worldRot.y)).Append(", ")
          .Append(F(worldRot.z)).Append(", ").Append(F(worldRot.w)).Append("],\n");

        // Head view matrix: world->eye built from the Unity head pose.
        // This is the reliable rotation source — use with depth_fov_tangents for
        // unprojection when depth_view rotation is suspected to drift.
        // Convention: right-handed, camera looks down -Z (OpenCV/Open3D).
        Matrix4x4 headView = BuildHeadViewMatrix(worldPos, worldRot);
        sb.Append("\t\"head_view\" : [");
        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
                sb.Append(F(headView[r, c])).Append((c == 3 && r == 3) ? "" : ", ");
        sb.Append("],\n");

        // THE depth reprojection matrix (proj * view * trackingSpaceWorldToLocal) for
        // the LEFT-eye depth slice. Maps a tracking-space-LOCAL world point -> depth
        // clip space. Written COLUMN-MAJOR (Unity Matrix4x4 mNN indexing: mRowCol).
        // Offline we invert it to unproject the depth PNG with the CORRECT depth FOV
        // and the depth camera's OWN pose. Also record the tracking-space world
        // transform so the local-space points can be lifted back to world.
        sb.Append("\t\"depth_reprojection\" : [");
        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
                sb.Append(F(depthReproj[r, c]))
                  .Append((c == 3 && r == 3) ? "" : ", ");
        sb.Append("],\n");

        // trackingSpace local->world (so world = trackingSpaceLocalToWorld * localPt).
        Matrix4x4 tsLocalToWorld = _trackingSpace != null
            ? _trackingSpace.localToWorldMatrix : Matrix4x4.identity;
        sb.Append("\t\"tracking_space_local_to_world\" : [");
        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
                sb.Append(F(tsLocalToWorld[r, c]))
                  .Append((c == 3 && r == 3) ? "" : ", ");
        sb.Append("],\n");

        // CLEAN depth-camera params (the reliable offline-unprojection path). proj is
        // the SDK's perspective matrix (eye->clip); view is world->eye using the depth
        // camera's OWN create-pose. Both column-major. Offline: for pixel (x,y) at
        // metric depth z, eye-space point = (Xe*z, Ye*z, -z) with Xe,Ye from proj's
        // fov, then world = inv(view) * eye. fov tangents + near/far also stored.
        if (depthCam.valid)
        {
            sb.Append("\t\"depth_proj\" : [");
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                    sb.Append(F(depthCam.proj[r, c])).Append((c == 3 && r == 3) ? "" : ", ");
            sb.Append("],\n");

            sb.Append("\t\"depth_view\" : [");
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                    sb.Append(F(depthCam.view[r, c])).Append((c == 3 && r == 3) ? "" : ", ");
            sb.Append("],\n");

            sb.Append("\t\"depth_fov_tangents\" : [")
              .Append(F(depthCam.fovLeftTan)).Append(", ").Append(F(depthCam.fovRightTan)).Append(", ")
              .Append(F(depthCam.fovTopTan)).Append(", ").Append(F(depthCam.fovDownTan)).Append("],\n");
            sb.Append("\t\"depth_near_far\" : [")
              .Append(F(depthCam.nearZ)).Append(", ").Append(F(depthCam.farZ)).Append("],\n");
        }

        sb.Append("\t\"version_major\" : 1,\n");
        sb.Append("\t\"version_minor\" : 0\n");
        sb.Append("}\n");

        string path = Path.Combine(_sessionDir, $"frame_{index:D4}.json");
        try { File.WriteAllText(path, sb.ToString()); }
        catch (System.Exception ex) { Debug.LogError($"[KFCAP] Failed to write {path}: {ex.Message}"); }
    }

    // Builds a right-handed world->eye view matrix from the Unity head pose.
    // Unity is left-handed (Z-forward), OpenCV/Open3D cameras are right-handed
    // (Z-forward-into-scene, Y-down). We flip Y and Z to convert.
    private static Matrix4x4 BuildHeadViewMatrix(Vector3 pos, Quaternion rot)
    {
        // World->Unity-eye: standard TRS inverse.
        Matrix4x4 unityView = Matrix4x4.TRS(pos, rot, Vector3.one).inverse;
        // Flip Y and Z rows to go Unity->OpenCV convention.
        Matrix4x4 m = unityView;
        m.m10 = -unityView.m10; m.m11 = -unityView.m11;
        m.m12 = -unityView.m12; m.m13 = -unityView.m13;
        m.m20 = -unityView.m20; m.m21 = -unityView.m21;
        m.m22 = -unityView.m22; m.m23 = -unityView.m23;
        return m;
    }

    // Reads the SDK's global depth reprojection matrix array and returns the
    // LEFT-eye (slice 0) matrix — the same slice EnvDepthCapture samples. The SDK
    // sets "_EnvironmentDepthReprojectionMatrices" every frame in
    // EnvironmentDepthManager.OnBeforeRender. Returns identity if not yet published
    // (offline unprojection can then detect and skip the frame).
    private static readonly int _reprojMatricesId =
        Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");

    private Matrix4x4 GetDepthReprojectionMatrix()
    {
        Matrix4x4[] mats = Shader.GetGlobalMatrixArray(_reprojMatricesId);
        if (mats != null && mats.Length > 0)
            return mats[0]; // slice 0 = left eye
        Debug.LogWarning("[KFCAP] depth reprojection matrix not published yet — " +
                         "frame will fall back to the (less accurate) pose extrinsic.");
        return Matrix4x4.identity;
    }

    // Holds the clean depth-camera parameters read from the SDK's internal
    // DepthFrameDesc (left eye). proj + view are the SDK's OWN matrices from
    // EnvironmentDepthUtils.CalculateDepthCameraMatrices, so unprojection offline is
    // a trivial, provably-correct pinhole — no reverse-engineering the composite
    // reprojection matrix. See §depth-params in the progress doc.
    private struct DepthCamParams
    {
        public bool valid;
        public Matrix4x4 proj;   // eye -> clip (SDK perspective)
        public Matrix4x4 view;   // world -> eye (depth camera's own create-pose)
        public float fovLeftTan, fovRightTan, fovTopTan, fovDownTan, nearZ, farZ;
    }

    // Reflection handles into the SDK internals (resolved once).
    private System.Reflection.FieldInfo _frameDescsField;   // EnvironmentDepthManager.frameDescriptors
    private System.Reflection.MethodInfo _calcMatricesMethod; // EnvironmentDepthUtils.CalculateDepthCameraMatrices
    private System.Type _frameDescType;
    private bool _reflectionResolved;

    private void ResolveDepthReflection()
    {
        _reflectionResolved = true;
        try
        {
            var mgrType = typeof(EnvironmentDepthManager);
            _frameDescsField = mgrType.GetField("frameDescriptors",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            var asm = mgrType.Assembly;
            _frameDescType = asm.GetType("Meta.XR.EnvironmentDepth.DepthFrameDesc");
            var utilsType = asm.GetType("Meta.XR.EnvironmentDepth.EnvironmentDepthUtils");
            _calcMatricesMethod = utilsType?.GetMethod("CalculateDepthCameraMatrices",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

            if (_frameDescsField == null || _frameDescType == null || _calcMatricesMethod == null)
                Debug.LogWarning("[KFCAP] Depth reflection incomplete — falling back to reprojection matrix. " +
                                 $"(field={_frameDescsField != null} type={_frameDescType != null} method={_calcMatricesMethod != null})");
            else
                VLog("Depth reflection resolved — will capture clean proj/view per frame.");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[KFCAP] Depth reflection failed: {ex.Message}");
        }
    }

    private DepthCamParams GetDepthCamParams()
    {
        var p = new DepthCamParams { valid = false };
        if (!_reflectionResolved) ResolveDepthReflection();
        if (_depthManager == null || _frameDescsField == null || _calcMatricesMethod == null)
            return p;

        try
        {
            // frameDescriptors is a DepthFrameDesc[]; take slice 0 (left eye).
            var descs = (System.Array)_frameDescsField.GetValue(_depthManager);
            if (descs == null || descs.Length == 0) return p;
            object desc0 = descs.GetValue(0);

            // Pull the raw FOV/near/far off the struct (internal fields).
            var t = _frameDescType;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            p.fovLeftTan  = (float)t.GetField("fovLeftAngleTangent", bf).GetValue(desc0);
            p.fovRightTan = (float)t.GetField("fovRightAngleTangent", bf).GetValue(desc0);
            p.fovTopTan   = (float)t.GetField("fovTopAngleTangent", bf).GetValue(desc0);
            p.fovDownTan  = (float)t.GetField("fovDownAngleTangent", bf).GetValue(desc0);
            p.nearZ       = (float)t.GetField("nearZ", bf).GetValue(desc0);
            p.farZ        = (float)t.GetField("farZ", bf).GetValue(desc0);

            // Call the SDK's own matrix builder: out proj, out view.
            object[] args = { desc0, null, null };
            _calcMatricesMethod.Invoke(null, args);
            p.proj = (Matrix4x4)args[1];
            p.view = (Matrix4x4)args[2];
            p.valid = true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[KFCAP] GetDepthCamParams failed: {ex.Message}");
        }
        return p;
    }

    // ---------------------------------------------------------------------------
    // STEP 4: metric depth → 16-bit PNG
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Blits the SDK's global environment-depth texture through Custom/EnvDepthCapture
    /// (which outputs LINEAR METRES) into an RFloat RT, reads it back on the GPU
    /// thread via AsyncGPUReadback, converts metres→millimetres, and writes a 16-bit
    /// grayscale PNG frame_XXXX.depth.png. Millimetre scale is recorded in the
    /// manifest as depth_scale so Stage 2 divides back to metres.
    /// </summary>
    private void CaptureDepthPng(int index)
    {
        // Bind the SDK's global depth array to our material property so the shader
        // can sample slice 0 (left eye) explicitly. This bypasses SAMPLE_TEXTURE2D_X
        // which requires a stereo keyword that plain Graphics.Blit doesn't set.
        var envDepth = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
        if (envDepth != null)
            _depthCaptureMaterial.SetTexture("_EnvDepthArray", envDepth);

        Graphics.Blit(null, _depthRT, _depthCaptureMaterial);

        // Read back the float RT asynchronously. We capture 'index' in the callback
        // so the PNG is named correctly even though the readback completes later.
        AsyncGPUReadback.Request(_depthRT, 0, GraphicsFormat.R32_SFloat, req =>
        {
            if (req.hasError)
            {
                Debug.LogError($"[KFCAP] Depth readback failed for frame {index}.");
                return;
            }

            var floats = req.GetData<float>();
            int count = captureWidth * captureHeight;
            if (floats.Length < count)
            {
                Debug.LogError($"[KFCAP] Depth readback short: {floats.Length} < {count} for frame {index}.");
                return;
            }

            // Pack metres→millimetres into 16-bit big-endian gray PNG.
            byte[] png = EncodeGray16Png(floats, captureWidth, captureHeight, DepthScaleToMillimetres);
            string path = Path.Combine(_sessionDir, $"frame_{index:D4}.depth.png");
            try { File.WriteAllBytes(path, png); }
            catch (System.Exception ex) { Debug.LogError($"[KFCAP] Failed to write {path}: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Encodes a float depth buffer (metres) as a 16-bit grayscale PNG, values
    /// scaled by 'scale' (metres→millimetres) and clamped to 16-bit range. Hand-rolled
    /// because Unity's ImageConversion has no 16-bit-gray encoder. PNG here is a
    /// single-channel (grayscale) 16-bit image with one IDAT, no interlace.
    /// </summary>
    private static byte[] EncodeGray16Png(Unity.Collections.NativeArray<float> depthMetres, int w, int h, float scale)
    {
        // Raw pixel bytes: each row prefixed with filter byte 0, pixels big-endian u16.
        int stride = w * 2;
        byte[] raw = new byte[h * (stride + 1)];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            raw[p++] = 0; // filter type 0 (none)
            int rowStart = y * w;
            for (int x = 0; x < w; x++)
            {
                float mm = depthMetres[rowStart + x] * scale;
                int v = mm <= 0f ? 0 : (int)(mm + 0.5f);
                if (v > 65535) v = 65535;
                raw[p++] = (byte)((v >> 8) & 0xFF); // high byte first (PNG is big-endian)
                raw[p++] = (byte)(v & 0xFF);
            }
        }

        using var ms = new MemoryStream();
        // PNG signature
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        // IHDR: width, height, bit depth 16, colour type 0 (grayscale)
        byte[] ihdr = new byte[13];
        WriteBE32(ihdr, 0, (uint)w);
        WriteBE32(ihdr, 4, (uint)h);
        ihdr[8] = 16; // bit depth
        ihdr[9] = 0;  // colour type: grayscale
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(ms, "IHDR", ihdr);

        // IDAT: zlib-wrapped DEFLATE of the raw scanlines.
        byte[] compressed = ZlibCompress(raw);
        WriteChunk(ms, "IDAT", compressed);
        WriteChunk(ms, "IEND", System.Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteBE32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        byte[] len = new byte[4]; WriteBE32(len, 0, (uint)data.Length); s.Write(len, 0, 4);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        // CRC over type + data
        uint crc = Crc32(typeBytes, data);
        byte[] crcBytes = new byte[4]; WriteBE32(crcBytes, 0, crc); s.Write(crcBytes, 0, 4);
    }

    // zlib stream = 2-byte header + raw DEFLATE + 4-byte Adler-32. We use .NET's
    // DeflateStream for the DEFLATE body and add the zlib wrapper ourselves.
    private static byte[] ZlibCompress(byte[] data)
    {
        using var outMs = new MemoryStream();
        outMs.WriteByte(0x78); // CMF: 32K window, deflate
        outMs.WriteByte(0x01); // FLG: no dict, fastest — check bits make (0x78*256+0x01)%31==0
        using (var deflate = new System.IO.Compression.DeflateStream(outMs, System.IO.Compression.CompressionLevel.Fastest, true))
            deflate.Write(data, 0, data.Length);
        uint adler = Adler32(data);
        byte[] a = new byte[4]; WriteBE32(a, 0, adler); outMs.Write(a, 0, 4);
        return outMs.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        const uint MOD = 65521;
        uint a = 1, b = 0;
        foreach (byte d in data) { a = (a + d) % MOD; b = (b + a) % MOD; }
        return (b << 16) | a;
    }

    private static uint[] _crcTable;
    private static uint Crc32(byte[] type, byte[] data)
    {
        if (_crcTable == null)
        {
            _crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
                _crcTable[n] = c;
            }
        }
        uint crc = 0xFFFFFFFF;
        foreach (byte t in type) crc = _crcTable[(crc ^ t) & 0xFF] ^ (crc >> 8);
        foreach (byte d in data) crc = _crcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Pinhole intrinsic from the camera's vertical FOV and the capture resolution.
    /// fy = (H/2) / tan(vfov/2); fx = fy (square pixels, aspect folded into cx/cy);
    /// principal point at the image centre. Matches the ScreenCamera_*.json layout
    /// (cx = width/2 - 0.5, cy = height/2 - 0.5).
    /// </summary>
    private void BuildIntrinsic(out float fx, out float fy, out float cx, out float cy)
    {
        float vfovDeg = _headCamera != null ? _headCamera.fieldOfView : 60f;
        float vfovRad = vfovDeg * Mathf.Deg2Rad;
        fy = (captureHeight * 0.5f) / Mathf.Tan(vfovRad * 0.5f);
        fx = fy; // square pixels; Quest depth is ~square, refine in step 4 if needed
        cx = captureWidth * 0.5f - 0.5f;
        cy = captureHeight * 0.5f - 0.5f;
    }

    /// <summary>
    /// Builds the 4x4 world-to-camera extrinsic in Open3D's convention, returned as
    /// 16 floats COLUMN-MAJOR (Open3D stores extrinsics column-major).
    ///
    /// Conversion (see class summary for the why):
    ///   1. Unity camera-to-world = TRS(worldPos, worldRot). Unity is left-handed,
    ///      camera looks down +Z with +Y up.
    ///   2. Flip Y and Z of the CAMERA axes to reach the OpenCV/Open3D camera basis
    ///      (looks down +Z with +Y DOWN): C = camToWorld * diag(1,-1,-1,1).
    ///   3. Flip X of the WORLD to match the mesh pipeline's import X-flip
    ///      (MeshDownloadManager.flipObjXAxisForUnity): C = diag(-1,1,1,1) * C.
    ///   4. Extrinsic (world→camera) = inverse(C).
    /// </summary>
    private float[] BuildOpen3DExtrinsic(Vector3 worldPos, Quaternion worldRot)
    {
        Matrix4x4 camToWorld = Matrix4x4.TRS(worldPos, worldRot, Vector3.one);

        Matrix4x4 flipYZ = Matrix4x4.Scale(new Vector3(1f, -1f, -1f)); // camera basis
        Matrix4x4 flipX = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));   // world basis (mesh X-flip)

        Matrix4x4 c = flipX * camToWorld * flipYZ;   // camera-to-world in Open3D space
        Matrix4x4 extrinsic = c.inverse;             // world-to-camera

        // Unity Matrix4x4 is column-major already ([col][row] via m{col}{row}); emit
        // in column-major order (m00,m10,m20,m30, m01,...): that is exactly how
        // Open3D reads the flat 16-float array.
        return new float[]
        {
            extrinsic.m00, extrinsic.m10, extrinsic.m20, extrinsic.m30,
            extrinsic.m01, extrinsic.m11, extrinsic.m21, extrinsic.m31,
            extrinsic.m02, extrinsic.m12, extrinsic.m22, extrinsic.m32,
            extrinsic.m03, extrinsic.m13, extrinsic.m23, extrinsic.m33,
        };
    }

    /// <summary>
    /// Rewrites manifest.json listing every frame captured so far. Small file,
    /// rewritten each capture so the session stays valid even if the app is killed.
    /// </summary>
    private void WriteManifest()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("\t\"session_id\" : \"").Append(_sessionId).Append("\",\n");
        sb.Append("\t\"count\" : ").Append(_keyframes.Count).Append(",\n");
        sb.Append("\t\"width\" : ").Append(captureWidth).Append(",\n");
        sb.Append("\t\"height\" : ").Append(captureHeight).Append(",\n");
        // depth_scale = value a PNG pixel is multiplied by to get METRES. We store
        // millimetres, so metres = pixel / 1000  ⇒  depth_scale = 1/1000.
        sb.Append("\t\"has_depth\" : ").Append((captureDepth && _depthReady) ? "true" : "false").Append(",\n");
        sb.Append("\t\"depth_scale\" : ").Append(F(1f / DepthScaleToMillimetres)).Append(",\n");
        sb.Append("\t\"depth_unit\" : \"metres_per_unit\",\n");
        sb.Append("\t\"frames\" : [");
        for (int i = 0; i < _keyframes.Count; i++)
            sb.Append(i > 0 ? ", " : "").Append('"').Append($"frame_{i:D4}.json").Append('"');
        sb.Append("],\n");
        sb.Append("\t\"depth_frames\" : [");
        for (int i = 0; i < _keyframes.Count; i++)
            sb.Append(i > 0 ? ", " : "").Append('"').Append($"frame_{i:D4}.depth.png").Append('"');
        sb.Append("]\n");
        sb.Append("}\n");

        string path = Path.Combine(_sessionDir, "manifest.json");
        try { File.WriteAllText(path, sb.ToString()); }
        catch (System.Exception ex) { Debug.LogError($"[KFCAP] Failed to write manifest: {ex.Message}"); }
    }

    // Invariant-culture float formatting so JSON is valid regardless of device locale
    // (a comma decimal separator would corrupt the file).
    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Current head/CenterEye world pose. Uses the OVR eye pose in tracking-space
    /// local coords transformed to world through trackingSpace — the same
    /// convention ObjectPicker uses for the controller. Falls back to Camera.main.
    /// </summary>
    private bool TryGetHeadPose(out Vector3 worldPos, out Quaternion worldRot)
    {
        if (_trackingSpace != null)
        {
            // Head pose comes from the eye node (not a controller). Read the center
            // eye in tracking-space local coords, then transform into world through
            // trackingSpace — same world-transform convention ObjectPicker uses.
            Vector3 eyeLocalPos = UnityEngine.XR.InputTracking.GetLocalPosition(UnityEngine.XR.XRNode.CenterEye);
            Quaternion eyeLocalRot = UnityEngine.XR.InputTracking.GetLocalRotation(UnityEngine.XR.XRNode.CenterEye);
            worldPos = _trackingSpace.TransformPoint(eyeLocalPos);
            worldRot = _trackingSpace.rotation * eyeLocalRot;
            return true;
        }

        if (Camera.main != null)
        {
            worldPos = Camera.main.transform.position;
            worldRot = Camera.main.transform.rotation;
            return true;
        }

        worldPos = Vector3.zero;
        worldRot = Quaternion.identity;
        return false;
    }

    // Same lookup ObjectPicker uses: prefer OVRCameraRig.trackingSpace, else the
    // main camera's parent, else null (caller falls back to Camera.main).
    private static Transform FindTrackingSpaceTransform()
    {
        var cameraRig = FindFirstObjectByType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.trackingSpace != null)
            return cameraRig.trackingSpace;
        if (Camera.main != null && Camera.main.transform.parent != null)
            return Camera.main.transform.parent;
        return null;
    }

    // ---------------------------------------------------------------------------
    // ToolMenu integration (Step 5)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Called by the ToolMenu "Scan Room" Toggle onValueChanged (dynamic bool).
    /// Starts or stops keyframe capture and resets the session counter when starting.
    /// </summary>
    public void SetScanMode(bool on)
    {
        capturing = on;
        if (on)
        {
            _keyframes.Clear();
            _skippedCount = 0;
            Debug.Log("[KFCAP] Scan STARTED by user.");
        }
        else
        {
            Debug.Log($"[KFCAP] Scan STOPPED. Captured {_keyframes.Count} keyframes.");
        }
        RefreshScanLabel();
    }

    private void RefreshScanLabel()
    {
        if (_scanSublabel == null) return;
        if (capturing)
            _scanSublabel.text = $"scanning… {_keyframes.Count} frames";
        else if (_keyframes.Count > 0)
            _scanSublabel.text = $"done — {_keyframes.Count} frames";
        else
            _scanSublabel.text = "press to start";
    }

    private void VLog(string msg)
    {
        if (verboseLogging) Debug.Log($"[KFCAP] {msg}");
    }
}
