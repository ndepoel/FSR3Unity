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

using UnityEngine.Rendering;

namespace FidelityFX
{
    /// <summary>
    /// An immutable structure wrapping all of the necessary information to bind a specific buffer or attachment of a render target to a compute shader.
    /// </summary>
    public readonly struct ResourceView
    {
        /// <summary>
        /// This value is the equivalent of not setting any value at all; all struct fields will have their default values.
        /// It does not refer to a valid texture, therefore any variable set to this value should be checked for IsValid and reassigned before being bound to a shader.
        /// </summary>
        public static readonly ResourceView Unassigned = new ResourceView(default);
            
        /// <summary>
        /// This value contains a valid texture reference that can be bound to a shader, however it is just an empty placeholder texture.
        /// Binding this to a shader can be seen as setting the texture variable inside the shader to null.
        /// </summary>
        public static readonly ResourceView None = new ResourceView(BuiltinRenderTextureType.None);
            
        public ResourceView(in RenderTargetIdentifier renderTarget, RenderTextureSubElement subElement = RenderTextureSubElement.Default, int mipLevel = 0)
        {
            RenderTarget = renderTarget;
            SubElement = subElement;
            MipLevel = mipLevel;
        }
            
        public bool IsValid => !RenderTarget.Equals(default);
            
        public readonly RenderTargetIdentifier RenderTarget;
        public readonly RenderTextureSubElement SubElement;
        public readonly int MipLevel;
    }
}
