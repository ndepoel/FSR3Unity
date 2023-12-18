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
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FidelityFX
{
    /// <summary>
    /// Base class for all of the compute passes that make up the FSR3 Upscaler process.
    /// This loosely matches the FfxPipelineState struct from the original FSR3 codebase, wrapped in an object-oriented blanket.
    /// These classes are responsible for loading compute shaders, managing temporary resources, binding resources to shader kernels and dispatching said shaders.
    /// </summary>
    internal abstract class Fsr3UpscalerPass: IDisposable
    {
        internal const int ShadingChangeMipLevel = 4;   // This matches the FFX_FSR3UPSCALER_SHADING_CHANGE_MIP_LEVEL define

        protected readonly Fsr3Upscaler.ContextDescription ContextDescription;
        protected readonly Fsr3UpscalerResources Resources;
        protected readonly ComputeBuffer Constants;
        
        protected ComputeShader ComputeShader;
        protected int KernelIndex;
        
        protected Fsr3UpscalerPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
        {
            ContextDescription = contextDescription;
            Resources = resources;
            Constants = constants;
        }

        public virtual void Dispose()
        {
        }

        public abstract void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY);

        protected void InitComputeShader(string passName, ComputeShader shader)
        {
            InitComputeShader(passName, shader, ContextDescription.Flags);
        }
        
        private void InitComputeShader(string passName, ComputeShader shader, Fsr3Upscaler.InitializationFlags flags)
        {
            if (shader == null)
            {
                throw new MissingReferenceException($"Shader for FSR3 Upscaler '{passName}' could not be loaded! Please ensure it is included in the project correctly.");
            }

            ComputeShader = shader;
            KernelIndex = ComputeShader.FindKernel("CS");

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

            // Inform the shader which render pipeline we're currently using
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.GetType().Name.Contains("HDRenderPipeline"))
            {
                ComputeShader.EnableKeyword("UNITY_FSR3UPSCALER_HDRP");
            }
        }
    }

    internal class Fsr3UpscalerComputeLuminancePyramidPass : Fsr3UpscalerPass
    {
        private readonly ComputeBuffer _spdConstants;
        
        public Fsr3UpscalerComputeLuminancePyramidPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants, ComputeBuffer spdConstants)
            : base(contextDescription, resources, constants)
        {
            _spdConstants = spdConstants;
            
            InitComputeShader("compute_luminance_pyramid_pass", contextDescription.Shaders.computeLuminancePyramidPass);
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var color = ref dispatchParams.Color;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavSpdAtomicCount, Resources.SpdAtomicCounter);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavExposureMipLumaChange, Resources.SceneLuminance, ShadingChangeMipLevel);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavExposureMip5, Resources.SceneLuminance, 5);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavAutoExposure, Resources.AutoExposure);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbSpd, _spdConstants, 0, Marshal.SizeOf<Fsr3Upscaler.SpdConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr3UpscalerReconstructPreviousDepthPass : Fsr3UpscalerPass
    {
        public Fsr3UpscalerReconstructPreviousDepthPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("reconstruct_previous_depth_pass", contextDescription.Shaders.reconstructPreviousDepthPass);
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var color = ref dispatchParams.Color;
            ref var depth = ref dispatchParams.Depth;
            ref var motionVectors = ref dispatchParams.MotionVectors;
            ref var exposure = ref dispatchParams.Exposure;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputDepth, depth.RenderTarget, depth.MipLevel, depth.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex]);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
    
    internal class Fsr3UpscalerDepthClipPass : Fsr3UpscalerPass
    {
        public Fsr3UpscalerDepthClipPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("depth_clip_pass", contextDescription.Shaders.depthClipPass);
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            ref var color = ref dispatchParams.Color;
            ref var depth = ref dispatchParams.Depth;
            ref var motionVectors = ref dispatchParams.MotionVectors;
            ref var exposure = ref dispatchParams.Exposure;
            ref var reactive = ref dispatchParams.Reactive;
            ref var tac = ref dispatchParams.TransparencyAndComposition;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputDepth, depth.RenderTarget, depth.MipLevel, depth.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvReactiveMask, reactive.RenderTarget, reactive.MipLevel, reactive.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvTransparencyAndCompositionMask, tac.RenderTarget, tac.MipLevel, tac.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvReconstructedPrevNearestDepth, Fsr3ShaderIDs.UavReconstructedPrevNearestDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedDepth, Fsr3ShaderIDs.UavDilatedDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvPrevDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex ^ 1]);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbFsr3Upscaler, Constants, 0, Marshal.SizeOf<Fsr3Upscaler.UpscalerConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr3UpscalerLockPass : Fsr3UpscalerPass
    {
        public Fsr3UpscalerLockPass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            InitComputeShader("lock_pass", contextDescription.Shaders.lockPass);
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvLockInputLuma, Fsr3ShaderIDs.UavLockInputLuma);
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
            InitComputeShader("accumulate_pass", contextDescription.Shaders.accumulatePass);
