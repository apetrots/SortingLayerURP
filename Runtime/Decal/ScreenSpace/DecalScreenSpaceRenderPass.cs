using System.Collections.Generic;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    internal class DecalDrawScreenSpaceSystem : DecalDrawSystem
    {
        public DecalDrawScreenSpaceSystem(DecalEntityManager entityManager) : base("DecalDrawScreenSpaceSystem.Execute", entityManager) { }
        protected override int GetPassIndex(DecalCachedChunk decalCachedChunk) => decalCachedChunk.passIndexScreenSpace;
    }

    internal class DecalScreenSpaceRenderPass : ScriptableRenderPass
    {
        private FilteringSettings m_FilteringSettings;
        private ProfilingSampler m_ProfilingSampler;
        private List<ShaderTagId> m_ShaderTagIdList;
        private DecalDrawScreenSpaceSystem m_DrawSystem;
        private DecalScreenSpaceSettings m_Settings;
        private bool m_DecalLayers;
        private PassData m_PassData;

        public DecalScreenSpaceRenderPass(DecalScreenSpaceSettings settings, DecalDrawScreenSpaceSystem drawSystem, bool decalLayers)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingSkybox;

            var scriptableRenderPassInput = ScriptableRenderPassInput.Depth; // Require depth
            ConfigureInput(scriptableRenderPassInput);

            m_DrawSystem = drawSystem;
            m_Settings = settings;
            m_ProfilingSampler = new ProfilingSampler("Decal Screen Space Render");
            m_FilteringSettings = new FilteringSettings(RenderQueueRange.opaque, -1);
            m_DecalLayers = decalLayers;

            m_ShaderTagIdList = new List<ShaderTagId>();

            if (m_DrawSystem == null)
                m_ShaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.DecalScreenSpaceProjector));
            else
                m_ShaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.DecalScreenSpaceMesh));

            m_PassData = new PassData();
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            InitPassData(ref m_PassData);
            RenderingUtils.SetScaleBiasRt(renderingData.commandBuffer, in renderingData);
            ExecutePass(context, m_PassData, ref renderingData, renderingData.commandBuffer);
        }

        private class PassData
        {
            internal FilteringSettings filteringSettings;
            internal ProfilingSampler profilingSampler;
            internal List<ShaderTagId> shaderTagIdList;
            internal DecalDrawScreenSpaceSystem drawSystem;
            internal DecalScreenSpaceSettings settings;
            internal bool decalLayers;
            internal bool isGLDevice;
            internal TextureHandle colorTarget;

            internal RenderingData renderingData;
        }

        void InitPassData(ref PassData passData)
        {
            passData.filteringSettings = m_FilteringSettings;
            passData.profilingSampler = m_ProfilingSampler;
            passData.shaderTagIdList = m_ShaderTagIdList;
            passData.drawSystem = m_DrawSystem;
            passData.settings = m_Settings;
            passData.decalLayers = m_DecalLayers;
            passData.isGLDevice = IsGLDevice();
        }

        private static void ExecutePass(ScriptableRenderContext context, PassData passData, ref RenderingData renderingData, CommandBuffer cmd)
        {
            SortingCriteria sortingCriteria = SortingCriteria.CommonTransparent;
            DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(passData.shaderTagIdList, ref renderingData, sortingCriteria);

            using (new ProfilingScope(cmd, passData.profilingSampler))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                NormalReconstruction.SetupProperties(cmd, renderingData.cameraData);

                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.DecalNormalBlendLow, passData.settings.normalBlend == DecalNormalBlend.Low);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.DecalNormalBlendMedium, passData.settings.normalBlend == DecalNormalBlend.Medium);
                CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.DecalNormalBlendHigh, passData.settings.normalBlend == DecalNormalBlend.High);

                if (!passData.isGLDevice)
                    CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.DecalLayers, passData.decalLayers);

                passData.drawSystem?.Execute(cmd);

                var param = new RendererListParams(renderingData.cullResults, drawingSettings, passData.filteringSettings);
                var rl = context.CreateRendererList(ref param);
                cmd.DrawRendererList(rl);
            }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, FrameResources frameResources, ref RenderingData renderingData)
        {
            UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;

            TextureHandle cameraDepthTexture = frameResources.GetTexture(UniversalResource.CameraDepthTexture);

            RenderGraphUtils.SetGlobalTexture(renderGraph, Shader.PropertyToID("_CameraDepthTexture"), cameraDepthTexture);

            using (var builder = renderGraph.AddRenderPass<PassData>("Decal Screen Space Pass", out var passData, m_ProfilingSampler))
            {
                TextureHandle cameraColor = frameResources.GetTexture(UniversalResource.CameraColor);

                InitPassData(ref passData);
                passData.colorTarget = cameraColor;
                passData.renderingData = renderingData;

                builder.UseColorBuffer(renderer.activeColorTexture, 0);
                builder.UseDepthBuffer(renderer.activeDepthTexture, DepthAccess.Read);

                if (cameraDepthTexture.IsValid())
                    builder.ReadTexture(cameraDepthTexture);

                builder.SetRenderFunc((PassData data, RenderGraphContext rgContext) =>
                {
                    RenderingUtils.SetScaleBiasRt(rgContext.cmd, in data.renderingData, data.colorTarget);
                    ExecutePass(rgContext.renderContext, data, ref data.renderingData, rgContext.cmd);
                });
            }
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            if (cmd == null)
            {
                throw new System.ArgumentNullException("cmd");
            }

            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.DecalNormalBlendLow, false);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.DecalNormalBlendMedium, false);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.DecalNormalBlendHigh, false);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.DecalLayers, false);

        }

        bool IsGLDevice()
        {
            return
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2 ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore;
        }
    }
}
