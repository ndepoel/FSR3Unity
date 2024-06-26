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
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using FidelityFX;

namespace UnityEngine.Rendering.PostProcessing
{
    [UnityEngine.Scripting.Preserve]
    [Serializable]
    public class SuperResolution
    {
        public Func<PostProcessRenderContext, IFsr3UpscalerCallbacks> callbacksFactory { get; set; } = (context) => new Fsr3UpscalerCallbacksBase();
        
        [Tooltip("Standard scaling ratio presets.")]
        public Fsr3Upscaler.QualityMode qualityMode = Fsr3Upscaler.QualityMode.Quality;

        [Tooltip("Apply RCAS sharpening to the image after upscaling.")]
        public bool performSharpenPass = true;
        [Tooltip("Strength of the sharpening effect.")]
        [Range(0, 1)] public float sharpness = 0.8f;
        
        [Tooltip("Allow the use of half precision compute operations, potentially improving performance if the platform supports it.")]
        public bool enableFP16 = false;
        
        [Tooltip("Choose where to get the exposure value from. Use auto-exposure from either FSR3 or Unity, provide a manual exposure texture, or use a default value.")]
        public ExposureSource exposureSource = ExposureSource.Auto;
        [Tooltip("Value by which the input signal will be divided, to get back to the original signal produced by the game.")]
        public float preExposure = 1.0f;
        [Tooltip("Optional 1x1 texture containing the exposure value for the current frame.")]
        public Texture exposure = null;

        public enum ExposureSource
        {
            Default,
            Auto,
            Unity,
            Manual,
        }

        [Tooltip("Optional texture to control the influence of the current frame on the reconstructed output. If unset, either an auto-generated or a default cleared reactive mask will be used.")]
        public Texture reactiveMask = null;
        [Tooltip("Optional texture for marking areas of specialist rendering which should be accounted for during the upscaling process. If unset, a default cleared mask will be used.")]
        public Texture transparencyAndCompositionMask = null;
        [Tooltip("Automatically generate a reactive mask based on the difference between opaque-only render output and the final render output including alpha transparencies.")]
        public bool autoGenerateReactiveMask = true;
        [Tooltip("Parameters to control the process of auto-generating a reactive mask.")]
        public GenerateReactiveParameters generateReactiveParameters = new GenerateReactiveParameters();

        [Serializable]
        public class GenerateReactiveParameters
        {
            [Tooltip("A value to scale the output")]
            [Range(0, 2)] public float scale = 0.5f;
            [Tooltip("A threshold value to generate a binary reactive mask")]
            [Range(0, 1)] public float cutoffThreshold = 0.2f;
            [Tooltip("A value to set for the binary reactive mask")]
            [Range(0, 1)] public float binaryValue = 0.9f;
            [Tooltip("Flags to determine how to generate the reactive mask")]
            public Fsr3Upscaler.GenerateReactiveFlags flags = Fsr3Upscaler.GenerateReactiveFlags.ApplyTonemap | Fsr3Upscaler.GenerateReactiveFlags.ApplyThreshold | Fsr3Upscaler.GenerateReactiveFlags.UseComponentsMax;
        }

        [Tooltip("(Experimental) Automatically generate and use Reactive mask and Transparency & composition mask internally.")]
        public bool autoGenerateTransparencyAndComposition = false;
        [Tooltip("Parameters to control the process of auto-generating transparency and composition masks.")]
        public GenerateTcrParameters generateTransparencyAndCompositionParameters = new GenerateTcrParameters();

        [Serializable]
        public class GenerateTcrParameters
        {
            [Tooltip("Setting this value too small will cause visual instability. Larger values can cause ghosting.")]
            [Range(0, 1)] public float autoTcThreshold = 0.05f;
            [Tooltip("Smaller values will increase stability at hard edges of translucent objects.")]
            [Range(0, 2)] public float autoTcScale = 1.0f;
            [Tooltip("Larger values result in more reactive pixels.")]
            [Range(0, 10)] public float autoReactiveScale = 5.0f;
            [Tooltip("Maximum value reactivity can reach.")]
            [Range(0, 1)] public float autoReactiveMax = 0.9f;
        }
        
