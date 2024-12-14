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
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace FidelityFX.FSR3
{
    /// <summary>
    /// Base class for all of the compute passes that make up the FSR3 Upscaler process.
    /// This loosely matches the FfxPipelineState struct from the original FSR3 codebase, wrapped in an object-oriented blanket.
    /// These classes are responsible for loading compute shaders, managing temporary resources, binding resources to shader kernels and dispatching said shaders.
    /// </summary>
    internal abstract class Fsr3UpscalerPass: IDisposable
    {
        protected readonly Fsr3Upscaler.ContextDescription ContextDescription;
        protected readonly Fsr3UpscalerResources Resources;
        protected readonly ComputeBuffer Constants;
        
        protected ComputeShader ComputeShader;
        protected int KernelIndex;

        private CustomSampler _sampler;
        
        protected Fsr3UpscalerPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
        {
            ContextDescription = contextDescription;
            Resources = resources;
            Constants = constants;
        }

        public virtual void Dispose()
        {
        }

        public void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            BeginSample(commandBuffer);
            DoScheduleDispatch(commandBuffer, dispatchParams, frameIndex, dispatchX, dispatchY);
            EndSample(commandBuffer);
        }

        protected abstract void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY);
        
        protected void InitComputeShader(string passName, ComputeShader shader)
        {
            InitComputeShader(passName, shader, ContextDescription.Flags);
        }
        
        private void InitComputeShader(string passName, ComputeShader shader, Fsr3Upscaler.InitializationFlags flags)
        {
            if (shader == null)
            {
                throw new MissingReferenceException($"Shader for FSR3 Upscaler pass '{passName}' could not be loaded! Please ensure it is included in the project correctly.");
            }

            ComputeShader = shader;
            KernelIndex = ComputeShader.FindKernel("CS");
            _sampler = CustomSampler.Create(passName);

            bool useLut = false;
#if UNITY_2022_1_OR_NEWER   // This will also work in 2020.3.43+ and 2021.3.14+ 
            if (SystemInfo.computeSubGroupSize == 64)
            {
                useLut = true;
            }
#endif
            
            // This matches the permutation rules from the CreatePipeline* functions
            if ((flags & Fsr3Upscaler.InitializationFlags.EnableHighDynamicRange) != 0) ComputeShader.EnableKeyword("FFX_FSR3UPSCALER_OPTION_HDR_COLOR_INPUT");
            if ((flags & Fsr3Upscaler.InitializationFlags.EnableDisplayResolutionMotionVectors) == 0) ComputeShader.EnableKeyword("FFX_FSR3UPSCALER_OPTION_LOW_RESOLUTION_MOTION_VECTORS");
            if ((flags & Fsr3Upscaler.InitializationFlags.EnableMotionVectorsJitterCancellation) != 0) ComputeShader.EnableKeyword("FFX_FSR3UPSCALER_OPTION_JITTERED_MOTION_VECTORS");
            if ((flags & Fsr3Upscaler.InitializationFlags.EnableDepthInverted) != 0) ComputeShader.EnableKeyword("FFX_FSR3UPSCALER_OPTION_INVERTED_DEPTH");
            if (useLut) ComputeShader.EnableKeyword("FFX_FSR3UPSCALER_OPTION_REPROJECT_USE_LANCZOS_TYPE");
            if ((flags & Fsr3Upscaler.InitializationFlags.EnableFP16Usage) != 0) ComputeShader.EnableKeyword("FFX_HALF");
        }

        [Conditional("ENABLE_PROFILER")]
        protected void BeginSample(CommandBuffer cmd)
        {
            cmd.BeginSample(_sampler);
        }

        [Conditional("ENABLE_PROFILER")]
        protected void EndSample(CommandBuffer cmd)
        {
            cmd.EndSample(_sampler);
        }
    }
    
    internal class Fsr3UpscalerPrepareInputsPass : Fsr3UpscalerPass
    {
        public Fsr3UpscalerPrepareInputsPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Prepare Inputs", contextDescription.Shaders.prepareInputsPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var color = ref dispatchParams.Color;
            ref var depth = ref dispatchParams.Depth;
            ref var motionVectors = ref dispatchParams.MotionVectors;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputDepth, depth.RenderTarget, depth.MipLevel, depth.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavDilatedMotionVectors, Resources.DilatedVelocity);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavDilatedDepth, Resources.DilatedDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavReconstructedPrevNearestDepth, Resources.ReconstructedPrevNearestDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavFarthestDepth, Fsr3ShaderIDs.UavIntermediate);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavCurrentLuma, Resources.Luma[frameIndex]);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr3UpscalerLumaPyramidPass : Fsr3UpscalerPass
    {
        private readonly ComputeBuffer _spdConstants;
        
        public Fsr3UpscalerLumaPyramidPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants, ComputeBuffer spdConstants)
            : base(contextDescription, resources, constants)
        {
            _spdConstants = spdConstants;
            
            InitComputeShader("Compute Luminance Pyramid", contextDescription.Shaders.lumaPyramidPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvCurrentLuma, Resources.Luma[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvFarthestDepth, Fsr3ShaderIDs.UavIntermediate);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdAtomicCount, Resources.SpdAtomicCounter);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavFrameInfo, Resources.FrameInfo);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip0, Resources.SpdMips, 0);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip1, Resources.SpdMips, 1);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip2, Resources.SpdMips, 2);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip3, Resources.SpdMips, 3);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip4, Resources.SpdMips, 4);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip5, Resources.SpdMips, 5);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbSpd, _spdConstants, 0, Marshal.SizeOf<Fsr3Upscaler.SpdConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr3UpscalerShadingChangePyramidPass : Fsr3UpscalerPass
    {
        private readonly ComputeBuffer _spdConstants;
        
        public Fsr3UpscalerShadingChangePyramidPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants, ComputeBuffer spdConstants)
            : base(contextDescription, resources, constants)
        {
            _spdConstants = spdConstants;
            
            InitComputeShader("Compute Shading Change Pyramid", contextDescription.Shaders.shadingChangePyramidPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var exposure = ref dispatchParams.Exposure;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvCurrentLuma, Resources.Luma[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvPreviousLuma, Resources.Luma[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedVelocity);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdAtomicCount, Resources.SpdAtomicCounter);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip0, Resources.SpdMips, 0);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip1, Resources.SpdMips, 1);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip2, Resources.SpdMips, 2);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip3, Resources.SpdMips, 3);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip4, Resources.SpdMips, 4);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdMip5, Resources.SpdMips, 5);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbSpd, _spdConstants, 0, Marshal.SizeOf<Fsr3Upscaler.SpdConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
    
    internal class Fsr3UpscalerShadingChangePass : Fsr3UpscalerPass
    {
        public Fsr3UpscalerShadingChangePass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Compute Shading Change", contextDescription.Shaders.shadingChangePass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvSpdMips, Resources.SpdMips);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr3UpscalerPrepareReactivityPass : Fsr3UpscalerPass
    {
        public Fsr3UpscalerPrepareReactivityPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Prepare Reactivity", contextDescription.Shaders.prepareReactivityPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var exposure = ref dispatchParams.Exposure;
            ref var reactive = ref dispatchParams.Reactive;
            ref var tac = ref dispatchParams.TransparencyAndComposition;

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvReconstructedPrevNearestDepth, Resources.ReconstructedPrevNearestDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedVelocity);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedDepth, Resources.DilatedDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvReactiveMask, reactive.RenderTarget, reactive.MipLevel, reactive.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvTransparencyAndCompositionMask, tac.RenderTarget, tac.MipLevel, tac.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvAccumulation, Resources.Accumulation[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvShadingChange, Fsr3ShaderIDs.UavShadingChange);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvCurrentLuma, Resources.Luma[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavAccumulation, Resources.Accumulation[frameIndex]); 

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr3UpscalerLumaInstabilityPass : Fsr3UpscalerPass
    {
        public Fsr3UpscalerLumaInstabilityPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Compute Luminance Instability", contextDescription.Shaders.lumaInstabilityPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var exposure = ref dispatchParams.Exposure;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedReactiveMasks, Fsr3ShaderIDs.UavDilatedReactiveMasks);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedVelocity);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvFrameInfo, Resources.FrameInfo);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvLumaHistory, Resources.LumaHistory[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvFarthestDepthMip1, Fsr3ShaderIDs.UavFarthestDepthMip1);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvCurrentLuma, Resources.Luma[frameIndex]);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavLumaHistory, Resources.LumaHistory[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavLumaInstability, Fsr3ShaderIDs.UavIntermediate);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
    
    internal class Fsr3UpscalerAccumulatePass : Fsr3UpscalerPass
    {
        private const string SharpeningKeyword = "FFX_FSR3UPSCALER_OPTION_APPLY_SHARPENING";
    
#if UNITY_2021_2_OR_NEWER
        private readonly LocalKeyword _sharpeningKeyword;
#endif
        
        public Fsr3UpscalerAccumulatePass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Accumulate", contextDescription.Shaders.accumulatePass);
#if UNITY_2021_2_OR_NEWER
            _sharpeningKeyword = new LocalKeyword(ComputeShader, SharpeningKeyword);
#endif
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
#if UNITY_2021_2_OR_NEWER
            if (dispatchParams.EnableSharpening)
                commandBuffer.EnableKeyword(ComputeShader, _sharpeningKeyword);
            else
                commandBuffer.DisableKeyword(ComputeShader, _sharpeningKeyword);
#else
            if (dispatchParams.EnableSharpening)
                commandBuffer.EnableShaderKeyword(SharpeningKeyword);
            else
                commandBuffer.DisableShaderKeyword(SharpeningKeyword);
#endif
            
            ref var color = ref dispatchParams.Color;
            ref var exposure = ref dispatchParams.Exposure;
            ref var output = ref dispatchParams.Output;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedReactiveMasks, Fsr3ShaderIDs.UavDilatedReactiveMasks);
            
            if ((ContextDescription.Flags & Fsr3Upscaler.InitializationFlags.EnableDisplayResolutionMotionVectors) == 0)
            {
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedVelocity);
            }
            else
            {
                ref var motionVectors = ref dispatchParams.MotionVectors;
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            }

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInternalUpscaled, Resources.InternalUpscaled[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvLanczosLut, Resources.LanczosLut);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvFarthestDepthMip1, Fsr3ShaderIDs.UavFarthestDepthMip1);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvCurrentLuma, Resources.Luma[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvLumaInstability, Fsr3ShaderIDs.UavIntermediate);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavInternalUpscaled, Resources.InternalUpscaled[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavUpscaledOutput, output.RenderTarget, output.MipLevel, output.SubElement);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr3UpscalerSharpenPass : Fsr3UpscalerPass
    {
        private readonly ComputeBuffer _rcasConstants;

        public Fsr3UpscalerSharpenPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants, ComputeBuffer rcasConstants)
            : base(contextDescription, resources, constants)
        {
            _rcasConstants = rcasConstants;
            
            InitComputeShader("RCAS Sharpening", contextDescription.Shaders.sharpenPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var exposure = ref dispatchParams.Exposure;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvRcasInput, Resources.InternalUpscaled[frameIndex]);
            
            ref var output = ref dispatchParams.Output;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavUpscaledOutput, output.RenderTarget, output.MipLevel, output.SubElement);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbRcas, _rcasConstants, 0, Marshal.SizeOf<Fsr3Upscaler.RcasConstants>());

            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr3UpscalerGenerateReactivePass : Fsr3UpscalerPass
    {
        private readonly ComputeBuffer _generateReactiveConstants;

        public Fsr3UpscalerGenerateReactivePass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer generateReactiveConstants)
            : base(contextDescription, resources, null)
        {
            _generateReactiveConstants = generateReactiveConstants;
            
            InitComputeShader("Auto-Generate Reactive Mask", contextDescription.Shaders.autoGenReactivePass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
        }

        public void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.GenerateReactiveDescription dispatchParams, int dispatchX, int dispatchY)
        {
            BeginSample(commandBuffer);
            
            ref var opaqueOnly = ref dispatchParams.ColorOpaqueOnly;
            ref var color = ref dispatchParams.ColorPreUpscale;
            ref var reactive = ref dispatchParams.OutReactive;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvOpaqueOnly, opaqueOnly.RenderTarget, opaqueOnly.MipLevel, opaqueOnly.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavAutoReactive, reactive.RenderTarget, reactive.MipLevel, reactive.SubElement);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbGenReactive, _generateReactiveConstants, 0, Marshal.SizeOf<Fsr3Upscaler.GenerateReactiveConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
            
            EndSample(commandBuffer);
        }
    }

    internal class Fsr3UpscalerTcrAutogeneratePass : Fsr3UpscalerPass
    {
        private readonly ComputeBuffer _tcrAutogenerateConstants;

        public Fsr3UpscalerTcrAutogeneratePass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants, ComputeBuffer tcrAutogenerateConstants)
            : base(contextDescription, resources, constants)
        {
            _tcrAutogenerateConstants = tcrAutogenerateConstants;
            
            InitComputeShader("Auto-Generate Transparency & Composition Mask", contextDescription.Shaders.tcrAutoGenPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var color = ref dispatchParams.Color;
            ref var motionVectors = ref dispatchParams.MotionVectors;
            ref var opaqueOnly = ref dispatchParams.ColorOpaqueOnly;
            ref var reactive = ref dispatchParams.Reactive;
            ref var tac = ref dispatchParams.TransparencyAndComposition;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvOpaqueOnly, opaqueOnly.RenderTarget, opaqueOnly.MipLevel, opaqueOnly.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvPrevColorPreAlpha, Resources.PrevPreAlpha[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvPrevColorPostAlpha, Resources.PrevPostAlpha[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvReactiveMask, reactive.RenderTarget, reactive.MipLevel, reactive.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvTransparencyAndCompositionMask, tac.RenderTarget, tac.MipLevel, tac.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavAutoReactive, Resources.AutoReactive);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavAutoComposition, Resources.AutoComposition);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavPrevColorPreAlpha, Resources.PrevPreAlpha[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavPrevColorPostAlpha, Resources.PrevPostAlpha[frameIndex]);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbGenReactive, _tcrAutogenerateConstants, 0, Marshal.SizeOf<Fsr3Upscaler.GenerateReactiveConstants2>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    internal class Fsr3UpscalerDebugViewPass : Fsr3UpscalerPass
    {
        public Fsr3UpscalerDebugViewPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Debug View", contextDescription.Shaders.debugViewPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var exposure = ref dispatchParams.Exposure;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedReactiveMasks, Fsr3ShaderIDs.UavDilatedReactiveMasks);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedVelocity);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedDepth, Resources.DilatedDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInternalUpscaled, Resources.InternalUpscaled[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);
            
            ref var output = ref dispatchParams.Output;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavUpscaledOutput, output.RenderTarget, output.MipLevel, output.SubElement);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
#endif
}
