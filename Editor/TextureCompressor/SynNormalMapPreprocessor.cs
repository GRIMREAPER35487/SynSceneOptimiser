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

        public static void ReNormalize(Color32[] pixels)
        {
            if (pixels == null) return;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                float nx = (c.r / 255f) * 2f - 1f;
                float ny = (c.g / 255f) * 2f - 1f;
                float lenSq = nx * nx + ny * ny;
                if (lenSq > 1f)
                {
                    float invLen = 1f / Mathf.Sqrt(lenSq);
                    nx *= invLen;
                    ny *= invLen;
                    pixels[i] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt((nx * 0.5f + 0.5f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((ny * 0.5f + 0.5f) * 255f), 0, 255),
                        255,
                        c.a
                    );
                }
            }
        }
    }
}
