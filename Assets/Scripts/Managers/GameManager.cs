
 
 using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Arcor2.ClientSdk.ClientServices.Enums;
using Arcor2.ClientSdk.ClientServices.Managers;
using Arcor2.ClientSdk.Communication.OpenApi.Models;
using TMPro;
using UnityEngine;

public class GameManager : Singleton<GameManager> 
{

    public CommunicationManager CommunicationManager;
    public GameObject ScenesListMenu, SceneEditorMainMenu, AddGridMenu, FloorPrefab, RectanglePrefab, Rectangle, MATPrefab, TablePrefab, MatGridPrefab, ConveyorPrefab;
    [Header("Collision Boxes")]
    public GameObject CollisionBoxPrefab;
    [Tooltip("Set -1 to keep prefab/object layer unchanged.")]
    public int CollisionBoxLayer = -1;
    public Transform Origin;
    
    public Transform SceneMeshOrigin;
    [Header("Scene Mesh Alignment")]
    [SerializeField] private float sceneMeshMoveSpeed = 0.5f;
    [SerializeField] private float sceneMeshVerticalSpeed = 0.5f;
    [SerializeField] private float sceneMeshRotateSpeedDegPerSec = 90f;
    [SerializeField] private int sceneMeshAlignmentLayer = 10;
    [SerializeField] private int sceneMeshDefaultLayer = 10;
    [SerializeField] private float sceneMeshGripMoveSensitivity = 1.0f;
    [SerializeField] private float sceneMeshGripRotateSensitivity = 1.0f;
    [SerializeField] private float sceneMeshAlignSlowMultiplier = 0.25f;
    [SerializeField] private float sceneMeshAlignFastMultiplier = 2.0f;
    public bool SelectingRectangle = false;
    public TMP_Text SelectRectangleSubLabel, SelectRectangleLabel;
    Arcor2.ClientSdk.ClientServices.Managers.SceneManager SceneManager;
    public Dictionary<string, ObjectTypeManager> ObjectTypeManagerList = new Dictionary<string, ObjectTypeManager>();

    enum DrawState { Idle, DrawingDepth, DrawingWidth, DrawingHeight, Done }
    public enum ObjectType { MAT, Table, Conveyor, MatGrid }
    DrawState currentState = DrawState.Idle;

    Vector3 p0, p1, p2;
    GameObject Box;
    private readonly List<GameObject> spawnedCollisionBoxes = new List<GameObject>();
    float height = 1f;

    public Material BoxMaterial, BoxMaterialTemp;
    
    private MeshDownloadManager meshDownloadManager;
    private Dictionary<string, GameObject> importedMeshes = new Dictionary<string, GameObject>();
    private GameObject serverSceneMesh;
    private Vector3 serverSceneMeshLocalPosition = Vector3.zero;
    private Quaternion serverSceneMeshLocalRotation = Quaternion.identity;
    private Vector3 serverSceneMeshLocalScale = Vector3.one;
    private bool sceneMeshMoveGripActive;
    private Vector3 sceneMeshMoveGripStartControllerPosition;
    private Vector3 sceneMeshMoveGripStartMeshPosition;
    private bool sceneMeshRotateGripActive;
    private Quaternion sceneMeshRotateGripStartControllerRotation = Quaternion.identity;
    private Quaternion sceneMeshRotateGripStartMeshRotation = Quaternion.identity;
    private readonly Dictionary<Renderer, Material[]> sceneMeshOriginalMaterials = new Dictionary<Renderer, Material[]>();
    private readonly Dictionary<Renderer, Material[]> sceneMeshPortalMaterials = new Dictionary<Renderer, Material[]>();

    public event Action<string, List<MeshDownloadManager.AvailableMeshInfo>> SceneMeshBindingMissing;
    public event Action<bool> SceneMeshAlignmentModeChanged;

    public bool IsSceneMeshAlignmentMode { get; private set; }

    private void Start()
    {
        CommunicationManager.ConnectedToServer += ConnectedToServer;
        
        // Initialize managers
        meshDownloadManager = MeshDownloadManager.Instance;
        if (meshDownloadManager != null)
        {
            meshDownloadManager.SceneBindingMissing += OnSceneMeshBindingMissing;
        }
        
        // Subscribe to mesh import events
        MeshImporter.Instance.OnMeshImported += OnMeshImported;
    }

    private void OnDestroy()
    {
        if (CommunicationManager != null)
        {
            CommunicationManager.ConnectedToServer -= ConnectedToServer;
        }

        if (meshDownloadManager != null)
        {
            meshDownloadManager.SceneBindingMissing -= OnSceneMeshBindingMissing;
        }

        if (MeshImporter.Instance != null)
        {
            MeshImporter.Instance.OnMeshImported -= OnMeshImported;
        }

        CleanupServerSceneMeshImmediate();
    }

    private void Update()
    {
        if (!IsSceneMeshAlignmentMode || serverSceneMesh == null)
        {
            return;
        }

        ApplySceneMeshAlignmentInput();
    }


    private void FixedUpdate()
    {
        if (currentState == DrawState.DrawingHeight)
        {
            float d_h = OVRInput.Get(OVRInput.Axis2D.Any).y;
            UpdatePreviewBox_Height(d_h / 100f);
        }
    }

