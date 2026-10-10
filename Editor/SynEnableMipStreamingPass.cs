using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimization pass that enables Mipmap Streaming on all candidate scene textures
    /// (including renderers, particles, UI, terrain, skybox, packages, and cached generated assets)
    /// to reduce dynamic VRAM footprint during runs while strictly protecting Color Palettes.
    /// </summary>
    public class SynEnableMipStreamingPass : SynOptimizationPass
    {
        public override string Id => "SynEnableMipStreamingPass";
        public override string Name => "Enable Mip Streaming";
        public override string Description => "Enables Mipmap Streaming on all textures referenced in the scene (meshes, particles, UI, terrain, skybox, and generated cache) while protecting Color Palettes.";
        public override string Category => "Texture Optimization";
        public override int Priority => 95;

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Enables Mipmap Streaming only on textures that benefit from it: textures with mipmaps, at least " + SynMipStreamingEligibility.MinStreamingSize + "px, sampled by mesh or terrain renderers. UI, sprites, cookies, lookup tables, ramps, palettes and point-filtered textures are skipped. Import settings are reverted after the build.", MessageType.Info);

            bool streamLightmaps = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeLightmaps", true);
            bool newStreamLightmaps = EditorGUILayout.Toggle(new GUIContent("Include Lightmaps & Volumes", "Enables Mipmap Streaming on Bakery/Unity lightmaps without downscaling them."), streamLightmaps);
            if (newStreamLightmaps != streamLightmaps) SynSceneOptimizerSettings.SetBool("MipStreaming_IncludeLightmaps", newStreamLightmaps);

            bool streamTerrain = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeTerrain", true);
            bool newStreamTerrain = EditorGUILayout.Toggle(new GUIContent("Include Terrain Layers", "Scans Terrain diffuse, normal and mask layers."), streamTerrain);
            if (newStreamTerrain != streamTerrain) SynSceneOptimizerSettings.SetBool("MipStreaming_IncludeTerrain", newStreamTerrain);

            bool streamPackages = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludePackages", true);
            bool newStreamPackages = EditorGUILayout.Toggle(new GUIContent("Include Packages (AudioLink, etc.)", "Scans textures originating from Packages/ directory."), streamPackages);
            if (newStreamPackages != streamPackages) SynSceneOptimizerSettings.SetBool("MipStreaming_IncludePackages", newStreamPackages);

            bool enableKaiser = SynSceneOptimizerSettings.GetBool("MipStreaming_EnableKaiser", true);
            bool newEnableKaiser = EditorGUILayout.Toggle(new GUIContent("Enforce Kaiser Mipmap Filter", "Sets Mipmap Filter to Kaiser on streamed textures for crisp, high-clarity textures in VR. Applied for the build only and reverted afterwards."), enableKaiser);
            if (newEnableKaiser != enableKaiser) SynSceneOptimizerSettings.SetBool("MipStreaming_EnableKaiser", newEnableKaiser);

            if (GUILayout.Button(new GUIContent("Repair Changes From Older Versions...", "Older versions of this pass permanently changed texture import settings. Review and undo those changes.")))
            {
                SynMipStreamingRepairWindow.Open();
            }
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            HashSet<Texture2D> candidateTextures = new HashSet<Texture2D>();
            HashSet<Material> candidateMaterials = new HashSet<Material>();

            bool streamLightmaps = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeLightmaps", true);
            bool streamTerrain = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeTerrain", true);
            bool streamPackages = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludePackages", true);

            List<GameObject> rootObjects = new List<GameObject>();
            if (scene.IsValid() && scene.isLoaded)
            {
                rootObjects.AddRange(scene.GetRootGameObjects());
            }
            else
            {
                for (int s = 0; s < SceneManager.sceneCount; s++)
                {
                    Scene sc = SceneManager.GetSceneAt(s);
                    if (sc.isLoaded) rootObjects.AddRange(sc.GetRootGameObjects());
                }
            }

            foreach (GameObject root in rootObjects)
            {
                if (root == null) continue;

                // 1. Mesh and Skinned renderers only: Unity's streaming system computes the visible mip level from
                //    their bounds and UV density. Particles, UI, sprites, skyboxes, cookies and projectors can't be
                //    measured, so streaming their textures saves nothing and can leave them blurry.
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                    if (SynProtectionData.IsProtected(r.gameObject)) continue;

                    Material[] mats = r.sharedMaterials;
                    if (mats == null) continue;
                    foreach (Material mat in mats)
                    {
                        if (mat != null && !SynProtectionData.IsProtected(mat))
                        {
                            candidateMaterials.Add(mat);
                        }
                    }
                }

                // 2. Terrain layers (terrain renders through the streaming-aware terrain system)
                if (streamTerrain)
                {
                    foreach (var t in root.GetComponentsInChildren<Terrain>(true))
                    {
                        if (t == null || t.terrainData == null) continue;
                        if (t.terrainData.terrainLayers != null)
                        {
                            foreach (var layer in t.terrainData.terrainLayers)
                            {
                                if (layer == null) continue;
                                if (layer.diffuseTexture != null) candidateTextures.Add(layer.diffuseTexture);
                                if (layer.normalMapTexture != null) candidateTextures.Add(layer.normalMapTexture);
                                if (layer.maskMapTexture != null) candidateTextures.Add(layer.maskMapTexture);
                            }
                        }
                        if (t.materialTemplate != null && !SynProtectionData.IsProtected(t.materialTemplate))
                        {
                            candidateMaterials.Add(t.materialTemplate);
                        }
                    }
                }
            }

            // 3. Textures staged by earlier passes (replacements for renderer materials)
            foreach (var staged in SynPipelineCompactor.GetAllStagedMaterials())
            {
                if (staged == null || staged.TrackedTextures == null) continue;
                foreach (var kvp in staged.TrackedTextures)
                {
                    if (kvp.Value is Texture2D tex2D)
                    {
                        candidateTextures.Add(tex2D);
                    }
                }
            }

            // 4. Extract all TexEnv properties from gathered candidate materials
            foreach (Material mat in candidateMaterials)
            {
                if (mat == null || mat.shader == null) continue;
                Shader shader = mat.shader;
                int propCount = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        Texture tex = mat.GetTexture(ShaderUtil.GetPropertyName(shader, i));
                        if (tex is Texture2D tex2D)
                        {
                            candidateTextures.Add(tex2D);
                        }
                    }
                }
            }

            // 5. Lightmaps (sampled per renderer via lightmapIndex, so streaming can measure them)
            if (streamLightmaps && LightmapSettings.lightmaps != null)
            {
                foreach (LightmapData lm in LightmapSettings.lightmaps)
                {
                    if (lm == null) continue;
                    if (lm.lightmapColor != null) candidateTextures.Add(lm.lightmapColor);
                    if (lm.lightmapDir != null) candidateTextures.Add(lm.lightmapDir);
                    if (lm.shadowMask != null) candidateTextures.Add(lm.shadowMask);
                }
            }

            // 6. Process candidate textures and apply Mipmap Streaming.
            //    Every importer change is journaled to disk first so it is reverted after the build/play session,
            //    even if the editor restarts in between.
            var journal = SynImporterRevertJournal.Load();
            bool enableKaiser = SynSceneOptimizerSettings.GetBool("MipStreaming_EnableKaiser", true);

            int modifiedImporterCount = 0;
            int modifiedAssetCount = 0;
            var skippedByVerdict = new Dictionary<SynMipStreamingVerdict, int>();

            foreach (Texture2D tex in candidateTextures)
            {
                if (tex == null) continue;

                // Check user protected textures
                if (SynProtectionData.IsProtected(tex))
                {
                    continue;
                }

                string assetPath = AssetDatabase.GetAssetPath(tex);

                // Handle native .asset / cached textures that have no importer
                if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".asset"))
                {
                    // Native .asset textures in the user's project have no importer to revert, so leave them alone
                    if (!assetPath.StartsWith(SynAssetCache.BaseCachePath)) continue;

                    if (SynMipStreamingEligibility.Evaluate(tex, null) == SynMipStreamingVerdict.Eligible)
                    {
                        var so = new SerializedObject(tex);
                        var streamingProp = so.FindProperty("m_StreamingMipmaps");
                        if (streamingProp != null && !streamingProp.boolValue)
                        {
                            streamingProp.boolValue = true;
                            so.ApplyModifiedPropertiesWithoutUndo();
                            modifiedAssetCount++;
                        }
                    }
                    continue;
                }

                // If in-memory instance without asset path
                if (string.IsNullOrEmpty(assetPath))
                {
                    continue;
                }

                // Skip packages if disabled
                if (!streamPackages && assetPath.StartsWith("Packages/"))
                {
                    continue;
                }

                // Lightmap check with protectBakery
                bool isBakery = SynProtectionData.IsBakeryAsset(assetPath, tex);
                if (isBakery && !streamLightmaps)
                {
                    continue;
                }

                // Modify TextureImporter for project / package assets
                TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null) continue;

                // Never force mipmaps on; skip anything that can't benefit (see SynMipStreamingEligibility)
                SynMipStreamingVerdict verdict = SynMipStreamingEligibility.Evaluate(tex, importer);
                if (verdict != SynMipStreamingVerdict.Eligible)
                {
                    skippedByVerdict.TryGetValue(verdict, out int count);
                    skippedByVerdict[verdict] = count + 1;
                    continue;
                }

                {
                    bool needsReimport = false;
                    SynImporterRevertJournal.Entry entry = journal.TryGetValue(assetPath, out var existing)
                        ? existing
                        : new SynImporterRevertJournal.Entry();

                    // Kaiser Mipmap Filter for sharper distant textures in VR (reverted after build)
                    if (enableKaiser && importer.mipmapFilter != TextureImporterMipFilter.KaiserFilter)
                    {
                        if (entry.OriginalMipFilter < 0) entry.OriginalMipFilter = (int)importer.mipmapFilter;
                        importer.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
                        needsReimport = true;
                    }

                    if (!importer.streamingMipmaps)
                    {
                        entry.StreamingWasOff = true;
                        importer.streamingMipmaps = true;
                        needsReimport = true;
                    }

                    if (needsReimport)
                    {
                        journal[assetPath] = entry;
                        SynImporterRevertJournal.Save(journal);
                        try
                        {
                            importer.SaveAndReimport();
                            modifiedImporterCount++;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not enable mip streaming on '{assetPath}': {e.Message}");
                        }
                    }
                }
            }

            if (skippedByVerdict.Count > 0 && SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false))
            {
                var parts = new List<string>();
                foreach (var kvp in skippedByVerdict) parts.Add($"{SynMipStreamingEligibility.Describe(kvp.Key)}: {kvp.Value}");
                Debug.Log("[SYN SCENE OPTIMIZER] Mipmap Streaming skipped textures that would not benefit: " + string.Join(", ", parts));
            }

            int totalUpdated = modifiedImporterCount + modifiedAssetCount;
            if (totalUpdated > 0)
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Enabled Mipmap Streaming on {totalUpdated} scene textures ({modifiedImporterCount} imported assets, {modifiedAssetCount} cached/native assets).");
                SynPipelineCompactor.LogChange(Name, $"Enabled Mipmap Streaming on {totalUpdated} scene textures ({modifiedImporterCount} imported assets, {modifiedAssetCount} cached/native assets).");
            }
            else
            {
                Debug.Log("[SYN SCENE OPTIMIZER] Mipmap Streaming Pass: All referenced scene textures already have Mipmap Streaming enabled.");
            }
        }

        /// <summary>
        /// Restores every importer setting recorded in the revert journal (plus legacy SessionState lists
        /// written by older versions), then clears the journal.
        /// </summary>
        public static void RevertImporterChanges()
        {
            var journal = SynImporterRevertJournal.Load();

            // Legacy lists from versions that tracked changes in SessionState only
            foreach (string path in SessionState.GetString("SynModifiedMipStreamingTextures", "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!journal.ContainsKey(path)) journal[path] = new SynImporterRevertJournal.Entry();
                journal[path].StreamingWasOff = true;
            }
            foreach (string path in SessionState.GetString("SynModifiedMipEnabledTextures", "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!journal.ContainsKey(path)) journal[path] = new SynImporterRevertJournal.Entry();
                journal[path].MipmapsWereOff = true;
            }
            SessionState.EraseString("SynModifiedMipStreamingTextures");
            SessionState.EraseString("SynModifiedMipEnabledTextures");

            if (journal.Count == 0) return;

            int revertedCount = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var kvp in journal)
                {
                    string path = kvp.Key;
                    var entry = kvp.Value;
                    if (!path.StartsWith("Packages/") && !File.Exists(path)) continue;

                    try
                    {
                        if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;

                        bool changed = false;
                        if (entry.StreamingWasOff && importer.streamingMipmaps)
                        {
                            importer.streamingMipmaps = false;
                            changed = true;
                        }
                        if (entry.MipmapsWereOff && importer.mipmapEnabled)
                        {
                            importer.mipmapEnabled = false;
                            changed = true;
                        }
                        if (entry.OriginalMipFilter >= 0 && (int)importer.mipmapFilter != entry.OriginalMipFilter)
                        {
                            importer.mipmapFilter = (TextureImporterMipFilter)entry.OriginalMipFilter;
                            changed = true;
                        }

                        if (changed)
                        {
                            importer.SaveAndReimport();
                            revertedCount++;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not revert texture settings on '{path}': {e.Message}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            SynImporterRevertJournal.Clear();
            if (revertedCount > 0)
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Reverted temporary import settings on {revertedCount} scene textures.");
            }
        }
    }
}