        public Vector2 jitter { get; private set; }
        public Vector2Int renderSize => _maxRenderSize;
        public Vector2Int displaySize => _displaySize;
        public RenderTargetIdentifier colorOpaqueOnly { get; set; }

        private Fsr3UpscalerContext _fsrContext;
        private Vector2Int _maxRenderSize;
        private Vector2Int _displaySize;
        private bool _resetHistory;

        private IFsr3UpscalerCallbacks _callbacks;

        private readonly Fsr3Upscaler.DispatchDescription _dispatchDescription = new Fsr3Upscaler.DispatchDescription();
        private readonly Fsr3Upscaler.GenerateReactiveDescription _genReactiveDescription = new Fsr3Upscaler.GenerateReactiveDescription();

        private Fsr3Upscaler.QualityMode _prevQualityMode;
        private ExposureSource _prevExposureSource;
        private Vector2Int _prevDisplaySize;

        private Rect _originalRect;

        public bool IsSupported()
        {
            return SystemInfo.supportsComputeShaders && SystemInfo.supportsMotionVectors;
        }
        
        public DepthTextureMode GetCameraFlags()
        {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }
        
        public void Release()
        {
            DestroyFsrContext();
        }

        public void ResetHistory()
        {
            _resetHistory = true;
        }

        public void ConfigureJitteredProjectionMatrix(PostProcessRenderContext context)
        {
            ApplyJitter(context.camera);
        }

        public void ConfigureCameraViewport(PostProcessRenderContext context)
        {
            var camera = context.camera;
            _originalRect = camera.rect;
            
            // Determine the desired rendering and display resolutions
            _displaySize = new Vector2Int(camera.pixelWidth, camera.pixelHeight);
            Fsr3Upscaler.GetRenderResolutionFromQualityMode(out int maxRenderWidth, out int maxRenderHeight, _displaySize.x, _displaySize.y, qualityMode);
            _maxRenderSize = new Vector2Int(maxRenderWidth, maxRenderHeight);
            
            // Render to a smaller portion of the screen by manipulating the camera's viewport rect
            camera.aspect = (float)_displaySize.x / _displaySize.y;
            camera.rect = new Rect(0, 0, _originalRect.width * _maxRenderSize.x / _displaySize.x, _originalRect.height * _maxRenderSize.y / _displaySize.y);
        }

        public void ResetCameraViewport(PostProcessRenderContext context)
        {
            context.camera.rect = _originalRect;
        }

        public void Render(PostProcessRenderContext context)
        {
            var cmd = context.command;
            cmd.BeginSample("FSR3 Upscaler");

            // Monitor for any resolution changes and recreate the FSR3 Upscaler context if necessary
            // We can't create an FSR3 Upscaler context without info from the post-processing context, so delay the initial setup until here
            if (_fsrContext == null || _displaySize.x != _prevDisplaySize.x || _displaySize.y != _prevDisplaySize.y || qualityMode != _prevQualityMode || exposureSource != _prevExposureSource)
            {
                DestroyFsrContext();
                CreateFsrContext(context);
            }

            SetupDispatchDescription(context);

            if (autoGenerateReactiveMask)
            {
                SetupAutoReactiveDescription(context);

                var scaledRenderSize = _genReactiveDescription.RenderSize;
                cmd.GetTemporaryRT(Fsr3ShaderIDs.UavAutoReactive, scaledRenderSize.x, scaledRenderSize.y, 0, default, GraphicsFormat.R8_UNorm, 1, true);
                _fsrContext.GenerateReactiveMask(_genReactiveDescription, cmd);
                _dispatchDescription.Reactive = new ResourceView(Fsr3ShaderIDs.UavAutoReactive);
            }
            
            _fsrContext.Dispatch(_dispatchDescription, cmd);
            
            cmd.EndSample("FSR3 Upscaler");
            
            _resetHistory = false;
        }

