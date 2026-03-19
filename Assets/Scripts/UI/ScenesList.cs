using Arcor2.ClientSdk.ClientServices.Enums;
using System.Collections.Specialized;
using UnityEngine;
using UnityEngine.InputSystem.HID;

public class ScenesList : MonoBehaviour
{
    public GameObject ButtonPrefab;
    public GameObject Content;
    public CommunicationManager CommunicationManager;
    public TMPro.TMP_Text TitleLabel;

    private void Awake()
    {
    }

    private void Start()
    {
        CommunicationManager.Arcor2Session.ConnectionOpened += ConnectionOpened;
        
        ((INotifyCollectionChanged) CommunicationManager.Arcor2Session.Scenes).CollectionChanged += (sender, args) => UpdateScenesList();

        UpdateScenesList();
    }

  

    private void UpdateScenesList()
    {
        foreach (Transform t in Content.transform)
        {
            if (t.gameObject.tag != "Persistent")
                GameObject.Destroy(t.gameObject);
        }

        foreach (var scene in CommunicationManager.Arcor2Session.Scenes)
        {
            TextTileButton button = Instantiate(ButtonPrefab, Content.transform).GetComponent<TextTileButton>();
            button.Label.text = scene.Data.Name;
            button.Button.onValueChanged.AddListener((enabled) => CommunicationManager.Arcor2Session.Scenes[scene.Id].OpenAsync());
        }

    }

    private void ConnectionOpened(object sender, System.EventArgs e)
    {
        TitleLabel.text = $"Scenes List. Connected to {CommunicationManager.ServerUri}";        
    }

    
 
}
