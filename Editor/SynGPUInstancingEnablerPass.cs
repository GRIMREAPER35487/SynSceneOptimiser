using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimization pass that scans scene renderers and enables GPU Instancing on eligible materials
    /// (dynamic objects, unbatched statics, and multi-renderer shared materials) to combine draw calls into hardware instanced batches.
    /// </summary>
    public class SynGPUInstancingEnablerPass : SynOptimizationPass
    {
        public override string Id => "SynGPUInstancingEnablerPass";
        public override string Name => "GPU Instancing Enabler";
        public override string Description => "Enables GPU Instancing on materials across the scene to combine draw calls into instanced batches for identical meshes.";
        public override string Category => "Batching & Instancing";
        public override int Priority => 35; // Runs right after Mesh Deduplicator (30) and before Palette/Atlas passes (50)
        public override string Tab => "Optimizers";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool includeDynamic = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeDynamic", true);
            bool includeUnbatchedStatics = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeUnbatchedStatics", true);
            bool includeBatchedStatics = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeBatchedStatics", false);
            bool ignoreSkinned = SynSceneOptimizerSettings.GetBool("GPUInstancing_IgnoreSkinned", true);
            int minInstances = SynSceneOptimizerSettings.GetInt("GPUInstancing_MinInstanceCount", 2);
            bool disableStaticBatching = SynSceneOptimizerSettings.GetBool("GPUInstancing_DisableStaticBatching", false);
            string ignoreMaterialsRaw = SynSceneOptimizerSettings.GetString("GPUInstancing_IgnoreMaterials", "");

            var ignoreFilters = ParseIgnoreFilters(ignoreMaterialsRaw);

            // 1. Map materials to their using renderers
            var matToRenderers = GatherMaterialUsages(
                renderers,
                includeDynamic,
                includeUnbatchedStatics,
                includeBatchedStatics,
                ignoreSkinned,
                ignoreFilters
            );

            int materialsQueuedCount = 0;
            int renderersAffectedCount = 0;
            int batchingDisabledCount = 0;

            // 2. Queue eligible materials for GPU Instancing
            foreach (var kvp in matToRenderers)
            {
                Material mat = kvp.Key;
                List<Renderer> usingRenderers = kvp.Value;

                if (usingRenderers.Count < minInstances) continue;

                // If already enabled on the material asset, skip staging
                if (mat.enableInstancing) continue;

                renderersAffectedCount += usingRenderers.Count;

                // Stage into SynPipelineCompactor
                var matState = SynPipelineCompactor.GetStagingState(mat);
                if (matState != null)
                {
                    if (!matState.IsGPUInstanced)
                    {
                        matState.IsGPUInstanced = true;
                        matState.IsDirty = true;
                        if (!matState.AppliedPassTags.Contains("Inst"))
                        {
                            matState.AppliedPassTags.Add("Inst");
                        }
                        materialsQueuedCount++;
                    }
                }
                else
                {
                    // Fallback if staging context is inactive: set in memory
                    mat.enableInstancing = true;
                    materialsQueuedCount++;
                }

                // 3. Optionally disable Static Batching on instanced static GameObjects
                if (disableStaticBatching)
                {
                    foreach (Renderer r in usingRenderers)
                    {
                        if (r != null && r.gameObject.isStatic)
                        {
                            StaticEditorFlags curFlags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                            if ((curFlags & StaticEditorFlags.BatchingStatic) != 0)
                            {
                                GameObjectUtility.SetStaticEditorFlags(r.gameObject, curFlags & ~StaticEditorFlags.BatchingStatic);
                                batchingDisabledCount++;
                            }
                        }
                    }
                }
            }

            SynPipelineCompactor.LogChange(
                "GPU Instancing",
                string.Format("Queued GPU Instancing on {0} materials across {1} renderers.{2}",
                    materialsQueuedCount,
                    renderersAffectedCount,
                    batchingDisabledCount > 0 ? $" Disabled static batching on {batchingDisabledCount} objects." : "")
            );
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            int minInstances = SynSceneOptimizerSettings.GetInt("GPUInstancing_MinInstanceCount", 2);
            int newMinInstances = EditorGUILayout.IntSlider(new GUIContent("Min Instance Count", "Only enable instancing if a material is shared by at least this many renderers in the scene."), minInstances, 1, 10);
            if (newMinInstances != minInstances)
            {
                SynSceneOptimizerSettings.SetInt("GPUInstancing_MinInstanceCount", newMinInstances);
            }

            bool includeDynamic = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeDynamic", true);
            bool newIncludeDynamic = EditorGUILayout.Toggle(new GUIContent("Include Dynamic Objects", "Enable instancing for non-static GameObjects (props, pickups, interactive items)."), includeDynamic);
            if (newIncludeDynamic != includeDynamic)
            {
                SynSceneOptimizerSettings.SetBool("GPUInstancing_IncludeDynamic", newIncludeDynamic);
            }

            bool includeUnbatched = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeUnbatchedStatics", true);
            bool newIncludeUnbatched = EditorGUILayout.Toggle(new GUIContent("Include Unbatched Statics", "Enable instancing for static GameObjects that have Static Batching disabled (e.g. Bakery lightmapped objects)."), includeUnbatched);
            if (newIncludeUnbatched != includeUnbatched)
            {
                SynSceneOptimizerSettings.SetBool("GPUInstancing_IncludeUnbatchedStatics", newIncludeUnbatched);
            }

            bool includeBatched = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeBatchedStatics", false);
            bool newIncludeBatched = EditorGUILayout.Toggle(new GUIContent("Include Batched Statics", "Enable instancing as a fallback on static GameObjects even if Static Batching is on."), includeBatched);
            if (newIncludeBatched != includeBatched)
            {
                SynSceneOptimizerSettings.SetBool("GPUInstancing_IncludeBatchedStatics", newIncludeBatched);
            }

            bool ignoreSkinned = SynSceneOptimizerSettings.GetBool("GPUInstancing_IgnoreSkinned", true);
            bool newIgnoreSkinned = EditorGUILayout.Toggle(new GUIContent("Ignore Skinned Meshes", "Exclude SkinnedMeshRenderers (standard GPU instancing requires rigid MeshRenderers)."), ignoreSkinned);
            if (newIgnoreSkinned != ignoreSkinned)
            {
                SynSceneOptimizerSettings.SetBool("GPUInstancing_IgnoreSkinned", newIgnoreSkinned);
            }

            bool disableStaticBatching = SynSceneOptimizerSettings.GetBool("GPUInstancing_DisableStaticBatching", false);
            bool newDisableStaticBatching = EditorGUILayout.Toggle(new GUIContent("Disable Static Batching on Instances", "Turn off Batching Static on objects that share instanced materials so GPU Instancing batches them without duplicating vertex VRAM."), disableStaticBatching);
            if (newDisableStaticBatching != disableStaticBatching)
            {
                SynSceneOptimizerSettings.SetBool("GPUInstancing_DisableStaticBatching", newDisableStaticBatching);
            }

            string ignoreMats = SynSceneOptimizerSettings.GetString("GPUInstancing_IgnoreMaterials", "");
            string newIgnoreMats = EditorGUILayout.TextField(new GUIContent("Ignore Materials (comma-separated)", "Materials containing any of these comma-separated strings will be skipped."), ignoreMats);
            if (newIgnoreMats != ignoreMats)
            {
                SynSceneOptimizerSettings.SetString("GPUInstancing_IgnoreMaterials", newIgnoreMats);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(new GUIContent("Preview Eligible Materials", "Scans active scene and logs which materials would be instanced to the console."), GUILayout.Height(22)))
            {
                PreviewEligibleMaterials();
            }

            if (GUILayout.Button(new GUIContent("Apply to Material Assets on Disk", "Permanently enables GPU Instancing on eligible material assets on disk now (Undoable)."), GUILayout.Height(22)))
            {
                ApplyInstancingToDiskAssets();
            }

            EditorGUILayout.EndHorizontal();
        }

        private static List<string> ParseIgnoreFilters(string raw)
        {
            var filters = new List<string>();
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (var part in raw.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = part.Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(trimmed)) filters.Add(trimmed);
                }
            }
            return filters;
        }

        private static Dictionary<Material, List<Renderer>> GatherMaterialUsages(
            List<Renderer> renderers,
            bool includeDynamic,
            bool includeUnbatchedStatics,
            bool includeBatchedStatics,
            bool ignoreSkinned,
            List<string> ignoreFilters)
        {
            var matToRenderers = new Dictionary<Material, List<Renderer>>();

            foreach (Renderer r in renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (SynProtectionData.IsProtected(r.gameObject)) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;
                if (ignoreSkinned && r is SkinnedMeshRenderer) continue;

                bool isStatic = r.gameObject.isStatic;
                StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                bool isBatchingStatic = (flags & StaticEditorFlags.BatchingStatic) != 0;

                bool eligible = false;
                if (!isStatic && includeDynamic) eligible = true;
                else if (isStatic && !isBatchingStatic && includeUnbatchedStatics) eligible = true;
                else if (isStatic && isBatchingStatic && includeBatchedStatics) eligible = true;

                if (!eligible) continue;

                Material[] sharedMats = r.sharedMaterials;
                if (sharedMats == null) continue;

                foreach (Material mat in sharedMats)
                {
                    if (mat == null) continue;
                    if (SynProtectionData.IsProtected(mat)) continue;

                    string matNameLower = mat.name != null ? mat.name.ToLowerInvariant() : "";
                    bool isIgnored = false;
                    foreach (var filter in ignoreFilters)
                    {
                        if (matNameLower.Contains(filter)) { isIgnored = true; break; }
                    }
                    if (isIgnored) continue;

                    if (!matToRenderers.TryGetValue(mat, out var list))
                    {
                        list = new List<Renderer>();
                        matToRenderers[mat] = list;
                    }
                    list.Add(r);
                }
            }

            return matToRenderers;
        }

        private void PreviewEligibleMaterials()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                Debug.LogError("[GPU Instancing Enabler] Active scene is invalid or not loaded.");
                return;
            }

            var allRenderers = SynSceneQuery.GetAllRenderers(activeScene);
            bool includeDynamic = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeDynamic", true);
            bool includeUnbatchedStatics = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeUnbatchedStatics", true);
            bool includeBatchedStatics = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeBatchedStatics", false);
            bool ignoreSkinned = SynSceneOptimizerSettings.GetBool("GPUInstancing_IgnoreSkinned", true);
            int minInstances = SynSceneOptimizerSettings.GetInt("GPUInstancing_MinInstanceCount", 2);
            string ignoreMaterialsRaw = SynSceneOptimizerSettings.GetString("GPUInstancing_IgnoreMaterials", "");

            var ignoreFilters = ParseIgnoreFilters(ignoreMaterialsRaw);
            var usages = GatherMaterialUsages(allRenderers, includeDynamic, includeUnbatchedStatics, includeBatchedStatics, ignoreSkinned, ignoreFilters);

            int eligibleCount = 0;
            int totalRenderers = 0;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<b><color=#00e676>[GPU Instancing Enabler] Scene Analysis Preview:</color></b>");

            foreach (var kvp in usages.OrderByDescending(x => x.Value.Count))
            {
                Material mat = kvp.Key;
                int count = kvp.Value.Count;
                if (count < minInstances) continue;

                string status = mat.enableInstancing ? "<color=#888888>(Already Enabled)</color>" : "<color=#00e676>(ELIGIBLE)</color>";
                if (!mat.enableInstancing)
                {
                    eligibleCount++;
                    totalRenderers += count;
                }

                sb.AppendLine($" • <b>{mat.name}</b> (Shader: {mat.shader.name}) — <b>{count} renderers</b> {status}");
            }

            sb.AppendLine($"<b>Summary:</b> Found <b>{eligibleCount}</b> materials needing GPU Instancing across <b>{totalRenderers}</b> renderers (Min Threshold: {minInstances}).");
            Debug.Log(sb.ToString());
        }

        private void ApplyInstancingToDiskAssets()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                Debug.LogError("[GPU Instancing Enabler] Active scene is invalid or not loaded.");
                return;
            }

            var allRenderers = SynSceneQuery.GetAllRenderers(activeScene);
            bool includeDynamic = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeDynamic", true);
            bool includeUnbatchedStatics = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeUnbatchedStatics", true);
            bool includeBatchedStatics = SynSceneOptimizerSettings.GetBool("GPUInstancing_IncludeBatchedStatics", false);
            bool ignoreSkinned = SynSceneOptimizerSettings.GetBool("GPUInstancing_IgnoreSkinned", true);
            int minInstances = SynSceneOptimizerSettings.GetInt("GPUInstancing_MinInstanceCount", 2);
            string ignoreMaterialsRaw = SynSceneOptimizerSettings.GetString("GPUInstancing_IgnoreMaterials", "");

            var ignoreFilters = ParseIgnoreFilters(ignoreMaterialsRaw);
            var usages = GatherMaterialUsages(allRenderers, includeDynamic, includeUnbatchedStatics, includeBatchedStatics, ignoreSkinned, ignoreFilters);

            var toModify = new List<Material>();
            int totalRenderersAffected = 0;

            foreach (var kvp in usages)
            {
                Material mat = kvp.Key;
                if (kvp.Value.Count < minInstances) continue;
                if (mat.enableInstancing) continue;

                toModify.Add(mat);
                totalRenderersAffected += kvp.Value.Count;
            }

            if (toModify.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "GPU Instancing Enabler",
                    $"All eligible materials ({usages.Count} scanned) already have GPU Instancing enabled!",
                    "OK"
                );
                return;
            }

            bool confirm = EditorUtility.DisplayDialog(
                "Enable GPU Instancing on Disk Assets",
                $"Found {toModify.Count} material assets across {totalRenderersAffected} renderers that do not have GPU Instancing enabled.\n\nWould you like to enable GPU Instancing on these material assets now?",
                "Yes, Enable Now",
                "Cancel"
            );

            if (!confirm) return;

            Undo.RecordObjects(toModify.ToArray(), "Enable GPU Instancing on Materials");
            foreach (var mat in toModify)
            {
                mat.enableInstancing = true;
                EditorUtility.SetDirty(mat);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"<color=#00e676><b>[GPU Instancing Enabler]</b></color> Successfully enabled GPU Instancing on <b>{toModify.Count}</b> material assets across <b>{totalRenderersAffected}</b> renderers!");
            EditorUtility.DisplayDialog(
                "GPU Instancing Applied",
                $"Successfully enabled GPU Instancing on {toModify.Count} material assets across {totalRenderersAffected} renderers!",
                "OK"
            );
        }
    }
}
