using UnityEngine;
using UnityEngine.Android;
using Meta.XR.EnvironmentDepth;

public class EnvDepthProbe : MonoBehaviour
{
    const string ScenePerm = "com.oculus.permission.USE_SCENE";
    static readonly int DepthTexID = Shader.PropertyToID("_EnvironmentDepthTexture");
    EnvironmentDepthManager mgr;

    void Start()
    {
        Debug.Log("[EnvDepthProbe] Awake called");
        mgr = Object.FindFirstObjectByType<EnvironmentDepthManager>();
        Debug.Log($"[EnvDepthProbe] Manager found: {mgr != null}");
        Invoke(nameof(DelayedLog), 3f);
    }

    void DelayedLog()
    {
        Log("Start-delayed");
    }

    void Update()
    {
        if (Time.frameCount % 90 == 0) Log("tick");
    }

    void Log(string tag)
    {
        bool supported = EnvironmentDepthManager.IsSupported;
        bool perm = Permission.HasUserAuthorizedPermission(ScenePerm);
        bool avail = mgr != null && mgr.IsDepthAvailable;
        var tex = Shader.GetGlobalTexture(DepthTexID);
        Debug.Log($"[EnvDepthProbe/{tag}] IsSupported={supported} " +
                  $"Permission={perm} IsDepthAvailable={avail} " +
                  $"Texture={(tex != null ? "SET" : "NULL")}");
    }
}