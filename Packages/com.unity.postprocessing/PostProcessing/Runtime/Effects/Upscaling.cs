using System;
using System.Runtime.InteropServices;
using FidelityFX.FSR2;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.PostProcessing
{
	[Scripting.Preserve]
	[Serializable]
	public class Upscaling
	{
        public Func<PostProcessRenderContext, IUpscalerCallbacks> callbacksFactory { get; set; } = (context) => new UpscalerCallbacksBase();

        public enum UpscalerType
        {
	        [InspectorName("FidelityFX Super Resolution 2.3 (FSR2)")] FSR2,
	        [InspectorName("FidelityFX Super Resolution 3.1 (FSR3)")] FSR3,
        }

        [Tooltip("Which upscaling technology to use.")]
        public UpscalerType upscalerType = UpscalerType.FSR2;
        
        [Tooltip("Standard scaling ratio presets.")]
        public Fsr2.QualityMode qualityMode = Fsr2.QualityMode.Quality;

        [Tooltip("Apply sharpening to the image after upscaling.")]
        public bool performSharpenPass = true;
        [Tooltip("Strength of the sharpening effect.")]
        [Range(0, 1)] public float sharpness = 0.8f;

        [Tooltip("Adjust the influence of motion vectors on temporal accumulation.")]
        [Range(0, 1)] public float velocityFactor = 1.0f;
        
        [Tooltip("Choose where to get the exposure value from. Use auto-exposure from either the upscaler or Unity, provide a manual exposure texture, or use a default value.")]
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
        
        [Tooltip("Enable a debug view to analyze the upscaling process.")]
        public bool enableDebugView = false;

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
            public Fsr2.GenerateReactiveFlags flags = Fsr2.GenerateReactiveFlags.ApplyTonemap | Fsr2.GenerateReactiveFlags.ApplyThreshold | Fsr2.GenerateReactiveFlags.UseComponentsMax;
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
        
        public Vector2 Jitter { get; private set; }
        public Vector2 JitterOffset { get; private set; }
        public Vector2Int MaxRenderSize => _maxRenderSize;
        public Vector2Int UpscaleSize => _upscaleSize;
        public bool Reset => _resetHistory;
        public RenderTargetIdentifier ColorOpaqueOnly { get; set; }

        private bool _initialized;
        private Upscaler _upscaler;
        private Vector2Int _maxRenderSize;
        private Vector2Int _upscaleSize;
        private bool _resetHistory;

        private IUpscalerCallbacks _callbacks;

        private UpscalerType _prevUpscalerType;
        private Fsr2.QualityMode _prevQualityMode;
        private ExposureSource _prevExposureSource;
        private Vector2Int _prevUpscaleSize;

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
			DestroyUpscaler();
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
			_upscaleSize = new Vector2Int(camera.pixelWidth, camera.pixelHeight);
			Fsr2.GetRenderResolutionFromQualityMode(out int maxRenderWidth, out int maxRenderHeight, _upscaleSize.x, _upscaleSize.y, qualityMode);
			_maxRenderSize = new Vector2Int(maxRenderWidth, maxRenderHeight);
            
			// Render to a smaller portion of the screen by manipulating the camera's viewport rect
			camera.aspect = (float)_upscaleSize.x / _upscaleSize.y;
			camera.rect = new Rect(0, 0, _originalRect.width * _maxRenderSize.x / _upscaleSize.x, _originalRect.height * _maxRenderSize.y / _upscaleSize.y);
		}

		public void ResetCameraViewport(PostProcessRenderContext context)
		{
			context.camera.rect = _originalRect;
		}
		
		public void Render(PostProcessRenderContext context)
		{
			// Monitor for any resolution changes and recreate the upscaler context if necessary
			// We can't create an upscaler context without info from the post-processing context, so delay the initial setup until here
			if (!_initialized || _upscaler == null || _upscaleSize.x != _prevUpscaleSize.x || _upscaleSize.y != _prevUpscaleSize.y || 
			    upscalerType != _prevUpscalerType || qualityMode != _prevQualityMode || exposureSource != _prevExposureSource)
			{
				DestroyUpscaler();
				CreateUpscaler(context);
			}

			_upscaler?.Render(context, this);
            
			_resetHistory = false;
		}

		private void CreateUpscaler(PostProcessRenderContext context)
		{
			if (_upscaler == null || upscalerType != _prevUpscalerType)
			{
				_upscaler = upscalerType switch
				{
					UpscalerType.FSR2 when FSR2Upscaler.IsSupported => new FSR2Upscaler(),
					UpscalerType.FSR3 when FSR3Upscaler.IsSupported => new FSR3Upscaler(),
					_ => new FSR2Upscaler(),	// Fallback for when the selected upscaler is not supported on the current hardware
				};

				_prevUpscalerType = upscalerType;
			}
			
			_prevQualityMode = qualityMode;
			_prevExposureSource = exposureSource;
			_prevUpscaleSize = _upscaleSize;
			
			_callbacks = callbacksFactory(context);
			
			_upscaler.CreateContext(context, this);
			
			// Apply a mipmap bias so that textures retain their sharpness
			float biasOffset = Fsr2.GetMipmapBiasOffset(_maxRenderSize.x, _upscaleSize.x);
			if (_callbacks != null && !float.IsNaN(biasOffset) && !float.IsInfinity(biasOffset))
			{
				_callbacks.ApplyMipmapBias(biasOffset);
			}
			
			_initialized = true;
		}

		private void DestroyUpscaler()
		{
			_initialized = false;
			
			_upscaler?.DestroyContext();

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
			
			// Perform custom jittering of the camera's projection matrix according to FSR's recipe
			int jitterPhaseCount = Fsr2.GetJitterPhaseCount(scaledRenderSize.x, _upscaleSize.x);
			Fsr2.GetJitterOffset(out float jitterX, out float jitterY, Time.frameCount, jitterPhaseCount);
            
			JitterOffset = new Vector2(jitterX, jitterY);

			jitterX = 2.0f * jitterX / scaledRenderSize.x;
			jitterY = 2.0f * jitterY / scaledRenderSize.y;

			var jitterTranslationMatrix = Matrix4x4.Translate(new Vector3(jitterX, jitterY, 0));
			camera.nonJitteredProjectionMatrix = camera.projectionMatrix;
			camera.projectionMatrix = jitterTranslationMatrix * camera.nonJitteredProjectionMatrix;
			camera.useJitteredProjectionMatrixForTransparentRendering = true;

			Jitter = new Vector2(jitterX, jitterY);
		}
		
		internal Vector2Int GetScaledRenderSize(Camera camera)
		{
			if (!RuntimeUtilities.IsDynamicResolutionEnabled(camera))
				return _maxRenderSize;

			return new Vector2Int(Mathf.CeilToInt(_maxRenderSize.x * ScalableBufferManager.widthScaleFactor), Mathf.CeilToInt(_maxRenderSize.y * ScalableBufferManager.heightScaleFactor));
		}

		internal static BuiltinRenderTextureType GetDepthTexture(Camera cam)
		{
			return cam.renderingPath is RenderingPath.Forward or RenderingPath.VertexLit ? BuiltinRenderTextureType.Depth : BuiltinRenderTextureType.CameraTarget;
		}
	}
	
	/// <summary>
	/// A collection of callbacks required by the upscaler.
	/// This allows some customization by the game dev on how to integrate upscaling into their own game setup.
	/// </summary>
	public interface IUpscalerCallbacks
	{
		/// <summary>
		/// Apply a mipmap bias to in-game textures to prevent them from becoming blurry as the internal rendering resolution lowers.
		/// This will need to be customized on a per-game basis, as there is no clear universal way to determine what are "in-game" textures.
		/// The default implementation will simply apply a mipmap bias to all 2D textures, which will include things like UI textures and which might miss things like terrain texture arrays.
		/// 
		/// Depending on how your game organizes its assets, you will want to create a filter that more specifically selects the textures that need to have this mipmap bias applied.
		/// You may also want to store the bias offset value and apply it to any assets that are loaded in on demand.
		/// </summary>
		void ApplyMipmapBias(float biasOffset);

		void UndoMipmapBias();
	}
	
	/// <summary>
	/// Default implementation of IUpscalerCallbacks.
	/// These are fine for testing but a proper game will want to extend and override these methods.
	/// </summary>
	public class UpscalerCallbacksBase: IUpscalerCallbacks
	{
		protected float CurrentBiasOffset = 0;

		public virtual void ApplyMipmapBias(float biasOffset)
		{
			if (float.IsNaN(biasOffset) || float.IsInfinity(biasOffset))
				return;
            
			CurrentBiasOffset += biasOffset;
            
			if (Mathf.Approximately(CurrentBiasOffset, 0f))
			{
				CurrentBiasOffset = 0f;
			}

			foreach (var texture in Resources.FindObjectsOfTypeAll<Texture2D>())
			{
				if (texture.mipmapCount <= 1)
					continue;
                
				texture.mipMapBias += biasOffset;
			}
		}

		public virtual void UndoMipmapBias()
		{
			if (CurrentBiasOffset == 0f)
				return;

			ApplyMipmapBias(-CurrentBiasOffset);
		}
	}
}
