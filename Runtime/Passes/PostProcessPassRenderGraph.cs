using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using System;

namespace UnityEngine.Rendering.Universal
{
    internal partial class PostProcessPass : ScriptableRenderPass
    {
        #region StopNaNs
        private class StopNaNsPassData
        {
            internal TextureHandle stopNaNTarget;
            internal TextureHandle sourceTexture;
            internal Material stopNaN;
        }

        public void RenderStopNaN(RenderGraph renderGraph, in TextureHandle activeCameraColor, out TextureHandle stopNaNTarget, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;

            var cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            var desc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor,
                cameraTargetDescriptor.width,
                cameraTargetDescriptor.height,
                cameraTargetDescriptor.graphicsFormat,
                DepthBits.None);

            stopNaNTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_StopNaNsTarget", true, FilterMode.Bilinear);

            using (var builder = renderGraph.AddRenderPass<StopNaNsPassData>("Stop NaNs", out var passData, ProfilingSampler.Get(URPProfileId.RG_StopNaNs)))
            {
                passData.stopNaNTarget = builder.UseColorBuffer(stopNaNTarget, 0);
                passData.sourceTexture = builder.ReadTexture(activeCameraColor);
                passData.stopNaN = m_Materials.stopNaN;

                builder.SetRenderFunc((StopNaNsPassData data, RenderGraphContext context) =>
                {
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, data.stopNaN, 0);
                });