    private void ConnectedToServer()
    {
        Debug.LogError("Connected to server");
        foreach (var objectType in CommunicationManager.Arcor2Session.ObjectTypes)
        {
            ObjectTypeManagerList.Add(objectType.Id, objectType);
        }
        
        CommunicationManager.Arcor2Session.NavigationStateChanged += (_, args) =>
        {
            PerformNavigationStateChange(args.State);
        };
        ((INotifyCollectionChanged)CommunicationManager.Arcor2Session.Scenes).CollectionChanged += (_, args) =>
        {
            Debug.LogError("something has changed within scene");
            Debug.LogError(args.ToString());
        };
        PerformNavigationStateChange(CommunicationManager.Arcor2Session.NavigationState);
    }

    private void PerformNavigationStateChange(NavigationState state)
    {
        Debug.LogError($"Navigation state change to {state}");
        switch (state)

        {
            case NavigationState.Scene:
                HideScenesList();
                SceneOpened();
                break;
            case NavigationState.MenuListOfScenes:
                CleanupSceneObjects();
                ShowScenesList();
                break;
            default:
                CleanupSceneObjects();
                break;
        }
    }

    private void SceneOpened()
    {
        CleanupSceneObjects();
        SceneManager = CommunicationManager.Arcor2Session.Scenes[CommunicationManager.Arcor2Session.NavigationId];
        _ = LoadCurrentSceneMeshAsync();
        foreach (var ao in SceneManager.ActionObjects)
        {
            SpawnActionObject(ao);
            
            // Load mesh if available
            if (ao.ObjectType.Data.Meta.ObjectModel.Mesh != null)
            {
                MeshImporter.Instance.LoadModel(ao.ObjectType.Data.Meta.ObjectModel.Mesh, ao.Data.Meta.Id);
            }
            
        }
        ((INotifyCollectionChanged) SceneManager.ActionObjects).CollectionChanged += ActionObjectsChanged;
    }

    private void CleanupSceneObjects()
    {
        if (SceneManager != null && SceneManager.ActionObjects is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= ActionObjectsChanged;
        }

        SceneManager = null;

        foreach (var spawnedBox in spawnedCollisionBoxes)
        {
            if (spawnedBox != null)
            {
                Destroy(spawnedBox);
            }
        }

        spawnedCollisionBoxes.Clear();

        if (serverSceneMesh != null)
        {
            CleanupServerSceneMeshImmediate();
        }

        serverSceneMeshLocalPosition = Vector3.zero;
        serverSceneMeshLocalRotation = Quaternion.identity;
        serverSceneMeshLocalScale = Vector3.one;
    }

