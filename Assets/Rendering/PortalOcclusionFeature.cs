using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class PortalOcclusionFeature : ScriptableRendererFeature
{
    static readonly int k_RawId = Shader.PropertyToID("_PortalOccRaw");

    class OccPrepass : ScriptableRenderPass
    {
        static readonly ShaderTagId k_Tag = new ShaderTagId("PortalOccPrepass");
        class PassData { public RendererListHandle list; }

        public OccPrepass() { renderPassEvent = RenderPassEvent.BeforeRenderingOpaques; }

        public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
        {
            var cam    = frame.Get<UniversalCameraData>();
            var render = frame.Get<UniversalRenderingData>();
            var light  = frame.Get<UniversalLightData>();

            var desc             = cam.cameraTargetDescriptor;
            desc.colorFormat     = RenderTextureFormat.ARGBHalf;
            desc.depthBufferBits = 0;
            desc.msaaSamples     = 1;

            TextureHandle occRaw = UniversalRenderer.CreateRenderGraphTexture(
                rg, desc, "_PortalOccRaw", false, FilterMode.Bilinear);

            var draw   = RenderingUtils.CreateDrawingSettings(k_Tag, render, cam, light,
                             cam.defaultOpaqueSortFlags);
            var filter = new FilteringSettings(RenderQueueRange.opaque);
            var list   = new RendererListParams(render.cullResults, draw, filter);

            using (var b = rg.AddRasterRenderPass<PassData>("Portal Occ Prepass", out var data))
            {
                data.list = rg.CreateRendererList(list);
                b.UseRendererList(data.list);
                b.SetRenderAttachment(occRaw, 0);
                b.AllowPassCulling(false);
                b.SetGlobalTextureAfterPass(occRaw, k_RawId);
                b.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                {
                    ctx.cmd.ClearRenderTarget(RTClearFlags.Color, new Color(1, 0, 0, 0), 1, 0);
                    ctx.cmd.DrawRendererList(d.list);
                });
            }
        }
    }

    OccPrepass m_Pass;
    public override void Create() { m_Pass = new OccPrepass(); }
    public override void AddRenderPasses(ScriptableRenderer r, ref RenderingData data) => r.EnqueuePass(m_Pass);
}
