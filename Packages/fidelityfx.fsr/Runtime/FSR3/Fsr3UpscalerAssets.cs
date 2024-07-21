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
using UnityEngine.Serialization;

namespace FidelityFX.FSR3
{
    /// <summary>
    /// Scriptable object containing all shader resources required by FidelityFX Super Resolution 3 (FSR3) Upscaler.
    /// These can be stored in an asset file and referenced from a scene or prefab, avoiding the need to load the shaders from a Resources folder.
    /// </summary>
    [CreateAssetMenu(fileName = "FSR3 Upscaler Assets", menuName = "FidelityFX/FSR3 Upscaler Assets", order = 1103)]
    public class Fsr3UpscalerAssets : ScriptableObject
    {
        public Fsr3UpscalerShaders shaders;
        
#if UNITY_EDITOR
        private void Reset()
        {
            shaders = new Fsr3UpscalerShaders
            {
                prepareInputsPass = FindComputeShader("ffx_fsr3upscaler_prepare_inputs_pass"),
                lumaPyramidPass = FindComputeShader("ffx_fsr3upscaler_luma_pyramid_pass"),
                shadingChangePyramidPass = FindComputeShader("ffx_fsr3upscaler_shading_change_pyramid_pass"),
                shadingChangePass = FindComputeShader("ffx_fsr3upscaler_shading_change_pass"),
                prepareReactivityPass = FindComputeShader("ffx_fsr3upscaler_prepare_reactivity_pass"),
                lumaInstabilityPass = FindComputeShader("ffx_fsr3upscaler_luma_instability_pass"),
                accumulatePass = FindComputeShader("ffx_fsr3upscaler_accumulate_pass"),
                sharpenPass = FindComputeShader("ffx_fsr3upscaler_rcas_pass"),
                autoGenReactivePass = FindComputeShader("ffx_fsr3upscaler_autogen_reactive_pass"),
                tcrAutoGenPass = FindComputeShader("ffx_fsr3upscaler_tcr_autogen_pass"),
                debugViewPass = FindComputeShader("ffx_fsr3upscaler_debug_view_pass"),
            };
        }

        private static ComputeShader FindComputeShader(string name)
        {
            string[] assetGuids = UnityEditor.AssetDatabase.FindAssets($"t:ComputeShader {name}");
            if (assetGuids == null || assetGuids.Length == 0)
                return null;

            string assetPath = UnityEditor.AssetDatabase.GUIDToAssetPath(assetGuids[0]);
            return UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(assetPath);
        }
#endif
    }
    
    /// <summary>
    /// All the compute shaders used by the FSR3 Upscaler.
    /// </summary>
    [System.Serializable]
    public class Fsr3UpscalerShaders
    {
        /// <summary>
        /// The compute shader used by the prepare inputs pass.
        /// </summary>
        public ComputeShader prepareInputsPass;
        
        /// <summary>
        /// The compute shader used by the luminance pyramid computation pass.
        /// </summary>
        public ComputeShader lumaPyramidPass;

        /// <summary>
        /// The compute shader used by the shading change pyramid pass.
        /// </summary>
        public ComputeShader shadingChangePyramidPass;

        /// <summary>
        /// The compute shader used by the shading change pass.
        /// </summary>
        public ComputeShader shadingChangePass;

        /// <summary>
        /// The compute shader used by the prepare reactivity pass.
        /// </summary>
        public ComputeShader prepareReactivityPass;
        
        /// <summary>
        /// The compute shader used by the luma instability pass.
        /// </summary>
        public ComputeShader lumaInstabilityPass;

        /// <summary>
        /// The compute shader used by the accumulation pass.
        /// </summary>
        public ComputeShader accumulatePass;

        /// <summary>
        /// The compute shader used by the RCAS sharpening pass.
        /// </summary>
        public ComputeShader sharpenPass;

        /// <summary>
        /// The compute shader used to auto-generate a reactive mask.
        /// </summary>
        public ComputeShader autoGenReactivePass;

        /// <summary>
        /// The compute shader used to auto-generate a transparency & composition mask.
        /// </summary>
        public ComputeShader tcrAutoGenPass;

        /// <summary>
        /// The compute shader used to display a debug view.
        /// </summary>
        public ComputeShader debugViewPass;
        
        /// <summary>
        /// Returns a copy of this class and its contents.
        /// </summary>
        public Fsr3UpscalerShaders Clone()
        {
            return (Fsr3UpscalerShaders)MemberwiseClone();
        }

        /// <summary>
        /// Returns a copy of this class with clones of all its shaders.
        /// This can be useful if you're running multiple FSR3 Upscaler instances with different shader configurations.
        /// Be sure to clean up these clones through Dispose once you're done with them.
        /// </summary>
        public Fsr3UpscalerShaders DeepCopy()
        {
            return new Fsr3UpscalerShaders
            {
                prepareInputsPass = Object.Instantiate(prepareInputsPass),
                lumaPyramidPass = Object.Instantiate(lumaPyramidPass),
                shadingChangePyramidPass = Object.Instantiate(shadingChangePyramidPass),
                shadingChangePass = Object.Instantiate(shadingChangePass),
                prepareReactivityPass = Object.Instantiate(prepareReactivityPass),
                lumaInstabilityPass = Object.Instantiate(lumaInstabilityPass),
                accumulatePass = Object.Instantiate(accumulatePass),
                sharpenPass = Object.Instantiate(sharpenPass),
                autoGenReactivePass = Object.Instantiate(autoGenReactivePass),
                tcrAutoGenPass = Object.Instantiate(tcrAutoGenPass),
                debugViewPass = Object.Instantiate(debugViewPass),
            };
        }

        /// <summary>
        /// Destroy all the shaders within this instance.
        /// Use this only on clones created through DeepCopy.
        /// </summary>
        public void Dispose()
        {
            Object.Destroy(prepareInputsPass);
            Object.Destroy(lumaPyramidPass);
            Object.Destroy(shadingChangePyramidPass);
            Object.Destroy(shadingChangePass);
            Object.Destroy(prepareReactivityPass);
            Object.Destroy(lumaInstabilityPass);
            Object.Destroy(accumulatePass);
            Object.Destroy(sharpenPass);
            Object.Destroy(autoGenReactivePass);
            Object.Destroy(tcrAutoGenPass);
            Object.Destroy(debugViewPass);
        }
    }
}