    private void ActionObjectsChanged(object sender, NotifyCollectionChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (ActionObjectManager ao in args.NewItems)
                {
                    SpawnActionObject(ao);
                    
                    // Load mesh if available
                    if (ao.ObjectType.Data.Meta.ObjectModel.Mesh != null)
                    {
                        MeshImporter.Instance.LoadModel(ao.ObjectType.Data.Meta.ObjectModel.Mesh, ao.Data.Meta.Id);
                    }
                }
                break;
                
        }
    }

    private ActionObject SpawnActionObject(Arcor2.ClientSdk.ClientServices.Managers.ActionObjectManager actionObject)
    {
        ActionObject newActionObject = null;
        Vector3 position = TransformConvertor.ROSToUnity(DataHelper.PositionToVector3(actionObject.Data.Meta.Pose.Position));
        Quaternion rotation = TransformConvertor.ROSToUnity(DataHelper.OrientationToQuaternion(actionObject.Data.Meta.Pose.Orientation));

        switch (actionObject.ObjectType.Id)
        {
            case "Mat":
                newActionObject = Instantiate(MATPrefab, Origin).GetComponent<GrabbableMat>();
                newActionObject.transform.localPosition = position;
                newActionObject.transform.localRotation = rotation;
                newActionObject.Initialize(actionObject);
                EnsureAlwaysVisibleContentRenderer(newActionObject.gameObject);
                break;
            case "MatGrid":
                newActionObject = Instantiate(MatGridPrefab, Origin).GetComponent<GrabbableMatGrid>();
                newActionObject.transform.localPosition = position;
                newActionObject.transform.localRotation = rotation;
                newActionObject.Initialize(actionObject);
                EnsureAlwaysVisibleContentRenderer(newActionObject.gameObject);
                break;
            default:
                
                if (actionObject.ObjectType.IsVirtualCollisionObject())
                {
                    Debug.LogError($"Objekt je kolizn�: {actionObject.Data.Meta.Name}, type: {actionObject.Data.Meta.Type}");

                    GameObject collisionBox;
                    if (CollisionBoxPrefab != null)
                    {
                        collisionBox = Instantiate(CollisionBoxPrefab, Origin);
                        collisionBox.AddComponent<PortalBoxBinder>();
                    }

                    else
                    {
                        collisionBox = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        collisionBox.transform.SetParent(Origin.transform, true);
                        var fallbackRenderer = collisionBox.GetComponent<MeshRenderer>();
                        if (fallbackRenderer != null && BoxMaterial != null)
                        {
                            fallbackRenderer.material = BoxMaterial;
                        }

                        Debug.LogWarning("CollisionBoxPrefab is not set. Using fallback primitive cube without Meta grabbable setup.");
                    }
                    var objectModel = actionObject.ObjectType.Data.Meta.ObjectModel.Box;
                    
                    collisionBox.transform.localScale = new Vector3((float) objectModel.SizeX, (float) objectModel.SizeY, (float) objectModel.SizeZ);
                    collisionBox.transform.localPosition = TransformConvertor.ROSToUnity(DataHelper.PositionToVector3(actionObject.Data.Meta.Pose.Position));
                    collisionBox.transform.localRotation = TransformConvertor.ROSToUnity(DataHelper.OrientationToQuaternion(actionObject.Data.Meta.Pose.Orientation));
                    if (CollisionBoxLayer >= 0 && CollisionBoxLayer <= 31)
                    {
                        SetLayerRecursively(collisionBox, CollisionBoxLayer);
                    }

                    spawnedCollisionBoxes.Add(collisionBox);
                    var binding = collisionBox.GetComponent<CollisionObjectBinding>();
                    if (binding == null)
                    {
                        binding = collisionBox.AddComponent<CollisionObjectBinding>();
                    }
                    binding.Initialize(actionObject, Origin);
                    if (collisionBox.GetComponent<CollisionBoxEditOverlay>() == null)
                    {
                        collisionBox.AddComponent<CollisionBoxEditOverlay>();
                    }
                    EditModeManager.Instance?.RegisterEditable(collisionBox);
                }

               
                break;
        }

        if (newActionObject != null)
        {
            EditModeManager.Instance?.RegisterEditable(newActionObject.gameObject);
        }

        return newActionObject;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        if (root == null)
        {
            return;
        }

        root.layer = layer;
        foreach (Transform child in root.transform)
        {
            if (child != null)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }
    }

    private void OnMeshImported(object sender, ImportedMeshEventArgs args)
    {
        string aoId = args.Name; // This is the action object ID passed from MeshImporter.LoadModel()
        
        // Store the mesh for later use
        importedMeshes[aoId] = args.RootGameObject;
        
        // Apply mesh to the action object if it has been spawned
        // Find the spawned action object and apply mesh to it
        ApplyMeshToActionObject(aoId, args.RootGameObject);
    }

    private void ApplyMeshToActionObject(string aoId, GameObject meshObject)
    {
        // Find action object in scene by its ID
        // This could be done by searching through spawned objects or storing a dictionary of spawned action objects

        // Position the mesh at the origin (you might want to adjust this based on action object position)
        if (meshObject != null)
        {
            meshObject.transform.SetParent(Origin, false);
            meshObject.transform.localPosition = Vector3.zero;
        }
    }

    private ObjectType StringToObjectType(string str)
    {
        if (str.StartsWith("mat"))
            return ObjectType.MAT;
        else if (str.StartsWith("conveyor"))
            return ObjectType.Conveyor;
        return ObjectType.Table;
    }

    private void HideScenesList()
    {
        ScenesListMenu.SetActive(false);
        SceneEditorMainMenu.SetActive(true);
        Debug.LogError("SwitchedToScene");
    }

    private void ShowScenesList()
    {
        ScenesListMenu.SetActive(true);
        SceneEditorMainMenu.SetActive(false);
        Debug.LogError("SwitchedToListOfScenes");

    }

    public void SceneLoadedCallback()
    {
        
        
        /*MRUKRoom room = MRUK.Instance.GetCurrentRoom();
        GameObject floor = Instantiate(FloorPrefab);
        floor.transform.SetPositionAndRotation(room.FloorAnchor.GetAnchorCenter(), room.FloorAnchor.transform.rotation);
        Rect? rect = room.FloorAnchor.PlaneRect;       
        floor.transform.localScale = new Vector3(room.FloorAnchor.PlaneRect.Value.width, 0.1f, room.FloorAnchor.PlaneRect.Value.height);
        //room.FloorAnchor.*/



    }

    
    public void SpawnMat()
    {
        Quaternion spawnRotation = Quaternion.identity;
        if (IsSceneMeshAlignmentMode)
        {
            SetSceneMeshAlignmentMode(false);
        }

        EditModeManager.Instance?.SetMatEditMode(true);
        AddNewObjectToScene(ObjectType.MAT, GetPositionInFrontOfCamera(), spawnRotation);   
    }

    public void SpawnTable()
    {
        Quaternion spawnRotation = Quaternion.identity;
        AddNewObjectToScene(ObjectType.Table, GetPositionInFrontOfCamera(), spawnRotation);
    }

    public void SpawnMatGrid()
    {   
        AddGridMenu.SetActive(true);
        SceneEditorMainMenu.SetActive(false);
        //Quaternion spawnRotation = Quaternion.identity;
        //AddNewObjectToScene(ObjectType.MatGrid, GetPositionInFrontOfCamera(), spawnRotation);
    }

    public void SpawnConveyor()
    {
        Quaternion spawnRotation = Quaternion.identity;
        AddNewObjectToScene(ObjectType.Conveyor, GetPositionInFrontOfCamera(), spawnRotation);
    }

    public Vector3 GetPositionInFrontOfCamera()
    {
        return Camera.main.transform.position + Camera.main.transform.forward * 1.0f;
    }
    /*
    public void SpawnMat(Vector3 position,  Quaternion rotation)
    {
        SpawnObject(ObjectType.MAT, position, rotation);
    }

    public void SpawnTable(Vector3 position, Quaternion rotation)
    {
        SpawnObject(ObjectType.Table, position, rotation);
    }
    public void SpawnGridMat(Vector3 position, Quaternion rotation)
    {

        SpawnObject(ObjectType.Conveyor, position, rotation);
    }*/

    public GameObject SpawnObject(ObjectType type, Vector3 position, Quaternion rotation)
    {
        GameObject prefab = null;
       switch (type)
        {
            case ObjectType.MAT:
                prefab = MATPrefab;
                break;
            case ObjectType.Table:
                prefab = TablePrefab;
                break;
            case ObjectType.Conveyor:
                prefab = ConveyorPrefab;
                break;
        }
        var spawnedObject = Instantiate(prefab, position, rotation, Origin);
        if (type == ObjectType.MAT || type == ObjectType.MatGrid)
        {
            EnsureAlwaysVisibleContentRenderer(spawnedObject);
        }
        EditModeManager.Instance?.RegisterEditable(spawnedObject);
        return spawnedObject;
    }

    public async void AddNewObjectToScene(ObjectType type, Vector3 position, Quaternion orientation, List<Arcor2.ClientSdk.Communication.OpenApi.Models.Parameter> parameters = null)
    {
        Debug.LogError("AddNewObjectToScene start");
        Debug.Assert(SceneManager != null);
        var pose = new Arcor2.ClientSdk.Communication.OpenApi.Models.Pose(
                    DataHelper.Vector3ToPosition(TransformConvertor.UnityToROS(position)),
                    DataHelper.QuaternionToOrientation(TransformConvertor.UnityToROS(orientation)));
       switch (type)
        {
            case ObjectType.MAT:                
                await SceneManager.AddActionObjectWithDefaultParametersAsync("Mat", GetFreeAOName("mat"), pose);
                break;
            case ObjectType.MatGrid:
                if (parameters != null)
                {
                    Debug.LogError("adding with parameters");
                    await SceneManager.AddActionObjectAsync("MatGrid", GetFreeAOName("mat_grid"), pose, parameters);
                } else
                {
                    Debug.LogError("adding without parameters");
                    await SceneManager.AddActionObjectWithDefaultParametersAsync("MatGrid", GetFreeAOName("mat_grid"), pose);
                }
                break;
            case ObjectType.Table:
                await SceneManager.AddActionObjectWithDefaultParametersAsync("Table", GetFreeAOName("table"), pose);
                break;
            case ObjectType.Conveyor:
                try { await SceneManager.AddActionObjectWithDefaultParametersAsync("WeighingMachine", GetFreeAOName("table"), pose);
                } catch(Exception ex)
                {
                    Debug.LogError(ex);
                }
                
                break;
        }
        Debug.LogError("AddNewObjectToScene finish");
    }

    public void SelectRectangle()
    {
        SelectingRectangle = !SelectingRectangle;
        if (SelectingRectangle)
        {
            SelectRectangleSubLabel.text = "In progress, press for finish";
            
        } else
        {
            SelectRectangleSubLabel.text = "";
            FinalizeBox();
            height = 1f;
            currentState = DrawState.Idle;
        }
    }

    public void PointOnFloorSelected(UnityEngine.Pose pose)
    {
        Vector3 pos = ToOriginSpace(pose.position);
        if (currentState == DrawState.Idle)
        {
            
            p0 = pos;
            //p0.y -= 0.3f; // move 30cm bellow ground
            Box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Box.transform.SetParent(Origin, false);
            Box.GetComponent<MeshRenderer>().material = BoxMaterialTemp;
            currentState = DrawState.DrawingDepth;
        }
        else if (currentState == DrawState.DrawingDepth)
        {
            p1 = pos;
            currentState = DrawState.DrawingWidth;
        }
        else if (currentState == DrawState.DrawingWidth)
        {
            p2 = pos;
            currentState = DrawState.DrawingHeight;
        }
        
    }

    public void PointOnFloorHovered(UnityEngine.Pose pose)
    {
        Vector3 pos = ToOriginSpace(pose.position);

        if (currentState == DrawState.DrawingDepth)
        {
            UpdatePreviewBox_Depth(pos);
        }
        else if (currentState == DrawState.DrawingWidth)
        {
            UpdatePreviewBox_Width(pos);
        }
    }

    void UpdatePreviewBox_Depth(Vector3 current)
    {
        Vector3 forwardVec = current - p0;
        float depth = forwardVec.magnitude;
        Vector3 forward = forwardVec.normalized;

        float height = 1.0f;
        float width = 0.01f;

        Vector3 center = p0 + forward * (depth * 0.5f) + Vector3.up * (height * 0.5f);

        Box.transform.localPosition = center;
        Box.transform.localRotation = Quaternion.LookRotation(forward, Vector3.up);
        Box.transform.localScale = new Vector3(width, height, depth);
    }

    void UpdatePreviewBox_Width(Vector3 current)
    {
        Vector3 forwardVec = p1 - p0;
        Vector3 forward = forwardVec.normalized;
        float depth = forwardVec.magnitude;

        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        float width = Vector3.Dot((current - p0), right);
        float height = 1.0f;

        Vector3 center = p0 + (forward * depth * 0.5f) + (right * width * 0.5f) + Vector3.up * (height * 0.5f);

        Box.transform.localPosition = center;
        Box.transform.localRotation = Quaternion.LookRotation(forward, Vector3.up);
        Box.transform.localScale = new Vector3(Mathf.Abs(width), height, depth);
    }

    void UpdatePreviewBox_Height(float heightDiff)
    {
        Box.transform.localPosition = new Vector3(Box.transform.localPosition.x, Box.transform.localPosition.y + heightDiff / 2f, Box.transform.localPosition.z);
        Box.transform.localScale = new Vector3(Box.transform.localScale.x, Box.transform.localScale.y + heightDiff, Box.transform.localScale.z);
    }

    private Vector3 ToOriginSpace(Vector3 worldPoint)
    {
        if (Origin == null)
            return worldPoint;
        return Origin.InverseTransformPoint(worldPoint);
    }

    async void FinalizeBox()
    {
        //CommunicationManager.Arcor2Session.CreateObjectTypeAsync(this, CommunicationManager.
        ObjectModel objectModel = new ObjectModel();
        ObjectTypeMeta objectTypeMeta;
        ObjectModel.TypeEnum type = ObjectModel.TypeEnum.Box;
        Box box = new(GetFreeObjectTypeName("CollisionBox"), (decimal) Box.transform.localScale.x, 
            (decimal) Box.transform.localScale.y, (decimal) Box.transform.localScale.z);
        objectModel.Type = type;
        objectModel.Box = box;
        objectTypeMeta = new ObjectTypeMeta(builtIn: false, description: "", type: box.Id, objectModel: objectModel,
            varBase: "CollisionObject", hasPose: true, modified: DateTime.Now);
        //Arcor2.ClientSdk.Communication.OpenApi.Models.Pose pose = new Arcor2.ClientSdk.Communication.OpenApi.Models.Pose(new Position(Box.transform.posi;

        Vector3 point = TransformConvertor.UnityToROS(Box.transform.localPosition);
        Arcor2.ClientSdk.Communication.OpenApi.Models.Pose pose = new Arcor2.ClientSdk.Communication.OpenApi.Models.Pose(DataHelper.Vector3ToPosition(point), DataHelper.QuaternionToOrientation(TransformConvertor.UnityToROS(Box.transform.localRotation)));
        AddVirtualCollisionObjectToSceneResponse result = await CommunicationManager.Arcor2Session.GetUnderlyingArcor2Client().AddVirtualCollisionObjectToSceneAsync(new AddVirtualCollisionObjectToSceneRequestArgs(objectTypeMeta.Type, pose, objectTypeMeta.ObjectModel));
        if (!result.Result)
        {
            foreach (var msg in result.Messages)
            {
                Debug.LogError(msg);
            }
        }
        GameObject.Destroy(Box.gameObject);
        Box = null;
    }

    public string GetFreeObjectTypeName(string objectTypeName)
    {
        int i = 1;
        bool hasFreeName;
        string freeName = objectTypeName;
        do
        {
            hasFreeName = true;
            if (ObjectTypeNameExists(freeName))
            {
                hasFreeName = false;
            }
            if (!hasFreeName)
                freeName = ToUnderscoreCase(objectTypeName) + "_" + i++.ToString();
        } while (!hasFreeName);

        return freeName;
    }

    bool ObjectTypeNameExists(string objectTypeName) {
        return ObjectTypeManagerList.ContainsKey(objectTypeName);
    }

    void CreateBoxFromThreePoints(Vector3 p0, Vector3 p1, Vector3 p2)
    {
        Debug.LogError($"p1: {p0}, p2: {p1}, p3: {p2}");
        // 1. Sm�r a d�lka prvn� hrany (hloubka kv�dru)
        Vector3 forwardVec = p1 - p0;
        float depth = forwardVec.magnitude;
        Vector3 forward = forwardVec.normalized;

        // 2. Sm�r druh� hrany = kolmo na prvn�, v rovin� podlahy (Y = up)
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        // 3. D�lka druh� hrany (���ka)
        float width = Vector3.Dot((p2 - p0), right);

        // 4. V��ka (konstantn�)
        float height = 1.0f;

        // 5. V�po�et st�edu kv�dru
        Vector3 center = p0 + (right * width * 0.5f) + (forward * depth * 0.5f) + (Vector3.up * height * 0.5f);

        // 6. Vytvo�en� kv�dru
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.transform.position = center;
        box.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        box.transform.localScale = new Vector3(Mathf.Abs(width), height, depth);
        Debug.LogError($"position: {center}, rotation: {box.transform.rotation}, width: {width}, height: {height}");
    }

    /// <summary>
    /// Finds free action object name, based on action object type (e.g. Box, Box_1, Box_2 etc.)
    /// </summary>
    /// <param name="aoType">Type of action object</param>
    /// <returns></returns>
    public string GetFreeAOName(string aoType)
    {
        int i = 1;
        bool hasFreeName;
        string freeName = ToUnderscoreCase(aoType);
        do
        {
            hasFreeName = true;
            if (ActionObjectWIthNameExists(freeName))
            {
                hasFreeName = false;
            }
            if (!hasFreeName)
                freeName = ToUnderscoreCase(aoType) + "_" + i++.ToString();
        } while (!hasFreeName);

        return freeName;
    }

    /// <summary>
    /// Transform string to underscore case (e.g. CamelCase to camel_case)
    /// </summary>
    /// <param name="str">String to be transformed</param>
    /// <returns>Underscored string</returns>
    public static string ToUnderscoreCase(string str)
    {
        return string.Concat(str.Select((x, i) => i > 0 && char.IsUpper(x) ? "_" + x.ToString() : x.ToString())).ToLower();
    }

    public bool ActionObjectWIthNameExists(string name)
    {
        if (SceneManager.ActionObjects == null) return false;
        foreach (var ao in SceneManager.ActionObjects)
        {
            if (ao.Data.Meta.Name == name)
                return true;
        }
        return false;
    }

    public async Task<bool> LoadCurrentSceneMeshAsync()
    {
        if (meshDownloadManager == null)
        {
            meshDownloadManager = MeshDownloadManager.Instance;
        }

        string sceneId = CommunicationManager?.Arcor2Session?.NavigationId;
        if (meshDownloadManager == null || string.IsNullOrWhiteSpace(sceneId) || GetSceneMeshParent() == null)
        {
            return false;
        }

        CaptureServerSceneMeshTransform();
        GameObject loadedMesh = await meshDownloadManager.LoadSceneMeshAsync(sceneId);
        if (loadedMesh == null)
        {
            return false;
        }

        if (meshDownloadManager.TryGetCachedSceneTransform(sceneId, out var relativePosition, out var relativeRotation))
        {
            serverSceneMeshLocalPosition = relativePosition;
            serverSceneMeshLocalRotation = relativeRotation;
        }

        AttachServerSceneMesh(loadedMesh);
        return true;
    }

    public async Task<bool> BindCurrentSceneMeshAsync(string meshId)
    {
        if (meshDownloadManager == null)
        {
            meshDownloadManager = MeshDownloadManager.Instance;
        }

        string sceneId = CommunicationManager?.Arcor2Session?.NavigationId;
        if (meshDownloadManager == null || string.IsNullOrWhiteSpace(sceneId))
        {
            return false;
        }

        var binding = await meshDownloadManager.BindSceneMeshWithDefaultFallbackAsync(sceneId, meshId);
        if (binding == null || !binding.Bound)
        {
            return false;
        }

        return await LoadCurrentSceneMeshAsync();
    }

    public async Task<bool> RebuildCurrentSceneMeshFromCollisionBoxesAsync(IReadOnlyList<Transform> boxTransforms)
    {
        if (meshDownloadManager == null)
        {
            meshDownloadManager = MeshDownloadManager.Instance;
        }

        string sceneId = CommunicationManager?.Arcor2Session?.NavigationId;
        if (meshDownloadManager == null || string.IsNullOrWhiteSpace(sceneId) || serverSceneMesh == null)
        {
            return false;
        }

        CaptureServerSceneMeshTransform();
        GameObject rebuiltMesh = await meshDownloadManager.RebuildSceneMeshFromBoxesAsync(sceneId, boxTransforms, serverSceneMesh.transform);
        if (rebuiltMesh == null)
        {
            return false;
        }

        AttachServerSceneMesh(rebuiltMesh);
        return true;
    }

    public async Task<bool> PersistCurrentSceneMeshTransformAsync()
    {
        if (meshDownloadManager == null)
        {
            meshDownloadManager = MeshDownloadManager.Instance;
        }

        string sceneId = CommunicationManager?.Arcor2Session?.NavigationId;
        if (meshDownloadManager == null || string.IsNullOrWhiteSpace(sceneId) || serverSceneMesh == null || GetSceneMeshParent() == null)
        {
            return false;
        }

        CaptureServerSceneMeshTransform();
        var response = await meshDownloadManager.UpdateSceneMeshTransformAsync(
            sceneId,
            serverSceneMeshLocalPosition,
            serverSceneMeshLocalRotation);

        return response != null;
    }

    public List<Transform> GetActiveCollisionBoxTransforms()
    {
        var collisionBindings = FindObjectsByType<CollisionObjectBinding>(FindObjectsSortMode.None);
        var transforms = new List<Transform>(collisionBindings.Length);
        for (int i = 0; i < collisionBindings.Length; i++)
        {
            if (collisionBindings[i] != null && collisionBindings[i].gameObject.activeInHierarchy)
            {
                transforms.Add(collisionBindings[i].transform);
            }
        }

        return transforms;
    }

    private void AttachServerSceneMesh(GameObject newMesh)
    {
        Transform sceneMeshParent = GetSceneMeshParent();
        if (newMesh == null || sceneMeshParent == null)
        {
            return;
        }

        serverSceneMesh = newMesh;
        serverSceneMesh.name = "ServerSceneMesh";
        serverSceneMesh.transform.SetParent(sceneMeshParent, false);
        serverSceneMesh.transform.localPosition = serverSceneMeshLocalPosition;
        serverSceneMesh.transform.localRotation = serverSceneMeshLocalRotation;
        serverSceneMesh.transform.localScale = serverSceneMeshLocalScale;
        SetCollidersEnabledRecursively(serverSceneMesh, false);
        CacheSceneMeshMaterials(serverSceneMesh);
        ApplySceneMeshRenderMode();
    }

    public void ToggleSceneMeshAlignmentMode()
    {
        SetSceneMeshAlignmentMode(!IsSceneMeshAlignmentMode);
    }

    public void SetSceneMeshAlignmentMode(bool enabled)
    {
        if (IsSceneMeshAlignmentMode == enabled)
        {
            return;
        }

        IsSceneMeshAlignmentMode = enabled;
        if (serverSceneMesh != null)
        {
            ApplySceneMeshRenderMode();
        }

        SceneMeshAlignmentModeChanged?.Invoke(IsSceneMeshAlignmentMode);
    }

    private void ApplySceneMeshRenderMode()
    {
        if (serverSceneMesh == null)
        {
            return;
        }

        bool alignmentMode = IsSceneMeshAlignmentMode;
        SetLayerRecursively(serverSceneMesh, alignmentMode ? sceneMeshAlignmentLayer : sceneMeshDefaultLayer);

        foreach (var entry in sceneMeshOriginalMaterials)
        {
            Renderer renderer = entry.Key;
            if (renderer == null)
            {
                continue;
            }

            if (alignmentMode || !sceneMeshPortalMaterials.TryGetValue(renderer, out var portalMaterials) || portalMaterials == null)
            {
                renderer.sharedMaterials = entry.Value;
            }
            else
            {
                renderer.sharedMaterials = portalMaterials;
            }
        }
    }

    private void CacheSceneMeshMaterials(GameObject root)
    {
        ClearSceneMeshMaterialCache();
        if (root == null)
        {
            return;
        }

        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
            {
                continue;
            }

            Material[] originalMaterials = renderer.sharedMaterials;
            sceneMeshOriginalMaterials[renderer] = originalMaterials;
            sceneMeshPortalMaterials[renderer] = CreatePortalMaterialSet(originalMaterials);
        }
    }

    private void ClearSceneMeshMaterialCache()
    {
        foreach (var materialSet in sceneMeshPortalMaterials.Values)
        {
            if (materialSet == null)
            {
                continue;
            }

            for (int i = 0; i < materialSet.Length; i++)
            {
                if (materialSet[i] != null)
                {
                    Destroy(materialSet[i]);
                }
            }
        }

        sceneMeshOriginalMaterials.Clear();
        sceneMeshPortalMaterials.Clear();
    }

    private static Material[] CreatePortalMaterialSet(Material[] originalMaterials)
    {
        Shader portalShader = Shader.Find("Custom/PortalContentUnlit");
        if (portalShader == null || originalMaterials == null)
        {
            return null;
        }

        Material[] portalMaterials = new Material[originalMaterials.Length];
        for (int i = 0; i < originalMaterials.Length; i++)
        {
            Material source = originalMaterials[i];
            if (source == null)
            {
                continue;
            }

            var portalMaterial = new Material(portalShader);
            CopyPortalMaterialProperties(source, portalMaterial);
            portalMaterial.SetFloat("_DebugMode", 0f);   // ← ADD THIS LINE (1 = Occ)

            portalMaterials[i] = portalMaterial;
        }

        return portalMaterials;
    }

    private static void CopyPortalMaterialProperties(Material source, Material destination)
    {
        Texture sourceTexture = null;
        if (source.HasProperty("_BaseMap"))
        {
            sourceTexture = source.GetTexture("_BaseMap");
        }
        else if (source.HasProperty("_MainTex"))
        {
            sourceTexture = source.GetTexture("_MainTex");
        }

        if (sourceTexture != null && destination.HasProperty("_BaseMap"))
        {
            destination.SetTexture("_BaseMap", sourceTexture);
        }

        Color sourceColor = Color.white;
        if (source.HasProperty("_BaseColor"))
        {
            sourceColor = source.GetColor("_BaseColor");
        }
        else if (source.HasProperty("_Color"))
        {
            sourceColor = source.GetColor("_Color");
        }

        if (destination.HasProperty("_BaseColor"))
        {
            destination.SetColor("_BaseColor", sourceColor);
        }
    }

    private static void EnsureAlwaysVisibleContentRenderer(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        var overlayRenderer = root.GetComponent<AlwaysVisibleContentRenderer>();
        if (overlayRenderer == null)
        {
            overlayRenderer = root.AddComponent<AlwaysVisibleContentRenderer>();
        }

        overlayRenderer.Refresh();
    }

    private void ApplySceneMeshAlignmentInput()
    {
        float sensitivityMultiplier = GetSceneMeshAlignmentSensitivityMultiplier();
        bool leftGripHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger);
        bool rightGripHeld = OVRInput.Get(OVRInput.Button.SecondaryHandTrigger);

        if (leftGripHeld)
        {
            UpdateSceneMeshGripMove(sensitivityMultiplier);
        }
        else
        {
            sceneMeshMoveGripActive = false;
        }

        if (rightGripHeld)
        {
            UpdateSceneMeshGripRotation(sensitivityMultiplier);
        }
        else
        {
            sceneMeshRotateGripActive = false;
        }

        Vector2 planarInput = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        Vector2 secondaryInput = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        float rotateYawInput = secondaryInput.x;
        float rotatePitchInput = 0f;
        float rotateRollInput = 0f;
        float verticalInput = secondaryInput.y;

