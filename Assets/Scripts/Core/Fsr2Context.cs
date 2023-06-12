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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FidelityFX
{
    /// <summary>
    /// This class loosely matches the FfxFsr2Context struct from the original FSR2 codebase.
    /// It manages the various resources and compute passes required by the FSR2 process.
    /// Note that this class does not know anything about Unity render pipelines; all it knows is CommandBuffers and RenderTargetIdentifiers.
    /// This should make it suitable for integration with any of the available Unity render pipelines.
    /// </summary>
    public class Fsr2Context
    {
        private const int MaxQueuedFrames = 16;
        
        private Fsr2.ContextDescription _contextDescription;
        private CommandBuffer _commandBuffer;
        
        private Fsr2Pipeline _depthClipPipeline;
        private Fsr2Pipeline _reconstructPreviousDepthPipeline;
        private Fsr2Pipeline _lockPipeline;
        private Fsr2Pipeline _accumulatePipeline;
        private Fsr2Pipeline _rcasPipeline;
        private Fsr2Pipeline _computeLuminancePyramidPipeline;
        private Fsr2Pipeline _generateReactivePipeline;
        private Fsr2Pipeline _tcrAutogeneratePipeline;

        private readonly Fsr2Resources _resources = new Fsr2Resources();

        private ComputeBuffer _fsr2ConstantsBuffer;
        private readonly Fsr2.Fsr2Constants[] _fsr2ConstantsArray = { new Fsr2.Fsr2Constants() };
        private ref Fsr2.Fsr2Constants Constants => ref _fsr2ConstantsArray[0];

        private ComputeBuffer _spdConstantsBuffer;
        private readonly Fsr2.SpdConstants[] _spdConstantsArray = { new Fsr2.SpdConstants() };
        private ref Fsr2.SpdConstants SpdConsts => ref _spdConstantsArray[0];
    
        private ComputeBuffer _rcasConstantsBuffer;
        private readonly Fsr2.RcasConstants[] _rcasConstantsArray = new Fsr2.RcasConstants[1];
        private ref Fsr2.RcasConstants RcasConsts => ref _rcasConstantsArray[0];

        private ComputeBuffer _generateReactiveConstantsBuffer;
        private readonly Fsr2.GenerateReactiveConstants[] _generateReactiveConstantsArray = { new Fsr2.GenerateReactiveConstants() };
        private ref Fsr2.GenerateReactiveConstants GenReactiveConsts => ref _generateReactiveConstantsArray[0];

        private ComputeBuffer _tcrAutogenerateConstantsBuffer;
        private readonly Fsr2.GenerateReactiveConstants2[] _tcrAutogenerateConstantsArray = { new Fsr2.GenerateReactiveConstants2() };
        private ref Fsr2.GenerateReactiveConstants2 TcrAutoGenConsts => ref _tcrAutogenerateConstantsArray[0];

        private bool _firstExecution;
        private Vector2 _previousJitterOffset;
        private int _resourceFrameIndex;

        public void Create(Fsr2.ContextDescription contextDescription)
        {
            _contextDescription = contextDescription;
            _commandBuffer = new CommandBuffer { name = "FSR2" };
            
            _fsr2ConstantsBuffer = CreateConstantBuffer<Fsr2.Fsr2Constants>();
            _spdConstantsBuffer = CreateConstantBuffer<Fsr2.SpdConstants>();
            _rcasConstantsBuffer = CreateConstantBuffer<Fsr2.RcasConstants>();
            _generateReactiveConstantsBuffer = CreateConstantBuffer<Fsr2.GenerateReactiveConstants>();
            _tcrAutogenerateConstantsBuffer = CreateConstantBuffer<Fsr2.GenerateReactiveConstants2>();

            // Set defaults
            _firstExecution = true;
            _resourceFrameIndex = 0;
            
            Constants.displaySize = _contextDescription.DisplaySize;
            
            _resources.Create(_contextDescription);
            CreatePipelines();
        }

        private void CreatePipelines()
        {
            _computeLuminancePyramidPipeline = new Fsr2ComputeLuminancePyramidPipeline(_contextDescription, _resources, _fsr2ConstantsBuffer, _spdConstantsBuffer);
            _reconstructPreviousDepthPipeline = new Fsr2ReconstructPreviousDepthPipeline(_contextDescription, _resources, _fsr2ConstantsBuffer);
            _depthClipPipeline = new Fsr2DepthClipPipeline(_contextDescription, _resources, _fsr2ConstantsBuffer);
            _lockPipeline = new Fsr2LockPipeline(_contextDescription, _resources, _fsr2ConstantsBuffer);
            _accumulatePipeline = new Fsr2AccumulatePipeline(_contextDescription, _resources, _fsr2ConstantsBuffer);
            _rcasPipeline = new Fsr2RcasPipeline(_contextDescription, _resources, _fsr2ConstantsBuffer, _rcasConstantsBuffer);
            _generateReactivePipeline = new Fsr2GenerateReactivePipeline(_contextDescription, _resources, _generateReactiveConstantsBuffer);
            _tcrAutogeneratePipeline = new Fsr2TcrAutogeneratePipeline(_contextDescription, _resources, _fsr2ConstantsBuffer, _tcrAutogenerateConstantsBuffer);
        }
        
        public void Destroy()
        {
            DestroyPipeline(ref _tcrAutogeneratePipeline);
            DestroyPipeline(ref _generateReactivePipeline);
            DestroyPipeline(ref _computeLuminancePyramidPipeline);
            DestroyPipeline(ref _rcasPipeline);
            DestroyPipeline(ref _accumulatePipeline);
            DestroyPipeline(ref _lockPipeline);
            DestroyPipeline(ref _reconstructPreviousDepthPipeline);
            DestroyPipeline(ref _depthClipPipeline);
            
            _resources.Destroy();
            
            DestroyConstantBuffer(ref _tcrAutogenerateConstantsBuffer);
            DestroyConstantBuffer(ref _generateReactiveConstantsBuffer);
            DestroyConstantBuffer(ref _rcasConstantsBuffer);
            DestroyConstantBuffer(ref _spdConstantsBuffer);
            DestroyConstantBuffer(ref _fsr2ConstantsBuffer);

            _commandBuffer.Dispose();
            _commandBuffer = null;
        }

        public void Dispatch(Fsr2.DispatchDescription dispatchParams)
        {
            _commandBuffer.Clear();
            Dispatch(dispatchParams, _commandBuffer);
            Graphics.ExecuteCommandBuffer(_commandBuffer);
        }
        
        public void Dispatch(Fsr2.DispatchDescription dispatchParams, CommandBuffer commandBuffer)
        {
            if ((_contextDescription.Flags & Fsr2.InitializationFlags.EnableDebugChecking) != 0)
            {
                DebugCheckDispatch(dispatchParams);
            }
            
            if (_firstExecution)
            {
                commandBuffer.SetRenderTarget(_resources.LockStatus[0]);
                commandBuffer.ClearRenderTarget(false, true, Color.clear);
                commandBuffer.SetRenderTarget(_resources.LockStatus[1]);
                commandBuffer.ClearRenderTarget(false, true, Color.clear);
                
                // Reset atomic counter to 0
                commandBuffer.SetRenderTarget(_resources.SpdAtomicCounter);
                commandBuffer.ClearRenderTarget(false, true, Color.clear);
            }
            
            int frameIndex = _resourceFrameIndex % 2;
            bool resetAccumulation = dispatchParams.Reset || _firstExecution;
            _firstExecution = false;

            // If auto exposure is enabled use the auto exposure SRV, otherwise what the app sends
            if ((_contextDescription.Flags & Fsr2.InitializationFlags.EnableAutoExposure) != 0)
                dispatchParams.Exposure = _resources.AutoExposure;
            else if (dispatchParams.Exposure == null) 
                dispatchParams.Exposure = _resources.DefaultExposure;

            if (dispatchParams.EnableAutoReactive)
            {
                // Create the auto-TCR resources only when we need them
                if (_resources.AutoReactive == null)
                    _resources.CreateTcrAutogenResources(_contextDescription);
                
                if (resetAccumulation)
                    commandBuffer.Blit(_resources.PrevPreAlpha[frameIndex ^ 1], dispatchParams.ColorOpaqueOnly ?? Fsr2ShaderIDs.SrvOpaqueOnly);
            }
            else if (_resources.AutoReactive != null)
            {
                // Destroy the auto-TCR resources if we don't use the feature 
                _resources.DestroyTcrAutogenResources();
            }
            
            if (dispatchParams.Reactive == null) dispatchParams.Reactive = _resources.DefaultReactive;
            if (dispatchParams.TransparencyAndComposition == null) dispatchParams.TransparencyAndComposition = _resources.DefaultReactive;
            Fsr2Resources.CreateAliasableResources(commandBuffer, _contextDescription, dispatchParams);
            
            SetupConstants(dispatchParams, resetAccumulation);
            
            // Reactive mask bias
            const int threadGroupWorkRegionDim = 8;
            int dispatchSrcX = (Constants.renderSize.x + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchSrcY = (Constants.renderSize.y + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchDstX = (_contextDescription.DisplaySize.x + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchDstY = (_contextDescription.DisplaySize.y + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;

            // Clear reconstructed depth for max depth store
            if (resetAccumulation)
            {
                commandBuffer.SetRenderTarget(_resources.LockStatus[frameIndex ^ 1]);
                commandBuffer.ClearRenderTarget(false, true, Color.clear);
                
                commandBuffer.SetRenderTarget(_resources.InternalUpscaled[frameIndex ^ 1]);
                commandBuffer.ClearRenderTarget(false, true, Color.clear);
                
                commandBuffer.SetRenderTarget(_resources.SceneLuminance);
                commandBuffer.ClearRenderTarget(false, true, Color.clear);
                
                // Auto exposure always used to track luma changes in locking logic
                commandBuffer.SetRenderTarget(_resources.AutoExposure);
                commandBuffer.ClearRenderTarget(false, true, new Color(-1f, 1e8f, 0f, 0f));
            }
            
            // Auto exposure
            SetupSpdConstants(dispatchParams, out var dispatchThreadGroupCount);
            
            // Initialize constant buffers data
            _fsr2ConstantsBuffer.SetData(_fsr2ConstantsArray);
            _spdConstantsBuffer.SetData(_spdConstantsArray);

            // Auto reactive
            if (dispatchParams.EnableAutoReactive)
            {
                GenerateTransparencyCompositionReactive(dispatchParams, commandBuffer, frameIndex);
                dispatchParams.Reactive = _resources.AutoReactive;
                dispatchParams.TransparencyAndComposition = _resources.AutoComposition;
            }
            
            // Compute luminance pyramid
            _computeLuminancePyramidPipeline.ScheduleDispatch(commandBuffer, dispatchParams, frameIndex, dispatchThreadGroupCount.x, dispatchThreadGroupCount.y);

            // Reconstruct previous depth
            _reconstructPreviousDepthPipeline.ScheduleDispatch(commandBuffer, dispatchParams, frameIndex, dispatchSrcX, dispatchSrcY);

            // Depth clip
            _depthClipPipeline.ScheduleDispatch(commandBuffer, dispatchParams, frameIndex, dispatchSrcX, dispatchSrcY);

            // Create locks
            _lockPipeline.ScheduleDispatch(commandBuffer, dispatchParams, frameIndex, dispatchSrcX, dispatchSrcY);

            // Accumulate
            _accumulatePipeline.ScheduleDispatch(commandBuffer, dispatchParams, frameIndex, dispatchDstX, dispatchDstY);

            if (dispatchParams.EnableSharpening)
            {
                // Compute the constants
                SetupRcasConstants(dispatchParams);
                _rcasConstantsBuffer.SetData(_rcasConstantsArray);
                
                // Dispatch RCAS
                const int threadGroupWorkRegionDimRcas = 16;
                int threadGroupsX = (Screen.width + threadGroupWorkRegionDimRcas - 1) / threadGroupWorkRegionDimRcas;
                int threadGroupsY = (Screen.height + threadGroupWorkRegionDimRcas - 1) / threadGroupWorkRegionDimRcas;
                _rcasPipeline.ScheduleDispatch(commandBuffer, dispatchParams, frameIndex, threadGroupsX, threadGroupsY);
            }

            _resourceFrameIndex = (_resourceFrameIndex + 1) % MaxQueuedFrames;

            Fsr2Resources.DestroyAliasableResources(commandBuffer);
        }

        public void GenerateReactiveMask(Fsr2.GenerateReactiveDescription dispatchParams)
        {
            _commandBuffer.Clear();
            GenerateReactiveMask(dispatchParams, _commandBuffer);
            Graphics.ExecuteCommandBuffer(_commandBuffer);
        }

        public void GenerateReactiveMask(Fsr2.GenerateReactiveDescription dispatchParams, CommandBuffer commandBuffer)
        {
            const int threadGroupWorkRegionDim = 8;
            int dispatchSrcX = (dispatchParams.RenderSize.x + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchSrcY = (dispatchParams.RenderSize.y + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;

            GenReactiveConsts.scale = dispatchParams.Scale;
            GenReactiveConsts.threshold = dispatchParams.CutoffThreshold;
            GenReactiveConsts.binaryValue = dispatchParams.BinaryValue;
            GenReactiveConsts.flags = (uint)dispatchParams.Flags;
            _generateReactiveConstantsBuffer.SetData(_generateReactiveConstantsArray);
            
            ((Fsr2GenerateReactivePipeline)_generateReactivePipeline).ScheduleDispatch(commandBuffer, dispatchParams, dispatchSrcX, dispatchSrcY);
        }

        private void GenerateTransparencyCompositionReactive(Fsr2.DispatchDescription dispatchParams, CommandBuffer commandBuffer, int frameIndex)
        {
            const int threadGroupWorkRegionDim = 8;
            int dispatchSrcX = (dispatchParams.RenderSize.x + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchSrcY = (dispatchParams.RenderSize.y + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;

            TcrAutoGenConsts.autoTcThreshold = dispatchParams.AutoTcThreshold;
            TcrAutoGenConsts.autoTcScale = dispatchParams.AutoTcScale;
            TcrAutoGenConsts.autoReactiveScale = dispatchParams.AutoReactiveScale;
            TcrAutoGenConsts.autoReactiveMax = dispatchParams.AutoReactiveMax;
            _tcrAutogenerateConstantsBuffer.SetData(_tcrAutogenerateConstantsArray);
            
            _tcrAutogeneratePipeline.ScheduleDispatch(commandBuffer, dispatchParams, frameIndex, dispatchSrcX, dispatchSrcY);
        }

        private void SetupConstants(Fsr2.DispatchDescription dispatchParams, bool resetAccumulation)
        {
            ref Fsr2.Fsr2Constants constants = ref Constants;
            
            constants.jitterOffset = dispatchParams.JitterOffset;
            constants.renderSize = dispatchParams.RenderSize;
            constants.maxRenderSize = _contextDescription.MaxRenderSize;
            constants.inputColorResourceDimensions = dispatchParams.InputResourceSize;

            // Compute the horizontal FOV for the shader from the vertical one
            float aspectRatio = (float)dispatchParams.RenderSize.x / dispatchParams.RenderSize.y;
            float cameraAngleHorizontal = Mathf.Atan(Mathf.Tan(dispatchParams.CameraFovAngleVertical / 2.0f) * aspectRatio) * 2.0f;
            constants.tanHalfFOV = Mathf.Tan(cameraAngleHorizontal * 0.5f);
            constants.viewSpaceToMetersFactor = (dispatchParams.ViewSpaceToMetersFactor > 0.0f) ? dispatchParams.ViewSpaceToMetersFactor : 1.0f;

            // Compute params to enable device depth to view space depth computation in shader
            constants.deviceToViewDepth = SetupDeviceDepthToViewSpaceDepthParams(dispatchParams);
            
            // To be updated if resource is larger than the actual image size
            constants.downscaleFactor = new Vector2((float)constants.renderSize.x / _contextDescription.DisplaySize.x, (float)constants.renderSize.y / _contextDescription.DisplaySize.y);
            constants.previousFramePreExposure = constants.preExposure;
            constants.preExposure = (dispatchParams.PreExposure != 0) ? dispatchParams.PreExposure : 1.0f;
            
            // Motion vector data
            Vector2Int motionVectorsTargetSize = (_contextDescription.Flags & Fsr2.InitializationFlags.EnableDisplayResolutionMotionVectors) != 0 ? constants.displaySize : constants.renderSize;
            constants.motionVectorScale = dispatchParams.MotionVectorScale / motionVectorsTargetSize;
            
            // Compute jitter cancellation
            if ((_contextDescription.Flags & Fsr2.InitializationFlags.EnableMotionVectorsJitterCancellation) != 0)
            {
                constants.motionVectorJitterCancellation = (_previousJitterOffset - constants.jitterOffset) / motionVectorsTargetSize;
                _previousJitterOffset = constants.jitterOffset;
            }

            int jitterPhaseCount = Fsr2.GetJitterPhaseCount(dispatchParams.RenderSize.x, _contextDescription.DisplaySize.x);
            if (resetAccumulation || constants.jitterPhaseCount == 0)
            {
                constants.jitterPhaseCount = jitterPhaseCount;
            }
            else
            {
                int jitterPhaseCountDelta = (int)(jitterPhaseCount - constants.jitterPhaseCount);
                if (jitterPhaseCountDelta > 0)
                    constants.jitterPhaseCount++;
                else if (jitterPhaseCountDelta < 0)
                    constants.jitterPhaseCount--;
            }
            
            // Convert delta time to seconds and clamp to [0, 1]
            constants.deltaTime = Mathf.Clamp01(dispatchParams.FrameTimeDelta);

            if (resetAccumulation)
                constants.frameIndex = 0;
            else
                constants.frameIndex++;

            // Shading change usage of the SPD mip levels
            constants.lumaMipLevelToUse = Fsr2Pipeline.ShadingChangeMipLevel;

            float mipDiv = 2 << constants.lumaMipLevelToUse;
            constants.lumaMipDimensions.x = (int)(constants.maxRenderSize.x / mipDiv);
            constants.lumaMipDimensions.y = (int)(constants.maxRenderSize.y / mipDiv);
        }
        
        private Vector4 SetupDeviceDepthToViewSpaceDepthParams(Fsr2.DispatchDescription dispatchParams)
        {
            bool inverted = (_contextDescription.Flags & Fsr2.InitializationFlags.EnableDepthInverted) != 0;
            bool infinite = (_contextDescription.Flags & Fsr2.InitializationFlags.EnableDepthInfinite) != 0;

            // make sure it has no impact if near and far plane values are swapped in dispatch params
            // the flags "inverted" and "infinite" will decide what transform to use
            float min = Mathf.Min(dispatchParams.CameraNear, dispatchParams.CameraFar);
            float max = Mathf.Max(dispatchParams.CameraNear, dispatchParams.CameraFar);

            if (inverted)
            {
                (min, max) = (max, min);
            }

            float q = max / (min - max);
            float d = -1.0f;

            Vector4 matrixElemC = new Vector4(q, -1.0f - Mathf.Epsilon, q, 0.0f + Mathf.Epsilon);
            Vector4 matrixElemE = new Vector4(q * min, -min - Mathf.Epsilon, q * min, max);
            
            // Revert x and y coords
            float aspect = (float)dispatchParams.RenderSize.x / dispatchParams.RenderSize.y;
            float cotHalfFovY = Mathf.Cos(0.5f * dispatchParams.CameraFovAngleVertical) / Mathf.Sin(0.5f * dispatchParams.CameraFovAngleVertical);

            int matrixIndex = (inverted ? 2 : 0) + (infinite ? 1 : 0);
            return new Vector4(
                d * matrixElemC[matrixIndex],
                matrixElemE[matrixIndex],
                aspect / cotHalfFovY,
                1.0f / cotHalfFovY);
        }

        private void SetupRcasConstants(Fsr2.DispatchDescription dispatchParams)
        {
            int sharpnessIndex = Mathf.RoundToInt(Mathf.Clamp01(dispatchParams.Sharpness) * (RcasConfigs.Count - 1));
            RcasConsts = RcasConfigs[sharpnessIndex];
        }

        private void SetupSpdConstants(Fsr2.DispatchDescription dispatchParams, out Vector2Int dispatchThreadGroupCount)
        {
            RectInt rectInfo = new RectInt(0, 0, dispatchParams.RenderSize.x, dispatchParams.RenderSize.y);
            SpdSetup(rectInfo, out dispatchThreadGroupCount, out var workGroupOffset, out var numWorkGroupsAndMips);
            
            // Downsample
            ref Fsr2.SpdConstants spdConstants = ref SpdConsts;
            spdConstants.numWorkGroups = (uint)numWorkGroupsAndMips.x;
            spdConstants.mips = (uint)numWorkGroupsAndMips.y;
            spdConstants.workGroupOffsetX = (uint)workGroupOffset.x;
            spdConstants.workGroupOffsetY = (uint)workGroupOffset.y;
            spdConstants.renderSizeX = (uint)dispatchParams.RenderSize.x;
            spdConstants.renderSizeY = (uint)dispatchParams.RenderSize.y;
        }

        private static void SpdSetup(RectInt rectInfo, out Vector2Int dispatchThreadGroupCount, out Vector2Int workGroupOffset, out Vector2Int numWorkGroupsAndMips, int mips = -1)
        {
            workGroupOffset = new Vector2Int(rectInfo.x / 64, rectInfo.y / 64);

            int endIndexX = (rectInfo.x + rectInfo.width - 1) / 64;
            int endIndexY = (rectInfo.y + rectInfo.height - 1) / 64;

            dispatchThreadGroupCount = new Vector2Int(endIndexX + 1 - workGroupOffset.x, endIndexY + 1 - workGroupOffset.y);

            numWorkGroupsAndMips = new Vector2Int(dispatchThreadGroupCount.x * dispatchThreadGroupCount.y, mips);
            if (mips < 0)
            {
                float resolution = Math.Max(rectInfo.width, rectInfo.height);
                numWorkGroupsAndMips.y = Math.Min(Mathf.FloorToInt(Mathf.Log(resolution, 2.0f)), 12);
            }
        }

        private void DebugCheckDispatch(Fsr2.DispatchDescription dispatchParams)
        {
            // Global texture binding may be queued as part of the command list, which is why we check these after running the process at least once
            if (!_firstExecution && !dispatchParams.Reset)
            {
                if (!dispatchParams.Color.HasValue && Shader.GetGlobalTexture(Fsr2ShaderIDs.SrvInputColor) == null)
                {
                    Debug.LogError("Color resource is null");
                }

                if (!dispatchParams.Depth.HasValue && Shader.GetGlobalTexture(Fsr2ShaderIDs.SrvInputDepth) == null)
                {
                    Debug.LogError("Depth resource is null");
                }

                if (!dispatchParams.MotionVectors.HasValue && Shader.GetGlobalTexture(Fsr2ShaderIDs.SrvInputMotionVectors) == null)
                {
                    Debug.LogError("MotionVectors resource is null");
                }
                
                if (!dispatchParams.Output.HasValue && Shader.GetGlobalTexture(Fsr2ShaderIDs.UavUpscaledOutput) == null)
                {
                    Debug.LogError("Output resource is null");
                }
            }

            if (dispatchParams.Exposure.HasValue && (_contextDescription.Flags & Fsr2.InitializationFlags.EnableAutoExposure) != 0)
            {
                Debug.LogWarning("Exposure resource provided, however auto exposure flag is present");
            }

            if (Mathf.Abs(dispatchParams.JitterOffset.x) > 1.0f || Mathf.Abs(dispatchParams.JitterOffset.y) > 1.0f)
            {
                Debug.LogWarning("JitterOffset contains value outside of expected range [-1.0, 1.0]");
            }

            if (dispatchParams.MotionVectorScale.x > _contextDescription.MaxRenderSize.x || dispatchParams.MotionVectorScale.y > _contextDescription.MaxRenderSize.y)
            {
                Debug.LogWarning("MotionVectorScale contains scale value greater than MaxRenderSize");
            }

            if (dispatchParams.MotionVectorScale.x == 0.0f || dispatchParams.MotionVectorScale.y == 0.0f)
            {
                Debug.LogWarning("MotionVectorScale contains zero scale value");
            }

            if (dispatchParams.RenderSize.x > _contextDescription.MaxRenderSize.x || dispatchParams.RenderSize.y > _contextDescription.MaxRenderSize.y)
            {
                Debug.LogWarning("RenderSize is greater than context MaxRenderSize");
            }

            if (dispatchParams.RenderSize.x == 0 || dispatchParams.RenderSize.y == 0)
            {
                Debug.LogWarning("RenderSize contains zero dimension");
            }

            if (dispatchParams.FrameTimeDelta > 1.0f)
            {
                Debug.LogWarning("FrameTimeDelta is greater than 1.0f - this value should be seconds (~0.0166 for 60fps)");
            }

            if (dispatchParams.PreExposure == 0.0f)
            {
                Debug.LogError("PreExposure provided as 0.0f which is invalid");
            }

            bool infiniteDepth = (_contextDescription.Flags & Fsr2.InitializationFlags.EnableDepthInfinite) != 0;
            bool inverseDepth = (_contextDescription.Flags & Fsr2.InitializationFlags.EnableDepthInverted) != 0;

            if (inverseDepth)
            {
                if (dispatchParams.CameraNear < dispatchParams.CameraFar)
                {
                    Debug.LogWarning("EnableDepthInverted flag is present yet CameraNear is less than CameraFar");
                }

                if (infiniteDepth)
                {
                    if (dispatchParams.CameraNear < float.MaxValue)
                    {
                        Debug.LogWarning("EnableDepthInfinite and EnableDepthInverted present, yet CameraNear != float.MaxValue");
                    }
                }
                
                if (dispatchParams.CameraFar < 0.075f)
                {
                    Debug.LogWarning("EnableDepthInverted present, CameraFar value is very low which may result in depth separation artefacting");
                }
            }
            else
            {
                if (dispatchParams.CameraNear > dispatchParams.CameraFar)
                {
                    Debug.LogWarning("CameraNear is greater than CameraFar in non-inverted-depth context");
                }

                if (infiniteDepth)
                {
                    if (dispatchParams.CameraFar < float.MaxValue)
                    {
                        Debug.LogWarning("EnableDepthInfinite present, yet CameraFar != float.MaxValue");
                    }
                }

                if (dispatchParams.CameraNear < 0.075f)
                {
                    Debug.LogWarning("CameraNear value is very low which may result in depth separation artefacting");
                }
            }

            if (dispatchParams.CameraFovAngleVertical <= 0.0f)
            {
                Debug.LogError("CameraFovAngleVertical is 0.0f - this value should be > 0.0f");
            }

            if (dispatchParams.CameraFovAngleVertical > Mathf.PI)
            {
                Debug.LogError("CameraFovAngleVertical is greater than 180 degrees/PI");
            }
        }

        /// <summary>
        /// The FSR2 C++ codebase uses floats bitwise converted to ints to pass sharpness parameters to the RCAS shader.
        /// This is not possible in C# without enabling unsafe code compilation, so to avoid that we instead use a table of precomputed values.
        /// </summary>
        private static readonly List<Fsr2.RcasConstants> RcasConfigs = new List<Fsr2.RcasConstants>()
        {
            new Fsr2.RcasConstants(1048576000u, 872428544u),
            new Fsr2.RcasConstants(1049178080u, 877212745u),
            new Fsr2.RcasConstants(1049823372u, 882390168u),
            new Fsr2.RcasConstants(1050514979u, 887895276u),
            new Fsr2.RcasConstants(1051256227u, 893859143u),
            new Fsr2.RcasConstants(1052050675u, 900216232u),
            new Fsr2.RcasConstants(1052902144u, 907032080u),
            new Fsr2.RcasConstants(1053814727u, 914306687u),
            new Fsr2.RcasConstants(1054792807u, 922105590u),
            new Fsr2.RcasConstants(1055841087u, 930494326u),
            new Fsr2.RcasConstants(1056964608u, 939538432u),
            new Fsr2.RcasConstants(1057566688u, 944322633u),
            new Fsr2.RcasConstants(1058211980u, 949500056u),
            new Fsr2.RcasConstants(1058903587u, 955005164u),
            new Fsr2.RcasConstants(1059644835u, 960969031u),
            new Fsr2.RcasConstants(1060439283u, 967326120u),
            new Fsr2.RcasConstants(1061290752u, 974141968u),
            new Fsr2.RcasConstants(1062203335u, 981416575u),
            new Fsr2.RcasConstants(1063181415u, 989215478u),
            new Fsr2.RcasConstants(1064229695u, 997604214u),
            new Fsr2.RcasConstants(1065353216u, 1006648320),
        };
        
        private static ComputeBuffer CreateConstantBuffer<TConstants>() where TConstants: struct
        {
            return new ComputeBuffer(1, Marshal.SizeOf<TConstants>(), ComputeBufferType.Constant);
        }
        
        private static void DestroyConstantBuffer(ref ComputeBuffer bufferRef)
        {
            if (bufferRef == null)
                return;
            
            bufferRef.Release();
            bufferRef = null;
        }

        private static void DestroyPipeline(ref Fsr2Pipeline pipeline)
        {
            if (pipeline == null)
                return;
            
            pipeline.Dispose();
            pipeline = null;
        }
    }
}
