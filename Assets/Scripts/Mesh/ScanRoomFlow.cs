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
    public enum ScanMode { ReloadExisting, FreshSpaceSetup }

    [Tooltip("ReloadExisting = reuse the device's existing room scan (fast, default). " +
             "FreshSpaceSetup = ask the system to run Space Setup if no data is found.")]
    [SerializeField] private ScanMode mode = ScanMode.ReloadExisting;

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

        // 1) Load the MRUK room from device.
        if (MRUK.Instance == null)
        {
            Debug.LogError("[SCANFLOW] MRUK.Instance is null.");
            _running = false;
            yield break;
        }

        var loadTask = MRUK.Instance.LoadSceneFromDevice(
            requestSceneCaptureIfNoDataFound: mode == ScanMode.FreshSpaceSetup);
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
