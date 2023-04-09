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

// Suppress a few warnings produced by FFX's HLSL code
#pragma warning(disable: 3078)      // Loop control variable conflicts
#pragma warning(disable: 3203)      // Signed/unsigned mismatch

#define FFX_GPU         // Compiling for GPU
#define FFX_HLSL        // Compile for plain HLSL

// Use the DXC shader compiler on modern graphics APIs to enable a few advanced features
#if defined(SHADER_API_D3D12) || defined(SHADER_API_VULKAN) || defined(SHADER_API_METAL)
//#pragma use_dxc   // Using DXC will currently break DX11 support since DX11 and DX12 share the same shader bytecode in Unity. Disabling this by default... *sigh*
#endif

// Enable half precision data types on platforms that support it
#if defined(UNITY_COMPILER_DXC) && defined(FFX_HALF)
#pragma require Native16Bit
#endif

// Hack to work around the lack of texture atomics on Metal
#if defined(SHADER_API_METAL)
#define InterlockedAdd(dest, val, orig)     { (orig) = (dest); (dest) += (val); }
#define InterlockedMin(dest, val)           { (dest) = min((dest), (val)); }
#define InterlockedMax(dest, val)           { (dest) = max((dest), (val)); }
#endif
