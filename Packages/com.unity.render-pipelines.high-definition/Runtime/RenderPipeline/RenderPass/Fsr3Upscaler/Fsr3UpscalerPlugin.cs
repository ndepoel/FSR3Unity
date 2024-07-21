using System;
using System.Collections.Generic;
using FidelityFX;
using FidelityFX.FSR3;

// We use an old C# trick here to override the UnityEngine.AMD namespace and force FSR2Pass to use code from this namespace instead.
// By exactly mimicking the interface of the AMDUnityPlugin module, we can replace Unity's implementation of FSR2 with one of our own.
// Here we redirect all calls to the open source FSR3 Upscaler port with a bit of glue logic, making the entire thing cross-platform compatible. 
namespace UnityEngine.Rendering.HighDefinition.AMD
{
    public static class AMDUnityPlugin
    {
        internal static Fsr3UpscalerAssets Assets;
        
        static AMDUnityPlugin()
        {
            _ = Load();
        }

        public static bool Load()
        {
            Unload();
            
            Assets = Resources.Load<Fsr3UpscalerAssets>("FSR3 Upscaler Assets");
            return Assets != null;
        }

        public static bool IsLoaded() => Assets != null;

        internal static void Unload()
        {
            if (Assets != null)
            {
                Resources.UnloadAsset(Assets);
                Assets = null;
            }
        }
    }

    public class GraphicsDevice
    {
        private static GraphicsDevice sGraphicsDevice;
        
        public static GraphicsDevice device => sGraphicsDevice;

        public static int version => 0x00;

        private readonly List<FSR2Context> _contexts = new();
        
        public static GraphicsDevice CreateGraphicsDevice()
        {
            if (sGraphicsDevice != null)
            {
                sGraphicsDevice.Destroy();
                sGraphicsDevice = null;
            }

            var graphicsDevice = new GraphicsDevice();
            if (graphicsDevice.Initialize())
            {
                sGraphicsDevice = graphicsDevice;
                return graphicsDevice;
            }
            
            Debug.LogWarning("Failed to initialize FSR3 Upscaler Graphics Device");
            return null;
        }

#if UNITY_EDITOR
        private GraphicsDevice()
        {
            // Ensure resources are properly cleaned up during an in-editor domain reload
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Destroy;
        }
#endif
        
        private bool Initialize()
        {
            return AMDUnityPlugin.Load();
        }

        private void Destroy()
        {
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Destroy;
#endif
            
            foreach (var context in _contexts)
            {
                context.Destroy();
            }
            
            _contexts.Clear();
            
            AMDUnityPlugin.Unload();
        }

        public FSR2Context CreateFeature(CommandBuffer cmd, in FSR2CommandInitializationData initSettings)
        {
            var context = new FSR2Context();
            if (!context.Initialize(initSettings))
                return null;
            
            _contexts.Add(context);
            return context;
        }

        public void DestroyFeature(CommandBuffer cmd, FSR2Context fsrContext)
        {
            if (fsrContext == null)
                return;
            
            fsrContext.Destroy();
            _contexts.Remove(fsrContext);
        }

        public void ExecuteFSR2(CommandBuffer cmd, FSR2Context fsrContext, in FSR2TextureTable textures)
        {
            if (fsrContext == null)
                return;
            
            fsrContext.Execute(cmd, textures);
        }

        public bool GetRenderResolutionFromQualityMode(FSR2Quality qualityMode, uint displayWidth, uint displayHeight, out uint renderWidth, out uint renderHeight)
        {
            Fsr3Upscaler.GetRenderResolutionFromQualityMode(out int rw, out int rh, (int)displayWidth, (int)displayHeight, ConvertQualityMode(qualityMode));
            renderWidth = (uint)rw;
            renderHeight = (uint)rh;
            return true;
        }

        public float GetUpscaleRatioFromQualityMode(FSR2Quality qualityMode)
        {
            return Fsr3Upscaler.GetUpscaleRatioFromQualityMode(ConvertQualityMode(qualityMode));
        }

