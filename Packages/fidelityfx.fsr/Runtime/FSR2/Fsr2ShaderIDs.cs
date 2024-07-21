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

namespace FidelityFX.FSR2
{
    public static class Fsr2ShaderIDs
    {
        // Shader resource views, i.e. read-only bindings
        public static readonly int SrvInputColor = Shader.PropertyToID("r_input_color_jittered");
        public static readonly int SrvOpaqueOnly = Shader.PropertyToID("r_input_opaque_only");
        public static readonly int SrvInputMotionVectors = Shader.PropertyToID("r_input_motion_vectors");
        public static readonly int SrvInputDepth = Shader.PropertyToID("r_input_depth");
        public static readonly int SrvInputExposure = Shader.PropertyToID("r_input_exposure");
        public static readonly int SrvAutoExposure = Shader.PropertyToID("r_auto_exposure");
        public static readonly int SrvReactiveMask = Shader.PropertyToID("r_reactive_mask");
        public static readonly int SrvTransparencyAndCompositionMask = Shader.PropertyToID("r_transparency_and_composition_mask");
        public static readonly int SrvReconstructedPrevNearestDepth = Shader.PropertyToID("r_reconstructed_previous_nearest_depth");
        public static readonly int SrvDilatedMotionVectors = Shader.PropertyToID("r_dilated_motion_vectors");
        public static readonly int SrvPrevDilatedMotionVectors = Shader.PropertyToID("r_previous_dilated_motion_vectors");
        public static readonly int SrvDilatedDepth = Shader.PropertyToID("r_dilatedDepth");
        public static readonly int SrvInternalUpscaled = Shader.PropertyToID("r_internal_upscaled_color");
        public static readonly int SrvLockStatus = Shader.PropertyToID("r_lock_status");
        public static readonly int SrvLockInputLuma = Shader.PropertyToID("r_lock_input_luma");
        public static readonly int SrvPreparedInputColor = Shader.PropertyToID("r_prepared_input_color");
        public static readonly int SrvLumaHistory = Shader.PropertyToID("r_luma_history");
        public static readonly int SrvRcasInput = Shader.PropertyToID("r_rcas_input");
        public static readonly int SrvLanczosLut = Shader.PropertyToID("r_lanczos_lut");
        public static readonly int SrvSceneLuminanceMips = Shader.PropertyToID("r_imgMips");
        public static readonly int SrvUpscaleMaximumBiasLut = Shader.PropertyToID("r_upsample_maximum_bias_lut");
        public static readonly int SrvDilatedReactiveMasks = Shader.PropertyToID("r_dilated_reactive_masks");
        public static readonly int SrvPrevColorPreAlpha = Shader.PropertyToID("r_input_prev_color_pre_alpha");
        public static readonly int SrvPrevColorPostAlpha = Shader.PropertyToID("r_input_prev_color_post_alpha");

        // Unordered access views, i.e. random read/write bindings
        public static readonly int UavReconstructedPrevNearestDepth = Shader.PropertyToID("rw_reconstructed_previous_nearest_depth");
        public static readonly int UavDilatedMotionVectors = Shader.PropertyToID("rw_dilated_motion_vectors");
        public static readonly int UavDilatedDepth = Shader.PropertyToID("rw_dilatedDepth");
        public static readonly int UavInternalUpscaled = Shader.PropertyToID("rw_internal_upscaled_color");
        public static readonly int UavLockStatus = Shader.PropertyToID("rw_lock_status");
        public static readonly int UavLockInputLuma = Shader.PropertyToID("rw_lock_input_luma");
        public static readonly int UavNewLocks = Shader.PropertyToID("rw_new_locks");
        public static readonly int UavPreparedInputColor = Shader.PropertyToID("rw_prepared_input_color");
        public static readonly int UavLumaHistory = Shader.PropertyToID("rw_luma_history");
        public static readonly int UavUpscaledOutput = Shader.PropertyToID("rw_upscaled_output");
        public static readonly int UavExposureMipLumaChange = Shader.PropertyToID("rw_img_mip_shading_change");
        public static readonly int UavExposureMip5 = Shader.PropertyToID("rw_img_mip_5");
        public static readonly int UavDilatedReactiveMasks = Shader.PropertyToID("rw_dilated_reactive_masks");
        public static readonly int UavAutoExposure = Shader.PropertyToID("rw_auto_exposure");
        public static readonly int UavSpdAtomicCount = Shader.PropertyToID("rw_spd_global_atomic");
        public static readonly int UavAutoReactive = Shader.PropertyToID("rw_output_autoreactive");
        public static readonly int UavAutoComposition = Shader.PropertyToID("rw_output_autocomposition");
        public static readonly int UavPrevColorPreAlpha = Shader.PropertyToID("rw_output_prev_color_pre_alpha");
        public static readonly int UavPrevColorPostAlpha = Shader.PropertyToID("rw_output_prev_color_post_alpha");

        // Constant buffer bindings
        public static readonly int CbFsr2 = Shader.PropertyToID("cbFSR2");
        public static readonly int CbSpd = Shader.PropertyToID("cbSPD");
        public static readonly int CbRcas = Shader.PropertyToID("cbRCAS");
        public static readonly int CbGenReactive = Shader.PropertyToID("cbGenerateReactive");
    }
}
