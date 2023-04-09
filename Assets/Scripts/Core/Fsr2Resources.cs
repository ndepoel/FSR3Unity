// Copyright (c) 2023 Nico de Poel
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

namespace FidelityFX
{
    /// <summary>
    /// Helper class for bundling and managing persistent resources required by the FSR2 process.
    /// This includes lookup tables, default fallback resources and double-buffered resources that get swapped between frames.
    /// </summary>
    internal class Fsr2Resources
    {
        public Texture2D DefaultExposure;
        public Texture2D DefaultReactive;
        public Texture2D LanczosLut;
        public Texture2D MaximumBiasLut;
        public RenderTexture AutoExposure;
        public RenderTexture SceneLuminance;
        public readonly RenderTexture[] DilatedMotionVectors = new RenderTexture[2];
        public readonly RenderTexture[] LockStatus = new RenderTexture[2];
        public readonly RenderTexture[] InternalUpscaled = new RenderTexture[2];
        public readonly RenderTexture[] LumaHistory = new RenderTexture[2];
        
        public void Create(Fsr2.ContextDescription contextDescription)
        {
            // Generate the data for the LUT
            const int lanczos2LutWidth = 128;
            float[] lanczos2Weights = new float[lanczos2LutWidth];
            for (int currentLanczosWidthIndex = 0; currentLanczosWidthIndex < lanczos2LutWidth; ++currentLanczosWidthIndex)
            {
                float x = 2.0f * currentLanczosWidthIndex / (lanczos2LutWidth - 1);
                float y = Fsr2.Lanczos2(x);
                lanczos2Weights[currentLanczosWidthIndex] = y;
            }

            float[] maximumBias = new float[MaximumBiasTextureWidth * MaximumBiasTextureHeight];
            for (int i = 0; i < maximumBias.Length; ++i)
            {
                maximumBias[i] = MaximumBias[i] / 2.0f;
            }

            // Resource FSR2_LanczosLutData: FFX_RESOURCE_USAGE_READ_ONLY, FFX_SURFACE_FORMAT_R16_SNORM, FFX_RESOURCE_FLAGS_NONE
            // R16_SNorm textures are not supported by Unity on most platforms, strangely enough. So instead we use R32_SFloat and upload pre-normalized float data.
            LanczosLut = new Texture2D(lanczos2LutWidth, 1, GraphicsFormat.R32_SFloat, TextureCreationFlags.None) { name = "FSR2_LanczosLutData" };
            LanczosLut.SetPixelData(lanczos2Weights, 0);
            LanczosLut.Apply();
            
            // Resource FSR2_MaximumUpsampleBias: FFX_RESOURCE_USAGE_READ_ONLY, FFX_SURFACE_FORMAT_R16_SNORM, FFX_RESOURCE_FLAGS_NONE
            MaximumBiasLut = new Texture2D(MaximumBiasTextureWidth, MaximumBiasTextureHeight, GraphicsFormat.R32_SFloat, TextureCreationFlags.None) { name = "FSR2_MaximumUpsampleBias" };
            MaximumBiasLut.SetPixelData(maximumBias, 0);
            MaximumBiasLut.Apply();
            
            // Resource FSR2_DefaultExposure: FFX_RESOURCE_USAGE_READ_ONLY, FFX_SURFACE_FORMAT_R32G32_FLOAT, FFX_RESOURCE_FLAGS_NONE
            DefaultExposure = new Texture2D(1, 1, GraphicsFormat.R32G32_SFloat, TextureCreationFlags.None) { name = "FSR2_DefaultExposure" };
            DefaultExposure.SetPixel(0, 0, Color.clear);
            DefaultExposure.Apply();

            // Resource FSR2_DefaultReactivityMask: FFX_RESOURCE_USAGE_READ_ONLY, FFX_SURFACE_FORMAT_R8_UNORM, FFX_RESOURCE_FLAGS_NONE
            DefaultReactive = new Texture2D(1, 1, GraphicsFormat.R8_UNorm, TextureCreationFlags.None) { name = "FSR2_DefaultReactivityMask" };
            DefaultReactive.SetPixel(0, 0, Color.clear);
            DefaultReactive.Apply();

            // Resource FSR2_AutoExposure: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R32G32_FLOAT, FFX_RESOURCE_FLAGS_NONE
            AutoExposure = new RenderTexture(1, 1, 0, GraphicsFormat.R32G32_SFloat) { name = "FSR2_AutoExposure", enableRandomWrite = true };
            AutoExposure.Create();
            
            // Resource FSR2_ExposureMips: FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16_FLOAT, FFX_RESOURCE_FLAGS_ALIASABLE
            // This is a rather special case: it's an aliasable resource, but because we require a mipmap chain and bind specific mip levels per shader, we can't easily use temporary RTs for this.
            int w = contextDescription.MaxRenderSize.x / 2, h = contextDescription.MaxRenderSize.y / 2;
            int mipCount = 1 + Mathf.FloorToInt(Mathf.Log(Math.Max(w, h), 2.0f));
            SceneLuminance = new RenderTexture(w, h, 0, GraphicsFormat.R16_SFloat, mipCount) { name = "FSR2_ExposureMips", enableRandomWrite = true, useMipMap = true, autoGenerateMips = false };
            SceneLuminance.Create();
            
            // Resources FSR2_InternalDilatedVelocity1/2: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16G16_FLOAT, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(DilatedMotionVectors, "FSR2_InternalDilatedVelocity", contextDescription.MaxRenderSize, GraphicsFormat.R16G16_SFloat);
            
            // Resources FSR2_LockStatus1/2: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16G16_FLOAT, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(LockStatus, "FSR2_LockStatus", contextDescription.DisplaySize, GraphicsFormat.R16G16_SFloat);
            
            // Resources FSR2_InternalUpscaled1/2: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R16G16B16A16_FLOAT, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(InternalUpscaled, "FSR2_InternalUpscaled", contextDescription.DisplaySize, GraphicsFormat.R16G16B16A16_SFloat);
            
            // Resources FSR2_LumaHistory1/2: FFX_RESOURCE_USAGE_RENDERTARGET | FFX_RESOURCE_USAGE_UAV, FFX_SURFACE_FORMAT_R8G8B8A8_UNORM, FFX_RESOURCE_FLAGS_NONE
            CreateDoubleBufferedResource(LumaHistory, "FSR2_LumaHistory", contextDescription.DisplaySize, GraphicsFormat.R8G8B8A8_UNorm);
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
            DestroyResource(LumaHistory);
            DestroyResource(InternalUpscaled);
            DestroyResource(LockStatus);
            DestroyResource(DilatedMotionVectors);
            DestroyResource(ref SceneLuminance);
            DestroyResource(ref AutoExposure);
            DestroyResource(ref DefaultReactive);
            DestroyResource(ref DefaultExposure);
            DestroyResource(ref MaximumBiasLut);
            DestroyResource(ref LanczosLut);
        }
        
