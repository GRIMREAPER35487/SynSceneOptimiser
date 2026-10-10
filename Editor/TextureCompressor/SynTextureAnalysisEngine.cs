using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Synthos.SynSceneOptimizer.TextureCompressor
{
    public static class AnalysisConstants
    {
        public const int SobelSamplingDenominator = 512;
        public const int DctBlockSize = 8;
        public const int GlcmLevels = 16;
        public const int HistogramBins = 256;

        public const float AlphaThreshold = 0.05f;

        // Percentiles for normalization
        public const float GradientPercentileLow = 0.01f;
        public const float GradientPercentileHigh = 0.35f;
        public const float SpatialFreqPercentileLow = 0.005f;
        public const float SpatialFreqPercentileHigh = 0.25f;
        public const float ColorVariancePercentileLow = 0.001f;
        public const float ColorVariancePercentileHigh = 0.15f;
        public const float EntropyPercentileLow = 1.0f;
        public const float EntropyPercentileHigh = 7.5f;

        // Weights for High Accuracy Strategy
        public const float HighAccuracyGradientWeight = 0.30f;
        public const float HighAccuracySpatialFrequencyWeight = 0.25f;
        public const float HighAccuracyColorVarianceWeight = 0.20f;
        public const float HighAccuracyEntropyWeight = 0.25f;
    }

    public static class AlphaExtractor
    {
        public const float TransparentMarker = -1.0f;

        public static bool IsTransparent(float val) => val < 0f;

        /// <param name="alphaIsTransparency">
        /// Only true when alpha is the texture's coverage (main texture of a cutout/transparent material).
        /// Otherwise alpha holds data such as smoothness or DXT5nm normal X and every texel must be analyzed.
        /// </param>
        public static ProcessedPixelData Extract(Color32[] pixels, int width, int height, bool alphaIsTransparency)
        {
            int total = width * height;
            float[] grayscale = new float[total];
            Color[] opaquePixels = new Color[total];
            int opaqueCount = 0;
            bool hasAlpha = false;

            for (int i = 0; i < total; i++)
            {
                Color32 c = pixels[i];
                float a = c.a / 255f;
                if (a < 0.99f) hasAlpha = true;

                if (alphaIsTransparency && a < AnalysisConstants.AlphaThreshold)
                {
                    grayscale[i] = TransparentMarker;
                }
                else
                {
                    float r = c.r / 255f;
                    float g = c.g / 255f;
                    float b = c.b / 255f;
                    float gray = 0.299f * r + 0.587f * g + 0.114f * b;
                    grayscale[i] = gray;
                    opaquePixels[opaqueCount++] = new Color(r, g, b, a);
                }
            }

            return new ProcessedPixelData
            {
                Grayscale = grayscale,
                OpaquePixels = opaquePixels,
                OpaqueCount = opaqueCount,
                Width = width,
                Height = height,
                HasSignificantAlpha = hasAlpha
            };
        }
    }

    public class ProcessedPixelData
    {
        public float[] Grayscale;
        public Color[] OpaquePixels;
        public int OpaqueCount;
        public int Width;
        public int Height;
        public bool HasSignificantAlpha;
    }

    public static class ImageMath
    {
        public static float NormalizeWithPercentile(float val, float low, float high)
        {
            if (high <= low) return 0f;
            return Mathf.Clamp01((val - low) / (high - low));
        }

        public static float CalculateSobelGradient(float[] grayscale, int width, int height, int opaqueCount)
        {
            if (opaqueCount == 0) return 0f;

            float total = 0f;
            int count = 0;
            int step = Mathf.Max(1, width / AnalysisConstants.SobelSamplingDenominator);
            int totalPixels = width * height;

            for (int y = 1; y < height - 1; y += step)
            {
                for (int x = 1; x < width - 1; x += step)
                {
                    int idx = y * width + x;
                    int idxUpLeft = idx - width - 1;
                    int idxUp = idx - width;
                    int idxUpRight = idx - width + 1;
                    int idxLeft = idx - 1;
                    int idxRight = idx + 1;
                    int idxDownLeft = idx + width - 1;
                    int idxDown = idx + width;
                    int idxDownRight = idx + width + 1;

                    if (idxDownRight >= totalPixels || idxUpLeft < 0) continue;

                    if (AlphaExtractor.IsTransparent(grayscale[idx]) ||
                        AlphaExtractor.IsTransparent(grayscale[idxUpLeft]) ||
                        AlphaExtractor.IsTransparent(grayscale[idxUp]) ||
                        AlphaExtractor.IsTransparent(grayscale[idxUpRight]) ||
                        AlphaExtractor.IsTransparent(grayscale[idxLeft]) ||
                        AlphaExtractor.IsTransparent(grayscale[idxRight]) ||
                        AlphaExtractor.IsTransparent(grayscale[idxDownLeft]) ||
                        AlphaExtractor.IsTransparent(grayscale[idxDown]) ||
                        AlphaExtractor.IsTransparent(grayscale[idxDownRight]))
                        continue;

                    float gx = -grayscale[idxUpLeft] + grayscale[idxUpRight] - 2f * grayscale[idxLeft] + 2f * grayscale[idxRight] - grayscale[idxDownLeft] + grayscale[idxDownRight];
                    float gy = -grayscale[idxUpLeft] - 2f * grayscale[idxUp] - grayscale[idxUpRight] + grayscale[idxDownLeft] + 2f * grayscale[idxDown] + grayscale[idxDownRight];

                    total += Mathf.Sqrt(gx * gx + gy * gy);
                    count++;
                }
            }

            return count > 0 ? total / count : 0f;
        }

        public static float CalculateSpatialFrequency(float[] grayscale, int width, int height, int opaqueCount)
        {
            if (opaqueCount == 0) return 0f;

            float rowFreq = 0f;
            float colFreq = 0f;
            int rowCount = 0;
            int colCount = 0;
            int step = Mathf.Max(1, width / AnalysisConstants.SobelSamplingDenominator);
            int totalPixels = width * height;

            for (int y = 0; y < height; y += step)
            {
                for (int x = step; x < width; x += step)
                {
                    int idx = y * width + x;
                    int prevIdx = y * width + (x - step);

                    if (idx < totalPixels && prevIdx >= 0 &&
                        !AlphaExtractor.IsTransparent(grayscale[idx]) &&
                        !AlphaExtractor.IsTransparent(grayscale[prevIdx]))
                    {
                        float diff = grayscale[idx] - grayscale[prevIdx];
                        rowFreq += diff * diff;
                        rowCount++;
                    }
                }
            }

            for (int y = step; y < height; y += step)
            {
                for (int x = 0; x < width; x += step)
                {
                    int idx = y * width + x;
                    int prevIdx = (y - step) * width + x;

                    if (idx < totalPixels && prevIdx >= 0 &&
                        !AlphaExtractor.IsTransparent(grayscale[idx]) &&
                        !AlphaExtractor.IsTransparent(grayscale[prevIdx]))
                    {
                        float diff = grayscale[idx] - grayscale[prevIdx];
                        colFreq += diff * diff;
                        colCount++;
                    }
                }
            }

            float rf = rowCount > 0 ? Mathf.Sqrt(rowFreq / rowCount) : 0f;
            float cf = colCount > 0 ? Mathf.Sqrt(colFreq / colCount) : 0f;
            return Mathf.Sqrt(rf * rf + cf * cf);
        }

        public static float CalculateColorVariance(Color[] opaquePixels, int opaqueCount)
        {
            if (opaqueCount <= 1) return 0f;

            float sumR = 0f, sumG = 0f, sumB = 0f;
            int step = Mathf.Max(1, opaqueCount / 4096);
            int samples = 0;

            for (int i = 0; i < opaqueCount; i += step)
            {
                sumR += opaquePixels[i].r;
                sumG += opaquePixels[i].g;
                sumB += opaquePixels[i].b;
                samples++;
            }

            if (samples <= 1) return 0f;
            float meanR = sumR / samples;
            float meanG = sumG / samples;
            float meanB = sumB / samples;

            float varR = 0f, varG = 0f, varB = 0f;
            for (int i = 0; i < opaqueCount; i += step)
            {
                float dr = opaquePixels[i].r - meanR;
                float dg = opaquePixels[i].g - meanG;
                float db = opaquePixels[i].b - meanB;
                varR += dr * dr;
                varG += dg * dg;
                varB += db * db;
            }

            return (varR + varG + varB) / (3f * samples);
        }

        public static float CalculateEntropy(float[] grayscale, int opaqueCount)
        {
            if (opaqueCount == 0) return 0f;

            int[] hist = new int[AnalysisConstants.HistogramBins];
            int step = Mathf.Max(1, grayscale.Length / 8192);
            int sampleCount = 0;

            for (int i = 0; i < grayscale.Length; i += step)
            {
                float val = grayscale[i];
                if (!AlphaExtractor.IsTransparent(val))
                {
                    int bin = Mathf.Clamp((int)(val * (AnalysisConstants.HistogramBins - 1)), 0, AnalysisConstants.HistogramBins - 1);
                    hist[bin]++;
                    sampleCount++;
                }
            }

            if (sampleCount == 0) return 0f;

            float entropy = 0f;
            for (int i = 0; i < AnalysisConstants.HistogramBins; i++)
            {
                if (hist[i] > 0)
                {
                    float p = (float)hist[i] / sampleCount;
                    entropy -= p * Mathf.Log(p, 2f);
                }
            }

            return entropy;
        }
    }

    public static class SynTextureAnalysisEngine
    {
        public static float AnalyzeComplexity(Color32[] pixels, int width, int height, bool isNormalMap, bool alphaIsTransparency = false)
        {
            if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0) return 0f;

            var data = AlphaExtractor.Extract(pixels, width, height, alphaIsTransparency && !isNormalMap);
            if (data.OpaqueCount == 0) return 0f;

            float gradient = ImageMath.CalculateSobelGradient(data.Grayscale, data.Width, data.Height, data.OpaqueCount);
            float spatialFreq = ImageMath.CalculateSpatialFrequency(data.Grayscale, data.Width, data.Height, data.OpaqueCount);
            float colorVar = isNormalMap ? 0f : ImageMath.CalculateColorVariance(data.OpaquePixels, data.OpaqueCount);
            float entropy = ImageMath.CalculateEntropy(data.Grayscale, data.OpaqueCount);

            float normGrad = ImageMath.NormalizeWithPercentile(gradient, AnalysisConstants.GradientPercentileLow, AnalysisConstants.GradientPercentileHigh);
            float normFreq = ImageMath.NormalizeWithPercentile(spatialFreq, AnalysisConstants.SpatialFreqPercentileLow, AnalysisConstants.SpatialFreqPercentileHigh);
            float normColor = ImageMath.NormalizeWithPercentile(colorVar, AnalysisConstants.ColorVariancePercentileLow, AnalysisConstants.ColorVariancePercentileHigh);
            float normEntropy = ImageMath.NormalizeWithPercentile(entropy, AnalysisConstants.EntropyPercentileLow, AnalysisConstants.EntropyPercentileHigh);

            if (isNormalMap)
            {
                return Mathf.Clamp01(0.6f * normGrad + 0.4f * normFreq);
            }

            return Mathf.Clamp01(
                AnalysisConstants.HighAccuracyGradientWeight * normGrad +
                AnalysisConstants.HighAccuracySpatialFrequencyWeight * normFreq +
                AnalysisConstants.HighAccuracyColorVarianceWeight * normColor +
                AnalysisConstants.HighAccuracyEntropyWeight * normEntropy
            );
        }

        public static float AnalyzeComplexity(Texture2D tex, bool isNormalMap)
        {
            if (tex == null) return 0f;
            Color32[] pixels = tex.GetPixels32();
            return AnalyzeComplexity(pixels, tex.width, tex.height, isNormalMap);
        }
    }
}
