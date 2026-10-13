using System;
using System.Collections.Generic;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Settings of the Texture Complexity Compressor, resolved for the current build target. The presets and settings
    /// are the Avatar Compressor's, so the same name means the same decisions in both tools.
    /// </summary>
    internal sealed class SynTextureCompressorSettings
    {
        public const string PresetKey = "SmartVRAM_Preset";
        public static readonly string[] PresetNames = { "HighQuality", "Quality", "Balanced", "Aggressive", "Maximum", "Custom" };
        public static readonly string[] PresetLabels = { "High Quality", "Quality", "Balanced", "Aggressive", "Maximum", "Custom" };

        // Keys an existing project may have set before presets existed; any of them means Custom
        private static readonly string[] LegacyCustomKeys =
        {
            "SmartVRAM_MaxResPC", "SmartVRAM_MinResPC", "SmartVRAM_MaxResAndroid", "SmartVRAM_MinResAndroid",
            "SmartVRAM_HighComplexityThreshold", "SmartVRAM_LowComplexityThreshold"
        };

        public string Preset;
        public AnalysisStrategyType Strategy;
        public float FastWeight, HighAccuracyWeight, PerceptualWeight;
        public float HighComplexityThreshold, LowComplexityThreshold;
        public int MinDivisor, MaxDivisor;
        public int MaxResolution, MinResolution;
        public bool ForcePowerOfTwo;
        public int MinSourceSize, SkipIfSmallerThan;
        public bool UseHighQualityFormatForHighComplexity;

        // Not part of the presets (same as the Avatar Compressor)
        public bool ProcessMainTextures, ProcessNormalMaps, ProcessEmissionMaps, ProcessOtherTextures;
        public bool SkipUnknownUncompressedTextures;
        public bool RespectImportMaxSize;
        public bool SizeFiltersUseSourceSize;
        public bool RespectImportFormat;
        public bool AvoidCompressionArtifacts;
        public bool ReformatFilteredTextures;
        public bool LimitByWorldSize;
        public bool KeepVisibleDetail;
        public float ViewDistanceCm;

        // The Avatar Compressor's preferences
        public bool UseSourceFiles;
        public AnalysisBackendPreference AnalysisBackend;
        public ResizeBackendPreference ResizeBackend;

        /// <summary>The preset in use: the saved one, else Custom for projects with older size settings, else Balanced.</summary>
        public static string GetPreset()
        {
            string saved = SynSceneOptimizerSettings.GetString(PresetKey, "");
            if (Array.IndexOf(PresetNames, saved) >= 0) return saved;
            foreach (string key in LegacyCustomKeys)
            {
                if (SynSceneOptimizerSettings.HasKey(key)) return "Custom";
            }
            return "Balanced";
        }

        public static SynTextureCompressorSettings Load(bool mobile)
        {
            var s = new SynTextureCompressorSettings { Preset = GetPreset(), Strategy = AnalysisStrategyType.Combined, ForcePowerOfTwo = true };
            switch (s.Preset)
            {
                case "HighQuality": s.Set(0.1f, 0.5f, 0.4f, 0.3f, 0.1f, 1, 2, 2048, 256, 1024, 512, true); break;
                case "Quality": s.Set(0.2f, 0.5f, 0.3f, 0.5f, 0.15f, 1, 4, 2048, 128, 512, 256, true); break;
                case "Aggressive": s.Set(0.4f, 0.4f, 0.2f, 0.70f, 0.20f, 2, 8, 2048, 32, 128, 64, false); break;
                case "Maximum": s.Set(0.5f, 0.3f, 0.2f, 0.70f, 0.15f, 4, 16, 2048, 32, 64, 32, false); break;
                case "Custom":
                    s.Set(
                        SynSceneOptimizerSettings.GetFloat("SmartVRAM_FastWeight", 0.3f),
                        SynSceneOptimizerSettings.GetFloat("SmartVRAM_HighAccuracyWeight", 0.5f),
                        SynSceneOptimizerSettings.GetFloat("SmartVRAM_PerceptualWeight", 0.2f),
                        SynSceneOptimizerSettings.GetFloat("SmartVRAM_HighComplexityThreshold", 0.70f),
                        SynSceneOptimizerSettings.GetFloat("SmartVRAM_LowComplexityThreshold", 0.20f),
                        SynSceneOptimizerSettings.GetInt("SmartVRAM_MinDivisor", 1),
                        SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxDivisor", 8),
                        mobile ? SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxResAndroid", 1024) : SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxResPC", 2048),
                        mobile ? SynSceneOptimizerSettings.GetInt("SmartVRAM_MinResAndroid", 256) : SynSceneOptimizerSettings.GetInt("SmartVRAM_MinResPC", 512),
                        SynSceneOptimizerSettings.GetInt("SmartVRAM_MinSourceSize", 256),
                        SynSceneOptimizerSettings.GetInt("SmartVRAM_SkipIfSmallerThan", 128),
                        SynSceneOptimizerSettings.GetBool("SmartVRAM_UseHQFormat", true));
                    s.Strategy = (AnalysisStrategyType)SynSceneOptimizerSettings.GetInt("SmartVRAM_Strategy", (int)AnalysisStrategyType.Combined);
                    s.ForcePowerOfTwo = SynSceneOptimizerSettings.GetBool("SmartVRAM_ForcePowerOfTwo", true);
                    break;
                default: s.Set(0.3f, 0.5f, 0.2f, 0.7f, 0.2f, 1, 8, 2048, 64, 256, 128, true); break;
            }

            s.ProcessMainTextures = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProcessMain", true);
            s.ProcessNormalMaps = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProcessNormal", true);
            s.ProcessEmissionMaps = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProcessEmission", true);
            s.ProcessOtherTextures = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProcessOther", true);
            s.SkipUnknownUncompressedTextures = SynSceneOptimizerSettings.GetBool("SmartVRAM_SkipUnknownUncompressed", true);
            s.RespectImportMaxSize = SynSceneOptimizerSettings.GetBool("SmartVRAM_RespectImportMaxSize", true);
            s.SizeFiltersUseSourceSize = SynSceneOptimizerSettings.GetBool("SmartVRAM_SizeFiltersUseSourceSize", true);
            s.RespectImportFormat = SynSceneOptimizerSettings.GetBool("SmartVRAM_RespectImportFormat", true);
            s.AvoidCompressionArtifacts = SynSceneOptimizerSettings.GetBool("SmartVRAM_AvoidCompressionArtifacts", true);
            s.ReformatFilteredTextures = SynSceneOptimizerSettings.GetBool("SmartVRAM_ReformatFilteredTextures", false);
            s.LimitByWorldSize = SynSceneOptimizerSettings.GetBool("SmartVRAM_LimitByWorldSize", true);
            s.KeepVisibleDetail = SynSceneOptimizerSettings.GetBool("SmartVRAM_KeepVisibleDetail", true);
            s.ViewDistanceCm = mobile
                ? SynSceneOptimizerSettings.GetFloat("SmartVRAM_ViewDistanceAndroid", DefaultViewDistanceAndroid)
                : SynSceneOptimizerSettings.GetFloat("SmartVRAM_ViewDistancePC", DefaultViewDistancePC);
            s.UseSourceFiles = SynSceneOptimizerSettings.GetBool("SmartVRAM_UseSourceFiles", true);
            s.AnalysisBackend = (AnalysisBackendPreference)SynSceneOptimizerSettings.GetInt("SmartVRAM_AnalysisBackend", (int)AnalysisBackendPreference.Auto);
            s.ResizeBackend = (ResizeBackendPreference)SynSceneOptimizerSettings.GetInt("SmartVRAM_ResizeBackend", (int)ResizeBackendPreference.Auto);
            return s;
        }

        public const float DefaultViewDistancePC = 100f;
        public const float DefaultViewDistanceAndroid = 150f;

        private void Set(float fast, float accuracy, float perceptual, float high, float low, int minDivisor, int maxDivisor, int maxRes, int minRes, int minSource, int skipBelow, bool highQualityFormat)
        {
            FastWeight = fast;
            HighAccuracyWeight = accuracy;
            PerceptualWeight = perceptual;
            HighComplexityThreshold = high;
            LowComplexityThreshold = low;
            MinDivisor = Mathf.Max(1, minDivisor);
            MaxDivisor = Mathf.Max(MinDivisor, maxDivisor);
            MaxResolution = maxRes;
            MinResolution = Mathf.Min(minRes, maxRes);
            MinSourceSize = minSource;
            SkipIfSmallerThan = skipBelow;
            UseHighQualityFormatForHighComplexity = highQualityFormat;
        }

        /// <summary>True if textures are resized from their source image file.</summary>
        public bool ScalesFromSource => !RespectImportMaxSize && UseSourceFiles;

        /// <summary>Text that changes whenever a setting that affects the analysis scores changes (score cache key).</summary>
        public string AnalysisSignature()
        {
            return $"{Strategy}_{FastWeight:F2}_{HighAccuracyWeight:F2}_{PerceptualWeight:F2}";
        }

        /// <summary>Text that changes whenever a setting that affects the produced textures changes (cache key).</summary>
        public string Signature()
        {
            return string.Join("_", new[]
            {
                Preset, AnalysisSignature(),
                HighComplexityThreshold.ToString("F2"), LowComplexityThreshold.ToString("F2"), MinDivisor.ToString(), MaxDivisor.ToString(),
                MaxResolution.ToString(), MinResolution.ToString(), ForcePowerOfTwo ? "p2" : "np2", UseHighQualityFormatForHighComplexity ? "hq" : "lq",
                RespectImportMaxSize ? "rms" : "src", UseSourceFiles ? "sf" : "imp", RespectImportFormat ? "rfmt" : "fmt",
                AvoidCompressionArtifacts ? "art" : "noart", ResizeBackend.ToString()
            });
        }
    }

    /// <summary>
    /// A texture with manually chosen compression (the Avatar Compressor's frozen textures): a fixed divisor and
    /// format, or skipped entirely. Stored by asset GUID so it survives renames and moves.
    /// </summary>
    [Serializable]
    public class SynFrozenTexture
    {
        public string textureGuid;
        public int divisor = 1;
        [SerializeField] internal FrozenTextureFormat format = FrozenTextureFormat.Auto;
        public bool skip;
    }

    /// <summary>Lookup of the frozen textures stored in the protection settings.</summary>
    internal static class SynFrozenTextures
    {
        public static SynFrozenTexture Get(Texture2D texture)
        {
            string guid = GetGuid(texture);
            return guid != null ? Get(guid) : null;
        }

        public static SynFrozenTexture Get(string guid)
        {
            var data = SynProtectionData.GetInstance();
            if (data == null || data.frozenTextures == null || string.IsNullOrEmpty(guid)) return null;
            foreach (var frozen in data.frozenTextures)
            {
                if (frozen != null && frozen.textureGuid == guid) return frozen;
            }
            return null;
        }

        public static Dictionary<string, SynFrozenTexture> BuildLookup()
        {
            var lookup = new Dictionary<string, SynFrozenTexture>();
            var data = SynProtectionData.GetInstance();
            if (data == null || data.frozenTextures == null) return lookup;
            foreach (var frozen in data.frozenTextures)
            {
                if (frozen != null && !string.IsNullOrEmpty(frozen.textureGuid)) lookup[frozen.textureGuid] = frozen;
            }
            return lookup;
        }

        public static void Freeze(Texture2D texture, int divisor)
        {
            string guid = GetGuid(texture);
            var data = SynProtectionData.GetInstance();
            if (guid == null || data == null) return;
            Undo.RecordObject(data, "Freeze Texture");
            var existing = Get(guid);
            if (existing != null) existing.divisor = divisor;
            else data.frozenTextures.Add(new SynFrozenTexture { textureGuid = guid, divisor = divisor });
            EditorUtility.SetDirty(data);
        }

        public static void Unfreeze(string guid)
        {
            var data = SynProtectionData.GetInstance();
            if (data == null) return;
            Undo.RecordObject(data, "Unfreeze Texture");
            data.frozenTextures.RemoveAll(f => f == null || f.textureGuid == guid);
            EditorUtility.SetDirty(data);
        }

        public static string GetGuid(Texture2D texture)
        {
            string path = texture != null ? AssetDatabase.GetAssetPath(texture) : null;
            if (string.IsNullOrEmpty(path)) return null;
            string guid = AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }

        public static string Signature()
        {
            var parts = new List<string>();
            foreach (var kvp in BuildLookup()) parts.Add($"{kvp.Key}:{kvp.Value.divisor}:{kvp.Value.format}:{kvp.Value.skip}");
            parts.Sort(StringComparer.Ordinal);
            return string.Join(",", parts);
        }
    }

    /// <summary>
    /// Which kind of texture a material slot holds (main, normal, emission, other), and whether the slot is a known
    /// visual one. World shaders (Unity Standard, Mochie, lilToon, Poiyomi) plus the texture's import type.
    /// </summary>
    internal static class SynTextureSlotKinds
    {
        public enum Kind { Main, Normal, Emission, Other }

        private static readonly HashSet<string> MainSlots = new HashSet<string>(StringComparer.Ordinal)
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_DiffuseMap", "_ColorMap"
        };

        // Known visual slots besides main/normal/emission; uncompressed textures elsewhere may be data (LUTs, masks)
        private static readonly HashSet<string> KnownOtherSlots = new HashSet<string>(StringComparer.Ordinal)
        {
            // Unity Standard / URP / HDRP
            "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap", "_ParallaxMap", "_DetailMask", "_DetailAlbedoMap",
            "_MaskMap", "_DetailMap", "_HeightMap", "_ThicknessMap", "_ClearCoatMap", "_SpecularColorMap",
            // Mochie Standard
            "_MetallicMap", "_RoughnessMap", "_PackedMap", "_DetailMainTex", "_DetailMetallicMap", "_DetailRoughnessMap",
            "_DetailOcclusionMap", "_HeightMask", "_EmissionMask",
            // lilToon / Poiyomi common
            "_Main2ndTex", "_Main3rdTex", "_AlphaMask", "_ShadowColorTex", "_RimColorTex", "_MatCapTex",
            "_MainColorAdjustMask", "_ShadowStrengthMask", "_BacklightColorTex"
        };

        public static readonly string[] KindLabels = { "Main", "Normal", "Emission", "Other" };

        public static Kind GetKind(string property, Texture texture)
        {
            string lower = property.ToLowerInvariant();
            if (IsImportedAsNormalMap(texture) || lower.Contains("bump") || lower.Contains("normal")) return Kind.Normal;
            if (lower.Contains("emission") || lower.Contains("emissive")) return lower.Contains("mask") ? Kind.Other : Kind.Emission;
            if (MainSlots.Contains(property)) return Kind.Main;
            return Kind.Other;
        }

        public static bool IsKnownSlot(string property, Texture texture)
        {
            return GetKind(property, texture) != Kind.Other || KnownOtherSlots.Contains(property);
        }

        private static bool IsImportedAsNormalMap(Texture texture)
        {
            string path = texture != null ? AssetDatabase.GetAssetPath(texture) : null;
            return !string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType == TextureImporterType.NormalMap;
        }
    }
}
