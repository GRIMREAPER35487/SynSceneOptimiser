using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    public class SynSceneOptimizerSettings
    {
        private const string KeyPrefix = "Synthos.SynSceneOptimizer.";

        public static bool GetBool(string key, bool defaultValue = false)
        {
            return EditorPrefs.GetBool(KeyPrefix + key, defaultValue);
        }

        public static void SetBool(string key, bool value)
        {
            EditorPrefs.SetBool(KeyPrefix + key, value);
        }

        public static float GetFloat(string key, float defaultValue = 0f)
        {
            return EditorPrefs.GetFloat(KeyPrefix + key, defaultValue);
        }

        public static void SetFloat(string key, float value)
        {
            EditorPrefs.SetFloat(KeyPrefix + key, value);
        }

        public static string GetString(string key, string defaultValue = "")
        {
            return EditorPrefs.GetString(KeyPrefix + key, defaultValue);
        }

        public static void SetString(string key, string value)
        {
            EditorPrefs.SetString(KeyPrefix + key, value);
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
            return EditorPrefs.GetInt(KeyPrefix + key, defaultValue);
        }

        public static void SetInt(string key, int value)
        {
            EditorPrefs.SetInt(KeyPrefix + key, value);
        }

        public static string GetSettingsSignature()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("CacheVersion=2;");

            // Int keys
            string[] intKeys = new[]
            {
                "SmartVRAM_MaxResPC", "SmartVRAM_MinResPC",
                "SmartVRAM_MaxResAndroid", "SmartVRAM_MinResAndroid",
                "MeshSimplifier_PC_MinTriangleCount", "MeshSimplifier_Mobile_MinTriangleCount",
                "MeshSimplifier_Mobile_SceneTriangleCap"
            };
            foreach (var k in intKeys) sb.Append(k).Append('=').Append(GetInt(k)).Append(';');

            // Float keys
            string[] floatKeys = new[]
            {
                "SmartVRAM_HighComplexityThreshold", "SmartVRAM_LowComplexityThreshold",
                "MeshSimplifier_PC_TargetRatio", "MeshSimplifier_Mobile_TargetRatio",
                "MeshSimplifier_TierRatio_50k", "MeshSimplifier_TierRatio_20k",
                "MeshSimplifier_TierRatio_8k", "MeshSimplifier_TierRatio_4k",
                "MeshSimplifier_TierRatio_Default", "MeshSimplifier_PreserveRatio"
            };
            foreach (var k in floatKeys) sb.Append(k).Append('=').Append(GetFloat(k)).Append(';');

            // Bool keys
            string[] boolKeys = new[]
            {
                "GlobalPassesEnabled",
                "Pass_SynEditorOnlyPrunerPass_Enabled", "EditorOnlyPruner_NukeTagged", "EditorOnlyPruner_StripEmptyRenderers",
                "Pass_SynColorPalettePass_Enabled", "PaletteAtlas_OptimizeStatic", "PaletteAtlas_OptimizeNonStatic", "PaletteAtlas_IgnoreInstanced",
                "Pass_synthos.mesh_simplifier_Enabled", "MeshSimplifier_PC_Enabled", "MeshSimplifier_Mobile_Enabled", "MeshSimplifier_Mobile_UseDynamicTiers", "MeshSimplifier_Mobile_UseSceneCap", "MeshSimplifier_UsePreserveRatio",
                "Pass_SynSmartTextureVRAMPass_Enabled", "SmartVRAM_ProtectBakery", "SmartVRAM_ProtectMochie", "SmartVRAM_PruneUnusedSlots",
                "Pass_SynDisableStochasticPass_Enabled",
                "Pass_SynEnableMipStreamingPass_Enabled", "MipStreaming_IncludeLightmaps", "MipStreaming_IncludeParticles", "MipStreaming_IncludeUI", "MipStreaming_IncludeTerrain", "MipStreaming_IncludeSkybox", "MipStreaming_IncludePackages", "MipStreaming_EnableKaiser",
                "Pass_SynAudioOptimizerPass_Enabled"
            };
            foreach (var k in boolKeys) sb.Append(k).Append('=').Append(GetBool(k)).Append(';');

            // String keys
            string[] stringKeys = new[]
            {
                "MeshSimplifier_TargetPlatform", "PaletteAtlas_IgnoreMaterials", "SmartVRAM_ProtectedPatterns"
            };
            foreach (var k in stringKeys) sb.Append(k).Append('=').Append(GetString(k)).Append(';');

            return sb.ToString();
        }
    }
}
