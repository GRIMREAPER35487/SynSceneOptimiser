using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    public static class SynShaderAnalyzer
    {
        // Pre-compiled case-insensitive regex blueprints for unlit/matte shaders and reflective surfaces
        private static readonly Regex UnlitMatteRegex = new Regex(
            @"unlit|particle|textmeshpro|sprite|ui\/|decal|canvas|flat|hidden\/|skybox",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static readonly Regex ReflectionRegex = new Regex(
            @"(metal|smooth|water|fluid|glass|specular|mirror)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        public static bool IsUnlitOrMatte(Shader shader)
        {
            if (shader == null) return true;
            return UnlitMatteRegex.IsMatch(shader.name);
        }

        public static bool RequiresReflectionFidelity(Material mat)
        {
            if (mat == null) return false;
            
            string shaderName = mat.shader != null ? mat.shader.name : "";

            // 1. Check material name signature (explicit override)
            string matNameLower = mat.name.ToLower();
            if (matNameLower.Contains("metal") || matNameLower.Contains("smooth") ||
                matNameLower.Contains("water") || matNameLower.Contains("fluid") ||
                matNameLower.Contains("glass") || matNameLower.Contains("specular") ||
                matNameLower.Contains("mirror") || matNameLower.Contains("chrome") ||
                matNameLower.Contains("gold") || matNameLower.Contains("silver") ||
                matNameLower.Contains("shiny") || matNameLower.Contains("wet"))
            {
                return true;
            }

            // 2. Fetch threshold dynamically from settings, default to 0.05f
            float threshold = SynSceneOptimizerSettings.GetFloat("SmoothnessThreshold", 0.05f);

            // 3. Mochie Standard / Lite / Mobile shaders handling
            if (shaderName.Contains("Mochie") && (shaderName.Contains("Standard") || shaderName.Contains("Lite") || shaderName.Contains("Mobile")))
            {
                float metallic = mat.HasProperty("_MetallicStrength") ? mat.GetFloat("_MetallicStrength") : 0.0f;
                float roughness = mat.HasProperty("_RoughnessStrength") ? mat.GetFloat("_RoughnessStrength") : 1.0f;
                float smoothnessToggle = mat.HasProperty("_SmoothnessToggle") ? mat.GetFloat("_SmoothnessToggle") : 0.0f;

                bool hasMetallicMap = mat.HasProperty("_SampleMetallic") && mat.GetFloat("_SampleMetallic") > 0 && mat.HasProperty("_MetallicMap") && mat.GetTexture("_MetallicMap") != null;
                bool hasRoughnessMap = mat.HasProperty("_SampleRoughness") && mat.GetFloat("_SampleRoughness") > 0 && mat.HasProperty("_RoughnessMap") && mat.GetTexture("_RoughnessMap") != null;
                bool hasPackedMap = mat.HasProperty("_PackedMap") && mat.GetTexture("_PackedMap") != null;

                if (hasMetallicMap || hasRoughnessMap || hasPackedMap)
                {
                    return true;
                }

                // If smoothnessToggle == 0, slider is Roughness (0 = smooth, 1 = rough)
                // If smoothnessToggle == 1, slider is Smoothness (0 = rough, 1 = smooth)
                float actualSmoothness = 0.0f;
                if (smoothnessToggle == 1.0f)
                {
                    actualSmoothness = roughness; 
                }
                else
                {
                    actualSmoothness = 1.0f - roughness; 
                }

                if (metallic >= threshold || actualSmoothness >= threshold)
                {
                    return true;
                }

                return false;
            }

            // 4. Standard / Standard (Specular Setup) Unity shaders
            float defaultMetallic = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0.0f;
            float defaultGlossiness = mat.HasProperty("_Glossiness") ? mat.GetFloat("_Glossiness") : (mat.HasProperty("_Smoothness") ? mat.GetFloat("_Smoothness") : 0.0f);
            
            bool hasDefaultMetallicMap = mat.HasProperty("_MetallicGlossMap") && mat.GetTexture("_MetallicGlossMap") != null;
            bool hasSpecGlossMap = mat.HasProperty("_SpecGlossMap") && mat.GetTexture("_SpecGlossMap") != null;

            if (hasDefaultMetallicMap || hasSpecGlossMap)
            {
                return true;
            }

            if (defaultMetallic >= threshold || defaultGlossiness >= threshold)
            {
                return true;
            }

            // 5. Generic fallback for any other shaders
            Shader shader = mat.shader;
            if (shader != null)
            {
                int propCount = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < propCount; i++)
                {
                    string propName = ShaderUtil.GetPropertyName(shader, i);
                    // Exclude properties that are default settings or strengths
                    if (propName.Contains("Strength") || propName.Contains("Toggle") || propName.Contains("Contrast") || propName.Contains("Brightness") || propName.Contains("Model"))
                    {
                        continue;
                    }

                    if (propName.ToLower().Contains("metallic") || propName.ToLower().Contains("smoothness") || propName.ToLower().Contains("glossiness"))
                    {
                        ShaderUtil.ShaderPropertyType propType = ShaderUtil.GetPropertyType(shader, i);
                        if (propType == ShaderUtil.ShaderPropertyType.Float || propType == ShaderUtil.ShaderPropertyType.Range)
                        {
                            if (mat.HasProperty(propName) && mat.GetFloat(propName) >= threshold) return true;
                        }
                        else if (propType == ShaderUtil.ShaderPropertyType.TexEnv)
                        {
                            if (mat.HasProperty(propName) && mat.GetTexture(propName) != null) return true;
                        }
                    }
                }
            }

            return false;
        }

        public static bool HasVertexManipulation(Material mat)
        {
            if (mat == null || mat.shader == null) return false;

            string shaderName = mat.shader.name.ToLower();

            // 1. Check shader name for common vertex animation/deformation tags
            if (shaderName.Contains("wind") || shaderName.Contains("sway") || 
                shaderName.Contains("grass") || shaderName.Contains("foliage") || 
                shaderName.Contains("water") || shaderName.Contains("fluid") || 
                shaderName.Contains("wave") || shaderName.Contains("ocean") || 
                shaderName.Contains("shake") || shaderName.Contains("rotate") || 
                shaderName.Contains("spin") || shaderName.Contains("clock") || 
                shaderName.Contains("audiolink") || shaderName.Contains("vertex"))
            {
                return true;
            }

            // 2. Check specific properties that enable vertex deformation/animation
            string[] animProperties = new string[]
            {
                "_WindSway", "_WindSwayToggle", "_VertexAnim", "_VertexAnimToggle",
                "_WaveSpeed", "_RotationSpeed", "_Speed", "_SpinSpeed",
                "_WindContribution0", "_WindContribution1", "_WindContribution2",
                "_VertexPositionToggle", "_EnableVertexAnim", "_UseWind"
            };

            foreach (string prop in animProperties)
            {
                if (mat.HasProperty(prop))
                {
                    float val = mat.GetFloat(prop);
                    if (val > 0.0f)
                    {
                        return true;
                    }
                }
            }

            // 3. Check common material keywords
            string[] animKeywords = new string[]
            {
                "_VERTEX_ANIMATION_ON", "_WIND_ON", "_VERTEX_ANIM_ON", "_USE_WIND"
            };

            foreach (string kw in animKeywords)
            {
                if (mat.IsKeywordEnabled(kw))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
