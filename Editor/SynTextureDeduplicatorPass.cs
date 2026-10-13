using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Points materials at one copy of textures that are exact duplicates: the same source file contents and the
    /// same import settings, so the build would hold the same data twice (e.g. an asset pack imported five times).
    /// Only the build copies of materials the optimizer may change are redirected. A duplicate that something else
    /// still uses (protected or video objects, UI, sprites, scripts) becomes the copy the others point at.
    /// Runs after the Texture Complexity Compressor, so identical downscaled copies are merged too.
    ///
    /// TextMesh Pro font atlases live inside their font asset and text components point at the font, not the
    /// atlas, so identical font assets (e.g. a package shipping its own copy of LiberationSans SDF) are merged by
    /// pointing the text components at one font and a matching material.
    /// </summary>
    public class SynTextureDeduplicatorPass : SynOptimizationPass
    {
        public override string Id => "SynTextureDeduplicatorPass";
        public override string Name => "Texture Deduplicator";
        public override string Description => "Finds textures that are exact copies of each other (same image file contents and import settings) and points materials at one copy, so the build holds it once.";
        public override string Category => "Memory & Assets";
        public override int Priority => 47; // After the Texture Complexity Compressor (45), before palettes (50)
        public override SynProtectionTargets ProtectionTargets => SynProtectionTargets.Objects | SynProtectionTargets.Materials | SynProtectionTargets.Textures;
        public override string ProtectionEffect => "Swaps the material for a copy that uses one shared copy of identical textures, and points text at one copy of identical fonts.";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            DeduplicateFonts(scene);

            // Every texture slot on materials whose build copy the optimizer may change, by its current texture
            var slots = new List<(SynVirtualMaterialState state, string property, Texture2D texture)>();
            var processed = new HashSet<Material>();
            foreach (Renderer r in renderers)
            {
                if (r == null || SynSceneQuery.IsVideoComponentDetected(r)) continue;
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat == null || !processed.Add(mat) || SynProtectionData.IsProtected(mat)) continue;
                    SynVirtualMaterialState state = SynPipelineCompactor.GetStagingState(mat);
                    if (state == null) continue;
                    foreach (var kvp in state.TrackedTextures)
                    {
                        if (kvp.Value is Texture2D tex) slots.Add((state, kvp.Key, tex));
                    }
                }
            }

            // Group by content: textures with the same key are byte-identical in the build
            var keys = new Dictionary<Texture2D, string>();
            var groups = new Dictionary<string, List<Texture2D>>();
            foreach (var (_, _, tex) in slots)
            {
                if (keys.ContainsKey(tex)) continue;
                string key = GetContentKey(tex);
                keys[tex] = key;
                if (key == null) continue;
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = new List<Texture2D>();
                group.Add(tex);
            }

            // Textures used where materials can't be redirected keep shipping, so they are the copy to keep
            HashSet<Texture2D> pinned = SynSmartTextureVRAMOptimizerPass.CollectTexturesUsedOutsideRedirectedRenderers(scene, renderers);
            foreach (Material mat in processed)
            {
                if (!SynProtectionData.IsProtected(mat) || mat.shader == null) continue;
                foreach (string property in mat.GetTexturePropertyNames())
                {
                    if (mat.GetTexture(property) is Texture2D tex) pinned.Add(tex);
                }
            }

            var replacement = new Dictionary<Texture2D, Texture2D>();
            foreach (List<Texture2D> group in groups.Values)
            {
                if (group.Count < 2) continue;
                group.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
                Texture2D keep = group.Find(t => pinned.Contains(t)) ?? group[0];
                // A copy that is also used elsewhere ships anyway, so redirecting away from it gains nothing
                foreach (Texture2D tex in group)
                {
                    if (tex != keep && !pinned.Contains(tex)) replacement[tex] = keep;
                }
            }
            if (replacement.Count == 0) return;

            int slotCount = 0;
            foreach (var (state, property, tex) in slots)
            {
                if (!replacement.TryGetValue(tex, out Texture2D keep)) continue;
                state.TrackedTextures[property] = keep;
                state.IsDirty = true;
                if (!state.AppliedPassTags.Contains("TexDedup")) state.AppliedPassTags.Add("TexDedup");
                slotCount++;
            }

            long savedBytes = 0;
            var names = new List<string>();
            foreach (Texture2D tex in replacement.Keys)
            {
                savedBytes += SynLightingTextureLimitsPass.EstimateBytes(tex);
                if (names.Count < 5 && !names.Contains(tex.name)) names.Add(tex.name);
            }

            string msg = $"Merged {replacement.Count} duplicate texture(s) into the copies they match ({slotCount} material slots; saved est. ~{savedBytes / (1024.0 * 1024.0):F1} MB VRAM): {string.Join(", ", names)}{(replacement.Count > names.Count ? ", ..." : "")}.";
            Debug.Log($"[SYN SCENE OPTIMIZER] Texture Deduplicator: {msg}");
            SynPipelineCompactor.LogChange(Name, msg);
        }

        // ---- TextMesh Pro fonts ----

        private const string FontAssetTypeName = "TMP_FontAsset";

        private static bool IsFontAsset(UnityEngine.Object obj) => obj is ScriptableObject && obj.GetType().Name == FontAssetTypeName;

        // Text component fields this step rewrites; any other reference to a font, its material or atlas keeps it
        private static bool IsTextFontField(string propertyPath)
        {
            return propertyPath == "m_fontAsset" || propertyPath == "m_sharedMaterial" || propertyPath.StartsWith("m_fontSharedMaterials.", StringComparison.Ordinal);
        }

        private void DeduplicateFonts(Scene scene)
        {
            var textComponents = new List<Component>();
            var rewritable = new HashSet<GameObject>();
            var fontUses = new Dictionary<UnityEngine.Object, int>();
            var allComponents = new List<Component>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform) continue;
                    allComponents.Add(component);
                    var font = new SerializedObject(component).FindProperty("m_fontAsset");
                    if (font == null || font.propertyType != SerializedPropertyType.ObjectReference || !IsFontAsset(font.objectReferenceValue)) continue;
                    if (SynProtectionData.IsProtected(component.gameObject)) continue;
                    textComponents.Add(component);
                    rewritable.Add(component.gameObject);
                    fontUses.TryGetValue(font.objectReferenceValue, out int uses);
                    fontUses[font.objectReferenceValue] = uses + 1;
                }
            }
            if (fontUses.Count < 2) return;

            // Fonts, font materials and atlases something else references (scripts, protected text, fallback tables,
            // renderers that are not text) keep shipping
            var pinned = new HashSet<UnityEngine.Object>();
            foreach (Component component in allComponents)
            {
                bool isText = textComponents.Contains(component);
                bool isTextRenderer = component is Renderer && rewritable.Contains(component.gameObject);
                SerializedProperty iterator = new SerializedObject(component).GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference || iterator.objectReferenceValue == null) continue;
                    if (isText && IsTextFontField(iterator.propertyPath)) continue;
                    if (isTextRenderer && iterator.propertyPath.StartsWith("m_Materials.", StringComparison.Ordinal)) continue;
                    pinned.Add(iterator.objectReferenceValue);
                }
            }
            foreach (UnityEngine.Object font in fontUses.Keys)
            {
                SerializedProperty iterator = new SerializedObject(font).GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference && IsFontAsset(iterator.objectReferenceValue) && iterator.objectReferenceValue != font)
                        pinned.Add(iterator.objectReferenceValue);
                }
            }

            // Group identical fonts; the copy to keep is one that ships anyway, else the most used
            var groups = new Dictionary<string, List<UnityEngine.Object>>();
            foreach (UnityEngine.Object font in fontUses.Keys)
            {
                string key = GetFontKey(font);
                if (key == null) continue;
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = new List<UnityEngine.Object>();
                group.Add(font);
            }

            var replacement = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;
                group.Sort((a, b) => fontUses[b] != fontUses[a] ? fontUses[b].CompareTo(fontUses[a]) : string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
                UnityEngine.Object keep = group.Find(IsFontPinned(pinned)) ?? group[0];
                foreach (UnityEngine.Object font in group)
                {
                    if (font != keep && !IsFontPinned(pinned)(font)) replacement[font] = keep;
                }
            }
            if (replacement.Count == 0) return;

            int changed = 0;
            int skipped = 0;
            var materialCopies = new Dictionary<(Material, UnityEngine.Object), Material>();
            foreach (Component component in textComponents)
            {
                var so = new SerializedObject(component);
                SerializedProperty fontProp = so.FindProperty("m_fontAsset");
                if (!replacement.TryGetValue(fontProp.objectReferenceValue, out UnityEngine.Object keep)) continue;
                UnityEngine.Object old = fontProp.objectReferenceValue;

                // Every material the text uses must move to the kept atlas, or the text is left exactly as it is
                var materialRemap = new Dictionary<Material, Material>();
                SerializedProperty shared = so.FindProperty("m_sharedMaterial");
                SerializedProperty sharedList = so.FindProperty("m_fontSharedMaterials");
                var used = new List<Material>();
                if (shared != null && shared.objectReferenceValue is Material sharedMaterial) used.Add(sharedMaterial);
                if (sharedList != null && sharedList.isArray)
                {
                    for (int i = 0; i < sharedList.arraySize; i++)
                    {
                        if (sharedList.GetArrayElementAtIndex(i).objectReferenceValue is Material listed) used.Add(listed);
                    }
                }
                bool mapped = true;
                foreach (Material material in used)
                {
                    if (materialRemap.ContainsKey(material)) continue;
                    Material match = GetMatchingFontMaterial(material, old, keep, materialCopies);
                    if (match == null)
                    {
                        mapped = false;
                        break;
                    }
                    materialRemap[material] = match;
                }
                if (!mapped)
                {
                    skipped++;
                    continue;
                }

                fontProp.objectReferenceValue = keep;
                if (shared != null && shared.objectReferenceValue is Material currentShared) shared.objectReferenceValue = materialRemap[currentShared];
                if (sharedList != null && sharedList.isArray)
                {
                    for (int i = 0; i < sharedList.arraySize; i++)
                    {
                        SerializedProperty element = sharedList.GetArrayElementAtIndex(i);
                        if (element.objectReferenceValue is Material listed) element.objectReferenceValue = materialRemap[listed];
                    }
                }
                so.ApplyModifiedPropertiesWithoutUndo();

                // 3D text draws through a MeshRenderer that holds the same material
                // Unity's null check: GetComponent returns a "missing" placeholder in the editor, which `is` accepts
                Renderer renderer = component.GetComponent<Renderer>();
                if (renderer != null)
                {
                    Material[] materials = renderer.sharedMaterials;
                    bool swapped = false;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] != null && materialRemap.TryGetValue(materials[i], out Material remapped))
                        {
                            materials[i] = remapped;
                            swapped = true;
                        }
                    }
                    if (swapped) renderer.sharedMaterials = materials;
                }
                changed++;
            }

            long savedBytes = 0;
            var names = new List<string>();
            foreach (UnityEngine.Object font in replacement.Keys)
            {
                foreach (Texture atlas in GetAtlases(font)) savedBytes += SynLightingTextureLimitsPass.EstimateBytes(atlas);
                names.Add($"{font.name} ({AssetDatabase.GetAssetPath(font)})");
            }
            if (changed == 0) return;
            string msg = $"Merged {replacement.Count} duplicate TextMesh Pro font(s) into the copies they match ({changed} text components; saved est. ~{savedBytes / (1024.0 * 1024.0):F1} MB VRAM): {string.Join(", ", names)}." +
                (skipped > 0 ? $" {skipped} text components kept their font because their material could not be moved to the matching atlas, so that copy still ships." : "");
            Debug.Log($"[SYN SCENE OPTIMIZER] Texture Deduplicator: {msg}");
            SynPipelineCompactor.LogChange(Name, msg);
        }

        private static Predicate<UnityEngine.Object> IsFontPinned(HashSet<UnityEngine.Object> pinned)
        {
            return font =>
            {
                if (pinned.Contains(font)) return true;
                if (GetFontMaterial(font) is Material material && pinned.Contains(material)) return true;
                foreach (Texture atlas in GetAtlases(font))
                {
                    if (pinned.Contains(atlas)) return true;
                }
                return false;
            };
        }

        // The kept font's own material when it looks the same, else a copy of the old material on the kept atlas.
        // Null when the material doesn't sample the old font's atlas (the text is then left alone).
        private static Material GetMatchingFontMaterial(Material oldMaterial, UnityEngine.Object oldFont, UnityEngine.Object keepFont, Dictionary<(Material, UnityEngine.Object), Material> copies)
        {
            Texture oldAtlas = GetAtlases(oldFont).Count > 0 ? GetAtlases(oldFont)[0] : null;
            Texture keepAtlas = GetAtlases(keepFont).Count > 0 ? GetAtlases(keepFont)[0] : null;
            if (keepAtlas == null || oldMaterial.shader == null || !oldMaterial.HasProperty("_MainTex") || oldMaterial.GetTexture("_MainTex") != oldAtlas) return null;

            Material keepMaterial = GetFontMaterial(keepFont) as Material;
            if (keepMaterial != null && MaterialsMatchExceptMainTex(oldMaterial, keepMaterial)) return keepMaterial;

            if (copies.TryGetValue((oldMaterial, keepFont), out Material existing)) return existing;
            string hash = SynAssetCache.ComputeCompositeHash("FontMaterialRemap_v1", AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(oldMaterial)), oldMaterial.name, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(keepFont)));
            if (!SynAssetCache.TryGetCachedAsset(SynAssetCache.MaterialsCategory, hash, out Material copy))
            {
                copy = new Material(oldMaterial);
                copy.SetTexture("_MainTex", keepAtlas);
                copy = SynAssetCache.SaveCachedAsset(copy, SynAssetCache.MaterialsCategory, hash, oldMaterial.name + "_FontDedup");
            }
            SynPipelineCompactor.RegisterGeneratedAsset(copy);
            copies[(oldMaterial, keepFont)] = copy;
            return copy;
        }

        private static bool MaterialsMatchExceptMainTex(Material a, Material b)
        {
            if (a.shader != b.shader) return false;
            var keywordsA = new HashSet<string>(a.shaderKeywords);
            if (!keywordsA.SetEquals(b.shaderKeywords)) return false;
            int count = ShaderUtil.GetPropertyCount(a.shader);
            for (int i = 0; i < count; i++)
            {
                string name = ShaderUtil.GetPropertyName(a.shader, i);
                switch (ShaderUtil.GetPropertyType(a.shader, i))
                {
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        if (name != "_MainTex" && a.GetTexture(name) != b.GetTexture(name)) return false;
                        break;
                    case ShaderUtil.ShaderPropertyType.Color:
                    case ShaderUtil.ShaderPropertyType.Vector:
                        if (a.GetVector(name) != b.GetVector(name)) return false;
                        break;
                    default:
                        if (!Mathf.Approximately(a.GetFloat(name), b.GetFloat(name))) return false;
                        break;
                }
            }
            return true;
        }

        private static UnityEngine.Object GetFontMaterial(UnityEngine.Object font)
        {
            return new SerializedObject(font).FindProperty("material")?.objectReferenceValue;
        }

        private static List<Texture> GetAtlases(UnityEngine.Object font)
        {
            var atlases = new List<Texture>();
            var so = new SerializedObject(font);
            SerializedProperty list = so.FindProperty("m_AtlasTextures");
            if (list != null && list.isArray)
            {
                for (int i = 0; i < list.arraySize; i++)
                {
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue is Texture atlas) atlases.Add(atlas);
                }
            }
            if (atlases.Count == 0 && so.FindProperty("atlas")?.objectReferenceValue is Texture legacy) atlases.Add(legacy);
            return atlases;
        }

        // Object references, and the GUID of the source font file the asset was generated from (copies of the same
        // font asset in different packages point at different copies of the .ttf), don't change how text renders
        private static readonly Regex InstanceIdPattern = new Regex(@"""instanceID"":-?\d+|""guid"":\s*""[0-9a-f]*""", RegexOptions.Compiled);

        // Glyph, character and face data plus the atlas pixels. Dynamic fonts add glyphs at runtime and are skipped.
        private static string GetFontKey(UnityEngine.Object font)
        {
            try
            {
                var so = new SerializedObject(font);
                SerializedProperty mode = so.FindProperty("m_AtlasPopulationMode");
                if (mode != null && mode.intValue != 0) return null;

                List<Texture> atlases = GetAtlases(font);
                if (atlases.Count == 0) return null;

                var key = new System.Text.StringBuilder(InstanceIdPattern.Replace(EditorJsonUtility.ToJson(font), ""));
                SerializedProperty fallbacks = so.FindProperty("m_FallbackFontAssetTable");
                if (fallbacks != null && fallbacks.isArray)
                {
                    for (int i = 0; i < fallbacks.arraySize; i++) key.Append("|fallback:").Append(fallbacks.GetArrayElementAtIndex(i).objectReferenceInstanceIDValue);
                }
                foreach (Texture atlas in atlases)
                {
                    key.Append('|').Append(atlas.width).Append('x').Append(atlas.height).Append(':').Append(atlas.graphicsFormat).Append(':').Append(HashPixels(atlas));
                }

                using (var md5 = MD5.Create())
                {
                    return Convert.ToBase64String(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key.ToString())));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Texture Deduplicator could not compare font '{font.name}': {e.Message}");
                return null;
            }
        }

        // Font atlases are usually not readable, so they are drawn into a render texture and read back
        private static string HashPixels(Texture tex)
        {
            RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            var readback = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                readback.Apply(false);
                using (var md5 = MD5.Create())
                {
                    return Convert.ToBase64String(md5.ComputeHash(readback.GetRawTextureData()));
                }
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        // Hash of the source file plus the import settings, or null for textures without an imported source file
        private static string GetContentKey(Texture2D tex)
        {
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is TextureImporter importer)) return null;
            if (SynProtectionData.IsProtected(tex) || SynProtectionData.IsBakeryAsset(path, tex)) return null;

            try
            {
                using (var md5 = MD5.Create())
                {
                    byte[] fileHash = md5.ComputeHash(File.ReadAllBytes(path));
                    byte[] settingsHash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(GetImportSettings(importer)));
                    return Convert.ToBase64String(fileHash) + "|" + Convert.ToBase64String(settingsHash);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Texture Deduplicator could not read '{path}': {e.Message}");
                return null;
            }
        }

        // Everything that changes the imported result: the importer's settings and every platform override
        private static string GetImportSettings(TextureImporter importer)
        {
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            var text = new System.Text.StringBuilder(EditorJsonUtility.ToJson(settings));
            foreach (string platform in new[] { "DefaultTexturePlatform", "Standalone", "Android", "iPhone" })
            {
                text.Append('|').Append(EditorJsonUtility.ToJson(importer.GetPlatformTextureSettings(platform)));
            }
            return text.ToString();
        }
    }
}
