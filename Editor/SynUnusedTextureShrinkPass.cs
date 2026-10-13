using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Imports textures that nothing in the world can ever draw at 32x32 for the build. A material keeps a texture
    /// in a slot even when the feature that reads it is off (Mochie Standard puts an 8192x8192 rain sheet on every
    /// material), and Unity ships it. Clearing the slot needs a copy of the material, which protected objects, video
    /// players and materials scripts compare against can't use; shrinking the texture works wherever it is referenced.
    ///
    /// A texture qualifies only when everything the scene pulls into the build references it through material
    /// slots, every one of those materials' shader variants skips the slot (also with any keyword an Udon script can
    /// turn on or off), and no script reads the slot out of a material. The import setting is restored afterwards.
    /// </summary>
    public class SynUnusedTextureShrinkPass : SynOptimizationPass
    {
        public override string Id => "SynUnusedTextureShrinkPass";
        public override string Name => "Unused Texture Shrinker";
        public override string Description => "Imports textures that no material in the world can ever sample (e.g. a rain texture on materials with rain off) at 32x32 for the build. Import settings are restored afterwards.";
        public override string Category => "Memory & Assets";
        // Before the texture compressor (45), palettes (50) and mip streaming (95): they then see a 32x32 texture and
        // leave it alone instead of making copies of it
        public override int Priority => 24;
        public override SynProtectionTargets ProtectionTargets => SynProtectionTargets.Textures;
        public override string ProtectionEffect => "Imports the texture at 32x32 during the build, only when nothing can draw it.";
        public override bool RunInPreview => false; // Temporarily edits project import settings

        private const int ShrunkSize = 32;

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("A texture is shrunk only if every material that holds it skips it in its shader, even with any shader keyword an Udon script in the world can turn on, and nothing else (UI, sprites, scripts, animations) uses it. Works on protected objects too, since their materials are not changed.", MessageType.Info);
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            UnityEngine.Object[] dependencies = CollectSceneDependencies(scene);
            SynUdonUsageScan scan = SynUdonUsageScan.Run(dependencies);
            if (scan.KeywordsUnknown)
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Unused Texture Shrinker: skipped, a script could turn on any shader feature: {string.Join("; ", scan.UnknownReasons)}.");
                return;
            }

            // Every material slot holding each texture, and textures referenced any other way
            var slots = new Dictionary<Texture, List<(Material material, string property)>>();
            var otherUses = new HashSet<Texture>();
            foreach (UnityEngine.Object obj in dependencies)
            {
                switch (obj)
                {
                    case null:
                    case Texture _:
                    case Shader _:
                    case Mesh _:
                    case AudioClip _:
                    case MonoScript _:
                    case GameObject _:
                    case Transform _:
                        break;
                    case Material material:
                        CollectMaterialSlots(material, slots);
                        break;
                    default:
                        CollectTextureReferences(obj, otherUses);
                        break;
                }
            }

            SynTargetPlatform platform = SynAssetCache.GetCurrentTargetPlatform();
            string importerPlatform = platform == SynTargetPlatform.Android ? "Android" : platform == SynTargetPlatform.iOS ? "iPhone" : "Standalone";
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;

            var toShrink = new List<Texture>();
            foreach (var kvp in slots)
            {
                Texture tex = kvp.Key;
                if (tex.width <= ShrunkSize && tex.height <= ShrunkSize) continue;
                if (otherUses.Contains(tex)) continue;
                if (tex is Texture2D tex2D && SynProtectionData.IsProtected(tex2D)) continue;
                if (SynProtectionData.IsBakeryAsset(tex)) continue;
                if (!(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) is TextureImporter)) continue;
                if (CanBeDrawn(kvp.Value, scan, target)) continue;
                toShrink.Add(tex);
            }
            SynShaderTextureUsage.SaveCache();
            if (toShrink.Count == 0) return;

            var journal = SynImporterRevertJournal.Load();
            var shrunk = new List<Texture>();
            long savedBytes = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (Texture tex in toShrink)
                {
                    string path = AssetDatabase.GetAssetPath(tex);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    long before = SynLightingTextureLimitsPass.EstimateBytes(tex);

                    SynImporterRevertJournal.Entry entry = journal.TryGetValue(path, out var existing) ? existing : new SynImporterRevertJournal.Entry();
                    if (!SynImporterRevertJournal.LimitMaxSize(importer, importerPlatform, ShrunkSize, entry)) continue;

                    // Journaled before the reimport, so a crash mid-build still gets reverted
                    journal[path] = entry;
                    SynImporterRevertJournal.Save(journal);
                    try
                    {
                        importer.SaveAndReimport();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not shrink unused texture '{path}': {e.Message}");
                        continue;
                    }
                    savedBytes += before;
                    shrunk.Add(tex);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (shrunk.Count == 0) return;
            shrunk.Sort((a, b) => SynLightingTextureLimitsPass.EstimateBytes(b).CompareTo(SynLightingTextureLimitsPass.EstimateBytes(a)));
            var names = new List<string>();
            for (int i = 0; i < shrunk.Count && i < 5; i++) names.Add(shrunk[i].name);
            string msg = $"Imported {shrunk.Count} texture(s) that nothing in the world can draw at {ShrunkSize}x{ShrunkSize} (saved est. ~{savedBytes / (1024.0 * 1024.0):F1} MB VRAM): {string.Join(", ", names)}{(shrunk.Count > 5 ? ", ..." : "")}. Reverted after the build.";
            Debug.Log($"[SYN SCENE OPTIMIZER] Unused Texture Shrinker: {msg}");
            SynPipelineCompactor.LogChange(Name, msg);
        }

        // What the build pulls in for this scene: its objects and everything they reference, including prefabs a
        // script can spawn, animation clips, sprites, fonts and Udon program constants
        private static UnityEngine.Object[] CollectSceneDependencies(Scene scene)
        {
            var roots = new List<UnityEngine.Object>();
            foreach (GameObject root in scene.GetRootGameObjects()) roots.Add(root);
            if (RenderSettings.skybox != null) roots.Add(RenderSettings.skybox);
            if (RenderSettings.customReflectionTexture != null) roots.Add(RenderSettings.customReflectionTexture);
            return EditorUtility.CollectDependencies(roots.ToArray());
        }

        // Saved slots, not only the shader's: a slot the shader doesn't declare still references its texture
        private static void CollectMaterialSlots(Material material, Dictionary<Texture, List<(Material, string)>> slots)
        {
            SerializedProperty texEnvs = new SerializedObject(material).FindProperty("m_SavedProperties.m_TexEnvs");
            if (texEnvs == null || !texEnvs.isArray) return;
            for (int i = 0; i < texEnvs.arraySize; i++)
            {
                SerializedProperty element = texEnvs.GetArrayElementAtIndex(i);
                string property = element.FindPropertyRelative("first")?.stringValue;
                if (!(element.FindPropertyRelative("second.m_Texture")?.objectReferenceValue is Texture tex) || string.IsNullOrEmpty(property)) continue;
                if (!slots.TryGetValue(tex, out var list)) slots[tex] = list = new List<(Material, string)>();
                list.Add((material, property));
            }
        }

        private static void CollectTextureReferences(UnityEngine.Object obj, HashSet<Texture> into)
        {
            SerializedProperty iterator = new SerializedObject(obj).GetIterator();
            while (iterator.Next(true))
            {
                if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Texture tex) into.Add(tex);
            }
        }

        // True if any slot holding the texture could be sampled, with the material's keywords or any keyword a script
        // can change, by its shader as authored or by the mobile shader the Mobile Shader Fallback swaps it to later
        private static bool CanBeDrawn(List<(Material material, string property)> uses, SynUdonUsageScan scan, BuildTarget target)
        {
            foreach (var (material, property) in uses)
            {
                if (scan.TextureReadsUnknown || scan.TextureReads.Contains(property)) return true;
                if (material.shader == null) return true;

                if (SynShaderTextureUsage.CanSample(material.shader, material.shaderKeywords, property, scan, target) != false) return true;

                Shader shipped = MobileShaderFallbackPass.GetShippedFallback(material.shader);
                if (shipped != null && SynShaderTextureUsage.CanSample(shipped, material.shaderKeywords, property, scan, target) != false) return true;
            }
            return false;
        }
    }
}