                return;
            }
        }
        #endregion

        #region SMAA
        private class SMAASetupPassData
        {
            internal Vector4 metrics;
            internal Texture2D areaTexture;
            internal Texture2D searchTexture;
            internal float stencilRef;
            internal float stencilMask;
            internal AntialiasingQuality antialiasingQuality;
            internal Material material;
        }

        private class SMAAPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal TextureHandle depthStencilTexture;
            internal TextureHandle blendTexture;
            internal CameraData cameraData;
            internal Material material;
        }

        public void RenderSMAA(RenderGraph renderGraph, in TextureHandle source, out TextureHandle SMAATarget, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;

            var desc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_Descriptor.graphicsFormat,
                DepthBits.None);
            SMAATarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_SMAATarget", true, FilterMode.Bilinear);

            var edgeTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_SMAAEdgeFormat,
                DepthBits.None);
            var edgeTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, edgeTextureDesc, "_EdgeStencilTexture", true, FilterMode.Bilinear);

            var edgeTextureStencilDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                GraphicsFormat.None,
                DepthBits.Depth24);
            var edgeTextureStencil = UniversalRenderer.CreateRenderGraphTexture(renderGraph, edgeTextureStencilDesc, "_EdgeTexture", true, FilterMode.Bilinear);

            var blendTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                GraphicsFormat.R8G8B8A8_UNorm,
                DepthBits.None);
            var blendTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, blendTextureDesc, "_BlendTexture", true, FilterMode.Point);

            // Anti-aliasing
            var material = m_Materials.subpixelMorphologicalAntialiasing;

            using (var builder = renderGraph.AddRenderPass<SMAASetupPassData>("SMAA Material Setup", out var passData, ProfilingSampler.Get(URPProfileId.RG_SMAAMaterialSetup)))
            {
                const int kStencilBit = 64;
                // TODO RENDERGRAPH: handle dynamic scaling
                passData.metrics = new Vector4(1f / m_Descriptor.width, 1f / m_Descriptor.height, m_Descriptor.width, m_Descriptor.height);
                passData.areaTexture = m_Data.textures.smaaAreaTex;
                passData.searchTexture = m_Data.textures.smaaSearchTex;
                passData.stencilRef = (float)kStencilBit;
                passData.stencilMask = (float)kStencilBit;
                passData.antialiasingQuality = cameraData.antialiasingQuality;
                passData.material = material;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((SMAASetupPassData data, RenderGraphContext context) =>
                {
                    // Globals
                    data.material.SetVector(ShaderConstants._Metrics, data.metrics);
                    data.material.SetTexture(ShaderConstants._AreaTexture, data.areaTexture);
                    data.material.SetTexture(ShaderConstants._SearchTexture, data.searchTexture);
                    data.material.SetFloat(ShaderConstants._StencilRef, data.stencilRef);
                    data.material.SetFloat(ShaderConstants._StencilMask, data.stencilMask);

                    // Quality presets
                    data.material.shaderKeywords = null;

                    switch (data.antialiasingQuality)
                    {
                        case AntialiasingQuality.Low:
                            data.material.EnableKeyword(ShaderKeywordStrings.SmaaLow);
                            break;
                        case AntialiasingQuality.Medium:
                            data.material.EnableKeyword(ShaderKeywordStrings.SmaaMedium);
                            break;
                        case AntialiasingQuality.High:
                            data.material.EnableKeyword(ShaderKeywordStrings.SmaaHigh);
                            break;
                    }
                });
            }

            using (var builder = renderGraph.AddRenderPass<SMAAPassData>("SMAA Edge Detection", out var passData, ProfilingSampler.Get(URPProfileId.RG_SMAAEdgeDetection)))
            {
                passData.destinationTexture = builder.UseColorBuffer(edgeTexture, 0);
                passData.depthStencilTexture = builder.UseDepthBuffer(edgeTextureStencil, DepthAccess.Write);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.cameraData = renderingData.cameraData;
                passData.material = material;

                builder.SetRenderFunc((SMAAPassData data, RenderGraphContext context) =>
                {
                    var pixelRect = data.cameraData.pixelRect;
                    var SMAAMaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Pass 1: Edge detection
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, SMAAMaterial, 0);
                });
            }

            using (var builder = renderGraph.AddRenderPass<SMAAPassData>("SMAA Blend weights", out var passData, ProfilingSampler.Get(URPProfileId.RG_SMAABlendWeight)))
            {
                passData.destinationTexture = builder.UseColorBuffer(blendTexture, 0);
                passData.sourceTexture = builder.ReadTexture(edgeTexture);
                passData.cameraData = renderingData.cameraData;
                passData.material = material;

                builder.SetRenderFunc((SMAAPassData data, RenderGraphContext context) =>
                {
                    var pixelRect = data.cameraData.pixelRect;
                    var SMAAMaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Pass 2: Blend weights
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, SMAAMaterial, 1);
                });
            }

            using (var builder = renderGraph.AddRenderPass<SMAAPassData>("SMAA Neighborhood blending", out var passData, ProfilingSampler.Get(URPProfileId.RG_SMAANeighborhoodBlend)))
            {
                passData.destinationTexture = builder.UseColorBuffer(SMAATarget, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.blendTexture = builder.ReadTexture(blendTexture);
                passData.cameraData = renderingData.cameraData;
                passData.material = material;

                builder.SetRenderFunc((SMAAPassData data, RenderGraphContext context) =>
                {
                    var pixelRect = data.cameraData.pixelRect;
                    var SMAAMaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Pass 3: Neighborhood blending
                    cmd.SetGlobalTexture(ShaderConstants._BlendTexture, data.blendTexture);

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, SMAAMaterial, 2);
                });
            }
        }
        #endregion

        #region Bloom
        private class UberSetupBloomPassData
        {
            internal Vector4 bloomParams;
            internal bool useRGBM;
            internal Vector4 dirtScaleOffset;
            internal float dirtIntensity;
            internal Texture dirtTexture;
            internal bool highQualityFilteringValue;
            internal TextureHandle bloomTexture;
            internal Material uberMaterial;
        }

        public void UberPostSetupBloomPass(RenderGraph rendergraph, in TextureHandle bloomTexture, Material uberMaterial)
        {
            using (var builder = rendergraph.AddRenderPass<UberSetupBloomPassData>("UberPost - UberPostSetupBloomPass", out var passData, ProfilingSampler.Get(URPProfileId.RG_UberPostSetupBloomPass)))
            {
                // Setup bloom on uber
                var tint = m_Bloom.tint.value.linear;
                var luma = ColorUtils.Luminance(tint);
                tint = luma > 0f ? tint * (1f / luma) : Color.white;
                var bloomParams = new Vector4(m_Bloom.intensity.value, tint.r, tint.g, tint.b);

                // Setup lens dirtiness on uber
                // Keep the aspect ratio correct & center the dirt texture, we don't want it to be
                // stretched or squashed
                var dirtTexture = m_Bloom.dirtTexture.value == null ? Texture2D.blackTexture : m_Bloom.dirtTexture.value;
                float dirtRatio = dirtTexture.width / (float)dirtTexture.height;
                float screenRatio = m_Descriptor.width / (float)m_Descriptor.height;
                var dirtScaleOffset = new Vector4(1f, 1f, 0f, 0f);
                float dirtIntensity = m_Bloom.dirtIntensity.value;

                if (dirtRatio > screenRatio)
                {
                    dirtScaleOffset.x = screenRatio / dirtRatio;
                    dirtScaleOffset.z = (1f - dirtScaleOffset.x) * 0.5f;
                }
                else if (screenRatio > dirtRatio)
                {
                    dirtScaleOffset.y = dirtRatio / screenRatio;
                    dirtScaleOffset.w = (1f - dirtScaleOffset.y) * 0.5f;
                }

                passData.bloomParams = bloomParams;
                passData.dirtScaleOffset = dirtScaleOffset;
                passData.dirtIntensity = dirtIntensity;
                passData.dirtTexture = dirtTexture;
                passData.highQualityFilteringValue = m_Bloom.highQualityFiltering.value;

                passData.bloomTexture = builder.ReadTexture(bloomTexture);
                passData.uberMaterial = uberMaterial;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((UberSetupBloomPassData data, RenderGraphContext context) =>
                {
                    var uberMaterial = data.uberMaterial;
                    uberMaterial.SetVector(ShaderConstants._Bloom_Params, data.bloomParams);
                    uberMaterial.SetFloat(ShaderConstants._Bloom_RGBM, data.useRGBM ? 1f : 0f);
                    uberMaterial.SetVector(ShaderConstants._LensDirt_Params, data.dirtScaleOffset);
                    uberMaterial.SetFloat(ShaderConstants._LensDirt_Intensity, data.dirtIntensity);
                    uberMaterial.SetTexture(ShaderConstants._LensDirt_Texture, data.dirtTexture);

                    // Keyword setup - a bit convoluted as we're trying to save some variants in Uber...
                    if (data.highQualityFilteringValue)
                        uberMaterial.EnableKeyword(data.dirtIntensity > 0f ? ShaderKeywordStrings.BloomHQDirt : ShaderKeywordStrings.BloomHQ);
                    else
                        uberMaterial.EnableKeyword(data.dirtIntensity > 0f ? ShaderKeywordStrings.BloomLQDirt : ShaderKeywordStrings.BloomLQ);

                    uberMaterial.SetTexture(ShaderConstants._Bloom_Texture, data.bloomTexture);
                });
            }
        }

        private class BloomSetupPassData
        {
            internal Vector4 bloomParams;
            internal bool highQualityFilteringValue;
            internal bool useRGBM;
            internal Material material;
        }

        private class BloomPassData
        {
            internal TextureHandle sourceTexture;
            internal TextureHandle sourceTextureLowMip;
            internal Material material;
        }

        public void RenderBloomTexture(RenderGraph renderGraph, in TextureHandle source, out TextureHandle destination, ref RenderingData renderingData)
        {
            // Start at half-res
            int downres = 1;
            switch (m_Bloom.downscale.value)
            {
                case BloomDownscaleMode.Half:
                    downres = 1;
                    break;
                case BloomDownscaleMode.Quarter:
                    downres = 2;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            int tw = m_Descriptor.width >> downres;
            int th = m_Descriptor.height >> downres;

            // Determine the iteration count
            int maxSize = Mathf.Max(tw, th);
            int iterations = Mathf.FloorToInt(Mathf.Log(maxSize, 2f) - 1);
            int mipCount = Mathf.Clamp(iterations, 1, m_Bloom.maxIterations.value);

            var bloomMaterial = m_Materials.bloom;

            using (var builder = renderGraph.AddRenderPass<BloomSetupPassData>("Bloom - Setup", out var passData, ProfilingSampler.Get(URPProfileId.RG_BloomSetupPass)))
            {
                // Pre-filtering parameters
                float clamp = m_Bloom.clamp.value;
                float threshold = Mathf.GammaToLinearSpace(m_Bloom.threshold.value);
                float thresholdKnee = threshold * 0.5f; // Hardcoded soft knee

                // Material setup
                float scatter = Mathf.Lerp(0.05f, 0.95f, m_Bloom.scatter.value);

                passData.bloomParams = new Vector4(scatter, clamp, threshold, thresholdKnee);
                passData.highQualityFilteringValue = m_Bloom.highQualityFiltering.value;
                passData.useRGBM = m_UseRGBM;
                passData.material = bloomMaterial;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((BloomSetupPassData data, RenderGraphContext context) =>
                {
                    var bloomMaterial = data.material;

                    bloomMaterial.SetVector(ShaderConstants._Params, data.bloomParams);
                    CoreUtils.SetKeyword(bloomMaterial, ShaderKeywordStrings.BloomHQ, data.highQualityFilteringValue);
                    CoreUtils.SetKeyword(bloomMaterial, ShaderKeywordStrings.UseRGBM, data.useRGBM);
                });
            }

            // Prefilter
            var desc = GetCompatibleDescriptor(tw, th, m_DefaultHDRFormat);
            _BloomMipDown[0] = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_BloomMipDown", true, FilterMode.Bilinear);
            _BloomMipUp[0] = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_BloomMipUp", true, FilterMode.Bilinear);
            using (var builder = renderGraph.AddRenderPass<BloomPassData>("Bloom - Prefilter", out var passData, ProfilingSampler.Get(URPProfileId.RG_BloomPrefilter)))
            {
                builder.UseColorBuffer(_BloomMipDown[0], 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.material = bloomMaterial;

                builder.SetRenderFunc((BloomPassData data, RenderGraphContext context) =>
                {
                    var material = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, material, 0);
                });
            }

            // Downsample - gaussian pyramid
            TextureHandle lastDown = _BloomMipDown[0];
            for (int i = 1; i < mipCount; i++)
            {
                tw = Mathf.Max(1, tw >> 1);
                th = Mathf.Max(1, th >> 1);
                ref TextureHandle mipDown = ref _BloomMipDown[i];
                ref TextureHandle mipUp = ref _BloomMipUp[i];

                desc.width = tw;
                desc.height = th;

                mipDown = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_BloomMipDown", true, FilterMode.Bilinear);
                mipUp = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_BloomMipUp", true, FilterMode.Bilinear);

                // Classic two pass gaussian blur - use mipUp as a temporary target
                //   First pass does 2x downsampling + 9-tap gaussian
                //   Second pass does 9-tap gaussian using a 5-tap filter + bilinear filtering
                using (var builder = renderGraph.AddRenderPass<BloomPassData>("Bloom - First pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_BloomFirstPass)))
                {
                    builder.UseColorBuffer(mipUp, 0);
                    passData.sourceTexture = builder.ReadTexture(lastDown);
                    passData.material = bloomMaterial;

                    builder.SetRenderFunc((BloomPassData data, RenderGraphContext context) =>
                    {
                        var material = data.material;
                        var cmd = context.cmd;
                        RTHandle sourceTextureHdl = data.sourceTexture;

                        Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                        Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, material, 1);
                    });
                }

                using (var builder = renderGraph.AddRenderPass<BloomPassData>("Bloom - Second pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_BloomSecondPass)))
                {
                    builder.UseColorBuffer(mipDown, 0);
                    passData.sourceTexture = builder.ReadTexture(mipUp);
                    passData.material = bloomMaterial;

                    builder.SetRenderFunc((BloomPassData data, RenderGraphContext context) =>
                    {
                        var material = data.material;
                        var cmd = context.cmd;
                        RTHandle sourceTextureHdl = data.sourceTexture;

                        Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                        Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, material, 2);
                    });
                }

                lastDown = mipDown;
            }

            // Upsample (bilinear by default, HQ filtering does bicubic instead
            for (int i = mipCount - 2; i >= 0; i--)
            {
                TextureHandle lowMip = (i == mipCount - 2) ? _BloomMipDown[i + 1] : _BloomMipUp[i + 1];
                TextureHandle highMip = _BloomMipDown[i];
                TextureHandle dst = _BloomMipUp[i];

                using (var builder = renderGraph.AddRenderPass<BloomPassData>("Bloom - Upsample", out var passData, ProfilingSampler.Get(URPProfileId.RG_BloomUpsample)))
                {
                    builder.UseColorBuffer(dst, 0);
                    passData.sourceTexture = builder.ReadTexture(highMip);
                    passData.sourceTextureLowMip = builder.ReadTexture(lowMip);
                    passData.material = bloomMaterial;

                    builder.SetRenderFunc((BloomPassData data, RenderGraphContext context) =>
                    {
                        var material = data.material;
                        var cmd = context.cmd;
                        RTHandle sourceTextureHdl = data.sourceTexture;

                        cmd.SetGlobalTexture(ShaderConstants._SourceTexLowMip, data.sourceTextureLowMip);

                        Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                        Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, material, 3);
                    });
                }
            }

            destination = _BloomMipUp[0];
        }
        #endregion

        #region DoF
        public void RenderDoF(RenderGraph renderGraph, in TextureHandle source, out TextureHandle destination, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;

            var dofMaterial = m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian ? m_Materials.gaussianDepthOfField : m_Materials.bokehDepthOfField;

            var desc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_Descriptor.graphicsFormat,
                DepthBits.None);
            destination = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_DoFTarget", true, FilterMode.Bilinear);

            if (m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian)
            {
                RenderDoFGaussian(renderGraph, source, destination, ref dofMaterial, ref renderingData);
            }
            else if (m_DepthOfField.mode.value == DepthOfFieldMode.Bokeh)
            {
                RenderDoFBokeh(renderGraph, source, destination, ref dofMaterial, ref renderingData);
            }
        }

        private class DoFGaussianSetupPassData
        {
            internal RenderTextureDescriptor sourceDescriptor;
            internal int downSample;
            internal RenderingData renderingData;
            internal Vector3 cocParams;
            internal bool highQualitySamplingValue;
            internal Material material;
        };

        private class DoFGaussianPassData
        {
            internal TextureHandle cocTexture;
            internal TextureHandle colorTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
        };

        public void RenderDoFGaussian(RenderGraph renderGraph, in TextureHandle source, in TextureHandle destination, ref Material dofMaterial, ref RenderingData renderingData)
        {
            int downSample = 2;
            var material = dofMaterial;
            int wh = m_Descriptor.width / downSample;
            int hh = m_Descriptor.height / downSample;

            using (var builder = renderGraph.AddRenderPass<DoFGaussianSetupPassData>("Setup DoF passes", out var passData, ProfilingSampler.Get(URPProfileId.RG_SetupDoF)))
            {
                float farStart = m_DepthOfField.gaussianStart.value;
                float farEnd = Mathf.Max(farStart, m_DepthOfField.gaussianEnd.value);

                // Assumes a radius of 1 is 1 at 1080p
                // Past a certain radius our gaussian kernel will look very bad so we'll clamp it for
                // very high resolutions (4K+).
                float maxRadius = m_DepthOfField.gaussianMaxRadius.value * (wh / 1080f);
                maxRadius = Mathf.Min(maxRadius, 2f);

                passData.sourceDescriptor = m_Descriptor;
                passData.downSample = downSample;
                passData.cocParams = new Vector3(farStart, farEnd, maxRadius);
                passData.highQualitySamplingValue = m_DepthOfField.highQualitySampling.value;
                passData.material = material;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((DoFGaussianSetupPassData data, RenderGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var dofmaterial = data.material;

                    dofmaterial.SetVector(ShaderConstants._CoCParams, data.cocParams);
                    CoreUtils.SetKeyword(dofmaterial, ShaderKeywordStrings.HighQualitySampling, data.highQualitySamplingValue);
                    PostProcessUtils.SetSourceSize(cmd, data.sourceDescriptor);
                    cmd.SetGlobalVector(ShaderConstants._DownSampleScaleFactor, new Vector4(1.0f / data.downSample, 1.0f / data.downSample, data.downSample, data.downSample));
                });
            }

            // Temporary textures
            var fullCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, m_Descriptor.width, m_Descriptor.height, m_GaussianCoCFormat);
            var fullCoCTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, fullCoCTextureDesc, "_FullCoCTexture", true, FilterMode.Bilinear);
            var halfCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, m_GaussianCoCFormat);
            var halfCoCTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, halfCoCTextureDesc, "_HalfCoCTexture", true, FilterMode.Bilinear);
            var pingTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, m_DefaultHDRFormat);
            var pingTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, pingTextureDesc, "_PingTexture", true, FilterMode.Bilinear);
            var pongTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, m_DefaultHDRFormat);
            var pongTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, pongTextureDesc, "_PongTexture", true, FilterMode.Bilinear);

            using (var builder = renderGraph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Compute CoC", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFComputeCOC)))
            {
                builder.UseColorBuffer(fullCoCTexture, 0);
                passData.sourceTexture = builder.ReadTexture(source);

                UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;
                builder.ReadTexture(renderer.frameResources.cameraDepthTexture);

                passData.material = material;
                builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;
                    // Compute CoC
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 0);
                });
            }

            using (var builder = renderGraph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Downscale & Prefilter Color + CoC", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFDownscalePrefilter)))
            {
                builder.UseColorBuffer(halfCoCTexture, 0);
                builder.UseColorBuffer(pingTexture, 1);
                // TODO RENDERGRAPH: investigate - Setting MRTs without a depth buffer is not supported, could we add the support and remove the depth?
                builder.UseDepthBuffer(halfCoCTexture, DepthAccess.ReadWrite);

                passData.sourceTexture = builder.ReadTexture(source);
                passData.cocTexture = builder.ReadTexture(fullCoCTexture);

                passData.material = material;

                builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Downscale & prefilter color + coc
                    cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, data.cocTexture);

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 1);
                });
            }

            using (var builder = renderGraph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Blur H", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFBlurH)))
            {
                builder.UseColorBuffer(pongTexture, 0);
                passData.sourceTexture = builder.ReadTexture(pingTexture);
                passData.cocTexture = builder.ReadTexture(halfCoCTexture);

                passData.material = material;

                builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTexture = data.sourceTexture;

                    // Blur
                    cmd.SetGlobalTexture(ShaderConstants._HalfCoCTexture, data.cocTexture);
                    Vector2 viewportScale = sourceTexture.useScaling ? new Vector2(sourceTexture.rtHandleProperties.rtHandleScale.x, sourceTexture.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTexture, viewportScale, dofmaterial, 2);
                });
            }

            using (var builder = renderGraph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Blur V", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFBlurV)))
            {
                builder.UseColorBuffer(pingTexture, 0);
                passData.sourceTexture = builder.ReadTexture(pongTexture);
                passData.cocTexture = builder.ReadTexture(halfCoCTexture);

                passData.material = material;

                builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Blur
                    cmd.SetGlobalTexture(ShaderConstants._HalfCoCTexture, data.cocTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 3);
                });
            }

            using (var builder = renderGraph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Composite", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFComposite)))
            {
                builder.UseColorBuffer(destination, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.cocTexture = builder.ReadTexture(fullCoCTexture);
                passData.colorTexture = builder.ReadTexture(pingTexture);

                passData.material = material;

                builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Composite
                    cmd.SetGlobalTexture(ShaderConstants._ColorTexture, data.colorTexture);
                    cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, data.cocTexture);

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 4);
                });
            }
        }

        private class DoFBokehSetupPassData
        {
            internal Vector4[] bokehKernel;
            internal RenderTextureDescriptor sourceDescriptor;
            internal int downSample;
            internal float uvMargin;
            internal Vector4 cocParams;
            internal bool useFastSRGBLinearConversion;
            internal Material material;
        };

        private class DoFBokehPassData
        {
            internal TextureHandle cocTexture;
            internal TextureHandle dofTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
        };

        public void RenderDoFBokeh(RenderGraph renderGraph, in TextureHandle source, in TextureHandle destination, ref Material dofMaterial, ref RenderingData renderingData)
        {
            int downSample = 2;
            var material = dofMaterial;
            int wh = m_Descriptor.width / downSample;
            int hh = m_Descriptor.height / downSample;

            using (var builder = renderGraph.AddRenderPass<DoFBokehSetupPassData>("Setup DoF passes", out var passData, ProfilingSampler.Get(URPProfileId.RG_SetupDoF)))
            {
                // "A Lens and Aperture Camera Model for Synthetic Image Generation" [Potmesil81]
                float F = m_DepthOfField.focalLength.value / 1000f;
                float A = m_DepthOfField.focalLength.value / m_DepthOfField.aperture.value;
                float P = m_DepthOfField.focusDistance.value;
                float maxCoC = (A * F) / (P - F);
                float maxRadius = GetMaxBokehRadiusInPixels(m_Descriptor.height);
                float rcpAspect = 1f / (wh / (float)hh);


                // Prepare the bokeh kernel constant buffer
                int hash = m_DepthOfField.GetHashCode();
                if (hash != m_BokehHash || maxRadius != m_BokehMaxRadius || rcpAspect != m_BokehRCPAspect)
                {
                    m_BokehHash = hash;
                    m_BokehMaxRadius = maxRadius;
                    m_BokehRCPAspect = rcpAspect;
                    PrepareBokehKernel(maxRadius, rcpAspect);
                }
                float uvMargin = (1.0f / m_Descriptor.height) * downSample;

                passData.bokehKernel = m_BokehKernel;
                passData.sourceDescriptor = m_Descriptor;
                passData.downSample = downSample;
                passData.uvMargin = uvMargin;
                passData.cocParams = new Vector4(P, maxCoC, maxRadius, rcpAspect);
                passData.useFastSRGBLinearConversion = m_UseFastSRGBLinearConversion;
                passData.material = material;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((DoFBokehSetupPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;

                    CoreUtils.SetKeyword(dofmaterial, ShaderKeywordStrings.UseFastSRGBLinearConversion, data.useFastSRGBLinearConversion);
                    cmd.SetGlobalVector(ShaderConstants._CoCParams, data.cocParams);
                    cmd.SetGlobalVectorArray(ShaderConstants._BokehKernel, data.bokehKernel);
                    cmd.SetGlobalVector(ShaderConstants._DownSampleScaleFactor, new Vector4(1.0f / data.downSample, 1.0f / data.downSample, data.downSample, data.downSample));
                    cmd.SetGlobalVector(ShaderConstants._BokehConstants, new Vector4(data.uvMargin, data.uvMargin * 2.0f));
                    PostProcessUtils.SetSourceSize(cmd, data.sourceDescriptor);
                });
            }

            // Temporary textures
            var fullCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, m_Descriptor.width, m_Descriptor.height, GraphicsFormat.R8_UNorm);
            var fullCoCTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, fullCoCTextureDesc, "_FullCoCTexture", true, FilterMode.Bilinear);
            var pingTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, GraphicsFormat.R16G16B16A16_SFloat);
            var pingTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, pingTextureDesc, "_PingTexture", true, FilterMode.Bilinear);
            var pongTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, GraphicsFormat.R16G16B16A16_SFloat);
            var pongTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, pongTextureDesc, "_PongTexture", true, FilterMode.Bilinear);

            using (var builder = renderGraph.AddRenderPass<DoFBokehPassData>("Depth of Field - Compute CoC", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFComputeCOC)))
            {
                builder.UseColorBuffer(fullCoCTexture, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.material = material;

                UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;
                builder.ReadTexture(renderer.frameResources.cameraDepthTexture);

                builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Compute CoC
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 0);
                });
            }

            using (var builder = renderGraph.AddRenderPass<DoFBokehPassData>("Depth of Field - Downscale & Prefilter Color + CoC", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFDownscalePrefilter)))
            {
                builder.UseColorBuffer(pingTexture, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.cocTexture = builder.ReadTexture(fullCoCTexture);
                passData.material = material;

                builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Downscale & prefilter color + coc
                    cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, data.cocTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 1);
                });
            }

            using (var builder = renderGraph.AddRenderPass<DoFBokehPassData>("Depth of Field - Bokeh Blur", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFBlurBokeh)))
            {
                builder.UseColorBuffer(pongTexture, 0);
                passData.sourceTexture = builder.ReadTexture(pingTexture);
                passData.material = material;

                builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Downscale & prefilter color + coc
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 2);
                });
            }

            using (var builder = renderGraph.AddRenderPass<DoFBokehPassData>("Depth of Field - Post-filtering", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFPostFilter)))
            {
                builder.UseColorBuffer(pingTexture, 0);
                passData.sourceTexture = builder.ReadTexture(pongTexture);
                passData.material = material;

                builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Post - filtering
                    // TODO RENDERGRAPH: Look into loadstore op in BlitDstDiscardContent
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 3);
                });
            }

            using (var builder = renderGraph.AddRenderPass<DoFBokehPassData>("Depth of Field - Composite", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFComposite)))
            {
                builder.UseColorBuffer(destination, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.dofTexture = builder.ReadTexture(pingTexture);

                passData.material = material;

                builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Composite
                    // TODO RENDERGRAPH: Look into loadstore op in BlitDstDiscardContent
                    cmd.SetGlobalTexture(ShaderConstants._DofTexture, data.dofTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 4);
                });
            }
        }
        #endregion

        #region Panini
        private class PaniniProjectionPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal RenderTextureDescriptor sourceTextureDesc;
            internal Material material;
            internal Vector4 paniniParams;
            internal bool isPaniniGeneric;
        }

        public void RenderPaniniProjection(RenderGraph renderGraph, in TextureHandle source, out TextureHandle destination, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;

            var desc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_Descriptor.graphicsFormat,
                DepthBits.None);

            destination = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_PaniniProjectionTarget", true, FilterMode.Bilinear);

            float distance = m_PaniniProjection.distance.value;
            var viewExtents = CalcViewExtents(camera);
            var cropExtents = CalcCropExtents(camera, distance);

            float scaleX = cropExtents.x / viewExtents.x;
            float scaleY = cropExtents.y / viewExtents.y;
            float scaleF = Mathf.Min(scaleX, scaleY);

            float paniniD = distance;
            float paniniS = Mathf.Lerp(1f, Mathf.Clamp01(scaleF), m_PaniniProjection.cropToFit.value);

            using (var builder = renderGraph.AddRenderPass<PaniniProjectionPassData>("Panini Projection", out var passData, ProfilingSampler.Get(URPProfileId.PaniniProjection)))
            {
                passData.destinationTexture = builder.UseColorBuffer(destination, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.material = m_Materials.paniniProjection;
                passData.paniniParams = new Vector4(viewExtents.x, viewExtents.y, paniniD, paniniS);
                passData.isPaniniGeneric = 1f - Mathf.Abs(paniniD) > float.Epsilon;
                passData.sourceTextureDesc = m_Descriptor;

                builder.SetRenderFunc((PaniniProjectionPassData data, RenderGraphContext context) =>
                {
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    cmd.SetGlobalVector(ShaderConstants._Params, data.paniniParams);
                    cmd.EnableShaderKeyword(data.isPaniniGeneric ? ShaderKeywordStrings.PaniniGeneric : ShaderKeywordStrings.PaniniUnitDistance);

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, data.material, 0);

                    cmd.DisableShaderKeyword(data.isPaniniGeneric ? ShaderKeywordStrings.PaniniGeneric : ShaderKeywordStrings.PaniniUnitDistance);
                });

                return;
            }
        }
        #endregion

        #region MotionBlur
        private class MotionBlurPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
            internal int passIndex;
            internal RenderTextureDescriptor sourceDescriptor;
            internal Matrix4x4[] prevVP;
            internal float intensity;
            internal float clamp;
