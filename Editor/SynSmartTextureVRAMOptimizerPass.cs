using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor;
using Synthos.TextureAnalysis;

namespace Synthos.SynSceneOptimizer
{
    public class SynSmartTextureVRAMOptimizerPass : SynOptimizationPass
    {
        public override string Id => "SynSmartTextureVRAMPass";
        public override string Name => "Texture Complexity Compressor (Derived from Avatar Compressor)";
        public override string Description => "Intelligently analyzes scene texture frequency, complexity, and normal layouts to dynamically downscale flat/simple textures, prune unused material slots, and select optimal per-platform compression formats.";
        public override string Category => "Memory & Assets";
        public override int Priority => 45; // Runs before Color Palettes (50) so it never downscales generated palette textures
        public override SynProtectionTargets ProtectionTargets => SynProtectionTargets.Objects | SynProtectionTargets.Materials | SynProtectionTargets.Textures;
        public override string ProtectionEffect => "Swaps the material for a copy using downscaled textures.";

        private static readonly string[] DivisorLabels = { "1x", "2x", "4x", "8x", "16x" };
        private static readonly int[] Divisors = { 1, 2, 4, 8, 16 };

        private static void DrawToggle(string key, bool defaultValue, string label, string tooltip)
        {
            bool value = SynSceneOptimizerSettings.GetBool(key, defaultValue);
            bool newValue = EditorGUILayout.Toggle(new GUIContent(label, tooltip), value);
            if (newValue != value) SynSceneOptimizerSettings.SetBool(key, newValue);
        }

        private static void DrawSlider(string key, float defaultValue, string label, string tooltip, float min, float max)
        {
            float value = SynSceneOptimizerSettings.GetFloat(key, defaultValue);
            float newValue = EditorGUILayout.Slider(new GUIContent(label, tooltip), value, min, max);
            if (Mathf.Abs(newValue - value) > 0.001f) SynSceneOptimizerSettings.SetFloat(key, newValue);
        }

