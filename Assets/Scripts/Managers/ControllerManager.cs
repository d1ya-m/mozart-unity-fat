using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class ControllerManager : MonoBehaviour
{
    public CalibrationManager calibrationManager;
    public Transform RightController;

    public Camera eventCamera; // kamera použitá pro raycasting (napø. hlavní kamera nebo "eye")
    public float rayDistance = 10f;
    public GraphicRaycaster graphicRaycaster; // z Canvasu
    public EventSystem eventSystem; // musí být ve scénì

    void Update()
    {
        if (OVRInput.GetUp(OVRInput.Button.One))
        {
            calibrationManager.Calibrate();
        }
        //ShootRay();
    }

    void ShootRay()
    {
        PointerEventData pointerEventData = new PointerEventData(eventSystem)
        {
            position = eventCamera.WorldToScreenPoint(RightController.position + RightController.forward * rayDistance)
        };

        List<RaycastResult> results = new List<RaycastResult>();
        graphicRaycaster.Raycast(pointerEventData, results);

        foreach (RaycastResult result in results)
        {
            Debug.Log("UI prvek zasažen: " + result.gameObject.name);

            // Simulace kliknutí:
            ExecuteEvents.Execute(result.gameObject, pointerEventData, ExecuteEvents.pointerClickHandler);
        }

        Debug.DrawRay(RightController.position, RightController.forward * rayDistance, Color.green);
    }
}
