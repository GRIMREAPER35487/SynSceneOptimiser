using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    public enum SynMipStreamingVerdict
    {
        Eligible,
        NoMipmaps,
        NotTexture2D,
        UIOrSprite,
        Cookie,
        LookupTexture,
        PointFiltered,
        TooSmall,
        ColorPalette
    }

    /// <summary>
    /// Decides whether a texture benefits from mipmap streaming. Streaming only pays off for textures sampled
    /// by Mesh/Skinned/Terrain renderers (where Unity can compute the visible mip level), that already have a
    /// mip chain, and that are large enough for dropping top mips to matter. Lookup tables, ramps, palettes,
    /// point-filtered and UI textures must always sample their full-resolution mip, so streaming them either
    /// does nothing or produces wrong lookups at a distance.
    /// </summary>
    public static class SynMipStreamingEligibility
    {
        // Below this size the top mip is too small to be worth streaming out
        public const int MinStreamingSize = 256;

        public static SynMipStreamingVerdict Evaluate(Texture2D tex, TextureImporter importer)
        {
            if (tex == null) return SynMipStreamingVerdict.NotTexture2D;

            if (importer != null)
            {
                if (importer.textureShape != TextureImporterShape.Texture2D) return SynMipStreamingVerdict.NotTexture2D;
                if (importer.textureType == TextureImporterType.Sprite || importer.textureType == TextureImporterType.GUI) return SynMipStreamingVerdict.UIOrSprite;
                if (importer.textureType == TextureImporterType.Cookie) return SynMipStreamingVerdict.Cookie;
                if (!importer.mipmapEnabled) return SynMipStreamingVerdict.NoMipmaps;
                if (importer.filterMode == FilterMode.Point) return SynMipStreamingVerdict.PointFiltered;
            }
            else
            {
                if (tex.mipmapCount <= 1) return SynMipStreamingVerdict.NoMipmaps;
                if (tex.filterMode == FilterMode.Point) return SynMipStreamingVerdict.PointFiltered;
            }

            if (IsColorPalette(tex)) return SynMipStreamingVerdict.ColorPalette;
            if (IsLookupTexture(tex)) return SynMipStreamingVerdict.LookupTexture;
            if (Mathf.Max(tex.width, tex.height) < MinStreamingSize) return SynMipStreamingVerdict.TooSmall;

            return SynMipStreamingVerdict.Eligible;
        }

        /// <summary>
        /// LUTs, gradient ramps and strip textures: very thin or extremely elongated, or named as lookups.
        /// </summary>
        public static bool IsLookupTexture(Texture2D tex)
        {
            return IsLookupShape(tex.width, tex.height, tex.name);
        }

        public static bool IsLookupShape(int width, int height, string name)
        {
            int min = Mathf.Min(width, height);
            int max = Mathf.Max(width, height);
            if (min <= 16 || max >= min * 8) return true;

            string lower = (name ?? "").ToLowerInvariant();
            return lower.Contains("lut") || lower.Contains("ramp") || lower.Contains("gradient") || lower.Contains("lookup");
        }

        public static bool IsColorPalette(Texture2D tex)
        {
            string name = tex.name.ToLowerInvariant();
            if (name.Contains("palette") || name.Contains("colorsheet")) return true;

            string path = AssetDatabase.GetAssetPath(tex);
            return !string.IsNullOrEmpty(path) && path.ToLowerInvariant().Contains("palette");
        }

        public static string Describe(SynMipStreamingVerdict verdict)
        {
            switch (verdict)
            {
                case SynMipStreamingVerdict.Eligible: return "Eligible";
                case SynMipStreamingVerdict.NoMipmaps: return "No mipmaps";
                case SynMipStreamingVerdict.NotTexture2D: return "Cubemap / array / 3D";
                case SynMipStreamingVerdict.UIOrSprite: return "UI / sprite texture";
                case SynMipStreamingVerdict.Cookie: return "Light cookie";
                case SynMipStreamingVerdict.LookupTexture: return "Lookup / ramp / gradient";
                case SynMipStreamingVerdict.PointFiltered: return "Point filtered";
                case SynMipStreamingVerdict.TooSmall: return $"Smaller than {MinStreamingSize}px";
                case SynMipStreamingVerdict.ColorPalette: return "Color palette";
                default: return verdict.ToString();
            }
        }
    }
}
