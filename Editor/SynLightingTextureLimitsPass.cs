using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Caps the size of directional lightmaps and reflection probe cubemaps for the build. Both hold low-detail data:
    /// a directional lightmap only stores the dominant light direction, and probes feed blurry reflections. Import
    /// settings are changed for the build or Play Mode session only and reverted afterwards, like Mip Streaming
    /// (see <see cref="SynImporterRevertJournal"/>).
    /// </summary>
    public class SynLightingTextureLimitsPass : SynOptimizationPass
    {
        public override string Id => "SynLightingTextureLimitsPass";
        public override string Name => "Lighting Texture Size Limits";
        public override string Description => "Caps directional lightmaps and reflection probe cubemaps at a per-platform size for the build. The import settings are reverted afterwards.";
        public override string Category => "Memory & Assets";
        public override int Priority => 90; // Before Mip Streaming (95), which reimports the same lightmaps
        public override bool RunInPreview => false; // Temporarily edits project import settings

        private static readonly string[] DirLabels = { "No limit", "512", "1024", "2048", "4096" };
        private static readonly int[] DirSizes = { 0, 512, 1024, 2048, 4096 };
        private static readonly string[] ProbeLabels = { "No limit", "32", "64", "128", "256", "512" };
        private static readonly int[] ProbeSizes = { 0, 32, 64, 128, 256, 512 };

        private const string DirPcKey = "LightingLimits_DirectionalPC";
        private const string DirQuestKey = "LightingLimits_DirectionalQuest";
        private const string ProbePcKey = "LightingLimits_ProbePC";
        private const string ProbeQuestKey = "LightingLimits_ProbeQuest";

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Directional lightmaps only store which way the light comes from, and reflection probes give blurry reflections, so both can be much smaller than the color lightmap without visible loss. Sizes are lowered for the build only; your import settings are restored afterwards.", MessageType.Info);

            EditorGUILayout.LabelField("DIRECTIONAL LIGHTMAPS (MAX SIZE)", EditorStyles.boldLabel);
            DrawSizePopup("PC", DirPcKey, 2048, DirLabels, DirSizes);
            DrawSizePopup("Quest / Android", DirQuestKey, 1024, DirLabels, DirSizes);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("REFLECTION PROBES (MAX FACE SIZE)", EditorStyles.boldLabel);
            DrawSizePopup("PC", ProbePcKey, 128, ProbeLabels, ProbeSizes);
            DrawSizePopup("Quest / Android", ProbeQuestKey, 128, ProbeLabels, ProbeSizes);
        }

        private static void DrawSizePopup(string label, string key, int defaultValue, string[] labels, int[] sizes)
        {
            int value = SynSceneOptimizerSettings.GetInt(key, defaultValue);
            int newValue = EditorGUILayout.IntPopup(label, value, labels, sizes);
            if (newValue != value) SynSceneOptimizerSettings.SetInt(key, newValue);
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            SynTargetPlatform platform = SynAssetCache.GetCurrentTargetPlatform();
            bool mobile = platform == SynTargetPlatform.Android || platform == SynTargetPlatform.iOS;
            string importerPlatform = platform == SynTargetPlatform.Android ? "Android" : platform == SynTargetPlatform.iOS ? "iPhone" : "Standalone";
            int dirCap = SynSceneOptimizerSettings.GetInt(mobile ? DirQuestKey : DirPcKey, mobile ? 1024 : 2048);
            int probeCap = SynSceneOptimizerSettings.GetInt(mobile ? ProbeQuestKey : ProbePcKey, 128);

            var targets = new Dictionary<Texture, int>();
            if (dirCap > 0 && LightmapSettings.lightmaps != null)
            {
                foreach (LightmapData lm in LightmapSettings.lightmaps)
                {
                    if (lm != null && lm.lightmapDir != null) targets[lm.lightmapDir] = dirCap;
                }
            }
            if (probeCap > 0)
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (ReflectionProbe probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                    {
                        Texture tex = probe.mode == ReflectionProbeMode.Custom ? probe.customBakedTexture
                            : probe.mode == ReflectionProbeMode.Baked ? probe.bakedTexture : null;
                        if (tex != null) targets[tex] = probeCap;
                    }
                }
            }
            if (targets.Count == 0) return;

            var journal = SynImporterRevertJournal.Load();
            int dirCount = 0, probeCount = 0;
            long savedBytes = 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var kvp in targets)
                {
                    Texture tex = kvp.Key;
                    int cap = kvp.Value;
                    // Only the user's own protection list applies: the general texture check protects all lighting data
                    if (tex.width <= cap || (tex is Texture2D tex2D && SynProtectionData.IsProtected(tex2D))) continue;

                    string path = AssetDatabase.GetAssetPath(tex);
                    if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;

                    SynImporterRevertJournal.Entry entry = journal.TryGetValue(path, out var existing) ? existing : new SynImporterRevertJournal.Entry();
                    if (!SynImporterRevertJournal.LimitMaxSize(importer, importerPlatform, cap, entry)) continue;

                    // Journaled before the reimport, so a crash mid-build still gets reverted
                    journal[path] = entry;
                    SynImporterRevertJournal.Save(journal);
                    try
                    {
                        importer.SaveAndReimport();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not limit the size of '{path}': {e.Message}");
                        continue;
                    }

                    // Each halving of the size quarters the memory
                    double scale = (double)cap / tex.width;
                    savedBytes += (long)(EstimateBytes(tex) * (1.0 - scale * scale));
                    if (tex is Cubemap) probeCount++;
                    else dirCount++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (dirCount + probeCount > 0)
            {
                string msg = $"Lighting Texture Size Limits: capped {dirCount} directional lightmap(s) at {dirCap} and {probeCount} reflection probe(s) at {probeCap} (saved est. ~{savedBytes / (1024.0 * 1024.0):F1} MB VRAM). Reverted after the build.";
                Debug.Log($"[SYN SCENE OPTIMIZER] {msg}");
                SynPipelineCompactor.LogChange(Name, msg);
            }
        }

        internal static long EstimateBytes(Texture tex)
        {
            int faces = tex is Cubemap ? 6 : 1;
            int mips = tex is Texture2D t2d ? t2d.mipmapCount : tex is Cubemap cube ? cube.mipmapCount : 1;
            long total = 0;
            for (int mip = 0; mip < Mathf.Max(1, mips); mip++)
            {
                total += (long)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.ComputeMipmapSize(
                    Mathf.Max(1, tex.width >> mip), Mathf.Max(1, tex.height >> mip), tex.graphicsFormat) * faces;
            }
            return total;
        }
    }
}
