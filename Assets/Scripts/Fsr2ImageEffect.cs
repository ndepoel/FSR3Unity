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
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FidelityFX
{
    /// <summary>
    /// This class is responsible for hooking into various Unity events and translating them to the FSR2 subsystem.
    /// This includes creation and destruction of the FSR2 context, as well as dispatching commands at the right time.
    /// This component also exposes various FSR2 parameters to the Unity inspector.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class Fsr2ImageEffect : MonoBehaviour
    {
        public IFsr2Callbacks Callbacks { get; set; } = new Fsr2CallbacksBase();
        
        [Tooltip("Standard scaling ratio presets.")]
        public Fsr2.QualityMode qualityMode = Fsr2.QualityMode.Quality;

        [Tooltip("Apply RCAS sharpening to the image after upscaling.")]
        public bool performSharpenPass = true;
        [Tooltip("Strength of the sharpening effect.")]
        [Range(0, 1)] public float sharpness = 0.8f;
        
        [Tooltip("Allow the use of half precision compute operations, potentially improving performance if the platform supports it.")]
        public bool enableFP16 = false;

        [Header("Exposure")]
        [Tooltip("Allow an exposure value to be computed internally. When set to false, either the provided exposure texture or a default exposure value will be used.")]
        public bool enableAutoExposure = true;
        [Tooltip("Value by which the input signal will be divided, to get back to the original signal produced by the game.")]
        public float preExposure = 1.0f;
        [Tooltip("Optional 1x1 texture containing the exposure value for the current frame.")]
        public Texture exposure = null;

        [Header("Reactivity, Transparency & Composition")] 
        [Tooltip("Optional texture to control the influence of the current frame on the reconstructed output. If unset, either an auto-generated or a default cleared reactive mask will be used.")]
        public Texture reactiveMask = null;
        [Tooltip("Optional texture for marking areas of specialist rendering which should be accounted for during the upscaling process. If unset, a default cleared mask will be used.")]
        public Texture transparencyAndCompositionMask = null;
        [Tooltip("Automatically generate a reactive mask based on the difference between opaque-only render output and the final render output including alpha transparencies.")]
        public bool autoGenerateReactiveMask = true;
        [Tooltip("Parameters to control the process of auto-generating a reactive mask.")]
        [SerializeField] private GenerateReactiveParameters generateReactiveParameters = new GenerateReactiveParameters();
        public GenerateReactiveParameters GenerateReactiveParams => generateReactiveParameters;
        
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
            public Fsr2.GenerateReactiveFlags flags = Fsr2.GenerateReactiveFlags.ApplyTonemap | Fsr2.GenerateReactiveFlags.ApplyThreshold | Fsr2.GenerateReactiveFlags.UseComponentsMax;
        }

        [Tooltip("(Experimental) Automatically generate and use Reactive mask and Transparency & composition mask internally.")]
        public bool autoGenerateTransparencyAndComposition = false;
        [Tooltip("Parameters to control the process of auto-generating transparency and composition masks.")]
        [SerializeField] private GenerateTcrParameters generateTransparencyAndCompositionParameters = new GenerateTcrParameters();
        public GenerateTcrParameters GenerateTcrParams => generateTransparencyAndCompositionParameters;
        
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

        [Header("Output resources")]
        [Tooltip("Optional render texture to copy motion vector data to, for additional post-processing after upscaling.")]
        public RenderTexture outputMotionVectors;

        private Fsr2Context _context;
        private Vector2Int _maxRenderSize;
        private Vector2Int _displaySize;
        private float _appliedBiasOffset;
        private bool _resetHistory;
        
        private readonly Fsr2.DispatchDescription _dispatchDescription = new Fsr2.DispatchDescription();
        private readonly Fsr2.GenerateReactiveDescription _genReactiveDescription = new Fsr2.GenerateReactiveDescription();

        private Fsr2ImageEffectHelper _helper;
        
        private Camera _renderCamera;
        private RenderTexture _originalRenderTarget;
        private DepthTextureMode _originalDepthTextureMode;
        private Rect _originalRect;

        private Fsr2.QualityMode _prevQualityMode;
        private Vector2Int _prevDisplaySize;
        private bool _prevAutoExposure;

        private CommandBuffer _dispatchCommandBuffer;
        private CommandBuffer _opaqueInputCommandBuffer;
        private RenderTexture _colorOpaqueOnly;

        private Material _copyWithDepthMaterial;

        private void OnEnable()
        {
            // Set up the original camera to output all of the required FSR2 input resources at the desired resolution
            _renderCamera = GetComponent<Camera>();
            _originalRenderTarget = _renderCamera.targetTexture;
            _originalDepthTextureMode = _renderCamera.depthTextureMode;
            _renderCamera.targetTexture = null;     // Clear the camera's target texture so we can fully control how the output gets written
            _renderCamera.depthTextureMode = _originalDepthTextureMode | DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            
            // Determine the desired rendering and display resolutions
            _displaySize = GetDisplaySize();
            Fsr2.GetRenderResolutionFromQualityMode(out var maxRenderWidth, out var maxRenderHeight, _displaySize.x, _displaySize.y, qualityMode);
            _maxRenderSize = new Vector2Int(maxRenderWidth, maxRenderHeight);

            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogError("FSR2 requires compute shader support!");
                enabled = false;
                return;
            }

            if (_maxRenderSize.x == 0 || _maxRenderSize.y == 0)
            {
                Debug.LogError($"FSR2 render size is invalid: {_maxRenderSize.x}x{_maxRenderSize.y}. Please check your screen resolution and camera viewport parameters.");
                enabled = false;
                return;
            }
            
            _helper = GetComponent<Fsr2ImageEffectHelper>();
            _copyWithDepthMaterial = new Material(Shader.Find("Hidden/BlitCopyWithDepth"));
            
            CreateFsrContext();
            CreateCommandBuffers();
        }

        private void OnDisable()
        {
            DestroyCommandBuffers();
            DestroyFsrContext();

            if (_copyWithDepthMaterial != null)
            {
                Destroy(_copyWithDepthMaterial);
                _copyWithDepthMaterial = null;
            }
            
            // Restore the camera's original state
            _renderCamera.depthTextureMode = _originalDepthTextureMode;
            _renderCamera.targetTexture = _originalRenderTarget;
        }

        private void CreateFsrContext()
        {
            // Initialize FSR2 context
            Fsr2.InitializationFlags flags = 0;
            if (_renderCamera.allowHDR) flags |= Fsr2.InitializationFlags.EnableHighDynamicRange;
            if (enableFP16) flags |= Fsr2.InitializationFlags.EnableFP16Usage;
            if (enableAutoExposure) flags |= Fsr2.InitializationFlags.EnableAutoExposure;
            if (UsingDynamicResolution()) flags |= Fsr2.InitializationFlags.EnableDynamicResolution;

            _context = Fsr2.CreateContext(_displaySize, _maxRenderSize, Callbacks, flags);

            _prevDisplaySize = _displaySize;
            _prevQualityMode = qualityMode;
            _prevAutoExposure = enableAutoExposure;
            
            ApplyMipmapBias();
        }

        private void DestroyFsrContext()
        {
            UndoMipmapBias();
            
            if (_context != null)
            {
                _context.Destroy();
                _context = null;
            }
        }

        private void CreateCommandBuffers()
        {
            _dispatchCommandBuffer = new CommandBuffer { name = "FSR2 Dispatch" };
            _opaqueInputCommandBuffer = new CommandBuffer { name = "FSR2 Opaque Input" };
            _renderCamera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _opaqueInputCommandBuffer);
        }

        private void DestroyCommandBuffers()
        {
            if (_opaqueInputCommandBuffer != null)
            {
                _renderCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _opaqueInputCommandBuffer);
                _opaqueInputCommandBuffer.Release();
                _opaqueInputCommandBuffer = null;
            }

            if (_dispatchCommandBuffer != null)
            {
                _dispatchCommandBuffer.Release();
                _dispatchCommandBuffer = null;
            }
        }
        
        private void ApplyMipmapBias()
        {
            // Apply a mipmap bias so that textures retain their sharpness
            float biasOffset = Fsr2.GetMipmapBiasOffset(_maxRenderSize.x, _displaySize.x);
            if (!float.IsNaN(biasOffset) && !float.IsInfinity(biasOffset))
            {
                Callbacks.ApplyMipmapBias(biasOffset);
                _appliedBiasOffset = biasOffset;
            }
            else
            {
                _appliedBiasOffset = 0f;
            }
        }

        private void UndoMipmapBias()
        {
            // Undo the current mipmap bias offset
            if (_appliedBiasOffset != 0f && !float.IsNaN(_appliedBiasOffset) && !float.IsInfinity(_appliedBiasOffset))
            {
                Callbacks.UndoMipmapBias(_appliedBiasOffset);
                _appliedBiasOffset = 0f;
            }
        }

        private void Update()
        {
            // Monitor for any changes in parameters that require a reset of the FSR2 context
            var displaySize = GetDisplaySize();
            if (displaySize.x != _prevDisplaySize.x || displaySize.y != _prevDisplaySize.y || qualityMode != _prevQualityMode || enableAutoExposure != _prevAutoExposure)
            {
                // Force all resources to be destroyed and recreated with the new settings
                OnDisable();
                OnEnable();
            }
        }

        public void ResetHistory()
        {
            // Reset the temporal accumulation, for when the camera cuts to a different location or angle
            _resetHistory = true;
        }

        private void LateUpdate()
        {
            // Remember the original camera viewport before we modify it in OnPreCull
            _originalRect = _renderCamera.rect;
        }

        private void OnPreCull()
        {
            if (_helper == null || !_helper.enabled)
            {
                // Render to a smaller portion of the screen by manipulating the camera's viewport rect
                _renderCamera.aspect = (_displaySize.x * _originalRect.width) / (_displaySize.y * _originalRect.height);
                _renderCamera.rect = new Rect(0, 0, _originalRect.width * _maxRenderSize.x / _renderCamera.pixelWidth, _originalRect.height * _maxRenderSize.y / _renderCamera.pixelHeight);
            }
            
            // Set up the opaque-only command buffer to make a copy of the camera color buffer right before transparent drawing starts 
            _opaqueInputCommandBuffer.Clear();
            if (autoGenerateReactiveMask || autoGenerateTransparencyAndComposition)
            {
                var scaledRenderSize = GetScaledRenderSize();
                _colorOpaqueOnly = RenderTexture.GetTemporary(scaledRenderSize.x, scaledRenderSize.y, 0, GetDefaultFormat());
                _opaqueInputCommandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, _colorOpaqueOnly);
            }

            if (autoGenerateReactiveMask)
            {
                SetupAutoReactiveDescription();
            }
            
            SetupDispatchDescription();
            
            ApplyJitter();
        }

        private void SetupDispatchDescription()
        {
            // Set up the main FSR2 dispatch parameters
            // The input and output textures are left blank here, as they get bound directly through SetGlobalTexture and GetTemporaryRT elsewhere in this source file
            _dispatchDescription.Color = null;
            _dispatchDescription.Depth = null;
            _dispatchDescription.MotionVectors = null;
            _dispatchDescription.Exposure = null;
            _dispatchDescription.Reactive = null;
            _dispatchDescription.TransparencyAndComposition = null;
            
            if (!enableAutoExposure && exposure != null) _dispatchDescription.Exposure = exposure;
            if (reactiveMask != null) _dispatchDescription.Reactive = reactiveMask;
            if (transparencyAndCompositionMask != null) _dispatchDescription.TransparencyAndComposition = transparencyAndCompositionMask;

            var scaledRenderSize = GetScaledRenderSize();
            
            _dispatchDescription.Output = null;
            _dispatchDescription.PreExposure = preExposure;
            _dispatchDescription.EnableSharpening = performSharpenPass;
            _dispatchDescription.Sharpness = sharpness;
            _dispatchDescription.MotionVectorScale.x = -scaledRenderSize.x;
            _dispatchDescription.MotionVectorScale.y = -scaledRenderSize.y;
            _dispatchDescription.RenderSize = scaledRenderSize;
            _dispatchDescription.FrameTimeDelta = Time.unscaledDeltaTime;
            _dispatchDescription.CameraNear = _renderCamera.nearClipPlane;
            _dispatchDescription.CameraFar = _renderCamera.farClipPlane;
            _dispatchDescription.CameraFovAngleVertical = _renderCamera.fieldOfView * Mathf.Deg2Rad;
            _dispatchDescription.ViewSpaceToMetersFactor = 1.0f; // 1 unit is 1 meter in Unity
            _dispatchDescription.Reset = _resetHistory;
            _resetHistory = false;

            // Set up the parameters for the optional experimental auto-TCR feature
            _dispatchDescription.EnableAutoReactive = autoGenerateTransparencyAndComposition;
            if (autoGenerateTransparencyAndComposition)
            {
                _dispatchDescription.ColorOpaqueOnly = _colorOpaqueOnly;
                _dispatchDescription.AutoTcThreshold = generateTransparencyAndCompositionParameters.autoTcThreshold;
                _dispatchDescription.AutoTcScale = generateTransparencyAndCompositionParameters.autoTcScale;
                _dispatchDescription.AutoReactiveScale = generateTransparencyAndCompositionParameters.autoReactiveScale;
                _dispatchDescription.AutoReactiveMax = generateTransparencyAndCompositionParameters.autoReactiveMax;
            }

            if (SystemInfo.usesReversedZBuffer)
            {
                // Swap the near and far clip plane distances as FSR2 expects this when using inverted depth
                (_dispatchDescription.CameraNear, _dispatchDescription.CameraFar) = (_dispatchDescription.CameraFar, _dispatchDescription.CameraNear);
            }
        }

        private void SetupAutoReactiveDescription()
        {
            // Set up the parameters to auto-generate a reactive mask
            _genReactiveDescription.ColorOpaqueOnly = _colorOpaqueOnly;
            _genReactiveDescription.ColorPreUpscale = null;
            _genReactiveDescription.OutReactive = null;
            _genReactiveDescription.RenderSize = GetScaledRenderSize();
            _genReactiveDescription.Scale = generateReactiveParameters.scale;
            _genReactiveDescription.CutoffThreshold = generateReactiveParameters.cutoffThreshold;
            _genReactiveDescription.BinaryValue = generateReactiveParameters.binaryValue;
            _genReactiveDescription.Flags = generateReactiveParameters.flags;
        }

        private void ApplyJitter()
        {
            var scaledRenderSize = GetScaledRenderSize();
            
            // Perform custom jittering of the camera's projection matrix according to FSR2's recipe
            int jitterPhaseCount = Fsr2.GetJitterPhaseCount(scaledRenderSize.x, _displaySize.x);
            Fsr2.GetJitterOffset(out float jitterX, out float jitterY, Time.frameCount, jitterPhaseCount);

            _dispatchDescription.JitterOffset = new Vector2(jitterX, jitterY);

            jitterX = 2.0f * jitterX / scaledRenderSize.x;
            jitterY = 2.0f * jitterY / scaledRenderSize.y;

            var jitterTranslationMatrix = Matrix4x4.Translate(new Vector3(jitterX, jitterY, 0));
            _renderCamera.nonJitteredProjectionMatrix = _renderCamera.projectionMatrix;
            _renderCamera.projectionMatrix = jitterTranslationMatrix * _renderCamera.nonJitteredProjectionMatrix;
            _renderCamera.useJitteredProjectionMatrixForTransparentRendering = true;
        }
        
        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            // Restore the camera's viewport rect so we can output at full resolution
            _renderCamera.rect = _originalRect;
            _renderCamera.ResetProjectionMatrix();

            // Update the input resource descriptions
            _dispatchDescription.InputResourceSize = new Vector2Int(src.width, src.height);

            _dispatchCommandBuffer.Clear();
            _dispatchCommandBuffer.SetGlobalTexture(Fsr2ShaderIDs.SrvInputColor, BuiltinRenderTextureType.CameraTarget, RenderTextureSubElement.Color);
            _dispatchCommandBuffer.SetGlobalTexture(Fsr2ShaderIDs.SrvInputDepth, BuiltinRenderTextureType.CameraTarget, RenderTextureSubElement.Depth);
            _dispatchCommandBuffer.SetGlobalTexture(Fsr2ShaderIDs.SrvInputMotionVectors, BuiltinRenderTextureType.MotionVectors);

            if (autoGenerateReactiveMask)
            {
                // The auto-reactive mask pass is executed separately from the main FSR2 passes
                var scaledRenderSize = GetScaledRenderSize();
                _dispatchCommandBuffer.GetTemporaryRT(Fsr2ShaderIDs.UavAutoReactive, scaledRenderSize.x, scaledRenderSize.y, 0, default, GraphicsFormat.R8_UNorm, 1, true);
                _context.GenerateReactiveMask(_genReactiveDescription, _dispatchCommandBuffer);
                _dispatchDescription.Reactive = Fsr2ShaderIDs.UavAutoReactive;
            }

            // The backbuffer is not set up to allow random-write access, so we need a temporary render texture for FSR2 to output to
            _dispatchCommandBuffer.GetTemporaryRT(Fsr2ShaderIDs.UavUpscaledOutput, _displaySize.x, _displaySize.y, 0, default, GetDefaultFormat(), default, 1, true);

            _context.Dispatch(_dispatchDescription, _dispatchCommandBuffer);

            // Output the upscaled image
            if (_originalRenderTarget != null)
            {
                // Output to the camera target texture, passing through depth and motion vectors
                _dispatchCommandBuffer.SetGlobalTexture("_DepthTex", BuiltinRenderTextureType.CameraTarget, RenderTextureSubElement.Depth);
                _dispatchCommandBuffer.Blit(Fsr2ShaderIDs.UavUpscaledOutput, _originalRenderTarget, _copyWithDepthMaterial);
                if (outputMotionVectors != null)
                    _dispatchCommandBuffer.Blit(BuiltinRenderTextureType.MotionVectors, outputMotionVectors);
            }
            else
            {
                // Output directly to the backbuffer
                _dispatchCommandBuffer.Blit(Fsr2ShaderIDs.UavUpscaledOutput, dest);
            }
            
            _dispatchCommandBuffer.ReleaseTemporaryRT(Fsr2ShaderIDs.UavUpscaledOutput);
            _dispatchCommandBuffer.ReleaseTemporaryRT(Fsr2ShaderIDs.UavAutoReactive);

            Graphics.ExecuteCommandBuffer(_dispatchCommandBuffer);
            
            if (_colorOpaqueOnly != null)
            {
                RenderTexture.ReleaseTemporary(_colorOpaqueOnly);
                _colorOpaqueOnly = null;
            }

            // Shut up the Unity warning about not writing to the destination texture 
            RenderTexture.active = dest;
        }

        private RenderTextureFormat GetDefaultFormat()
        {
            if (_originalRenderTarget != null)
                return _originalRenderTarget.format;

            return _renderCamera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
        }

        private Vector2Int GetDisplaySize()
        {
            if (_originalRenderTarget != null)
                return new Vector2Int(_originalRenderTarget.width, _originalRenderTarget.height);
            
            return new Vector2Int(_renderCamera.pixelWidth, _renderCamera.pixelHeight);
        }

        private bool UsingDynamicResolution()
        {
            return _renderCamera.allowDynamicResolution || (_originalRenderTarget != null && _originalRenderTarget.useDynamicScale);
        }

        private Vector2Int GetScaledRenderSize()
        {
            if (UsingDynamicResolution())
                return new Vector2Int(Mathf.CeilToInt(_maxRenderSize.x * ScalableBufferManager.widthScaleFactor), Mathf.CeilToInt(_maxRenderSize.y * ScalableBufferManager.heightScaleFactor));
            
            return _maxRenderSize;
        }
    }
}