#if ENABLE_VR && ENABLE_XR_MODULE
            internal bool singlePass;
#endif
        }

#if ENABLE_VR && ENABLE_XR_MODULE
        const int PREV_VP_MATRIX_SIZE = 2;
#else
        const int PREV_VP_MATRIX_SIZE = 1;
#endif
        internal static Matrix4x4[] prevVP = new Matrix4x4[PREV_VP_MATRIX_SIZE];
        public void RenderMotionBlur(RenderGraph renderGraph, in TextureHandle source, out TextureHandle destination, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;
            var material = m_Materials.cameraMotionBlur;

            var desc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_Descriptor.graphicsFormat,
                DepthBits.None);

            destination = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_MotionBlurTarget", true, FilterMode.Bilinear);


            if (!m_PrevVPMatricesMap.TryGetValue(cameraData.camera.GetInstanceID(), out var prevVPMatricies))
                m_PrevVPMatricesMap.Add(cameraData.camera.GetInstanceID(), new Matrix4x4[PREV_VP_MATRIX_SIZE]);

            if (prevVPMatricies?.Length != PREV_VP_MATRIX_SIZE)
                prevVPMatricies = new Matrix4x4[PREV_VP_MATRIX_SIZE];

#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled && cameraData.xr.singlePassEnabled)
            {
                var viewProj0 = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(0), true) * cameraData.GetViewMatrix(0);
                var viewProj1 = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(1), true) * cameraData.GetViewMatrix(1);
                if (m_ResetHistory)
                {
                    prevVP[0] = viewProj0;
                    prevVP[1] = viewProj1;
                }
                else
                    prevVP = prevVPMatricies;

                prevVPMatricies[0] = viewProj0;
                prevVPMatricies[1] = viewProj1;
            }
            else
