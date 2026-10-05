using UnityEngine;

namespace Synthos.SynSceneOptimizer.TextureCompressor
{
    public class SynComplexityCalculator
    {
        private readonly float _highComplexityThreshold;
        private readonly float _lowComplexityThreshold;
        private readonly int _minDivisor;
        private readonly int _maxDivisor;

        public SynComplexityCalculator(
            float highComplexityThreshold = 0.70f,
            float lowComplexityThreshold = 0.20f,
            int minDivisor = 1,
            int maxDivisor = 8
        )
        {
            _highComplexityThreshold = highComplexityThreshold;
            _lowComplexityThreshold = lowComplexityThreshold;
            _minDivisor = minDivisor;
            _maxDivisor = maxDivisor;
        }

        public int CalculateRecommendedDivisor(float complexity)
        {
            float t;
            if (Mathf.Approximately(_highComplexityThreshold, _lowComplexityThreshold))
            {
                t = 0.5f;
            }
            else if (complexity >= _highComplexityThreshold)
            {
                t = 0f;
            }
            else if (complexity <= _lowComplexityThreshold)
            {
                t = 1f;
            }
            else
            {
                t = 1f - (complexity - _lowComplexityThreshold) / (_highComplexityThreshold - _lowComplexityThreshold);
            }

            float logMin = Mathf.Log(_minDivisor, 2);
            float logMax = Mathf.Log(_maxDivisor, 2);
            float logDivisor = Mathf.Lerp(logMin, logMax, t);

            int divisor = Mathf.RoundToInt(Mathf.Pow(2, Mathf.Round(logDivisor)));
            return Mathf.Clamp(divisor, _minDivisor, _maxDivisor);
        }
    }
}
