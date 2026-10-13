using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
    /// </summary>
    public class SynTextureDeduplicatorPass : SynOptimizationPass
    {
        public override string Id => "SynTextureDeduplicatorPass";
        public override string Name => "Texture Deduplicator";
        public override string Description => "Finds textures that are exact copies of each other (same image file contents and import settings) and points materials at one copy, so the build holds it once.";
        public override string Category => "Memory & Assets";
        public override int Priority => 47; // After the Texture Complexity Compressor (45), before palettes (50)

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
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
                foreach (Texture2D tex in group)
                {
                    if (tex != keep) replacement[tex] = keep;
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

            // Only duplicates nothing else uses leave the build
            long savedBytes = 0;
            int dropped = 0;
            var names = new List<string>();
            foreach (Texture2D tex in replacement.Keys)
            {
                if (pinned.Contains(tex)) continue;
                dropped++;
                savedBytes += SynLightingTextureLimitsPass.EstimateBytes(tex);
                if (names.Count < 5 && !names.Contains(tex.name)) names.Add(tex.name);
            }

            string msg = $"Pointed {slotCount} material slot(s) at one copy of {replacement.Count} duplicate texture(s); {dropped} copies leave the build (saved est. ~{savedBytes / (1024.0 * 1024.0):F1} MB VRAM){(names.Count > 0 ? ": " + string.Join(", ", names) : "")}.";
            Debug.Log($"[SYN SCENE OPTIMIZER] Texture Deduplicator: {msg}");
            SynPipelineCompactor.LogChange(Name, msg);
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
