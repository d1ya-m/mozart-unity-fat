using System;
using System.Globalization;
using System.IO;
using System.Text;
using Meta.XR;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Records an RGB dataset on the Quest for later offline photogrammetry (COLMAP /
/// OpenMVS). Start/stop is driven by a "Scan Room" toggle button (SetScanMode) or the
/// controller A/X button. While scanning, every captureInterval seconds it saves:
///
///   dataset_&lt;sessionId&gt;/
///     calibration/left_camera.json      (intrinsics: fx,fy,cx,cy,resolution,lens offset)
///     frames/left_000001.jpg            (RGB photo)
///     poses/frames.csv                  (per-frame id,image,timestamp,pose)
///     manifest.json
///
/// This is the RGB path — it uses the passthrough camera (Meta.XR.PassthroughCameraAccess),
/// NOT the depth sensor. Requires the HEADSET_CAMERA permission and a PassthroughCameraAccess
/// component (CameraPosition = Left) assigned in the Inspector.
///
/// NOTE: only CAPTURE runs on-device; the COLMAP/OpenMVS reconstruction is a separate
/// offline PC step. Pull the dataset with:
///   adb pull /sdcard/Android/data/&lt;pkg&gt;/files/dataset_&lt;sessionId&gt; .
/// </summary>
public class QuestDatasetRecorder : MonoBehaviour
{
    [Header("Camera")]
    [Tooltip("The passthrough camera to record (create a PassthroughCameraAccess component, " +
             "CameraPosition = Left, and assign it here).")]
    [SerializeField] private PassthroughCameraAccess leftCamera;

    [Header("Capture")]
    [Tooltip("Seconds between saved frames while scanning. 0.25-0.5s gives good overlap " +
             "for photogrammetry without flooding storage.")]
    [SerializeField] private float captureIntervalSeconds = 0.35f;
    [Tooltip("JPEG quality 1-100.")]
    [SerializeField] private int jpegQuality = 90;
    [Tooltip("Safety cap so a long scan can't fill storage.")]
    [SerializeField] private int maxFrames = 600;

    [Header("Scan control")]
    [Tooltip("Master switch. Driven by the 'Scan Room' toggle via SetScanMode(). Starts OFF; " +
             "press the button (or controller A/X) to start, press again to stop.")]
    [SerializeField] private bool scanning = false;
    [Tooltip("The 'Scan Room' Toggle, so the controller shortcut keeps the UI in sync (optional).")]
    [SerializeField] private UnityEngine.UI.Toggle _scanToggle;
    [Tooltip("Sublabel TMP text on the Scan Room button (shows live frame count).")]
    [SerializeField] private TMPro.TMP_Text _scanSublabel;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;

    private string _datasetDir;
    private string _sessionId;
    private StreamWriter _framesCsv;
    private int _frameId;
    private float _lastCaptureTime;
    private bool _calibrationSaved;

    private void Start()
    {
        _sessionId = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _datasetDir = Path.Combine(Application.persistentDataPath, $"dataset_{_sessionId}");
        Directory.CreateDirectory(Path.Combine(_datasetDir, "frames"));
        Directory.CreateDirectory(Path.Combine(_datasetDir, "calibration"));
        Directory.CreateDirectory(Path.Combine(_datasetDir, "poses"));

        _framesCsv = new StreamWriter(Path.Combine(_datasetDir, "poses", "frames.csv"));
        _framesCsv.WriteLine("frame_id,camera,image,timestamp,px,py,pz,qx,qy,qz,qw,width,height");
        _framesCsv.Flush();

        Log($"[DSREC] Session '{_sessionId}' -> {_datasetDir}");
        Log($"[DSREC] Ready. scanning={scanning} interval={captureIntervalSeconds}s. " +
            $"camera supported={PassthroughCameraAccess.IsSupported}");
        RefreshLabel();
    }

    private void OnDestroy()
    {
        _framesCsv?.Flush();
        _framesCsv?.Dispose();
    }

    private void Update()
    {
        // Controller shortcut: right A / left X toggles scanning (works if UI is hidden).
        if (OVRInput.GetDown(OVRInput.Button.One) || OVRInput.GetDown(OVRInput.Button.Three))
        {
            bool next = !scanning;
            if (_scanToggle != null) _scanToggle.isOn = next;   // fires SetScanMode
            else SetScanMode(next);
        }

        if (!scanning) return;
        if (_frameId >= maxFrames) { Debug.LogWarning("[DSREC] maxFrames reached; stopping."); SetScanMode(false); return; }
        if (Time.time - _lastCaptureTime < captureIntervalSeconds) return;

        if (leftCamera == null) { Debug.LogWarning("[DSREC] No PassthroughCameraAccess assigned."); return; }
        if (!leftCamera.IsPlaying || !leftCamera.IsUpdatedThisFrame) return;

        if (!_calibrationSaved) SaveCalibration();
        CaptureFrame();
        _lastCaptureTime = Time.time;
    }

    // Wire the "Scan Room" toggle's On Value Changed (Boolean) -> this.
    public void SetScanMode(bool on)
    {
        scanning = on;
        if (on) Log($"[DSREC] Scan STARTED.");
        else Log($"[DSREC] Scan STOPPED. Captured {_frameId} frames -> {_datasetDir}");
        RefreshLabel();
    }

    private void SaveCalibration()
    {
        var intr = leftCamera.Intrinsics;

        // The SDK reports fx/fy/cx/cy at Intrinsics.SensorResolution, but the frames are
        // saved at CurrentResolution (the actual playback size). These are NOT guaranteed
        // equal (the SDK itself crops/scales between them — see PassthroughCameraAccess
        // CalcSensorCropRegion). If we wrote the sensor-resolution intrinsics against
        // CurrentResolution-sized JPGs, any offline reconstruction (COLMAP/OpenMVS) that
        // trusts the calibration would be off. So SCALE the intrinsics to the actual frame
        // resolution and write the calibration at THAT resolution, keeping everything
        // consistent with the saved images.
        Vector2Int sensorRes = intr.SensorResolution;
        Vector2Int frameRes = leftCamera.CurrentResolution;

        float sx = (sensorRes.x > 0) ? (float)frameRes.x / sensorRes.x : 1f;
        float sy = (sensorRes.y > 0) ? (float)frameRes.y / sensorRes.y : 1f;

        float fx = intr.FocalLength.x * sx;
        float fy = intr.FocalLength.y * sy;
        float cx = intr.PrincipalPoint.x * sx;
        float cy = intr.PrincipalPoint.y * sy;

        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.Append("{\n");
        sb.Append($"  \"camera\": \"left\",\n");
        // width/height and fx/fy/cx/cy are all at the FRAME resolution (= the saved JPGs).
        sb.Append($"  \"width\": {frameRes.x},\n");
        sb.Append($"  \"height\": {frameRes.y},\n");
        sb.Append($"  \"fx\": {fx.ToString(ci)},\n");
        sb.Append($"  \"fy\": {fy.ToString(ci)},\n");
        sb.Append($"  \"cx\": {cx.ToString(ci)},\n");
        sb.Append($"  \"cy\": {cy.ToString(ci)},\n");
        // Also record the raw sensor values + resolutions for transparency/debugging.
        sb.Append($"  \"sensor_width\": {sensorRes.x},\n");
        sb.Append($"  \"sensor_height\": {sensorRes.y},\n");
        sb.Append($"  \"sensor_fx\": {intr.FocalLength.x.ToString(ci)},\n");
        sb.Append($"  \"sensor_fy\": {intr.FocalLength.y.ToString(ci)},\n");
        sb.Append($"  \"sensor_cx\": {intr.PrincipalPoint.x.ToString(ci)},\n");
        sb.Append($"  \"sensor_cy\": {intr.PrincipalPoint.y.ToString(ci)},\n");
        sb.Append($"  \"lens_offset_position\": [{intr.LensOffset.position.x.ToString(ci)}, {intr.LensOffset.position.y.ToString(ci)}, {intr.LensOffset.position.z.ToString(ci)}],\n");
        sb.Append($"  \"lens_offset_rotation_xyzw\": [{intr.LensOffset.rotation.x.ToString(ci)}, {intr.LensOffset.rotation.y.ToString(ci)}, {intr.LensOffset.rotation.z.ToString(ci)}, {intr.LensOffset.rotation.w.ToString(ci)}]\n");
        sb.Append("}\n");
        File.WriteAllText(Path.Combine(_datasetDir, "calibration", "left_camera.json"), sb.ToString());
        _calibrationSaved = true;

        if (sensorRes != frameRes)
            Log($"[DSREC] Calibration scaled: sensor {sensorRes.x}x{sensorRes.y} -> frame {frameRes.x}x{frameRes.y} (fx {intr.FocalLength.x:F1}->{fx:F1}).");
        else
            Log($"[DSREC] Saved calibration (fx={fx:F1} res={frameRes.x}x{frameRes.y}, sensor==frame).");
    }

    private void CaptureFrame()
    {
        _frameId++;
        Pose pose = leftCamera.GetCameraPose();
        DateTime ts = leftCamera.Timestamp;
        Vector2Int res = leftCamera.CurrentResolution;

        // CPU pixel readback -> JPEG. GetColors() returns a NativeArray<Color32> (RGBA).
        NativeArray<Color32> colors = leftCamera.GetColors();
        if (!colors.IsCreated || colors.Length < res.x * res.y)
        {
            Debug.LogWarning($"[DSREC] frame {_frameId}: no pixels yet, skipping.");
            _frameId--;
            return;
        }

        var tex = new Texture2D(res.x, res.y, TextureFormat.RGBA32, false);
        tex.SetPixelData(colors, 0);
        tex.Apply(false);
        byte[] jpg = tex.EncodeToJPG(jpegQuality);
        Destroy(tex);

        string fileName = $"left_{_frameId:D6}.jpg";
        File.WriteAllBytes(Path.Combine(_datasetDir, "frames", fileName), jpg);

        var ci = CultureInfo.InvariantCulture;
        _framesCsv.WriteLine(
            $"{_frameId},left,frames/{fileName},{ts.ToString("o", ci)}," +
            $"{pose.position.x.ToString(ci)},{pose.position.y.ToString(ci)},{pose.position.z.ToString(ci)}," +
            $"{pose.rotation.x.ToString(ci)},{pose.rotation.y.ToString(ci)},{pose.rotation.z.ToString(ci)},{pose.rotation.w.ToString(ci)}," +
            $"{res.x},{res.y}");
        _framesCsv.Flush();

        WriteManifest();
        RefreshLabel();
        if (verboseLogging && _frameId % 10 == 0)
            Log($"[DSREC] captured {_frameId} frames (last pos={pose.position}).");
    }

    private void WriteManifest()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"session_id\": \"{_sessionId}\",\n");
        sb.Append($"  \"camera\": \"left\",\n");
        sb.Append($"  \"frame_count\": {_frameId},\n");
        sb.Append($"  \"calibration\": \"calibration/left_camera.json\",\n");
        sb.Append($"  \"poses\": \"poses/frames.csv\"\n");
        sb.Append("}\n");
        File.WriteAllText(Path.Combine(_datasetDir, "manifest.json"), sb.ToString());
    }

    private void RefreshLabel()
    {
        if (_scanSublabel == null) return;
        _scanSublabel.text = scanning ? $"scanning… {_frameId} photos"
                           : _frameId > 0 ? $"done — {_frameId} photos"
                           : "press to start";
    }

    private void Log(string msg) { if (verboseLogging) Debug.Log(msg); }
}