        private static Fsr3Upscaler.QualityMode ConvertQualityMode(FSR2Quality qualityMode)
        {
            // FSR3 offers two more quality modes (Native AA and Ultra Quality) than standard FSR2, so a conversion is needed
            const int diff = (int)Fsr3Upscaler.QualityMode.Quality - (int)FSR2Quality.Quality;
            return (Fsr3Upscaler.QualityMode)((int)qualityMode + diff);
        }
    }

    public class FSR2Context
    {
        public static bool DrawDebugView { get; set; } = false;
        
        private FSR2CommandInitializationData _initData;
        public ref readonly FSR2CommandInitializationData initData => ref _initData;

        private FSR2CommandExecutionData _executeData;
        public ref FSR2CommandExecutionData executeData => ref _executeData;

        private readonly Fsr3UpscalerContext _context = new();
        private readonly Fsr3Upscaler.DispatchDescription _dispatchDescription = new();
        
        internal bool Initialize(in FSR2CommandInitializationData initSettings)
        {
            _initData = initSettings;
            _executeData = new FSR2CommandExecutionData();

            if (AMDUnityPlugin.Assets == null || AMDUnityPlugin.Assets.shaders == null)
                return false;

            Fsr3Upscaler.InitializationFlags flags = 0;
            if (initSettings.GetFlag(FfxFsr2InitializationFlags.EnableHighDynamicRange)) flags |= Fsr3Upscaler.InitializationFlags.EnableHighDynamicRange;
            if (initSettings.GetFlag(FfxFsr2InitializationFlags.EnableDisplayResolutionMotionVectors)) flags |= Fsr3Upscaler.InitializationFlags.EnableDisplayResolutionMotionVectors;
            if (initSettings.GetFlag(FfxFsr2InitializationFlags.EnableMotionVectorsJitterCancellation)) flags |= Fsr3Upscaler.InitializationFlags.EnableMotionVectorsJitterCancellation;
            if (initSettings.GetFlag(FfxFsr2InitializationFlags.DepthInverted)) flags |= Fsr3Upscaler.InitializationFlags.EnableDepthInverted;
            if (initSettings.GetFlag(FfxFsr2InitializationFlags.EnableDepthInfinite)) flags |= Fsr3Upscaler.InitializationFlags.EnableDepthInfinite;
            if (initSettings.GetFlag(FfxFsr2InitializationFlags.EnableAutoExposure)) flags |= Fsr3Upscaler.InitializationFlags.EnableAutoExposure;
            if (initSettings.GetFlag(FfxFsr2InitializationFlags.EnableDynamicResolution)) flags |= Fsr3Upscaler.InitializationFlags.EnableDynamicResolution;
            
            var contextDescription = new Fsr3Upscaler.ContextDescription
            {
                Flags = flags,
                MaxRenderSize = new Vector2Int((int)initSettings.maxRenderSizeWidth, (int)initSettings.maxRenderSizeHeight),
                MaxUpscaleSize = new Vector2Int((int)initSettings.displaySizeWidth, (int)initSettings.displaySizeHeight),
                Shaders = AMDUnityPlugin.Assets.shaders,
            };
            
            _context.Create(contextDescription);
            return true;
        }

        internal void Destroy()
        {
            _context.Destroy();
        }

