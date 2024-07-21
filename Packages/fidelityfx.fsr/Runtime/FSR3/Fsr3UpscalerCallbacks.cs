// Copyright (c) 2024 Nico de Poel
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

using UnityEngine;

namespace FidelityFX.FSR3
{
    /// <summary>
    /// A collection of callbacks required by the FSR3 Upscaler process.
    /// This allows some customization by the game dev on how to integrate FSR3 upscaling into their own game setup.
    /// </summary>
    public interface IFsr3UpscalerCallbacks
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
    /// Default implementation of IFsr3UpscalerCallbacks.
    /// These are fine for testing but a proper game will want to extend and override these methods.
    /// </summary>
    public class Fsr3UpscalerCallbacksBase: IFsr3UpscalerCallbacks
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
