using System.Collections;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Android;

public class MozartSpatialBridge : MonoBehaviour
{
    [Header("Content Roots")]
    [SerializeField] private Transform contentOrigin;
    [SerializeField] private Transform sceneMeshOrigin;
    [SerializeField] private Transform roomReferenceRoot;

    [Header("Alignment")]
    [SerializeField] private bool alignOriginToScannedRoom = true;
    [SerializeField] private bool useFloorAnchorAsSpatialReference = true;
    [SerializeField] private bool flattenRoomReferenceToWorldUp = true;
    [SerializeField] private bool captureInitialOriginOffsetOnStart = true;

    private Vector3 _initialOriginLocalPosition = Vector3.zero;
    private Quaternion _initialOriginLocalRotation = Quaternion.identity;
    private Vector3 _initialOriginLocalScale = Vector3.one;
    private bool _initialOriginOffsetCaptured;
    private bool _mrukEventsRegistered;
    private Coroutine _alignmentRetryCoroutine;

    private void OnEnable()
    {
        TryRegisterMrukEvents();
        StartAlignmentRetryLoop();
    }

    private void OnDisable()
    {
        StopAlignmentRetryLoop();
        UnregisterMrukEvents();
    }

    private async void Start()
    {
        CaptureInitialOriginOffsetIfNeeded();
        EnsureHierarchyRoots();
        TryAlignOriginToCurrentRoom();
        LogSpatialState("start");
        StartCoroutine(LogDelayedStates());
        await EnsureDeviceSceneLoadedAsync();
        TryRegisterMrukEvents();
        TryAlignOriginToCurrentRoom();
        StartAlignmentRetryLoop();
    }

    private void OnSceneLoaded()
    {
        TryAlignOriginToCurrentRoom();
        LogSpatialState("scene_loaded");
    }

    private void OnRoomCreated(MRUKRoom room)
    {
        TryAlignOriginToCurrentRoom();
        LogSpatialState($"room_created:{room?.name ?? "null"}");
    }

    private void OnRoomUpdated(MRUKRoom room)
    {
        TryAlignOriginToCurrentRoom();
        LogSpatialState($"room_updated:{room?.name ?? "null"}");
    }

    private void OnRoomRemoved(MRUKRoom room)
    {
        LogSpatialState($"room_removed:{room?.name ?? "null"}");
    }

    private IEnumerator LogDelayedStates()
    {
        yield return new WaitForSeconds(0.5f);
        LogSpatialState("after_0.5s");
        yield return new WaitForSeconds(1.5f);
        LogSpatialState("after_2.0s");
        yield return new WaitForSeconds(3.0f);
        LogSpatialState("after_5.0s");
        yield return new WaitForSeconds(5.0f);
        LogSpatialState("after_10.0s");
        yield return new WaitForSeconds(10.0f);
        LogSpatialState("after_20.0s");
    }

    private async Task EnsureDeviceSceneLoadedAsync()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        await WaitForScenePermissionAsync();

        if (MRUK.Instance == null)
        {
            Debug.LogError("[MozartSpatialBridge] MRUK instance is null.");
            return;
        }

        if (FindFirstObjectByType<MRUKRoom>() != null)
        {
            return;
        }

        var sceneModel = MRUK.Instance.SceneSettings.EnableHighFidelityScene
            ? MRUK.SceneModel.V2FallbackV1
            : MRUK.SceneModel.V1;

        var result = await MRUK.Instance.LoadSceneFromDevice(
            requestSceneCaptureIfNoDataFound: true,
            removeMissingRooms: true,
            sceneModel: sceneModel);

        LogSpatialState($"manual_load_result:{result}");
#endif
    }

    private static async Task WaitForScenePermissionAsync()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string permission = "com.oculus.permission.USE_SCENE";
        for (int i = 0; i < 80; i++)
        {
            if (Permission.HasUserAuthorizedPermission(permission))
            {
                Debug.Log("[MozartSpatialBridge] Scene permission granted.");
                return;
            }

            await Task.Delay(250);
        }

        Debug.LogWarning("[MozartSpatialBridge] Timed out waiting for scene permission.");