#endif
            {
                int prevViewProjMIdx = 0;
#if ENABLE_VR && ENABLE_XR_MODULE
                if (cameraData.xr.enabled)
                    prevViewProjMIdx = cameraData.xr.multipassId;
#endif
                // This is needed because Blit will reset viewproj matrices to identity and UniversalRP currently
                // relies on SetupCameraProperties instead of handling its own matrices.
                // TODO: We need get rid of SetupCameraProperties and setup camera matrices in Universal
                var proj = cameraData.GetProjectionMatrix();
                var view = cameraData.GetViewMatrix();
                var viewProj = proj * view;

                if (m_ResetHistory)
                    prevVP[0] = viewProj;
                else
                    prevVP[0] = prevVPMatricies[prevViewProjMIdx];

                prevVPMatricies[prevViewProjMIdx] = viewProj;
            }

            using (var builder = renderGraph.AddRenderPass<MotionBlurPassData>("Motion Blur", out var passData, ProfilingSampler.Get(URPProfileId.RG_MotionBlur)))
            {
                passData.destinationTexture = builder.UseColorBuffer(destination, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.material = material;
                passData.passIndex = (int)m_MotionBlur.quality.value;
                passData.sourceDescriptor = m_Descriptor;
                passData.prevVP = prevVP;
                passData.intensity = m_MotionBlur.intensity.value;
                passData.clamp = m_MotionBlur.clamp.value;
#if ENABLE_VR && ENABLE_XR_MODULE
                passData.singlePass = cameraData.xr.enabled && cameraData.xr.singlePassEnabled;
#endif
                builder.SetRenderFunc((MotionBlurPassData data, RenderGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var sourceDesc = data.sourceDescriptor;
                    RTHandle sourceTextureHdl = data.sourceTexture;
#if ENABLE_VR && ENABLE_XR_MODULE
                    if (data.singlePass)
                        data.material.SetMatrixArray("_PrevViewProjMStereo", data.prevVP);
                    else
#endif
                    data.material.SetMatrix("_PrevViewProjM", data.prevVP[0]);

                    data.material.SetFloat("_Intensity", data.intensity);
                    data.material.SetFloat("_Clamp", data.clamp);

                    PostProcessUtils.SetSourceSize(cmd, sourceDesc);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, data.material, data.passIndex);
                });

                return;
            }
        }