        private static void DrawIntField(string key, int defaultValue, string label, string tooltip)
        {
            int value = SynSceneOptimizerSettings.GetInt(key, defaultValue);
            int newValue = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent(label, tooltip), value));
            if (newValue != value) SynSceneOptimizerSettings.SetInt(key, newValue);
        }

        private static void DrawIntPopup(string key, int defaultValue, string label, string tooltip, int[] values)
        {
            int value = SynSceneOptimizerSettings.GetInt(key, defaultValue);
            string[] labels = Array.ConvertAll(values, v => v.ToString());
            int newValue = EditorGUILayout.IntPopup(new GUIContent(label, tooltip), value, Array.ConvertAll(labels, l => new GUIContent(l)), values);
            if (newValue != value) SynSceneOptimizerSettings.SetInt(key, newValue);
        }

        private static void DrawEnumPopup<T>(string key, T defaultValue, string label, string tooltip) where T : Enum
        {
            var value = (T)Enum.ToObject(typeof(T), SynSceneOptimizerSettings.GetInt(key, Convert.ToInt32(defaultValue)));
            var newValue = (T)EditorGUILayout.EnumPopup(new GUIContent(label, tooltip), value);
            if (!newValue.Equals(value)) SynSceneOptimizerSettings.SetInt(key, Convert.ToInt32(newValue));
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Makes the Avatar Compressor's decisions for scene textures: scores how much visible detail each texture has, sizes it by that score (and by how large it appears in the world), picks the compression format from its detail and alpha, and compresses from the original image file. Results are cached per platform.", MessageType.Info);

            DrawToggle("SmartVRAM_ProtectBakery", true, "Protect Bakery & Lightmaps", "Guarantees that all Bakery lightmaps, directional maps, volumes, and shadowmasks are never downscaled.");
            DrawToggle("SmartVRAM_ProtectMochie", true, "Protect Mochie Textures", "Guarantees that all default Mochie shader lookup textures, normal arrays, and rain sheets inside Assets/Mochie/ are never touched.");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("PRESET", EditorStyles.boldLabel);
            string preset = SynTextureCompressorSettings.GetPreset();
            int presetIndex = Mathf.Max(0, Array.IndexOf(SynTextureCompressorSettings.PresetNames, preset));
            int newPresetIndex = EditorGUILayout.Popup(new GUIContent("Preset", "The Avatar Compressor's presets: the same name makes the same decisions in both tools. Custom uses the values below."), presetIndex, SynTextureCompressorSettings.PresetLabels);
            if (newPresetIndex != presetIndex) SynSceneOptimizerSettings.SetString(SynTextureCompressorSettings.PresetKey, SynTextureCompressorSettings.PresetNames[newPresetIndex]);

            bool mobileTarget = SynAssetCache.GetCurrentTargetPlatform() != SynTargetPlatform.PC;
            var resolved = SynTextureCompressorSettings.Load(mobileTarget);
            if (resolved.Preset != "Custom")
            {
                EditorGUILayout.HelpBox($"{SynTextureCompressorSettings.PresetLabels[newPresetIndex]}: {resolved.MinResolution}-{resolved.MaxResolution} px, divisor {resolved.MinDivisor}x-{resolved.MaxDivisor}x, full size above {resolved.HighComplexityThreshold:P0} detail, smallest below {resolved.LowComplexityThreshold:P0}. Only textures over {resolved.MinSourceSize} px are processed and textures of {resolved.SkipIfSmallerThan} px or less are left alone. {(resolved.UseHighQualityFormatForHighComplexity ? "Detailed textures get BC7 (ASTC 4x4 on Quest)." : "No high-quality format for detailed textures.")}", MessageType.None);
            }
            else
            {
                DrawCustomSettings();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("TEXTURE FILTERS", EditorStyles.boldLabel);
            DrawToggle("SmartVRAM_ProcessMain", true, "Main Textures", "Process main textures (_MainTex, _BaseMap, etc.).");
            DrawToggle("SmartVRAM_ProcessNormal", true, "Normal Maps", "Process normal maps.");
            DrawToggle("SmartVRAM_ProcessEmission", true, "Emission Maps", "Process emission textures.");
            DrawToggle("SmartVRAM_ProcessOther", true, "Other Textures", "Process other textures (metallic, roughness, occlusion, etc.).");
            using (new EditorGUI.DisabledScope(!resolved.ProcessOtherTextures))
            {
                EditorGUI.indentLevel++;
                DrawToggle("SmartVRAM_SkipUnknownUncompressed", true, "Skip Unknown Uncompressed", "Skip uncompressed textures assigned to unknown shader properties. Uncompressed textures on unrecognized properties may contain non-visual data (e.g., masks, LUTs) that compression could corrupt.");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("SOURCE TEXTURES", EditorStyles.boldLabel);
            DrawToggle("SmartVRAM_RespectImportMaxSize", true, "Respect Import Max Size", "Use the texture's import Max Size as the starting size. When off, the full-quality source file (fit to Max Resolution) is the starting size, so textures imported smaller can come out larger.");
            using (new EditorGUI.DisabledScope(resolved.RespectImportMaxSize || !resolved.UseSourceFiles))
            {
                EditorGUI.indentLevel++;
                DrawToggle("SmartVRAM_SizeFiltersUseSourceSize", true, "Size Filters Use Source Size", "With Respect Import Max Size off, the size filters (Min Source Size, Skip If Smaller Than) check the source file's size, so a texture imported small from a large file is still processed. When off, they check the imported size, so such textures are skipped and left exactly as imported.");
                EditorGUI.indentLevel--;
            }
            DrawToggle("SmartVRAM_RespectImportFormat", true, "Respect Import Format", "Keep the compression format the texture was imported with for this platform. When off, the format is chosen from each texture's detail and alpha, which can make detailed textures larger and simple ones smaller.");
            DrawToggle("SmartVRAM_AvoidCompressionArtifacts", true, "Avoid Compression Artifacts", "When the compressor picks DXT1/DXT5 (ASTC 6x6/8x8 on Quest), test-compress the texture's smooth and clean areas first. If it would show stair-stepping or blocky color edges, use BC7 (ASTC 4x4) instead.");
            DrawToggle("SmartVRAM_ReformatFilteredTextures", false, "Reformat Filtered Textures", "Textures caught by the size filters keep their imported size but still get their compression format chosen (from detail and alpha, with the banding check), even when Respect Import Format is on. When off, they are left exactly as imported.");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("SIZE IN THE WORLD", EditorStyles.boldLabel);
            DrawToggle("SmartVRAM_LimitByWorldSize", true, "Limit By Size In World", "Shrink textures that are sharper than anyone could see, judged from how large the surfaces they cover are in the world (a 2048 texture on a mug). Only ever makes textures smaller than the detail analysis would, never larger.");
            DrawToggle("SmartVRAM_KeepVisibleDetail", true, "Keep Visible Detail", "Do not shrink a texture below what is visible from the viewing distance on the surfaces it covers (floors, walls). Low-detail textures keep detail in proportion to their score, so only flat ones shrink freely. Min Divisor still applies.");
            float pcDistance = SynSceneOptimizerSettings.GetFloat("SmartVRAM_ViewDistancePC", SynTextureCompressorSettings.DefaultViewDistancePC);
            float newPcDistance = EditorGUILayout.Slider(new GUIContent("Viewing Distance PC (cm)", "How close players get to surfaces. Closer needs sharper textures."), pcDistance, 20f, 500f);
            if (Mathf.Abs(newPcDistance - pcDistance) > 0.5f) SynSceneOptimizerSettings.SetFloat("SmartVRAM_ViewDistancePC", Mathf.Round(newPcDistance));
            float questDistance = SynSceneOptimizerSettings.GetFloat("SmartVRAM_ViewDistanceAndroid", SynTextureCompressorSettings.DefaultViewDistanceAndroid);
            float newQuestDistance = EditorGUILayout.Slider(new GUIContent("Viewing Distance Quest (cm)", "How close players get to surfaces on Quest builds."), questDistance, 20f, 500f);
            if (Mathf.Abs(newQuestDistance - questDistance) > 0.5f) SynSceneOptimizerSettings.SetFloat("SmartVRAM_ViewDistanceAndroid", Mathf.Round(newQuestDistance));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("PREFERENCES", EditorStyles.boldLabel);
            DrawToggle("SmartVRAM_UseSourceFiles", true, "Use Source Files", "Resize from the original image file instead of Unity's already-compressed import. Off: resize the imported texture (faster, lower quality, Respect Import Max Size always applies).");
            DrawEnumPopup("SmartVRAM_AnalysisBackend", AnalysisBackendPreference.Auto, "Analysis Backend", "Where detail scores are computed. Auto uses the GPU when available.");
            DrawEnumPopup("SmartVRAM_ResizeBackend", ResizeBackendPreference.Auto, "Resize Backend", "Where textures are resized. Auto uses the GPU when available.");

            EditorGUILayout.Space(6);
            DrawFrozenTextures();

            EditorGUILayout.Space(6);
            DrawPreview(resolved);

            EditorGUILayout.Space(6);
            DrawProtectedTextures();
        }

        private static void DrawCustomSettings()
        {
            DrawEnumPopup("SmartVRAM_Strategy", AnalysisStrategyType.Combined, "Analysis Strategy", "Complexity analysis method.");
            if ((AnalysisStrategyType)SynSceneOptimizerSettings.GetInt("SmartVRAM_Strategy", (int)AnalysisStrategyType.Combined) == AnalysisStrategyType.Combined)
            {
                EditorGUI.indentLevel++;
                DrawSlider("SmartVRAM_FastWeight", 0.3f, "Fast Weight", "Weight of the fast (gradient) analysis.", 0f, 1f);
                DrawSlider("SmartVRAM_HighAccuracyWeight", 0.5f, "High Accuracy Weight", "Weight of the high-accuracy (DCT/entropy) analysis.", 0f, 1f);
                DrawSlider("SmartVRAM_PerceptualWeight", 0.2f, "Perceptual Weight", "Weight of the perceptual (structure/edges) analysis.", 0f, 1f);
                EditorGUI.indentLevel--;
            }

            DrawSlider("SmartVRAM_HighComplexityThreshold", 0.70f, "High Detail Threshold", "Textures with detail above this use minimal compression (and the high-quality format).", 0f, 1f);
            DrawSlider("SmartVRAM_LowComplexityThreshold", 0.20f, "Low Detail Threshold", "Textures with detail below this use maximum compression.", 0f, 1f);

            int minDiv = SynSceneOptimizerSettings.GetInt("SmartVRAM_MinDivisor", 1);
            int newMinDiv = EditorGUILayout.IntPopup(new GUIContent("Min Divisor", "Minimum resolution divisor (1 = no reduction)."), minDiv, Array.ConvertAll(DivisorLabels, l => new GUIContent(l)), Divisors);
            if (newMinDiv != minDiv) SynSceneOptimizerSettings.SetInt("SmartVRAM_MinDivisor", newMinDiv);
            int maxDiv = SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxDivisor", 8);
            int newMaxDiv = EditorGUILayout.IntPopup(new GUIContent("Max Divisor", "Maximum resolution divisor."), maxDiv, Array.ConvertAll(DivisorLabels, l => new GUIContent(l)), Divisors);
            if (newMaxDiv != maxDiv) SynSceneOptimizerSettings.SetInt("SmartVRAM_MaxDivisor", newMaxDiv);

            DrawIntPopup("SmartVRAM_MaxResPC", 2048, "Max PC Resolution", "Maximum output resolution on PC.", new[] { 256, 512, 1024, 2048, 4096 });
            DrawIntPopup("SmartVRAM_MinResPC", 512, "Min PC Resolution", "Minimum output resolution on PC.", new[] { 32, 64, 128, 256, 512, 1024 });
            DrawIntPopup("SmartVRAM_MaxResAndroid", 1024, "Max Quest Resolution", "Maximum output resolution on Quest.", new[] { 256, 512, 1024, 2048 });
            DrawIntPopup("SmartVRAM_MinResAndroid", 256, "Min Quest Resolution", "Minimum output resolution on Quest.", new[] { 32, 64, 128, 256, 512 });
            DrawToggle("SmartVRAM_ForcePowerOfTwo", true, "Force Power Of Two", "Force output to power of 2 dimensions.");

            DrawIntField("SmartVRAM_MinSourceSize", 256, "Min Source Size", "Only process textures larger than this size.");
            DrawIntField("SmartVRAM_SkipIfSmallerThan", 128, "Skip If Smaller Than", "Skip textures smaller than or equal to this size.");
            DrawToggle("SmartVRAM_UseHQFormat", true, "High Quality Format", "Use BC7/ASTC 4x4 for high detail textures (highest quality).");
        }

        // ---------------------------------------------------------------------------------------------------------
        // Frozen textures (the Avatar Compressor's manual overrides)
        // ---------------------------------------------------------------------------------------------------------

        private static bool s_showFrozen = true;
        private static string s_frozenSearch = "";
        private static Vector2 s_frozenScroll;

        private static void DrawFrozenTextures()
        {
            var data = SynProtectionData.GetInstance();
            if (data == null) return;
            data.frozenTextures.RemoveAll(f => f == null);

            s_showFrozen = EditorGUILayout.Foldout(s_showFrozen, $"FROZEN TEXTURES ({data.frozenTextures.Count})", true, EditorStyles.foldoutHeader);
            if (!s_showFrozen) return;

            if (data.frozenTextures.Count == 0)
            {
                EditorGUILayout.HelpBox("No frozen textures. Freeze a texture from the preview below to set its divisor and format by hand, or to skip it.", MessageType.Info);
                return;
            }

            s_frozenSearch = EditorGUILayout.TextField("Search", s_frozenSearch);
            bool useScroll = data.frozenTextures.Count >= 3;
            if (useScroll) s_frozenScroll = EditorGUILayout.BeginScrollView(s_frozenScroll, GUILayout.MaxHeight(250));

            int removeIndex = -1;
            for (int i = data.frozenTextures.Count - 1; i >= 0; i--)
            {
                var frozen = data.frozenTextures[i];
                string path = AssetDatabase.GUIDToAssetPath(frozen.textureGuid);
                var texture = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                string name = !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : frozen.textureGuid;
                if (!MatchesSearch(s_frozenSearch, name, path)) continue;

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                DrawThumbnail(texture);
                EditorGUILayout.BeginVertical();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(name, EditorStyles.boldLabel, GUILayout.MinWidth(0));
                if (GUILayout.Button("Unfreeze", GUILayout.Width(85))) removeIndex = i;
                EditorGUILayout.EndHorizontal();

                if (texture == null)
                {
                    EditorGUILayout.LabelField("Texture not found (deleted?).", EditorStyles.miniLabel);
                }

                EditorGUI.BeginChangeCheck();
                bool skip = EditorGUILayout.Toggle("Skip Compression", frozen.skip);
                int divisor = frozen.divisor;
                FrozenTextureFormat format = frozen.format;
                using (new EditorGUI.DisabledScope(skip))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Divisor", GUILayout.Width(75));
                    foreach (int div in Divisors)
                    {
                        bool selected = divisor == div;
                        if (GUILayout.Toggle(selected, div.ToString(), selected ? EditorStyles.miniButtonMid : EditorStyles.miniButton, GUILayout.Width(35)) && !selected)
                        {
                            divisor = div;
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Format", GUILayout.Width(75));
                    format = (FrozenTextureFormat)EditorGUILayout.EnumPopup(GUIContent.none, format);
                    EditorGUILayout.EndHorizontal();
                }
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(data, "Change Frozen Texture");
                    frozen.skip = skip;
                    frozen.divisor = divisor;
                    frozen.format = format;
                    EditorUtility.SetDirty(data);
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
            }

            if (useScroll) EditorGUILayout.EndScrollView();

            if (removeIndex >= 0)
            {
                SynFrozenTextures.Unfreeze(data.frozenTextures[removeIndex].textureGuid);
            }
        }

        private static void DrawThumbnail(Texture2D texture)
        {
            Texture preview = texture != null ? (Texture)AssetPreview.GetAssetPreview(texture) ?? AssetPreview.GetMiniThumbnail(texture) : null;
            Rect rect = GUILayoutUtility.GetRect(40, 40, GUILayout.Width(40), GUILayout.Height(40));
            if (preview != null) GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit);
            else GUI.Box(rect, GUIContent.none);
            if (texture != null && Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                EditorGUIUtility.PingObject(texture);
                Event.current.Use();
            }
        }

        private static bool MatchesSearch(string search, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            foreach (string value in values)
            {
                if (!string.IsNullOrEmpty(value) && value.IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Preview (the Avatar Compressor's preview, for the open scene)
        // ---------------------------------------------------------------------------------------------------------

        private sealed class PreviewEntry
        {
            public Texture2D Texture;
            public string Guid;
            public string Path;
            public SynTextureSlotKinds.Kind Kind;
            public bool IsProcessed;
            public string SkipReason;
            public float Complexity;
            public int Divisor;
            public Vector2Int OriginalSize, BaseSize, RecommendedSize;
            public long OriginalMemory, EstimatedMemory;
            public TextureFormat? PredictedFormat;
            public bool IsFrozen;
            public bool FormatOnly;
            public FrozenTextureFormat FrozenFormat;
            public SynTexelDensityInfo? Density;
            public Vector2Int? MaxResolutionWanted;
        }

        private static List<PreviewEntry> s_preview;
        private static string s_previewHash;
        private static bool s_showPreview;
        private static string s_previewSearch = "";
        private static Vector2 s_previewScroll;

        private static string PreviewHash(SynTextureCompressorSettings s)
        {
            return string.Join("|", s.Signature(), s.ProcessMainTextures, s.ProcessNormalMaps, s.ProcessEmissionMaps, s.ProcessOtherTextures,
                s.SkipUnknownUncompressedTextures, s.MinSourceSize, s.SkipIfSmallerThan, s.SizeFiltersUseSourceSize, s.ReformatFilteredTextures,
                s.LimitByWorldSize, s.KeepVisibleDetail, s.ViewDistanceCm, s.AnalysisBackend,
                SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectBakery", true), SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectMochie", true),
                SynSceneOptimizerSettings.GetString("SmartVRAM_ProtectedPatterns", ""), SynFrozenTextures.Signature(), SceneManager.GetActiveScene().path);
        }

        private void DrawPreview(SynTextureCompressorSettings s)
        {
            EditorGUILayout.LabelField("PREVIEW", EditorStyles.boldLabel);
            if (GUILayout.Button("Generate Preview", GUILayout.Height(30)))
            {
                try
                {
                    s_preview = GeneratePreview(s);
                    s_previewHash = PreviewHash(s);
                    s_showPreview = true;
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            if (!s_showPreview || s_preview == null) return;

            if (s_preview.Count == 0)
            {
                EditorGUILayout.HelpBox("No textures to compress in the open scene.", MessageType.Info);
                if (GUILayout.Button("Close")) s_showPreview = false;
                return;
            }

            if (PreviewHash(s) != s_previewHash)
            {
                EditorGUILayout.HelpBox("Settings changed since the preview was made. Generate it again to see their effect.", MessageType.Warning);
            }
            EditorGUILayout.HelpBox("Shows the open scene as it is. In a build, the passes that run first (Texture Deduplicator, atlases, color palettes, Unused Texture Shrinker) can change which textures reach the compressor.", MessageType.None);

            int processed = 0, frozenCount = 0, skipped = 0;
            long totalOriginal = 0, totalAfter = 0;
            foreach (var e in s_preview)
            {
                if (!e.IsProcessed) skipped++;
                else if (e.IsFrozen) frozenCount++;
                else processed++;
                totalOriginal += e.OriginalMemory;
                totalAfter += e.EstimatedMemory;
            }
            EditorGUILayout.LabelField(frozenCount > 0 ? $"{processed} to compress, {frozenCount} frozen, {skipped} skipped" : $"{processed} to compress, {skipped} skipped", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Original", MemoryCalculator.FormatBytes(totalOriginal), EditorStyles.boldLabel);
            EditorGUILayout.LabelField("After", MemoryCalculator.FormatBytes(totalAfter), EditorStyles.boldLabel);
            float savings = totalOriginal > 0 ? 1f - (float)totalAfter / totalOriginal : 0f;
            Color saved = GUI.color;
            GUI.color = Color.green;
            EditorGUILayout.LabelField("Savings", $"{savings:P0} (-{MemoryCalculator.FormatBytes(totalOriginal - totalAfter)})", EditorStyles.boldLabel);
            GUI.color = saved;
            EditorGUILayout.EndVertical();

            s_previewSearch = EditorGUILayout.TextField("Search", s_previewSearch);
            s_previewScroll = EditorGUILayout.BeginScrollView(s_previewScroll, GUILayout.MaxHeight(400));
            bool processedHeader = false, frozenHeader = false, skippedHeader = false;
            foreach (var e in s_preview)
            {
                if (!MatchesSearch(s_previewSearch, e.Texture != null ? e.Texture.name : "", e.Path, SynTextureSlotKinds.KindLabels[(int)e.Kind])) continue;
                if (e.IsProcessed && !e.IsFrozen && !processedHeader) { EditorGUILayout.LabelField("To compress", EditorStyles.boldLabel); processedHeader = true; }
                if (e.IsProcessed && e.IsFrozen && !frozenHeader) { EditorGUILayout.Space(6); EditorGUILayout.LabelField("Frozen (manual)", EditorStyles.boldLabel); frozenHeader = true; }
                if (!e.IsProcessed && !skippedHeader) { EditorGUILayout.Space(6); EditorGUILayout.LabelField("Skipped", EditorStyles.boldLabel); skippedHeader = true; }
                DrawPreviewEntry(e);
            }
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Close Preview"))
            {
                s_showPreview = false;
                s_preview = null;
            }
        }

        private static void DrawPreviewEntry(PreviewEntry e)
        {
            bool frozenChanged = e.Guid != null && e.IsFrozen != (SynFrozenTextures.Get(e.Guid) != null);
            using (new EditorGUI.DisabledScope(!e.IsProcessed || frozenChanged))
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.BeginVertical(GUILayout.Width(45));
                DrawThumbnail(e.Texture);
                EditorGUILayout.LabelField(SynTextureSlotKinds.KindLabels[(int)e.Kind], EditorStyles.centeredGreyMiniLabel, GUILayout.Width(45));
                EditorGUILayout.EndVertical();

                EditorGUILayout.BeginVertical();
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(e.Texture != null ? e.Texture.name : "(missing)", EditorStyles.boldLabel, GUILayout.MinWidth(0));
                if (e.IsProcessed && e.Guid != null)
                {
                    if (e.IsFrozen)
                    {
                        Color bg = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
                        if (GUILayout.Button("Unfreeze", GUILayout.Width(85))) SynFrozenTextures.Unfreeze(e.Guid);
                        GUI.backgroundColor = bg;
                    }
                    else if (GUILayout.Button("Freeze", GUILayout.Width(85)))
                    {
                        SynFrozenTextures.Freeze(e.Texture, e.Divisor);
                    }
                }
                EditorGUILayout.EndHorizontal();

                if (e.IsProcessed)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Detail", GUILayout.Width(85));
                    Rect bar = GUILayoutUtility.GetRect(100, 16, GUILayout.Width(100));
                    EditorGUI.DrawRect(bar, new Color(0.15f, 0.15f, 0.15f));
                    EditorGUI.DrawRect(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(e.Complexity), bar.height), Color.Lerp(Color.green, Color.red, e.Complexity));
                    EditorGUILayout.LabelField($"{e.Complexity:P0}", GUILayout.Width(45));
                    EditorGUILayout.EndHorizontal();

                    string manual = e.IsFrozen ? " (manual)" : "";
                    string size;
                    if (e.RecommendedSize == e.OriginalSize) size = $"{e.OriginalSize.x}x{e.OriginalSize.y} (unchanged){manual}";
                    else if (e.RecommendedSize.x > e.OriginalSize.x) size = $"{e.OriginalSize.x}x{e.OriginalSize.y} → {e.RecommendedSize.x}x{e.RecommendedSize.y} (larger, from the source file){manual}";
                    else size = $"{e.OriginalSize.x}x{e.OriginalSize.y} → {e.RecommendedSize.x}x{e.RecommendedSize.y} (÷{Mathf.Max(1, Mathf.RoundToInt(e.OriginalSize.x / (float)e.RecommendedSize.x))}){manual}";
                    if (e.FormatOnly) size += " (format only)";
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Size", GUILayout.Width(85));
                    EditorGUILayout.LabelField(size, GUILayout.MinWidth(0));
                    EditorGUILayout.EndHorizontal();

                    if (e.Density.HasValue)
                    {
                        var d = e.Density.Value;
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("In the world", GUILayout.Width(85));
                        EditorGUILayout.LabelField($"{d.DensityAt(e.BaseSize):F1} → {d.DensityAt(e.RecommendedSize):F1} px/cm (visible: {d.TargetDensity:F1})", EditorStyles.miniLabel, GUILayout.MinWidth(0));
                        EditorGUILayout.EndHorizontal();
                        if (e.MaxResolutionWanted.HasValue)
                        {
                            var w = e.MaxResolutionWanted.Value;
                            EditorGUILayout.HelpBox($"Max Resolution keeps this below what is visible. {w.x}x{w.y} would give {d.DensityAt(w):F1} px/cm for about {(float)w.x * w.y / ((float)e.RecommendedSize.x * e.RecommendedSize.y):F0}x the memory.", MessageType.Warning);
                        }
                    }

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Format", GUILayout.Width(85));
                    if (e.PredictedFormat.HasValue)
                    {
                        Color c = GUI.color;
                        GUI.color = FormatColor(e.PredictedFormat.Value);
                        EditorGUILayout.LabelField(FormatName(e.PredictedFormat.Value), EditorStyles.boldLabel, GUILayout.Width(80));
                        GUI.color = c;
                        string info = FormatInfo(e.PredictedFormat.Value);
                        if (e.IsFrozen && e.FrozenFormat != FrozenTextureFormat.Auto) info += " (manual)";
                        EditorGUILayout.LabelField(info, EditorStyles.miniLabel, GUILayout.MinWidth(0));
                    }
                    else
                    {
                        EditorGUILayout.LabelField("n/a", EditorStyles.miniLabel);
                    }
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Size", GUILayout.Width(85));
                    EditorGUILayout.LabelField($"{e.OriginalSize.x}x{e.OriginalSize.y}");
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Reason", GUILayout.Width(85));
                    EditorGUILayout.LabelField(e.SkipReason, EditorStyles.miniLabel, GUILayout.MinWidth(0));
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
            }
        }

        private static string FormatName(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.ASTC_4x4: return "ASTC 4x4";
                case TextureFormat.ASTC_6x6: return "ASTC 6x6";
                case TextureFormat.ASTC_8x8: return "ASTC 8x8";
                default: return format.ToString();
            }
        }

        private static string FormatInfo(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.DXT1: return "4 bpp, RGB, no alpha";
                case TextureFormat.DXT5: return "8 bpp, RGBA";
                case TextureFormat.BC5: return "8 bpp, normal maps";
                case TextureFormat.BC7: return "8 bpp, highest quality";
                case TextureFormat.ASTC_4x4: return "8 bpp, highest quality";
                case TextureFormat.ASTC_6x6: return "3.56 bpp, balanced";
                case TextureFormat.ASTC_8x8: return "2 bpp, smallest";
                default: return "";
            }
        }

        private static Color FormatColor(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.BC7:
                case TextureFormat.ASTC_4x4: return new Color(0.2f, 0.8f, 0.4f);
                case TextureFormat.BC5: return new Color(0.2f, 0.7f, 0.9f);
                case TextureFormat.DXT5:
                case TextureFormat.ASTC_6x6: return new Color(0.9f, 0.8f, 0.2f);
                case TextureFormat.DXT1:
                case TextureFormat.ASTC_8x8: return new Color(0.9f, 0.6f, 0.2f);
                default: return Color.white;
            }
        }

        private List<PreviewEntry> GeneratePreview(SynTextureCompressorSettings s)
        {
            var result = new List<PreviewEntry>();
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return result;

            EditorUtility.DisplayProgressBar("Texture Complexity Compressor", "Collecting textures...", 0f);
            string previousPass = SynProtectionData.CurrentPassId;
            List<Renderer> renderers;
            try
            {
                // The renderers this pass would get in a build
                SynProtectionData.CurrentPassId = Id;
                SynSceneQuery.ClearCache();
                renderers = SynSceneQuery.GetAllRenderers(scene);
                renderers.RemoveAll(r => r == null || SynProtectionData.IsProtected(r.gameObject));
            }
            finally
            {
                SynProtectionData.CurrentPassId = previousPass;
            }

            var tools = new Tools(s);
            var collection = Collect(scene, renderers, s);
            EditorUtility.DisplayProgressBar("Texture Complexity Compressor", "Scoring detail...", 0.2f);
            Score(collection.Candidates, s, tools.Processor);
            EditorUtility.DisplayProgressBar("Texture Complexity Compressor", "Measuring size in the world...", 0.5f);
            var densities = MeasureDensities(collection.Candidates, renderers, s);

            for (int i = 0; i < collection.Candidates.Count; i++)
            {
                var c = collection.Candidates[i];
                EditorUtility.DisplayProgressBar("Texture Complexity Compressor", $"Planning {c.SourceTexture.name}...", 0.6f + 0.4f * i / collection.Candidates.Count);
                Texture2D tex = c.SourceTexture;
                var plan = MakePlan(c, s, tools, null, densities);
                var selector = c.FormatOnly ? tools.FormatOnlySelector : tools.FormatSelector;

                TextureFormat format;
                if (!c.IsNormalMap && plan.Target == plan.Imported && selector.KeepsImportFormat(tex.format, plan.FormatOverride))
                {
                    format = tex.format;
                }
                else
                {
                    // As in the build: alpha and banding are judged on the resized pixels
                    Texture2D resized = tools.Processor.ResizeSingle(tex, plan.Target, c.IsNormalMap);
                    try
                    {
                        bool hasAlpha = TextureFormatSelector.HasSignificantAlpha(resized != null ? resized : tex);
                        format = selector.ResolveTargetFormat(tex.format, c.IsNormalMap, plan.Analysis.NormalizedComplexity, hasAlpha, plan.FormatOverride);
                        if (resized != null && !c.IsNormalMap && s.AvoidCompressionArtifacts && selector.ChoosesFormat(tex.format, plan.FormatOverride))
                        {
                            format = CompressionArtifactDetector.ChooseCleanFormat(resized, format);
                        }
                    }
                    finally
                    {
                        if (resized != null) UnityEngine.Object.DestroyImmediate(resized);
                    }
                }

                result.Add(new PreviewEntry
                {
                    Texture = tex,
                    Guid = c.Guid,
                    Path = c.AssetPath,
                    Kind = c.Kind,
                    IsProcessed = true,
                    Complexity = plan.Analysis.NormalizedComplexity,
                    Divisor = plan.Analysis.RecommendedDivisor,
                    OriginalSize = plan.Imported,
                    BaseSize = plan.BaseSize,
                    RecommendedSize = plan.Target,
                    OriginalMemory = MemoryCalculator.CalculateCompressedMemory(tex.width, tex.height, tex.format, tex.mipmapCount),
                    EstimatedMemory = MemoryCalculator.CalculateCompressedMemory(plan.Target.x, plan.Target.y, format, tex.mipmapCount),
                    PredictedFormat = format,
                    IsFrozen = c.Frozen != null,
                    FormatOnly = c.FormatOnly,
                    FrozenFormat = c.Frozen != null ? c.Frozen.format : FrozenTextureFormat.Auto,
                    Density = plan.Density,
                    MaxResolutionWanted = plan.MaxResolutionWanted
                });
            }

            foreach (var c in collection.Skipped)
            {
                Texture2D tex = c.SourceTexture;
                long memory = MemoryCalculator.CalculateCompressedMemory(tex.width, tex.height, tex.format, tex.mipmapCount);
                result.Add(new PreviewEntry
                {
                    Texture = tex,
                    Guid = c.Guid,
                    Path = c.AssetPath,
                    Kind = c.Kind,
                    IsProcessed = false,
                    SkipReason = c.SkipReason,
                    Divisor = 1,
                    OriginalSize = new Vector2Int(tex.width, tex.height),
                    RecommendedSize = new Vector2Int(tex.width, tex.height),
                    OriginalMemory = memory,
                    EstimatedMemory = memory,
                    IsFrozen = c.Frozen != null
                });
            }

            // Compress first, then frozen, then skipped; each by path (as the Avatar Compressor does)
            int Group(PreviewEntry e) => !e.IsProcessed ? 2 : e.IsFrozen ? 1 : 0;
            result.Sort((a, b) =>
            {
                int g = Group(a).CompareTo(Group(b));
                return g != 0 ? g : string.CompareOrdinal(a.Path ?? "", b.Path ?? "");
            });
            return result;
        }

        private static void DrawProtectedTextures()
        {
            EditorGUILayout.LabelField("PROTECTED TEXTURES & EXCLUSIONS", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Textures added here will NEVER be downscaled or touched by the complexity compressor.", MessageType.None);

            var protectionData = SynProtectionData.GetInstance();
            if (protectionData == null) return;

            // Drag and drop box for quick texture addition
            var dropArea = GUILayoutUtility.GetRect(0.0f, 35.0f, GUILayout.ExpandWidth(true));
            var dropBoxStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11
            };
            dropBoxStyle.normal.textColor = Color.gray;
            GUI.Box(dropArea, "Drag & Drop Textures Here to Protect Them", dropBoxStyle);

            var evt = Event.current;
            if ((evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform) && dropArea.Contains(evt.mousePosition))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    Undo.RecordObject(protectionData, "Add Protected Textures");
                    bool changed = false;
                    foreach (var dragged in DragAndDrop.objectReferences)
                    {
                        if (dragged is Texture2D t && !protectionData.protectedTextures.Contains(t))
                        {
                            protectionData.protectedTextures.Add(t);
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        EditorUtility.SetDirty(protectionData);
                        AssetDatabase.SaveAssets();
                    }
                }
            }

            // Name pattern filter
            string patterns = SynSceneOptimizerSettings.GetString("SmartVRAM_ProtectedPatterns", "");
            string newPatterns = EditorGUILayout.TextField(new GUIContent("Name Filter", "Any texture containing these keywords (comma separated, e.g. Poster, UI, Foliage) will be skipped."), patterns);
            if (newPatterns != patterns)
            {
                SynSceneOptimizerSettings.SetString("SmartVRAM_ProtectedPatterns", newPatterns);
            }

            // List individual slots
            for (int i = 0; i < protectionData.protectedTextures.Count; i++)
            {
                GUILayout.BeginHorizontal();
                protectionData.protectedTextures[i] = (Texture2D)EditorGUILayout.ObjectField(
                    protectionData.protectedTextures[i],
                    typeof(Texture2D),
                    false
                );

                if (GUILayout.Button("X", GUILayout.Width(20)))
                {
                    Undo.RecordObject(protectionData, "Remove Protected Texture");
                    protectionData.protectedTextures.RemoveAt(i);
                    EditorUtility.SetDirty(protectionData);
                    AssetDatabase.SaveAssets();
                    break;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Protected Texture Slot", GUILayout.Height(20)))
            {
                Undo.RecordObject(protectionData, "Add Protected Texture Slot");
                protectionData.protectedTextures.Add(null);
                EditorUtility.SetDirty(protectionData);
            }
            if (GUILayout.Button("Open Global Protection Window", GUILayout.Height(20)))
            {
                SynProtectionWindow.ShowWindow();
            }
            GUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Collection, scoring and planning (shared by the build and the preview)
        // ---------------------------------------------------------------------------------------------------------

        private class TextureCandidate
        {
            public Texture2D SourceTexture;
            public string AssetPath;
            public string Guid;
            public SynTextureSlotKinds.Kind Kind;
            public bool IsNormalMap;
            public bool IsEmission;
            public bool AlphaIsTransparency;
            public bool FormatOnly;
            public SynFrozenTexture Frozen;
            public float ComplexityScore;
            public string SkipReason;
            public readonly List<(Material Material, string Property)> Uses = new List<(Material, string)>();
            public readonly List<SynTextureSlotKinds.Kind> UseKinds = new List<SynTextureSlotKinds.Kind>();
            public Texture2D OptimizedTexture;
            public string Note;
        }

        private sealed class Collection
        {
            public readonly HashSet<Material> Materials = new HashSet<Material>();
            public readonly List<TextureCandidate> Candidates = new List<TextureCandidate>();
            public readonly List<TextureCandidate> Skipped = new List<TextureCandidate>();
            public readonly Dictionary<Texture2D, TextureCandidate> Map = new Dictionary<Texture2D, TextureCandidate>();
        }

        private sealed class Plan
        {
            public TextureAnalysisResult Analysis;
            public Vector2Int Imported, BaseSize, Target;
            public SynTexelDensityInfo? Density;
            public FrozenTextureFormat? FormatOverride;
            public Vector2Int? MaxResolutionWanted;
            public string Notes = "";
        }

        private sealed class Tools
        {
            public readonly TextureProcessor Processor;
            public readonly ComplexityCalculator ComplexityCalc;
            public readonly TextureFormatSelector FormatSelector;
            public readonly TextureFormatSelector FormatOnlySelector;

            public Tools(SynTextureCompressorSettings s)
            {
                Processor = new TextureProcessor(s.MinResolution, s.MaxResolution, s.ForcePowerOfTwo, s.ResizeBackend);
                ComplexityCalc = new ComplexityCalculator(s.HighComplexityThreshold, s.LowComplexityThreshold, s.MinDivisor, s.MaxDivisor);
                FormatSelector = new TextureFormatSelector(CompressionPlatform.Auto, s.UseHighQualityFormatForHighComplexity, s.HighComplexityThreshold, s.RespectImportFormat);
                FormatOnlySelector = new TextureFormatSelector(CompressionPlatform.Auto, s.UseHighQualityFormatForHighComplexity, s.HighComplexityThreshold, false);
            }
        }

        private static bool IsTypeEnabled(SynTextureSlotKinds.Kind kind, SynTextureCompressorSettings s)
        {
            switch (kind)
            {
                case SynTextureSlotKinds.Kind.Main: return s.ProcessMainTextures;
                case SynTextureSlotKinds.Kind.Normal: return s.ProcessNormalMaps;
                case SynTextureSlotKinds.Kind.Emission: return s.ProcessEmissionMaps;
                default: return s.ProcessOtherTextures;
            }
        }

        /// <summary>
        /// The Avatar Compressor's texture collection for the scene: every texture in the materials of the renderers
        /// being optimized, each either a candidate or skipped with the reason.
        /// </summary>
        private static Collection Collect(Scene scene, List<Renderer> renderers, SynTextureCompressorSettings s)
        {
            bool protectBakery = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectBakery", true);
            bool protectMochie = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectMochie", true);
            string patterns = SynSceneOptimizerSettings.GetString("SmartVRAM_ProtectedPatterns", "");
            string[] patternTokens = string.IsNullOrEmpty(patterns) ? new string[0] : patterns.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var frozenLookup = SynFrozenTextures.BuildLookup();
            var collection = new Collection();

            foreach (var r in renderers)
            {
                if (r == null || IsEditorOnly(r.transform) || SynSceneQuery.IsVideoComponentDetected(r)) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                foreach (var m in mats)
                {
                    if (m != null && !SynProtectionData.IsProtected(m)) collection.Materials.Add(m);
                }
            }

            // Every slot that uses each texture
            var all = new List<TextureCandidate>();
            foreach (var mat in collection.Materials)
            {
                if (mat.shader == null) continue;
                int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                    if (!(mat.GetTexture(propName) is Texture2D tex)) continue;

                    var kind = SynTextureSlotKinds.GetKind(propName, tex);
                    if (!collection.Map.TryGetValue(tex, out var c))
                    {
                        string path = AssetDatabase.GetAssetPath(tex);
                        c = new TextureCandidate
                        {
                            SourceTexture = tex,
                            AssetPath = path,
                            Guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                            Kind = kind
                        };
                        collection.Map[tex] = c;
                        all.Add(c);
                    }
                    // A texture used as a normal map anywhere is handled as one (as the Avatar Compressor does)
                    c.IsNormalMap |= kind == SynTextureSlotKinds.Kind.Normal;
                    c.IsEmission |= kind == SynTextureSlotKinds.Kind.Emission;
                    c.AlphaIsTransparency |= AlphaCoverage.IsCoverage(mat, propName);
                    if (c.Kind == SynTextureSlotKinds.Kind.Other && kind != SynTextureSlotKinds.Kind.Other) c.Kind = kind;
                    c.Uses.Add((mat, propName));
                    c.UseKinds.Add(kind);
                }
            }

            // A replacement only applies to redirected renderers. If anything else in the scene (protected/video
            // renderers, particles, UI, sprites, Udon, skybox, animated material swaps) still uses the original, both
            // versions would ship, so such textures are left alone.
            var pinned = CollectTexturesUsedOutsideRedirectedRenderers(scene, renderers);

            foreach (var c in all)
            {
                Texture2D tex = c.SourceTexture;
                if (c.Guid != null) frozenLookup.TryGetValue(c.Guid, out c.Frozen);
                c.SkipReason = GetSkipReason(c, s, protectBakery, protectMochie, patternTokens, pinned);
                if (c.SkipReason != null)
                {
                    collection.Map.Remove(tex);
                    collection.Skipped.Add(c);
                }
                else
                {
                    collection.Candidates.Add(c);
                }
            }
            return collection;
        }

        private static string GetSkipReason(TextureCandidate c, SynTextureCompressorSettings s, bool protectBakery, bool protectMochie, string[] patternTokens, HashSet<Texture2D> pinned)
        {
            Texture2D tex = c.SourceTexture;
            if (string.IsNullOrEmpty(c.AssetPath)) return "Made at runtime (no asset file)";
            if (protectBakery && SynProtectionData.IsBakeryAsset(c.AssetPath, tex)) return "Bakery lightmap data (Protect Bakery)";
            if (protectMochie && SynProtectionData.IsMochieAsset(c.AssetPath, tex)) return "Mochie shader texture (Protect Mochie)";
            if (SynProtectionData.IsProtected(tex)) return "Protected";
            if (patternTokens.Length > 0)
            {
                string lowerName = tex.name.ToLowerInvariant();
                foreach (var pat in patternTokens)
                {
                    if (lowerName.Contains(pat.Trim().ToLowerInvariant())) return $"Name filter ({pat.Trim()})";
                }
            }
            if (c.Frozen != null && c.Frozen.skip) return "Frozen: skip compression";

            // The size filters (Min Source Size / Skip If Smaller Than), on the size it scales from
            Vector2Int filterSize = s.ScalesFromSource && s.SizeFiltersUseSourceSize
                ? TextureSourceCache.GetPlannedBaseSize(tex, false, true, s.MaxResolution)
                : new Vector2Int(tex.width, tex.height);
            int maxDim = Mathf.Max(filterSize.x, filterSize.y);
            c.FormatOnly = false;
            if (maxDim < s.MinSourceSize || maxDim <= s.SkipIfSmallerThan)
            {
                if (!s.ReformatFilteredTextures) return $"Too small ({maxDim} px; size filters)";
                c.FormatOnly = true;
            }

            // Processed if any slot passes the type filter and the unknown-data check
            bool anyTypeEnabled = false;
            bool anySlot = false;
            for (int i = 0; i < c.Uses.Count; i++)
            {
                if (!IsTypeEnabled(c.UseKinds[i], s)) continue;
                anyTypeEnabled = true;
                bool unknownData = s.SkipUnknownUncompressedTextures && !TextureFormatInfo.IsCompressed(tex.format) && !SynTextureSlotKinds.IsKnownSlot(c.Uses[i].Property, tex);
                if (!unknownData)
                {
                    anySlot = true;
                    break;
                }
            }
            if (!anySlot) return anyTypeEnabled ? "Uncompressed, in an unknown shader slot (may be data)" : "Texture type turned off (Texture Filters)";

            if (pinned.Contains(tex)) return "Also used outside optimized renderers (both versions would ship)";
            return null;
        }

        private static void Score(List<TextureCandidate> candidates, SynTextureCompressorSettings s, TextureProcessor processor)
        {
            // Cached per texture content and analysis settings in Library/; the rest are scored in small batches (on
            // the GPU when available).
            var scoreCache = LoadScoreCache();
            string analysis = s.AnalysisSignature();
            var toAnalyze = new List<TextureCandidate>();
            foreach (var candidate in candidates)
            {
                if (scoreCache.TryGetValue(GetScoreKey(candidate, analysis), out float cachedScore)) candidate.ComplexityScore = cachedScore;
                else toAnalyze.Add(candidate);
            }
            if (toAnalyze.Count == 0) return;

            var analyzer = new TextureAnalyzer(s.Strategy, s.FastWeight, s.HighAccuracyWeight, s.PerceptualWeight, processor, s.AnalysisBackend);
            const int batchSize = 16;
            for (int start = 0; start < toAnalyze.Count; start += batchSize)
            {
                var batch = toAnalyze.GetRange(start, Math.Min(batchSize, toAnalyze.Count - start));
                var inputs = new Dictionary<Texture2D, TextureAnalysisInput>();
                foreach (var candidate in batch)
                {
                    inputs[candidate.SourceTexture] = new TextureAnalysisInput(candidate.IsNormalMap, candidate.AlphaIsTransparency);
                }

                var scores = analyzer.AnalyzeBatch(inputs);
                foreach (var candidate in batch)
                {
                    if (scores.TryGetValue(candidate.SourceTexture, out float score))
                    {
                        candidate.ComplexityScore = Mathf.Clamp01(score);
                        scoreCache[GetScoreKey(candidate, analysis)] = candidate.ComplexityScore;
                    }
                    else
                    {
                        candidate.ComplexityScore = AnalysisConstants.DefaultComplexityScore;
                    }
                }
            }
            SaveScoreCache(scoreCache);
        }

        private static Dictionary<Texture2D, SynTexelDensityInfo> MeasureDensities(List<TextureCandidate> candidates, List<Renderer> renderers, SynTextureCompressorSettings s)
        {
            if (!s.LimitByWorldSize) return null;
            var uses = new Dictionary<Texture2D, List<(Material, string)>>();
            foreach (var c in candidates)
            {
                if (!c.FormatOnly && c.Frozen == null) uses[c.SourceTexture] = c.Uses;
            }
            return SynWorldTexelDensity.Analyze(renderers, uses, s.ViewDistanceCm);
        }

        /// <summary>
        /// The Avatar Compressor's size decision: a frozen divisor, or the detail score's divisor limited by how large
        /// the texture appears in the world and floored to keep visible detail.
        /// </summary>
        private static Plan MakePlan(TextureCandidate c, SynTextureCompressorSettings s, Tools tools, TextureSourceCache sourceCache, Dictionary<Texture2D, SynTexelDensityInfo> densities)
        {
            Texture2D src = c.SourceTexture;
            var plan = new Plan { Imported = new Vector2Int(src.width, src.height) };

            // Build: the source the cache made; preview: the importer's source size (nothing is imported)
            plan.BaseSize = plan.Imported;
            if (s.ScalesFromSource)
            {
                if (sourceCache != null)
                {
                    if (sourceCache.TryGetCachedSize(src, out Vector2Int sourceSize)) plan.BaseSize = TextureSourceCache.FitWithin(sourceSize, s.MaxResolution);
                }
                else
                {
                    plan.BaseSize = TextureSourceCache.GetPlannedBaseSize(src, false, true, s.MaxResolution);
                }
            }

            bool frozen = c.Frozen != null;
            if (frozen)
            {
                int divisor = Mathf.Max(1, c.Frozen.divisor);
                plan.Analysis = new TextureAnalysisResult(AnalysisConstants.DefaultComplexityScore, divisor, tools.Processor.CalculateNewDimensions(plan.BaseSize.x, plan.BaseSize.y, divisor));
                plan.FormatOverride = c.Frozen.format;
                plan.Notes = $"frozen: divisor {divisor}x, format {c.Frozen.format}";
            }
            else
            {
                plan.Analysis = AnalysisResultHelper.BuildResult(c.ComplexityScore, plan.BaseSize.x, plan.BaseSize.y, c.IsEmission, c.IsNormalMap, tools.ComplexityCalc, tools.Processor);
                plan.Density = !c.FormatOnly && densities != null && densities.TryGetValue(src, out var measured) ? measured : (SynTexelDensityInfo?)null;
                if (plan.Density.HasValue)
                {
                    var density = plan.Density.Value;
                    int scored = plan.Analysis.RecommendedDivisor;
                    plan.Analysis = SynWorldTexelDensity.ApplyLimit(plan.Analysis, plan.BaseSize, density, tools.Processor);
                    if (plan.Analysis.RecommendedDivisor != scored)
                    {
                        plan.Notes += $" | sharper than visible ({density.DensityAt(plan.BaseSize):F1} px/cm, needs {density.TargetDensity:F1}): divisor {scored}x -> {plan.Analysis.RecommendedDivisor}x";
                    }
                    if (s.KeepVisibleDetail)
                    {
                        int limited = plan.Analysis.RecommendedDivisor;
                        plan.Analysis = SynWorldTexelDensity.ApplyFloor(plan.Analysis, plan.BaseSize, density, s.MinDivisor, s.LowComplexityThreshold, tools.Processor);
                        if (plan.Analysis.RecommendedDivisor != limited)
                        {
                            var shrunk = new Vector2Int(plan.BaseSize.x / limited, plan.BaseSize.y / limited);
                            plan.Notes += $" | divisor {limited}x would leave {density.DensityAt(shrunk):F1} px/cm, keeping detail: divisor {plan.Analysis.RecommendedDivisor}x";
                        }
                    }
                }
            }

            plan.Target = tools.Processor.CalculateTargetSize(plan.BaseSize, plan.Analysis);
            if (c.FormatOnly)
            {
                // Caught by the size filters: keep the imported size, only the format changes
                plan.Target = new Vector2Int(TextureProcessor.EnsureMultipleOf4(plan.Imported.x), TextureProcessor.EnsureMultipleOf4(plan.Imported.y));
            }
            else if (!frozen && !s.RespectImportMaxSize)
            {
                // From a larger source file, the import size is still the default: only grow past it when visible
                plan.Target = SynWorldTexelDensity.GateUpscale(plan.Target, plan.Imported, plan.Analysis.NormalizedComplexity, s.LowComplexityThreshold, plan.Density);
            }

            if (!frozen && !c.FormatOnly && plan.Density.HasValue)
            {
                Vector2Int fullSize = plan.Imported;
                if (s.ScalesFromSource && TextureSourceCache.TryGetSourceSize(src, out Vector2Int sourceFull)) fullSize = TextureSourceCache.FitWithin(sourceFull, TextureSourceCache.MaxSourceSize);
                if (SynWorldTexelDensity.IsLimitedByMaxResolution(plan.Target, fullSize, s.MaxResolution, plan.Density.Value, plan.Analysis.NormalizedComplexity, s.LowComplexityThreshold, out Vector2Int wanted))
                {
                    plan.MaxResolutionWanted = wanted;
                }
            }
            return plan;
        }

        // v4: frozen textures, process filters, reformat filtered textures, low-detail visible floor
        private const string TextureHashTag = "SmartVRAM_v4";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool verbose = SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false);

            SynTargetPlatform currentPlatform = SynAssetCache.GetCurrentTargetPlatform();
            bool isMobile = currentPlatform == SynTargetPlatform.Android || currentPlatform == SynTargetPlatform.iOS;
            var settings = SynTextureCompressorSettings.Load(isMobile);
            var tools = new Tools(settings);

            var collection = Collect(scene, renderers, settings);
            var candidateList = collection.Candidates;
            if (verbose && collection.Skipped.Count > 0)
            {
                var sb = new System.Text.StringBuilder("[SYN SCENE OPTIMIZER] Texture Complexity Compressor: skipped textures:\n");
                foreach (var c in collection.Skipped) sb.AppendLine($" • '{c.SourceTexture.name}': {c.SkipReason}");
                Debug.Log(sb.ToString());
            }
            if (candidateList.Count == 0) return;

            Score(candidateList, settings, tools.Processor);

            // Size and format for each texture, made from the original image file
            TextureSourceCache sourceCache = null;
            if (settings.UseSourceFiles)
            {
                sourceCache = new TextureSourceCache(SynAssetCache.BaseCachePath + "_SourceImport");
                using (SynAssetDatabaseScope.Suspend())
                {
                    // Imports the source files, which can't happen inside the pipeline's StartAssetEditing batch
                    var sources = new List<Texture2D>();
                    foreach (var c in candidateList) sources.Add(c.SourceTexture);
                    sourceCache.Prepare(sources);
                }
            }

            var densities = MeasureDensities(candidateList, renderers, settings);

            int downscaledCount = 0;
            long estimatedBytesSaved = 0;
            var normalPreprocessor = new NormalMapPreprocessor();
            foreach (var candidate in candidateList)
            {
                try
                {
                    candidate.OptimizedTexture = Optimize(candidate, settings, tools, sourceCache, densities, normalPreprocessor, currentPlatform) ?? candidate.SourceTexture;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SYN SCENE OPTIMIZER] Texture Complexity Compressor: could not optimize '{candidate.SourceTexture.name}', keeping it: {e.Message}");
                    candidate.OptimizedTexture = candidate.SourceTexture;
                }

                if (candidate.OptimizedTexture != candidate.SourceTexture)
                {
                    downscaledCount++;
                    estimatedBytesSaved += EstimateGpuBytes(candidate.SourceTexture) - EstimateGpuBytes(candidate.OptimizedTexture);
                }
            }

            // Virtual staging of scene materials with optimized textures in-place
            int materialsReLinked = 0;
            foreach (var mat in collection.Materials)
            {
                if (mat == null || mat.shader == null || SynProtectionData.IsProtected(mat)) continue;

                int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                bool matHasOptimizedTex = false;
                for (int i = 0; i < propCount && !matHasOptimizedTex; i++)
                {
                    if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    Texture tex = mat.GetTexture(ShaderUtil.GetPropertyName(mat.shader, i));
                    matHasOptimizedTex = tex is Texture2D tex2D && collection.Map.TryGetValue(tex2D, out var candidate) && candidate.OptimizedTexture != null && candidate.OptimizedTexture != tex2D;
                }
                if (!matHasOptimizedTex) continue;

                SynVirtualMaterialState matState = SynPipelineCompactor.GetStagingState(mat);
                if (matState == null) continue;
                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                    if (mat.GetTexture(propName) is Texture2D tex2D && collection.Map.TryGetValue(tex2D, out var candidate)
                        && candidate.OptimizedTexture != null && candidate.OptimizedTexture != tex2D)
                    {
                        matState.TrackedTextures[propName] = candidate.OptimizedTexture;
                        matState.IsDirty = true;
                    }
                }
                if (!matState.AppliedPassTags.Contains("SmartVRAM")) matState.AppliedPassTags.Add("SmartVRAM");
                materialsReLinked++;
            }

            if (downscaledCount > 0)
            {
                double savedMB = estimatedBytesSaved / (1024.0 * 1024.0);
                string msg = $"Texture Complexity Compressor ({SynTextureCompressorSettings.PresetLabels[Math.Max(0, Array.IndexOf(SynTextureCompressorSettings.PresetNames, settings.Preset))]}): Optimized {downscaledCount} / {candidateList.Count} textures across {materialsReLinked} materials (Saved Est. ~{savedMB:F1} MB VRAM).";
                Debug.Log($"[SYN SCENE OPTIMIZER] {msg}");
                SynPipelineCompactor.LogChange(Name, msg);
            }
            else
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Texture Complexity Compressor: Analyzed {candidateList.Count} textures. All textures are optimal or already at appropriate resolution.");
            }

            if (verbose)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[SYN SCENE OPTIMIZER] Detailed Texture Complexity Breakdown:");
                foreach (var c in candidateList)
                {
                    Texture2D o = c.OptimizedTexture;
                    string result = o != null && o != c.SourceTexture
                        ? $"{c.SourceTexture.width}x{c.SourceTexture.height} {c.SourceTexture.format} -> {o.width}x{o.height} {o.format}"
                        : $"kept at {c.SourceTexture.width}x{c.SourceTexture.height} {c.SourceTexture.format}";
                    string tags = (c.Frozen != null ? " [FROZEN]" : "") + (c.FormatOnly ? " [FORMAT ONLY]" : "");
                    sb.AppendLine($" • '{c.SourceTexture.name}'{tags} (detail {c.ComplexityScore:P0}{(c.IsNormalMap ? ", normal map" : "")}{(c.IsEmission ? ", emission" : "")}): {result} | {c.Note}");
                }
                Debug.Log(sb.ToString());
            }
        }

        /// <summary>
        /// The Avatar Compressor's per-texture compression: size from the plan, format from detail and alpha with the
        /// banding check (or the frozen format), normal maps through the layout conversion, all made from the
        /// original image file when there is one. Returns the original when nothing would change.
        /// </summary>
        private Texture2D Optimize(
            TextureCandidate c,
            SynTextureCompressorSettings s,
            Tools tools,
            TextureSourceCache sourceCache,
            Dictionary<Texture2D, SynTexelDensityInfo> densities,
            NormalMapPreprocessor normalPreprocessor,
            SynTargetPlatform platform)
        {
            Texture2D src = c.SourceTexture;
            Plan plan = MakePlan(c, s, tools, sourceCache, densities);
            Vector2Int target = plan.Target;
            Vector2Int imported = plan.Imported;
            TextureAnalysisResult analysis = plan.Analysis;
            c.Note = (plan.Density.HasValue
                ? $"{plan.Density.Value.DensityAt(target):F1} px/cm in the world (visible: {plan.Density.Value.TargetDensity:F1}), divisor {analysis.RecommendedDivisor}x"
                : $"size in the world not measured, divisor {analysis.RecommendedDivisor}x") + plan.Notes;

            if (plan.MaxResolutionWanted.HasValue)
            {
                var wanted = plan.MaxResolutionWanted.Value;
                var density = plan.Density.Value;
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Texture Complexity Compressor: '{src.name}': Max Resolution {s.MaxResolution} keeps it at {target.x}x{target.y}, {density.DensityAt(target):F1} px/cm in the world (visible: {density.TargetDensity:F1}). {wanted.x}x{wanted.y} would give {density.DensityAt(wanted):F1} px/cm for about {(float)wanted.x * wanted.y / ((float)target.x * target.y):F0}x the memory; raise Max Resolution to keep more detail.");
            }

            TextureFormatSelector selector = c.FormatOnly ? tools.FormatOnlySelector : tools.FormatSelector;
            TextureFormat sourceFormat = src.format;
            FrozenTextureFormat? formatOverride = plan.FormatOverride;

            // Unity's import is already one compression of the source file; with the same size and format, keep it.
            // Normal maps always go through the layout conversion.
            if (!c.IsNormalMap && target == imported && selector.KeepsImportFormat(sourceFormat, formatOverride)) return src;

            string hash = SynAssetCache.ComputeTextureHash(src, TextureHashTag, target.x, target.y,
                $"{platform}_{s.Signature()}_{c.ComplexityScore:F3}_{(c.IsNormalMap ? 1 : 0)}{(c.IsEmission ? 1 : 0)}{(c.AlphaIsTransparency ? 1 : 0)}{(c.FormatOnly ? 1 : 0)}_{(formatOverride.HasValue ? formatOverride.Value.ToString() : "auto")}");
            if (SynAssetCache.TryGetCachedAsset<Texture2D>(SynAssetCache.TexturesCategory, hash, out Texture2D cached)) return cached;

            // Resize from the full-quality source when available; the normal-map layout is read from the same pixels
            Texture2D source = sourceCache?.Load(src);
            bool fromSource = source != null;
            NormalMapPreprocessor.SourceLayout layout = NormalMapPreprocessor.SourceLayout.Auto;
            Texture2D resized;
            try
            {
                if (c.IsNormalMap)
                {
                    layout = !fromSource
                        ? NormalMapSourceLayoutDetector.Resolve(src, sourceFormat)
                        : TextureSourceCache.IsRawNormalSource(src) ? NormalMapPreprocessor.SourceLayout.RGB : NormalMapSourceLayoutDetector.DetectDXTnmLike(source);
                }
                resized = tools.Processor.ResizeSingle(fromSource ? source : src, target, c.IsNormalMap);
            }
            finally
            {
                if (fromSource) UnityEngine.Object.DestroyImmediate(source);
            }
            if (resized == null) return src;

            // Alpha is checked on the resized pixels: alpha that only existed in fine detail may be gone
            bool hasAlpha = TextureFormatSelector.HasSignificantAlpha(resized);
            TextureFormat format = selector.ResolveTargetFormat(sourceFormat, c.IsNormalMap, analysis.NormalizedComplexity, hasAlpha, formatOverride);
            if (!c.IsNormalMap && s.AvoidCompressionArtifacts && selector.ChoosesFormat(sourceFormat, formatOverride))
            {
                format = CompressionArtifactDetector.ChooseCleanFormat(resized, format);
            }

            if (!c.IsNormalMap && target == imported && format == sourceFormat && TextureFormatInfo.IsCompressed(sourceFormat))
            {
                UnityEngine.Object.DestroyImmediate(resized);
                return src;
            }

            bool preserveAlpha = c.IsNormalMap && NormalMapPreprocessor.ShouldPreserveSemanticAlpha(format, layout, hasAlpha);
            Color32[] originalPixels = c.IsNormalMap && resized.isReadable ? resized.GetPixels32() : null;
            if (resized.format != format && !TryCompress(resized, sourceFormat, format, c.IsNormalMap, preserveAlpha, layout, normalPreprocessor))
            {
                // Primary compression failed: restore the pixels and fall back like the Avatar Compressor
                if (originalPixels != null)
                {
                    resized.SetPixels32(originalPixels);
                    resized.Apply(resized.mipmapCount > 1);
                }
                TextureFormat fallback = TextureFormatSelector.ResolvePlatform(CompressionPlatform.Auto) == CompressionPlatform.Mobile ? TextureFormat.ASTC_6x6 : TextureFormat.DXT5;
                if (!TryCompress(resized, sourceFormat, fallback, c.IsNormalMap, false, layout, normalPreprocessor))
                {
                    UnityEngine.Object.DestroyImmediate(resized);
                    return src;
                }
            }

            resized.wrapModeU = src.wrapModeU;
            resized.wrapModeV = src.wrapModeV;
            resized.filterMode = src.filterMode;
            resized.anisoLevel = src.anisoLevel;
            resized.name = $"{src.name}_opt_{resized.width}x{resized.height}";
            if (resized.mipmapCount > 1)
            {
                // VRChat warns about textures without mipmap streaming
                var serialized = new SerializedObject(resized);
                SerializedProperty streaming = serialized.FindProperty("m_StreamingMipmaps");
                if (streaming != null)
                {
                    streaming.boolValue = true;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            return SynAssetCache.SaveCachedAsset(resized, SynAssetCache.TexturesCategory, hash);
        }

        private static bool TryCompress(Texture2D texture, TextureFormat sourceFormat, TextureFormat targetFormat, bool isNormalMap, bool preserveAlpha, NormalMapPreprocessor.SourceLayout layout, NormalMapPreprocessor normalPreprocessor)
        {
            try
            {
                if (isNormalMap) normalPreprocessor.PrepareForCompression(texture, sourceFormat, targetFormat, preserveAlpha, layout);
                EditorUtility.CompressTexture(texture, targetFormat, TextureCompressionQuality.Best);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Texture Complexity Compressor: could not compress '{texture.name}' to {targetFormat}: {e.Message}");
                return false;
            }
        }

        private static long EstimateGpuBytes(Texture2D tex)
        {
            if (tex == null) return 0;
            long total = 0;
            for (int mip = 0; mip < tex.mipmapCount; mip++)
            {
                total += GraphicsFormatUtility.ComputeMipmapSize(Mathf.Max(1, tex.width >> mip), Mathf.Max(1, tex.height >> mip), tex.graphicsFormat);
            }
            return total;
        }

        private const string ScoreCachePath = "Library/SynSceneOptimizer/TextureComplexityScores.txt";

        // Identity hash covers the texture content and import settings; flags, analysis settings and algorithm version
        // cover the analysis. v6: strategy in the analysis settings, normal maps decoded from their packed layout
        private static string GetScoreKey(TextureCandidate c, string analysis)
        {
            return $"v6|{SynAssetCache.GetAssetIdentityHash(c.SourceTexture)}|n{(c.IsNormalMap ? 1 : 0)}|a{(c.AlphaIsTransparency ? 1 : 0)}|w{analysis}";
        }

        private static Dictionary<string, float> LoadScoreCache()
        {
            var cache = new Dictionary<string, float>();
            if (!File.Exists(ScoreCachePath)) return cache;
            try
            {
                foreach (string line in File.ReadAllLines(ScoreCachePath))
                {
                    int tab = line.LastIndexOf('\t');
                    if (tab > 0 && float.TryParse(line.Substring(tab + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float score))
                    {
                        cache[line.Substring(0, tab)] = score;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not read texture score cache: {e.Message}");
            }
            return cache;
        }

        private static void SaveScoreCache(Dictionary<string, float> cache)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ScoreCachePath));
                var lines = new List<string>(cache.Count);
                foreach (var kvp in cache) lines.Add(kvp.Key + "\t" + kvp.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllLines(ScoreCachePath, lines);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not save texture score cache: {e.Message}");
            }
        }

        /// <summary>
        /// Textures that something other than the redirected Mesh/Skinned renderers references directly or via a
        /// material: other components (protected and video renderers, particles, UI, sprites, Udon/UdonSharp fields,
        /// lights, probes), the skybox, and materials/textures/sprites swapped in by animation clips.
        /// </summary>
        internal static HashSet<Texture2D> CollectTexturesUsedOutsideRedirectedRenderers(Scene scene, List<Renderer> renderers)
        {
            var redirected = new HashSet<Renderer>();
            foreach (Renderer r in renderers)
            {
                if (r != null && !SynSceneQuery.IsVideoComponentDetected(r)) redirected.Add(r);
            }

            var pinned = new HashSet<Texture2D>();
            var visitedMaterials = new HashSet<Material>();
            var visitedClips = new HashSet<AnimationClip>();

            void PinObject(UnityEngine.Object obj)
            {
                switch (obj)
                {
                    case Texture2D tex:
                        pinned.Add(tex);
                        break;
                    case Sprite sprite when sprite.texture != null:
                        pinned.Add(sprite.texture);
                        break;
                    case Material mat when visitedMaterials.Add(mat) && mat.shader != null:
                        int count = ShaderUtil.GetPropertyCount(mat.shader);
                        for (int i = 0; i < count; i++)
                        {
                            if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                            if (mat.GetTexture(ShaderUtil.GetPropertyName(mat.shader, i)) is Texture2D matTex) pinned.Add(matTex);
                        }
                        break;
                }
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform || component is MeshFilter) continue;
                    if (component is Renderer r && redirected.Contains(r)) continue;

                    if (component is Animator animator && animator.runtimeAnimatorController != null)
                    {
                        foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                        {
                            if (clip == null || !visitedClips.Add(clip)) continue;
                            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                            {
                                foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                                {
                                    PinObject(key.value);
                                }
                            }
                        }
                    }

                    var iterator = new SerializedObject(component).GetIterator();
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue != null)
                        {
                            PinObject(iterator.objectReferenceValue);
                        }
                    }
                }
            }

            if (RenderSettings.skybox != null) PinObject(RenderSettings.skybox);
            return pinned;
        }

        private static bool IsEditorOnly(Transform t)
        {
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }
    }
}
