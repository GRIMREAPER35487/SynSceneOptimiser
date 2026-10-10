using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor;
using Synthos.SynSceneOptimizer.TextureCompressor;

namespace Synthos.SynSceneOptimizer
{
    public class SynSmartTextureVRAMOptimizerPass : SynOptimizationPass
    {
        public override string Id => "SynSmartTextureVRAMPass";
        public override string Name => "Texture Complexity Compressor (Derived from Avatar Compressor)";
        public override string Description => "Intelligently analyzes scene texture frequency, complexity, and normal layouts to dynamically downscale flat/simple textures, prune unused material slots, and select optimal per-platform compression formats.";
        public override string Category => "Memory & Assets";
        public override int Priority => 45; // Runs before Color Palettes (50) so it never downscales generated palette textures

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Analyzes scene textures via Sobel gradient & spatial frequency to automatically downscale low-complexity textures (e.g. flat walls 2048 -> 512/256), saving 75-93% VRAM per texture. Integrates with deterministic per-platform cache.", MessageType.Info);

            bool protectBakery = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectBakery", true);
            bool newProtectBakery = EditorGUILayout.Toggle(new GUIContent("Protect Bakery & Lightmaps", "Guarantees that all Bakery lightmaps, directional maps, volumes, and shadowmasks are never downscaled."), protectBakery);
            if (newProtectBakery != protectBakery) SynSceneOptimizerSettings.SetBool("SmartVRAM_ProtectBakery", newProtectBakery);

            bool protectMochie = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectMochie", true);
            bool newProtectMochie = EditorGUILayout.Toggle(new GUIContent("Protect Mochie Textures", "Guarantees that all default Mochie shader lookup textures, normal arrays, and rain sheets inside Assets/Mochie/ are never touched."), protectMochie);
            if (newProtectMochie != protectMochie) SynSceneOptimizerSettings.SetBool("SmartVRAM_ProtectMochie", newProtectMochie);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("COMPLEXITY THRESHOLDS", EditorStyles.boldLabel);

            float highThresh = SynSceneOptimizerSettings.GetFloat("SmartVRAM_HighComplexityThreshold", 0.70f);
            float newHighThresh = EditorGUILayout.Slider(new GUIContent("High Detail Threshold", "Textures above this complexity score retain full resolution and receive BC7 / ASTC 4x4."), highThresh, 0.40f, 0.95f);
            if (Mathf.Abs(newHighThresh - highThresh) > 0.001f) SynSceneOptimizerSettings.SetFloat("SmartVRAM_HighComplexityThreshold", newHighThresh);

            float lowThresh = SynSceneOptimizerSettings.GetFloat("SmartVRAM_LowComplexityThreshold", 0.20f);
            float newLowThresh = EditorGUILayout.Slider(new GUIContent("Low Detail Threshold", "Textures below this complexity score are aggressive candidates for 4x/8x downscaling (e.g. 2048 -> 512/256)."), lowThresh, 0.05f, 0.45f);
            if (Mathf.Abs(newLowThresh - lowThresh) > 0.001f) SynSceneOptimizerSettings.SetFloat("SmartVRAM_LowComplexityThreshold", newLowThresh);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("RESOLUTION BOUNDS (MIN / MAX)", EditorStyles.boldLabel);

            int pcMax = SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxResPC", 2048);
            int newPCMax = EditorGUILayout.IntPopup("Max PC Resolution", pcMax, new string[] { "512", "1024", "2048", "4096" }, new int[] { 512, 1024, 2048, 4096 });
            if (newPCMax != pcMax) SynSceneOptimizerSettings.SetInt("SmartVRAM_MaxResPC", newPCMax);

            int pcMin = SynSceneOptimizerSettings.GetInt("SmartVRAM_MinResPC", 512);
            int newPCMin = EditorGUILayout.IntPopup("Min PC Resolution (Floor)", pcMin, new string[] { "64", "128", "256", "512", "1024" }, new int[] { 64, 128, 256, 512, 1024 });
            if (newPCMin != pcMin) SynSceneOptimizerSettings.SetInt("SmartVRAM_MinResPC", newPCMin);

