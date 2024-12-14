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

// Suppress a few warnings produced by FFX's HLSL code
#pragma warning(disable: 3078)      // Loop control variable conflicts
#pragma warning(disable: 3203)      // Signed/unsigned mismatch
#pragma warning(disable: 3556)      // Integer divides might be much slower, try using uints if possible

#define FFX_GPU         // Compiling for GPU
#define FFX_HLSL        // Compile for plain HLSL

// Use the DXC shader compiler on modern graphics APIs to enable a few advanced features
// The DXC-related pragmas are disabled by default, as DXC doesn't support all platforms yet and will break on some platforms when enabled.
// Consider this to be an experimental feature. If you want to benefit from 16-bit floating point and wave operations, and don't care about supporting older graphics APIs, then it's worth a try. 
//#if defined(SHADER_API_D3D12) || defined(SHADER_API_VULKAN) || defined(SHADER_API_METAL)
//#pragma use_dxc   // Using DXC will currently break DX11 support since DX11 and DX12 share the same shader bytecode in Unity.
//#endif

// Enable half precision data types on platforms that support it
//#if defined(UNITY_COMPILER_DXC) && defined(FFX_HALF)
//#pragma require Native16Bit
//#endif

// Allow use of Xbox Series-specific optimizations
// #if defined(SHADER_API_GAMECORE_XBOXSERIES)
// #define __XBOX_SCARLETT
// #endif

// Hack to work around the lack of texture atomics on Metal
#if defined(SHADER_API_METAL)
#define InterlockedAdd(dest, val, orig)     { (orig) = (dest); (dest) += (val); }
#define InterlockedMin(dest, val)           { (dest) = min((dest), (val)); }
#define InterlockedMax(dest, val)           { (dest) = max((dest), (val)); }
#endif

// Workaround for HDRP using texture arrays for its camera buffers on some platforms
// The below defines are adapted from: Packages/com.unity.render-pipelines.core/ShaderLibrary/TextureXR.hlsl
#if ((defined(SHADER_API_D3D11) || defined(SHADER_API_D3D12)) && !defined(SHADER_API_XBOXONE) && !defined(SHADER_API_GAMECORE)) || defined(SHADER_API_PSSL) || defined(SHADER_API_VULKAN)
    #define UNITY_TEXTURE2D_X_ARRAY_SUPPORTED
#endif

// Control if TEXTURE2D_X macros will expand to texture arrays
#if defined(UNITY_TEXTURE2D_X_ARRAY_SUPPORTED) && defined(UNITY_FSR_TEXTURE2D_X_ARRAY)
    #define USE_TEXTURE2D_X_AS_ARRAY
#endif

// Early defines for single-pass instancing
#if defined(STEREO_INSTANCING_ON) && defined(UNITY_TEXTURE2D_X_ARRAY_SUPPORTED)
    #define UNITY_STEREO_INSTANCING_ENABLED
#endif

// Helper macros to handle XR single-pass with Texture2DArray
#if defined(USE_TEXTURE2D_X_AS_ARRAY)

    // Only single-pass stereo instancing used array indexing
    #if defined(UNITY_STEREO_INSTANCING_ENABLED)
        static uint unity_StereoEyeIndex;
        #define SLICE_ARRAY_INDEX   unity_StereoEyeIndex
    #else
        #define SLICE_ARRAY_INDEX  0
    #endif

    // Declare and sample camera buffers as texture arrays
    #define UNITY_FSR_TEX2D(type)          Texture2DArray<type>
    #define UNITY_FSR_RWTEX2D(type)        RWTexture2DArray<type>
    #define UNITY_FSR_POS(pxPos)           FfxUInt32x3(pxPos, SLICE_ARRAY_INDEX)
    #define UNITY_FSR_UV(uv)               FfxFloat32x3(uv, SLICE_ARRAY_INDEX)
    #define UNITY_FSR_GETDIMS(tex, w, h)   { FfxUInt32 uElements; (tex).GetDimensions((w), (h), uElements); }
    
#endif
