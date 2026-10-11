using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Packs the sprites of UI Images into shared atlases so a panel's images can draw in one batch instead of one
    /// per texture. Each atlas is a PNG in the cache imported as a normal multi-sprite texture, so Unity compresses
    /// it and keeps 9-slice borders and pivots. Only the build/Play Mode/preview copy of the scene is changed.
    /// </summary>
    public static class SynUISpriteAtlas
    {
        private const string AtlasHashTag = "UIAtlas_v3"; // v3: full sprite rect kept (tight sprites' trimmed margins restored); v2: rectangular atlases
        private const int Padding = 4;      // gap around each sprite, half of it filled with copied edge pixels
        private const int Extrude = 2;
        public const int MaxSpriteSize = 512;

        public class Result
        {
            public int Atlases;
            public int SpritesPacked;
            public int ImagesChanged;
            public int SpritesSkipped;
            public int ReferencesRewritten;

            // Why sprites were left alone, e.g. "tiled image" -> 4
            public Dictionary<string, int> SkipReasons = new Dictionary<string, int>();

            public string DescribeSkips() => string.Join(", ", SkipReasons.OrderByDescending(kvp => kvp.Value).Select(kvp => $"{kvp.Value} {kvp.Key}"));

            internal void AddSkip(string reason, int count = 1)
            {
                if (count <= 0) return;
                SkipReasons.TryGetValue(reason, out int current);
                SkipReasons[reason] = current + count;
                SpritesSkipped += count;
            }
        }

        private class SpriteGroupKey
        {
            public FilterMode Filter;
            public bool Srgb;
            public bool Compressed;
            public float PixelsPerUnit;

            public override bool Equals(object obj) => obj is SpriteGroupKey k && k.Filter == Filter && k.Srgb == Srgb && k.Compressed == Compressed && Mathf.Approximately(k.PixelsPerUnit, PixelsPerUnit);
            public override int GetHashCode() => ((int)Filter * 397) ^ (Srgb ? 1 : 0) ^ (Compressed ? 2 : 0) ^ PixelsPerUnit.GetHashCode();
            public override string ToString() => $"f{(int)Filter}_s{(Srgb ? 1 : 0)}_c{(Compressed ? 1 : 0)}_p{PixelsPerUnit:0.###}";
        }

        private class Placement
        {
            public Sprite Sprite;
            public RectInt Rect;    // where the sprite's pixels go in the atlas (padding excluded)
        }

        private class AtlasLayout
        {
            public List<Placement> Placements;
            public int Width;
            public int Height;
        }

        /// <param name="rewriteScriptReferences">
        /// Also pack sprites that scripts, Udon variables and button sprite swaps hold, and point those references at
        /// the atlas copies, so swaps keep batching and sprite comparisons in scripts still match.
        /// </param>
        public static Result Run(Scene scene, int maxAtlasSize, bool rewriteScriptReferences)
        {
            var result = new Result();
            var spriteUsers = CollectEligibleSprites(scene, rewriteScriptReferences, out var excluded);
            var fullRemap = new Dictionary<Sprite, Sprite>();
            foreach (string reason in excluded.Values) result.AddSkip(reason);
            if (spriteUsers.Count < 2)
            {
                result.AddSkip("with no other sprite to share an atlas with", spriteUsers.Count);
                return result;
            }

            // Sprites with matching texture settings can share an atlas; order them by canvas so a panel's
            // sprites tend to land in the same atlas
            var groups = spriteUsers.Keys
                .GroupBy(GetGroupKey)
                .ToList();

            foreach (var group in groups)
            {
                var sprites = group
                    .OrderByDescending(s => Mathf.CeilToInt(s.rect.height))
                    .ThenBy(s => GetFirstCanvasPath(spriteUsers[s]))
                    .ToList();

                foreach (AtlasLayout atlasLayout in Pack(sprites, maxAtlasSize))
                {
                    if (atlasLayout.Placements.Count < 2) continue; // a one-sprite atlas saves nothing

                    Dictionary<Sprite, Sprite> remap = BuildAtlas(atlasLayout, group.Key);
                    if (remap == null) continue;

                    result.Atlases++;
                    result.SpritesPacked += remap.Count;
                    foreach (var kvp in remap)
                    {
                        fullRemap[kvp.Key] = kvp.Value;
                        foreach (Image image in spriteUsers[kvp.Key])
                        {
                            image.sprite = kvp.Value;
                            result.ImagesChanged++;
                        }
                    }
                }
            }

            if (rewriteScriptReferences && fullRemap.Count > 0)
            {
                result.ReferencesRewritten = RewriteScriptReferences(scene, fullRemap);
            }

            // Eligible but unpacked: alone in their settings group, or their atlas failed to build
            result.AddSkip("with no other sprite of matching settings to share an atlas with", spriteUsers.Count - fullRemap.Count);
            return result;
        }

        /// <summary>
        /// Points every serialized sprite reference on the scene's scripts at its atlas copy. That covers UdonSharp
        /// fields, the reference list an UdonBehaviour rebuilds its variables from, and button sprite swaps.
        /// UdonSharp copies its fields into Udon during the build, so both sides are rewritten.
        /// </summary>
        private static int RewriteScriptReferences(Scene scene, Dictionary<Sprite, Sprite> remap)
        {
            int count = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour is Image) continue;

                    var so = new SerializedObject(behaviour);
                    var iterator = so.GetIterator();
                    bool changed = false;
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                            iterator.objectReferenceValue is Sprite sprite &&
                            remap.TryGetValue(sprite, out Sprite atlasSprite))
                        {
                            iterator.objectReferenceValue = atlasSprite;
                            changed = true;
                            count++;
                        }
                    }
                    if (changed) so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            return count;
        }

        /// <summary>
        /// Sprites whose every Image user can safely be pointed at an atlas copy. A sprite that anything else uses
        /// (an animation, a SpriteRenderer, a protected script, an excluded Image, and scripts unless their references
        /// are rewritten) is left alone, so both versions never ship and runtime swaps keep working.
        /// </summary>
        private static Dictionary<Sprite, List<Image>> CollectEligibleSprites(Scene scene, bool rewriteScriptReferences, out Dictionary<Sprite, string> excluded)
        {
            var users = new Dictionary<Sprite, List<Image>>();
            excluded = new Dictionary<Sprite, string>();

            var referencedElsewhere = CollectSpritesUsedOutsideImages(scene, rewriteScriptReferences);

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Image image in root.GetComponentsInChildren<Image>(true))
                {
                    Sprite sprite = image.sprite;
                    if (sprite == null) continue;

                    string reason = GetImageSkipReason(image) ?? GetSpriteSkipReason(sprite);
                    if (reason == null && referencedElsewhere.TryGetValue(sprite, out string usedBy)) reason = usedBy;
                    if (reason != null)
                    {
                        if (!excluded.ContainsKey(sprite)) excluded[sprite] = reason;
                        continue;
                    }

                    if (!users.TryGetValue(sprite, out var list))
                    {
                        list = new List<Image>();
                        users[sprite] = list;
                    }
                    list.Add(image);
                }
            }

            foreach (Sprite sprite in excluded.Keys) users.Remove(sprite);
            return users;
        }

        // Video screens are RawImages or renderers, never Images, so video players' control UI is fine to pack
        private static string GetImageSkipReason(Image image)
        {
            if (SynProtectionData.IsProtected(image.gameObject)) return "on protected objects";
            if (image.type == Image.Type.Tiled) return "on tiled images";   // tiling needs the texture to repeat on its own
            if (image.useSpriteMesh) return "using a sprite mesh";
            if (image.overrideSprite != image.sprite) return "with an override sprite";

            // Custom shaders may expect the sprite to fill the whole texture (0-1 UVs)
            Material mat = image.material;
            if (mat == null || mat == image.defaultMaterial) return null;
            string shader = mat.shader != null ? mat.shader.name : "";
            return shader == "UI/Default" || shader.Contains("Supersampled UI") ? null : "with a custom UI shader";
        }

        private static string GetSpriteSkipReason(Sprite sprite)
        {
            if (sprite.packed) return "already in an atlas";
            Texture2D tex = sprite.texture;
            if (tex == null || sprite.associatedAlphaSplitTexture != null) return "without a usable texture";
            Rect r = sprite.rect;
            if (r.width < 1 || r.height < 1 || sprite.textureRect.width < 1 || sprite.textureRect.height < 1) return "without a usable texture";
            if (r.width > MaxSpriteSize || r.height > MaxSpriteSize) return $"larger than {MaxSpriteSize} px";
            if (SynProtectionData.IsProtected(tex)) return "with a protected texture";
            return null;
        }

        private static Dictionary<Sprite, string> CollectSpritesUsedOutsideImages(Scene scene, bool scriptReferencesRewritable)
        {
            var sprites = new Dictionary<Sprite, string>();
            void Add(Sprite sprite, string reason)
            {
                if (sprite != null && !sprites.ContainsKey(sprite)) sprites[sprite] = reason;
            }
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (SpriteRenderer sr in root.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    Add(sr.sprite, "also used by a SpriteRenderer");
                }

                // Scripts, Udon variables and button sprite swaps. When their references get rewritten they don't
                // block packing, except on protected objects, which are never changed.
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour is Image) continue;
                    bool isProtected = SynProtectionData.IsProtected(behaviour.gameObject);
                    if (scriptReferencesRewritable && !isProtected) continue;
                    var iterator = new SerializedObject(behaviour).GetIterator();
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Sprite s)
                        {
                            Add(s, isProtected ? "held by a script on a protected object" : "held by a script or button sprite swap");
                        }
                    }
                }

                // Animations that swap sprites
                foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
                {
                    if (animator.runtimeAnimatorController == null) continue;
                    foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                    {
                        if (clip == null) continue;
                        foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        {
                            foreach (ObjectReferenceKeyframe key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                            {
                                if (key.value is Sprite s) Add(s, "changed by an animation");
                            }
                        }
                    }
                }
            }
            return sprites;
        }

        private static SpriteGroupKey GetGroupKey(Sprite sprite)
        {
            Texture2D tex = sprite.texture;
            return new SpriteGroupKey
            {
                Filter = tex.filterMode,
                Srgb = GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat),
                Compressed = GraphicsFormatUtility.IsCompressedFormat(tex.graphicsFormat),
                PixelsPerUnit = sprite.pixelsPerUnit,
            };
        }

        private static string GetFirstCanvasPath(List<Image> images)
        {
            Canvas canvas = images.Select(i => SynUIAnalysis.GetRootCanvas(i.transform)).FirstOrDefault(c => c != null);
            return canvas != null ? SynPersistentObjectReference.GetHierarchyPath(canvas.transform) : "";
        }

        /// <summary>
        /// Shelf packing into power-of-two atlases (square or rectangular): the smallest area that fits everything,
        /// or as many full-size atlases as needed, each trimmed to the height it actually uses. Empty atlas space
        /// costs as much memory as used space once compressed, so this keeps atlases tight.
        /// </summary>
        private static List<AtlasLayout> Pack(List<Sprite> sprites, int maxSize)
        {
            var layouts = new List<AtlasLayout>();

            var sizes = new List<(int W, int H)>();
            for (int w = 64; w <= maxSize; w *= 2)
            {
                for (int h = 64; h <= maxSize; h *= 2) sizes.Add((w, h));
            }
            // Smallest area first; for equal areas prefer squarer shapes
            sizes = sizes.OrderBy(sz => sz.W * sz.H).ThenBy(sz => Mathf.Abs(sz.W - sz.H)).ToList();

            foreach (var (w, h) in sizes)
            {
                var single = TryPack(sprites, w, h, out List<Sprite> rest, out _);
                if (rest.Count == 0)
                {
                    layouts.Add(new AtlasLayout { Placements = single, Width = w, Height = h });
                    return layouts;
                }
            }

            List<Sprite> remaining = sprites;
            while (remaining.Count > 0)
            {
                var placed = TryPack(remaining, maxSize, maxSize, out List<Sprite> rest, out int usedHeight);
                if (placed.Count == 0) break;
                layouts.Add(new AtlasLayout { Placements = placed, Width = maxSize, Height = Mathf.Min(maxSize, Mathf.NextPowerOfTwo(Mathf.Max(64, usedHeight))) });
                remaining = rest;
            }
            return layouts;
        }

        private static List<Placement> TryPack(List<Sprite> sprites, int width, int height, out List<Sprite> rest, out int usedHeight)
        {
            var placed = new List<Placement>();
            rest = new List<Sprite>();
            int x = 0, y = 0, shelfHeight = 0;
            usedHeight = 0;

            foreach (Sprite sprite in sprites)
            {
                // The full sprite rect: tight sprites only store their trimmed pixels (textureRect), but they are
                // drawn over the whole rect, so the atlas cell must keep the transparent margin too
                int w = Mathf.CeilToInt(sprite.rect.width) + Padding * 2;
                int h = Mathf.CeilToInt(sprite.rect.height) + Padding * 2;
                if (w > width || h > height)
                {
                    rest.Add(sprite);
                    continue;
                }
                if (x + w > width)
                {
                    x = 0;
                    y += shelfHeight;
                    shelfHeight = 0;
                }
                if (y + h > height)
                {
                    rest.Add(sprite);
                    continue;
                }

                placed.Add(new Placement { Sprite = sprite, Rect = new RectInt(x + Padding, y + Padding, w - Padding * 2, h - Padding * 2) });
                x += w;
                shelfHeight = Mathf.Max(shelfHeight, h);
                usedHeight = Mathf.Max(usedHeight, y + h);
            }
            return placed;
        }

        /// <summary>Writes (or reuses) the atlas PNG and returns original sprite -> atlas sprite.</summary>
        private static Dictionary<Sprite, Sprite> BuildAtlas(AtlasLayout atlasLayout, SpriteGroupKey key)
        {
            List<Placement> layout = atlasLayout.Placements;
            int width = atlasLayout.Width, height = atlasLayout.Height;
            string platform = SynAssetCache.GetPlatformName();
            var tokens = new List<string> { AtlasHashTag, key.ToString(), $"{width}x{height}" };
            foreach (Placement p in layout)
            {
                Rect r = p.Sprite.textureRect;
                tokens.Add($"{SynAssetCache.GetAssetIdentityHash(p.Sprite)}|{r}|{p.Sprite.border}|{p.Sprite.pivot}|{p.Rect}");
            }
            string hash = SynAssetCache.ComputeCompositeHashWithPlatform(platform, tokens.ToArray());
            string path = SynAssetCache.GetAssetPath(SynAssetCache.AtlasesCategory, hash, "UIAtlas", ".png", platform);

            using (SynAssetDatabaseScope.Suspend())
            {
                if (!File.Exists(path))
                {
                    if (!WriteAtlasPng(layout, width, height, key, path)) return null;
                    if (!AssetDatabase.IsValidFolder(Path.GetDirectoryName(path).Replace('\\', '/'))) AssetDatabase.Refresh();
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                }

                // Also repairs an atlas whose import settings were never applied (e.g. an interrupted earlier run)
                if (AssetImporter.GetAtPath(path) is TextureImporter existing &&
                    (existing.textureType != TextureImporterType.Sprite || existing.spriteImportMode != SpriteImportMode.Multiple))
                {
                    ConfigureImporter(path, layout, Mathf.Max(width, height), key);
                }

                SynAssetCache.RecordUsage(path);
                var atlasSprites = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToDictionary(s => s.name);
                var remap = new Dictionary<Sprite, Sprite>();
                for (int i = 0; i < layout.Count; i++)
                {
                    if (atlasSprites.TryGetValue(SpriteName(i), out Sprite atlasSprite)) remap[layout[i].Sprite] = atlasSprite;
                }

                if (remap.Count != layout.Count)
                {
                    Debug.LogWarning($"[SYN SCENE OPTIMIZER] UI sprite atlas '{path}' is missing sprites; keeping the original sprites for it.");
                    return null;
                }
                return remap;
            }
        }

        private static string SpriteName(int index) => $"s{index}";

        private static bool WriteAtlasPng(List<Placement> layout, int width, int height, SpriteGroupKey key, string path)
        {
            var output = new Color32[width * height];
            var sourcePixels = new Dictionary<Texture2D, (Color32[] Pixels, int Width)>();

            foreach (Placement p in layout)
            {
                Texture2D texture = p.Sprite.texture;
                if (!sourcePixels.TryGetValue(texture, out var source))
                {
                    Color32[] pixels = ReadPixels(texture, key.Srgb);
                    if (pixels == null) return false;
                    source = (pixels, texture.width);
                    sourcePixels[texture] = source;
                }

                // The stored pixels (textureRect) sit at textureRectOffset inside the full sprite rect; the rest of
                // the cell is the transparent margin Unity trimmed off tight sprites
                Rect tr = p.Sprite.textureRect;
                Vector2 offset = p.Sprite.textureRectOffset;
                int sx = Mathf.FloorToInt(tr.x), sy = Mathf.FloorToInt(tr.y);
                int tw = Mathf.FloorToInt(tr.width), th = Mathf.FloorToInt(tr.height);
                int ox = Mathf.RoundToInt(offset.x), oy = Mathf.RoundToInt(offset.y);
                int w = p.Rect.width, h = p.Rect.height;

                // The cell plus its edge pixels copied outwards, so bilinear filtering at the edges samples the
                // sprite's own colours instead of its neighbours
                for (int y = -Extrude; y < h + Extrude; y++)
                {
                    int cy = Mathf.Clamp(y, 0, h - 1) - oy;
                    int dstRow = (p.Rect.y + y) * width;
                    for (int x = -Extrude; x < w + Extrude; x++)
                    {
                        int cx = Mathf.Clamp(x, 0, w - 1) - ox;
                        output[dstRow + p.Rect.x + x] = cx >= 0 && cy >= 0 && cx < tw && cy < th
                            ? source.Pixels[(sy + cy) * source.Width + sx + cx]
                            : new Color32(0, 0, 0, 0);
                    }
                }
            }

            var atlas = new Texture2D(width, height, TextureFormat.RGBA32, false, !key.Srgb);
            try
            {
                atlas.SetPixels32(output);
                atlas.Apply(false);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, atlas.EncodeToPNG());
                return true;
            }
            finally
            {
                Object.DestroyImmediate(atlas);
            }
        }

        // GPU copy of a texture that may be compressed or not readable, read back in the same colour space
        private static Color32[] ReadPixels(Texture2D texture, bool srgb)
        {
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, !srgb);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                copy.Apply(false);
                return copy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) Object.DestroyImmediate(copy);
            }
        }

        private static void ConfigureImporter(string path, List<Placement> layout, int size, SpriteGroupKey key)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return;

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.spritePixelsPerUnit = key.PixelsPerUnit;
            importer.sRGBTexture = key.Srgb;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = key.Filter;
            importer.maxTextureSize = size;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = key.Compressed ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Uncompressed;

            var meta = new SpriteMetaData[layout.Count];
            for (int i = 0; i < layout.Count; i++)
            {
                Sprite sprite = layout[i].Sprite;
                Rect r = sprite.rect;
                meta[i] = new SpriteMetaData
                {
                    name = SpriteName(i),
                    rect = new Rect(layout[i].Rect.x, layout[i].Rect.y, layout[i].Rect.width, layout[i].Rect.height),
                    alignment = (int)SpriteAlignment.Custom,
                    pivot = new Vector2(sprite.pivot.x / r.width, sprite.pivot.y / r.height),
                    border = sprite.border,
                };
            }

#pragma warning disable 0618 // spritesheet still works in 2022.3 and needs no 2D Sprite package
            importer.spritesheet = meta;
#pragma warning restore 0618
            importer.SaveAndReimport();
        }
    }
}
