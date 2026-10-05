using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer.TextureCompressor
{
    public class SynTextureFormatSelector
    {
        private readonly bool _useHighQualityFormatForHighComplexity;
        private readonly float _highQualityComplexityThreshold;

        public SynTextureFormatSelector(
            bool useHighQualityFormatForHighComplexity = true,
            float highQualityComplexityThreshold = 0.70f
        )
        {
            _useHighQualityFormatForHighComplexity = useHighQualityFormatForHighComplexity;
            _highQualityComplexityThreshold = highQualityComplexityThreshold;
        }

        public TextureFormat SelectFormat(bool isNormalMap, float complexity, bool hasAlpha, SynTargetPlatform platform)
        {
            if (platform == SynTargetPlatform.Android || platform == SynTargetPlatform.iOS)
            {
                return SelectMobileFormat(isNormalMap, complexity, hasAlpha);
            }
            else
            {
                return SelectDesktopFormat(isNormalMap, complexity, hasAlpha);
            }
        }

        private TextureFormat SelectDesktopFormat(bool isNormalMap, float complexity, bool hasAlpha)
        {
            if (isNormalMap)
            {
                // BC5 is optimal two-channel normal format for desktop PC
                return TextureFormat.BC5;
            }

            if (_useHighQualityFormatForHighComplexity && complexity >= _highQualityComplexityThreshold)
            {
                // High quality BC7 for complex textures
                return TextureFormat.BC7;
            }

            // Standard DXT formats
            return hasAlpha ? TextureFormat.DXT5 : TextureFormat.DXT1;
        }

        private TextureFormat SelectMobileFormat(bool isNormalMap, float complexity, bool hasAlpha)
        {
            if (isNormalMap)
            {
                // ASTC 5x5 or 6x6 provides excellent quality/compression for normal maps on mobile
                return TextureFormat.ASTC_5x5;
            }

            if (_useHighQualityFormatForHighComplexity && complexity >= _highQualityComplexityThreshold)
            {
                return TextureFormat.ASTC_4x4; // 8.00 bpp
            }

            if (complexity >= 0.35f)
            {
                return TextureFormat.ASTC_6x6; // 3.56 bpp
            }

            return TextureFormat.ASTC_8x8; // 2.00 bpp (ultra lightweight for flat/gradient textures)
        }
    }
}