#if UNITY_EDITOR
#if ENABLE_INPUT_SYSTEM
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.aKey.isPressed) planarInput.x -= 1f;
            if (keyboard.dKey.isPressed) planarInput.x += 1f;
            if (keyboard.sKey.isPressed) planarInput.y -= 1f;
            if (keyboard.wKey.isPressed) planarInput.y += 1f;
            if (keyboard.rKey.isPressed) verticalInput += 1f;
            if (keyboard.fKey.isPressed) verticalInput -= 1f;
            if (keyboard.qKey.isPressed) rotateYawInput -= 1f;
            if (keyboard.eKey.isPressed) rotateYawInput += 1f;
            if (keyboard.tKey.isPressed) rotatePitchInput += 1f;
            if (keyboard.gKey.isPressed) rotatePitchInput -= 1f;
            if (keyboard.zKey.isPressed) rotateRollInput -= 1f;
            if (keyboard.xKey.isPressed) rotateRollInput += 1f;
        }
#endif
#endif

        var cameraTransform = Camera.main != null ? Camera.main.transform : Origin;
        Vector3 forward = cameraTransform != null ? cameraTransform.forward : Vector3.forward;
        Vector3 right = cameraTransform != null ? cameraTransform.right : Vector3.right;
        forward.y = 0f;
        right.y = 0f;
        forward.Normalize();
        right.Normalize();

        Vector3 horizontalDelta = (right * planarInput.x + forward * planarInput.y) * (sceneMeshMoveSpeed * sensitivityMultiplier * Time.deltaTime);
        Vector3 verticalDelta = Vector3.up * (verticalInput * sceneMeshVerticalSpeed * sensitivityMultiplier * Time.deltaTime);
        float yawDelta = rotateYawInput * sceneMeshRotateSpeedDegPerSec * sensitivityMultiplier * Time.deltaTime;
        float pitchDelta = rotatePitchInput * sceneMeshRotateSpeedDegPerSec * sensitivityMultiplier * Time.deltaTime;
        float rollDelta = rotateRollInput * sceneMeshRotateSpeedDegPerSec * sensitivityMultiplier * Time.deltaTime;

        serverSceneMesh.transform.position += horizontalDelta + verticalDelta;
        if (Mathf.Abs(yawDelta) > 0.001f)
        {
            serverSceneMesh.transform.Rotate(Vector3.up, yawDelta, Space.World);
        }

        if (Mathf.Abs(pitchDelta) > 0.001f)
        {
            serverSceneMesh.transform.Rotate(serverSceneMesh.transform.right, pitchDelta, Space.World);
        }

        if (Mathf.Abs(rollDelta) > 0.001f)
        {
            serverSceneMesh.transform.Rotate(serverSceneMesh.transform.forward, rollDelta, Space.World);
        }

        CaptureServerSceneMeshTransform();
    }

    private float GetSceneMeshAlignmentSensitivityMultiplier()
    {
        bool slowHeld = OVRInput.Get(OVRInput.Button.One);
        bool fastHeld = OVRInput.Get(OVRInput.Button.Two);

#if UNITY_EDITOR
#if ENABLE_INPUT_SYSTEM
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null)
        {
            slowHeld |= keyboard.leftShiftKey.isPressed;
            fastHeld |= keyboard.leftCtrlKey.isPressed;
        }