#if UNITY_2021_2_OR_NEWER
            _sharpeningKeyword = new LocalKeyword(ComputeShader, SharpeningKeyword);
#endif
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
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
            
            if ((ContextDescription.Flags & Fsr3Upscaler.InitializationFlags.EnableDisplayResolutionMotionVectors) == 0)
            {
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex]);
            }
            else
            {
                ref var motionVectors = ref dispatchParams.MotionVectors;
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputMotionVectors, motionVectors.RenderTarget, motionVectors.MipLevel, motionVectors.SubElement);
            }

            ref var exposure = ref dispatchParams.Exposure;
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputExposure, exposure.RenderTarget, exposure.MipLevel, exposure.SubElement);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvDilatedReactiveMasks, Fsr3ShaderIDs.UavDilatedReactiveMasks);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInternalUpscaled, Resources.InternalUpscaled[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvLockStatus, Resources.LockStatus[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvPreparedInputColor, Fsr3ShaderIDs.UavPreparedInputColor);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvLanczosLut, Resources.LanczosLut);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvUpscaleMaximumBiasLut, Resources.MaximumBiasLut);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvSceneLuminanceMips, Resources.SceneLuminance);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvAutoExposure, Resources.AutoExposure);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvLumaHistory, Resources.LumaHistory[frameIndex ^ 1]);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavInternalUpscaled, Resources.InternalUpscaled[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavLockStatus, Resources.LockStatus[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavLumaHistory, Resources.LumaHistory[frameIndex]);
            
            ref var output = ref dispatchParams.Output;
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
            
            InitComputeShader("rcas_pass", contextDescription.Shaders.sharpenPass);
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
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
            
            InitComputeShader("autogen_reactive_pass", contextDescription.Shaders.autoGenReactivePass);
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
        }

        public void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.GenerateReactiveDescription dispatchParams, int dispatchX, int dispatchY)
        {
            ref var opaqueOnly = ref dispatchParams.ColorOpaqueOnly;
            ref var color = ref dispatchParams.ColorPreUpscale;
            ref var reactive = ref dispatchParams.OutReactive;
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvOpaqueOnly, opaqueOnly.RenderTarget, opaqueOnly.MipLevel, opaqueOnly.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.SrvInputColor, color.RenderTarget, color.MipLevel, color.SubElement);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr3ShaderIDs.UavAutoReactive, reactive.RenderTarget, reactive.MipLevel, reactive.SubElement);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr3ShaderIDs.CbGenReactive, _generateReactiveConstants, 0, Marshal.SizeOf<Fsr3Upscaler.GenerateReactiveConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr3UpscalerTcrAutogeneratePass : Fsr3UpscalerPass
    {
        private readonly ComputeBuffer _tcrAutogenerateConstants;

        public Fsr3UpscalerTcrAutogeneratePass(Fsr3Upscaler.ContextDescription contextDescription, Fsr3UpscalerResources resources, ComputeBuffer constants, ComputeBuffer tcrAutogenerateConstants)
            : base(contextDescription, resources, constants)
        {
            _tcrAutogenerateConstants = tcrAutogenerateConstants;
            
            InitComputeShader("tcr_autogen_pass", contextDescription.Shaders.tcrAutoGenPass);
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr3Upscaler.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
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
}
