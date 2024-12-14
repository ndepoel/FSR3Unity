using System;

namespace UnityEngine.Rendering.PostProcessing
{
	internal abstract class Upscaler
	{
		public abstract void CreateContext(PostProcessRenderContext context, Upscaling config);

		public abstract void DestroyContext();
		
		public abstract void Render(PostProcessRenderContext context, Upscaling config);
	}
}
