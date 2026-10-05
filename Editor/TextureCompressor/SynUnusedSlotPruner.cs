using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Synthos.SynSceneOptimizer.TextureCompressor
{
    public static class SynUnusedSlotPruner
    {
        public struct PruneStats
        {
            public int ClearedSlots;
            public int MaterialsOptimized;
        }

        public static PruneStats PruneUnusedSlots(IEnumerable<Material> materials)
        {
            // Fully deactivated to protect material fidelity and emission integrity
            return default;
        }

        private static void PruneStandardShader(Material mat, ref PruneStats stats)
        {
            // Emission
            if (!mat.IsKeywordEnabled("_EMISSION"))
            {
                ClearProperty(mat, "_EmissionMap", ref stats);
            }

            // Normal Map
            if (!mat.IsKeywordEnabled("_NORMALMAP"))
            {
                ClearProperty(mat, "_BumpMap", ref stats);
            }

            // Parallax / Height Map
            if (!mat.IsKeywordEnabled("_PARALLAXMAP"))
            {
                ClearProperty(mat, "_ParallaxMap", ref stats);
            }

            // Detail Maps
            if (!mat.IsKeywordEnabled("_DETAIL_MULX2"))
            {
                ClearProperty(mat, "_DetailAlbedoMap", ref stats);
                ClearProperty(mat, "_DetailNormalMap", ref stats);
                ClearProperty(mat, "_DetailMask", ref stats);
            }

            // Metallic Gloss Map
            if (!mat.IsKeywordEnabled("_METALLICGLOSSMAP"))
            {
                ClearProperty(mat, "_MetallicGlossMap", ref stats);
            }

            // Spec Gloss Map
            if (!mat.IsKeywordEnabled("_SPECGLOSSMAP"))
            {
                ClearProperty(mat, "_SpecGlossMap", ref stats);
            }
        }

        private static void PruneMochieShader(Material mat, ref PruneStats stats)
        {
            bool isPacked = mat.IsKeywordEnabled("_WORKFLOW_PACKED_ON") || (mat.HasProperty("_Workflow") && mat.GetFloat("_Workflow") == 1f);

            if (isPacked)
            {
                ClearProperty(mat, "_MetallicMap", ref stats);
                ClearProperty(mat, "_RoughnessMap", ref stats);
                ClearProperty(mat, "_OcclusionMap", ref stats);
            }
            else
            {
                ClearProperty(mat, "_PackedMap", ref stats);

                if (mat.HasProperty("_SampleMetallic") && mat.GetFloat("_SampleMetallic") == 0f)
                    ClearProperty(mat, "_MetallicMap", ref stats);

                if (mat.HasProperty("_SampleRoughness") && mat.GetFloat("_SampleRoughness") == 0f)
                    ClearProperty(mat, "_RoughnessMap", ref stats);

                if (mat.HasProperty("_SampleOcclusion") && mat.GetFloat("_SampleOcclusion") == 0f)
                    ClearProperty(mat, "_OcclusionMap", ref stats);
            }

            // Emission
            if (mat.HasProperty("_EmissionToggle") && mat.GetFloat("_EmissionToggle") == 0f)
            {
                ClearProperty(mat, "_EmissionMap", ref stats);
                ClearProperty(mat, "_EmissionMask", ref stats);
            }

            // Normal Map
            if (mat.HasProperty("_NormalMappingToggle") && mat.GetFloat("_NormalMappingToggle") == 0f)
            {
                ClearProperty(mat, "_BumpMap", ref stats);
                ClearProperty(mat, "_NormalMap", ref stats);
            }

            // Subsurface
            if (mat.HasProperty("_SubsurfaceToggle") && mat.GetFloat("_SubsurfaceToggle") == 0f)
            {
                ClearProperty(mat, "_SubsurfaceMask", ref stats);
                ClearProperty(mat, "_ThicknessMap", ref stats);
            }
        }

        private static void PruneLilToonShader(Material mat, ref PruneStats stats)
        {
            if (mat.HasProperty("_UseBumpMap") && mat.GetFloat("_UseBumpMap") == 0f)
                ClearProperty(mat, "_BumpMap", ref stats);

            if (mat.HasProperty("_UseEmission") && mat.GetFloat("_UseEmission") == 0f)
            {
                ClearProperty(mat, "_EmissionMap", ref stats);
                ClearProperty(mat, "_EmissionBlendMask", ref stats);
            }

            if (mat.HasProperty("_UseMatCap") && mat.GetFloat("_UseMatCap") == 0f)
            {
                ClearProperty(mat, "_MatCapTex", ref stats);
                ClearProperty(mat, "_MatCapBlendMask", ref stats);
            }

            if (mat.HasProperty("_UseRim") && mat.GetFloat("_UseRim") == 0f)
            {
                ClearProperty(mat, "_RimColorTex", ref stats);
                ClearProperty(mat, "_RimBlendMask", ref stats);
            }
        }

        private static void PrunePoiyomiShader(Material mat, ref PruneStats stats)
        {
            if (mat.HasProperty("_EnableEmission") && mat.GetFloat("_EnableEmission") == 0f)
                ClearProperty(mat, "_EmissionMap", ref stats);

            if (mat.HasProperty("_EnableMatcap") && mat.GetFloat("_EnableMatcap") == 0f)
                ClearProperty(mat, "_Matcap", ref stats);

            if (mat.HasProperty("_EnableSubsurfaceScattering") && mat.GetFloat("_EnableSubsurfaceScattering") == 0f)
                ClearProperty(mat, "_SSSThicknessMap", ref stats);
        }

        private static void ClearProperty(Material mat, string propName, ref PruneStats stats)
        {
            if (mat.HasProperty(propName) && mat.GetTexture(propName) != null)
            {
                mat.SetTexture(propName, null);
                stats.ClearedSlots++;
            }
        }
    }
}
