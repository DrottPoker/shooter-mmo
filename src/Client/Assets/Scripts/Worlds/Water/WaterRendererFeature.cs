using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Mmorpg.Client.Worlds
{
    // Draws the water surfaces before the other transparent objects, then copies the camera's
    // color with the water in it to _WaterSceneColorTexture, so what draws after the water, such
    // as a waterfall, shows the water behind it instead of only the ground below. Every renderer
    // of the project carries it: the water shader's pass has a light mode that only this
    // feature draws, so without it no water surface shows.
    public sealed class WaterRendererFeature : ScriptableRendererFeature
    {
        public const string SurfaceLightMode = "MmorpgWaterSurface";
        public const string SceneColorTextureName = "_WaterSceneColorTexture";

        private WaterSurfacesPass pass;

        public override void Create()
        {
            pass = new WaterSurfacesPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(pass);
        }

        private sealed class WaterSurfacesPass : ScriptableRenderPass
        {
            private static readonly ShaderTagId SurfaceTag = new ShaderTagId(SurfaceLightMode);
            private static readonly int SceneColorId = Shader.PropertyToID(SceneColorTextureName);

            public WaterSurfacesPass()
            {
                profilingSampler = new ProfilingSampler("Water Surfaces");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var rendering = frameData.Get<UniversalRenderingData>();
                var camera = frameData.Get<UniversalCameraData>();
                var lights = frameData.Get<UniversalLightData>();

                // Back to front, as transparent objects draw, so the stencil keeps the nearest
                // water where two bodies overlap.
                var drawing = RenderingUtils.CreateDrawingSettings(
                    SurfaceTag,
                    rendering,
                    camera,
                    lights,
                    SortingCriteria.CommonTransparent);
                var filtering = new FilteringSettings(RenderQueueRange.transparent);
                var surfaces = renderGraph.CreateRendererList(
                    new RendererListParams(rendering.cullResults, drawing, filtering));

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Water Surfaces", out var data, profilingSampler))
                {
                    data.Surfaces = surfaces;
                    builder.UseRendererList(surfaces);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);

                    // The water reads the camera's depth and opaque textures and the sun's shadows.
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderFunc(static (PassData passData, RasterGraphContext context) =>
                        context.cmd.DrawRendererList(passData.Surfaces));
                }

                var description = renderGraph.GetTextureDesc(resources.activeColorTexture);
                description.name = SceneColorTextureName;
                description.msaaSamples = MSAASamples.None;
                description.clearBuffer = false;
                var sceneColor = renderGraph.CreateTexture(description);
                using (var builder = renderGraph.AddBlitPass(
                    resources.activeColorTexture,
                    sceneColor,
                    Vector2.one,
                    Vector2.zero,
                    returnBuilder: true,
                    passName: "Copy Water Scene Color"))
                {
                    builder.SetGlobalTextureAfterPass(sceneColor, SceneColorId);
                }
            }

            private sealed class PassData
            {
                public RendererListHandle Surfaces;
            }
        }
    }
}
