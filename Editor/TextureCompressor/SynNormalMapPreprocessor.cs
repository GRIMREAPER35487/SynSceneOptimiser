using System;
using UnityEngine;

namespace Synthos.SynSceneOptimizer.TextureCompressor
{
    public static class SynNormalMapPreprocessor
    {
        public static void PackForTargetFormat(Color32[] pixels, TextureFormat targetFormat)
        {
            if (pixels == null || pixels.Length == 0) return;

            if (targetFormat == TextureFormat.BC5)
            {
                // BC5 samples Red (X) and Green (Y)
                // Ensure pixels have normal X in R, normal Y in G
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 c = pixels[i];
                    // If source was DXTnm (AG layout), convert to RG
                    // Usually uncompressed texture will have RGB or AG
                    byte nx = c.r;
                    byte ny = c.g;
                    if (c.r == 255 && c.b == 255) // DXTnm layout hint
                    {
                        nx = c.a;
                    }
                    pixels[i] = new Color32(nx, ny, 255, 255);
                }
            }
            else if (targetFormat == TextureFormat.DXT5)
            {
                // DXT5nm packs X in Alpha, Y in Green, R=1, B=1
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 c = pixels[i];
                    byte nx = c.r;
                    byte ny = c.g;
                    pixels[i] = new Color32(255, ny, 255, nx);
                }
            }
        }

        /// <summary>
        /// Re-normalizes resampled tangent-space normals and writes them back in plain RGB layout
        /// (X in R, Y in G, reconstructed Z in B, A = 1). DXT5nm sources (X in A, R ≈ 1) are decoded first.
        /// The RGB output unpacks correctly with Unity's UnpackNormal for BC5, ASTC and uncompressed formats.
        /// </summary>
        public static void ReNormalize(Color32[] pixels)
        {
            if (pixels == null || pixels.Length == 0) return;

            bool agLayout = IsAGLayout(pixels);
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                float nx = ((agLayout ? c.a : c.r) / 255f) * 2f - 1f;
                float ny = (c.g / 255f) * 2f - 1f;
                float lenSq = nx * nx + ny * ny;
                if (lenSq > 1f)
                {
                    float invLen = 1f / Mathf.Sqrt(lenSq);
                    nx *= invLen;
                    ny *= invLen;
                    lenSq = 1f;
                }
                float nz = Mathf.Sqrt(Mathf.Max(0f, 1f - lenSq));

                pixels[i] = new Color32(EncodeUnit(nx), EncodeUnit(ny), EncodeUnit(nz), 255);
            }
        }

        // DXT5nm stores X in alpha and leaves red saturated; RGB normals keep red centred around 0.5
        private static bool IsAGLayout(Color32[] pixels)
        {
            int saturatedRed = 0;
            bool alphaVaries = false;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].r >= 250) saturatedRed++;
                if (pixels[i].a < 250) alphaVaries = true;
            }
            return alphaVaries && saturatedRed >= pixels.Length * 0.95f;
        }

        private static byte EncodeUnit(float v)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt((v * 0.5f + 0.5f) * 255f), 0, 255);
        }
    }
}
