using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    public class SynDisableStochasticPass : SynOptimizationPass
    {
        public override string Id => "SynDisableStochasticPass";
        public override string Name => "Disable Stochastic Sampling";
        public override string Description => "Disables Stochastic texture sampling on Android and iOS to optimize mobile performance and rendering.";
        public override string Category => "Texture Optimization";
        public override int Priority => 450;

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
            if (activeTarget != BuildTarget.Android && activeTarget != BuildTarget.iOS)
            {
                return;
            }

            int disabledCount = 0;
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

                    bool modified = false;

                    // 1. Check Poiyomi properties
                    // _StochasticMode (KeywordEnum: Deliot Heitz, Hextile, None)
                    if (matState.TrackedFloats.ContainsKey("_StochasticMode"))
                    {
                        float currentMode = matState.TrackedFloats["_StochasticMode"];
                        if (currentMode != 2.0f) // 2 is None
                        {
                            matState.TrackedFloats["_StochasticMode"] = 2.0f;
                            matState.IsDirty = true;
                            modified = true;
                        }
                    }

                    // Look for any float property ending with "Stochastic" and set to 0.0f
                    List<string> floatProps = new List<string>(matState.TrackedFloats.Keys);
                    foreach (string propName in floatProps)
                    {
                        if (propName.EndsWith("Stochastic"))
                        {
                            if (matState.TrackedFloats[propName] != 0.0f)
                            {
                                matState.TrackedFloats[propName] = 0.0f;
                                matState.IsDirty = true;
                                modified = true;
                            }
                        }
                    }

                    // 2. Check Mochie Standard properties
                    // _PrimarySampleMode (0 = Default, 1 = Stochastic, 2 = Supersampling, 3 = Triplanar)
                    if (matState.TrackedFloats.ContainsKey("_PrimarySampleMode"))
                    {
                        if (matState.TrackedFloats["_PrimarySampleMode"] == 1.0f) // 1 is Stochastic
                        {
                            matState.TrackedFloats["_PrimarySampleMode"] = 0.0f; // Default
                            matState.IsDirty = true;
                            modified = true;
                        }
                    }
                    // _DetailSampleMode (0 = Default, 1 = Stochastic, 2 = Supersampling, 3 = Triplanar)
                    if (matState.TrackedFloats.ContainsKey("_DetailSampleMode"))
                    {
                        if (matState.TrackedFloats["_DetailSampleMode"] == 1.0f) // 1 is Stochastic
                        {
                            matState.TrackedFloats["_DetailSampleMode"] = 0.0f; // Default
                            matState.IsDirty = true;
                            modified = true;
                        }
                    }

                    // 3. Check Silent Glass properties
                    // _UseStochastic (0 = Disabled, 1 = Enabled)
                    if (matState.TrackedFloats.ContainsKey("_UseStochastic"))
                    {
                        if (matState.TrackedFloats["_UseStochastic"] != 0.0f)
                        {
                            matState.TrackedFloats["_UseStochastic"] = 0.0f;
                            matState.IsDirty = true;
                            modified = true;
                        }
                    }

                    // 4. Update keywords
                    // Disable Poiyomi stochastic keywords
                    if (matState.TrackedKeywords.Contains("_STOCHASTICMODE_DELIOT_HEITZ"))
                    {
                        matState.TrackedKeywords.Remove("_STOCHASTICMODE_DELIOT_HEITZ");
                        matState.IsDirty = true;
                        modified = true;
                    }
                    if (matState.TrackedKeywords.Contains("_STOCHASTICMODE_HEXTILE"))
                    {
                        matState.TrackedKeywords.Remove("_STOCHASTICMODE_HEXTILE");
                        matState.IsDirty = true;
                        modified = true;
                    }
                    if (matState.TrackedFloats.ContainsKey("_StochasticMode") && !matState.TrackedKeywords.Contains("_STOCHASTICMODE_NONE"))
                    {
                        matState.TrackedKeywords.Add("_STOCHASTICMODE_NONE");
                        matState.IsDirty = true;
                        modified = true;
                    }

                    // Disable Mochie stochastic keywords
                    if (matState.TrackedKeywords.Contains("_STOCHASTIC_ON"))
                    {
                        matState.TrackedKeywords.Remove("_STOCHASTIC_ON");
                        matState.IsDirty = true;
                        modified = true;
                    }
                    if (matState.TrackedKeywords.Contains("_STOCHASTIC_DETAIL_ON"))
                    {
                        matState.TrackedKeywords.Remove("_STOCHASTIC_DETAIL_ON");
                        matState.IsDirty = true;
                        modified = true;
                    }

                    // Disable Silent Glass stochastic keywords
                    if (matState.TrackedKeywords.Contains("STOCHASTIC"))
                    {
                        matState.TrackedKeywords.Remove("STOCHASTIC");
                        matState.IsDirty = true;
                        modified = true;
                    }

                    if (modified)
                    {
                        disabledCount++;
                        if (!matState.AppliedPassTags.Contains("NoStochastic"))
                        {
                            matState.AppliedPassTags.Add("NoStochastic");
                        }
                    }
                }
            }

            if (disabledCount > 0)
            {
                SynPipelineCompactor.LogChange(
                    "Disable Stochastic",
                    string.Format("Disabled Stochastic texture sampling on {0} materials.", disabledCount)
                );
            }
        }
    }
}