        internal void Execute(CommandBuffer cmd, in FSR2TextureTable textures)
        {
            _dispatchDescription.Color = new ResourceView(textures.colorInput);
            _dispatchDescription.Depth = new ResourceView(textures.depth);
            _dispatchDescription.MotionVectors = new ResourceView(textures.motionVectors);
            _dispatchDescription.Exposure = new ResourceView(textures.exposureTexture);
            _dispatchDescription.Reactive = new ResourceView(textures.biasColorMask);
            _dispatchDescription.TransparencyAndComposition = new ResourceView(textures.transparencyMask);
            _dispatchDescription.Output = new ResourceView(textures.colorOutput);
            _dispatchDescription.JitterOffset = new Vector2(_executeData.jitterOffsetX, _executeData.jitterOffsetY);
            _dispatchDescription.MotionVectorScale = new Vector2(_executeData.MVScaleX, _executeData.MVScaleY);
            _dispatchDescription.RenderSize = new Vector2Int((int)_executeData.renderSizeWidth, (int)_executeData.renderSizeHeight);
            _dispatchDescription.UpscaleSize = new Vector2Int((int)_initData.displaySizeWidth, (int)_initData.displaySizeHeight);
            _dispatchDescription.EnableSharpening = _executeData.enableSharpening != 0;
            _dispatchDescription.Sharpness = _executeData.sharpness;
            _dispatchDescription.FrameTimeDelta = _executeData.frameTimeDelta / 1000f;
            _dispatchDescription.PreExposure = _executeData.preExposure;
            _dispatchDescription.Reset = _executeData.reset != 0;
            _dispatchDescription.CameraNear = _executeData.cameraNear;
            _dispatchDescription.CameraFar = _executeData.cameraFar;
            _dispatchDescription.CameraFovAngleVertical = _executeData.cameraFovAngleVertical;
            _dispatchDescription.ViewSpaceToMetersFactor = 1.0f;    // 1 unit == 1 meter in Unity
            _dispatchDescription.Flags = DrawDebugView ? Fsr3Upscaler.DispatchFlags.DrawDebugView : 0;
            _dispatchDescription.UseTextureArrays = TextureXR.useTexArray && textures.colorInput.dimension == TextureDimension.Tex2DArray;

            _context.Dispatch(_dispatchDescription, cmd);
        }
    }

    public struct FSR2CommandInitializationData
    {
        public uint displaySizeHeight;
        public uint displaySizeWidth;
        public FfxFsr2InitializationFlags ffxFsrFlags;
        public uint maxRenderSizeHeight;
        public uint maxRenderSizeWidth;

        public readonly bool GetFlag(FfxFsr2InitializationFlags flag)
        {
            return (ffxFsrFlags & flag) == flag;
        }

        public void SetFlag(FfxFsr2InitializationFlags flag, bool value)
        {
            if (value)
                ffxFsrFlags |= flag;
            else
                ffxFsrFlags &= ~flag;
        }
    }
    
    public struct FSR2CommandExecutionData
    {
        public float cameraFar;
        public float cameraFovAngleVertical;
        public float cameraNear;
        public int enableSharpening;
        public float frameTimeDelta;
        public float jitterOffsetX;
        public float jitterOffsetY;
        public float MVScaleX;
        public float MVScaleY;
        public float preExposure;
        public uint renderSizeHeight;
        public uint renderSizeWidth;
        public int reset;
        public float sharpness;
    }
    
    public struct FSR2TextureTable
    {
        public Texture biasColorMask;
        public Texture colorInput;
        public Texture colorOutput;
        public Texture depth;
        public Texture exposureTexture;
        public Texture motionVectors;
        public Texture reactiveMask;    // Note: reactiveMask does not seem to be used at all by HDRP, instead we get a biasColorMask
        public Texture transparencyMask;
    }

    [Flags]
    public enum FfxFsr2InitializationFlags
    {
        EnableHighDynamicRange = 1 << 0,
        EnableDisplayResolutionMotionVectors = 1 << 1,
        EnableMotionVectorsJitterCancellation = 1 << 2,
        DepthInverted = 1 << 3,
        EnableDepthInfinite = 1 << 4,
        EnableAutoExposure = 1 << 5,
        EnableDynamicResolution = 1 << 6,
        EnableTexture1DUsage = 1 << 7,
    }

    public enum FSR2Quality
    {
        Quality,
        Balanced,
        Performance,
        UltraPerformance,
    }
}
