using Arcor2.ClientSdk.ClientServices;
using Arcor2.ClientSdk.Communication;
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Arcor2.ClientSdk.ClientServices.Enums {

    public class CommunicationManager : Singleton<CommunicationManager> {
    
        public Arcor2.ClientSdk.ClientServices.Arcor2Session Arcor2Session = new Arcor2Session(
        new Arcor2SessionSettings
        {
            SynchronizationAction = action => {
                MainThreadDispatcher.Enqueue(action);
            }
        });
        public System.Uri ServerUri = new System.Uri("ws://butcluster.ddns.net:6789");

        public event Action ConnectedToServer;

        private void Awake()
        {
            Arcor2Session.ConnectionError += OnConnectionError;
            Arcor2Session.ConnectionOpened += OnConnectionOpened;
            Arcor2Session.ConnectionClosed += OnConnectionClosed;

            ConnectToServer();
            /*
            Arcor2Session.NavigationStateChanged += (_, args) =>
            {
                Debug.LogError(args.State);
                switch (args.State)
                {
                    case NavigationState.Scene:
                        ShowScene(args.Id);
                        break;
                    case NavigationState.Project:
                    case NavigationState.Package:
                        Debug.Log("Not supported");
                        break;
                    case NavigationState.MenuListOfScenes:
                    case NavigationState.MenuListOfProjects:
                    case NavigationState.MenuListOfPackages:
                        //ShowMenu();
                        break;
                    default:
                        // Ignore scene and project closed events
                        break;
                }
            };*/
        }

        private async void ConnectToServer()
        {
            await Arcor2Session.ConnectAsync(ServerUri);
            await Arcor2Session.InitializeAsync();
            await Arcor2Session.RegisterAndSubscribeAsync("TEST");
            ConnectedToServer.Invoke();
        }

        private void OnConnectionError(object sender, Exception e)
        {
            Debug.LogError(e.ToString());
        }

        private void OnConnectionOpened(object sender, EventArgs e)
        {
            Debug.LogError("Connected to server");
            
        }

        private void OnConnectionClosed(object sender, EventArgs args)
        {
            Debug.Log($"Disconnected from server: {args}");
        }

        private async void OnDestroy()
        {
            await Arcor2Session.CloseAsync();
        }

        private void ShowScene(string id)
        {
            Debug.LogError(Arcor2Session.ObjectTypes);
        }

    }
}