#endregion

#region LensFlare
        private class LensFlarePassData
        {
            internal TextureHandle destinationTexture;
            internal RenderTextureDescriptor sourceDescriptor;
            internal Camera camera;
            internal Material material;
            internal bool usePanini;
            internal float paniniDistance;
            internal float paniniCropToFit;
        }

        public void RenderLensFlareDatadriven(RenderGraph renderGraph, in TextureHandle destination, ref RenderingData renderingData)
        {
            using (var builder = renderGraph.AddRenderPass<LensFlarePassData>("Lens Flare Pass", out var passData, ProfilingSampler.Get(URPProfileId.LensFlareDataDriven)))
            {
                // Use WriteTexture here because DoLensFlareDataDrivenCommon will call SetRenderTarget internally.
                // TODO RENDERGRAPH: convert SRP core lensflare to be rendergraph friendly
                passData.destinationTexture = builder.WriteTexture(destination);
                passData.sourceDescriptor = m_Descriptor;
                passData.camera = renderingData.cameraData.camera;
                passData.material = m_Materials.lensFlareDataDriven;
                if (m_PaniniProjection.IsActive())
                {
                    passData.usePanini = true;
                    passData.paniniDistance = m_PaniniProjection.distance.value;
                    passData.paniniCropToFit = m_PaniniProjection.cropToFit.value;
                }
                else
                {
                    passData.usePanini = false;
                    passData.paniniDistance = 1.0f;
                    passData.paniniCropToFit = 1.0f;
                }

                builder.SetRenderFunc((LensFlarePassData data, RenderGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var camera = data.camera;

                    var gpuView = camera.worldToCameraMatrix;
                    var gpuNonJitteredProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                    // Zero out the translation component.
                    gpuView.SetColumn(3, new Vector4(0, 0, 0, 1));
                    var gpuVP = gpuNonJitteredProj * camera.worldToCameraMatrix;

                    LensFlareCommonSRP.DoLensFlareDataDrivenCommon(data.material, LensFlareCommonSRP.Instance, camera, (float)data.sourceDescriptor.width, (float)data.sourceDescriptor.height,
                        data.usePanini, data.paniniDistance, data.paniniCropToFit,
                        true,
                        camera.transform.position,
                        gpuVP,
                        cmd, data.destinationTexture,
                        (Light light, Camera cam, Vector3 wo) => { return GetLensFlareLightAttenuation(light, cam, wo); },
                        ShaderConstants._FlareOcclusionRemapTex, ShaderConstants._FlareOcclusionTex, ShaderConstants._FlareOcclusionIndex,
                        ShaderConstants._FlareTex, ShaderConstants._FlareColorValue,
                        ShaderConstants._FlareData0, ShaderConstants._FlareData1, ShaderConstants._FlareData2, ShaderConstants._FlareData3, ShaderConstants._FlareData4,
                        false);
                });

                return;
            }
        }
