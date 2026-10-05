using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    public class MobileShaderFallbackPass : SynOptimizationPass
    {
        public override string Id => "MobileShaderFallbackPass";
        public override string Name => "Mobile Shader Fallback";
        public override string Description => "Swaps heavy desktop shaders to optimized mobile equivalents when targeting mobile builds.";
        public override string Category => "Asset Fallback";
        public override int Priority => 1000;

        // Individual toggles for every variant path
        [SerializeField] private bool includeStandardVariants = true;
        [SerializeField] private bool includeStandardLiteVariants = true;

        public bool IncludeStandardVariants
        {
            get => includeStandardVariants;
            set => includeStandardVariants = value;
        }

        public bool IncludeStandardLiteVariants
        {
            get => includeStandardLiteVariants;
            set => includeStandardLiteVariants = value;
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            bool includeStandard = SynSceneOptimizerSettings.GetBool("Fallback_IncludeStandard", true);
            bool newIncludeStandard = EditorGUILayout.Toggle(new GUIContent("Include Standard", "Process Mochie/Standard variants."), includeStandard);
            if (newIncludeStandard != includeStandard)
            {
                SynSceneOptimizerSettings.SetBool("Fallback_IncludeStandard", newIncludeStandard);
            }

            bool includeLite = SynSceneOptimizerSettings.GetBool("Fallback_IncludeStandardLite", true);
            bool newIncludeLite = EditorGUILayout.Toggle(new GUIContent("Include Standard Lite", "Process Mochie/Standard Lite variants."), includeLite);
            if (newIncludeLite != includeLite)
            {
                SynSceneOptimizerSettings.SetBool("Fallback_IncludeStandardLite", newIncludeLite);
            }
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
            if (activeTarget != BuildTarget.Android && activeTarget != BuildTarget.iOS)
            {
                return;
            }

            // Sync structural code to use the user profile configuration system options updated via DrawGUI
            bool includeStandard = SynSceneOptimizerSettings.GetBool("Fallback_IncludeStandard", true);
            bool includeLite = SynSceneOptimizerSettings.GetBool("Fallback_IncludeStandardLite", true);

            int swappedCount = 0;
            HashSet<Material> processed = new HashSet<Material>();

            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;

                Material[] sharedMats = r.sharedMaterials;
                foreach (Material mat in sharedMats)
                {
                    if (mat == null || processed.Contains(mat)) continue;
                    if (SynProtectionData.IsProtected(mat)) continue;
                    processed.Add(mat);

                    SynVirtualMaterialState matState = SynPipelineCompactor.GetStagingState(mat);
                    if (matState == null) continue;

                    Shader currentShader = matState.TargetShader;
                    if (currentShader == null) continue;

                    string shaderName = currentShader.name;
                    string targetMobileShaderName = null;

                    // Evaluate Standard Variants
                    if (includeStandard)
                    {
                        if (shaderName == "Mochie/Standard")
                        {
                            targetMobileShaderName = "Mochie/Standard Mobile";
                        }
                        else if (shaderName == "Synthos/Mochie/Standard")
                        {
                            targetMobileShaderName = "Synthos/Mochie/Standard Mobile";
                        }
                    }

                    // Evaluate Standard Lite Variants (only check if a target wasn't already assigned)
                    if (includeLite && targetMobileShaderName == null)
                    {
                        if (shaderName == "Mochie/Standard Lite")
                        {
                            targetMobileShaderName = "Mochie/Standard Mobile";
                        }
                        else if (shaderName == "Synthos/Mochie/Standard Lite")
                        {
                            targetMobileShaderName = "Synthos/Mochie/Standard Mobile";
                        }
                    }

                    if (targetMobileShaderName != null)
                    {
                        Shader mobileShader = Shader.Find(targetMobileShaderName);
                        if (mobileShader != null)
                        {
                            matState.TargetShader = mobileShader;
                            matState.IsDirty = true;
                            if (!matState.AppliedPassTags.Contains("Mbl"))
                            {
                                matState.AppliedPassTags.Add("Mbl");
                            }
                            swappedCount++;
                        }
                    }
                }
            }

            if (swappedCount > 0)
            {
                SynPipelineCompactor.LogChange(
                    "Mobile Fallback",
                    string.Format("Swapped {0} materials to Mobile/Standard variants.", swappedCount)
                );
            }
        }
    }
}