            EditorGUILayout.Space(2);

            int androidMax = SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxResAndroid", 1024);
            int newAndroidMax = EditorGUILayout.IntPopup("Max Android Resolution", androidMax, new string[] { "512", "1024", "2048" }, new int[] { 512, 1024, 2048 });
            if (newAndroidMax != androidMax) SynSceneOptimizerSettings.SetInt("SmartVRAM_MaxResAndroid", newAndroidMax);

            int androidMin = SynSceneOptimizerSettings.GetInt("SmartVRAM_MinResAndroid", 256);
            int newAndroidMin = EditorGUILayout.IntPopup("Min Android Resolution (Floor)", androidMin, new string[] { "64", "128", "256", "512" }, new int[] { 64, 128, 256, 512 });
            if (newAndroidMin != androidMin) SynSceneOptimizerSettings.SetInt("SmartVRAM_MinResAndroid", newAndroidMin);

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
            public bool AlphaIsTransparency;
            public Color32[] PixelsSnapshot;
            public int Width;
            public int Height;
            public float ComplexityScore;
            public int RecommendedDivisor;
            public Texture2D OptimizedTexture;
        }

        private class PendingImporterCopy
        {
            public TextureCandidate Candidate;
            public string CopyPath;
            public int MaxSize;
        }

        // Bumped from "SmartVRAM" so legacy uncompressed RGBA32 cache entries are never reused
        private const string TextureHashTag = "SmartVRAM_v2";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool protectBakery = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectBakery", true);
            bool protectMochie = SynSceneOptimizerSettings.GetBool("SmartVRAM_ProtectMochie", true);
            bool pruneUnused = SynSceneOptimizerSettings.GetBool("SmartVRAM_PruneUnusedSlots", true);
            float highThresh = SynSceneOptimizerSettings.GetFloat("SmartVRAM_HighComplexityThreshold", 0.70f);
            float lowThresh = SynSceneOptimizerSettings.GetFloat("SmartVRAM_LowComplexityThreshold", 0.20f);

            SynTargetPlatform currentPlatform = SynAssetCache.GetCurrentTargetPlatform();
            int maxAllowedRes = (currentPlatform == SynTargetPlatform.Android || currentPlatform == SynTargetPlatform.iOS)
                ? SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxResAndroid", 1024)
                : SynSceneOptimizerSettings.GetInt("SmartVRAM_MaxResPC", 2048);

            int minAllowedRes = (currentPlatform == SynTargetPlatform.Android || currentPlatform == SynTargetPlatform.iOS)
                ? SynSceneOptimizerSettings.GetInt("SmartVRAM_MinResAndroid", 256)
                : SynSceneOptimizerSettings.GetInt("SmartVRAM_MinResPC", 512);

            var complexityCalc = new SynComplexityCalculator(highThresh, lowThresh, 1, 8);
            var formatSelector = new SynTextureFormatSelector(true, highThresh);

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

            // Step 2: Collect candidate textures
            string patterns = SynSceneOptimizerSettings.GetString("SmartVRAM_ProtectedPatterns", "");
            string[] patternTokens = string.IsNullOrEmpty(patterns)
                ? new string[0]
                : patterns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            var candidateMap = new Dictionary<Texture2D, TextureCandidate>();
            var candidateList = new List<TextureCandidate>();