        private void CreateFsrContext(PostProcessRenderContext context)
        {
            _prevQualityMode = qualityMode;
            _prevExposureSource = exposureSource;
            _prevDisplaySize = _displaySize;
            
            // Initialize FSR3 Upscaler context
            Fsr3Upscaler.InitializationFlags flags = 0;
            if (context.camera.allowHDR) flags |= Fsr3Upscaler.InitializationFlags.EnableHighDynamicRange;
            if (enableFP16) flags |= Fsr3Upscaler.InitializationFlags.EnableFP16Usage;
            if (exposureSource == ExposureSource.Auto) flags |= Fsr3Upscaler.InitializationFlags.EnableAutoExposure;
            if (RuntimeUtilities.IsDynamicResolutionEnabled(context.camera)) flags |= Fsr3Upscaler.InitializationFlags.EnableDynamicResolution;

            _callbacks = callbacksFactory(context);
            _fsrContext = Fsr3Upscaler.CreateContext(_displaySize, _maxRenderSize, context.resources.computeShaders.superResolution, flags);

            // Apply a mipmap bias so that textures retain their sharpness
            float biasOffset = Fsr3Upscaler.GetMipmapBiasOffset(_maxRenderSize.x, _displaySize.x);
            if (!float.IsNaN(biasOffset) && !float.IsInfinity(biasOffset))
            {
                _callbacks.ApplyMipmapBias(biasOffset);
            }
        }
        
        private void DestroyFsrContext()
        {
            if (_fsrContext != null)
            {
                _fsrContext.Destroy();
                _fsrContext = null;
            }

            if (_callbacks != null)
            {
                // Undo the current mipmap bias offset
                _callbacks.UndoMipmapBias();
                _callbacks = null;
            }
        }

        private void ApplyJitter(Camera camera)
        {
            var scaledRenderSize = GetScaledRenderSize(camera);
            
            // Perform custom jittering of the camera's projection matrix according to FSR3's recipe
            int jitterPhaseCount = Fsr3Upscaler.GetJitterPhaseCount(scaledRenderSize.x, _displaySize.x);
            Fsr3Upscaler.GetJitterOffset(out float jitterX, out float jitterY, Time.frameCount, jitterPhaseCount);
            
            _dispatchDescription.JitterOffset = new Vector2(jitterX, jitterY);

            jitterX = 2.0f * jitterX / scaledRenderSize.x;
            jitterY = 2.0f * jitterY / scaledRenderSize.y;

            var jitterTranslationMatrix = Matrix4x4.Translate(new Vector3(jitterX, jitterY, 0));
            camera.nonJitteredProjectionMatrix = camera.projectionMatrix;
            camera.projectionMatrix = jitterTranslationMatrix * camera.nonJitteredProjectionMatrix;
            camera.useJitteredProjectionMatrixForTransparentRendering = true;

            jitter = new Vector2(jitterX, jitterY);
        }

