using UnityEngine;
using UnityEngine.XR.Management;

public class DisableAROcclusion : MonoBehaviour
{
    void Awake()
    {
        var loader = XRGeneralSettings.Instance?.Manager?.activeLoader;
        Debug.Log($"[DisableAROcclusion] XR Loader: {(loader != null ? loader.name : "null")}");
    }
}