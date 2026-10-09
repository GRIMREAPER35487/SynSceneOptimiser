using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimization pass that merges different meshes using solid-color materials into a single 
    /// texture palette sheet, remapping their UVs in-memory so they can be batched together.
    /// </summary>
    public class SynColorPaletteOptimizerPass : SynOptimizationPass
    {
        public override string Id => "SynColorPaletteOptimizerPass";
        public override string Name => "Color Palette Optimizer";
        public override string Description => "Merges solid color materials into a single texture atlas to reduce material variance and draw calls.";
        public override string Category => "Texture Optimization";
        public override int Priority => 50;

        private struct ShaderPropertyLayout
        {
            public string metallicProp;
            public string smoothnessProp;
            public bool invertSmoothness; 
            public string emissionProp;
            public string metallicGlossMapProp;
            public string metallicGlossKeyword;
            public string emissionMapProp;
            public string emissionKeyword;
            
            public int metallicChannel;
            public int smoothnessChannel;
        }

        private static readonly Dictionary<string, ShaderPropertyLayout> ShaderLayouts = new Dictionary<string, ShaderPropertyLayout>
        {
            { "standard", new ShaderPropertyLayout {
                metallicProp = "_Metallic",
                smoothnessProp = "_Glossiness",
                invertSmoothness = false,
                emissionProp = "_EmissionColor",
                metallicGlossMapProp = "_MetallicGlossMap",
                metallicGlossKeyword = "_METALLICGLOSSMAP",
                emissionMapProp = "_EmissionMap",
                emissionKeyword = "_EMISSION",
                metallicChannel = 0,
                smoothnessChannel = 3
            }},
            { "mochie/standard", new ShaderPropertyLayout {
                metallicProp = "_MetallicStrength",
                smoothnessProp = "_RoughnessStrength",
                invertSmoothness = true,
                emissionProp = "_EmissionColor",
                metallicGlossMapProp = "_PackedMap",
                metallicGlossKeyword = "",
                emissionMapProp = "_EmissionMap",
                emissionKeyword = "",
                metallicChannel = 1,
                smoothnessChannel = 0
            }}
        };

        private struct MaterialPropertiesKey : IEquatable<MaterialPropertiesKey>, IComparable<MaterialPropertiesKey>
        {
            public Color AlbedoColor;
            public float Metallic;
            public float Smoothness;
            public Color EmissionColor;

            public bool Equals(MaterialPropertiesKey other)
            {
                return AlbedoColor == other.AlbedoColor &&
                       Metallic == other.Metallic &&
                       Smoothness == other.Smoothness &&
                       EmissionColor == other.EmissionColor;
            }

            public int CompareTo(MaterialPropertiesKey other)
            {
                int c = AlbedoColor.r.CompareTo(other.AlbedoColor.r);
                if (c != 0) return c;
                c = AlbedoColor.g.CompareTo(other.AlbedoColor.g);
                if (c != 0) return c;
                c = AlbedoColor.b.CompareTo(other.AlbedoColor.b);
                if (c != 0) return c;
                c = AlbedoColor.a.CompareTo(other.AlbedoColor.a);
                if (c != 0) return c;

                c = Metallic.CompareTo(other.Metallic);
                if (c != 0) return c;

                c = Smoothness.CompareTo(other.Smoothness);
                if (c != 0) return c;

                c = EmissionColor.r.CompareTo(other.EmissionColor.r);
                if (c != 0) return c;
                c = EmissionColor.g.CompareTo(other.EmissionColor.g);
                if (c != 0) return c;
                c = EmissionColor.b.CompareTo(other.EmissionColor.b);
                if (c != 0) return c;
                return EmissionColor.a.CompareTo(other.EmissionColor.a);
            }

            public override bool Equals(object obj)
            {
                return obj is MaterialPropertiesKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 23 + AlbedoColor.GetHashCode();
                    hash = hash * 23 + Metallic.GetHashCode();
                    hash = hash * 23 + Smoothness.GetHashCode();
                    hash = hash * 23 + EmissionColor.GetHashCode();
                    return hash;
                }
            }
        }

        private static readonly HashSet<string> UserTextureProperties = new HashSet<string>
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_BaseColorTab", "_ColorMap", "_MainColorMap",
            "_BumpMap", "_NormalMap", "_DetailNormalMap", "_DetailNormal", "_BumpMapDetail",
            "_MetallicGlossMap", "_MetallicMap", "_SpecGlossMap",
            "_OcclusionMap", "_DetailAOMap", "_ParallaxMap", "_HeightMap",
            "_EmissionMap", "_DetailAlbedoMap", "_DetailAlbedo",
            "_PackedMap", "_MaskMap", "_MetallicRoughnessMap",
            "_AlphaMask", "_DetailMask", "_ThicknessMap", "_Thickness", "_SubsurfaceMask",
            "_GlossMap", "_RoughnessMap"
        };

        private bool IsEditorOnly(Transform t)
        {
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }

        private bool ShouldExcludeRenderer(Renderer r)
        {
            return SynSceneQuery.IsVideoComponentDetected(r);
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool verbose = SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false);
            bool logOptimized = SynSceneOptimizerSettings.GetBool("PaletteAtlas_LogOptimized", true);
            bool optimizeStatic = SynSceneOptimizerSettings.GetBool("PaletteAtlas_OptimizeStatic", true);
            bool optimizeNonStatic = SynSceneOptimizerSettings.GetBool("PaletteAtlas_OptimizeNonStatic", true);
            bool ignoreInstanced = SynSceneOptimizerSettings.GetBool("PaletteAtlas_IgnoreInstanced", true);
            bool debugColors = SynSceneOptimizerSettings.GetBool("PaletteAtlas_DebugCombinedColors", false);
            bool extremeMode = SynSceneOptimizerSettings.GetBool("PaletteAtlas_ExtremeMode", false);
            
            string ignoreMaterialsRaw = SynSceneOptimizerSettings.GetString("PaletteAtlas_IgnoreMaterials", "");
            var ignoreMaterialFilters = new List<string>();
            if (!string.IsNullOrEmpty(ignoreMaterialsRaw))
            {
                foreach (var part in ignoreMaterialsRaw.Split(new char[]{','}, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = part.Trim();
                    if (!string.IsNullOrEmpty(t)) ignoreMaterialFilters.Add(t.ToLower());
                }
            }

            if (verbose)
            {
                Debug.Log($"[SynColorPaletteOptimizerPass] Verbose: Starting analysis of {renderers.Count} scene renderers.");
            }

            // 1. Pre-Filter structurally broken or protected objects out of the master list
            var eligibleRenderers = new List<Renderer>();

            foreach (var r in renderers)
            {
                if (r == null || IsEditorOnly(r.transform)) continue;
                if (SynProtectionData.IsProtected(r.gameObject)) continue;
                if (!(r is MeshRenderer)) continue;

                // Filter out empty or unassigned material slots
                var testMats = r.sharedMaterials;
                if (testMats == null || testMats.Length == 0 || testMats[0] == null)
                {
                    if (verbose)
                    {
                        Debug.LogWarning($"[SynPaletteGuardrail] Skipped '{r.gameObject.name}' because its Material array is completely empty or has null slots.");
                    }
                    continue;
                }

                // Filter out missing, empty, or protected mesh asset references
                Mesh testMesh = this.GetMesh(r);
                if (testMesh == null || testMesh.vertexCount == 0 || SynProtectionData.IsProtected(testMesh))
                {
                    if (verbose)
                    {
                        Debug.LogWarning($"[SynPaletteGuardrail] Skipped '{r.gameObject.name}' because its Mesh asset reference is missing, corrupted, or protected.");
                    }
                    continue;
                }

                if (testMesh.name.Contains("Palettized"))
                {
                    if (verbose)
                    {
                        Debug.LogWarning($"[SynPaletteGuardrail] Skipped '{r.gameObject.name}' because its mesh '{testMesh.name}' is already palettized.");
                    }
                    continue;
                }

                eligibleRenderers.Add(r);
            }

            // 2. Find eligible solid-color materials
            var eligibleMaterials = new Dictionary<Material, Color>();
            var ignoredMaterials = new Dictionary<Material, string>();
            var tvMaterials = new HashSet<Material>();

            foreach (var renderer in eligibleRenderers)
            {
                if (SynProtectionData.IsProtected(renderer.gameObject)) continue;
                var mats = renderer.sharedMaterials;
                bool isStaticObject = renderer.gameObject.isStatic;
                bool isExcluded = ShouldExcludeRenderer(renderer);

                if (!optimizeStatic && isStaticObject) continue;
                if (!optimizeNonStatic && !isStaticObject) continue;

                foreach (var mat in mats)
                {
                    if (mat == null) continue;

                    if (SynProtectionData.IsProtected(mat))
                    {
                        ignoredMaterials[mat] = "Protected by user settings";
                        continue;
                    }

                    if (ignoreMaterialFilters.Count > 0)
                    {
                        string lowerName = mat.name != null ? mat.name.ToLower() : "";
                        bool matched = false;
                        foreach (var f in ignoreMaterialFilters)
                        {
                            if (lowerName.Contains(f)) { matched = true; break; }
                        }
                        if (matched)
                        {
                            ignoredMaterials[mat] = "Ignored by settings (name match)";
                            continue;
                        }
                    }

                    if (isExcluded)
                    {
                        tvMaterials.Add(mat);
                        continue;
                    }

                    if (eligibleMaterials.ContainsKey(mat) || ignoredMaterials.ContainsKey(mat))
                    {
                        continue;
                    }

                    if (IsSolidColorMaterial(mat, ignoreInstanced, out Color color, out string reason))
                    {
                        eligibleMaterials[mat] = color;
                        if (verbose) Debug.Log($"[SynColorPaletteOptimizerPass] Verbose: Found eligible solid-color material candidate '{mat.name}' on GameObject '{renderer.gameObject.name}'.");
                    }
                    else
                    {
                        ignoredMaterials[mat] = reason;
                        if (verbose) Debug.Log($"[SynColorPaletteOptimizerPass] Verbose: Skipped material '{mat.name}' on '{renderer.gameObject.name}' because: {reason}.");
                    }
                }
            }

            if (logOptimized)
            {
                var summary = new System.Text.StringBuilder();
                summary.AppendLine("[SynSceneOptimizer] Color Palette Optimizer Diagnostics:");
                summary.AppendLine($"- Eligible & Combined: {eligibleMaterials.Count} materials");
                
                int instancedCount = 0;
                int texturedCount = 0;
                int otherCount = 0;
                var texturedDetails = new List<string>();
                foreach (var kvp in ignoredMaterials)
                {
                    if (kvp.Value.Contains("Instancing")) instancedCount++;
                    else if (kvp.Value.Contains("texture"))
                    {
                        texturedCount++;
                        if (texturedDetails.Count < 20)
                        {
                            texturedDetails.Add($"{kvp.Key.name} ({kvp.Value})");
                        }
                    }
                    else otherCount++;
                }

                summary.AppendLine($"- Skipped (GPU Instancing Enabled): {instancedCount}");
                summary.AppendLine($"- Skipped (Has textures): {texturedCount} (Sample: {string.Join(", ", texturedDetails)})");
                if (tvMaterials.Count > 0)
                {
                    summary.AppendLine($"- Skipped (Video Player / ProTV): {tvMaterials.Count}");
                }

                Debug.Log(summary.ToString());
            }

            if (eligibleMaterials.Count <= 1)
            {
                if (verbose)
                {
                    Debug.LogWarning($"[SynSceneOptimizer] Color Palette Optimizer: No redundant solid color materials to merge. (Found {eligibleMaterials.Count} eligible materials)");
                }
                return;
            }

            // 3. Group materials by shader and compatibility
            var groups = GroupMaterials(eligibleMaterials, extremeMode);

            int totalMergedMaterials = 0;
            int totalObjectsOptimized = 0;
            var optimizedMeshCache = new Dictionary<MeshCacheKey, Mesh>();

            // 4. Pre-gather ALL unique MaterialPropertiesKey across ALL groups to build ONE Master Texture
            var allSceneUniqueKeys = new List<MaterialPropertiesKey>();
            var globalKeyToPixel = new Dictionary<MaterialPropertiesKey, Vector2>();
            var allMaterialToKey = new Dictionary<Material, MaterialPropertiesKey>();
            bool anyGroupHasEmission = false;
            bool anyGroupHasMetallic = false;

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group.Count <= 1) continue;

                string shaderNameLower = group[0].shader.name.ToLower();
                ShaderPropertyLayout layout = default;
                bool hasLayout = false;
                foreach (var kvp in ShaderLayouts)
                {
                    if (shaderNameLower.Contains(kvp.Key))
                    {
                        layout = kvp.Value;
                        hasLayout = true;
                        break;
                    }
                }

                foreach (var mat in group)
                {
                    Color col = eligibleMaterials[mat];
                    float metallic = 0f;
                    float smoothness = 0f;
                    Color emission = Color.black;

                    if (hasLayout)
                    {
                        if (mat.HasProperty(layout.metallicProp)) metallic = mat.GetFloat(layout.metallicProp);
                        if (mat.HasProperty(layout.smoothnessProp)) smoothness = mat.GetFloat(layout.smoothnessProp);

                        bool isEmissiveGI = (mat.globalIlluminationFlags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) == 0 &&
                                            (mat.globalIlluminationFlags != MaterialGlobalIlluminationFlags.None);
                        bool keywordEnabled = mat.IsKeywordEnabled("_EMISSION") || 
                                             mat.IsKeywordEnabled("_EMISSIVE") ||
                                             mat.IsKeywordEnabled("_EMISSION_ON") ||
                                             (!string.IsNullOrEmpty(layout.emissionKeyword) && mat.IsKeywordEnabled(layout.emissionKeyword));
                        string matNameLower = mat.name.ToLower();
                        string sNameLower = mat.shader.name.ToLower();
                        bool isExplicitLight = matNameLower.Contains("bulb") || matNameLower.Contains("candle") || matNameLower.Contains("flame") ||
                                               matNameLower.Contains("light") || matNameLower.Contains("neon") || matNameLower.Contains("glow") ||
                                               sNameLower.Contains("emissive") || sNameLower.Contains("glow");

                        if ((isEmissiveGI || keywordEnabled || isExplicitLight) && mat.HasProperty(layout.emissionProp))
                        {
                            emission = mat.GetColor(layout.emissionProp);
                            if (mat.HasProperty("_EmissionStrength"))
                            {
                                emission *= mat.GetFloat("_EmissionStrength");
                            }
                        }
                    }

                    if (metallic > 0.001f || smoothness > 0.001f) anyGroupHasMetallic = true;
                    if (emission.r > 0.001f || emission.g > 0.001f || emission.b > 0.001f) anyGroupHasEmission = true;

                    var key = new MaterialPropertiesKey
                    {
                        AlbedoColor = col,
                        Metallic = metallic,
                        Smoothness = smoothness,
                        EmissionColor = emission
                    };

                    allMaterialToKey[mat] = key;
                    if (!allSceneUniqueKeys.Contains(key))
                    {
                        allSceneUniqueKeys.Add(key);
                    }
                }
            }

            if (allSceneUniqueKeys.Count == 0) return;

            // Deterministically sort unique keys so pixel coordinates & UVs are 100% reproducible across runs
            allSceneUniqueKeys.Sort();

            // Compute Master Grid Size for the 1 Unified Texture
            int masterGridSize = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(allSceneUniqueKeys.Count)));
            masterGridSize = Mathf.Max(masterGridSize, 16);

            for (int c = 0; c < allSceneUniqueKeys.Count; c++)
            {
                int x = c % masterGridSize;
                int y = c / masterGridSize;
                var k = allSceneUniqueKeys[c];

                float u = (x + 0.5f) / (float)masterGridSize;
                float v = (y + 0.5f) / (float)masterGridSize;
                globalKeyToPixel[k] = new Vector2(u, v);
            }

            // Build Master Hash incorporating all sorted keys to guarantee cache freshness if any scene color shifts
            var masterHashTokens = new List<string>
            {
                "MasterPalette_v7_srgb",
                QualitySettings.activeColorSpace.ToString(),
                masterGridSize.ToString(),
                allSceneUniqueKeys.Count.ToString(),
                anyGroupHasEmission.ToString(),
                anyGroupHasMetallic.ToString()
            };

            for (int c = 0; c < allSceneUniqueKeys.Count; c++)
            {
                var k = allSceneUniqueKeys[c];
                masterHashTokens.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "[{0:F4},{1:F4},{2:F4},{3:F4}|{4:F4}|{5:F4}|{6:F4},{7:F4},{8:F4}]",
                    k.AlbedoColor.r, k.AlbedoColor.g, k.AlbedoColor.b, k.AlbedoColor.a,
                    k.Metallic, k.Smoothness,
                    k.EmissionColor.r, k.EmissionColor.g, k.EmissionColor.b
                ));
            }

            string masterHash = SynAssetCache.ComputeCompositeHash(masterHashTokens.ToArray());
            string albedoHash = $"{masterHash}_Albedo";
            string metallicHash = $"{masterHash}_Metallic";
            string emissionHash = $"{masterHash}_Emission";

            Texture2D sharedPaletteTex = null;
            Texture2D sharedMetallicTex = null;
            Texture2D sharedEmissionTex = null;

            bool hasCachedAlbedo = SynAssetCache.TryGetCachedAsset<Texture2D>(SynAssetCache.PalettesCategory, albedoHash, out sharedPaletteTex);
            bool hasCachedMetallic = SynAssetCache.TryGetCachedAsset<Texture2D>(SynAssetCache.PalettesCategory, metallicHash, out sharedMetallicTex);
            bool hasCachedEmission = !anyGroupHasEmission || SynAssetCache.TryGetCachedAsset<Texture2D>(SynAssetCache.PalettesCategory, emissionHash, out sharedEmissionTex);

            bool needsBake = !hasCachedAlbedo || sharedPaletteTex == null ||
                             !hasCachedMetallic || sharedMetallicTex == null ||
                             (anyGroupHasEmission && (!hasCachedEmission || sharedEmissionTex == null));

            if (needsBake)
            {
                Texture2D masterAlbedo = new Texture2D(masterGridSize, masterGridSize, TextureFormat.RGBA32, false);
                masterAlbedo.filterMode = FilterMode.Point;
                masterAlbedo.wrapMode = TextureWrapMode.Clamp;
                Color32[] clearWhite = new Color32[masterGridSize * masterGridSize];
                for (int p = 0; p < clearWhite.Length; p++) clearWhite[p] = new Color32(255, 255, 255, 255);
                masterAlbedo.SetPixels32(clearWhite);

                Texture2D masterMetallic = new Texture2D(masterGridSize, masterGridSize, TextureFormat.RGBA32, false);
                masterMetallic.filterMode = FilterMode.Point;
                masterMetallic.wrapMode = TextureWrapMode.Clamp;
                Color32[] clearBlack = new Color32[masterGridSize * masterGridSize];
                for (int p = 0; p < clearBlack.Length; p++) clearBlack[p] = new Color32(0, 0, 0, 0);
                masterMetallic.SetPixels32(clearBlack);

                Texture2D masterEmission = null;
                if (anyGroupHasEmission)
                {
                    masterEmission = new Texture2D(masterGridSize, masterGridSize, TextureFormat.RGBA32, false);
                    masterEmission.filterMode = FilterMode.Point;
                    masterEmission.wrapMode = TextureWrapMode.Clamp;
                    masterEmission.SetPixels32(clearBlack);
                }

                for (int c = 0; c < allSceneUniqueKeys.Count; c++)
                {
                    int x = c % masterGridSize;
                    int y = c / masterGridSize;
                    var k = allSceneUniqueKeys[c];

                    // In Unity Linear color space, mat.GetColor() returns linear values.
                    // But Texture2D.EncodeToPNG() saves raw byte values into PNG, and Unity's TextureImporter
                    // imports PNGs as sRGB by default. At runtime, the GPU sampler applies an sRGB->Linear
                    // conversion (pow 2.2). If we write linear values into the PNG, the conversion runs twice,
                    // severely darkening and distorting colors (e.g. turning cream/peach into dark terracotta).
                    // Converting to gamma before saving ensures the GPU sRGB->Linear decode reproduces the exact Linear color!
                    Color albedoCol = QualitySettings.activeColorSpace == ColorSpace.Linear ? k.AlbedoColor.gamma : k.AlbedoColor;
                    albedoCol.r = Mathf.Clamp01(albedoCol.r);
                    albedoCol.g = Mathf.Clamp01(albedoCol.g);
                    albedoCol.b = Mathf.Clamp01(albedoCol.b);
                    albedoCol.a = Mathf.Clamp01(k.AlbedoColor.a);
                    masterAlbedo.SetPixel(x, y, debugColors ? Color.yellow : albedoCol);

                    // Standard Unity PBR MetallicGloss: R = Metallic, A = Smoothness (raw linear data masks)
                    Color mgColor = new Color(Mathf.Clamp01(k.Metallic), 0, 0, Mathf.Clamp01(k.Smoothness));
                    masterMetallic.SetPixel(x, y, mgColor);

                    if (masterEmission != null)
                    {
                        Color emCol = QualitySettings.activeColorSpace == ColorSpace.Linear ? k.EmissionColor.gamma : k.EmissionColor;
                        emCol.r = Mathf.Clamp01(emCol.r);
                        emCol.g = Mathf.Clamp01(emCol.g);
                        emCol.b = Mathf.Clamp01(emCol.b);
                        emCol.a = Mathf.Clamp01(k.EmissionColor.a);
                        masterEmission.SetPixel(x, y, emCol);
                    }
                }

                masterAlbedo.Apply(false, false);
                masterMetallic.Apply(false, false);
                if (masterEmission != null) masterEmission.Apply(false, false);

                sharedPaletteTex = SynAssetCache.SaveCachedAsset(masterAlbedo, SynAssetCache.PalettesCategory, albedoHash, "Palette_SceneMaster_Albedo");
                sharedMetallicTex = SynAssetCache.SaveCachedAsset(masterMetallic, SynAssetCache.PalettesCategory, metallicHash, "Palette_SceneMaster_Metallic");
                sharedEmissionTex = masterEmission != null ? SynAssetCache.SaveCachedAsset(masterEmission, SynAssetCache.PalettesCategory, emissionHash, "Palette_SceneMaster_Emission") : null;
            }

            // Separate renderers into static and non-static lists
            var staticRenderers = new List<Renderer>();
            var nonStaticRenderers = new List<Renderer>();
            foreach (var r in eligibleRenderers)
            {
                if (r.gameObject.isStatic) staticRenderers.Add(r);
                else nonStaticRenderers.Add(r);
            }

            // 5. Process each compatible group
            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group.Count <= 1) continue;

                string shaderNameLower = group[0].shader.name.ToLower();
                ShaderPropertyLayout layout = default;
                bool hasLayout = false;
                foreach (var kvp in ShaderLayouts)
                {
                    if (shaderNameLower.Contains(kvp.Key))
                    {
                        layout = kvp.Value;
                        hasLayout = true;
                        break;
                    }
                }

                var keyToPixel = globalKeyToPixel;
                var materialToKey = allMaterialToKey;
                var mappings = new Dictionary<Material, MaterialMapping>();
                foreach (var mat in group)
                {
                    var key = materialToKey[mat];
                    mappings[mat] = new MaterialMapping
                    {
                        PointUV = keyToPixel[key]
                    };
                }

                string cleanName = Regex.Replace(group[0].name, @"[^a-zA-Z0-9_]", "");

                // A. Handle Static Renderers (Creates Material_{cleanName}_StaticPalette)
                if (optimizeStatic && staticRenderers.Count > 0)
                {
                    string groupSubHash = SynAssetCache.ComputePaletteHash(group, (hasLayout ? layout.metallicProp : "None") + "_Static_v7", masterGridSize, masterGridSize);
                    string staticPaletteHash = SynAssetCache.ComputeCompositeHash("PaletteMaterial_Static_v7", masterHash, groupSubHash);
                    Material staticPaletteMat = null;

                    if (SynAssetCache.TryGetCachedAsset<Material>(SynAssetCache.MaterialsCategory, staticPaletteHash, out Material cachedStaticMat))
                    {
                        staticPaletteMat = cachedStaticMat;
                        ApplyPaletteTexturesToMaterial(staticPaletteMat, sharedPaletteTex, sharedMetallicTex, sharedEmissionTex, layout, hasLayout);
                    }
                    else
                    {
                        staticPaletteMat = CreatePaletteMaterial(group[0], sharedPaletteTex, sharedMetallicTex, sharedEmissionTex, layout, hasLayout, "_StaticPalette");
                        staticPaletteMat = SynAssetCache.SaveCachedAsset(staticPaletteMat, SynAssetCache.MaterialsCategory, staticPaletteHash, $"Material_{cleanName}_StaticPalette");
                    }

                    int optCount = ProcessRenderersForGroup(staticRenderers, group, staticPaletteMat, mappings, "PalMeshMaster_Static_v7", "_StaticPalettized", masterHash, optimizedMeshCache, verbose);
                    totalObjectsOptimized += optCount;
                }

                // B. Handle Non-Static Renderers (Creates Material_{cleanName}_NonStaticPalette)
                if (optimizeNonStatic && nonStaticRenderers.Count > 0)
                {
                    string groupSubHash = SynAssetCache.ComputePaletteHash(group, (hasLayout ? layout.metallicProp : "None") + "_NonStatic_v7", masterGridSize, masterGridSize);
                    string nonStaticPaletteHash = SynAssetCache.ComputeCompositeHash("PaletteMaterial_NonStatic_v7", masterHash, groupSubHash);
                    Material nonStaticPaletteMat = null;

                    if (SynAssetCache.TryGetCachedAsset<Material>(SynAssetCache.MaterialsCategory, nonStaticPaletteHash, out Material cachedNonStaticMat))
                    {
                        nonStaticPaletteMat = cachedNonStaticMat;
                        ApplyPaletteTexturesToMaterial(nonStaticPaletteMat, sharedPaletteTex, sharedMetallicTex, sharedEmissionTex, layout, hasLayout);
                    }
                    else
                    {
                        nonStaticPaletteMat = CreatePaletteMaterial(group[0], sharedPaletteTex, sharedMetallicTex, sharedEmissionTex, layout, hasLayout, "_NonStaticPalette");
                        nonStaticPaletteMat = SynAssetCache.SaveCachedAsset(nonStaticPaletteMat, SynAssetCache.MaterialsCategory, nonStaticPaletteHash, $"Material_{cleanName}_NonStaticPalette");
                    }

                    int optCount = ProcessRenderersForGroup(nonStaticRenderers, group, nonStaticPaletteMat, mappings, "PalMeshMaster_NonStatic_v7", "_NonStaticPalettized", masterHash, optimizedMeshCache, verbose);
                    totalObjectsOptimized += optCount;
                }

                totalMergedMaterials += group.Count;
            }

            EditorUtility.ClearProgressBar();

            SynPipelineCompactor.LogChange(
                "Color Palette",
                string.Format("Created unified master palette. Merged {0} solid color materials. Optimized {1} GameObjects.", totalMergedMaterials, totalObjectsOptimized)
            );
        }

        private void ApplyPaletteTexturesToMaterial(Material paletteMat, Texture2D albedoTex, Texture2D metallicTex, Texture2D emissionTex, ShaderPropertyLayout layout, bool hasLayout)
        {
            if (paletteMat == null) return;
            if (paletteMat.HasProperty("_MainTex"))
            {
                if (paletteMat.GetTexture("_MainTex") != albedoTex) paletteMat.SetTexture("_MainTex", albedoTex);
                paletteMat.SetTextureScale("_MainTex", Vector2.one);
                paletteMat.SetTextureOffset("_MainTex", Vector2.zero);
            }
            if (paletteMat.HasProperty("_BaseMap"))
            {
                if (paletteMat.GetTexture("_BaseMap") != albedoTex) paletteMat.SetTexture("_BaseMap", albedoTex);
                paletteMat.SetTextureScale("_BaseMap", Vector2.one);
                paletteMat.SetTextureOffset("_BaseMap", Vector2.zero);
            }

            if (hasLayout)
            {
                if (metallicTex != null && !string.IsNullOrEmpty(layout.metallicGlossMapProp))
                {
                    if (paletteMat.GetTexture(layout.metallicGlossMapProp) != metallicTex) paletteMat.SetTexture(layout.metallicGlossMapProp, metallicTex);
                    if (paletteMat.HasProperty(layout.metallicGlossMapProp))
                    {
                        paletteMat.SetTextureScale(layout.metallicGlossMapProp, Vector2.one);
                        paletteMat.SetTextureOffset(layout.metallicGlossMapProp, Vector2.zero);
                    }
                    if (paletteMat.HasProperty(layout.metallicProp)) paletteMat.SetFloat(layout.metallicProp, 1.0f);
                    if (paletteMat.HasProperty(layout.smoothnessProp)) paletteMat.SetFloat(layout.smoothnessProp, 1.0f);
                    if (paletteMat.HasProperty("_GlossMapScale")) paletteMat.SetFloat("_GlossMapScale", 1.0f);
                    if (paletteMat.HasProperty("_SmoothnessTextureChannel")) paletteMat.SetFloat("_SmoothnessTextureChannel", 0.0f);
                    if (!string.IsNullOrEmpty(layout.metallicGlossKeyword)) paletteMat.EnableKeyword(layout.metallicGlossKeyword);
                }

                if (emissionTex != null && !string.IsNullOrEmpty(layout.emissionMapProp))
                {
                    if (paletteMat.GetTexture(layout.emissionMapProp) != emissionTex) paletteMat.SetTexture(layout.emissionMapProp, emissionTex);
                    if (paletteMat.HasProperty(layout.emissionMapProp))
                    {
                        paletteMat.SetTextureScale(layout.emissionMapProp, Vector2.one);
                        paletteMat.SetTextureOffset(layout.emissionMapProp, Vector2.zero);
                    }
                    if (paletteMat.HasProperty(layout.emissionProp)) paletteMat.SetColor(layout.emissionProp, Color.white);
                    if (paletteMat.HasProperty("_EmissionStrength")) paletteMat.SetFloat("_EmissionStrength", 1.0f);
                    if (!string.IsNullOrEmpty(layout.emissionKeyword)) paletteMat.EnableKeyword(layout.emissionKeyword);
                }
                else if (paletteMat.HasProperty(layout.emissionProp))
                {
                    paletteMat.SetColor(layout.emissionProp, Color.black);
                    if (paletteMat.HasProperty("_EmissionStrength")) paletteMat.SetFloat("_EmissionStrength", 0f);
                    if (!string.IsNullOrEmpty(layout.emissionKeyword)) paletteMat.DisableKeyword(layout.emissionKeyword);
                }
            }
        }

        private Material CreatePaletteMaterial(Material baseMat, Texture2D albedoTex, Texture2D metallicTex, Texture2D emissionTex, ShaderPropertyLayout layout, bool hasLayout, string suffix)
        {
            Material paletteMat = UnityEngine.Object.Instantiate(baseMat);
            paletteMat.name = baseMat.shader.name.Replace("/", "_") + suffix;
            paletteMat.enableInstancing = true;

            string[] colorProps = { "_Color", "_BaseColor", "_MainColor", "_ColorTint" };
            foreach (var prop in colorProps)
            {
                if (paletteMat.HasProperty(prop))
                {
                    paletteMat.SetColor(prop, Color.white);
                }
            }

            ApplyPaletteTexturesToMaterial(paletteMat, albedoTex, metallicTex, emissionTex, layout, hasLayout);

            return paletteMat;
        }

        private int ProcessRenderersForGroup(
            List<Renderer> targetRenderers, 
            List<Material> group, 
            Material paletteMat, 
            Dictionary<Material, MaterialMapping> mappings, 
            string meshHashPrefix, 
            string meshSuffix, 
            string masterHash, 
            Dictionary<MeshCacheKey, Mesh> optimizedMeshCache, 
            bool verbose)
        {
            var materialsInGroup = new HashSet<Material>(group);
            int objectsOptimized = 0;

            foreach (var renderer in targetRenderers)
            {
                if (renderer == null || IsEditorOnly(renderer.transform) || SynProtectionData.IsProtected(renderer.gameObject)) continue;
                if (!(renderer is MeshRenderer)) continue;
                if (ShouldExcludeRenderer(renderer)) continue;

                var mats = renderer.sharedMaterials;
                if (mats == null) continue;

                bool hasTargetMaterial = false;
                foreach (var mat in mats)
                {
                    if (mat != null && !SynProtectionData.IsProtected(mat) && materialsInGroup.Contains(mat))
                    {
                        hasTargetMaterial = true;
                        break;
                    }
                }
                if (!hasTargetMaterial) continue;

                Mesh originalMesh = this.GetMesh(renderer);
                if (originalMesh == null || SynProtectionData.IsProtected(originalMesh)) continue;

                var cacheKey = new MeshCacheKey(originalMesh, mats, mappings);
                Mesh targetMesh = null;
                string palMeshHash = SynAssetCache.ComputeCompositeHash(meshHashPrefix, masterHash, SynAssetCache.GetAssetIdentityHash(originalMesh), cacheKey.MappingSignature);

                if (optimizedMeshCache.TryGetValue(cacheKey, out var cachedMesh))
                {
                    targetMesh = cachedMesh;
                }
                else if (SynAssetCache.TryGetCachedAsset<Mesh>(SynAssetCache.MeshesCategory, palMeshHash, out var diskCachedMesh))
                {
                    targetMesh = diskCachedMesh;
                    optimizedMeshCache[cacheKey] = diskCachedMesh;
                }
                else
                {
                    Mesh clonedMesh = UnityEngine.Object.Instantiate(originalMesh);
                    clonedMesh.name = originalMesh.name + meshSuffix;

                    try
                    {
                        SplitSubmeshVertices(clonedMesh);
                    }
                    catch (Exception meshEx)
                    {
                        Debug.LogError($"[Palette Remap Crash] Failed vertex split on shared mesh '{originalMesh.name}' via object '{renderer.gameObject.name}': {meshEx.Message}");
                        UnityEngine.Object.DestroyImmediate(clonedMesh);
                        continue;
                    }

                    var uvs = new List<Vector4>();
                    clonedMesh.GetUVs(0, uvs);
                    if (uvs.Count != clonedMesh.vertexCount)
                    {
                        uvs = new List<Vector4>(new Vector4[clonedMesh.vertexCount]);
                    }

                    int subMeshCount = clonedMesh.subMeshCount;
                    bool meshModified = false;

                    for (int sub = 0; sub < subMeshCount; sub++)
                    {
                        if (sub >= mats.Length) break;
                        Material slotMat = mats[sub];

                        if (slotMat != null && materialsInGroup.Contains(slotMat) && mappings.TryGetValue(slotMat, out var mapping))
                        {
                            Vector2 targetUV = mapping.PointUV;
                            int[] indices = clonedMesh.GetTriangles(sub);
                            foreach (int idx in indices)
                            {
                                if (idx >= 0 && idx < uvs.Count)
                                {
                                    Vector4 uvVal = uvs[idx];
                                    uvVal.x = targetUV.x;
                                    uvVal.y = targetUV.y;
                                    uvs[idx] = uvVal;
                                    meshModified = true;
                                }
                            }
                        }
                    }

                    if (meshModified)
                    {
                        SetMeshUVsPreservingDimension(clonedMesh, 0, uvs);
                        clonedMesh = SynAssetCache.SaveCachedAsset(clonedMesh, SynAssetCache.MeshesCategory, palMeshHash, $"{originalMesh.name}{meshSuffix}");
                        targetMesh = clonedMesh;
                        optimizedMeshCache[cacheKey] = clonedMesh;
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(clonedMesh);
                    }
                }

                if (targetMesh != null)
                {
                    this.SetMesh(renderer, targetMesh);
                    var newMats = new Material[mats.Length];
                    for (int slot = 0; slot < mats.Length; slot++)
                    {
                        Material slotMat = mats[slot];
                        if (slotMat != null && materialsInGroup.Contains(slotMat))
                        {
                            newMats[slot] = paletteMat;
                            if (verbose) Debug.Log($"[SynColorPaletteOptimizerPass] Verbose: Replaced material slot {slot} on renderer '{renderer.gameObject.name}' with '{paletteMat.name}'.");
                        }
                        else
                        {
                            newMats[slot] = slotMat;
                        }
                    }
                    renderer.sharedMaterials = newMats;
                    objectsOptimized++;
                }
            }

            return objectsOptimized;
        }

        private bool IsSolidColorMaterial(Material mat, bool ignoreInstanced, out Color color, out string reason)
        {
            color = Color.white;
            reason = "";
            if (mat == null) return false;

            if (SynProtectionData.IsProtected(mat))
            {
                reason = "Protected by user settings";
                return false;
            }

            if (ignoreInstanced && mat.enableInstancing)
            {
                reason = "GPU Instancing enabled";
                return false;
            }

            Shader shader = mat.shader;
            if (shader == null)
            {
                reason = "Shader is null";
                return false;
            }

            if (shader.name.StartsWith("Synthos/"))
            {
                reason = "Already optimized by SynScene Optimizer";
                return false;
            }

            if (mat.name.EndsWith("_StaticPalette") || mat.name.EndsWith("_NonStaticPalette") || mat.name.Contains("Palette_SceneMaster"))
            {
                reason = "Already an optimizer palette material";
                return false;
            }

            if (mat.HasProperty("_UseTextureArray") && mat.GetFloat("_UseTextureArray") > 0.0f)
            {
                reason = "Using texture array";
                return false;
            }

            if (HasVertexManipulation(mat))
            {
                reason = "Uses vertex manipulation/displacement/wind";
                return false;
            }

            if (!mat.HasProperty("_MainTex") && !mat.HasProperty("_BaseMap"))
            {
                reason = "Shader does not support _MainTex or _BaseMap";
                return false;
            }

            int propertyCount = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < propertyCount; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    string propName = ShaderUtil.GetPropertyName(shader, i);
                    if (UserTextureProperties.Contains(propName))
                    {
                        var tex = mat.GetTexture(propName);
                        if (tex != null && !IsInternalOrDummyTexture(propName, tex))
                        {
                            reason = $"Uses texture '{tex.name}' in property '{propName}'";
                            return false;
                        }
                    }
                }
            }

            // Skip materials with HDR emission (> 1.0) to prevent clamping their glow in 8-bit palette textures
            if (mat.HasProperty("_EmissionColor"))
            {
                Color em = mat.GetColor("_EmissionColor");
                float emStrength = mat.HasProperty("_EmissionStrength") ? mat.GetFloat("_EmissionStrength") : 1.0f;
                if ((em.r * emStrength > 1.001f || em.g * emStrength > 1.001f || em.b * emStrength > 1.001f) &&
                    (mat.IsKeywordEnabled("_EMISSION") || mat.IsKeywordEnabled("_EMISSIVE") || mat.IsKeywordEnabled("_EMISSION_ON")))
                {
                    reason = "Material has HDR emission (> 1.0) which cannot be preserved in an 8-bit texture atlas";
                    return false;
                }
            }

            string[] colorProps = { "_Color", "_BaseColor", "_MainColor", "_ColorTint" };
            foreach (var prop in colorProps)
            {
                if (mat.HasProperty(prop))
                {
                    color = mat.GetColor(prop);
                    return true;
                }
            }

            reason = "No color properties found";
            return false;
        }

        private bool HasVertexManipulation(Material mat)
        {
            if (mat == null || mat.shader == null) return false;

            string[] keywords = {
                "_VERTEX_MANIPULATION_ON",
                "_VERTEX_MANIPULATION",
                "_VERTEX_ANIMATION_ON",
                "_VERTEX_ANIM_ON",
                "_WIND_ON",
                "_DISPLACEMENT_ON",
                "VERTEX_MANIPULATION",
                "WIND"
            };

            foreach (var kw in keywords)
            {
                if (mat.IsKeywordEnabled(kw))
                {
                    return true;
                }
            }

            string[] floatToggles = {
                "_VertexManipulationToggle",
                "_VertexManipulation",
                "_VertexAnimation",
                "_VertexAnim",
                "_VertexAnimToggle",
                "_WindToggle",
                "_UseWind",
                "_EnableWind",
                "_DisplacementToggle",
                "_Displacement",
                "_VertexDeformToggle",
                "_VertexDeform"
            };

            foreach (var prop in floatToggles)
            {
                if (mat.HasProperty(prop))
                {
                    float val = mat.GetFloat(prop);
                    if (val > 0.0f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsInternalOrDummyTexture(string propName, Texture tex)
        {
            if (tex == null) return true;

            string lowerProp = propName.ToLowerInvariant();
            if (lowerProp.Contains("dfg") || 
                lowerProp.Contains("brdf") || 
                lowerProp.Contains("lut") || 
                lowerProp.Contains("sampler") ||
                lowerProp.Contains("internal") ||
                lowerProp.Contains("noisetex") ||
                lowerProp.Contains("dropletmask") ||
                lowerProp.Contains("rainsheet") ||
                lowerProp.Contains("windnoisetex"))
            {
                return true;
            }

            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path) || path == "unity default resources" || path == "unity_builtin_extra" || path.StartsWith("Library/"))
            {
                return true;
            }

            // Real project assets (Assets/ or Packages/) are ONLY dummy if they are tiny 1x1 or 2x2 solid pixels
            if (tex.width <= 2 && tex.height <= 2)
            {
                return true;
            }

            return false;
        }

        private List<List<Material>> GroupMaterials(Dictionary<Material, Color> eligibleMaterials, bool extremeMode)
        {
            var groups = new List<List<Material>>();
            bool groupByChannel = SynSceneOptimizerSettings.GetBool("PaletteAtlas_GroupByChannel", false);

            foreach (var mat in eligibleMaterials.Keys)
            {
                bool added = false;
                foreach (var group in groups)
                {
                    if (AreMaterialsCompatible(group[0], mat, extremeMode))
                    {
                        bool isCompatible = true;

                        if (groupByChannel && GetMaterialChannelLayoutSignature(group[0]) != GetMaterialChannelLayoutSignature(mat))
                        {
                            isCompatible = false;
                        }

                        if (isCompatible)
                        {
                            group.Add(mat);
                            added = true;
                            break;
                        }
                    }
                }
                if (!added)
                {
                    groups.Add(new List<Material> { mat });
                }
            }
            return groups;
        }

        private string GetMaterialChannelLayoutSignature(Material mat)
        {
            string shaderNameLower = mat.shader.name.ToLower();
            ShaderPropertyLayout layout = default;
            bool hasLayout = false;
            foreach (var kvp in ShaderLayouts)
            {
                if (shaderNameLower.Contains(kvp.Key))
                {
                    layout = kvp.Value;
                    hasLayout = true;
                    break;
                }
            }

            bool hasMetallic = false;
            bool hasEmission = false;

            if (hasLayout)
            {
                if (mat.HasProperty(layout.metallicProp) && mat.GetFloat(layout.metallicProp) > 0.0f)
                {
                    hasMetallic = true;
                }
                else if (!string.IsNullOrEmpty(layout.metallicGlossMapProp) && mat.HasProperty(layout.metallicGlossMapProp) && mat.GetTexture(layout.metallicGlossMapProp) != null)
                {
                    hasMetallic = true;
                }

                if (mat.HasProperty(layout.emissionProp))
                {
                    Color emissionColor = mat.GetColor(layout.emissionProp);
                    float strength = 1.0f;
                    if (mat.HasProperty("_EmissionStrength"))
                    {
                        strength = mat.GetFloat("_EmissionStrength");
                    }
                    if (emissionColor.r * strength > 0.001f || emissionColor.g * strength > 0.001f || emissionColor.b * strength > 0.001f)
                    {
                        bool keywordEnabled = true;
                        if (!string.IsNullOrEmpty(layout.emissionKeyword))
                        {
                            keywordEnabled = mat.IsKeywordEnabled(layout.emissionKeyword);
                        }
                        if (keywordEnabled)
                        {
                            hasEmission = true;
                        }
                    }
                }
            }

            return $"{(hasMetallic ? "M" : "")}_{(hasEmission ? "E" : "")}";
        }

        private static readonly HashSet<string> DynamicShaderKeywords = new HashSet<string>
        {
            "_EMISSION", "_METALLICGLOSSMAP", "_METALLICSPECGLOSSMAP", "_SPECGLOSSMAP",
            "_SPECULAR_HIGHLIGHTS_ON", "_SPECULAR_HIGHLIGHTS_OFF",
            "_GLOSSYREFLECTIONS_OFF", "_GLOSSYREFLECTIONS_ON",
            "_NORMALMAP", "_DETAIL_MULX2", "_PARALLAXMAP", "_OCCLUSIONMAP"
        };

        private static readonly HashSet<string> StandardBakeableProperties = new HashSet<string>
        {
            "_Color", "_BaseColor", "_MainColor", "_ColorTint",
            "_Metallic", "_Glossiness", "_GlossMapScale", "_Smoothness",
            "_MetallicStrength", "_RoughnessStrength",
            "_PackedRoughnessStrength", "_PackedMetallicStrength", "_PackedOcclusionStrength",
            "_EmissionColor", "_EmissionStrength"
        };

        private bool AreMaterialsCompatible(Material a, Material b, bool extremeMode)
        {
            if (a == null || b == null) return false;
            if (a.shader != b.shader) return false;

            // Compare Render Queue category (Opaque vs Cutout vs Transparent)
            int GetQueueCategory(Material m)
            {
                if (m.HasProperty("_BlendMode"))
                {
                    int bm = (int)m.GetFloat("_BlendMode");
                    if (bm == 0) return 0; // Opaque
                    if (bm == 1) return 1; // Cutout
                    return 2; // Transparent
                }
                if (m.HasProperty("_Mode"))
                {
                    int bm = (int)m.GetFloat("_Mode");
                    if (bm == 0) return 0; // Opaque
                    if (bm == 1) return 1; // Cutout
                    return 2; // Transparent
                }
                int q = m.renderQueue;
                if (q <= 2450) return 0; // Opaque (e.g. 1900, 2000)
                if (q <= 2500) return 1; // Cutout
                return 2; // Transparent
            }

            if (GetQueueCategory(a) != GetQueueCategory(b)) return false;

            // Compare Critical Render Pipeline properties (Cull / Double-Sided)
            if (a.HasProperty("_Cull") && b.HasProperty("_Cull"))
            {
                if (a.GetFloat("_Cull") != b.GetFloat("_Cull")) return false;
            }
            if (a.HasProperty("_DoubleSidedGI") && b.HasProperty("_DoubleSidedGI"))
            {
                if (a.GetFloat("_DoubleSidedGI") != b.GetFloat("_DoubleSidedGI")) return false;
            }

            return true;
        }

        private void SplitSubmeshVertices(Mesh mesh)
        {
            if (mesh.subMeshCount <= 1) return; // Skip if single submesh

            var originalVertices = mesh.vertices;
            var originalNormals = mesh.normals;
            var originalTangents = mesh.tangents;
            var originalColors = mesh.colors;
            
            var originalUVs = new List<Vector4>[8];
            for (int i = 0; i < 8; i++)
            {
                originalUVs[i] = new List<Vector4>();
                mesh.GetUVs(i, originalUVs[i]);
            }

            var newVertices = new List<Vector3>();
            var newNormals = new List<Vector3>();
            var newTangents = new List<Vector4>();
            var newColors = new List<Color>();
            
            var newUVs = new List<Vector4>[8];
            for (int i = 0; i < 8; i++)
            {
                newUVs[i] = new List<Vector4>();
            }

            int subMeshCount = mesh.subMeshCount;
            var newSubmeshTriangles = new List<int[]>();

            for (int sub = 0; sub < subMeshCount; sub++)
            {
                int[] triangles = mesh.GetTriangles(sub);
                int[] newTriangles = new int[triangles.Length];
                var oldToNewIndex = new Dictionary<int, int>();

                for (int i = 0; i < triangles.Length; i++)
                {
                    int oldIdx = triangles[i];
                    if (!oldToNewIndex.TryGetValue(oldIdx, out int newIdx))
                    {
                        newIdx = newVertices.Count;
                        oldToNewIndex[oldIdx] = newIdx;

                        newVertices.Add(originalVertices[oldIdx]);
                        
                        if (originalNormals != null && originalNormals.Length > oldIdx)
                            newNormals.Add(originalNormals[oldIdx]);
                        
                        if (originalTangents != null && originalTangents.Length > oldIdx)
                            newTangents.Add(originalTangents[oldIdx]);
                        
                        if (originalColors != null && originalColors.Length > oldIdx)
                            newColors.Add(originalColors[oldIdx]);

                        for (int uvChan = 0; uvChan < 8; uvChan++)
                        {
                            if (originalUVs[uvChan].Count > oldIdx)
                            {
                                newUVs[uvChan].Add(originalUVs[uvChan][oldIdx]);
                            }
                        }
                    }
                    newTriangles[i] = newIdx;
                }
                newSubmeshTriangles.Add(newTriangles);
            }

            mesh.Clear();
            if (newVertices.Count > 65535)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }
            mesh.vertices = newVertices.ToArray();
            
            if (newNormals.Count == newVertices.Count) mesh.normals = newNormals.ToArray();
            if (newTangents.Count == newVertices.Count) mesh.tangents = newTangents.ToArray();
            if (newColors.Count == newVertices.Count) mesh.colors = newColors.ToArray();
            
            for (int uvChan = 0; uvChan < 8; uvChan++)
            {
                if (newUVs[uvChan].Count == newVertices.Count)
                {
                    SetMeshUVsPreservingDimension(mesh, uvChan, newUVs[uvChan]);
                }
            }

            mesh.subMeshCount = subMeshCount;
            for (int sub = 0; sub < subMeshCount; sub++)
            {
                mesh.SetTriangles(newSubmeshTriangles[sub], sub);
            }
        }

        private void SetMeshUVsPreservingDimension(Mesh mesh, int channel, List<Vector4> uvs)
        {
            if (uvs == null || uvs.Count == 0) return;

            bool hasZ = false;
            bool hasW = false;
            for (int i = 0; i < uvs.Count; i++)
            {
                if (Mathf.Abs(uvs[i].z) > 0.0001f) hasZ = true;
                if (Mathf.Abs(uvs[i].w) > 0.0001f) hasW = true;
                if (hasW) break;
            }

            if (hasW)
            {
                mesh.SetUVs(channel, uvs);
            }
            else if (hasZ)
            {
                var uvs3 = new List<Vector3>(uvs.Count);
                for (int i = 0; i < uvs.Count; i++) uvs3.Add(new Vector3(uvs[i].x, uvs[i].y, uvs[i].z));
                mesh.SetUVs(channel, uvs3);
            }
            else
            {
                var uvs2 = new List<Vector2>(uvs.Count);
                for (int i = 0; i < uvs.Count; i++) uvs2.Add(new Vector2(uvs[i].x, uvs[i].y));
                mesh.SetUVs(channel, uvs2);
            }
        }

        private Mesh GetMesh(Renderer renderer)
        {
            if (renderer is MeshRenderer mr)
            {
                var filter = mr.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            }
            if (renderer is SkinnedMeshRenderer smr)
            {
                return smr.sharedMesh;
            }
            return null;
        }

        private void SetMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is MeshRenderer mr)
            {
                var filter = mr.GetComponent<MeshFilter>();
                if (filter != null)
                {
                    Mesh originalMesh = filter.sharedMesh;
                    filter.sharedMesh = mesh;

                    var collider = mr.GetComponent<MeshCollider>();
                    if (collider != null && collider.sharedMesh == originalMesh)
                    {
                        collider.sharedMesh = mesh;
                    }
                }
            }
            else if (renderer is SkinnedMeshRenderer smr)
            {
                Mesh originalMesh = smr.sharedMesh;
                smr.sharedMesh = mesh;

                var collider = smr.GetComponent<MeshCollider>();
                if (collider != null && collider.sharedMesh == originalMesh)
                {
                    collider.sharedMesh = mesh;
                }
            }
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            bool groupByChannel = SynSceneOptimizerSettings.GetBool("PaletteAtlas_GroupByChannel", false);
            bool newGroupByChannel = EditorGUILayout.Toggle(new GUIContent("Group By Channel Layout", "If checked, groups solid color materials based on their properties (metallic, emission) to prevent generating unused/empty emission or metallic palette maps. Highly recommended!"), groupByChannel);
            if (newGroupByChannel != groupByChannel)
            {
                SynSceneOptimizerSettings.SetBool("PaletteAtlas_GroupByChannel", newGroupByChannel);
            }

            bool logOpt = SynSceneOptimizerSettings.GetBool("PaletteAtlas_LogOptimized", true);
            bool newLogOpt = EditorGUILayout.Toggle(new GUIContent("Log Merged Materials", "If checked, prints details about which solid color materials were merged during build/play mode."), logOpt);
            if (newLogOpt != logOpt)
            {
                SynSceneOptimizerSettings.SetBool("PaletteAtlas_LogOptimized", newLogOpt);
            }

            bool optStatic = SynSceneOptimizerSettings.GetBool("PaletteAtlas_OptimizeStatic", true);
            bool newOptStatic = EditorGUILayout.Toggle(new GUIContent("Optimize Static Objects", "Optimize static renderers into palette materials for static batching and lightmapping."), optStatic);
            if (newOptStatic != optStatic)
            {
                SynSceneOptimizerSettings.SetBool("PaletteAtlas_OptimizeStatic", newOptStatic);
            }

            bool optNonStatic = SynSceneOptimizerSettings.GetBool("PaletteAtlas_OptimizeNonStatic", true);
            bool newOptNonStatic = EditorGUILayout.Toggle(new GUIContent("Optimize Dynamic / Non-Static Objects", "Optimize non-static / interactive objects into separate dynamic palette materials."), optNonStatic);
            if (newOptNonStatic != optNonStatic)
            {
                SynSceneOptimizerSettings.SetBool("PaletteAtlas_OptimizeNonStatic", newOptNonStatic);
            }

            bool ignoreInstanced = SynSceneOptimizerSettings.GetBool("PaletteAtlas_IgnoreInstanced", true);
            bool newIgnoreInstanced = EditorGUILayout.Toggle(new GUIContent("Ignore Instanced Materials", "Skip materials that already have GPU Instancing enabled to preserve their instancing."), ignoreInstanced);
            if (newIgnoreInstanced != ignoreInstanced)
            {
                SynSceneOptimizerSettings.SetBool("PaletteAtlas_IgnoreInstanced", newIgnoreInstanced);
            }

            bool extremeMode = SynSceneOptimizerSettings.GetBool("PaletteAtlas_ExtremeMode", false);
            bool newExtremeMode = EditorGUILayout.Toggle(new GUIContent("Force Extreme Channel Baking", "Ignore float slider discrepancies (like roughness, metallic, emission) across materials and procedurally bake them into texture maps instead, forcing material multipliers to 1.0."), extremeMode);
            if (newExtremeMode != extremeMode)
            {
                SynSceneOptimizerSettings.SetBool("PaletteAtlas_ExtremeMode", newExtremeMode);
            }

            string ignoreMaterials = SynSceneOptimizerSettings.GetString("PaletteAtlas_IgnoreMaterials", "");
            string newIgnoreMaterials = EditorGUILayout.TextField(new GUIContent("Ignore Materials (comma-separated)", "Materials containing any of these comma-separated fragments will be skipped by the optimizer."), ignoreMaterials);
            if (newIgnoreMaterials != ignoreMaterials)
            {
                SynSceneOptimizerSettings.SetString("PaletteAtlas_IgnoreMaterials", newIgnoreMaterials);
            }
        }

        private class MaterialMapping
        {
            public Vector2 PointUV;
        }

        private struct MeshCacheKey : IEquatable<MeshCacheKey>
        {
            public Mesh OriginalMesh;
            public string MappingSignature;

            public MeshCacheKey(Mesh mesh, Material[] materials, Dictionary<Material, MaterialMapping> mappings)
            {
                OriginalMesh = mesh;
                var sb = new System.Text.StringBuilder();
                int subCount = mesh.subMeshCount;
                for (int i = 0; i < subCount; i++)
                {
                    if (i < materials.Length && materials[i] != null && mappings.TryGetValue(materials[i], out var mapping))
                    {
                        sb.Append($"P:{mapping.PointUV.x:F6},{mapping.PointUV.y:F6}|");
                    }
                    else
                    {
                        sb.Append("N|");
                    }
                }
                MappingSignature = sb.ToString();
            }

            public bool Equals(MeshCacheKey other)
            {
                return OriginalMesh == other.OriginalMesh && MappingSignature == other.MappingSignature;
            }

            public override bool Equals(object obj)
            {
                return obj is MeshCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 23 + (OriginalMesh != null ? OriginalMesh.GetHashCode() : 0);
                    hash = hash * 23 + (MappingSignature != null ? MappingSignature.GetHashCode() : 0);
                    return hash;
                }
            }
        }
    }
}