#endif
#endif

        if (slowHeld)
        {
            return sceneMeshAlignSlowMultiplier;
        }

        if (fastHeld)
        {
            return sceneMeshAlignFastMultiplier;
        }

        return 1f;
    }

    private void UpdateSceneMeshGripMove(float sensitivityMultiplier)
    {
        Vector3 controllerPosition = OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch);
        Vector3 worldControllerPosition = TransformControllerLocalToWorld(controllerPosition);

        if (!sceneMeshMoveGripActive)
        {
            sceneMeshMoveGripActive = true;
            sceneMeshMoveGripStartControllerPosition = worldControllerPosition;
            sceneMeshMoveGripStartMeshPosition = serverSceneMesh.transform.position;
            return;
        }

        Vector3 controllerDelta = worldControllerPosition - sceneMeshMoveGripStartControllerPosition;
        serverSceneMesh.transform.position = sceneMeshMoveGripStartMeshPosition + (controllerDelta * sceneMeshGripMoveSensitivity * sensitivityMultiplier);
    }

    private void UpdateSceneMeshGripRotation(float sensitivityMultiplier)
    {
        Quaternion controllerRotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        Quaternion worldControllerRotation = TransformControllerLocalToWorld(controllerRotation);

        if (!sceneMeshRotateGripActive)
        {
            sceneMeshRotateGripActive = true;
            sceneMeshRotateGripStartControllerRotation = worldControllerRotation;
            sceneMeshRotateGripStartMeshRotation = serverSceneMesh.transform.rotation;
            return;
        }

        Quaternion controllerDelta = worldControllerRotation * Quaternion.Inverse(sceneMeshRotateGripStartControllerRotation);
        Quaternion targetRotation = controllerDelta * sceneMeshRotateGripStartMeshRotation;
        serverSceneMesh.transform.rotation = Quaternion.Slerp(
            sceneMeshRotateGripStartMeshRotation,
            targetRotation,
            Mathf.Max(0f, sceneMeshGripRotateSensitivity * sensitivityMultiplier));
    }

    private Vector3 TransformControllerLocalToWorld(Vector3 localPosition)
    {
        Transform trackingSpace = Origin != null ? Origin : transform;
        return trackingSpace.TransformPoint(localPosition);
    }

    private Quaternion TransformControllerLocalToWorld(Quaternion localRotation)
    {
        Transform trackingSpace = Origin != null ? Origin : transform;
        return trackingSpace.rotation * localRotation;
    }

    private void CaptureServerSceneMeshTransform()
    {
        if (serverSceneMesh == null)
        {
            return;
        }

        serverSceneMeshLocalPosition = serverSceneMesh.transform.localPosition;
        serverSceneMeshLocalRotation = serverSceneMesh.transform.localRotation;
        serverSceneMeshLocalScale = serverSceneMesh.transform.localScale;
    }

    private void CleanupServerSceneMeshImmediate()
    {
        if (serverSceneMesh == null)
        {
            return;
        }

        CaptureServerSceneMeshTransform();
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            DestroyImmediate(serverSceneMesh);
        }
        else
        {
            Destroy(serverSceneMesh);
        }
#else
        Destroy(serverSceneMesh);
#endif
        serverSceneMesh = null;
    }

    private static void SetCollidersEnabledRecursively(GameObject root, bool enabled)
    {
        if (root == null)
        {
            return;
        }

        var colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
            {
                colliders[i].enabled = enabled;
            }
        }
    }

    private void OnSceneMeshBindingMissing(string sceneId, List<MeshDownloadManager.AvailableMeshInfo> availableMeshes)
    {
        if (sceneId != CommunicationManager?.Arcor2Session?.NavigationId)
        {
            return;
        }

        SceneMeshBindingMissing?.Invoke(sceneId, availableMeshes);
    }

    private Transform GetSceneMeshParent()
    {
        return SceneMeshOrigin != null ? SceneMeshOrigin : Origin;
    }

}
