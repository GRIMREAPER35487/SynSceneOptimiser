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
            EditorGUILayout.HelpBox("Enables Mipmap Streaming across all scene textures to dramatically reduce dynamic runtime VRAM. Base project textures are automatically reverted after build. Color palettes are strictly protected.", MessageType.Info);

            bool streamLightmaps = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeLightmaps", true);
            bool newStreamLightmaps = EditorGUILayout.Toggle(new GUIContent("Include Lightmaps & Volumes", "Enables Mipmap Streaming on Bakery/Unity lightmaps and light volumes without downscaling them."), streamLightmaps);
            if (newStreamLightmaps != streamLightmaps) SynSceneOptimizerSettings.SetBool("MipStreaming_IncludeLightmaps", newStreamLightmaps);

            bool streamParticles = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeParticles", true);
            bool newStreamParticles = EditorGUILayout.Toggle(new GUIContent("Include Particles & VFX", "Scans ParticleSystemRenderers, TrailRenderers, and LineRenderers for streaming."), streamParticles);
            if (newStreamParticles != streamParticles) SynSceneOptimizerSettings.SetBool("MipStreaming_IncludeParticles", newStreamParticles);

            bool streamUI = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeUI", true);
            bool newStreamUI = EditorGUILayout.Toggle(new GUIContent("Include UI & Sprites", "Scans UI Canvas graphics, RawImages, and SpriteRenderers."), streamUI);
            if (newStreamUI != streamUI) SynSceneOptimizerSettings.SetBool("MipStreaming_IncludeUI", newStreamUI);

            bool streamTerrain = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeTerrain", true);
            bool newStreamTerrain = EditorGUILayout.Toggle(new GUIContent("Include Terrain Layers", "Scans Terrain splatmaps, normal maps, and diffuse layers."), streamTerrain);
            if (newStreamTerrain != streamTerrain) SynSceneOptimizerSettings.SetBool("MipStreaming_IncludeTerrain", newStreamTerrain);

            bool streamSkybox = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeSkybox", true);
            bool newStreamSkybox = EditorGUILayout.Toggle(new GUIContent("Include Skybox & Reflection Probes", "Enables streaming on the scene Skybox and custom Reflection Probes."), streamSkybox);
            if (newStreamSkybox != streamSkybox) SynSceneOptimizerSettings.SetBool("MipStreaming_IncludeSkybox", newStreamSkybox);

            bool streamPackages = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludePackages", true);
            bool newStreamPackages = EditorGUILayout.Toggle(new GUIContent("Include Packages (AudioLink, etc.)", "Scans textures originating from Packages/ directory."), streamPackages);
            if (newStreamPackages != streamPackages) SynSceneOptimizerSettings.SetBool("MipStreaming_IncludePackages", newStreamPackages);

            bool enableKaiser = SynSceneOptimizerSettings.GetBool("MipStreaming_EnableKaiser", true);
            bool newEnableKaiser = EditorGUILayout.Toggle(new GUIContent("Enforce Kaiser Mipmap Filter", "Sets Mipmap Filter to Kaiser on all scene textures for crisp, high-clarity textures in VR (stays on permanently)."), enableKaiser);
            if (newEnableKaiser != enableKaiser) SynSceneOptimizerSettings.SetBool("MipStreaming_EnableKaiser", newEnableKaiser);
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            HashSet<Texture2D> candidateTextures = new HashSet<Texture2D>();
            HashSet<Material> candidateMaterials = new HashSet<Material>();

            bool streamLightmaps = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeLightmaps", true);
            bool streamParticles = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeParticles", true);
            bool streamUI = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeUI", true);
            bool streamTerrain = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeTerrain", true);
            bool streamSkybox = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludeSkybox", true);
            bool streamPackages = SynSceneOptimizerSettings.GetBool("MipStreaming_IncludePackages", true);

            // 1. Collect from all scene GameObjects (Renderers, UI, Terrains, Probes, Lights)
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

                // 1a. All Renderers (Mesh, Skinned, Particles, Sprites, Trails, Lines)
                Renderer[] allSceneRenderers = root.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in allSceneRenderers)
                {
                    if (r == null) continue;
                    if (SynProtectionData.IsProtected(r.gameObject)) continue;

                    if (!streamParticles && (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer))
                    {
                        continue;
                    }

                    if (!streamUI && r is SpriteRenderer)
                    {
                        continue;
                    }

                    // Collect shared materials
                    Material[] mats = r.sharedMaterials;
                    if (mats != null)
                    {
                        foreach (Material mat in mats)
                        {
                            if (mat != null && !SynProtectionData.IsProtected(mat))
                            {
                                candidateMaterials.Add(mat);
                            }
                        }
                    }

                    // Special handling for SpriteRenderer
                    if (r is SpriteRenderer sr && sr.sprite != null && sr.sprite.texture != null)
                    {
                        candidateTextures.Add(sr.sprite.texture);
                    }

                    // Special handling for ParticleSystemRenderer
                    if (r is ParticleSystemRenderer psr)
                    {
                        if (psr.trailMaterial != null && !SynProtectionData.IsProtected(psr.trailMaterial))
                        {
                            candidateMaterials.Add(psr.trailMaterial);
                        }
                    }
                }

                // 1b. Canvas & UI Graphics
                if (streamUI)
                {
                    var graphics = root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
                    foreach (var g in graphics)
                    {
                        if (g == null) continue;
                        if (g.mainTexture is Texture2D uiTex)
                        {
                            candidateTextures.Add(uiTex);
                        }
                        if (g.material != null && !SynProtectionData.IsProtected(g.material))
                        {
                            candidateMaterials.Add(g.material);
                        }
                    }
                }

                // 1c. Terrain Components
                if (streamTerrain)
                {
                    var terrains = root.GetComponentsInChildren<Terrain>(true);
                    foreach (var t in terrains)
                    {
                        if (t == null || t.terrainData == null) continue;
                        TerrainData td = t.terrainData;

                        if (td.terrainLayers != null)
                        {
                            foreach (var layer in td.terrainLayers)
                            {
                                if (layer == null) continue;
                                if (layer.diffuseTexture != null) candidateTextures.Add(layer.diffuseTexture);
                                if (layer.normalMapTexture != null) candidateTextures.Add(layer.normalMapTexture);
                                if (layer.maskMapTexture != null) candidateTextures.Add(layer.maskMapTexture);
                            }
                        }

                        if (td.detailPrototypes != null)
                        {
                            foreach (var dp in td.detailPrototypes)
                            {
                                if (dp.prototypeTexture != null) candidateTextures.Add(dp.prototypeTexture);
                            }
                        }

                        if (t.materialTemplate != null && !SynProtectionData.IsProtected(t.materialTemplate))
                        {
                            candidateMaterials.Add(t.materialTemplate);
                        }
                    }
                }

                // 1d. Reflection Probes
                if (streamSkybox)
                {
                    var probes = root.GetComponentsInChildren<ReflectionProbe>(true);
                    foreach (var probe in probes)
                    {
                        if (probe == null) continue;
                        if (probe.customBakedTexture is Texture2D probeTex) candidateTextures.Add(probeTex);
                    }
                }

                // 1e. Light Cookies
                var lights = root.GetComponentsInChildren<Light>(true);
                foreach (var light in lights)
                {
                    if (light != null && light.cookie is Texture2D cookieTex)
                    {
                        candidateTextures.Add(cookieTex);
                    }
                }

                // 1f. Projectors
                var projectors = root.GetComponentsInChildren<Projector>(true);
                foreach (var proj in projectors)
                {
                    if (proj != null && proj.material != null && !SynProtectionData.IsProtected(proj.material))
                    {
                        candidateMaterials.Add(proj.material);
                    }
                }
            }

            // 2. Collect from Skybox
            if (streamSkybox && RenderSettings.skybox != null && !SynProtectionData.IsProtected(RenderSettings.skybox))
            {
                candidateMaterials.Add(RenderSettings.skybox);
            }

            // 3. Collect from Virtual Staged Materials in SynPipelineCompactor
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
                        string propName = ShaderUtil.GetPropertyName(shader, i);
                        Texture tex = mat.GetTexture(propName);
                        if (tex is Texture2D tex2D)
                        {
                            candidateTextures.Add(tex2D);
                        }
                    }
                }
            }

            // 5. Collect Lightmaps if enabled
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

            // 6. Process candidate textures and apply Mipmap Streaming
            string existingPathsStr = SessionState.GetString("SynModifiedMipStreamingTextures", "");
            HashSet<string> allModifiedMipStreamingPaths = new HashSet<string>(
                existingPathsStr.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            );

            string existingMipEnabledPathsStr = SessionState.GetString("SynModifiedMipEnabledTextures", "");
            HashSet<string> allModifiedMipEnabledPaths = new HashSet<string>(
                existingMipEnabledPathsStr.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            );

            int modifiedImporterCount = 0;
            int modifiedAssetCount = 0;

            foreach (Texture2D tex in candidateTextures)
            {
                if (tex == null) continue;

                // CRITICAL SAFETY: Strictly protect Color Palettes
                if (IsColorPaletteTexture(tex))
                {
                    continue;
                }

                // Check user protected textures
                if (SynProtectionData.IsProtected(tex))
                {
                    continue;
                }

                string assetPath = AssetDatabase.GetAssetPath(tex);

                // Handle native .asset / cached textures that have no importer
                if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".asset"))
                {
                    if (tex.mipmapCount > 1)
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
                if (importer != null)
                {
                    bool needsReimport = false;

                    // Ensure mipmaps are enabled so streaming works
                    if (!importer.mipmapEnabled)
                    {
                        importer.mipmapEnabled = true;
                        allModifiedMipEnabledPaths.Add(assetPath);
                        needsReimport = true;
                    }

                    // Enforce Kaiser Mipmap Filter for superior sharpness in VR (stays on permanently)
                    bool enableKaiser = SynSceneOptimizerSettings.GetBool("MipStreaming_EnableKaiser", true);
                    if (enableKaiser && importer.mipmapFilter != TextureImporterMipFilter.KaiserFilter)
                    {
                        importer.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
                        needsReimport = true;
                    }

                    if (!importer.streamingMipmaps)
                    {
                        importer.streamingMipmaps = true;
                        allModifiedMipStreamingPaths.Add(assetPath);
                        needsReimport = true;
                    }

                    if (needsReimport)
                    {
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

            if (allModifiedMipStreamingPaths.Count > 0)
            {
                SessionState.SetString("SynModifiedMipStreamingTextures", string.Join(";", allModifiedMipStreamingPaths));
            }
            if (allModifiedMipEnabledPaths.Count > 0)
            {
                SessionState.SetString("SynModifiedMipEnabledTextures", string.Join(";", allModifiedMipEnabledPaths));
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

        private static bool IsColorPaletteTexture(Texture2D tex)
        {
            if (tex == null) return false;

            string name = tex.name.ToLowerInvariant();
            if (name.Contains("palette") || name.Contains("colorsheet"))
            {
                return true;
            }

            string path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path))
            {
                string lowerPath = path.ToLowerInvariant();
                if (lowerPath.Contains("palette") || lowerPath.Contains("/palettes/"))
                {
                    return true;
                }
            }

            // Small point-filtered textures are lookup tables / palettes
            if (tex.filterMode == FilterMode.Point && tex.width <= 128 && tex.height <= 128 && tex.mipmapCount <= 1)
            {
                return true;
            }

            return false;
        }
    }
}
