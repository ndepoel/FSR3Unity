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

using UnityEngine;

namespace FidelityFX
{
    internal static class Fsr2ShaderIDs
    {
        // Shader resource views, i.e. read-only bindings
        internal static readonly int SrvInputColor = Shader.PropertyToID("r_input_color_jittered");
        internal static readonly int SrvOpaqueOnly = Shader.PropertyToID("r_input_opaque_only");
        internal static readonly int SrvInputMotionVectors = Shader.PropertyToID("r_input_motion_vectors");
        internal static readonly int SrvInputDepth = Shader.PropertyToID("r_input_depth");
        internal static readonly int SrvInputExposure = Shader.PropertyToID("r_input_exposure");
        internal static readonly int SrvAutoExposure = Shader.PropertyToID("r_auto_exposure");
        internal static readonly int SrvReactiveMask = Shader.PropertyToID("r_reactive_mask");
        internal static readonly int SrvTransparencyAndCompositionMask = Shader.PropertyToID("r_transparency_and_composition_mask");
        internal static readonly int SrvReconstructedPrevNearestDepth = Shader.PropertyToID("r_reconstructed_previous_nearest_depth");
        internal static readonly int SrvDilatedMotionVectors = Shader.PropertyToID("r_dilated_motion_vectors");
        internal static readonly int SrvPrevDilatedMotionVectors = Shader.PropertyToID("r_previous_dilated_motion_vectors");
        internal static readonly int SrvDilatedDepth = Shader.PropertyToID("r_dilatedDepth");
        internal static readonly int SrvInternalUpscaled = Shader.PropertyToID("r_internal_upscaled_color");
        internal static readonly int SrvLockStatus = Shader.PropertyToID("r_lock_status");
        internal static readonly int SrvLockInputLuma = Shader.PropertyToID("r_lock_input_luma");
        internal static readonly int SrvPreparedInputColor = Shader.PropertyToID("r_prepared_input_color");
        internal static readonly int SrvLumaHistory = Shader.PropertyToID("r_luma_history");
        internal static readonly int SrvRcasInput = Shader.PropertyToID("r_rcas_input");
        internal static readonly int SrvLanczosLut = Shader.PropertyToID("r_lanczos_lut");
        internal static readonly int SrvSceneLuminanceMips = Shader.PropertyToID("r_imgMips");
        internal static readonly int SrvUpscaleMaximumBiasLut = Shader.PropertyToID("r_upsample_maximum_bias_lut");
        internal static readonly int SrvDilatedReactiveMasks = Shader.PropertyToID("r_dilated_reactive_masks");
        internal static readonly int SrvPrevColorPreAlpha = Shader.PropertyToID("r_input_prev_color_pre_alpha");
        internal static readonly int SrvPrevColorPostAlpha = Shader.PropertyToID("r_input_prev_color_post_alpha");

        // Unordered access views, i.e. random read/write bindings
        internal static readonly int UavReconstructedPrevNearestDepth = Shader.PropertyToID("rw_reconstructed_previous_nearest_depth");
        internal static readonly int UavDilatedMotionVectors = Shader.PropertyToID("rw_dilated_motion_vectors");
        internal static readonly int UavDilatedDepth = Shader.PropertyToID("rw_dilatedDepth");
        internal static readonly int UavInternalUpscaled = Shader.PropertyToID("rw_internal_upscaled_color");
        internal static readonly int UavLockStatus = Shader.PropertyToID("rw_lock_status");
        internal static readonly int UavLockInputLuma = Shader.PropertyToID("rw_lock_input_luma");
        internal static readonly int UavNewLocks = Shader.PropertyToID("rw_new_locks");
        internal static readonly int UavPreparedInputColor = Shader.PropertyToID("rw_prepared_input_color");
        internal static readonly int UavLumaHistory = Shader.PropertyToID("rw_luma_history");
        internal static readonly int UavUpscaledOutput = Shader.PropertyToID("rw_upscaled_output");
        internal static readonly int UavExposureMipLumaChange = Shader.PropertyToID("rw_img_mip_shading_change");
        internal static readonly int UavExposureMip5 = Shader.PropertyToID("rw_img_mip_5");
        internal static readonly int UavDilatedReactiveMasks = Shader.PropertyToID("rw_dilated_reactive_masks");
        internal static readonly int UavAutoExposure = Shader.PropertyToID("rw_auto_exposure");
        internal static readonly int UavSpdAtomicCount = Shader.PropertyToID("rw_spd_global_atomic");
        internal static readonly int UavAutoReactive = Shader.PropertyToID("rw_output_autoreactive");
        internal static readonly int UavAutoComposition = Shader.PropertyToID("rw_output_autocomposition");
        internal static readonly int UavPrevColorPreAlpha = Shader.PropertyToID("rw_output_prev_color_pre_alpha");
        internal static readonly int UavPrevColorPostAlpha = Shader.PropertyToID("rw_output_prev_color_post_alpha");

        // Constant buffer bindings
        internal static readonly int CbFsr2 = Shader.PropertyToID("cbFSR2");
        internal static readonly int CbSpd = Shader.PropertyToID("cbSPD");
        internal static readonly int CbRcas = Shader.PropertyToID("cbRCAS");
        internal static readonly int CbGenReactive = Shader.PropertyToID("cbGenerateReactive");
    }
}
