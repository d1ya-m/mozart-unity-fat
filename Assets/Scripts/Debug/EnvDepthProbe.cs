using UnityEngine;
using UnityEngine.Android;
using Meta.XR.EnvironmentDepth;

// Logs the state of the Environment Depth pipeline so we can see, on-device via
// adb logcat, exactly which link is missing if dynamic occlusion isn't working:
//   IsSupported  - device/runtime supports env depth
//   Permission   - USE_SCENE permission granted
//   IsDepthAvailable - the manager is producing depth
//   Texture      - the global _EnvironmentDepthTexture is actually set
public class EnvDepthProbe : MonoBehaviour
{
    const string ScenePerm = "com.oculus.permission.USE_SCENE";
    static readonly int DepthTexID = Shader.PropertyToID("_EnvironmentDepthTexture");
    EnvironmentDepthManager _mgr;

    void Start()
    {
        _mgr = Object.FindFirstObjectByType<EnvironmentDepthManager>();
        Debug.Log($"[EnvDepthProbe] Manager found: {_mgr != null}");
        // Request the scene permission if it isn't granted yet.
        if (!Permission.HasUserAuthorizedPermission(ScenePerm))
        {
            Debug.Log("[EnvDepthProbe] Requesting USE_SCENE permission...");
            Permission.RequestUserPermission(ScenePerm);
        }
    }

    void Update()
    {
        if (Time.frameCount % 90 == 0) Log("tick");
    }

    void Log(string tag)
    {
        bool supported = EnvironmentDepthManager.IsSupported;
        bool perm = Permission.HasUserAuthorizedPermission(ScenePerm);
        bool avail = _mgr != null && _mgr.IsDepthAvailable;
        var tex = Shader.GetGlobalTexture(DepthTexID);
        Debug.Log($"[EnvDepthProbe/{tag}] IsSupported={supported} " +
                  $"Permission={perm} IsDepthAvailable={avail} " +
                  $"Texture={(tex != null ? "SET" : "NULL")}");
    }
}