#endif
    }

    private bool TryAlignOriginToCurrentRoom()
    {
        if (!alignOriginToScannedRoom || contentOrigin == null)
        {
            return false;
        }

        CaptureInitialOriginOffsetIfNeeded();
        EnsureHierarchyRoots();

        if (!TryGetCurrentRoomReference(out Transform referenceParent, out Vector3 referencePosition, out Quaternion referenceRotation))
        {
            return false;
        }

        string previousParentName = roomReferenceRoot.parent != null ? roomReferenceRoot.parent.name : "<root>";
        roomReferenceRoot.SetParent(null, false);
        roomReferenceRoot.SetPositionAndRotation(referencePosition, referenceRotation);
        if (referenceParent != null)
        {
            roomReferenceRoot.SetParent(referenceParent, true);
        }

        if (contentOrigin.parent != roomReferenceRoot)
        {
            contentOrigin.SetParent(roomReferenceRoot, false);
        }

        contentOrigin.localPosition = _initialOriginLocalPosition;
        contentOrigin.localRotation = _initialOriginLocalRotation;
        contentOrigin.localScale = _initialOriginLocalScale;
        string currentParentName = roomReferenceRoot.parent != null ? roomReferenceRoot.parent.name : "<root>";
        Debug.Log($"[MozartSpatialBridge] Aligned room reference root. Parent: {previousParentName} -> {currentParentName}");
        return true;
    }

    private bool TryGetCurrentRoomReference(out Transform referenceParent, out Vector3 referencePosition, out Quaternion referenceRotation)
    {
        referenceParent = null;
        referencePosition = Vector3.zero;
        referenceRotation = Quaternion.identity;

        MRUKRoom room = FindFirstObjectByType<MRUKRoom>();
        if (room == null)
        {
            return false;
        }

        if (useFloorAnchorAsSpatialReference && room.FloorAnchor != null)
        {
            referenceParent = room.FloorAnchor.transform;
            referencePosition = room.FloorAnchor.GetAnchorCenter();
            referenceRotation = GetReferenceRotation(room.FloorAnchor.transform.rotation);
            return true;
        }

        referenceParent = room.transform;
        referencePosition = room.transform.position;
        referenceRotation = GetReferenceRotation(room.transform.rotation);
        return true;
    }

    private Quaternion GetReferenceRotation(Quaternion sourceRotation)
    {
        if (!flattenRoomReferenceToWorldUp)
        {
            return sourceRotation;
        }

        Vector3 flattenedForward = Vector3.ProjectOnPlane(sourceRotation * Vector3.forward, Vector3.up);
        if (flattenedForward.sqrMagnitude < 1e-6f)
        {
            return Quaternion.identity;
        }

        return Quaternion.LookRotation(flattenedForward.normalized, Vector3.up);
    }

    private void EnsureHierarchyRoots()
    {
        if (contentOrigin == null && GameManager.Instance != null)
        {
            contentOrigin = GameManager.Instance.Origin;
        }

        if (sceneMeshOrigin == null && GameManager.Instance != null)
        {
            sceneMeshOrigin = GameManager.Instance.SceneMeshOrigin;
        }

        if (roomReferenceRoot == null)
        {
            var existingRoot = transform.Find("RoomReferenceRoot");
            roomReferenceRoot = existingRoot != null
                ? existingRoot
                : new GameObject("RoomReferenceRoot").transform;
            roomReferenceRoot.SetParent(transform, false);
        }

        if (contentOrigin != null && contentOrigin.parent != roomReferenceRoot)
        {
            contentOrigin.SetParent(roomReferenceRoot, true);
        }

        if (contentOrigin != null && sceneMeshOrigin != null && sceneMeshOrigin.parent != contentOrigin)
        {
            sceneMeshOrigin.SetParent(contentOrigin, true);
        }
    }

    private void CaptureInitialOriginOffsetIfNeeded()
    {
        if (_initialOriginOffsetCaptured || contentOrigin == null)
        {
            return;
        }

        if (captureInitialOriginOffsetOnStart)
        {
            _initialOriginLocalPosition = contentOrigin.localPosition;
            _initialOriginLocalRotation = contentOrigin.localRotation;
            _initialOriginLocalScale = contentOrigin.localScale;
        }
        else
        {
            _initialOriginLocalPosition = Vector3.zero;
            _initialOriginLocalRotation = Quaternion.identity;
            _initialOriginLocalScale = Vector3.one;
        }

        _initialOriginOffsetCaptured = true;
    }

    private void TryRegisterMrukEvents()
    {
        if (_mrukEventsRegistered || MRUK.Instance == null)
        {
            return;
        }

        MRUK.Instance.SceneLoadedEvent.AddListener(OnSceneLoaded);
        MRUK.Instance.RoomCreatedEvent.AddListener(OnRoomCreated);
        MRUK.Instance.RoomUpdatedEvent.AddListener(OnRoomUpdated);
        MRUK.Instance.RoomRemovedEvent.AddListener(OnRoomRemoved);
        _mrukEventsRegistered = true;
        Debug.Log("[MozartSpatialBridge] Registered MRUK event listeners.");
    }

    private void UnregisterMrukEvents()
    {
        if (!_mrukEventsRegistered || MRUK.Instance == null)
        {
            _mrukEventsRegistered = false;
            return;
        }

        MRUK.Instance.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
        MRUK.Instance.RoomCreatedEvent.RemoveListener(OnRoomCreated);
        MRUK.Instance.RoomUpdatedEvent.RemoveListener(OnRoomUpdated);
        MRUK.Instance.RoomRemovedEvent.RemoveListener(OnRoomRemoved);
        _mrukEventsRegistered = false;
    }

    private void StartAlignmentRetryLoop()
    {
        if (_alignmentRetryCoroutine == null)
        {
            _alignmentRetryCoroutine = StartCoroutine(AlignmentRetryLoop());
        }
    }

    private void StopAlignmentRetryLoop()
    {
        if (_alignmentRetryCoroutine != null)
        {
            StopCoroutine(_alignmentRetryCoroutine);
            _alignmentRetryCoroutine = null;
        }
    }

    private IEnumerator AlignmentRetryLoop()
    {
        const float retryIntervalSeconds = 1f;
        while (enabled)
        {
            TryRegisterMrukEvents();

            if (TryAlignOriginToCurrentRoom())
            {
                Transform currentParent = roomReferenceRoot != null ? roomReferenceRoot.parent : null;
                bool parentLooksValid = currentParent != null &&
                                        currentParent != transform &&
                                        currentParent.name != "RoomReferenceRoot";
                if (parentLooksValid)
                {
                    Debug.Log($"[MozartSpatialBridge] Room reference root attached to '{currentParent.name}'. Stopping retry loop.");
                    _alignmentRetryCoroutine = null;
                    yield break;
                }
            }

            yield return new WaitForSeconds(retryIntervalSeconds);
        }

        _alignmentRetryCoroutine = null;
    }

    private void LogSpatialState(string phase)
    {
        MRUKRoom[] rooms = FindObjectsByType<MRUKRoom>(FindObjectsSortMode.None);
        MRUKRoom room = rooms.Length > 0 ? rooms[0] : null;
        MRUKAnchor floor = room != null ? room.FloorAnchor : null;
        Transform cameraTransform = Camera.main != null ? Camera.main.transform : null;
        Transform trackingSpace = FindTrackingSpaceTransform();

        string roomSummary = room == null
            ? "room=null"
            : $"room={room.name}, isLocal={room.IsLocal}, anchors={room.Anchors.Count}, walls={room.WallAnchors.Count}";
        string floorSummary = floor == null
            ? "floor=null"
            : $"floor={FormatVector3(floor.GetAnchorCenter())}, rot={FormatVector3(floor.transform.eulerAngles)}";

        Debug.Log(
            $"[MozartSpatialBridge] phase={phase} " +
            $"roomCount={rooms.Length} " +
            $"{roomSummary} " +
            $"{floorSummary} " +
            $"camera={FormatTransform(cameraTransform)} " +
            $"trackingSpace={FormatTransform(trackingSpace)}");
    }

    private static Transform FindTrackingSpaceTransform()
    {
        var cameraRig = FindFirstObjectByType<OVRCameraRig>();
        return cameraRig != null ? cameraRig.trackingSpace : null;
    }

    private static string FormatTransform(Transform target)
    {
        if (target == null)
        {
            return "null";
        }

        return $"pos={FormatVector3(target.position)}, rot={FormatVector3(target.eulerAngles)}, localPos={FormatVector3(target.localPosition)}";
    }

    private static string FormatVector3(Vector3 value)
    {
        return $"({value.x:F3};{value.y:F3};{value.z:F3})";
    }
}