        private static void DestroyResource(ref Texture2D resource)
        {
            if (resource == null)
                return;
            
            UnityEngine.Object.Destroy(resource);
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

        private const int MaximumBiasTextureWidth = 16;
        private const int MaximumBiasTextureHeight = 16;
        private static readonly float[] MaximumBias =  
        {
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.876f,	1.809f,	1.772f,	1.753f,	1.748f,
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.869f,	1.801f,	1.764f,	1.745f,	1.739f,
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.976f,	1.841f,	1.774f,	1.737f,	1.716f,	1.71f,
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.914f,	1.784f,	1.716f,	1.673f,	1.649f,	1.641f,
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.793f,	1.676f,	1.604f,	1.562f,	1.54f,	1.533f,
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.802f,	1.619f,	1.536f,	1.492f,	1.467f,	1.454f,	1.449f,
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.812f,	1.575f,	1.496f,	1.456f,	1.432f,	1.416f,	1.408f,	1.405f,
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.555f,	1.479f,	1.438f,	1.413f,	1.398f,	1.387f,	1.381f,	1.379f,
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.812f,	1.555f,	1.474f,	1.43f,	1.404f,	1.387f,	1.376f,	1.368f,	1.363f,	1.362f,
            2.0f,	2.0f,	2.0f,	2.0f,	2.0f,	1.802f,	1.575f,	1.479f,	1.43f,	1.401f,	1.382f,	1.369f,	1.36f,	1.354f,	1.351f,	1.35f,
            2.0f,	2.0f,	1.976f,	1.914f,	1.793f,	1.619f,	1.496f,	1.438f,	1.404f,	1.382f,	1.367f,	1.357f,	1.349f,	1.344f,	1.341f,	1.34f,
            1.876f,	1.869f,	1.841f,	1.784f,	1.676f,	1.536f,	1.456f,	1.413f,	1.387f,	1.369f,	1.357f,	1.347f,	1.341f,	1.336f,	1.333f,	1.332f,
            1.809f,	1.801f,	1.774f,	1.716f,	1.604f,	1.492f,	1.432f,	1.398f,	1.376f,	1.36f,	1.349f,	1.341f,	1.335f,	1.33f,	1.328f,	1.327f,
            1.772f,	1.764f,	1.737f,	1.673f,	1.562f,	1.467f,	1.416f,	1.387f,	1.368f,	1.354f,	1.344f,	1.336f,	1.33f,	1.326f,	1.323f,	1.323f,
            1.753f,	1.745f,	1.716f,	1.649f,	1.54f,	1.454f,	1.408f,	1.381f,	1.363f,	1.351f,	1.341f,	1.333f,	1.328f,	1.323f,	1.321f,	1.32f,
            1.748f,	1.739f,	1.71f,	1.641f,	1.533f,	1.449f,	1.405f,	1.379f,	1.362f,	1.35f,	1.34f,	1.332f,	1.327f,	1.323f,	1.32f,	1.319f,
        };
    }
}
