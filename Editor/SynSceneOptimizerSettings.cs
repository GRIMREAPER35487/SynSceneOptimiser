using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Per-project optimizer settings, stored in ProjectSettings/SynSceneOptimizer.json so they are committed with the
    /// project and can differ per world. Versions before 1.1 kept them in machine-wide EditorPrefs; the first time a
    /// project loads without the file, every value the user had actually set there is copied in.
    /// </summary>
    public class SynSceneOptimizerSettings
    {
        private const string SettingsPath = "ProjectSettings/SynSceneOptimizer.json";
        private const string LegacyEditorPrefsPrefix = "Synthos.SynSceneOptimizer.";

        [Serializable]
        private class Entry
        {
            public string key;
            public string value;
        }

        [Serializable]
        private class Store
        {
            public int version = 1;
            public List<Entry> entries = new List<Entry>();
        }

        private static Dictionary<string, string> values;

        private static Dictionary<string, string> Values
        {
            get
            {
                if (values == null) Load();
                return values;
            }
        }

        public static bool GetBool(string key, bool defaultValue = false)
        {
            return Values.TryGetValue(key, out string v) && bool.TryParse(v, out bool b) ? b : defaultValue;
        }

        public static void SetBool(string key, bool value)
        {
            Set(key, value ? "true" : "false");
        }

        public static float GetFloat(string key, float defaultValue = 0f)
        {
            return Values.TryGetValue(key, out string v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : defaultValue;
        }

        public static void SetFloat(string key, float value)
        {
            Set(key, value.ToString("R", CultureInfo.InvariantCulture));
        }

        public static string GetString(string key, string defaultValue = "")
        {
            return Values.TryGetValue(key, out string v) ? v : defaultValue;
        }

        public static void SetString(string key, string value)
        {
            Set(key, value ?? "");
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
            return Values.TryGetValue(key, out string v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : defaultValue;
        }

        public static void SetInt(string key, int value)
        {
            Set(key, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Removes a setting so its built-in default applies again.
        /// </summary>
        public static void DeleteKey(string key)
        {
            if (Values.Remove(key)) Save();
        }

        /// <summary>
        /// Clears every setting in this project back to the built-in defaults.
        /// </summary>
        public static void ResetAll()
        {
            Values.Clear();
            Save();
        }

        private static void Set(string key, string value)
        {
            if (Values.TryGetValue(key, out string existing) && existing == value) return;
            values[key] = value;
            Save();
        }

        private static void Load()
        {
            values = new Dictionary<string, string>();

            if (File.Exists(SettingsPath))
            {
                try
                {
                    var store = JsonUtility.FromJson<Store>(File.ReadAllText(SettingsPath));
                    if (store?.entries != null)
                    {
                        foreach (var entry in store.entries)
                        {
                            if (!string.IsNullOrEmpty(entry.key)) values[entry.key] = entry.value ?? "";
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not read {SettingsPath}, using defaults: {e.Message}");
                }
                return;
            }

            int migrated = MigrateFromEditorPrefs();
            Save();
            if (migrated > 0)
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Copied {migrated} optimizer settings from this machine's editor preferences into {SettingsPath}. Settings are now saved per project.");
            }
        }

        private static void Save()
        {
            var store = new Store();
            var keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                store.entries.Add(new Entry { key = key, value = values[key] });
            }

            try
            {
                File.WriteAllText(SettingsPath, JsonUtility.ToJson(store, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not save {SettingsPath}: {e.Message}");
            }
        }

        /// <summary>
        /// Copies every setting the user had set in the old machine-wide EditorPrefs. Keys that were never set are
        /// skipped so their defaults keep applying.
        /// </summary>
        private static int MigrateFromEditorPrefs()
        {
            int count = 0;
            void Copy(string key, Func<string, string> read)
            {
                string legacyKey = LegacyEditorPrefsPrefix + key;
                if (!EditorPrefs.HasKey(legacyKey)) return;
                values[key] = read(legacyKey);
                count++;
            }

            foreach (string key in LegacyBoolKeys) Copy(key, k => EditorPrefs.GetBool(k) ? "true" : "false");
            foreach (string key in LegacyIntKeys) Copy(key, k => EditorPrefs.GetInt(k).ToString(CultureInfo.InvariantCulture));
            foreach (string key in LegacyFloatKeys) Copy(key, k => EditorPrefs.GetFloat(k).ToString("R", CultureInfo.InvariantCulture));
            foreach (string key in LegacyStringKeys) Copy(key, k => EditorPrefs.GetString(k));

            // Pass on/off toggles are named after each pass Id
            foreach (Type type in typeof(SynOptimizationPass).Assembly.GetTypes())
            {
                if (!type.IsSubclassOf(typeof(SynOptimizationPass)) || type.IsAbstract) continue;
                try
                {
                    var pass = (SynOptimizationPass)Activator.CreateInstance(type);
                    Copy(pass.ToggleKey, k => EditorPrefs.GetBool(k) ? "true" : "false");
                }
                catch
                {
                    // A pass that can't be constructed has no toggle to migrate
                }
            }
            return count;
        }

        private static readonly string[] LegacyBoolKeys =
        {
            "Audio_LogOptimized",
            "AutoOptimizeBuild",
            "AutoOptimizePlayMode",
            "BakeryRefl_SyncProjectSettings",
            "EditorOnlyPruner_NukeTagged",
            "EditorOnlyPruner_StripEmptyRenderers",
            "EditorOnlyPruner_VerboseLog",
            "EnableConsoleLogging",
            "EnableVerboseLogging",
            "Fallback_IncludeStandard",
            "Fallback_IncludeStandardLite",
            "ForceGPUInstancingOnBaked",
            "GPUInstancing_DisableStaticBatching",
            "GPUInstancing_IgnoreSkinned",
            "GPUInstancing_IncludeBatchedStatics",
            "GPUInstancing_IncludeDynamic",
            "GPUInstancing_IncludeUnbatchedStatics",
            "GlobalPassesEnabled",
            "MeshDeduplicator_DeduplicateMeshes",
            "MeshDeduplicator_DisableBatchingOnMeshes",
            "MeshDeduplicator_LogConsolidation",
            "MeshDeduplicator_QueueInstancing",
            "MeshOpt_CompactUV3",
            "MeshOpt_PurgeIntermediateClones",
            "MeshOpt_StripUnusedChannels",
            "MeshSimplifier_AdvancedOpen",
            "MeshSimplifier_Mobile_Enabled",
            "MeshSimplifier_Mobile_UseDynamicTiers",
            "MeshSimplifier_Mobile_UseSceneCap",
            "MeshSimplifier_PC_Enabled",
            "MeshSimplifier_SkipLightmapped",
            "MeshSimplifier_UsePreserveRatio",
            "MipStreaming_EnableKaiser",
            "MipStreaming_IncludeLightmaps",
            "MipStreaming_IncludePackages",
            "MipStreaming_IncludeTerrain",
            "Mirrors_LogOptimized",
            "Mirrors_StripStereo",
            "Mirrors_StripUI",
            "Mirrors_StripWater",
            "PaletteAtlas_DebugCombinedColors",
            "PaletteAtlas_ExtremeMode",
            "PaletteAtlas_GroupByChannel",
            "PaletteAtlas_IgnoreInstanced",
            "PaletteAtlas_LogOptimized",
            "PaletteAtlas_OptimizeNonStatic",
            "PaletteAtlas_OptimizeStatic",
            "Particles_ClampCapacity",
            "Particles_ForceCulling",
            "Particles_LogOptimized",
            "SlotTrimmer_LogTrimmed",
            "SmartVRAM_ProtectBakery",
            "SmartVRAM_ProtectMochie",
            "SmartVRAM_PruneUnusedSlots",
            "StopBuildOnPassError"
        };

        private static readonly string[] LegacyIntKeys =
        {
            "CacheRetentionDays",
            "GPUInstancing_MinInstanceCount",
            "MeshDeduplicator_MinInstances",
            "MeshSimplifier_Mobile_MinTriangleCount",
            "MeshSimplifier_Mobile_SceneTriangleCap",
            "MeshSimplifier_PC_MinTriangleCount",
            "Particles_AbsoluteMaxCap",
            "Particles_CullingMode",
            "SmartVRAM_MaxResAndroid",
            "SmartVRAM_MaxResPC",
            "SmartVRAM_MinResAndroid",
            "SmartVRAM_MinResPC"
        };

        private static readonly string[] LegacyFloatKeys =
        {
            "Audio_DecompressBelowSeconds",
            "Audio_StreamAboveSeconds",
            "Audio_VorbisQuality",
            "MeshSimplifier_Mobile_TargetRatio",
            "MeshSimplifier_PC_TargetRatio",
            "MeshSimplifier_PreserveRatio",
            "MeshSimplifier_TierRatio_20k",
            "MeshSimplifier_TierRatio_4k",
            "MeshSimplifier_TierRatio_50k",
            "MeshSimplifier_TierRatio_8k",
            "MeshSimplifier_TierRatio_Default",
            "Particles_SafetyMultiplier",
            "SmartVRAM_HighComplexityThreshold",
            "SmartVRAM_LowComplexityThreshold",
            "SmoothnessThreshold"
        };

        private static readonly string[] LegacyStringKeys =
        {
            "CustomCachePath",
            "GPUInstancing_IgnoreMaterials",
            "MeshSimplifier_TargetPlatform",
            "Mirrors_CustomStrip",
            "PaletteAtlas_IgnoreMaterials",
            "Particles_Exclusions",
            "SmartVRAM_ProtectedPatterns"
        };

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
