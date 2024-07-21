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
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FidelityFX.FSR2
{
    /// <summary>
    /// A collection of helper functions and data structures required by the FSR2 process.
    /// </summary>
    public static class Fsr2
    {
        /// <summary>
        /// Creates a new FSR2 context with standard parameters that are appropriate for the current platform. 
        /// </summary>
        public static Fsr2Context CreateContext(Vector2Int displaySize, Vector2Int maxRenderSize, Fsr2Shaders shaders, InitializationFlags flags = 0)
        {
            if (SystemInfo.usesReversedZBuffer)
                flags |= InitializationFlags.EnableDepthInverted;
            else
                flags &= ~InitializationFlags.EnableDepthInverted;
            
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            flags |= InitializationFlags.EnableDebugChecking;
#endif
            
            Debug.Log($"Setting up FSR2 with render size: {maxRenderSize.x}x{maxRenderSize.y}, display size: {displaySize.x}x{displaySize.y}, flags: {flags}");
            
            var contextDescription = new ContextDescription
            {
                Flags = flags,
                DisplaySize = displaySize,
                MaxRenderSize = maxRenderSize,
                Shaders = shaders,
            };
            
            var context = new Fsr2Context();
            context.Create(contextDescription);
            return context;
        }

        public static float GetUpscaleRatioFromQualityMode(QualityMode qualityMode)
        {
            switch (qualityMode)
            {
                case QualityMode.NativeAA:
                    return 1.0f;
                case QualityMode.UltraQuality:
                    return 1.2f;
                case QualityMode.Quality:
                    return 1.5f;
                case QualityMode.Balanced:
                    return 1.7f;
                case QualityMode.Performance:
                    return 2.0f;
                case QualityMode.UltraPerformance:
                    return 3.0f;
                default:
                    return 1.0f;
            }
        }

        public static void GetRenderResolutionFromQualityMode(
            out int renderWidth, out int renderHeight,
            int displayWidth, int displayHeight, QualityMode qualityMode)
        {
            float ratio = GetUpscaleRatioFromQualityMode(qualityMode);
            renderWidth = Mathf.RoundToInt(displayWidth / ratio);
            renderHeight = Mathf.RoundToInt(displayHeight / ratio);
        }
        
        public static float GetMipmapBiasOffset(int renderWidth, int displayWidth)
        {
            return Mathf.Log((float)renderWidth / displayWidth, 2.0f) - 1.0f;
        }

        public static int GetJitterPhaseCount(int renderWidth, int displayWidth)
        {
            const float basePhaseCount = 8.0f;
            int jitterPhaseCount = (int)(basePhaseCount * Mathf.Pow((float)displayWidth / renderWidth, 2.0f));
            return jitterPhaseCount;
        }

        public static void GetJitterOffset(out float outX, out float outY, int index, int phaseCount)
        {
            outX = Halton((index % phaseCount) + 1, 2) - 0.5f;
            outY = Halton((index % phaseCount) + 1, 3) - 0.5f;
        }
        
        // Calculate halton number for index and base.
        private static float Halton(int index, int @base)
        {
            float f = 1.0f, result = 0.0f;

            for (int currentIndex = index; currentIndex > 0;) {

                f /= @base;
                result += f * (currentIndex % @base);
                currentIndex = (int)Mathf.Floor((float)currentIndex / @base);
            }

            return result;
        }
        
        public static float Lanczos2(float value)
        {
            return Mathf.Abs(value) < Mathf.Epsilon ? 1.0f : Mathf.Sin(Mathf.PI * value) / (Mathf.PI * value) * (Mathf.Sin(0.5f * Mathf.PI * value) / (0.5f * Mathf.PI * value));
        }
        
#if !UNITY_2021_1_OR_NEWER
        internal static void SetBufferData(this CommandBuffer commandBuffer, ComputeBuffer computeBuffer, Array data)
        {
            commandBuffer.SetComputeBufferData(computeBuffer, data);
        }
#endif
        
        public enum QualityMode
        {
            NativeAA = 0,
            UltraQuality = 1,
            Quality = 2,
            Balanced = 3,
            Performance = 4,
            UltraPerformance = 5,
        }

        [Flags]
        public enum InitializationFlags
        {
            EnableHighDynamicRange = 1 << 0,
            EnableDisplayResolutionMotionVectors = 1 << 1,
            EnableMotionVectorsJitterCancellation = 1 << 2,
            EnableDepthInverted = 1 << 3,
            EnableDepthInfinite = 1 << 4,
            EnableAutoExposure = 1 << 5,
            EnableDynamicResolution = 1 << 6,
            EnableFP16Usage = 1 << 7,
            EnableDebugChecking = 1 << 8,
        }

        /// <summary>
        /// A structure encapsulating the parameters required to initialize FidelityFX Super Resolution 2 upscaling.
        /// </summary>
        public struct ContextDescription
        {
            public InitializationFlags Flags;
            public Vector2Int MaxRenderSize;
            public Vector2Int DisplaySize;
            public Fsr2Shaders Shaders;
        }

        /// <summary>
        /// A structure encapsulating the parameters for dispatching the various passes of FidelityFX Super Resolution 2.
        /// </summary>
        public class DispatchDescription
        {
            public ResourceView Color;
            public ResourceView Depth;
            public ResourceView MotionVectors;
            public ResourceView Exposure;                       // optional
            public ResourceView Reactive;                       // optional
            public ResourceView TransparencyAndComposition;     // optional
            public ResourceView Output;
            public Vector2 JitterOffset;
            public Vector2 MotionVectorScale;
            public Vector2Int RenderSize;
            public Vector2Int InputResourceSize;
            public bool EnableSharpening;
            public float Sharpness;
            public float FrameTimeDelta;    // in seconds
            public float PreExposure;
            public bool Reset;
            public float CameraNear;
            public float CameraFar;
            public float CameraFovAngleVertical;
            public float ViewSpaceToMetersFactor;
            public bool UseTextureArrays;   // Enable texture array bindings, primarily used for HDRP and XR
            
            // EXPERIMENTAL reactive mask generation parameters
            public bool EnableAutoReactive;
            public ResourceView ColorOpaqueOnly;
            public float AutoTcThreshold = 0.05f;
            public float AutoTcScale = 1.0f;
            public float AutoReactiveScale = 5.0f;
            public float AutoReactiveMax = 0.9f;
        }

        /// <summary>
        /// A structure encapsulating the parameters for automatic generation of a reactive mask.
        /// The default values for Scale, CutoffThreshold, BinaryValue and Flags were taken from the FSR2 demo project.
        /// </summary>
        public class GenerateReactiveDescription
        {
            public ResourceView ColorOpaqueOnly;
            public ResourceView ColorPreUpscale;
            public ResourceView OutReactive;
            public Vector2Int RenderSize;
            public float Scale = 0.5f;
            public float CutoffThreshold = 0.2f;
            public float BinaryValue = 0.9f;
            public GenerateReactiveFlags Flags = GenerateReactiveFlags.ApplyTonemap | GenerateReactiveFlags.ApplyThreshold | GenerateReactiveFlags.UseComponentsMax;
        }

        [Flags]
        public enum GenerateReactiveFlags
        {
            ApplyTonemap = 1 << 0,
            ApplyInverseTonemap = 1 << 1,
            ApplyThreshold = 1 << 2,
            UseComponentsMax = 1 << 3,
        }
        
        [Serializable, StructLayout(LayoutKind.Sequential)]
        internal struct UpscalerConstants
        {
            public Vector2Int renderSize;
            public Vector2Int maxRenderSize;
            public Vector2Int displaySize;
            public Vector2Int inputColorResourceDimensions;
            public Vector2Int lumaMipDimensions;
            public int lumaMipLevelToUse;
            public int frameIndex;
            
            public Vector4 deviceToViewDepth;
            public Vector2 jitterOffset;
            public Vector2 motionVectorScale;
            public Vector2 downscaleFactor;
            public Vector2 motionVectorJitterCancellation;
            public float preExposure;
            public float previousFramePreExposure;
            public float tanHalfFOV;
            public float jitterPhaseCount;
            public float deltaTime;
            public float dynamicResChangeFactor;
            public float viewSpaceToMetersFactor;
            public float padding;
        }
        
        [Serializable, StructLayout(LayoutKind.Sequential)]
        internal struct SpdConstants
        {
            public uint mips;
            public uint numWorkGroups;
            public uint workGroupOffsetX, workGroupOffsetY;
            public uint renderSizeX, renderSizeY;
        }

        [Serializable, StructLayout(LayoutKind.Sequential)]
        internal struct GenerateReactiveConstants
        {
            public float scale;
            public float threshold;
            public float binaryValue;
            public uint flags;
        }

        [Serializable, StructLayout(LayoutKind.Sequential)]
        internal struct GenerateReactiveConstants2
        {
            public float autoTcThreshold;
            public float autoTcScale;
            public float autoReactiveScale;
            public float autoReactiveMax;
        }
        
        [Serializable, StructLayout(LayoutKind.Sequential)]
        internal struct RcasConstants
        {
            public RcasConstants(uint sharpness, uint halfSharp)
            {
                this.sharpness = sharpness;
                this.halfSharp = halfSharp;
                dummy0 = dummy1 = 0;
            }
        
            public readonly uint sharpness;
            public readonly uint halfSharp;
            public readonly uint dummy0;
            public readonly uint dummy1;
        }
    }
}
