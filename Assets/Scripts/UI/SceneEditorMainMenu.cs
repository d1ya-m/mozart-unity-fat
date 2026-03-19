using Arcor2.ClientSdk.ClientServices;
using Arcor2.ClientSdk.ClientServices.Enums;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class SceneEditorMainMenu : MonoBehaviour
{
    public CommunicationManager CommunicationManager;
    public GameManager GameManager;
    public string DefaultMeshIdForBinding;
    public TMP_Text CloseSceneSubLabel, SaveSceneSubLabel, EditModeSubLabel, MatEditModeSubLabel, CutMeshSubLabel, MeshAlignmentSubLabel;

    private void Start()
    {
        RefreshEditModeLabel();
        SubscribeGameManager();
        RefreshMeshAlignmentLabel();
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
        if (GameManager != null && GameManager.IsSceneMeshAlignmentMode)
        {
            GameManager.SetSceneMeshAlignmentMode(false);
        }

        EditModeManager.Instance.ToggleEditMode();
        RefreshEditModeLabel();
    }

    public void SetEditMode(bool enabled)
    {
        if (enabled && GameManager != null && GameManager.IsSceneMeshAlignmentMode)
        {
            GameManager.SetSceneMeshAlignmentMode(false);
        }

        EditModeManager.Instance.SetEditMode(enabled);
        RefreshEditModeLabel();
    }

    public void ToggleMatEditMode()
    {
        if (GameManager != null && GameManager.IsSceneMeshAlignmentMode)
        {
            GameManager.SetSceneMeshAlignmentMode(false);
        }

        EditModeManager.Instance.ToggleMatEditMode();
        RefreshEditModeLabel();
    }

    public void SetMatEditMode(bool enabled)
    {
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

    private void RefreshEditModeLabel()
    {
        if (EditModeSubLabel == null)
        {
            return;
        }

        bool collisionEditEnabled = EditModeManager.Instance != null && EditModeManager.Instance.IsEditMode;
        bool matEditEnabled = EditModeManager.Instance != null && EditModeManager.Instance.IsMatEditMode;

        if (MatEditModeSubLabel != null)
        {
            EditModeSubLabel.text = collisionEditEnabled
                ? "Collision edit: ON"
                : "Collision edit: OFF";
            MatEditModeSubLabel.text = matEditEnabled
                ? "MAT edit: ON"
                : "MAT edit: OFF";
            return;
        }

        EditModeSubLabel.text =
            $"Collision edit: {(collisionEditEnabled ? "ON" : "OFF")}\nMAT edit: {(matEditEnabled ? "ON" : "OFF")}";
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

    private void OnEditModeChanged(bool _)
    {
        RefreshEditModeLabel();

        if (!EditModeManager.Instance.IsEditMode)
        {
            RebuildBackgroundMeshFromAllCollisionBoxesAsync();
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