        private void SetupDispatchDescription(PostProcessRenderContext context)
        {
            var camera = context.camera;
            
            // Set up the main FSR3 Upscaler dispatch parameters
            _dispatchDescription.Color = new ResourceView(context.source);
            _dispatchDescription.Depth = new ResourceView(BuiltinRenderTextureType.CameraTarget, RenderTextureSubElement.Depth);
            _dispatchDescription.MotionVectors = new ResourceView(BuiltinRenderTextureType.MotionVectors);
            _dispatchDescription.Exposure = ResourceView.Unassigned;
            _dispatchDescription.Reactive = ResourceView.Unassigned;
            _dispatchDescription.TransparencyAndComposition = ResourceView.Unassigned;

            if (exposureSource == ExposureSource.Manual && exposure != null) _dispatchDescription.Exposure = new ResourceView(exposure);
            if (exposureSource == ExposureSource.Unity) _dispatchDescription.Exposure = new ResourceView(context.autoExposureTexture);
            if (reactiveMask != null) _dispatchDescription.Reactive = new ResourceView(reactiveMask);
            if (transparencyAndCompositionMask != null) _dispatchDescription.TransparencyAndComposition = new ResourceView(transparencyAndCompositionMask);

            var scaledRenderSize = GetScaledRenderSize(context.camera);
            
            _dispatchDescription.Output = new ResourceView(context.destination);
            _dispatchDescription.PreExposure = preExposure;
            _dispatchDescription.EnableSharpening = performSharpenPass;
            _dispatchDescription.Sharpness = sharpness;
            _dispatchDescription.MotionVectorScale.x = -scaledRenderSize.x;
            _dispatchDescription.MotionVectorScale.y = -scaledRenderSize.y;
            _dispatchDescription.RenderSize = scaledRenderSize;
            _dispatchDescription.InputResourceSize = scaledRenderSize;
            _dispatchDescription.FrameTimeDelta = Time.unscaledDeltaTime;
            _dispatchDescription.CameraNear = camera.nearClipPlane;
            _dispatchDescription.CameraFar = camera.farClipPlane;
            _dispatchDescription.CameraFovAngleVertical = camera.fieldOfView * Mathf.Deg2Rad;
            _dispatchDescription.ViewSpaceToMetersFactor = 1.0f; // 1 unit is 1 meter in Unity
            _dispatchDescription.Reset = _resetHistory;

            // Set up the parameters for the optional experimental auto-TCR feature
            _dispatchDescription.EnableAutoReactive = autoGenerateTransparencyAndComposition;
            if (autoGenerateTransparencyAndComposition)
            {
                _dispatchDescription.ColorOpaqueOnly = new ResourceView(colorOpaqueOnly);
                _dispatchDescription.AutoTcThreshold = generateTransparencyAndCompositionParameters.autoTcThreshold;
                _dispatchDescription.AutoTcScale = generateTransparencyAndCompositionParameters.autoTcScale;
                _dispatchDescription.AutoReactiveScale = generateTransparencyAndCompositionParameters.autoReactiveScale;
                _dispatchDescription.AutoReactiveMax = generateTransparencyAndCompositionParameters.autoReactiveMax;
            }

            if (SystemInfo.usesReversedZBuffer)
            {
                // Swap the near and far clip plane distances as FSR3 expects this when using inverted depth
                (_dispatchDescription.CameraNear, _dispatchDescription.CameraFar) = (_dispatchDescription.CameraFar, _dispatchDescription.CameraNear);
            }
        }

        private void SetupAutoReactiveDescription(PostProcessRenderContext context)
        {
            // Set up the parameters to auto-generate a reactive mask
            _genReactiveDescription.ColorOpaqueOnly = new ResourceView(colorOpaqueOnly);
            _genReactiveDescription.ColorPreUpscale = new ResourceView(context.source);
            _genReactiveDescription.OutReactive = new ResourceView(Fsr3ShaderIDs.UavAutoReactive);
            _genReactiveDescription.RenderSize = GetScaledRenderSize(context.camera);
            _genReactiveDescription.Scale = generateReactiveParameters.scale;
            _genReactiveDescription.CutoffThreshold = generateReactiveParameters.cutoffThreshold;
            _genReactiveDescription.BinaryValue = generateReactiveParameters.binaryValue;
            _genReactiveDescription.Flags = generateReactiveParameters.flags;
        }

        internal Vector2Int GetScaledRenderSize(Camera camera)
        {
            if (!RuntimeUtilities.IsDynamicResolutionEnabled(camera))
                return _maxRenderSize;

            return new Vector2Int(Mathf.CeilToInt(_maxRenderSize.x * ScalableBufferManager.widthScaleFactor), Mathf.CeilToInt(_maxRenderSize.y * ScalableBufferManager.heightScaleFactor));
        }
    }
}
