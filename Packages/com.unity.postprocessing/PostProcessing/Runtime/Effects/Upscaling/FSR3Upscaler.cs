using FidelityFX;
using FidelityFX.FSR3;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.PostProcessing
{
	internal class FSR3Upscaler: Upscaler
	{
		public static bool IsSupported => SystemInfo.supportsComputeShaders;
		
		private Fsr3UpscalerContext _fsrContext;
		
		private readonly Fsr3Upscaler.DispatchDescription _dispatchDescription = new();
		private readonly Fsr3Upscaler.GenerateReactiveDescription _genReactiveDescription = new();
		
		public override void CreateContext(PostProcessRenderContext context, Upscaling config)
		{
			// Initialize FSR3 Upscaler context
			Fsr3Upscaler.InitializationFlags flags = 0;
			if (context.camera.allowHDR) flags |= Fsr3Upscaler.InitializationFlags.EnableHighDynamicRange;
			if (config.exposureSource == Upscaling.ExposureSource.Auto) flags |= Fsr3Upscaler.InitializationFlags.EnableAutoExposure;
			if (RuntimeUtilities.IsDynamicResolutionEnabled(context.camera)) flags |= Fsr3Upscaler.InitializationFlags.EnableDynamicResolution;

			_fsrContext = Fsr3Upscaler.CreateContext(config.UpscaleSize, config.MaxRenderSize, context.resources.computeShaders.fsr3Upscaler, flags);
		}

		public override void DestroyContext()
		{
			if (_fsrContext != null)
			{
				_fsrContext.Destroy();
				_fsrContext = null;
			}
		}

		public override void Render(PostProcessRenderContext context, Upscaling config)
		{
			var cmd = context.command;
			cmd.BeginSample("FSR3 Upscaler");
			
			SetupDispatchDescription(context, config);
			
			if (config.autoGenerateReactiveMask)
			{
				SetupAutoReactiveDescription(context, config);
			
				var scaledRenderSize = _genReactiveDescription.RenderSize;
				cmd.GetTemporaryRT(Fsr3ShaderIDs.UavAutoReactive, scaledRenderSize.x, scaledRenderSize.y, 0, default, GraphicsFormat.R8_UNorm, 1, true);
				_fsrContext.GenerateReactiveMask(_genReactiveDescription, cmd);
				_dispatchDescription.Reactive = new ResourceView(Fsr3ShaderIDs.UavAutoReactive);
			}
			         
			_fsrContext.Dispatch(_dispatchDescription, cmd);
			
			cmd.EndSample("FSR3 Upscaler");
		}
		
        private void SetupDispatchDescription(PostProcessRenderContext context, Upscaling config)
        {
            var camera = context.camera;
            
            // Set up the main FSR3 Upscaler dispatch parameters
            _dispatchDescription.Color = new ResourceView(context.source);
            _dispatchDescription.Depth = new ResourceView(Upscaling.GetDepthTexture(context.camera), RenderTextureSubElement.Depth);
            _dispatchDescription.MotionVectors = new ResourceView(BuiltinRenderTextureType.MotionVectors);
            _dispatchDescription.Exposure = ResourceView.Unassigned;
            _dispatchDescription.Reactive = ResourceView.Unassigned;
            _dispatchDescription.TransparencyAndComposition = ResourceView.Unassigned;

            if (config.exposureSource == Upscaling.ExposureSource.Manual && config.exposure != null) _dispatchDescription.Exposure = new ResourceView(config.exposure);
            if (config.exposureSource == Upscaling.ExposureSource.Unity) _dispatchDescription.Exposure = new ResourceView(context.autoExposureTexture);
            if (config.reactiveMask != null) _dispatchDescription.Reactive = new ResourceView(config.reactiveMask);
            if (config.transparencyAndCompositionMask != null) _dispatchDescription.TransparencyAndComposition = new ResourceView(config.transparencyAndCompositionMask);

            var scaledRenderSize = config.GetScaledRenderSize(context.camera);
            
            _dispatchDescription.Output = new ResourceView(context.destination);
            _dispatchDescription.PreExposure = config.preExposure;
            _dispatchDescription.EnableSharpening = config.performSharpenPass;
            _dispatchDescription.Sharpness = config.sharpness;
            _dispatchDescription.JitterOffset = config.JitterOffset;
            _dispatchDescription.MotionVectorScale.x = -scaledRenderSize.x;
            _dispatchDescription.MotionVectorScale.y = -scaledRenderSize.y;
            _dispatchDescription.RenderSize = scaledRenderSize;
            _dispatchDescription.UpscaleSize = config.UpscaleSize;
            _dispatchDescription.FrameTimeDelta = Time.unscaledDeltaTime;
            _dispatchDescription.CameraNear = camera.nearClipPlane;
            _dispatchDescription.CameraFar = camera.farClipPlane;
            _dispatchDescription.CameraFovAngleVertical = camera.fieldOfView * Mathf.Deg2Rad;
            _dispatchDescription.ViewSpaceToMetersFactor = 1.0f; // 1 unit is 1 meter in Unity
            _dispatchDescription.VelocityFactor = config.velocityFactor;
            _dispatchDescription.Reset = config.Reset;

            // Set up the parameters for the optional experimental auto-TCR feature
            _dispatchDescription.EnableAutoReactive = config.autoGenerateTransparencyAndComposition;
            if (config.autoGenerateTransparencyAndComposition)
            {
                _dispatchDescription.ColorOpaqueOnly = new ResourceView(config.ColorOpaqueOnly);
                _dispatchDescription.AutoTcThreshold = config.generateTransparencyAndCompositionParameters.autoTcThreshold;
                _dispatchDescription.AutoTcScale = config.generateTransparencyAndCompositionParameters.autoTcScale;
                _dispatchDescription.AutoReactiveScale = config.generateTransparencyAndCompositionParameters.autoReactiveScale;
                _dispatchDescription.AutoReactiveMax = config.generateTransparencyAndCompositionParameters.autoReactiveMax;
            }

            if (SystemInfo.usesReversedZBuffer)
            {
                // Swap the near and far clip plane distances as FSR3 Upscaler expects this when using inverted depth
                (_dispatchDescription.CameraNear, _dispatchDescription.CameraFar) = (_dispatchDescription.CameraFar, _dispatchDescription.CameraNear);
            }
        }

        private void SetupAutoReactiveDescription(PostProcessRenderContext context, Upscaling config)
        {
            // Set up the parameters to auto-generate a reactive mask
            _genReactiveDescription.ColorOpaqueOnly = new ResourceView(config.ColorOpaqueOnly);
            _genReactiveDescription.ColorPreUpscale = new ResourceView(context.source);
            _genReactiveDescription.OutReactive = new ResourceView(Fsr3ShaderIDs.UavAutoReactive);
            _genReactiveDescription.RenderSize = config.GetScaledRenderSize(context.camera);
            _genReactiveDescription.Scale = config.generateReactiveParameters.scale;
            _genReactiveDescription.CutoffThreshold = config.generateReactiveParameters.cutoffThreshold;
            _genReactiveDescription.BinaryValue = config.generateReactiveParameters.binaryValue;
            _genReactiveDescription.Flags = (Fsr3Upscaler.GenerateReactiveFlags)config.generateReactiveParameters.flags;
        }
	}
}
