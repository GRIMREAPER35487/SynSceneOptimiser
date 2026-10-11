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
        private const string AtlasHashTag = "UIAtlas_v1";
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

        /// <param name="rewriteScriptReferences">
        /// Also pack sprites that scripts, Udon variables and button sprite swaps hold, and point those references at
        /// the atlas copies, so swaps keep batching and sprite comparisons in scripts still match.
        /// </param>
        public static Result Run(Scene scene, int maxAtlasSize, bool rewriteScriptReferences)
        {
            var result = new Result();
            var spriteUsers = CollectEligibleSprites(scene, rewriteScriptReferences, out var excluded);
            var fullRemap = new Dictionary<Sprite, Sprite>();
            result.SpritesSkipped = excluded.Count;
            if (spriteUsers.Count < 2) return result;

            // Sprites with matching texture settings can share an atlas; order them by canvas so a panel's
            // sprites tend to land in the same atlas
            var groups = spriteUsers.Keys
                .GroupBy(GetGroupKey)
                .ToList();

            foreach (var group in groups)
            {
                var sprites = group
                    .OrderBy(s => GetFirstCanvasPath(spriteUsers[s]))
                    .ThenByDescending(s => s.rect.height)
                    .ToList();

                foreach (List<Placement> atlasLayout in Pack(sprites, maxAtlasSize, out int atlasSize))
                {
                    if (atlasLayout.Count < 2) continue; // a one-sprite atlas saves nothing

                    Dictionary<Sprite, Sprite> remap = BuildAtlas(atlasLayout, atlasSize, group.Key);
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
        private static Dictionary<Sprite, List<Image>> CollectEligibleSprites(Scene scene, bool rewriteScriptReferences, out HashSet<Sprite> excluded)
        {
            var users = new Dictionary<Sprite, List<Image>>();
            excluded = new HashSet<Sprite>();

            var referencedElsewhere = CollectSpritesUsedOutsideImages(scene, rewriteScriptReferences);

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Image image in root.GetComponentsInChildren<Image>(true))
                {
                    Sprite sprite = image.sprite;
                    if (sprite == null) continue;

                    if (!IsEligibleImage(image) || !IsEligibleSprite(sprite) || referencedElsewhere.Contains(sprite))
                    {
                        excluded.Add(sprite);
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

            foreach (Sprite sprite in excluded) users.Remove(sprite);
            return users;
        }

        private static bool IsEligibleImage(Image image)
        {
            if (SynProtectionData.IsProtected(image.gameObject)) return false;
            if (SynSceneQuery.IsVideoComponentDetected(image)) return false;
            if (image.type == Image.Type.Tiled) return false;   // tiling needs the texture to repeat on its own
            if (image.useSpriteMesh) return false;
            if (image.overrideSprite != image.sprite) return false;

            // Custom shaders may expect the sprite to fill the whole texture (0-1 UVs)
            Material mat = image.material;
            if (mat == null || mat == image.defaultMaterial) return true;
            string shader = mat.shader != null ? mat.shader.name : "";
            return shader == "UI/Default" || shader.Contains("Supersampled UI");
        }

        private static bool IsEligibleSprite(Sprite sprite)
        {
            if (sprite.packed) return false; // already in an atlas
            Texture2D tex = sprite.texture;
            if (tex == null || sprite.associatedAlphaSplitTexture != null) return false;
            Rect r = sprite.textureRect;
            if (r.width < 1 || r.height < 1 || r.width > MaxSpriteSize || r.height > MaxSpriteSize) return false;
            if (SynProtectionData.IsProtected(tex)) return false;
            return true;
        }

        private static HashSet<Sprite> CollectSpritesUsedOutsideImages(Scene scene, bool scriptReferencesRewritable)
        {
            var sprites = new HashSet<Sprite>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (SpriteRenderer sr in root.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (sr.sprite != null) sprites.Add(sr.sprite);
                }

                // Scripts, Udon variables and button sprite swaps. When their references get rewritten they don't
                // block packing, except on protected objects, which are never changed.
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour is Image) continue;
                    if (scriptReferencesRewritable && !SynProtectionData.IsProtected(behaviour.gameObject)) continue;
                    var iterator = new SerializedObject(behaviour).GetIterator();
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Sprite s)
                        {
                            sprites.Add(s);
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
                                if (key.value is Sprite s) sprites.Add(s);
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
        /// Shelf packing into square power-of-two atlases: the smallest size that fits everything, or as many
        /// full-size atlases as needed.
        /// </summary>
        private static IEnumerable<List<Placement>> Pack(List<Sprite> sprites, int maxSize, out int atlasSize)
        {
            var layouts = new List<List<Placement>>();
            atlasSize = maxSize;

            for (int size = 128; size <= maxSize; size *= 2)
            {
                var single = TryPack(sprites, size, out List<Sprite> rest);
                if (rest.Count == 0)
                {
                    atlasSize = size;
                    layouts.Add(single);
                    return layouts;
                }
            }

            List<Sprite> remaining = sprites;
            while (remaining.Count > 0)
            {
                var layout = TryPack(remaining, maxSize, out List<Sprite> rest);
                if (layout.Count == 0) break;
                layouts.Add(layout);
                remaining = rest;
            }
            return layouts;
        }

        private static List<Placement> TryPack(List<Sprite> sprites, int size, out List<Sprite> rest)
        {
            var placed = new List<Placement>();
            rest = new List<Sprite>();
            int x = 0, y = 0, shelfHeight = 0;

            foreach (Sprite sprite in sprites)
            {
                int w = Mathf.CeilToInt(sprite.textureRect.width) + Padding * 2;
                int h = Mathf.CeilToInt(sprite.textureRect.height) + Padding * 2;
                if (w > size || h > size)
                {
                    rest.Add(sprite);
                    continue;
                }
                if (x + w > size)
                {
                    x = 0;
                    y += shelfHeight;
                    shelfHeight = 0;
                }
                if (y + h > size)
                {
                    rest.Add(sprite);
                    continue;
                }

                placed.Add(new Placement { Sprite = sprite, Rect = new RectInt(x + Padding, y + Padding, w - Padding * 2, h - Padding * 2) });
                x += w;
                shelfHeight = Mathf.Max(shelfHeight, h);
            }
            return placed;
        }

        /// <summary>Writes (or reuses) the atlas PNG and returns original sprite -> atlas sprite.</summary>
        private static Dictionary<Sprite, Sprite> BuildAtlas(List<Placement> layout, int size, SpriteGroupKey key)
        {
            string platform = SynAssetCache.GetPlatformName();
            var tokens = new List<string> { AtlasHashTag, key.ToString(), size.ToString() };
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
                    if (!WriteAtlasPng(layout, size, key, path)) return null;
                    if (!AssetDatabase.IsValidFolder(Path.GetDirectoryName(path).Replace('\\', '/'))) AssetDatabase.Refresh();
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                }

                // Also repairs an atlas whose import settings were never applied (e.g. an interrupted earlier run)
                if (AssetImporter.GetAtPath(path) is TextureImporter existing &&
                    (existing.textureType != TextureImporterType.Sprite || existing.spriteImportMode != SpriteImportMode.Multiple))
                {
                    ConfigureImporter(path, layout, size, key);
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

        private static bool WriteAtlasPng(List<Placement> layout, int size, SpriteGroupKey key, string path)
        {
            var output = new Color32[size * size];
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

                Rect r = p.Sprite.textureRect;
                int sx = Mathf.FloorToInt(r.x), sy = Mathf.FloorToInt(r.y);
                int w = p.Rect.width, h = p.Rect.height;

                // The sprite itself plus its edge pixels copied outwards, so bilinear filtering at the edges
                // samples the sprite's own colours instead of its neighbours
                for (int y = -Extrude; y < h + Extrude; y++)
                {
                    int srcRow = (sy + Mathf.Clamp(y, 0, h - 1)) * source.Width;
                    int dstRow = (p.Rect.y + y) * size;
                    for (int x = -Extrude; x < w + Extrude; x++)
                    {
                        output[dstRow + p.Rect.x + x] = source.Pixels[srcRow + sx + Mathf.Clamp(x, 0, w - 1)];
                    }
                }
            }

            var atlas = new Texture2D(size, size, TextureFormat.RGBA32, false, !key.Srgb);
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
