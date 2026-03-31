using System.Globalization;
using UnityEngine;

public sealed class PerformanceOverlay : MonoBehaviour
{
    private const float SampleIntervalSeconds = 0.25f;
    private const int MaxFrameTimings = 4;
    private static readonly Vector3 OverlayLocalPosition = new Vector3(-0.18f, -0.12f, 0.6f);
    private static readonly Vector3 BackgroundLocalScale = new Vector3(0.22f, 0.12f, 1f);

    private readonly FrameTiming[] _frameTimings = new FrameTiming[MaxFrameTimings];

    private float _sampleElapsed;
    private int _sampleFrames;
    private float _sampleFrameTimeSum;
    private string _cachedText = "Perf: collecting...";
    private Transform _anchorTransform;
    private GameObject _displayRoot;
    private TextMesh _textMesh;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindFirstObjectByType<PerformanceOverlay>() != null)
        {
            return;
        }

        var root = new GameObject("PerformanceOverlay");
        DontDestroyOnLoad(root);
        root.AddComponent<PerformanceOverlay>();
    }

    private void Update()
    {
        EnsureDisplay();

        float deltaTime = Time.unscaledDeltaTime;
        if (deltaTime <= 0f)
        {
            return;
        }

        _sampleElapsed += deltaTime;
        _sampleFrames++;
        _sampleFrameTimeSum += deltaTime;

        if (_sampleElapsed < SampleIntervalSeconds)
        {
            return;
        }

        float averageFrameTimeMs = (_sampleFrameTimeSum / Mathf.Max(1, _sampleFrames)) * 1000f;
        float averageFps = _sampleFrames / Mathf.Max(_sampleElapsed, 0.0001f);

        float cpuFrameTimeMs = -1f;
        float gpuFrameTimeMs = -1f;

        FrameTimingManager.CaptureFrameTimings();
        uint timingCount = FrameTimingManager.GetLatestTimings((uint)_frameTimings.Length, _frameTimings);
        if (timingCount > 0)
        {
            FrameTiming timing = _frameTimings[timingCount - 1];
            cpuFrameTimeMs = (float)timing.cpuFrameTime;
            gpuFrameTimeMs = (float)timing.gpuFrameTime;
        }

        _cachedText =
            "FPS " + Format(averageFps) +
            "\nFrame " + Format(averageFrameTimeMs) + " ms" +
            "\nCPU " + FormatOrDash(cpuFrameTimeMs) + " ms" +
            "\nGPU " + FormatOrDash(gpuFrameTimeMs) + " ms";

        if (_textMesh != null)
        {
            _textMesh.text = _cachedText;
        }

        _sampleElapsed = 0f;
        _sampleFrames = 0;
        _sampleFrameTimeSum = 0f;
    }

    private void EnsureDisplay()
    {
        if (_anchorTransform == null)
        {
            _anchorTransform = ResolveAnchorTransform();
        }

        if (_anchorTransform == null)
        {
            return;
        }

        if (_displayRoot == null)
        {
            CreateDisplay();
        }

        if (_displayRoot != null && _displayRoot.transform.parent != _anchorTransform)
        {
            _displayRoot.transform.SetParent(_anchorTransform, false);
            _displayRoot.transform.localPosition = OverlayLocalPosition;
            _displayRoot.transform.localRotation = Quaternion.identity;
        }
    }

    private void CreateDisplay()
    {
        _displayRoot = new GameObject("PerformanceOverlayPanel");
        _displayRoot.layer = 0;

        var background = GameObject.CreatePrimitive(PrimitiveType.Quad);
        background.name = "Background";
        background.transform.SetParent(_displayRoot.transform, false);
        background.transform.localPosition = new Vector3(0.1f, -0.05f, 0f);
        background.transform.localRotation = Quaternion.identity;
        background.transform.localScale = BackgroundLocalScale;
        background.layer = 0;

        var backgroundCollider = background.GetComponent<Collider>();
        if (backgroundCollider != null)
        {
            Destroy(backgroundCollider);
        }

        var backgroundRenderer = background.GetComponent<MeshRenderer>();
        if (backgroundRenderer != null)
        {
            Shader shader = Shader.Find("Unlit/Color");
            if (shader != null)
            {
                var material = new Material(shader);
                material.color = new Color(0f, 0f, 0f, 0.72f);
                backgroundRenderer.sharedMaterial = material;
            }
        }

        var textObject = new GameObject("Text");
        textObject.transform.SetParent(_displayRoot.transform, false);
        textObject.transform.localPosition = new Vector3(0f, 0f, -0.001f);
        textObject.layer = 0;

        _textMesh = textObject.AddComponent<TextMesh>();
        _textMesh.text = _cachedText;
        _textMesh.fontSize = 72;
        _textMesh.characterSize = 0.0035f;
        _textMesh.anchor = TextAnchor.UpperLeft;
        _textMesh.alignment = TextAlignment.Left;
        _textMesh.color = Color.white;

        var meshRenderer = textObject.GetComponent<MeshRenderer>();
        if (meshRenderer != null)
        {
            meshRenderer.sortingOrder = 5000;
        }
    }

    private static string Format(float value)
    {
        return value.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string FormatOrDash(float value)
    {
        return value >= 0f ? Format(value) : "-";
    }

    private static Transform ResolveAnchorTransform()
    {
        if (Camera.main != null)
        {
            return Camera.main.transform;
        }

        var camera = FindFirstObjectByType<Camera>();
        return camera != null ? camera.transform : null;
    }
}
