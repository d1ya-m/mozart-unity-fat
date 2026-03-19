using Oculus.Interaction.Samples;
using TMPro;
using UnityEngine;
using static Meta.XR.MRUtilityKit.FindSpawnPositions;
using Arcor2.ClientSdk.Communication.OpenApi.Models;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

public class AddGridMenu : MonoBehaviour
{
    [SerializeField]
    private DropDownGroup RowsDropdown, ColsDropdown, MovementDropdown;
    public void AddButtonOnClick()
    {
        
        string movementText = MovementDropdown.SelectedToggle.GetComponentInChildren<TMP_Text>().text;
        Parameter rows = new(name: "rows", type: "integer", value: RowsDropdown.SelectedToggle.GetComponentInChildren<TMP_Text>().text);
        Parameter cols = new(name: "cols", type: "integer", value: ColsDropdown.SelectedToggle.GetComponentInChildren<TMP_Text>().text);
        Parameter movement = new(name: "move_pattern", type: "string", value: JsonConvert.SerializeObject(GetMovementTypeString(movementText)));
        GameManager.Instance.AddNewObjectToScene(GameManager.ObjectType.MatGrid, GameManager.Instance.GetPositionInFrontOfCamera(), Quaternion.identity, new List<Parameter> { rows, cols, movement});
        GameManager.Instance.SceneEditorMainMenu.gameObject.SetActive(true);
        gameObject.SetActive(false);
    }

    private string GetMovementTypeString(string text)
    {
        switch (text)
        {
            case "Random":
                return "RANDOM";
            case "Wawe":
                return "WAWE";
            default:
                return "STILL";
        }
    }

    public void CancelButtonOnClick()
    {
        gameObject.SetActive(false);
        GameManager.Instance.SceneEditorMainMenu.gameObject.SetActive(true);
    }
}