#endregion

#region FinalPass
        private class PostProcessingFinalSetupPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
            internal CameraData cameraData;
            internal bool isFxaaEnabled;
            internal bool doLateFsrColorConversion;
        }

        public void RenderFinalSetup(RenderGraph renderGraph, in TextureHandle source, in TextureHandle destination, ref RenderingData renderingData, bool performFXAA, bool performColorConversion)
        {
            // FSR color onversion or FXAA
            UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;

            using (var builder = renderGraph.AddRenderPass<PostProcessingFinalSetupPassData>("Postprocessing Final Setup Pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_FinalSetup)))
            {
                passData.destinationTexture = builder.UseColorBuffer(destination, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.cameraData = renderingData.cameraData;
                passData.material = m_Materials.scalingSetup;
                passData.isFxaaEnabled = performFXAA;
                passData.doLateFsrColorConversion = performColorConversion;

                builder.SetRenderFunc((PostProcessingFinalSetupPassData data, RenderGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var cameraData = data.cameraData;
                    var camera = data.cameraData.camera;
                    var material = data.material;
                    var isFxaaEnabled = data.isFxaaEnabled;
                    var doLateFsrColorConversion = data.doLateFsrColorConversion;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    if (isFxaaEnabled)
                    {
                        material.EnableKeyword(ShaderKeywordStrings.Fxaa);
                    }

                    if (doLateFsrColorConversion)
                    {
                        material.EnableKeyword(ShaderKeywordStrings.Gamma20);
                    }

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, material, 0);
                });
                return;
            }
        }

        private class PostProcessingFinalFSRScalePassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
            internal CameraData cameraData;

        }

        public void RenderFinalFSRScale(RenderGraph renderGraph, in TextureHandle source, in TextureHandle destination, ref RenderingData renderingData)
        {
            // FSR upscale
            UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;
            m_Materials.easu.shaderKeywords = null;

            using (var builder = renderGraph.AddRenderPass<PostProcessingFinalFSRScalePassData>("Postprocessing Final FSR Scale Pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_FinalFSRScale)))
            {
                passData.destinationTexture = builder.UseColorBuffer(destination, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.cameraData = renderingData.cameraData;
                passData.material = m_Materials.easu;

                builder.SetRenderFunc((PostProcessingFinalFSRScalePassData data, RenderGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var cameraData = data.cameraData;
                    var sourceTex = data.sourceTexture;
                    var destTex = data.destinationTexture;
                    var material = data.material;
                    RTHandle sourceHdl = (RTHandle)sourceTex;
                    RTHandle destHdl = (RTHandle)destTex;

                    var fsrInputSize = new Vector2(sourceHdl.referenceSize.x, sourceHdl.referenceSize.y);
                    var fsrOutputSize = new Vector2(destHdl.referenceSize.x, destHdl.referenceSize.y);
                    FSRUtils.SetEasuConstants(cmd, fsrInputSize, fsrInputSize, fsrOutputSize);

                    Vector2 viewportScale = sourceHdl.useScaling ? new Vector2(sourceHdl.rtHandleProperties.rtHandleScale.x, sourceHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceHdl, viewportScale, material, 0);

                    // RCAS
                    // Use the override value if it's available, otherwise use the default.
                    float sharpness = cameraData.fsrOverrideSharpness ? cameraData.fsrSharpness : FSRUtils.kDefaultSharpnessLinear;

                    // Set up the parameters for the RCAS pass unless the sharpness value indicates that it wont have any effect.
                    if (cameraData.fsrSharpness > 0.0f)
                    {
                        // RCAS is performed during the final post blit, but we set up the parameters here for better logical grouping.
                        material.EnableKeyword(ShaderKeywordStrings.Rcas);
                        FSRUtils.SetRcasConstantsLinear(cmd, sharpness);
                    }
                });
                return;
            }
        }

        private class PostProcessingFinalBlitPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
            internal CameraData cameraData;
            internal bool isFxaaEnabled;
        }

        public void RenderFinalBlit(RenderGraph renderGraph, in TextureHandle source, ref RenderingData renderingData, bool performFXAA)
        {
            UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;

            using (var builder = renderGraph.AddRenderPass<PostProcessingFinalBlitPassData>("Postprocessing Final Blit Pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_FinalBlit)))
            {
                passData.destinationTexture = builder.UseColorBuffer(renderer.frameResources.backBufferColor, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.cameraData = renderingData.cameraData;
                passData.material = m_Materials.finalPass;
                passData.isFxaaEnabled = performFXAA;

                builder.SetRenderFunc((PostProcessingFinalBlitPassData data, RenderGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var cameraData = data.cameraData;
                    var material = data.material;
                    var isFxaaEnabled = data.isFxaaEnabled;
                    RTHandle sourceTextureHdl = data.sourceTexture;
                    RTHandle destinationTextureHdl = data.destinationTexture;
                    if (isFxaaEnabled)
                        material.EnableKeyword(ShaderKeywordStrings.Fxaa);

                    bool isRenderToBackBufferTarget = !cameraData.isSceneViewCamera;
#if ENABLE_VR && ENABLE_XR_MODULE
                    if (cameraData.xr.enabled)
                        isRenderToBackBufferTarget = destinationTextureHdl == cameraData.xr.renderTarget;
#endif
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;

                    // We y-flip if
                    // 1) we are blitting from render texture to back buffer(UV starts at bottom) and
                    // 2) renderTexture starts UV at top
                    bool yflip = isRenderToBackBufferTarget && cameraData.targetTexture == null && SystemInfo.graphicsUVStartsAtTop;
                    Vector4 scaleBias = yflip ? new Vector4(viewportScale.x, -viewportScale.y, 0, viewportScale.y) : new Vector4(viewportScale.x, viewportScale.y, 0, 0);

                    cmd.SetViewport(cameraData.pixelRect);
                    Blitter.BlitTexture(cmd, sourceTextureHdl, scaleBias, material, 0);
                });

                return;
            }
        }

        public void RenderFinalPassRenderGraph(RenderGraph renderGraph, in TextureHandle source, ref RenderingData renderingData)
        {
            ref var cameraData = ref renderingData.cameraData;
            var material = m_Materials.finalPass;
            var cmd = renderingData.commandBuffer;

            material.shaderKeywords = null;

            PostProcessUtils.SetSourceSize(cmd, cameraData.cameraTargetDescriptor);
            SetupGrain(ref cameraData, material);
            SetupDithering(ref cameraData, material);

            if (RequireSRGBConversionBlitToBackBuffer(ref cameraData))
                material.EnableKeyword(ShaderKeywordStrings.LinearToSRGBConversion);

            GetActiveDebugHandler(ref renderingData)?.UpdateShaderGlobalPropertiesForFinalValidationPass(cmd, ref cameraData, !m_HasFinalPass);

            bool isFxaaEnabled = (cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing);
            bool isFsrEnabled = ((cameraData.imageScalingMode == ImageScalingMode.Upscaling) && (cameraData.upscalingFilter == ImageUpscalingFilter.FSR));
            bool doLateFsrColorConversion = (isFsrEnabled && (isFxaaEnabled || m_hasExternalPostPasses));
            bool isSetupRequired = (isFxaaEnabled || doLateFsrColorConversion);
            bool isScaling = cameraData.imageScalingMode != ImageScalingMode.None;

            var tempRtDesc = cameraData.cameraTargetDescriptor;
            tempRtDesc.msaaSamples = 1;
            tempRtDesc.depthBufferBits = 0;
            var scalingSetupTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tempRtDesc, "scalingSetupTarget", true, FilterMode.Point);
            var upscaleRtDesc = tempRtDesc;
            upscaleRtDesc.width = cameraData.pixelWidth;
            upscaleRtDesc.height = cameraData.pixelHeight;
            var upScaleTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, upscaleRtDesc, "_UpscaledTexture", true, FilterMode.Point);

            var currentSource = source;
            if (isScaling)
            {
                if (isSetupRequired)
                {
                    RenderFinalSetup(renderGraph, in currentSource, in scalingSetupTarget, ref renderingData, isFxaaEnabled, doLateFsrColorConversion);
                    currentSource = scalingSetupTarget;
                }

                switch (cameraData.imageScalingMode)
                {
                    case ImageScalingMode.Upscaling:
                    {
                        switch (cameraData.upscalingFilter)
                        {
                            case ImageUpscalingFilter.Point:
                            {
                                material.EnableKeyword(ShaderKeywordStrings.PointSampling);
                                break;
                            }
                            case ImageUpscalingFilter.Linear:
                            {
                                break;
                            }
                            case ImageUpscalingFilter.FSR:
                            {
                                RenderFinalFSRScale(renderGraph, in currentSource, in upScaleTarget, ref renderingData);
                                currentSource = upScaleTarget;
                                break;
                            }
                        }
                        break;
                    }
                    case ImageScalingMode.Downscaling:
                    {
                        break;
                    }
                }
            }

            RenderFinalBlit(renderGraph, in currentSource, ref renderingData, isFxaaEnabled);
        }
#endregion

#region UberPost
        private class UberPostPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal TextureHandle lutTexture;
            internal Vector4 lutParams;
            internal TextureHandle userLutTexture;
            internal Vector4 userLutParams;
            internal Material material;
            internal CameraData cameraData;
            internal TonemappingMode toneMappingMode;
            internal bool isHdr;
        }

        public void RenderUberPost(RenderGraph renderGraph, in TextureHandle sourceTexture, in TextureHandle destTexture, in TextureHandle lutTexture, ref RenderingData renderingData)
        {
            var material = m_Materials.uber;
            ref var postProcessingData = ref renderingData.postProcessingData;
            bool hdr = postProcessingData.gradingMode == ColorGradingMode.HighDynamicRange;
            int lutHeight = postProcessingData.lutSize;
            int lutWidth = lutHeight * lutHeight;

            // Source material setup
            float postExposureLinear = Mathf.Pow(2f, m_ColorAdjustments.postExposure.value);
            Vector4 lutParams = new Vector4(1f / lutWidth, 1f / lutHeight, lutHeight - 1f, postExposureLinear);

            RTHandle userLutRThdl = m_ColorLookup.texture.value ? RTHandles.Alloc(m_ColorLookup.texture.value) : null;
            TextureHandle userLutTexture = renderGraph.ImportTexture(userLutRThdl);
            Vector4 userLutParams = !m_ColorLookup.IsActive()
                ? Vector4.zero
                : new Vector4(1f / m_ColorLookup.texture.value.width,
                    1f / m_ColorLookup.texture.value.height,
                    m_ColorLookup.texture.value.height - 1f,
                    m_ColorLookup.contribution.value);

            using (var builder = renderGraph.AddRenderPass<UberPostPassData>("Postprocessing Uber Post Pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_UberPost)))
            {
                passData.destinationTexture = builder.UseColorBuffer(destTexture, 0);
                passData.sourceTexture = builder.ReadTexture(sourceTexture);
                passData.lutTexture = builder.ReadTexture(lutTexture);
                passData.lutParams = lutParams;
                if (userLutTexture.IsValid())
                    passData.userLutTexture = builder.ReadTexture(userLutTexture);
                passData.userLutParams = userLutParams;
                passData.cameraData = renderingData.cameraData;
                passData.material = material;
                passData.toneMappingMode = m_Tonemapping.mode.value;

                builder.SetRenderFunc((UberPostPassData data, RenderGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var cameraData = data.cameraData;
                    var camera = data.cameraData.camera;
                    var material = data.material;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    material.SetTexture(ShaderConstants._InternalLut, data.lutTexture);
                    material.SetVector(ShaderConstants._Lut_Params, data.lutParams);
                    material.SetTexture(ShaderConstants._UserLut, data.userLutTexture);
                    material.SetVector(ShaderConstants._UserLut_Params, data.userLutParams);

                    if (data.isHdr)
                    {
                        material.EnableKeyword(ShaderKeywordStrings.HDRGrading);
                    }
                    else
                    {
                        switch (data.toneMappingMode)
                        {
                            case TonemappingMode.Neutral: material.EnableKeyword(ShaderKeywordStrings.TonemapNeutral); break;
                            case TonemappingMode.ACES: material.EnableKeyword(ShaderKeywordStrings.TonemapACES); break;
                            default: break; // None
                        }
                    }

                    // Done with Uber, blit it
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, material, 0);
                });

                return;
            }
        }
#endregion

        private class PostFXSetupPassData { }

        public void RenderPostProcessingRenderGraph(RenderGraph renderGraph, in TextureHandle activeCameraColorTexture, in TextureHandle lutTexture, in TextureHandle postProcessingTarget ,ref RenderingData renderingData, bool hasFinalPass)
        {
            var stack = VolumeManager.instance.stack;
            m_DepthOfField = stack.GetComponent<DepthOfField>();
            m_MotionBlur = stack.GetComponent<MotionBlur>();
            m_PaniniProjection = stack.GetComponent<PaniniProjection>();
            m_Bloom = stack.GetComponent<Bloom>();
            m_LensDistortion = stack.GetComponent<LensDistortion>();
            m_ChromaticAberration = stack.GetComponent<ChromaticAberration>();
            m_Vignette = stack.GetComponent<Vignette>();
            m_ColorLookup = stack.GetComponent<ColorLookup>();
            m_ColorAdjustments = stack.GetComponent<ColorAdjustments>();
            m_Tonemapping = stack.GetComponent<Tonemapping>();
            m_FilmGrain = stack.GetComponent<FilmGrain>();
            m_UseFastSRGBLinearConversion = renderingData.postProcessingData.useFastSRGBLinearConversion;
            // TODO RENDERGRAPH: the descriptor should come from postProcessingTarget, not cameraTarget
            m_Descriptor = renderingData.cameraData.cameraTargetDescriptor;
            m_Descriptor.useMipMap = false;
            m_Descriptor.autoGenerateMips = false;
            m_HasFinalPass = hasFinalPass;

            ref CameraData cameraData = ref renderingData.cameraData;
            ref ScriptableRenderer renderer = ref cameraData.renderer;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            //We blit back and forth without msaa untill the last blit.
            bool useStopNan = cameraData.isStopNaNEnabled && m_Materials.stopNaN != null;
            bool useSubPixelMorpAA = cameraData.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing && SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;
            var dofMaterial = m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian ? m_Materials.gaussianDepthOfField : m_Materials.bokehDepthOfField;
            bool useDepthOfField = m_DepthOfField.IsActive() && !isSceneViewCamera && dofMaterial != null;
            bool useLensFlare = !LensFlareCommonSRP.Instance.IsEmpty();
            bool useMotionBlur = m_MotionBlur.IsActive() && !isSceneViewCamera;
            bool usePaniniProjection = m_PaniniProjection.IsActive() && !isSceneViewCamera;

            using (var builder = renderGraph.AddRenderPass<PostFXSetupPassData>("Setup PostFX passes", out var passData,
                ProfilingSampler.Get(URPProfileId.RG_SetupPostFX)))
            {
                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PostFXSetupPassData data, RenderGraphContext context) =>
                {
                    CommandBuffer cmd = context.cmd;

                    // Setup projection matrix for cmd.DrawMesh()
                    cmd.SetGlobalMatrix(ShaderConstants._FullscreenProjMat, GL.GetGPUProjectionMatrix(Matrix4x4.identity, true));
                });
            }

            TextureHandle currentSource = activeCameraColorTexture;

            // Optional NaN killer before post-processing kicks in
            // stopNaN may be null on Adreno 3xx. It doesn't support full shader level 3.5, but SystemInfo.graphicsShaderLevel is 35.
            if (useStopNan)
            {
                RenderStopNaN(renderGraph, in currentSource, out var stopNaNTarget, ref renderingData);
                currentSource = stopNaNTarget;
            }

            if(useSubPixelMorpAA)
            {
                RenderSMAA(renderGraph, in currentSource, out var SMAATarget, ref renderingData);
                currentSource = SMAATarget;
            }

            // Depth of Field
            // Adreno 3xx SystemInfo.graphicsShaderLevel is 35, but instancing support is disabled due to buggy drivers.
            // DOF shader uses #pragma target 3.5 which adds requirement for instancing support, thus marking the shader unsupported on those devices.
            if (useDepthOfField)
            {
                RenderDoF(renderGraph, in currentSource, out var DoFTarget, ref renderingData);
                currentSource = DoFTarget;
            }

            if(useMotionBlur)
            {
                RenderMotionBlur(renderGraph, in currentSource, out var MotionBlurTarget, ref renderingData);
                currentSource = MotionBlurTarget;
            }

            if(usePaniniProjection)
            {
                RenderPaniniProjection(renderGraph, in currentSource, out var PaniniTarget, ref renderingData);
                currentSource = PaniniTarget;
            }

            if(useLensFlare)
            {
                RenderLensFlareDatadriven(renderGraph, in currentSource, ref renderingData);
            }

            // Uberpost
            {
                // Reset uber keywords
                m_Materials.uber.shaderKeywords = null;

                // Bloom goes first
                bool bloomActive = m_Bloom.IsActive();
                if (bloomActive)
                {
                    RenderBloomTexture(renderGraph, currentSource, out var BloomTexture, ref renderingData);
                    UberPostSetupBloomPass(renderGraph, in BloomTexture, m_Materials.uber);
                }

                // TODO RENDERGRAPH: Once we started removing the non-RG code pass in URP, we should move functions below to renderfunc so that material setup happens at
                // the same timeline of executing the rendergraph. Keep them here for now so we cound reuse non-RG code to reduce maintainance cost.
                SetupLensDistortion(m_Materials.uber, isSceneViewCamera);
                SetupChromaticAberration(m_Materials.uber);
                SetupVignette(m_Materials.uber, cameraData.xr);
                SetupGrain(ref cameraData, m_Materials.uber);
                SetupDithering(ref cameraData, m_Materials.uber);

                if (RequireSRGBConversionBlitToBackBuffer(ref cameraData))
                    m_Materials.uber.EnableKeyword(ShaderKeywordStrings.LinearToSRGBConversion);

                // When we're running FSR upscaling and there's no passes after this (including the FXAA pass), we can safely perform color conversion as part of uber post

                // When FSR is active, we're required to provide it with input in a perceptual color space. Ideally, we can just do the color conversion as part of UberPost
                // since FSR will *usually* be executed right after it. Unfortunately, there are a couple of situations where this is not true:
                // 1. It's possible for users to add their own passes between UberPost and FinalPost. When user passes are present, we're unable to perform the conversion
                //    here since it'd change the color space that the passes operate in which could lead to incorrect results.
                // 2. When FXAA is enabled with FSR, FXAA is moved to an earlier pass to ensure that FSR sees fully anti-aliased input. The moved FXAA pass sits between
                //    UberPost and FSR so we can no longer perform color conversion here without affecting other passes.
                bool doEarlyFsrColorConversion = (!m_hasExternalPostPasses &&
                                                  (((cameraData.imageScalingMode == ImageScalingMode.Upscaling) && (cameraData.upscalingFilter == ImageUpscalingFilter.FSR)) &&
                                                   (cameraData.antialiasing != AntialiasingMode.FastApproximateAntialiasing)));
                if (doEarlyFsrColorConversion)
                {
                    m_Materials.uber.EnableKeyword(ShaderKeywordStrings.Gamma20);
                }

                if (m_UseFastSRGBLinearConversion)
                {
                    m_Materials.uber.EnableKeyword(ShaderKeywordStrings.UseFastSRGBLinearConversion);
                }

                GetActiveDebugHandler(ref renderingData)?.UpdateShaderGlobalPropertiesForFinalValidationPass(renderingData.commandBuffer, ref cameraData, !m_HasFinalPass);

                RenderUberPost(renderGraph, in currentSource, in postProcessingTarget, in lutTexture, ref renderingData);
            }

            m_ResetHistory = false;
        }
    }
}