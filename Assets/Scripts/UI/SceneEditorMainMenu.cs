using Arcor2.ClientSdk.ClientServices;
using Arcor2.ClientSdk.ClientServices.Enums;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SceneEditorMainMenu : MonoBehaviour
{
    public CommunicationManager CommunicationManager;
    public GameManager GameManager;
    public SpatialAnchorOriginManager SpatialAnchorOriginManager;
    public string DefaultMeshIdForBinding;
    public TMP_Text CloseSceneSubLabel, SaveSceneSubLabel, EditModeSubLabel, MatEditModeSubLabel, CutMeshSubLabel, MeshAlignmentSubLabel, OriginAnchorSubLabel;

    private bool _isCollisionMeshRebuildInProgress;
    private Toggle _collisionEditToggle;

    private void Start()
    {
        RefreshEditModeLabel();
        SubscribeGameManager();
        RefreshMeshAlignmentLabel();
        RefreshOriginAnchorLabel();
    }

    private void OnEnable()
    {
        SubscribeGameManager();

        if (EditModeManager.Instance != null)
        {
            EditModeManager.Instance.EditModeChanged += OnEditModeChanged;
            EditModeManager.Instance.MatEditModeChanged += OnMatEditModeChanged;
        }

        if (GameManager != null)
        {
            GameManager.SceneMeshAlignmentModeChanged += OnSceneMeshAlignmentModeChanged;
        }

        SubscribeSpatialAnchorManager();
    }

    private void OnDisable()
    {
        if (EditModeManager.Instance != null)
        {
            EditModeManager.Instance.EditModeChanged -= OnEditModeChanged;
            EditModeManager.Instance.MatEditModeChanged -= OnMatEditModeChanged;
        }

        if (GameManager != null)
        {
            GameManager.SceneMeshBindingMissing -= OnBindingMissing;
            GameManager.SceneMeshAlignmentModeChanged -= OnSceneMeshAlignmentModeChanged;
        }

        if (SpatialAnchorOriginManager != null)
        {
            SpatialAnchorOriginManager.OriginAnchorEditModeChanged -= OnOriginAnchorEditModeChanged;
        }
    }

    public async void CloseScene()
    {
        Debug.LogError("Trying to close scene");
        try
        {
            await CommunicationManager.Arcor2Session.Scenes[CommunicationManager.Arcor2Session.NavigationId!].CloseAsync();
            CloseSceneSubLabel.text = "";
        }
        catch (Arcor2Exception e)
        {
            CloseSceneSubLabel.text = "Failed to close scene (unsaved changes)";
        }
        
    }

    public async void SaveScene()
    {
        try
        {
            await CommunicationManager.Arcor2Session.Scenes[CommunicationManager.Arcor2Session.NavigationId!].SaveAsync();
            SaveSceneSubLabel.text = "";
            CloseSceneSubLabel.text = "";
        }
        catch (Arcor2Exception e)
        {
            Debug.LogError(e.ToString());
            SaveSceneSubLabel.text = "Failed to save scene";
        }
    }

    public void ToggleEditMode()
    {
        if (_isCollisionMeshRebuildInProgress)
        {
            return;
        }

        if (SpatialAnchorOriginManager != null && SpatialAnchorOriginManager.IsOriginAnchorEditMode)
        {
            SpatialAnchorOriginManager.SetOriginAnchorEditMode(false);
        }

        if (GameManager != null && GameManager.IsSceneMeshAlignmentMode)
        {
            GameManager.SetSceneMeshAlignmentMode(false);
        }

        EditModeManager.Instance.ToggleEditMode();
        RefreshEditModeLabel();
    }

    public void SetEditMode(bool enabled)
    {
        if (_isCollisionMeshRebuildInProgress)
        {
            return;
        }

        if (enabled && SpatialAnchorOriginManager != null && SpatialAnchorOriginManager.IsOriginAnchorEditMode)
        {
            SpatialAnchorOriginManager.SetOriginAnchorEditMode(false);
        }

        if (enabled && GameManager != null && GameManager.IsSceneMeshAlignmentMode)
        {
            GameManager.SetSceneMeshAlignmentMode(false);
        }

        EditModeManager.Instance.SetEditMode(enabled);
        RefreshEditModeLabel();
    }

    public void ToggleMatEditMode()
    {
        if (SpatialAnchorOriginManager != null && SpatialAnchorOriginManager.IsOriginAnchorEditMode)
        {
            SpatialAnchorOriginManager.SetOriginAnchorEditMode(false);
        }

        if (GameManager != null && GameManager.IsSceneMeshAlignmentMode)
        {
            GameManager.SetSceneMeshAlignmentMode(false);
        }

        EditModeManager.Instance.ToggleMatEditMode();
        RefreshEditModeLabel();
    }

    public void SetMatEditMode(bool enabled)
    {
        if (enabled && SpatialAnchorOriginManager != null && SpatialAnchorOriginManager.IsOriginAnchorEditMode)
        {
            SpatialAnchorOriginManager.SetOriginAnchorEditMode(false);
        }

        if (enabled && GameManager != null && GameManager.IsSceneMeshAlignmentMode)
        {
            GameManager.SetSceneMeshAlignmentMode(false);
        }

        EditModeManager.Instance.SetMatEditMode(enabled);
        RefreshEditModeLabel();
    }

    public async void ToggleSceneMeshAlignmentMode()
    {
        if (GameManager == null)
        {
            return;
        }

        if (SpatialAnchorOriginManager != null && SpatialAnchorOriginManager.IsOriginAnchorEditMode)
        {
            SpatialAnchorOriginManager.SetOriginAnchorEditMode(false);
        }

        if (!GameManager.IsSceneMeshAlignmentMode && EditModeManager.Instance.IsAnyEditMode)
        {
            EditModeManager.Instance.SetEditMode(false);
            EditModeManager.Instance.SetMatEditMode(false);
            RefreshEditModeLabel();
        }

        bool wasEnabled = GameManager.IsSceneMeshAlignmentMode;
        GameManager.ToggleSceneMeshAlignmentMode();

        if (wasEnabled)
        {
            if (MeshAlignmentSubLabel != null)
            {
                MeshAlignmentSubLabel.text = "Mesh align: saving...";
            }

            bool saved = await GameManager.PersistCurrentSceneMeshTransformAsync();
            if (MeshAlignmentSubLabel != null)
            {
                MeshAlignmentSubLabel.text = saved ? "Mesh align: OFF" : "Mesh align: save failed";
            }
        }

        RefreshMeshAlignmentLabel();
    }

    public async void SetSceneMeshAlignmentMode(bool enabled)
    {
        if (GameManager == null)
        {
            return;
        }

        if (enabled && SpatialAnchorOriginManager != null && SpatialAnchorOriginManager.IsOriginAnchorEditMode)
        {
            SpatialAnchorOriginManager.SetOriginAnchorEditMode(false);
        }

        if (enabled && EditModeManager.Instance.IsAnyEditMode)
        {
            EditModeManager.Instance.SetEditMode(false);
            EditModeManager.Instance.SetMatEditMode(false);
            RefreshEditModeLabel();
        }

        bool wasEnabled = GameManager.IsSceneMeshAlignmentMode;
        GameManager.SetSceneMeshAlignmentMode(enabled);

        if (wasEnabled && !enabled)
        {
            if (MeshAlignmentSubLabel != null)
            {
                MeshAlignmentSubLabel.text = "Mesh align: saving...";
            }

            bool saved = await GameManager.PersistCurrentSceneMeshTransformAsync();
            if (MeshAlignmentSubLabel != null)
            {
                MeshAlignmentSubLabel.text = saved ? "Mesh align: OFF" : "Mesh align: save failed";
            }
        }

        RefreshMeshAlignmentLabel();
    }

    public void ToggleOriginAnchorEditMode()
    {
        if (SpatialAnchorOriginManager == null)
        {
            return;
        }

        bool enabling = !SpatialAnchorOriginManager.IsOriginAnchorEditMode;
        if (enabling)
        {
            if (GameManager != null && GameManager.IsSceneMeshAlignmentMode)
            {
                GameManager.SetSceneMeshAlignmentMode(false);
            }

            if (EditModeManager.Instance != null && EditModeManager.Instance.IsAnyEditMode)
            {
                EditModeManager.Instance.SetEditMode(false);
                EditModeManager.Instance.SetMatEditMode(false);
                RefreshEditModeLabel();
            }
        }

        SpatialAnchorOriginManager.ToggleOriginAnchorEditMode();
        RefreshOriginAnchorLabel();
    }

    public void SetOriginAnchorEditMode(bool enabled)
    {
        if (SpatialAnchorOriginManager == null)
        {
            return;
        }

        if (enabled)
        {
            if (GameManager != null && GameManager.IsSceneMeshAlignmentMode)
            {
                GameManager.SetSceneMeshAlignmentMode(false);
            }

            if (EditModeManager.Instance != null && EditModeManager.Instance.IsAnyEditMode)
            {
                EditModeManager.Instance.SetEditMode(false);
                EditModeManager.Instance.SetMatEditMode(false);
                RefreshEditModeLabel();
            }
        }

        SpatialAnchorOriginManager.SetOriginAnchorEditMode(enabled);
        RefreshOriginAnchorLabel();
    }

    public async void SaveOriginAnchor()
    {
        if (SpatialAnchorOriginManager == null)
        {
            return;
        }

        if (OriginAnchorSubLabel != null)
        {
            OriginAnchorSubLabel.text = "Origin anchor: saving...";
        }

        bool saved = await SpatialAnchorOriginManager.SaveOriginAnchorAsync(replaceExistingAnchor: true);
        if (OriginAnchorSubLabel != null)
        {
            OriginAnchorSubLabel.text = saved ? "Origin anchor: saved" : "Origin anchor: save failed";
        }

        RefreshOriginAnchorLabel();
    }

    public async void ReloadOriginAnchor()
    {
        if (SpatialAnchorOriginManager == null)
        {
            return;
        }

        if (OriginAnchorSubLabel != null)
        {
            OriginAnchorSubLabel.text = "Origin anchor: loading...";
        }

        bool loaded = await SpatialAnchorOriginManager.ReloadOriginAnchorAsync(forceReload: true);
        if (OriginAnchorSubLabel != null)
        {
            OriginAnchorSubLabel.text = loaded ? "Origin anchor: loaded" : "Origin anchor: load failed";
        }

        RefreshOriginAnchorLabel();
    }

    public async void ClearOriginAnchor()
    {
        if (SpatialAnchorOriginManager == null)
        {
            return;
        }

        if (OriginAnchorSubLabel != null)
        {
            OriginAnchorSubLabel.text = "Origin anchor: clearing...";
        }

        bool cleared = await SpatialAnchorOriginManager.ClearSavedAnchorAsync();
        if (OriginAnchorSubLabel != null)
        {
            OriginAnchorSubLabel.text = cleared ? "Origin anchor: cleared" : "Origin anchor: clear failed";
        }

        RefreshOriginAnchorLabel();
    }

    public async void CutBackgroundMeshUsingSingleCollisionObject()
    {
        if (CutMeshSubLabel != null)
            CutMeshSubLabel.text = "Cutting mesh...";

        if (GameManager == null)
        {
            if (CutMeshSubLabel != null)
            {
                CutMeshSubLabel.text = "GameManager missing";
            }

            Debug.LogError("[SceneEditorMainMenu] GameManager reference is missing.");
            return;
        }

        var collisionBindings = FindObjectsByType<CollisionObjectBinding>(FindObjectsSortMode.None);
        if (collisionBindings == null || collisionBindings.Length == 0 || collisionBindings[0] == null)
        {
            if (CutMeshSubLabel != null)
            {
                CutMeshSubLabel.text = "Collision object not found";
            }

            Debug.LogError("[SceneEditorMainMenu] No CollisionObjectBinding found in scene.");
            return;
        }

        bool success = await GameManager.RebuildCurrentSceneMeshFromCollisionBoxesAsync(new List<Transform> { collisionBindings[0].transform });

        if (CutMeshSubLabel != null)
        {
            CutMeshSubLabel.text = success ? "Mesh cut done" : "Mesh cut failed";
        }
    }

    private void SubscribeGameManager()
    {
        if (GameManager == null)
        {
            GameManager = GameManager.Instance;
        }

        if (GameManager == null)
        {
            return;
        }

        GameManager.SceneMeshBindingMissing -= OnBindingMissing;
        GameManager.SceneMeshBindingMissing += OnBindingMissing;
        GameManager.SceneMeshAlignmentModeChanged -= OnSceneMeshAlignmentModeChanged;
        GameManager.SceneMeshAlignmentModeChanged += OnSceneMeshAlignmentModeChanged;
    }

    private void SubscribeSpatialAnchorManager()
    {
        if (SpatialAnchorOriginManager == null)
        {
            SpatialAnchorOriginManager = FindFirstObjectByType<SpatialAnchorOriginManager>(FindObjectsInactive.Include);
        }

        if (SpatialAnchorOriginManager == null)
        {
            return;
        }

        SpatialAnchorOriginManager.OriginAnchorEditModeChanged -= OnOriginAnchorEditModeChanged;
        SpatialAnchorOriginManager.OriginAnchorEditModeChanged += OnOriginAnchorEditModeChanged;
    }

    private void RefreshEditModeLabel()
    {
        if (EditModeSubLabel == null)
        {
            return;
        }

        bool collisionEditEnabled = EditModeManager.Instance != null && EditModeManager.Instance.IsEditMode;
        bool matEditEnabled = EditModeManager.Instance != null && EditModeManager.Instance.IsMatEditMode;
        string collisionStatus = _isCollisionMeshRebuildInProgress
            ? "Collision edit: rebuilding..."
            : $"Collision edit: {(collisionEditEnabled ? "ON" : "OFF")}";
        string matStatus = $"MAT edit: {(matEditEnabled ? "ON" : "OFF")}";

        if (MatEditModeSubLabel != null)
        {
            EditModeSubLabel.text = collisionStatus;
            MatEditModeSubLabel.text = matStatus;
            return;
        }

        EditModeSubLabel.text = $"{collisionStatus}\n{matStatus}";
    }

    private void RefreshMeshAlignmentLabel()
    {
        if (MeshAlignmentSubLabel == null || GameManager == null)
        {
            return;
        }

        MeshAlignmentSubLabel.text = GameManager.IsSceneMeshAlignmentMode
            ? "Mesh align: ON"
            : "Mesh align: OFF";
    }

    private void RefreshOriginAnchorLabel()
    {
        if (OriginAnchorSubLabel == null)
        {
            return;
        }

        if (SpatialAnchorOriginManager == null)
        {
            OriginAnchorSubLabel.text = "Origin anchor: manager missing";
            return;
        }

        if (SpatialAnchorOriginManager.IsOriginAnchorEditMode)
        {
            OriginAnchorSubLabel.text = "Origin anchor: EDIT";
            return;
        }

        OriginAnchorSubLabel.text = SpatialAnchorOriginManager.HasSavedAnchor
            ? "Origin anchor: saved"
            : "Origin anchor: missing";
    }

    private void OnEditModeChanged(bool _)
    {
        RefreshEditModeLabel();

        if (!EditModeManager.Instance.IsEditMode)
        {
            _ = RebuildBackgroundMeshFromAllCollisionBoxesAsync();
        }
    }

    private void OnMatEditModeChanged(bool _)
    {
        RefreshEditModeLabel();
    }

    private void OnSceneMeshAlignmentModeChanged(bool _)
    {
        RefreshMeshAlignmentLabel();
    }

    private void OnOriginAnchorEditModeChanged(bool _)
    {
        RefreshOriginAnchorLabel();
    }

    public async void BindDefaultMeshToCurrentScene()
    {
        if (GameManager == null)
        {
            if (CutMeshSubLabel != null)
            {
                CutMeshSubLabel.text = "GameManager missing";
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(DefaultMeshIdForBinding))
        {
            if (CutMeshSubLabel != null)
            {
                CutMeshSubLabel.text = "Default mesh ID missing";
            }

            return;
        }

        if (CutMeshSubLabel != null)
        {
            CutMeshSubLabel.text = "Binding mesh...";
        }

        bool success = await GameManager.BindCurrentSceneMeshAsync(DefaultMeshIdForBinding);
        if (CutMeshSubLabel != null)
        {
            CutMeshSubLabel.text = success ? "Mesh bound" : "Mesh bind failed";
        }
    }

    private async System.Threading.Tasks.Task RebuildBackgroundMeshFromAllCollisionBoxesAsync()
    {
        if (GameManager == null)
        {
            return;
        }

        if (_isCollisionMeshRebuildInProgress)
        {
            return;
        }

        _isCollisionMeshRebuildInProgress = true;
        SetCollisionEditButtonInteractable(false);
        RefreshEditModeLabel();

        try
        {
            if (CutMeshSubLabel != null)
            {
                CutMeshSubLabel.text = "Rebuilding mesh...";
            }

            bool success = await GameManager.RebuildCurrentSceneMeshFromCollisionBoxesAsync(GameManager.GetActiveCollisionBoxTransforms());
            if (CutMeshSubLabel != null)
            {
                CutMeshSubLabel.text = success ? "Mesh rebuild done" : "Mesh rebuild failed";
            }
        }
        finally
        {
            _isCollisionMeshRebuildInProgress = false;
            SetCollisionEditButtonInteractable(true);
            RefreshEditModeLabel();
        }
    }

    private void SetCollisionEditButtonInteractable(bool interactable)
    {
        Toggle collisionToggle = GetCollisionEditToggle();
        if (collisionToggle != null)
        {
            collisionToggle.interactable = interactable;
        }
    }

    private Toggle GetCollisionEditToggle()
    {
        if (_collisionEditToggle == null && EditModeSubLabel != null)
        {
            _collisionEditToggle = EditModeSubLabel.GetComponentInParent<Toggle>(true);
        }

        return _collisionEditToggle;
    }

    private void OnBindingMissing(string sceneId, List<MeshDownloadManager.AvailableMeshInfo> availableMeshes)
    {
        if (CutMeshSubLabel != null)
        {
            CutMeshSubLabel.text = "Mesh binding missing";
        }

        Debug.LogWarning($"[SceneEditorMainMenu] No mesh is bound to scene '{sceneId}'. Available meshes: {availableMeshes.Count}");
        for (int i = 0; i < availableMeshes.Count; i++)
        {
            Debug.LogWarning($"[SceneEditorMainMenu] Available mesh: {availableMeshes[i].MeshId} ({availableMeshes[i].Label})");
        }
    }
}
