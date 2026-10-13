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

        private static void DrawToggle(string key, bool defaultValue, string label, string tooltip)
        {
            bool value = SynSceneOptimizerSettings.GetBool(key, defaultValue);
            bool newValue = EditorGUILayout.Toggle(new GUIContent(label, tooltip), value);
            if (newValue != value) SynSceneOptimizerSettings.SetBool(key, newValue);
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Makes the Avatar Compressor's decisions for scene textures: scores how much visible detail each texture has, sizes it by that score (and by how large it appears in the world), picks the compression format from its detail and alpha, and compresses from the original image file. Results are cached per platform.", MessageType.Info);

            bool protectBakery = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectBakery", true);
            bool newProtectBakery = EditorGUILayout.Toggle(new GUIContent("Protect Bakery & Lightmaps", "Guarantees that all Bakery lightmaps, directional maps, volumes, and shadowmasks are never downscaled."), protectBakery);
            if (newProtectBakery != protectBakery) SynSceneOptimizerSettings.SetBool("SmartVRAM_ProtectBakery", newProtectBakery);

            bool protectMochie = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectMochie", true);
            bool newProtectMochie = EditorGUILayout.Toggle(new GUIContent("Protect Mochie Textures", "Guarantees that all default Mochie shader lookup textures, normal arrays, and rain sheets inside Assets/Mochie/ are never touched."), protectMochie);
            if (newProtectMochie != protectMochie) SynSceneOptimizerSettings.SetBool("SmartVRAM_ProtectMochie", newProtectMochie);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("PRESET", EditorStyles.boldLabel);
            string preset = SynTextureCompressorSettings.GetPreset();
            int presetIndex = Mathf.Max(0, System.Array.IndexOf(SynTextureCompressorSettings.PresetNames, preset));
            int newPresetIndex = EditorGUILayout.Popup(new GUIContent("Preset", "The Avatar Compressor's presets: the same name makes the same decisions in both tools. Custom uses the values below."), presetIndex, SynTextureCompressorSettings.PresetLabels);
            if (newPresetIndex != presetIndex) SynSceneOptimizerSettings.SetString(SynTextureCompressorSettings.PresetKey, SynTextureCompressorSettings.PresetNames[newPresetIndex]);

            bool mobileTarget = SynAssetCache.GetCurrentTargetPlatform() != SynTargetPlatform.PC;
            var resolved = SynTextureCompressorSettings.Load(mobileTarget);
            if (resolved.Preset != "Custom")
            {
                EditorGUILayout.HelpBox($"{SynTextureCompressorSettings.PresetLabels[newPresetIndex]}: {resolved.MinResolution}-{resolved.MaxResolution} px, divisor {resolved.MinDivisor}x-{resolved.MaxDivisor}x, full size above {resolved.HighComplexityThreshold:P0} detail, smallest below {resolved.LowComplexityThreshold:P0}. Textures of {resolved.SkipIfSmallerThan} px or less are left alone.", MessageType.None);
            }
            else
            {
                float highThresh = SynSceneOptimizerSettings.GetFloat("SmartVRAM_HighComplexityThreshold", 0.70f);
                float newHighThresh = EditorGUILayout.Slider(new GUIContent("High Detail Threshold", "Textures above this detail score keep full resolution and get the high-quality format."), highThresh, 0.10f, 0.95f);
                if (Mathf.Abs(newHighThresh - highThresh) > 0.001f) SynSceneOptimizerSettings.SetFloat("SmartVRAM_HighComplexityThreshold", newHighThresh);

                float lowThresh = SynSceneOptimizerSettings.GetFloat("SmartVRAM_LowComplexityThreshold", 0.20f);
                float newLowThresh = EditorGUILayout.Slider(new GUIContent("Low Detail Threshold", "Textures below this detail score get the largest divisor."), lowThresh, 0.05f, 0.45f);
                if (Mathf.Abs(newLowThresh - lowThresh) > 0.001f) SynSceneOptimizerSettings.SetFloat("SmartVRAM_LowComplexityThreshold", newLowThresh);

                string[] divisorLabels = { "1x", "2x", "4x", "8x", "16x" };
                int[] divisors = { 1, 2, 4, 8, 16 };
                int minDiv = SynSceneOptimizerSettings.GetInt("SmartVRAM_MinDivisor", 1);
                int newMinDiv = EditorGUILayout.IntPopup("Min Divisor", minDiv, divisorLabels, divisors);
                if (newMinDiv != minDiv) SynSceneOptimizerSettings.SetInt("SmartVRAM_MinDivisor", newMinDiv);
                int maxDiv = SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxDivisor", 8);
                int newMaxDiv = EditorGUILayout.IntPopup("Max Divisor", maxDiv, divisorLabels, divisors);
                if (newMaxDiv != maxDiv) SynSceneOptimizerSettings.SetInt("SmartVRAM_MaxDivisor", newMaxDiv);

                int pcMax = SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxResPC", 2048);
                int newPCMax = EditorGUILayout.IntPopup("Max PC Resolution", pcMax, new string[] { "512", "1024", "2048", "4096" }, new int[] { 512, 1024, 2048, 4096 });
                if (newPCMax != pcMax) SynSceneOptimizerSettings.SetInt("SmartVRAM_MaxResPC", newPCMax);
                int pcMin = SynSceneOptimizerSettings.GetInt("SmartVRAM_MinResPC", 512);
                int newPCMin = EditorGUILayout.IntPopup("Min PC Resolution", pcMin, new string[] { "32", "64", "128", "256", "512", "1024" }, new int[] { 32, 64, 128, 256, 512, 1024 });
                if (newPCMin != pcMin) SynSceneOptimizerSettings.SetInt("SmartVRAM_MinResPC", newPCMin);
                int androidMax = SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxResAndroid", 1024);
                int newAndroidMax = EditorGUILayout.IntPopup("Max Quest Resolution", androidMax, new string[] { "512", "1024", "2048" }, new int[] { 512, 1024, 2048 });
                if (newAndroidMax != androidMax) SynSceneOptimizerSettings.SetInt("SmartVRAM_MaxResAndroid", newAndroidMax);
                int androidMin = SynSceneOptimizerSettings.GetInt("SmartVRAM_MinResAndroid", 256);
                int newAndroidMin = EditorGUILayout.IntPopup("Min Quest Resolution", androidMin, new string[] { "32", "64", "128", "256", "512" }, new int[] { 32, 64, 128, 256, 512 });
                if (newAndroidMin != androidMin) SynSceneOptimizerSettings.SetInt("SmartVRAM_MinResAndroid", newAndroidMin);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("SIZE IN THE WORLD", EditorStyles.boldLabel);
            DrawToggle("SmartVRAM_LimitByWorldSize", true, "Limit By Size In World", "Measures how large each texture appears on the meshes that use it. A texture sharper than anyone can see from the viewing distance (a 2048 texture on a mug) is made smaller.");
            DrawToggle("SmartVRAM_KeepVisibleDetail", true, "Keep Visible Detail", "Doesn't shrink a detailed texture below what is visible from the viewing distance, even when its detail score allows it (e.g. a large wall).");
            float pcDistance = SynSceneOptimizerSettings.GetFloat("SmartVRAM_ViewDistancePC", SynTextureCompressorSettings.DefaultViewDistancePC);
            float newPcDistance = EditorGUILayout.Slider(new GUIContent("Viewing Distance PC (cm)", "How close players get to surfaces. Closer needs sharper textures."), pcDistance, 20f, 500f);
            if (Mathf.Abs(newPcDistance - pcDistance) > 0.5f) SynSceneOptimizerSettings.SetFloat("SmartVRAM_ViewDistancePC", Mathf.Round(newPcDistance));
            float questDistance = SynSceneOptimizerSettings.GetFloat("SmartVRAM_ViewDistanceAndroid", SynTextureCompressorSettings.DefaultViewDistanceAndroid);
            float newQuestDistance = EditorGUILayout.Slider(new GUIContent("Viewing Distance Quest (cm)", "How close players get to surfaces on Quest builds."), questDistance, 20f, 500f);
            if (Mathf.Abs(newQuestDistance - questDistance) > 0.5f) SynSceneOptimizerSettings.SetFloat("SmartVRAM_ViewDistanceAndroid", Mathf.Round(newQuestDistance));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("ADVANCED", EditorStyles.boldLabel);
            DrawToggle("SmartVRAM_RespectImportMaxSize", true, "Respect Import Max Size", "Scale from the size the texture is imported at. When off, scale from the full source file (fit to the max resolution); the result only grows past the import size when the extra detail is visible.");
            DrawToggle("SmartVRAM_RespectImportFormat", true, "Respect Import Format", "Keep the compressed format the texture is imported with. When off, the format is chosen from its detail and alpha (DXT1/BC7/BC5, ASTC on Quest).");
            DrawToggle("SmartVRAM_AvoidCompressionArtifacts", true, "Avoid Compression Artifacts", "Test-compresses smooth areas and steps up to a format that doesn't band or show blocks.");
            DrawToggle("SmartVRAM_SkipUnknownUncompressed", true, "Skip Unknown Uncompressed Textures", "Leaves uncompressed textures in unrecognized shader slots alone: they may hold data (lookup tables, masks) that compression would damage.");

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("PROTECTED TEXTURES & EXCLUSIONS", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Textures added here will NEVER be downscaled or touched by the complexity compressor.", MessageType.None);

            var protectionData = SynProtectionData.GetInstance();
            if (protectionData != null)
            {
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
        }

        private class TextureCandidate
        {
            public Texture2D SourceTexture;
            public string AssetPath;
            public bool IsNormalMap;
            public bool IsEmission;
            public bool AlphaIsTransparency;
            public bool KnownSlot;
            public bool FormatOnly;
            public float ComplexityScore;
            public readonly List<(Material Material, string Property)> Uses = new List<(Material, string)>();
            public Texture2D OptimizedTexture;
            public string Note;
        }

        // v3: the Avatar Compressor's decisions (source files, presets, format choice, size in the world)
        private const string TextureHashTag = "SmartVRAM_v3";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool protectBakery = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectBakery", true);
            bool protectMochie = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectMochie", true);
            bool verbose = SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false);

            SynTargetPlatform currentPlatform = SynAssetCache.GetCurrentTargetPlatform();
            bool isMobile = currentPlatform == SynTargetPlatform.Android || currentPlatform == SynTargetPlatform.iOS;
            var settings = SynTextureCompressorSettings.Load(isMobile);

            var processor = new TextureProcessor(settings.MinResolution, settings.MaxResolution, true, ResizeBackendPreference.Auto);
            var complexityCalc = new ComplexityCalculator(settings.HighComplexityThreshold, settings.LowComplexityThreshold, settings.MinDivisor, settings.MaxDivisor);
            var formatSelector = new TextureFormatSelector(CompressionPlatform.Auto, settings.UseHighQualityFormatForHighComplexity, settings.HighComplexityThreshold, settings.RespectImportFormat);
            var formatOnlySelector = new TextureFormatSelector(CompressionPlatform.Auto, settings.UseHighQualityFormatForHighComplexity, settings.HighComplexityThreshold, false);

            // Step 1: Collect all distinct scene materials
            var sceneMaterials = new HashSet<Material>();
            foreach (var r in renderers)
            {
                if (r == null || IsEditorOnly(r.transform) || SynSceneQuery.IsVideoComponentDetected(r)) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                foreach (var m in mats)
                {
                    if (m != null && !SynProtectionData.IsProtected(m))
                    {
                        sceneMaterials.Add(m);
                    }
                }
            }

            // Step 2: Collect candidate textures with every slot that uses them
            string patterns = SynSceneOptimizerSettings.GetString("SmartVRAM_ProtectedPatterns", "");
            string[] patternTokens = string.IsNullOrEmpty(patterns)
                ? new string[0]
                : patterns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            var candidateMap = new Dictionary<Texture2D, TextureCandidate>();
            var candidateList = new List<TextureCandidate>();
            var rejected = new HashSet<Texture2D>();

            foreach (var mat in sceneMaterials)
            {
                if (mat == null || mat.shader == null) continue;

                int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;

                    string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                    if (!(mat.GetTexture(propName) is Texture2D tex2D) || rejected.Contains(tex2D)) continue;

                    var kind = SynTextureSlotKinds.GetKind(propName, tex2D);
                    bool usedAsTransparency = AlphaCoverage.IsCoverage(mat, propName);

                    if (candidateMap.TryGetValue(tex2D, out var existing))
                    {
                        // A texture used as a normal map anywhere is handled as one (as the Avatar Compressor does)
                        existing.AlphaIsTransparency |= usedAsTransparency;
                        existing.IsNormalMap |= kind == SynTextureSlotKinds.Kind.Normal;
                        existing.IsEmission |= kind == SynTextureSlotKinds.Kind.Emission;
                        existing.KnownSlot |= SynTextureSlotKinds.IsKnownSlot(propName, tex2D);
                        existing.Uses.Add((mat, propName));
                        continue;
                    }

                    string path = AssetDatabase.GetAssetPath(tex2D);
                    bool skip = string.IsNullOrEmpty(path)
                        || (protectBakery && SynProtectionData.IsBakeryAsset(path, tex2D))
                        || (protectMochie && SynProtectionData.IsMochieAsset(path, tex2D))
                        || SynProtectionData.IsProtected(tex2D);
                    if (!skip && patternTokens.Length > 0)
                    {
                        string lowerName = tex2D.name.ToLowerInvariant();
                        foreach (var pat in patternTokens)
                        {
                            if (lowerName.Contains(pat.Trim().ToLowerInvariant()))
                            {
                                skip = true;
                                break;
                            }
                        }
                    }

                    // The Avatar Compressor's size filters (Min Source Size / Skip If Smaller Than), on the size it
                    // scales from
                    if (!skip)
                    {
                        Vector2Int filterSize = settings.RespectImportMaxSize
                            ? new Vector2Int(tex2D.width, tex2D.height)
                            : TextureSourceCache.GetPlannedBaseSize(tex2D, false, true, settings.MaxResolution);
                        int maxDim = Mathf.Max(filterSize.x, filterSize.y);
                        skip = maxDim < settings.MinSourceSize || maxDim <= settings.SkipIfSmallerThan;
                    }

                    if (skip)
                    {
                        rejected.Add(tex2D);
                        continue;
                    }

                    var candidate = new TextureCandidate
                    {
                        SourceTexture = tex2D,
                        AssetPath = path,
                        IsNormalMap = kind == SynTextureSlotKinds.Kind.Normal,
                        IsEmission = kind == SynTextureSlotKinds.Kind.Emission,
                        AlphaIsTransparency = usedAsTransparency,
                        KnownSlot = SynTextureSlotKinds.IsKnownSlot(propName, tex2D)
                    };
                    candidate.Uses.Add((mat, propName));
                    candidateMap[tex2D] = candidate;
                    candidateList.Add(candidate);
                }
            }

            // Uncompressed textures only in unrecognized slots may hold data (lookup tables, masks)
            if (settings.SkipUnknownUncompressedTextures)
            {
                candidateList.RemoveAll(c =>
                {
                    bool unknownData = !c.KnownSlot && !TextureFormatInfo.IsCompressed(c.SourceTexture.format);
                    if (unknownData) candidateMap.Remove(c.SourceTexture);
                    return unknownData;
                });
            }

            // Step 3: A replacement only applies to redirected renderers. If anything else in the scene (protected/video
            // renderers, particles, UI, sprites, Udon, skybox, animated material swaps) still uses the original, both
            // versions would ship, so such textures are left alone.
            var pinnedTextures = CollectTexturesUsedOutsideRedirectedRenderers(scene, renderers);
            int pinnedCount = candidateList.RemoveAll(c => pinnedTextures.Contains(c.SourceTexture));
            foreach (var tex in pinnedTextures) candidateMap.Remove(tex);
            if (pinnedCount > 0 && verbose)
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Texture Complexity Compressor: skipped {pinnedCount} textures that are also used outside optimized renderers (changing them would ship both versions).");
            }

            if (candidateList.Count == 0) return;

            // Step 4: Complexity scores. Cached per texture content and analysis weights in Library/; the rest are
            // scored in small batches (on the GPU when available).
            var scoreCache = LoadScoreCache();
            string weights = $"{settings.FastWeight:F2}_{settings.HighAccuracyWeight:F2}_{settings.PerceptualWeight:F2}";
            var toAnalyze = new List<TextureCandidate>();
            foreach (var candidate in candidateList)
            {
                if (scoreCache.TryGetValue(GetScoreKey(candidate, weights), out float cachedScore)) candidate.ComplexityScore = cachedScore;
                else toAnalyze.Add(candidate);
            }

            if (toAnalyze.Count > 0)
            {
                var analyzer = new TextureAnalyzer(AnalysisStrategyType.Combined, settings.FastWeight, settings.HighAccuracyWeight, settings.PerceptualWeight, processor);

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
                            scoreCache[GetScoreKey(candidate, weights)] = candidate.ComplexityScore;
                        }
                        else
                        {
                            candidate.ComplexityScore = AnalysisConstants.DefaultComplexityScore;
                        }
                    }
                }
                SaveScoreCache(scoreCache);
            }

            // Step 5: Size and format for each texture, made from the original image file
            var sourceCache = new TextureSourceCache(SynAssetCache.BaseCachePath + "_SourceImport");
            using (SynAssetDatabaseScope.Suspend())
            {
                // Imports the source files, which can't happen inside the pipeline's StartAssetEditing batch
                var sources = new List<Texture2D>();
                foreach (var c in candidateList) sources.Add(c.SourceTexture);
                sourceCache.Prepare(sources);
            }

            Dictionary<Texture2D, SynTexelDensityInfo> densities = null;
            if (settings.LimitByWorldSize)
            {
                var uses = new Dictionary<Texture2D, List<(Material, string)>>();
                foreach (var c in candidateList) uses[c.SourceTexture] = c.Uses;
                densities = SynWorldTexelDensity.Analyze(renderers, uses, settings.ViewDistanceCm);
            }

            int downscaledCount = 0;
            long estimatedBytesSaved = 0;
            var normalPreprocessor = new NormalMapPreprocessor();
            foreach (var candidate in candidateList)
            {
                try
                {
                    candidate.OptimizedTexture = Optimize(candidate, settings, processor, complexityCalc, formatSelector, formatOnlySelector, sourceCache, densities, normalPreprocessor, currentPlatform) ?? candidate.SourceTexture;
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

            // Step 6: Virtual Staging of scene materials with optimized textures in-place
            int materialsReLinked = 0;
            foreach (var mat in sceneMaterials)
            {
                if (mat == null || mat.shader == null || SynProtectionData.IsProtected(mat)) continue;

                int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                bool matHasOptimizedTex = false;

                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                    Texture tex = mat.GetTexture(propName);
                    if (tex is Texture2D tex2D && candidateMap.TryGetValue(tex2D, out var candidate))
                    {
                        if (candidate.OptimizedTexture != null && candidate.OptimizedTexture != tex2D)
                        {
                            matHasOptimizedTex = true;
                            break;
                        }
                    }
                }

                if (matHasOptimizedTex)
                {
                    SynVirtualMaterialState matState = SynPipelineCompactor.GetStagingState(mat);
                    if (matState != null)
                    {
                        for (int i = 0; i < propCount; i++)
                        {
                            if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                            string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                            Texture tex = mat.GetTexture(propName);
                            if (tex is Texture2D tex2D && candidateMap.TryGetValue(tex2D, out var candidate))
                            {
                                if (candidate.OptimizedTexture != null && candidate.OptimizedTexture != tex2D)
                                {
                                    matState.TrackedTextures[propName] = candidate.OptimizedTexture;
                                    matState.IsDirty = true;
                                }
                            }
                        }

                        if (!matState.AppliedPassTags.Contains("SmartVRAM"))
                        {
                            matState.AppliedPassTags.Add("SmartVRAM");
                        }
                        materialsReLinked++;
                    }
                }
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
                    sb.AppendLine($" • '{c.SourceTexture.name}' (detail {c.ComplexityScore:P0}{(c.IsNormalMap ? ", normal map" : "")}{(c.IsEmission ? ", emission" : "")}): {result} | {c.Note}");
                }
                Debug.Log(sb.ToString());
            }
        }

        /// <summary>
        /// The Avatar Compressor's per-texture decision and compression: size from the detail score (and from how large
        /// the texture appears in the world), format from detail and alpha with the banding check, normal maps through
        /// the layout conversion, all made from the original image file when there is one. Returns the original when
        /// nothing would change.
        /// </summary>
        private static Texture2D Optimize(
            TextureCandidate c,
            SynTextureCompressorSettings s,
            TextureProcessor processor,
            ComplexityCalculator complexityCalc,
            TextureFormatSelector formatSelector,
            TextureFormatSelector formatOnlySelector,
            TextureSourceCache sourceCache,
            Dictionary<Texture2D, SynTexelDensityInfo> densities,
            NormalMapPreprocessor normalPreprocessor,
            SynTargetPlatform platform)
        {
            Texture2D src = c.SourceTexture;
            var imported = new Vector2Int(src.width, src.height);
            Vector2Int baseSize = imported;
            if (!s.RespectImportMaxSize && sourceCache.TryGetCachedSize(src, out Vector2Int sourceSize))
            {
                baseSize = TextureSourceCache.FitWithin(sourceSize, s.MaxResolution);
            }

            TextureAnalysisResult analysis = AnalysisResultHelper.BuildResult(c.ComplexityScore, baseSize.x, baseSize.y, c.IsEmission, c.IsNormalMap, complexityCalc, processor);
            SynTexelDensityInfo? density = !c.FormatOnly && densities != null && densities.TryGetValue(src, out var measured) ? measured : (SynTexelDensityInfo?)null;
            if (density.HasValue)
            {
                analysis = SynWorldTexelDensity.ApplyLimit(analysis, baseSize, density.Value, processor);
                if (s.KeepVisibleDetail)
                {
                    analysis = SynWorldTexelDensity.ApplyFloor(analysis, baseSize, density.Value, s.MinDivisor, s.LowComplexityThreshold, processor);
                }
            }

            Vector2Int target = processor.CalculateTargetSize(baseSize, analysis);
            if (c.FormatOnly)
            {
                target = new Vector2Int(TextureProcessor.EnsureMultipleOf4(imported.x), TextureProcessor.EnsureMultipleOf4(imported.y));
            }
            else if (!s.RespectImportMaxSize)
            {
                target = SynWorldTexelDensity.GateUpscale(target, imported, analysis.NormalizedComplexity, s.LowComplexityThreshold, density);
            }
            c.Note = density.HasValue ? $"{density.Value.DensityAt(target):F1} px/cm in the world (needs {density.Value.TargetDensity:F1})" : "size in the world not measured";

            TextureFormatSelector selector = c.FormatOnly ? formatOnlySelector : formatSelector;
            TextureFormat sourceFormat = src.format;

            // Unity's import is already one compression of the source file; with the same size and format, keep it.
            // Normal maps always go through the layout conversion.
            if (!c.IsNormalMap && target == imported && selector.KeepsImportFormat(sourceFormat, null)) return src;

            string hash = SynAssetCache.ComputeTextureHash(src, TextureHashTag, target.x, target.y,
                $"{platform}_{s.Signature()}_{c.ComplexityScore:F3}_{(c.IsNormalMap ? 1 : 0)}{(c.IsEmission ? 1 : 0)}{(c.AlphaIsTransparency ? 1 : 0)}{(c.FormatOnly ? 1 : 0)}");
            if (SynAssetCache.TryGetCachedAsset<Texture2D>(SynAssetCache.TexturesCategory, hash, out Texture2D cached)) return cached;

            // Resize from the full-quality source when available; the normal-map layout is read from the same pixels
            Texture2D source = sourceCache.Load(src);
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
                resized = processor.ResizeSingle(fromSource ? source : src, target, c.IsNormalMap);
            }
            finally
            {
                if (fromSource) UnityEngine.Object.DestroyImmediate(source);
            }
            if (resized == null) return src;

            // Alpha is checked on the resized pixels: alpha that only existed in fine detail may be gone
            bool hasAlpha = TextureFormatSelector.HasSignificantAlpha(resized);
            TextureFormat format = selector.ResolveTargetFormat(sourceFormat, c.IsNormalMap, analysis.NormalizedComplexity, hasAlpha, null);
            if (!c.IsNormalMap && s.AvoidCompressionArtifacts && selector.ChoosesFormat(sourceFormat, null))
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

        // Identity hash covers the texture content and import settings; flags, weights and algorithm version cover the
        // analysis. v4: weights from the preset
        private static string GetScoreKey(TextureCandidate c, string weights)
        {
            return $"v4|{SynAssetCache.GetAssetIdentityHash(c.SourceTexture)}|n{(c.IsNormalMap ? 1 : 0)}|a{(c.AlphaIsTransparency ? 1 : 0)}|w{weights}";
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
