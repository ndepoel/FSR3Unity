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

namespace FidelityFX.FSR2
{
    /// <summary>
    /// Base class for all of the compute passes that make up the FSR2 process.
    /// This loosely matches the FfxPipelineState struct from the original FSR2 codebase, wrapped in an object-oriented blanket.
    /// These classes are responsible for loading compute shaders, managing temporary resources, binding resources to shader kernels and dispatching said shaders.
    /// </summary>
    internal abstract class Fsr2Pass: IDisposable
    {
        internal const int ShadingChangeMipLevel = 4;   // This matches the FFX_FSR2_SHADING_CHANGE_MIP_LEVEL define

        protected readonly Fsr2.ContextDescription ContextDescription;
        protected readonly Fsr2Resources Resources;
        protected readonly ComputeBuffer Constants;
        
        protected ComputeShader ComputeShader;
        protected int KernelIndex;
        
        private CustomSampler _sampler;
        
        protected Fsr2Pass(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
        {
            ContextDescription = contextDescription;
            Resources = resources;
            Constants = constants;
        }

        public virtual void Dispose()
        {
        }
        
        public void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            BeginSample(commandBuffer);
            DoScheduleDispatch(commandBuffer, dispatchParams, frameIndex, dispatchX, dispatchY);
            EndSample(commandBuffer);
        }

        protected abstract void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY);

        protected void InitComputeShader(string passName, ComputeShader shader)
        {
            InitComputeShader(passName, shader, ContextDescription.Flags);
        }
        
