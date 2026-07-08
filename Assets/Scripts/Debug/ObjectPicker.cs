using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ObjectPicker : MonoBehaviour
{
    [SerializeField] private Material stencilMaskMaterial;
    [SerializeField] private int portalMaskLayer = 9;
    [SerializeField] private float rayLength = 100f;
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private LineRenderer lineRenderer;
    [SerializeField] private Color hitColor = Color.green;
    [SerializeField] private Color missColor = Color.red;
    [Header("Status panel follow")]
    [SerializeField] private Transform statusPanel;     // the world-space Canvas
    [SerializeField] private bool statusPanelFollowsView = true;
    [SerializeField] private float statusPanelDistance = 1.5f;
    [SerializeField] private float statusPanelVerticalOffset = -0.3f;

    [Header("Debug visualization")]
    [Tooltip("When ON, a picked cluster is shown as a bright solid colour instead " +
             "of the invisible stencil mask, so you can SEE the pick succeeded.")]
    [SerializeField] private bool debugVisibleClusters = true;
    [SerializeField] private Color debugClusterColor = new Color(1f, 0.4f, 0f, 1f); // orange

    [Header("Cluster picking")]
    [Tooltip("Layer the cluster pick-colliders live on. The pick raycast only hits " +
             "this layer, so it ignores the room mesh / portals / UI. Create a User " +
             "Layer named 'ClusterPick' and set this to its index (default 11).")]
    [SerializeField] private int clusterPickLayer = 11;

    [Tooltip("When ON, prints verbose DIAG/PICK/PICKMASK/Preload logs (read via " +
             "adb logcat). Leave OFF for normal use - warnings/errors still print.")]
    [SerializeField] private bool verboseLogging = false;

    [Header("Server clusters (MRUK dynamic pipeline)")]
    [Tooltip("When set (by ScanRoomFlow after segmentation), clusters load from this " +
             "server base URL (e.g. http://192.168.1.50:5000/clusters/) INSTEAD of the " +
             "bundled StreamingAssets. Empty = use the bundled fallback clusters.")]
    [SerializeField] private string clusterBaseUrl = "";

    [Tooltip("When loading server (MRUK-sourced) clusters, skip the OBJ X-flip. The MRUK " +
             "mesh is already in Unity space, so flipping would mirror the picks. Bundled " +
             "StreamingAssets clusters (from the external server mesh) keep the flip.")]
    [SerializeField] private bool serverClustersSkipXFlip = true;

    // The transform server clusters are parented under (the MRUK global-mesh transform,
    // set via ReloadClustersFromUrl). When null, clusters fall back to _sceneMeshTransform.
    private Transform _serverClusterParent;

    // Bumped on every reload so the MeshDownloadManager cache keys are unique per load.
    // Without this, "cluster_preload_{id}" collides with the bundled load's keys and the
    // manager returns the old (destroyed -> null) cached object -> "Preload failed".
    private int _loadGeneration;

    [Header("Portal mode toggles (ToolMenu)")]
    [Tooltip("The 'Add Portal' Toggle. Used to auto-turn-off the other toggle so " +
             "Add/Remove are mutually exclusive. Optional but recommended.")]
    [SerializeField] private Toggle _addToggle;
    [Tooltip("The 'Remove Portal' Toggle.")]
    [SerializeField] private Toggle _removeToggle;
    [Tooltip("Sublabel (small text) on the Add Portal button - shows ON/OFF state.")]
    [SerializeField] private TMP_Text addPortalSubLabel;
    [Tooltip("Sublabel (small text) on the Remove Portal button - shows ON/OFF state.")]
    [SerializeField] private TMP_Text removePortalSubLabel;

    private GameObject _currentMask;
    private bool _clustersLoaded;
    private Transform _sceneMeshTransform;
    private LineRenderer _lineRenderer;
    private bool _isLoading;
    private float _lastDiagTime;
    private Transform _trackingSpace;
    private Material _laserMaterial;

    // Preloaded cluster meshes: index -> loaded GameObject. Each carries a
    // MeshCollider on clusterPickLayer and is kept ACTIVE (renderer off) so the
    // pick ray can always hit it. Picking by collider means match-space ==
    // render-space automatically (no cached centres to go stale).
    private readonly Dictionary<int, GameObject> _clusterObjects = new Dictionary<int, GameObject>();
    private readonly Dictionary<Collider, int> _colliderToCluster = new Dictionary<Collider, int>();
    private bool _clustersPreloaded;
    private string _loadedSceneKey;   // which mesh the current clusters belong to (Fix 1)

    // Multiple portals can be active at once. Mode is set by the ToolMenu buttons;
    // the trigger only acts in Add/Remove mode (Idle ignores it).
    private enum PickMode { Idle, Add, Remove }
    private PickMode _mode = PickMode.Idle;
    private readonly HashSet<int> _activePortals = new HashSet<int>();

    private void Start()
    {
        _lineRenderer = lineRenderer != null ? lineRenderer
            : GetComponent<LineRenderer>() ?? GetComponentInChildren<LineRenderer>() ?? GetComponentInParent<LineRenderer>();
        if (_lineRenderer == null) Debug.LogWarning("[ObjectPicker] No LineRenderer found - laser will not show. Assign one in the inspector.");
        else
        {
            _lineRenderer.widthMultiplier = 1f;
            _lineRenderer.startWidth = 0.005f;
            _lineRenderer.endWidth = 0.005f;
            _lineRenderer.positionCount = 2;
            // Unlit material that ignores scene depth so the laser is always
            // visible, including over passthrough.
            var laserMat = new Material(Shader.Find("Unlit/Color"));
            laserMat.color = hitColor;
            _lineRenderer.material = laserMat;
            _laserMaterial = laserMat;
        }
        _trackingSpace = FindTrackingSpaceTransform();
        if (_trackingSpace != null) VLog($"[ObjectPicker] trackingSpace found: {_trackingSpace.name}");
        else Debug.LogWarning("[ObjectPicker] trackingSpace not found - controller tracking will fall back to head gaze.");
        if (statusText == null) Debug.LogWarning("[ObjectPicker] No StatusText assigned - status messages will not show.");
        var mdm = MeshDownloadManager.Instance;
        if (mdm != null) mdm.MeshLoaded += OnMeshLoaded;
        else Debug.LogWarning("[ObjectPicker] MeshDownloadManager.Instance is null at Start - will not receive MeshLoaded event.");
        var existing = GameObject.Find("ServerSceneMesh");
        if (existing != null) { _sceneMeshTransform = existing.transform; EnableMeshCollider(existing); StartCoroutine(PreloadClustersCoroutine()); VLog("[ObjectPicker] Found existing scene mesh."); }
        else { VLog("[ObjectPicker] No scene mesh yet, waiting for mesh."); }
        SetStatus("Point at an object and pull trigger.");
    }

    private void OnDestroy()
    {
        var mdm = MeshDownloadManager.Instance;
        if (mdm != null) mdm.MeshLoaded -= OnMeshLoaded;
    }

    private void OnMeshLoaded(string key, GameObject sceneMesh)
    {
        if (key.StartsWith("cluster_preload")) return; // our own preloads
        _sceneMeshTransform = sceneMesh.transform;
        EnableMeshCollider(sceneMesh);

        // Fix 1: if a DIFFERENT scene mesh loaded, clear the old clusters/portals
        // and reload for the new one. Same scene -> keep what we have.
        if (_clustersPreloaded && key != _loadedSceneKey)
        {
            VLog($"[ObjectPicker] Scene changed ({_loadedSceneKey} -> {key}); clearing clusters.");
            ClearAllClusters();
        }
        _loadedSceneKey = key;

        if (!_clustersPreloaded) StartCoroutine(PreloadClustersCoroutine());
        SetStatus("Scene loaded. Loading clusters...");
        VLog($"[ObjectPicker] MeshLoaded event received (key={key}). Scene mesh transform set.");
    }

    // Destroy all preloaded clusters and reset state (used when the scene changes).
    private void ClearAllClusters()
    {
        foreach (var kv in _clusterObjects)
            if (kv.Value != null) Destroy(kv.Value);
        _clusterObjects.Clear();
        _colliderToCluster.Clear();
        _activePortals.Clear();
        _clustersPreloaded = false;
        _clustersLoaded = false;
    }

    /// <summary>
    /// PUBLIC ENTRY for the MRUK pipeline (called by ScanRoomFlow after segmentation).
    /// Clears any current clusters, then reloads from <paramref name="baseUrl"/> with the
    /// clusters parented under <paramref name="meshTransform"/> (the MRUK global-mesh
    /// transform) so match-space == render-space. Server clusters skip the OBJ X-flip.
    /// </summary>
    public void ReloadClustersFromUrl(string baseUrl, Transform meshTransform)
    {
        if (string.IsNullOrEmpty(baseUrl) || meshTransform == null)
        {
            Debug.LogWarning("[ObjectPicker] ReloadClustersFromUrl: empty url or null transform.");
            return;
        }
        Debug.Log($"[ObjectPicker] Reloading clusters from {baseUrl} under '{meshTransform.name}'.");
        ClearAllClusters();
        _loadGeneration++;                     // fresh cache keys so we re-download
        clusterBaseUrl = baseUrl;
        _serverClusterParent = meshTransform;
        _sceneMeshTransform = meshTransform;   // so the preload guard passes
        StartCoroutine(PreloadClustersCoroutine());
    }

    private static void EnableMeshCollider(GameObject meshObj)
    {
        var colliders = meshObj.GetComponentsInChildren<Collider>(true);
        foreach (var c in colliders) c.enabled = true;
    }

    // Load each clusterN.obj listed in clusters.json once, parent under the scene
    // mesh, give it a MeshCollider on clusterPickLayer, and keep it ACTIVE with the
    // renderer OFF (so the pick ray can hit it while it stays invisible until
    // selected). Picking by collider keeps match-space == render-space.
    private System.Collections.IEnumerator PreloadClustersCoroutine()
    {
        if (_clustersPreloaded || _sceneMeshTransform == null) yield break;
        _clustersPreloaded = true;
        _isLoading = true;

        // Fix 3: read the manifest to know exactly which cluster indices exist.
        List<int> indices = null;
        yield return StartCoroutine(LoadManifest(result => indices = result));
        if (indices == null || indices.Count == 0)
        {
            Debug.LogError("[ObjectPicker] clusters.json missing/empty - no clusters to load.");
            SetStatus("ERROR: clusters.json not found.");
            _isLoading = false;
            // Reset the preload guard so a later OnMeshLoaded/Update can retry. A single
            // transient UnityWebRequest failure against StreamingAssets (not uncommon on
            // Android) must NOT permanently disable the bundled portal path. _clustersLoaded
            // stays false so the trigger handler keeps gating until a load succeeds.
            _clustersPreloaded = false;
            yield break;
        }

        // Server clusters (MRUK) load from the base URL with the X-flip skipped and are
        // parented under the MRUK global-mesh transform. Bundled clusters keep the old
        // file:// path, default flip, and ServerSceneMesh parent.
        bool useServer = !string.IsNullOrEmpty(clusterBaseUrl);
        bool? flipOverride = useServer && serverClustersSkipXFlip ? (bool?)false : null;
        Transform clusterParent = useServer && _serverClusterParent != null
            ? _serverClusterParent : _sceneMeshTransform;

        int loaded = 0;
        foreach (int id in indices)
        {
            string path;
            if (useServer)
            {
                path = clusterBaseUrl.TrimEnd('/') + $"/cluster{id}.obj";
            }
            else
            {
                path = Path.Combine(Application.streamingAssetsPath, $"clusters/cluster{id}.obj");
                if (!path.Contains("://")) path = "file://" + path;
            }

            var task = MeshDownloadManager.Instance.LoadMeshFromServer($"cluster_preload_{_loadGeneration}_{id}", path, flipOverride);
            while (!task.IsCompleted) yield return null;
            GameObject obj = task.Result;
            if (obj == null) { Debug.LogWarning($"[ObjectPicker] Preload cluster{id} failed."); continue; }

            obj.transform.SetParent(clusterParent, false);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;

            // Disable any collider the loader put on obj itself (we use a
            // dedicated child for picking so relayering obj for rendering never
            // moves the pick collider off clusterPickLayer).
            foreach (var c in obj.GetComponentsInChildren<Collider>(true))
                c.enabled = false;

            // Dedicated pick-collider child, ALWAYS on clusterPickLayer. Its layer
            // is independent of how obj renders, so Add/Remove never breaks picking.
            var mf = obj.GetComponentInChildren<MeshFilter>(true);
            var pick = new GameObject($"ClusterPick_{id}");
            pick.transform.SetParent(obj.transform, false);
            pick.layer = clusterPickLayer;
            var mc = pick.AddComponent<MeshCollider>();
            if (mf != null && mf.sharedMesh != null) mc.sharedMesh = mf.sharedMesh;
            mc.convex = false;

            _colliderToCluster[mc] = id;
            _clusterObjects[id] = obj;

            // Active (so the pick collider is raycastable) but invisible until picked.
            var rend = obj.GetComponentInChildren<MeshRenderer>(true);
            if (rend != null) rend.enabled = false;
            obj.SetActive(true);

            loaded++;
            VLog($"[ObjectPicker] Preloaded cluster{id} (pick collider on layer {clusterPickLayer}).");
            yield return null; // spread across frames to avoid a hitch
        }

        _clustersLoaded = true;
        _isLoading = false;
        SetStatus($"Ready: {loaded} clusters.\nPress Add/Remove Portal.");
        Debug.Log($"[ObjectPicker] Preload complete: {loaded} clusters.");
    }

    // Reads the cluster manifest. From the server base URL when set (MRUK pipeline),
    // else the bundled StreamingAssets/clusters/clusters.json (APK-safe via UnityWebRequest).
    private System.Collections.IEnumerator LoadManifest(System.Action<List<int>> onDone)
    {
        string path;
        if (!string.IsNullOrEmpty(clusterBaseUrl))
        {
            path = clusterBaseUrl.TrimEnd('/') + "/clusters.json";
        }
        else
        {
            path = Path.Combine(Application.streamingAssetsPath, "clusters/clusters.json");
            if (!path.Contains("://")) path = "file://" + path;
        }
        using var req = UnityEngine.Networking.UnityWebRequest.Get(path);
        yield return req.SendWebRequest();
        if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ObjectPicker] Failed to load clusters.json: {req.error}");
            onDone(null);
            yield break;
        }
        var manifest = JsonUtility.FromJson<ClusterManifest>(req.downloadHandler.text);
        onDone(manifest != null && manifest.indices != null
            ? new List<int>(manifest.indices) : null);
    }

    [System.Serializable]
    private class ClusterManifest { public int count; public int[] indices; }

    private void LateUpdate()
    {
        // Keep the status panel pinned in front of the user's view so it's never
        // lost behind them or inside a wall.
        var headCam = GetHeadCamera();
        if (statusPanelFollowsView && statusPanel != null && headCam != null)
        {
            var cam = headCam.transform;

            Vector3 flatForward = cam.forward; flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.0001f) flatForward = Vector3.forward;
            flatForward.Normalize();
            statusPanel.position = cam.position + flatForward * statusPanelDistance
                                   + Vector3.up * statusPanelVerticalOffset;
            statusPanel.rotation = Quaternion.LookRotation(
                statusPanel.position - cam.position, Vector3.up);
        }
    }

    private void Update()
    {
        if (_sceneMeshTransform == null)
        {
            var found = GameObject.Find("ServerSceneMesh");
            if (found != null)
            {
                _sceneMeshTransform = found.transform;
                EnableMeshCollider(found);
                if (!_clustersPreloaded) StartCoroutine(PreloadClustersCoroutine());
                VLog("[ObjectPicker] Scene mesh found in Update.");
            }
            UpdateLaser(null, false);
            // Always show why we're idle, so we have eyes on-device.
            if (!_isLoading)
                SetStatus($"WAITING for scene mesh\nServerSceneMesh found: {found != null}\nclusters preloaded: {_clustersPreloaded} ({_clusterObjects.Count})");
            return;
        }

        // GameManager disables the scene-mesh collider right after load (after our
        // MeshLoaded handler already ran), so ensure it's on before raycasting.
        var sceneCol = _sceneMeshTransform.GetComponent<Collider>();
        if (sceneCol != null && !sceneCol.enabled) sceneCol.enabled = true;

        Ray ray = GetPointerRay();

        // Fix 2: pick by raycasting ONLY against the cluster colliders (their own
        // layer). The collider you hit IS the object you're pointing at - exact
        // per-shape containment, no centre-distance threshold, and immune to mesh
        // re-alignment (Fix 4) because colliders move with the mesh.
        int pickLayerMask = 1 << clusterPickLayer;
        bool clusterHit = Physics.Raycast(ray, out RaycastHit hit, rayLength, pickLayerMask);
        int pickedCluster = -1;
        if (_clustersLoaded && clusterHit
            && _colliderToCluster.TryGetValue(hit.collider, out int cid))
            pickedCluster = cid;
        bool validAim = pickedCluster >= 0;

        // The laser only appears in Add/Remove mode. In Idle it is hidden so the
        // green/red aiming guide only shows when it's actionable.
        bool laserActive = _mode != PickMode.Idle;
        if (_lineRenderer != null) _lineRenderer.enabled = laserActive;
        if (laserActive)
            UpdateLaser(validAim ? (Vector3?)hit.point : null, validAim);

        // Live on-screen status.
        if (!_isLoading)
        {
            if (_mode == PickMode.Idle)
                SetStatus("Idle.\nPress Add Portal or Remove Portal.");
            else
                SetStatus(validAim
                    ? $"Mode: {_mode}. On cluster {pickedCluster}\nPull trigger."
                    : $"Mode: {_mode}. Not on an object\nAim at an object.");
        }

        // Throttled diagnostic: prints once per second so we can see what's happening.
        if (verboseLogging && Time.time - _lastDiagTime > 1f)
        {
            _lastDiagTime = Time.time;
            Vector3 rawCtrl = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
            Debug.Log($"[ObjectPicker] DIAG mode={_mode} clusters={_clusterObjects.Count} " +
                      $"rawCtrl={rawCtrl} clusterHit={clusterHit} picked={pickedCluster} " +
                      $"hitPoint={(clusterHit ? hit.point.ToString() : "-")}");
        }

        if (!_clustersLoaded) return;
        // Accept either index trigger so it works regardless of which hand holds
        // the controller. PrimaryIndexTrigger=right, SecondaryIndexTrigger=left.
        bool triggered = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger)
                      || OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger);
