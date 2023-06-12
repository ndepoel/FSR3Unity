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
    /// Base class for all of the compute passes that make up the FSR2 process.
    /// This loosely matches the FfxPipelineState struct from the original FSR2 codebase, wrapped in an object-oriented blanket.
    /// These classes are responsible for loading compute shaders, managing temporary resources, binding resources to shader kernels and dispatching said shaders.
    /// </summary>
    internal abstract class Fsr2Pipeline: IDisposable
    {
        internal const int ShadingChangeMipLevel = 4;   // This matches the FFX_FSR2_SHADING_CHANGE_MIP_LEVEL define

        protected readonly Fsr2.ContextDescription ContextDescription;
        protected readonly Fsr2Resources Resources;
        protected readonly ComputeBuffer Constants;
        
        protected ComputeShader ComputeShader;
        protected int KernelIndex;
        
        protected virtual bool AllowFP16 => true;

        protected Fsr2Pipeline(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
        {
            ContextDescription = contextDescription;
            Resources = resources;
            Constants = constants;
        }

        public virtual void Dispose()
        {
            UnloadComputeShader();
        }

        public abstract void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY);

        protected void LoadComputeShader(string name)
        {
            LoadComputeShader(name, ContextDescription.Flags, ref ComputeShader, out KernelIndex);
        }
        
        private void LoadComputeShader(string name, Fsr2.InitializationFlags flags, ref ComputeShader shaderRef, out int kernelIndex)
        {
            if (shaderRef == null)
            {
                shaderRef = ContextDescription.Callbacks.LoadComputeShader(name);
                if (shaderRef == null)
                    throw new MissingReferenceException($"Shader '{name}' could not be loaded! Please ensure it is included in the project correctly.");
            }

            kernelIndex = shaderRef.FindKernel("CS");

            bool useLut = false;
#if UNITY_2022_1_OR_NEWER   // This will also work in 2020.3.43+ and 2021.3.14+ 
            if (SystemInfo.computeSubGroupSize == 64)
            {
                useLut = true;
            }
#endif
            
            // Allow 16-bit floating point as a configuration option, except on passes that explicitly disable it
            bool supportedFP16 = ((flags & Fsr2.InitializationFlags.EnableFP16Usage) != 0 && AllowFP16);

            // This matches the permutation rules from the CreatePipeline* functions
            if ((flags & Fsr2.InitializationFlags.EnableHighDynamicRange) != 0) shaderRef.EnableKeyword("FFX_FSR2_OPTION_HDR_COLOR_INPUT");
            if ((flags & Fsr2.InitializationFlags.EnableDisplayResolutionMotionVectors) == 0) shaderRef.EnableKeyword("FFX_FSR2_OPTION_LOW_RESOLUTION_MOTION_VECTORS");
            if ((flags & Fsr2.InitializationFlags.EnableMotionVectorsJitterCancellation) != 0) shaderRef.EnableKeyword("FFX_FSR2_OPTION_JITTERED_MOTION_VECTORS");
            if ((flags & Fsr2.InitializationFlags.EnableDepthInverted) != 0) shaderRef.EnableKeyword("FFX_FSR2_OPTION_INVERTED_DEPTH");
            if (useLut) shaderRef.EnableKeyword("FFX_FSR2_OPTION_REPROJECT_USE_LANCZOS_TYPE");
            if (supportedFP16) shaderRef.EnableKeyword("FFX_HALF");

            // Inform the shader which render pipeline we're currently using
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.GetType().Name.Contains("HDRenderPipeline"))
            {
                shaderRef.EnableKeyword("UNITY_FSR2_HDRP");
            }
        }

        private void UnloadComputeShader()
        {
            UnloadComputeShader(ref ComputeShader);
        }
        
        private void UnloadComputeShader(ref ComputeShader shaderRef)
        {
            if (shaderRef == null)
                return;

            ContextDescription.Callbacks.UnloadComputeShader(shaderRef);
            shaderRef = null;
        }
    }

    internal class Fsr2ComputeLuminancePyramidPipeline : Fsr2Pipeline
    {
        private readonly ComputeBuffer _spdConstants;
        
        public Fsr2ComputeLuminancePyramidPipeline(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants, ComputeBuffer spdConstants)
            : base(contextDescription, resources, constants)
        {
            _spdConstants = spdConstants;
            
            LoadComputeShader("FSR2/ffx_fsr2_compute_luminance_pyramid_pass");
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            if (dispatchParams.Color.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, dispatchParams.Color.Value, 0, RenderTextureSubElement.Color);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavSpdAtomicCount, Resources.SpdAtomicCounter);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavExposureMipLumaChange, Resources.SceneLuminance, ShadingChangeMipLevel);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavExposureMip5, Resources.SceneLuminance, 5);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavAutoExposure, Resources.AutoExposure);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.Fsr2Constants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbSpd, _spdConstants, 0, Marshal.SizeOf<Fsr2.SpdConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr2ReconstructPreviousDepthPipeline : Fsr2Pipeline
    {
        public Fsr2ReconstructPreviousDepthPipeline(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            LoadComputeShader("FSR2/ffx_fsr2_reconstruct_previous_depth_pass");
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            if (dispatchParams.Color.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, dispatchParams.Color.Value, 0, RenderTextureSubElement.Color);
            
            if (dispatchParams.Depth.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputDepth, dispatchParams.Depth.Value, 0, RenderTextureSubElement.Depth);
            
            if (dispatchParams.MotionVectors.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputMotionVectors, dispatchParams.MotionVectors.Value);
            
            if (dispatchParams.Exposure.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputExposure, dispatchParams.Exposure.Value);
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex]);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.Fsr2Constants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
    
    internal class Fsr2DepthClipPipeline : Fsr2Pipeline
    {
        public Fsr2DepthClipPipeline(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            LoadComputeShader("FSR2/ffx_fsr2_depth_clip_pass");
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            if (dispatchParams.Color.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, dispatchParams.Color.Value, 0, RenderTextureSubElement.Color);
            
            if (dispatchParams.Depth.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputDepth, dispatchParams.Depth.Value, 0, RenderTextureSubElement.Depth);

            if (dispatchParams.MotionVectors.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputMotionVectors, dispatchParams.MotionVectors.Value);

            if (dispatchParams.Exposure.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputExposure, dispatchParams.Exposure.Value);

            if (dispatchParams.Reactive.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvReactiveMask, dispatchParams.Reactive.Value);
            
            if (dispatchParams.TransparencyAndComposition.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvTransparencyAndCompositionMask, dispatchParams.TransparencyAndComposition.Value);
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvReconstructedPrevNearestDepth, Fsr2ShaderIDs.UavReconstructedPrevNearestDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvDilatedDepth, Fsr2ShaderIDs.UavDilatedDepth);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvPrevDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex ^ 1]);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.Fsr2Constants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr2LockPipeline : Fsr2Pipeline
    {
        public Fsr2LockPipeline(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            LoadComputeShader("FSR2/ffx_fsr2_lock_pass");
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvLockInputLuma, Fsr2ShaderIDs.UavLockInputLuma);
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.Fsr2Constants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
    
    internal class Fsr2AccumulatePipeline : Fsr2Pipeline
    {
        private const string SharpeningKeyword = "FFX_FSR2_OPTION_APPLY_SHARPENING";
        
        // Workaround: Disable FP16 path for the accumulate pass on NVIDIA due to reduced occupancy and high VRAM throughput.
        protected override bool AllowFP16 => SystemInfo.graphicsDeviceVendorID != 0x10DE;
    
#if UNITY_2021_2_OR_NEWER
        private readonly LocalKeyword _sharpeningKeyword;
#endif
        
        public Fsr2AccumulatePipeline(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants)
            : base(contextDescription, resources, constants)
        {
            LoadComputeShader("FSR2/ffx_fsr2_accumulate_pass");
#if UNITY_2021_2_OR_NEWER
            _sharpeningKeyword = new LocalKeyword(ComputeShader, SharpeningKeyword);
#endif
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
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
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvDilatedMotionVectors, Resources.DilatedMotionVectors[frameIndex]);
            else if (dispatchParams.MotionVectors.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputMotionVectors, dispatchParams.MotionVectors.Value);
            
            if (dispatchParams.Exposure.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputExposure, dispatchParams.Exposure.Value);
            
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
            
            if (dispatchParams.Output.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavUpscaledOutput, dispatchParams.Output.Value);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.Fsr2Constants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr2RcasPipeline : Fsr2Pipeline
    {
        private readonly ComputeBuffer _rcasConstants;

        public Fsr2RcasPipeline(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants, ComputeBuffer rcasConstants)
            : base(contextDescription, resources, constants)
        {
            _rcasConstants = rcasConstants;
            
            LoadComputeShader("FSR2/ffx_fsr2_rcas_pass");
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            if (dispatchParams.Exposure.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputExposure, dispatchParams.Exposure.Value);
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvRcasInput, Resources.InternalUpscaled[frameIndex]);
            
            if (dispatchParams.Output.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavUpscaledOutput, dispatchParams.Output.Value);

            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.Fsr2Constants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbRcas, _rcasConstants, 0, Marshal.SizeOf<Fsr2.RcasConstants>());

            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr2GenerateReactivePipeline : Fsr2Pipeline
    {
        private readonly ComputeBuffer _generateReactiveConstants;

        public Fsr2GenerateReactivePipeline(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer generateReactiveConstants)
            : base(contextDescription, resources, null)
        {
            _generateReactiveConstants = generateReactiveConstants;
            
            LoadComputeShader("FSR2/ffx_fsr2_autogen_reactive_pass");
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
        }

        public void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.GenerateReactiveDescription dispatchParams, int dispatchX, int dispatchY)
        {
            if (dispatchParams.ColorOpaqueOnly.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvOpaqueOnly, dispatchParams.ColorOpaqueOnly.Value, 0, RenderTextureSubElement.Color);
            
            if (dispatchParams.ColorPreUpscale.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, dispatchParams.ColorPreUpscale.Value, 0, RenderTextureSubElement.Color);
            
            if (dispatchParams.OutReactive.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavAutoReactive, dispatchParams.OutReactive.Value);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbGenReactive, _generateReactiveConstants, 0, Marshal.SizeOf<Fsr2.GenerateReactiveConstants>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }

    internal class Fsr2TcrAutogeneratePipeline : Fsr2Pipeline
    {
        private readonly ComputeBuffer _tcrAutogenerateConstants;

        public Fsr2TcrAutogeneratePipeline(Fsr2.ContextDescription contextDescription, Fsr2Resources resources, ComputeBuffer constants, ComputeBuffer tcrAutogenerateConstants)
            : base(contextDescription, resources, constants)
        {
            _tcrAutogenerateConstants = tcrAutogenerateConstants;
            
            LoadComputeShader("FSR2/ffx_fsr2_tcr_autogen_pass");
        }

        public override void ScheduleDispatch(CommandBuffer commandBuffer, Fsr2.DispatchDescription dispatchParams, int frameIndex, int dispatchX, int dispatchY)
        {
            if (dispatchParams.ColorOpaqueOnly.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvOpaqueOnly, dispatchParams.ColorOpaqueOnly.Value, 0, RenderTextureSubElement.Color);
            
            if (dispatchParams.Color.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputColor, dispatchParams.Color.Value, 0, RenderTextureSubElement.Color);
            
            if (dispatchParams.MotionVectors.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvInputMotionVectors, dispatchParams.MotionVectors.Value);
            
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvPrevColorPreAlpha, Resources.PrevPreAlpha[frameIndex ^ 1]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvPrevColorPostAlpha, Resources.PrevPostAlpha[frameIndex ^ 1]);

            if (dispatchParams.Reactive.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvReactiveMask, dispatchParams.Reactive.Value);

            if (dispatchParams.TransparencyAndComposition.HasValue)
                commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.SrvTransparencyAndCompositionMask, dispatchParams.TransparencyAndComposition.Value);

            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavAutoReactive, Resources.AutoReactive);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavAutoComposition, Resources.AutoComposition);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavPrevColorPreAlpha, Resources.PrevPreAlpha[frameIndex]);
            commandBuffer.SetComputeTextureParam(ComputeShader, KernelIndex, Fsr2ShaderIDs.UavPrevColorPostAlpha, Resources.PrevPostAlpha[frameIndex]);
            
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbFsr2, Constants, 0, Marshal.SizeOf<Fsr2.Fsr2Constants>());
            commandBuffer.SetComputeConstantBufferParam(ComputeShader, Fsr2ShaderIDs.CbGenReactive, _tcrAutogenerateConstants, 0, Marshal.SizeOf<Fsr2.GenerateReactiveConstants2>());
            
            commandBuffer.DispatchCompute(ComputeShader, KernelIndex, dispatchX, dispatchY, 1);
        }
    }
}
