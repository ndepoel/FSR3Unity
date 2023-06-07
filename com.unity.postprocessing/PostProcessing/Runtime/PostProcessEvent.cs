using System.Collections.Generic;

namespace UnityEngine.Rendering.PostProcessing
{
    /// <summary>
    /// Injection points for custom effects.
    /// </summary>
    public enum PostProcessEvent
    {
        /// <summary>
        /// Effects at this injection points will execute before transparent objects are rendered.
        /// These effects will be rendered at the internal render resolution.
        /// </summary>
        BeforeTransparent = 0,

        /// <summary>
        /// Effects at this injection point will execute before upscaling and temporal anti-aliasing.
        /// These effects will be rendered at the internal render resolution.
        /// </summary>
        BeforeUpscaling = 1,
        
        /// <summary>
        /// Effects at this injection points will execute after temporal anti-aliasing and before
        /// builtin effects are rendered.
        /// These effects will be rendered at the display resolution.
        /// </summary>
        BeforeStack = 2,

        /// <summary>
        /// Effects at this injection points will execute after builtin effects have been rendered
        /// and before the final pass that does FXAA and applies dithering.
        /// These effects will be rendered at the display resolution.
        /// </summary>
        AfterStack = 3,
    }

    // Box free comparer for our `PostProcessEvent` enum, else the runtime will box the type when
    // used  as a key in a dictionary, thus leading to garbage generation... *sigh*
    internal struct PostProcessEventComparer : IEqualityComparer<PostProcessEvent>
    {
        public bool Equals(PostProcessEvent x, PostProcessEvent y)
        {
            return x == y;
        }

        public int GetHashCode(PostProcessEvent obj)
        {
            return (int)obj;
        }
    }
}