            foreach (var mat in sceneMaterials)
            {
                if (mat == null || mat.shader == null) continue;

                int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;

                    string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                    Texture tex = mat.GetTexture(propName);
                    bool usedAsTransparency = IsMainTextureProperty(propName) && IsCutoutOrTransparent(mat);

                    if (tex is Texture2D existingTex && candidateMap.TryGetValue(existingTex, out var existingCandidate))
                    {
                        existingCandidate.AlphaIsTransparency |= usedAsTransparency;
                        continue;
                    }

                    if (tex is Texture2D tex2D)
                    {
                        string path = AssetDatabase.GetAssetPath(tex2D);
                        if (string.IsNullOrEmpty(path)) continue;

                        if (protectBakery && SynProtectionData.IsBakeryAsset(path, tex2D))
                            continue;

                        if (protectMochie && SynProtectionData.IsMochieAsset(path, tex2D))
                            continue;

                        if (SynProtectionData.IsProtected(tex2D))
                            continue;

                        if (patternTokens.Length > 0)
                        {
                            string lowerName = tex2D.name.ToLowerInvariant();
                            bool matchesPattern = false;
                            foreach (var pat in patternTokens)
                            {
                                if (lowerName.Contains(pat.Trim().ToLowerInvariant()))
                                {
                                    matchesPattern = true;
                                    break;
                                }
                            }
                            if (matchesPattern) continue;
                        }

                        // Skip small textures (< 128)
                        if (tex2D.width < 128 && tex2D.height < 128)
                            continue;

                        bool isNormal = (AssetImporter.GetAtPath(path) is TextureImporter ti && ti.textureType == TextureImporterType.NormalMap)
                            || propName.ToLower().Contains("bump") || propName.ToLower().Contains("normal");

                        var candidate = new TextureCandidate
                        {
                            SourceTexture = tex2D,
                            AssetPath = path,
                            IsNormalMap = isNormal,
                            AlphaIsTransparency = usedAsTransparency
                        };

                        candidateMap[tex2D] = candidate;
                        candidateList.Add(candidate);
                    }
                }
            }

            // Step 3: A downscaled copy only replaces the original on redirected renderers. If anything else in the
            // scene (protected/video renderers, particles, UI, sprites, Udon, skybox, animated material swaps)
            // still uses the original, both versions would ship, so such textures are left alone.
            var pinnedTextures = CollectTexturesUsedOutsideRedirectedRenderers(scene, renderers);
            int pinnedCount = candidateList.RemoveAll(c => pinnedTextures.Contains(c.SourceTexture));
            foreach (var tex in pinnedTextures) candidateMap.Remove(tex);
            if (pinnedCount > 0 && SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false))
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Texture Complexity Compressor: skipped {pinnedCount} textures that are also used outside optimized renderers (downscaling them would ship both versions).");
            }

            if (candidateList.Count == 0) return;

            // Step 4: Complexity scores. Cached per texture content in Library/ so unchanged textures are never
            // read back again; the rest are analyzed in small batches from a <=1024px readback.
            var scoreCache = LoadScoreCache();
            var toAnalyze = new List<TextureCandidate>();
            foreach (var candidate in candidateList)
            {
                if (scoreCache.TryGetValue(GetScoreKey(candidate), out float cachedScore))
                {
                    candidate.ComplexityScore = cachedScore;
                    candidate.RecommendedDivisor = complexityCalc.CalculateRecommendedDivisor(cachedScore);
                }
                else
                {
                    toAnalyze.Add(candidate);
                }
            }

            const int batchSize = 16;
            for (int start = 0; start < toAnalyze.Count; start += batchSize)
            {
                var batch = toAnalyze.GetRange(start, Math.Min(batchSize, toAnalyze.Count - start));
                foreach (var candidate in batch)
                {
                    candidate.PixelsSnapshot = ExtractPixels(candidate.SourceTexture, AnalysisReadbackSize, out candidate.Width, out candidate.Height);
                }

                Parallel.ForEach(batch, candidate =>
                {
                    if (candidate.PixelsSnapshot != null && candidate.PixelsSnapshot.Length > 0)
                    {
                        candidate.ComplexityScore = SynTextureAnalysisEngine.AnalyzeComplexity(candidate.PixelsSnapshot, candidate.Width, candidate.Height, candidate.IsNormalMap, candidate.AlphaIsTransparency);
                        candidate.RecommendedDivisor = complexityCalc.CalculateRecommendedDivisor(candidate.ComplexityScore);
                    }
                    else
                    {
                        candidate.ComplexityScore = 0.5f;
                        candidate.RecommendedDivisor = 1;
                    }
                    candidate.PixelsSnapshot = null; // free memory
                });

                foreach (var candidate in batch)
                {
                    scoreCache[GetScoreKey(candidate)] = candidate.ComplexityScore;
                }
            }
            if (toAnalyze.Count > 0) SaveScoreCache(scoreCache);

            // Step 5: Process and Cache Optimized Textures
            int downscaledCount = 0;
            long estimatedBytesSaved = 0;
            var pendingCopies = new List<PendingImporterCopy>();

            foreach (var candidate in candidateList)
            {
                Texture2D src = candidate.SourceTexture;
                int targetWidth = src.width / candidate.RecommendedDivisor;
                int targetHeight = src.height / candidate.RecommendedDivisor;

                // Enforce Min Resolution Floor (never downscale below floor unless original texture was already smaller)
                int minW = Mathf.Min(src.width, minAllowedRes);
                int minH = Mathf.Min(src.height, minAllowedRes);
                targetWidth = Mathf.Max(minW, targetWidth);
                targetHeight = Mathf.Max(minH, targetHeight);

                // Apply max resolution ceiling
                if (targetWidth > maxAllowedRes)
                {
                    targetHeight = Mathf.Max(minH, (targetHeight * maxAllowedRes) / targetWidth);
                    targetWidth = maxAllowedRes;
                }
                if (targetHeight > maxAllowedRes)
                {
                    targetWidth = Mathf.Max(minW, (targetWidth * maxAllowedRes) / targetHeight);
                    targetHeight = maxAllowedRes;
                }

                // If dimensions haven't changed, no downscaling needed
                if (targetWidth == src.width && targetHeight == src.height)
                {
                    candidate.OptimizedTexture = src;
                    continue;
                }

                // Importer-backed textures are duplicated into the cache and re-imported by Unity at a lower
                // max size, so compression, color space, normal encoding, mips and sampler state all follow
                // the original's import settings. The source asset is never modified.
                if (AssetImporter.GetAtPath(candidate.AssetPath) is TextureImporter)
                {
                    int maxSize = FloorPowerOfTwo(Mathf.Max(targetWidth, targetHeight));
                    if (maxSize >= Mathf.Max(src.width, src.height))
                    {
                        candidate.OptimizedTexture = src;
                        continue;
                    }

                    string hash = SynAssetCache.ComputeTextureHash(src, TextureHashTag, maxSize, maxSize, "ImporterCopy");
                    if (SynAssetCache.TryGetCachedAsset<Texture2D>(SynAssetCache.TexturesCategory, hash, out Texture2D cachedCopy))
                    {
                        candidate.OptimizedTexture = cachedCopy;
                        continue;
                    }

                    string copyPath = SynAssetCache.GetAssetPath(
                        SynAssetCache.TexturesCategory, hash, $"{src.name}_opt_{maxSize}", Path.GetExtension(candidate.AssetPath));
                    pendingCopies.Add(new PendingImporterCopy { Candidate = candidate, CopyPath = copyPath, MaxSize = maxSize });
                }
                else
                {
                    candidate.OptimizedTexture = CreateCompressedCopy(candidate, targetWidth, targetHeight, formatSelector, currentPlatform) ?? src;
                }
            }

            if (pendingCopies.Count > 0)
            {
                ImportDownscaledCopies(pendingCopies);
            }

            foreach (var candidate in candidateList)
            {
                if (candidate.OptimizedTexture != null && candidate.OptimizedTexture != candidate.SourceTexture)
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

            bool verbose = SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false);

            if (downscaledCount > 0)
            {
                double savedMB = estimatedBytesSaved / (1024.0 * 1024.0);
                string msg = $"Texture Complexity Compressor: Downscaled {downscaledCount} / {candidateList.Count} textures across {materialsReLinked} materials (Saved Est. ~{savedMB:F1} MB VRAM).";
                Debug.Log($"[SYN SCENE OPTIMIZER] {msg}");
                SynPipelineCompactor.LogChange(Name, msg);

                if (verbose)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("[SYN SCENE OPTIMIZER] Detailed Texture Complexity Breakdown:");
                    int logged = 0;
                    foreach (var c in candidateList)
                    {
                        if (c.OptimizedTexture != null && c.OptimizedTexture != c.SourceTexture)
                        {
                            sb.AppendLine($" • '{c.SourceTexture.name}' (Score: {c.ComplexityScore:F2}) -> Downscaled from {c.SourceTexture.width}x{c.SourceTexture.height} to {c.OptimizedTexture.width}x{c.OptimizedTexture.height}");
                            logged++;
                            if (logged >= 30) break;
                        }
                    }
                    Debug.Log(sb.ToString());
                }
            }
            else
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Texture Complexity Compressor: Analyzed {candidateList.Count} textures. All textures are optimal or already at appropriate resolution.");
            }
        }

        // The complexity metrics sample at most ~512 px across, so a 1024 px readback keeps the same detail while
        // using 1/16 of the memory of a 4K readback
        private const int AnalysisReadbackSize = 1024;

        private static Color32[] ExtractPixels(Texture2D src, int maxSize, out int width, out int height)
        {
            width = src.width;
            height = src.height;
            int largest = Mathf.Max(width, height);
            if (largest > maxSize)
            {
                width = Mathf.Max(1, width * maxSize / largest);
                height = Mathf.Max(1, height * maxSize / largest);
            }

            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D snapshot = new Texture2D(width, height, TextureFormat.RGBA32, false);
            snapshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            snapshot.Apply(false);

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            Color32[] pixels = snapshot.GetPixels32();
            UnityEngine.Object.DestroyImmediate(snapshot);
            return pixels;
        }

        /// <summary>
        /// Copies each source texture (with its import settings) into the cache, lowers the copy's max size
        /// and lets Unity re-import it. Runs outside the pipeline's StartAssetEditing batch so the copies
        /// are imported and loadable before materials are re-linked.
        /// </summary>
        private static void ImportDownscaledCopies(List<PendingImporterCopy> pendingCopies)
        {
            using (SynAssetDatabaseScope.Suspend())
            {
                string folder = SynAssetCache.GetCategoryPath(SynAssetCache.TexturesCategory).TrimEnd('/');
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    // Cache folders are created on disk directly; make sure the AssetDatabase knows about them
                    AssetDatabase.Refresh();
                }

                var copied = new List<PendingImporterCopy>();
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var p in pendingCopies)
                    {
                        if (File.Exists(p.CopyPath) || AssetDatabase.CopyAsset(p.Candidate.AssetPath, p.CopyPath))
                        {
                            copied.Add(p);
                        }
                        else
                        {
                            Debug.LogWarning($"[SYN SCENE OPTIMIZER] Texture Complexity Compressor: Failed to copy '{p.Candidate.AssetPath}' into the cache. Keeping original.");
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                string importerPlatform = GetImporterPlatformName(SynAssetCache.GetCurrentTargetPlatform());
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var p in copied)
                    {
                        if (!(AssetImporter.GetAtPath(p.CopyPath) is TextureImporter importer)) continue;

                        importer.maxTextureSize = Mathf.Min(importer.maxTextureSize, p.MaxSize);
                        TextureImporterPlatformSettings platformSettings = importer.GetPlatformTextureSettings(importerPlatform);
                        if (platformSettings.overridden)
                        {
                            platformSettings.maxTextureSize = Mathf.Min(platformSettings.maxTextureSize, p.MaxSize);
                            importer.SetPlatformTextureSettings(platformSettings);
                        }
                        importer.SaveAndReimport();
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                foreach (var p in pendingCopies)
                {
                    Texture2D copy = AssetDatabase.LoadAssetAtPath<Texture2D>(p.CopyPath);
                    p.Candidate.OptimizedTexture = copy != null ? copy : p.Candidate.SourceTexture;
                    if (copy != null) SynAssetCache.RecordUsage(p.CopyPath);
                }
            }
        }

        /// <summary>
        /// Fallback for textures without a TextureImporter (e.g. generated .asset textures):
        /// GPU resize, then compress to the platform format chosen by SynTextureFormatSelector.
        /// </summary>
        private static Texture2D CreateCompressedCopy(TextureCandidate candidate, int targetWidth, int targetHeight, SynTextureFormatSelector formatSelector, SynTargetPlatform platform)
        {
            Texture2D src = candidate.SourceTexture;
            bool hasAlpha = GraphicsFormatUtility.HasAlphaChannel(src.graphicsFormat);
            TextureFormat format = formatSelector.SelectFormat(candidate.IsNormalMap, candidate.ComplexityScore, hasAlpha, platform);

            // BC/DXT formats require dimensions that are multiples of 4; skip rather than ship uncompressed
            bool isMobile = platform == SynTargetPlatform.Android || platform == SynTargetPlatform.iOS;
            if (!isMobile && (targetWidth % 4 != 0 || targetHeight % 4 != 0)) return null;

            string hash = SynAssetCache.ComputeTextureHash(src, TextureHashTag, targetWidth, targetHeight, $"Fallback_{format}");
            if (SynAssetCache.TryGetCachedAsset<Texture2D>(SynAssetCache.TexturesCategory, hash, out Texture2D cachedTex))
            {
                return cachedTex;
            }

            Texture2D result = ResizeTexture(src, targetWidth, targetHeight, candidate.IsNormalMap);
            if (result == null) return null;

            EditorUtility.CompressTexture(result, format, TextureCompressionQuality.Best);
            result.wrapModeU = src.wrapModeU;
            result.wrapModeV = src.wrapModeV;
            result.filterMode = src.filterMode;
            result.anisoLevel = src.anisoLevel;
            result.name = $"{src.name}_opt_{targetWidth}x{targetHeight}";
            return SynAssetCache.SaveCachedAsset(result, SynAssetCache.TexturesCategory, hash);
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

        private static int FloorPowerOfTwo(int value)
        {
            int result = 32;
            while (result * 2 <= value && result < 16384) result *= 2;
            return result;
        }

        private static string GetImporterPlatformName(SynTargetPlatform platform)
        {
            switch (platform)
            {
                case SynTargetPlatform.Android: return "Android";
                case SynTargetPlatform.iOS: return "iPhone";
                default: return "Standalone";
            }
        }

        private static Texture2D ResizeTexture(Texture2D src, int targetWidth, int targetHeight, bool isNormalMap)
        {
            if (src == null) return null;

            // Normal maps hold linear data; an sRGB render target would gamma-encode the vectors
            RenderTextureReadWrite readWrite = isNormalMap ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default;
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32, readWrite);
            Graphics.Blit(src, rt);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, true, isNormalMap);
            result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            result.Apply(true);

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            if (isNormalMap)
            {
                Color32[] pixels = result.GetPixels32();
                SynNormalMapPreprocessor.ReNormalize(pixels);
                result.SetPixels32(pixels);
                result.Apply(true);
            }

            return result;
        }

        private const string ScoreCachePath = "Library/SynSceneOptimizer/TextureComplexityScores.txt";

        // Identity hash covers the texture content and import settings; flags and algorithm version cover the analysis
        private static string GetScoreKey(TextureCandidate c)
        {
            return $"v2|{SynAssetCache.GetAssetIdentityHash(c.SourceTexture)}|n{(c.IsNormalMap ? 1 : 0)}|a{(c.AlphaIsTransparency ? 1 : 0)}";
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
        private static HashSet<Texture2D> CollectTexturesUsedOutsideRedirectedRenderers(Scene scene, List<Renderer> renderers)
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

        private static bool IsMainTextureProperty(string propName)
        {
            return propName == "_MainTex" || propName == "_BaseMap" || propName == "_BaseColorMap";
        }

        // Alpha is coverage only for alpha-tested or blended materials (queue >= AlphaTest or blend keywords)
        private static bool IsCutoutOrTransparent(Material mat)
        {
            return mat.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.AlphaTest
                || mat.IsKeywordEnabled("_ALPHATEST_ON")
                || mat.IsKeywordEnabled("_ALPHABLEND_ON")
                || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON");
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
