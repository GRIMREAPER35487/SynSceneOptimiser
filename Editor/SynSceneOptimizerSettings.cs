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

        /// <summary>
        /// Signature of settings that change the CONTENT of cached assets without being part of their cache keys.
        /// A change purges the platform cache, so only list such settings here:
        /// - Pass on/off toggles are excluded: disabling a pass never makes another pass's cached output stale.
        /// - Texture sizes, mesh ratios, mesh-memory flags, and material bake parameters (shader, instancing)
        ///   are already hashed into each asset's key and are excluded so tuning them doesn't wipe the cache.
        /// Bump CacheVersion whenever a pass changes what it writes to the cache.
        /// </summary>
        public static string GetSettingsSignature()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("CacheVersion=4;");

            // Color palette grouping/baking options shape palette textures and materials beyond their hashed inputs
            string[] boolKeys = new[]
            {
                "PaletteAtlas_OptimizeStatic", "PaletteAtlas_OptimizeNonStatic", "PaletteAtlas_IgnoreInstanced",
                "PaletteAtlas_ExtremeMode", "PaletteAtlas_GroupByChannel",
                // Mip streaming writes m_StreamingMipmaps directly into cached .asset textures
                "MipStreaming_IncludeLightmaps",
                "MipStreaming_IncludeTerrain", "MipStreaming_IncludePackages"
            };
            foreach (var k in boolKeys) sb.Append(k).Append('=').Append(GetBool(k)).Append(';');

            string[] stringKeys = new[]
            {
                "PaletteAtlas_IgnoreMaterials"
            };
            foreach (var k in stringKeys) sb.Append(k).Append('=').Append(GetString(k)).Append(';');

            return sb.ToString();
        }
    }
}
