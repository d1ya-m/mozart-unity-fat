using System.Collections;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Orchestrates the one-session live demo when the "Scan Room" button is pressed:
///   1. Load the MRUK room from the device (ReloadExisting) or request a fresh Space
///      Setup capture (FreshSpaceSetup).
///   2. Grab the MRUK global mesh via GlobalMeshProvider (Phase 2).
///   3. Segment it via MeshSegmentationClient -> server (Phase 3).
///   4. Hand the returned base_url + the mesh transform to ObjectPicker (Phase 4) so
///      the object-shaped-portal Add/Remove flow operates on the live clusters.
///
/// On any failure it logs and leaves the currently-loaded clusters in place (the app
/// keeps working with whatever was loaded, e.g. the bundled fallback).
///
/// Wire the ToolMenu "Scan Room" button's click/onValueChanged -> OnScanRoomPressed().
/// </summary>
public class ScanRoomFlow : MonoBehaviour
{
    public enum ScanMode
    {
        // Launch the Quest Space Setup wizard so the user WALKS AROUND and scans the
        // room now, then load + segment the fresh result. This is the real "scan my
        // room" experience. The app pauses during Space Setup (Meta's system UI) and
        // resumes when the user finishes or cancels.
        FreshWalkAroundScan,
        // Skip the wizard; just load the room the headset already has (fast, good for
        // repeat testing when the room hasn't changed).
        ReloadExisting,
    }

    [Tooltip("FreshWalkAroundScan = press launches Quest Space Setup (user walks & scans), " +
             "then auto-segments. ReloadExisting = reuse the existing scan (fast, no walking).")]
    [SerializeField] private ScanMode mode = ScanMode.FreshWalkAroundScan;

    [SerializeField] private GlobalMeshProvider meshProvider;
    [SerializeField] private MeshSegmentationClient segmenter;
    [SerializeField] private ObjectPicker objectPicker;

    private bool _running;

    // Hook this to the "Scan Room" ToolMenu button (Button.onClick or Toggle.onValueChanged).
    public void OnScanRoomPressed()
    {
        if (_running) { Debug.Log("[SCANFLOW] Already running, ignoring press."); return; }
        StartCoroutine(ScanRoutine());
    }

    private IEnumerator ScanRoutine()
    {
        _running = true;
        Debug.Log($"[SCANFLOW] Scan pressed (mode={mode}).");

        if (meshProvider == null || segmenter == null || objectPicker == null)
        {
            Debug.LogError("[SCANFLOW] Missing references (meshProvider/segmenter/objectPicker). " +
                           "Assign them in the Inspector.");
            _running = false;
            yield break;
        }

        // 0) FreshWalkAroundScan: launch the Quest Space Setup wizard so the user walks
        //    around and scans the room NOW. The app pauses (Meta system UI) and resumes
        //    when they finish or cancel. RequestSpaceSetup completes successfully even on
        //    cancel; we then just load whatever room exists.
        if (mode == ScanMode.FreshWalkAroundScan)
        {
            Debug.Log("[SCANFLOW] Launching Space Setup (walk around and scan your room)...");
            var setup = OVRScene.RequestSpaceSetup();
            while (!setup.IsCompleted) yield return null;
            Debug.Log($"[SCANFLOW] Space Setup returned: {setup.GetResult()}");
        }

        // 1) Load the MRUK room from device (the freshly-scanned one, or the existing one).
        if (MRUK.Instance == null)
        {
            Debug.LogError("[SCANFLOW] MRUK.Instance is null.");
            _running = false;
            yield break;
        }

        var loadTask = MRUK.Instance.LoadSceneFromDevice(
            requestSceneCaptureIfNoDataFound: mode == ScanMode.FreshWalkAroundScan);
        while (!loadTask.IsCompleted) yield return null;
        Debug.Log($"[SCANFLOW] LoadSceneFromDevice done: {loadTask.Result}");

        // 2) Grab the global mesh (Phase 2). Retry briefly in case it isn't built yet.
        Mesh mesh = null; Transform meshT = null;
        for (int i = 0; i < 10 && mesh == null; i++)
        {
            if (meshProvider.TryCaptureGlobalMesh())
            {
                mesh = meshProvider.RoomMesh;
                meshT = meshProvider.RoomMeshTransform;
                break;
            }
            yield return new WaitForSeconds(0.5f);
        }
        if (mesh == null)
        {
            Debug.LogWarning("[SCANFLOW] No global mesh; keeping current clusters.");
            _running = false;
            yield break;
        }
        Debug.Log($"[SCANFLOW] Global mesh: {mesh.vertexCount} verts.");

        // 3) Segment via the server (Phase 3).
        MeshSegmentationClient.SegmentResult result = null;
        yield return segmenter.Segment(mesh, meshT, r => result = r);
        if (result == null)
        {
            Debug.LogWarning("[SCANFLOW] Segmentation failed; keeping current clusters " +
                             "(bundled fallback still works).");
            _running = false;
            yield break;
        }

        // 4) Hand the clusters to ObjectPicker (Phase 4). Parent under the MRUK mesh
        //    transform so picks land on the object (match-space == render-space).
        Debug.Log($"[SCANFLOW] Loading {result.count} clusters from {result.base_url}.");
        objectPicker.ReloadClustersFromUrl(result.base_url, meshT);

        Debug.Log("[SCANFLOW] Done. Use Add/Remove Portal to place portals on the scanned objects.");
        _running = false;
    }
}
