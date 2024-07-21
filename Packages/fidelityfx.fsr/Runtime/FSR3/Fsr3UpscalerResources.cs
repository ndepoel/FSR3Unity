// Copyright (c) 2024 Nico de Poel
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FidelityFX.FSR3
{
    /// <summary>
    /// Helper class for bundling and managing persistent resources required by the FSR3 Upscaler process.
    /// This includes lookup tables, default fallback resources and double-buffered resources that get swapped between frames.
    /// </summary>
    internal class Fsr3UpscalerResources
    {
        public Texture2D LanczosLut;
        public Texture2D DefaultExposure;
        public Texture2D DefaultReactive;
        
        public RenderTexture SpdAtomicCounter;
        public RenderTexture SpdMips;
        public RenderTexture DilatedVelocity;
        public RenderTexture DilatedDepth;
        public RenderTexture ReconstructedPrevNearestDepth;
        public RenderTexture FrameInfo;
        public RenderTexture AutoReactive;
        public RenderTexture AutoComposition;
        
        public readonly RenderTexture[] Accumulation = new RenderTexture[2];
        public readonly RenderTexture[] Luma = new RenderTexture[2];
        public readonly RenderTexture[] InternalUpscaled = new RenderTexture[2];
        public readonly RenderTexture[] LumaHistory = new RenderTexture[2];
        public readonly RenderTexture[] PrevPreAlpha = new RenderTexture[2];
        public readonly RenderTexture[] PrevPostAlpha = new RenderTexture[2];

        public void Create(Fsr3Upscaler.ContextDescription contextDescription)
        {
            // Generate the data for the LUT
            const int lanczos2LutWidth = 128;
            float[] lanczos2Weights = new float[lanczos2LutWidth];
            for (int currentLanczosWidthIndex = 0; currentLanczosWidthIndex < lanczos2LutWidth; ++currentLanczosWidthIndex)
            {
                float x = 2.0f * currentLanczosWidthIndex / (lanczos2LutWidth - 1);
                float y = Fsr3Upscaler.Lanczos2(x);
                lanczos2Weights[currentLanczosWidthIndex] = y;
            }

            Vector2Int maxRenderSize = contextDescription.MaxRenderSize;
            Vector2Int maxRenderSizeDiv2 = maxRenderSize / 2;
            
            // Resource FSR3UPSCALER_LanczosLutData: FFX_RESOURCE_USAGE_READ_ONLY, FFX_SURFACE_FORMAT_R16_SNORM, FFX_RESOURCE_FLAGS_NONE
            // R16_SNorm textures are not supported by Unity on most platforms, strangely enough. So instead we use R32_SFloat and upload pre-normalized float data.
            LanczosLut = new Texture2D(lanczos2LutWidth, 1, GraphicsFormat.R32_SFloat, TextureCreationFlags.None) { name = "FSR3UPSCALER_LanczosLutData" };
            LanczosLut.SetPixelData(lanczos2Weights, 0);
            LanczosLut.Apply();
            
            // Resource FSR3UPSCALER_DefaultReactivityMask: FFX_RESOURCE_USAGE_READ_ONLY, FFX_SURFACE_FORMAT_R8_UNORM, FFX_RESOURCE_FLAGS_NONE
            DefaultReactive = new Texture2D(1, 1, GraphicsFormat.R8_UNorm, TextureCreationFlags.None) { name = "FSR3UPSCALER_DefaultReactivityMask" };
            DefaultReactive.SetPixel(0, 0, Color.clear);
            DefaultReactive.Apply();
            
            // Resource FSR3UPSCALER_DefaultExposure: FFX_RESOURCE_USAGE_READ_ONLY, FFX_SURFACE_FORMAT_R32G32_FLOAT, FFX_RESOURCE_FLAGS_NONE
            DefaultExposure = new Texture2D(1, 1, GraphicsFormat.R32G32_SFloat, TextureCreationFlags.None) { name = "FSR3UPSCALER_DefaultExposure" };
            DefaultExposure.SetPixel(0, 0, Color.clear);
            DefaultExposure.Apply();
            
            // Resource FSR3UPSCALER_SpdAtomicCounter: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R32_UINT, FFX_RESOURCE_FLAGS_ALIASABLE
            // Despite what the original FSR3 codebase says, this resource really isn't aliasable. Resetting this counter to 0 every frame breaks auto-exposure on MacOS Metal.
            SpdAtomicCounter = new RenderTexture(1, 1, 0, GraphicsFormat.R32_UInt) { name = "FSR3UPSCALER_SpdAtomicCounter", enableRandomWrite = true };
            SpdAtomicCounter.Create();
            
            // Resource FSR3UPSCALER_SpdMips: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16G16_FLOAT, FFX_RESOURCE_FLAGS_ALIASABLE
            // This is a rather special case: it's an aliasable resource, but because we require a mipmap chain and bind specific mip levels per shader, we can't easily use temporary RTs for this.
            int mipCount = 1 + Mathf.FloorToInt(Mathf.Log(Math.Max(maxRenderSizeDiv2.x, maxRenderSizeDiv2.y), 2.0f));
            SpdMips = new RenderTexture(maxRenderSizeDiv2.x, maxRenderSizeDiv2.y, 0, GraphicsFormat.R16G16_SFloat, mipCount) { name = "FSR3UPSCALER_SpdMips", enableRandomWrite = true, useMipMap = true, autoGenerateMips = false };
            SpdMips.Create();
            
            // Resource FSR3UPSCALER_DilatedVelocity: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16G16_FLOAT, FFX_RESOURCE_FLAGS_NONE
            DilatedVelocity = new RenderTexture(maxRenderSize.x, maxRenderSize.y, 0, GraphicsFormat.R16G16_SFloat) { name = "FSR3UPSCALER_DilatedVelocity", enableRandomWrite = true };
            DilatedVelocity.Create();
            
            // Resource FSR3UPSCALER_DilatedDepth: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R32_FLOAT, FFX_RESOURCE_FLAGS_NONE
            DilatedDepth = new RenderTexture(maxRenderSize.x, maxRenderSize.y, 0, GraphicsFormat.R32_SFloat) { name = "FSR3UPSCALER_DilatedDepth", enableRandomWrite = true };
            DilatedDepth.Create();
            
            // Resource FSR3UPSCALER_ReconstructedPrevNearestDepth: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R32_UINT, FFX_RESOURCE_FLAGS_NONE
            ReconstructedPrevNearestDepth = new RenderTexture(maxRenderSize.x, maxRenderSize.y, 0, GraphicsFormat.R32_UInt) { name = "FSR3UPSCALER_ReconstructedPrevNearestDepth", enableRandomWrite = true };
            ReconstructedPrevNearestDepth.Create();
            
            // Resource FSR3UPSCALER_FrameInfo: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R32G32B32A32_FLOAT, FFX_RESOURCE_FLAGS_NONE
            FrameInfo = new RenderTexture(1, 1, 0, GraphicsFormat.R32G32B32A32_SFloat) { name = "FSR3UPSCALER_FrameInfo", enableRandomWrite = true };
            FrameInfo.Create();

            // Resources FSR3UPSCALER_Accumulation1/2: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R8_UNORM, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(Accumulation, "FSR3UPSCALER_Accumulation", maxRenderSize, GraphicsFormat.R8_UNorm);
            
            // Resources FSR3UPSCALER_Luma1/2: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16_FLOAT, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(Luma, "FSR3UPSCALER_Luma", maxRenderSize, GraphicsFormat.R16_SFloat);
            
            // Resources FSR3UPSCALER_InternalUpscaled1/2: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16G16B16A16_FLOAT, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(InternalUpscaled, "FSR3UPSCALER_InternalUpscaled", contextDescription.MaxUpscaleSize, GraphicsFormat.R16G16B16A16_SFloat);
            
            // Resources FSR3UPSCALER_LumaHistory1/2: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16G16B16A16_FLOAT, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(LumaHistory, "FSR3UPSCALER_LumaHistory", maxRenderSize, GraphicsFormat.R16G16B16A16_SFloat);
        }

        public void CreateTcrAutogenResources(Fsr3Upscaler.ContextDescription contextDescription)
        {
            // Resource FSR3UPSCALER_AutoReactive: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R8_UNORM, FFX_RESOURCE_FLAGS_NONE
            AutoReactive = new RenderTexture(contextDescription.MaxRenderSize.x, contextDescription.MaxRenderSize.y, 0, GraphicsFormat.R8_UNorm) { name = "FSR3UPSCALER_AutoReactive", enableRandomWrite = true };
            AutoReactive.Create();

            // Resource FSR3UPSCALER_AutoComposition: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R8_UNORM, FFX_RESOURCE_FLAGS_NONE
            AutoComposition = new RenderTexture(contextDescription.MaxRenderSize.x, contextDescription.MaxRenderSize.y, 0, GraphicsFormat.R8_UNorm) { name = "FSR3UPSCALER_AutoComposition", enableRandomWrite = true };
            AutoComposition.Create();

            // Resources FSR3UPSCALER_PrevPreAlpha0/1: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R11G11B10_FLOAT, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(PrevPreAlpha, "FSR3UPSCALER_PrevPreAlpha", contextDescription.MaxRenderSize, GraphicsFormat.B10G11R11_UFloatPack32);

            // Resources FSR3UPSCALER_PrevPostAlpha0/1: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R11G11B10_FLOAT, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(PrevPostAlpha, "FSR3UPSCALER_PrevPostAlpha", contextDescription.MaxRenderSize, GraphicsFormat.B10G11R11_UFloatPack32);
        }
        
        // Set up shared aliasable resources, i.e. temporary render textures
        // These do not need to persist between frames, but they do need to be available between passes
        public static void CreateAliasableResources(CommandBuffer commandBuffer, Fsr3Upscaler.ContextDescription contextDescription, Fsr3Upscaler.DispatchDescription dispatchParams)
        {
            Vector2Int maxUpscaleSize = contextDescription.MaxUpscaleSize;
            Vector2Int maxRenderSize = contextDescription.MaxRenderSize;
            Vector2Int maxRenderSizeDiv2 = maxRenderSize / 2;
            
            // FSR3UPSCALER_IntermediateFp16x1: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16_FLOAT, FFX_RESOURCE_FLAGS_ALIASABLE
            commandBuffer.GetTemporaryRT(Fsr3ShaderIDs.UavIntermediate, maxRenderSize.x, maxRenderSize.y, 0, default, GraphicsFormat.R16_SFloat, 1, true);
            
            // FSR3UPSCALER_ShadingChange: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R8_UNORM, FFX_RESOURCE_FLAGS_ALIASABLE
            commandBuffer.GetTemporaryRT(Fsr3ShaderIDs.UavShadingChange, maxRenderSizeDiv2.x, maxRenderSizeDiv2.y, 0, default, GraphicsFormat.R8_UNorm, 1, true);
            
            // FSR3UPSCALER_NewLocks: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R8_UNORM, FFX_RESOURCE_FLAGS_ALIASABLE
            commandBuffer.GetTemporaryRT(Fsr3ShaderIDs.UavNewLocks, maxUpscaleSize.x, maxUpscaleSize.y, 0, default, GraphicsFormat.R8_UNorm, 1, true);
            
            // FSR3UPSCALER_FarthestDepthMip1: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16_FLOAT, FFX_RESOURCE_FLAGS_ALIASABLE
            commandBuffer.GetTemporaryRT(Fsr3ShaderIDs.UavFarthestDepthMip1, maxRenderSizeDiv2.x, maxRenderSizeDiv2.y, 0, default, GraphicsFormat.R16_SFloat, 1, true);
            
            // FSR3UPSCALER_DilatedReactiveMasks: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R8G8B8A8_UNORM, FFX_RESOURCE_FLAGS_ALIASABLE
            commandBuffer.GetTemporaryRT(Fsr3ShaderIDs.UavDilatedReactiveMasks, maxRenderSize.x, maxRenderSize.y, 0, default, GraphicsFormat.R8G8B8A8_UNorm, 1, true);
        }

        public static void DestroyAliasableResources(CommandBuffer commandBuffer)
        {
            // Release all of the aliasable resources used this frame
            commandBuffer.ReleaseTemporaryRT(Fsr3ShaderIDs.UavDilatedReactiveMasks);
            commandBuffer.ReleaseTemporaryRT(Fsr3ShaderIDs.UavFarthestDepthMip1);
            commandBuffer.ReleaseTemporaryRT(Fsr3ShaderIDs.UavNewLocks);
            commandBuffer.ReleaseTemporaryRT(Fsr3ShaderIDs.UavShadingChange);
            commandBuffer.ReleaseTemporaryRT(Fsr3ShaderIDs.UavIntermediate);
        }

        private static void CreateDoubleBufferedResource(RenderTexture[] resource, string name, Vector2Int size, GraphicsFormat format)
        {
            for (int i = 0; i < 2; ++i)
            {
                resource[i] = new RenderTexture(size.x, size.y, 0, format) { name = name + (i + 1), enableRandomWrite = true };
                resource[i].Create();
            }
        }

        public void Destroy()
        {
            DestroyTcrAutogenResources();
            
            DestroyResource(LumaHistory);
            DestroyResource(InternalUpscaled);
            DestroyResource(Luma);
            DestroyResource(Accumulation);
            
            DestroyResource(ref FrameInfo);
            DestroyResource(ref ReconstructedPrevNearestDepth);
            DestroyResource(ref DilatedDepth);
            DestroyResource(ref DilatedVelocity);
            DestroyResource(ref SpdMips);
            DestroyResource(ref SpdAtomicCounter);
            
            DestroyResource(ref DefaultReactive);
            DestroyResource(ref DefaultExposure);
            DestroyResource(ref LanczosLut);
        }

        public void DestroyTcrAutogenResources()
        {
            DestroyResource(PrevPostAlpha);
            DestroyResource(PrevPreAlpha);
            DestroyResource(ref AutoComposition);
            DestroyResource(ref AutoReactive);
        }
        
        private static void DestroyResource(ref Texture2D resource)
        {
            if (resource == null)
                return;
            
#if UNITY_EDITOR
            if (Application.isPlaying && !UnityEditor.EditorApplication.isPaused)
                UnityEngine.Object.Destroy(resource);
            else
                UnityEngine.Object.DestroyImmediate(resource);
#else
            UnityEngine.Object.Destroy(resource);
#endif
            resource = null;
        }

        private static void DestroyResource(ref RenderTexture resource)
        {
            if (resource == null)
                return;
            
            resource.Release();
            resource = null;
        }

        private static void DestroyResource(RenderTexture[] resource)
        {
            for (int i = 0; i < resource.Length; ++i)
                DestroyResource(ref resource[i]);
        }
    }
}