        private void InitComputeShader(string passName, ComputeShader shader, Fsr2.InitializationFlags flags)
        {
            if (shader == null)
            {
                throw new MissingReferenceException($"Shader for FSR2 pass '{passName}' could not be loaded! Please ensure it is included in the project correctly.");
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
            if ((flags & Fsr2.InitializationFlags.EnableHighDynamicRange) != 0) ComputeShader.EnableKeyword("FFX_FSR2_OPTION_HDR_COLOR_INPUT");
            if ((flags & Fsr2.InitializationFlags.EnableDisplayResolutionMotionVectors) == 0) ComputeShader.EnableKeyword("FFX_FSR2_OPTION_LOW_RESOLUTION_MOTION_VECTORS");
            if ((flags & Fsr2.InitializationFlags.EnableMotionVectorsJitterCancellation) != 0) ComputeShader.EnableKeyword("FFX_FSR2_OPTION_JITTERED_MOTION_VECTORS");
            if ((flags & Fsr2.InitializationFlags.EnableDepthInverted) != 0) ComputeShader.EnableKeyword("FFX_FSR2_OPTION_INVERTED_DEPTH");
            if (useLut) ComputeShader.EnableKeyword("FFX_FSR2_OPTION_REPROJECT_USE_LANCZOS_TYPE");
            if ((flags & Fsr2.InitializationFlags.EnableFP16Usage) != 0) ComputeShader.EnableKeyword("FFX_HALF");
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

    internal class Fsr2ComputeLuminancePyramidPass : Fsr2Pass
    {
        private readonly ComputeBuffer _spdConstants;
        
        public Fsr2ComputeLuminancePyramidPass(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants, ComputeBuffer spdConstants)
            : base(contextDescription, resources, constants)
        {
            _spdConstants = spdConstants;
            
            InitComputeShader("Compute Luminance Pyramid", contextDescription.Shaders.computeLuminancePyramidPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var color = ref dispatchParams.Color;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavSpdAtomicCount, Resources.SpdAtomicCounter);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavExposureMipLumaChange, Resources.SceneLuminance, ShadingChangeMipLevel);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavExposureMip5, Resources.SceneLuminance, 5);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavAutoExposure, Resources.AutoExposure);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.UpscalerConstants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbSpd, _spdConstants, 0, Marshal.SizeOf<Fsr2.SpdConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr2ReconstructPreviousDepthPass : Fsr2Pass
    {
        public Fsr2ReconstructPreviousDepthPass(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Reconstruct & Dilate", contextDescription.Shaders.reconstructPreviousDepthPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var color = ref dispatchParams.Color;
            ref var depth = ref dispatchParams.Depth;
            ref var motionVectors = ref dispatchParams.MotionVectors;
            ref var exposure = ref dispatchParams.Exposure;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputDepth, depth.RenderTarget, depth.MipLevel, depth.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex]);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
    
    internal class Fsr2DepthClipPass : Fsr2Pass
    {
        public Fsr2DepthClipPass(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Depth Clip", contextDescription.Shaders.depthClipPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var color = ref dispatchParams.Color;
            ref var depth = ref dispatchParams.Depth;
            ref var motionVectors = ref dispatchParams.MotionVectors;
            ref var exposure = ref dispatchParams.Exposure;
            ref var reactive = ref dispatchParams.Reactive;
            ref var tac = ref dispatchParams.TransparencyAndComposition;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputDepth, depth.RenderTarget, depth.MipLevel, depth.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvReactiveMask, reactive.RenderTarget, reactive.MipLevel, reactive.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvTransparencyAndCompositionMask, tac.RenderTarget, tac.MipLevel, tac.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvReconstructedPrevNearestDepth, Fsr2ShaderIDs.UavReconstructedPrevNearestDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvDilatedDepth, Fsr2ShaderIDs.UavDilatedDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvPrevDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex ^ 1]);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr2LockPass : Fsr2Pass
    {
        public Fsr2LockPass(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Create Locks", contextDescription.Shaders.lockPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvLockInputLuma, Fsr2ShaderIDs.UavLockInputLuma);
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
    
    internal class Fsr2AccumulatePass : Fsr2Pass
    {
        private const string SharpeningKeyword = "FFX_FSR2_OPTION_APPLY_SHARPENING";
    
#if UNITY_2021_2_OR_NEWER
        private readonly LocalKeyword _sharpeningKeyword;
#endif
        
        public Fsr2AccumulatePass(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("Reproject & Accumulate", contextDescription.Shaders.accumulatePass);
#if UNITY_2021_2_OR_NEWER
            _sharpeningKeyword = new LocalKeyword(ComputeShader, SharpeningKeyword);
#endif
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
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
            
            if ((ContextDescription.Flags & Fsr2.InitializationFlags.EnableDisplayResolutionMotionVectors) == 0)
            {
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex]);
            }
            else
            {
                ref var motionVectors = ref dispatchParams.MotionVectors;
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            }

            ref var exposure = ref dispatchParams.Exposure;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvDilatedReactiveMasks, Fsr2ShaderIDs.UavDilatedReactiveMasks);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInternalUpscaled, Resources.InternalUpscaled[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvLockStatus, Resources.LockStatus[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvPreparedInputColor, Fsr2ShaderIDs.UavPreparedInputColor);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvLanczosLut, Resources.LanczosLut);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvUpscaleMaximumBiasLut, Resources.MaximumBiasLut);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvSceneLuminanceMips, Resources.SceneLuminance);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvAutoExposure, Resources.AutoExposure);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvLumaHistory, Resources.LumaHistory[frameIndex ^ 1]);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavInternalUpscaled, Resources.InternalUpscaled[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavLockStatus, Resources.LockStatus[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavLumaHistory, Resources.LumaHistory[frameIndex]);
            
            ref var output = ref dispatchParams.Output;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavUpscaledOutput, output.RenderTarget, output.MipLevel, output.SubElement);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr2SharpenPass : Fsr2Pass
    {
        private readonly ComputeBuffer _rcasConstants;

        public Fsr2SharpenPass(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants, ComputeBuffer rcasConstants)
            : base(contextDescription, resources, constants)
        {
            _rcasConstants = rcasConstants;
            
            InitComputeShader("RCAS Sharpening", contextDescription.Shaders.sharpenPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var exposure = ref dispatchParams.Exposure;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvRcasInput, Resources.InternalUpscaled[frameIndex]);
            
            ref var output = ref dispatchParams.Output;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavUpscaledOutput, output.RenderTarget, output.MipLevel, output.SubElement);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.UpscalerConstants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbRcas, _rcasConstants, 0, Marshal.SizeOf<Fsr2.RcasConstants>());

            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr2GenerateReactivePass : Fsr2Pass
    {
        private readonly ComputeBuffer _generateReactiveConstants;

        public Fsr2GenerateReactivePass(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer generateReactiveConstants)
            : base(contextDescription, resources, null)
        {
            _generateReactiveConstants = generateReactiveConstants;
            
            InitComputeShader("Auto-Generate Reactive Mask", contextDescription.Shaders.autoGenReactivePass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
        }

        public void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.GenerateReactiveDescription dispatchParams, int dispatchX, int dispatchY)
        {
            BeginSample(commandBuffer);
            
            ref var opaqueOnly = ref dispatchParams.ColorOpaqueOnly;
            ref var color = ref dispatchParams.ColorPreUpscale;
            ref var reactive = ref dispatchParams.OutReactive;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvOpaqueOnly, opaqueOnly.RenderTarget, opaqueOnly.MipLevel, opaqueOnly.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavAutoReactive, reactive.RenderTarget, reactive.MipLevel, reactive.SubElement);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbGenReactive, _generateReactiveConstants, 0, Marshal.SizeOf<Fsr2.GenerateReactiveConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
            
            EndSample(commandBuffer);
        }
    }

    internal class Fsr2TcrAutogeneratePass : Fsr2Pass
    {
        private readonly ComputeBuffer _tcrAutogenerateConstants;

        public Fsr2TcrAutogeneratePass(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants, ComputeBuffer tcrAutogenerateConstants)
            : base(contextDescription, resources, constants)
        {
            _tcrAutogenerateConstants = tcrAutogenerateConstants;
            
            InitComputeShader("Auto-Generate Transparency & Composition Mask", contextDescription.Shaders.tcrAutoGenPass);
        }

        protected override void DoScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var color = ref dispatchParams.Color;
            ref var motionVectors = ref dispatchParams.MotionVectors;
            ref var opaqueOnly = ref dispatchParams.ColorOpaqueOnly;
            ref var reactive = ref dispatchParams.Reactive;
            ref var tac = ref dispatchParams.TransparencyAndComposition;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvOpaqueOnly, opaqueOnly.RenderTarget, opaqueOnly.MipLevel, opaqueOnly.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvPrevColorPreAlpha, Resources.PrevPreAlpha[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvPrevColorPostAlpha, Resources.PrevPostAlpha[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvReactiveMask, reactive.RenderTarget, reactive.MipLevel, reactive.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvTransparencyAndCompositionMask, tac.RenderTarget, tac.MipLevel, tac.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavAutoReactive, Resources.AutoReactive);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavAutoComposition, Resources.AutoComposition);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavPrevColorPreAlpha, Resources.PrevPreAlpha[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavPrevColorPostAlpha, Resources.PrevPostAlpha[frameIndex]);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.UpscalerConstants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbGenReactive, _tcrAutogenerateConstants, 0, Marshal.SizeOf<Fsr2.GenerateReactiveConstants2>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
}
