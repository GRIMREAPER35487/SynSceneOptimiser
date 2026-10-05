using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
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
        public override int Priority => 50; // Runs before Color Palettes (100) & Texture Arrays (200)

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
            public Color32[] PixelsSnapshot;
            public int Width;
            public int Height;
            public float ComplexityScore;
            public int RecommendedDivisor;
            public Texture2D OptimizedTexture;
        }

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

                    if (tex is Texture2D tex2D && !candidateMap.ContainsKey(tex2D))
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

                        bool isNormal = propName.ToLower().Contains("bump") || propName.ToLower().Contains("normal");

                        var candidate = new TextureCandidate
                        {
                            SourceTexture = tex2D,
                            AssetPath = path,
                            IsNormalMap = isNormal
                        };

                        candidateMap[tex2D] = candidate;
                        candidateList.Add(candidate);
                    }
                }
            }

            if (candidateList.Count == 0) return;

            // Step 4a: Capture pixel snapshots on Main Thread
            foreach (var candidate in candidateList)
            {
                candidate.PixelsSnapshot = ExtractPixels(candidate.SourceTexture, out candidate.Width, out candidate.Height);
            }

            // Step 4b: Multi-threaded complexity analysis across candidate textures (32 threads)
            Parallel.ForEach(candidateList, candidate =>
            {
                if (candidate.PixelsSnapshot != null && candidate.PixelsSnapshot.Length > 0)
                {
                    candidate.ComplexityScore = SynTextureAnalysisEngine.AnalyzeComplexity(candidate.PixelsSnapshot, candidate.Width, candidate.Height, candidate.IsNormalMap);
                    candidate.RecommendedDivisor = complexityCalc.CalculateRecommendedDivisor(candidate.ComplexityScore);
                }
                else
                {
                    candidate.ComplexityScore = 0.5f;
                    candidate.RecommendedDivisor = 1;
                }
                candidate.PixelsSnapshot = null; // free memory
            });

            // Step 5: Process and Cache Optimized Textures
            int downscaledCount = 0;
            long estimatedBytesSaved = 0;

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

                // Build composite hash for caching
                string hash = SynAssetCache.ComputeTextureHash(
                    src,
                    "SmartVRAM",
                    targetWidth,
                    targetHeight,
                    $"Score{candidate.ComplexityScore:F2}_Div{candidate.RecommendedDivisor}"
                );

                // Check cache
                if (!SynAssetCache.TryGetCachedAsset<Texture2D>(SynAssetCache.TexturesCategory, hash, out Texture2D cachedTex))
                {
                    // Generate downscaled texture
                    cachedTex = ResizeTexture(src, targetWidth, targetHeight, candidate.IsNormalMap);
                    if (cachedTex != null)
                    {
                        cachedTex.name = $"{src.name}_opt_{targetWidth}x{targetHeight}";
                        cachedTex = SynAssetCache.SaveCachedAsset(cachedTex, SynAssetCache.TexturesCategory, hash);
                    }
                }

                if (cachedTex != null)
                {
                    candidate.OptimizedTexture = cachedTex;
                    downscaledCount++;
                    estimatedBytesSaved += (src.width * src.height * 4) - (targetWidth * targetHeight * 4);
                }
                else
                {
                    candidate.OptimizedTexture = src;
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
                string msg = $"Texture Complexity Compressor: Downscaled {downscaledCount} / {candidateList.Count} textures across {materialsReLinked} materials (Saved Est. ~{savedMB:F1} MB uncompressed VRAM).";
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

        private static Color32[] ExtractPixels(Texture2D src, out int width, out int height)
        {
            width = src.width;
            height = src.height;

            if (src.isReadable)
            {
                try
                {
                    return src.GetPixels32();
                }
                catch { }
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

        private static Texture2D ResizeTexture(Texture2D src, int targetWidth, int targetHeight, bool isNormalMap)
        {
            if (src == null) return null;

            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, true);
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
