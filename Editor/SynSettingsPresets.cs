using System.Collections.Generic;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// One-click starting points for the main quality/size trade-offs. A preset only touches the keys listed in
    /// <see cref="ControlledKeys"/>: they are first reset to their built-in defaults, then the preset's values are
    /// applied, so switching presets never leaves values from a previous one behind. Pass toggles and everything
    /// else stay as the user set them.
    /// </summary>
    public static class SynSettingsPresets
    {
        public class Preset
        {
            public string Name;
            public string Description;
            public Dictionary<string, object> Values;
        }

        private static readonly string[] ControlledKeys =
        {
            "SmartVRAM_MaxResPC", "SmartVRAM_MinResPC", "SmartVRAM_MaxResAndroid", "SmartVRAM_MinResAndroid",
            "SmartVRAM_LowComplexityThreshold", "SmartVRAM_HighComplexityThreshold",
            "MeshSimplifier_PC_Enabled", "MeshSimplifier_PC_MinTriangleCount", "MeshSimplifier_PC_TargetRatio",
            "MeshSimplifier_Mobile_Enabled", "MeshSimplifier_Mobile_MinTriangleCount", "MeshSimplifier_Mobile_TargetRatio",
            "MeshSimplifier_Mobile_UseDynamicTiers", "MeshSimplifier_Mobile_UseSceneCap", "MeshSimplifier_Mobile_SceneTriangleCap",
            "Audio_VorbisQuality", "Audio_StreamAboveSeconds", "Audio_DecompressBelowSeconds"
        };

        public static readonly Preset[] All =
        {
            new Preset
            {
                Name = "Balanced (Default)",
                Description = "The built-in defaults: PC textures up to 2048, Quest up to 1024, meshes simplified only above 25k (PC) / 4k (Quest) triangles.",
                Values = new Dictionary<string, object>()
            },
            new Preset
            {
                Name = "Quality",
                Description = "Keeps more detail: PC textures up to 4096 (never below 1024), Quest up to 2048, no mesh simplification on PC, gentler simplification on Quest, higher audio quality.",
                Values = new Dictionary<string, object>
                {
                    { "SmartVRAM_MaxResPC", 4096 },
                    { "SmartVRAM_MinResPC", 1024 },
                    { "SmartVRAM_MaxResAndroid", 2048 },
                    { "SmartVRAM_MinResAndroid", 512 },
                    { "SmartVRAM_LowComplexityThreshold", 0.10f },
                    { "MeshSimplifier_PC_Enabled", false },
                    { "MeshSimplifier_Mobile_MinTriangleCount", 8000 },
                    { "MeshSimplifier_Mobile_TargetRatio", 0.7f },
                    { "Audio_VorbisQuality", 0.85f }
                }
            },
            new Preset
            {
                Name = "Quest Aggressive",
                Description = "Smallest Quest builds: Quest textures down to 128 for flat surfaces, Quest scene capped at 150k rendered triangles with auto-tuned tiers, lower audio quality and earlier streaming. PC is left at defaults.",
                Values = new Dictionary<string, object>
                {
                    { "SmartVRAM_MaxResAndroid", 1024 },
                    { "SmartVRAM_MinResAndroid", 128 },
                    { "SmartVRAM_LowComplexityThreshold", 0.30f },
                    { "MeshSimplifier_Mobile_Enabled", true },
                    { "MeshSimplifier_Mobile_MinTriangleCount", 2000 },
                    { "MeshSimplifier_Mobile_UseDynamicTiers", true },
                    { "MeshSimplifier_Mobile_UseSceneCap", true },
                    { "MeshSimplifier_Mobile_SceneTriangleCap", 150000 },
                    { "Audio_VorbisQuality", 0.5f },
                    { "Audio_StreamAboveSeconds", 15f }
                }
            }
        };

        public static void Apply(Preset preset)
        {
            foreach (string key in ControlledKeys)
            {
                SynSceneOptimizerSettings.DeleteKey(key);
            }

            foreach (var kvp in preset.Values)
            {
                switch (kvp.Value)
                {
                    case bool b: SynSceneOptimizerSettings.SetBool(kvp.Key, b); break;
                    case int i: SynSceneOptimizerSettings.SetInt(kvp.Key, i); break;
                    case float f: SynSceneOptimizerSettings.SetFloat(kvp.Key, f); break;
                    case string s: SynSceneOptimizerSettings.SetString(kvp.Key, s); break;
                }
            }
        }
    }
}
