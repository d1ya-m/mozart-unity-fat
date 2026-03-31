using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class AlwaysVisibleContentRenderer : MonoBehaviour
{
    private const string OverlayShaderName = "Custom/AlwaysVisibleContentUnlit";
    private const string OverlayTemplateResourcePath = "AlwaysVisibleContent";
    private const int OverlayRenderQueue = 5000;
    private const int OverlaySortingOrder = 1000;

    private readonly Dictionary<Renderer, Material[]> overlayMaterials = new Dictionary<Renderer, Material[]>();
    private Shader overlayShader;
    private Material overlayTemplate;

    private void OnEnable()
    {
        Refresh();
    }

    private void OnTransformChildrenChanged()
    {
        if (!isActiveAndEnabled)
        {
            return;
        }

        Refresh();
    }

    private void OnDestroy()
    {
        foreach (var materialSet in overlayMaterials.Values)
        {
            DestroyMaterialSet(materialSet);
        }

        overlayMaterials.Clear();
    }

    public void Refresh()
    {
        overlayTemplate ??= Resources.Load<Material>(OverlayTemplateResourcePath);
        overlayShader ??= overlayTemplate != null ? overlayTemplate.shader : Shader.Find(OverlayShaderName);
        if (overlayShader == null)
        {
            Debug.LogWarning("AlwaysVisibleContentRenderer could not find overlay shader/material template.");
            return;
        }

        var activeRenderers = new HashSet<Renderer>();
        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
            {
                continue;
            }

            activeRenderers.Add(renderer);
            if (!overlayMaterials.TryGetValue(renderer, out var materialSet) || materialSet == null || materialSet.Length == 0)
            {
                materialSet = CreateOverlayMaterialSet(renderer.sharedMaterials);
                overlayMaterials[renderer] = materialSet;
            }

            if (materialSet != null)
            {
                renderer.sharedMaterials = materialSet;
            }

            renderer.sortingOrder = OverlaySortingOrder;
        }

        var removedRenderers = new List<Renderer>();
        foreach (var entry in overlayMaterials)
        {
            if (entry.Key == null || !activeRenderers.Contains(entry.Key))
            {
                DestroyMaterialSet(entry.Value);
                removedRenderers.Add(entry.Key);
            }
        }

        foreach (var renderer in removedRenderers)
        {
            overlayMaterials.Remove(renderer);
        }
    }

    private Material[] CreateOverlayMaterialSet(Material[] originalMaterials)
    {
        if (originalMaterials == null)
        {
            return null;
        }

        var overlaySet = new Material[originalMaterials.Length];
        for (int i = 0; i < originalMaterials.Length; i++)
        {
            var source = originalMaterials[i];
            if (source == null)
            {
                continue;
            }

            var overlayMaterial = overlayTemplate != null
                ? new Material(overlayTemplate)
                : new Material(overlayShader);
            overlayMaterial.renderQueue = OverlayRenderQueue;
            CopyProperties(source, overlayMaterial);
            overlaySet[i] = overlayMaterial;
        }

        return overlaySet;
    }

    private static void CopyProperties(Material source, Material destination)
    {
        Texture sourceTexture = null;
        if (source.HasProperty("_BaseMap"))
        {
            sourceTexture = source.GetTexture("_BaseMap");
        }
        else if (source.HasProperty("_MainTex"))
        {
            sourceTexture = source.GetTexture("_MainTex");
        }

        if (sourceTexture != null && destination.HasProperty("_BaseMap"))
        {
            destination.SetTexture("_BaseMap", sourceTexture);
        }

        Color sourceColor = Color.white;
        if (source.HasProperty("_BaseColor"))
        {
            sourceColor = source.GetColor("_BaseColor");
        }
        else if (source.HasProperty("_Color"))
        {
            sourceColor = source.GetColor("_Color");
        }

        if (destination.HasProperty("_BaseColor"))
        {
            destination.SetColor("_BaseColor", sourceColor);
        }
    }

    private static void DestroyMaterialSet(Material[] materialSet)
    {
        if (materialSet == null)
        {
            return;
        }

        for (int i = 0; i < materialSet.Length; i++)
        {
            if (materialSet[i] != null)
            {
                Destroy(materialSet[i]);
            }
        }
    }
}