#if UNITY_EDITOR
        triggered |= Input.GetKeyDown(KeyCode.Space);
#endif
        if (!triggered) return;
        VLog("[ObjectPicker] Trigger pressed.");
        if (_mode == PickMode.Idle) { SetStatus("Idle. Press Add Portal or Remove Portal first."); return; }
        if (_isLoading) { SetStatus("Still loading, please wait..."); return; }
        if (pickedCluster < 0) { SetStatus("Not on an object. Aim at an object."); VLog("[ObjectPicker] No cluster under ray."); return; }

        VLog($"[ObjectPicker] PICK mode={_mode} cluster={pickedCluster} worldHit={hit.point}");

        // COORDINATE VERIFICATION (Phase 4): log the aimed ray vs the picked cluster's
        // world centroid. If the pick is correct, the hit point and the cluster centroid
        // are both near where you're pointing. A mirrored/offset centroid means the
        // X-flip decision is wrong (see IMPLEMENTATION_NOTES.md).
        if (_clusterObjects.TryGetValue(pickedCluster, out var pickedObj) && pickedObj != null)
        {
            var mr = pickedObj.GetComponentInChildren<MeshRenderer>(true);
            Vector3 centroid = mr != null ? mr.bounds.center : pickedObj.transform.position;
            Debug.Log($"[ObjectPicker] PICKCHECK aim.origin={ray.origin} aim.dir={ray.direction} " +
                      $"hit={hit.point} clusterCentroid={centroid} " +
                      $"hit->centroid={(centroid - hit.point).magnitude:F2}m");
        }

        if (_mode == PickMode.Add) AddPortal(pickedCluster);
        else if (_mode == PickMode.Remove) RemovePortal(pickedCluster);
    }

    // OVRInput works over Quest Link in the editor too, so prefer the right
    // controller. Falls back to head gaze if no controller is tracked.
    // Match the project convention (SpatialAnchorOriginManager / GameManager):
    // controller pose comes from OVRInput in tracking-space-local coords, then
    // transformed to world via the OVRCameraRig trackingSpace transform.
    private Ray GetPointerRay()
    {
        if (_trackingSpace != null)
        {
            Vector3 localPos = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
            Quaternion localRot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
            Vector3 worldPos = _trackingSpace.TransformPoint(localPos);
            Quaternion worldRot = _trackingSpace.rotation * localRot;
            return new Ray(worldPos, worldRot * Vector3.forward);
        }
        return new Ray(Camera.main.transform.position, Camera.main.transform.forward);
    }

    private static Transform FindTrackingSpaceTransform()
    {
        var cameraRig = FindFirstObjectByType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.trackingSpace != null)
            return cameraRig.trackingSpace;
        if (Camera.main != null && Camera.main.transform.parent != null)
            return Camera.main.transform.parent;
        return null;
    }

    private Camera _headCamera;
    private Camera GetHeadCamera()
    {
        if (_headCamera != null) return _headCamera;
        if (Camera.main != null) { _headCamera = Camera.main; return _headCamera; }
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null && rig.centerEyeAnchor != null)
            _headCamera = rig.centerEyeAnchor.GetComponent<Camera>();
        if (_headCamera == null)
        {
            var cams = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            if (cams.Length > 0) _headCamera = cams[0];
        }
        return _headCamera;
    }


    private void UpdateLaser(Vector3? hitPoint, bool hit)
    {
        if (_lineRenderer == null) return;
        Ray pointer = GetPointerRay();
        Vector3 origin = pointer.origin;
        Vector3 direction = pointer.direction;
        _lineRenderer.SetPosition(0, origin);
        _lineRenderer.SetPosition(1, hitPoint ?? origin + direction * rayLength);
        Color c = hit ? hitColor : missColor;
        _lineRenderer.startColor = c;
        _lineRenderer.endColor = c;
        if (_laserMaterial != null) _laserMaterial.color = c;
    }

    // ---- ToolMenu Toggle hooks (Option C via Toggle's On Value Changed bool) ----
    // The buttons in ToolMenu are Toggle components. Wire each Toggle's
    // "On Value Changed (Boolean)" -> these methods (dynamic bool). The toggle
    // state directly drives the mode, so they never desync. The two toggles are
    // mutually exclusive: turning one on forces the other off + Idle.

    // Guard so the programmatic "turn the other toggle off" below does not
    // re-enter and clobber the mode we just set (the toggle race).
    private bool _suppressToggleCallback;

    // Wire the "Add Portal" Toggle -> SetAddMode (dynamic bool).
    public void SetAddMode(bool on)
    {
        if (_suppressToggleCallback) return;
        _mode = on ? PickMode.Add : PickMode.Idle;
        if (on && _removeToggle != null && _removeToggle.isOn)
        {
            _suppressToggleCallback = true;
            _removeToggle.isOn = false;
            _suppressToggleCallback = false;
        }
        RefreshModeStatus();
    }

    // Wire the "Remove Portal" Toggle -> SetRemoveMode (dynamic bool).
    public void SetRemoveMode(bool on)
    {
        if (_suppressToggleCallback) return;
        _mode = on ? PickMode.Remove : PickMode.Idle;
        if (on && _addToggle != null && _addToggle.isOn)
        {
            _suppressToggleCallback = true;
            _addToggle.isOn = false;
            _suppressToggleCallback = false;
        }
        RefreshModeStatus();
    }

    private void RefreshModeStatus()
    {
        string m = _mode == PickMode.Add ? "ADD" : _mode == PickMode.Remove ? "REMOVE" : "IDLE";
        SetStatus($"Mode: {m}\nActive portals: {_activePortals.Count}\n" +
                  (_mode == PickMode.Idle ? "Press Add/Remove Portal." : "Point at an object & pull trigger."));
        // Button sublabels reflect the live mode so state is visible in-headset.
        if (addPortalSubLabel != null)
            addPortalSubLabel.text = _mode == PickMode.Add ? "ON - point & trigger" : "off";
        if (removePortalSubLabel != null)
            removePortalSubLabel.text = _mode == PickMode.Remove ? "ON - point & trigger" : "off";
        VLog($"[ObjectPicker] Mode -> {_mode}");
    }

    // Add the cluster as a portal (kept active alongside any others). The cluster
    // GameObject is always active (its collider stays on clusterPickLayer so it
    // can be picked/removed later); we just turn its RENDERER on and set the
    // portal/debug material.
    private void AddPortal(int clusterId)
    {
        if (!_clusterObjects.TryGetValue(clusterId, out GameObject obj) || obj == null)
        {
            SetStatus($"Cluster {clusterId} not preloaded.");
            Debug.LogWarning($"[ObjectPicker] AddPortal: cluster{clusterId} not in preloaded set.");
            return;
        }

        var r = obj.GetComponentInChildren<MeshRenderer>(true);
        if (r != null)
        {
            if (debugVisibleClusters)
            {
                var dbg = new Material(Shader.Find("Unlit/Color"));
                dbg.color = debugClusterColor;
                dbg.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                dbg.renderQueue = 5000;
                r.material = dbg;
            }
            else if (stencilMaskMaterial != null)
            {
                r.material = stencilMaskMaterial;
            }
            // Renderer's layer controls rendering: portalMask for the stencil
            // effect, default (0) for the debug colour. The pick collider lives on
            // a separate child (ClusterPick_*), so this never affects picking.
            r.gameObject.layer = debugVisibleClusters ? 0 : portalMaskLayer;
            r.enabled = true;
        }

        _activePortals.Add(clusterId);
        SetStatus($"Portal added (cluster {clusterId}).\nActive portals: {_activePortals.Count}");
        VLog($"[ObjectPicker] AddPortal cluster {clusterId}. active={_activePortals.Count}");
    }

    // Remove the cluster's portal: turn its renderer back off (the GameObject and
    // its pick-collider stay active so it can be re-added or picked again).
    private void RemovePortal(int clusterId)
    {
        if (!_activePortals.Contains(clusterId))
        {
            SetStatus($"Cluster {clusterId} is not an active portal.");
            return;
        }
        if (_clusterObjects.TryGetValue(clusterId, out GameObject obj) && obj != null)
        {
            var r = obj.GetComponentInChildren<MeshRenderer>(true);
            if (r != null) r.enabled = false;
        }
        _activePortals.Remove(clusterId);
        SetStatus($"Portal removed (cluster {clusterId}).\nActive portals: {_activePortals.Count}");
        VLog($"[ObjectPicker] RemovePortal cluster {clusterId}. active={_activePortals.Count}");
    }

    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
    }

    // Gated diagnostic log: only prints when verboseLogging is enabled.
    private void VLog(string message)
    {
        if (verboseLogging) Debug.Log(message);
    }

}
