using System;
using System.Collections.Generic;
using Synthos.TextureAnalysis;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// How many texture pixels a texture needs, given the size of the world surfaces it covers and how closely they
    /// can be looked at. World version of the Avatar Compressor's texel density (one viewing distance instead of
    /// head and body).
    /// </summary>
    internal readonly struct SynTexelDensityInfo
    {
        /// <summary>Pixel count (width x height) at which the demanding surfaces reach <see cref="TargetDensity"/>.</summary>
        public readonly double RequiredPixels;

        /// <summary>Pixels per cm wanted on the surface.</summary>
        public readonly float TargetDensity;

        public SynTexelDensityInfo(double requiredPixels, float targetDensity)
        {
            RequiredPixels = requiredPixels;
            TargetDensity = targetDensity;
        }

        /// <summary>Pixels per cm the demanding surface gets at the given texture size.</summary>
        public float DensityAt(Vector2Int size)
        {
            return (float)(TargetDensity * Math.Sqrt((double)size.x * size.y / RequiredPixels));
        }

        /// <summary>Largest power-of-two divisor of <paramref name="baseSize"/> that still meets the target density.</summary>
        public int GetDivisor(Vector2Int baseSize)
        {
            double scale = Math.Sqrt(RequiredPixels / ((double)baseSize.x * baseSize.y));
            int divisor = 1;
            while (divisor < SynWorldTexelDensity.MaxDivisor && scale * divisor * 2 <= 1.0)
                divisor *= 2;
            return divisor;
        }
    }

    /// <summary>
    /// Measures how densely each texture is laid out on the scene's meshes, so a texture far sharper than anyone can
    /// see (a 2048 texture on a mug) can be made smaller, and a detailed texture stretched over a large wall is not
    /// shrunk below what is visible.
    /// </summary>
    /// <remarks>
    /// Every triangle using a texture is measured on its own: its UV area (times the material tiling) against its
    /// surface area in the world, giving the pixel count the texture needs for that triangle to reach the target
    /// density. The target comes from the viewing distance. A high area-weighted percentile decides, so a few
    /// stretched or hidden triangles don't. Only textures whose every use is a UV0-mapped slot on a mesh renderer
    /// being optimized are measured; anything else (triplanar, world/local mapping, other UV sets, matcaps, ramps,
    /// particles) gets no limit.
    /// </remarks>
    internal static class SynWorldTexelDensity
    {
        /// <summary>Display resolution assumed for the viewer, in pixels per degree of view (current headsets).</summary>
        public const float PixelsPerDegree = 25f;

        public const int MaxDivisor = 16;
        internal const float Percentile = 0.97f;
        private const double MinUvArea = 1e-10;
        private const double MinSurfaceArea = 1e-8;

        private static readonly string[] NonUvPropertyParts =
        {
            "matcap", "ramp", "grad", "lut", "flipbook", "panosphere", "cube", "decal", "noise", "glitter",
            "audiolink", "rain", "dfg", "ssr", "caustic", "flow"
        };

        /// <summary>Pixels per cm for one texture pixel per display pixel at the given viewing distance.</summary>
        public static float TargetDensity(float viewDistanceCm)
        {
            float distance = Mathf.Max(1f, viewDistanceCm);
            return PixelsPerDegree * Mathf.Rad2Deg * Mathf.Atan(1f / distance);
        }

        /// <summary>
        /// Measures textures from the slots that use them. Textures with any unmeasurable use are left out.
        /// </summary>
        public static Dictionary<Texture2D, SynTexelDensityInfo> Analyze(
            IEnumerable<Renderer> renderers,
            Dictionary<Texture2D, List<(Material Material, string Property)>> uses,
            float viewDistanceCm)
        {
            var result = new Dictionary<Texture2D, SynTexelDensityInfo>();
            if (renderers == null || uses == null || uses.Count == 0) return result;

            var slots = CollectSlots(renderers);
            var meshes = new Dictionary<Renderer, MeshData>();
            float target = TargetDensity(viewDistanceCm);
            try
            {
                foreach (var kvp in uses)
                {
                    double required = Measure(kvp.Value, slots, meshes, target);
                    if (required > 0) result[kvp.Key] = new SynTexelDensityInfo(required, target);
                }
            }
            finally
            {
                foreach (var data in meshes.Values) data?.Dispose();
            }
            return result;
        }

        /// <summary>Raises the divisor to the density limit's when that is larger.</summary>
        public static TextureAnalysisResult ApplyLimit(TextureAnalysisResult analysis, Vector2Int baseSize, SynTexelDensityInfo density, TextureProcessor processor)
        {
            int divisor = density.GetDivisor(baseSize);
            if (divisor <= analysis.RecommendedDivisor) return analysis;
            return new TextureAnalysisResult(analysis.NormalizedComplexity, divisor, processor.CalculateNewDimensions(baseSize.x, baseSize.y, divisor));
        }

        /// <summary>
        /// Lowers the divisor (down to <paramref name="minDivisor"/>) while the result would be below the target
        /// density. Only for textures with real detail: on a near-flat texture blur doesn't show.
        /// </summary>
        public static TextureAnalysisResult ApplyFloor(TextureAnalysisResult analysis, Vector2Int baseSize, SynTexelDensityInfo density, int minDivisor, float lowComplexityThreshold, TextureProcessor processor)
        {
            if (analysis.NormalizedComplexity < lowComplexityThreshold) return analysis;

            int floor = Math.Max(1, minDivisor);
            int divisor = analysis.RecommendedDivisor;
            while (divisor > floor && density.DensityAt(new Vector2Int(baseSize.x / divisor, baseSize.y / divisor)) < density.TargetDensity)
            {
                divisor = Math.Max(floor, divisor / 2);
            }
            if (divisor == analysis.RecommendedDivisor) return analysis;
            return new TextureAnalysisResult(analysis.NormalizedComplexity, divisor, processor.CalculateNewDimensions(baseSize.x, baseSize.y, divisor));
        }

        /// <summary>
        /// The target size, or the imported size when growing past it (from a larger source file) would not be
        /// visible: too little detail, or the surface is already at the target density at the imported size.
        /// </summary>
        public static Vector2Int GateUpscale(Vector2Int target, Vector2Int imported, float complexity, float lowComplexityThreshold, SynTexelDensityInfo? density)
        {
            if (target.x <= imported.x && target.y <= imported.y) return target;
            bool worthIt = complexity >= lowComplexityThreshold && !(density.HasValue && density.Value.DensityAt(imported) >= density.Value.TargetDensity);
            if (worthIt) return target;
            return new Vector2Int(
                Mathf.Min(target.x, TextureProcessor.EnsureMultipleOf4(imported.x)),
                Mathf.Min(target.y, TextureProcessor.EnsureMultipleOf4(imported.y)));
        }

        /// <summary>True if a texture in <paramref name="property"/> is sampled with UV0 across the surface.</summary>
        internal static bool IsUvMapped(Material material, string property)
        {
            string lower = property.ToLowerInvariant();
            foreach (string part in NonUvPropertyParts)
            {
                if (lower.Contains(part)) return false;
            }

            // Mochie: triplanar, and per-group UV set selectors (UV0..4, World, Local)
            foreach (string keyword in material.shaderKeywords)
            {
                if (keyword.IndexOf("TRIPLANAR", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
            string mochieSet = lower.Contains("detail") ? "_UVDetailSet" : "_UVMainSet";
            if (UsesOtherMapping(material, mochieSet)) return false;

            // lilToon (_UVMode) and Poiyomi (UV) per-texture selectors
            return !UsesOtherMapping(material, property + "_UVMode") && !UsesOtherMapping(material, property + "UV");
        }

        private static bool UsesOtherMapping(Material material, string selector)
        {
            return material.HasProperty(selector) && Mathf.Abs(material.GetFloat(selector)) > 0.5f;
        }

        private static double Measure(List<(Material Material, string Property)> uses, Dictionary<Material, List<(Renderer, int)>> slots, Dictionary<Renderer, MeshData> meshes, float target)
        {
            var samples = new List<(double Required, double Area)>();
            var seen = new HashSet<(Renderer, int, Material, string)>();
            foreach (var (material, property) in uses)
            {
                // Used somewhere that can't be measured: no limit for this texture
                if (material == null || !slots.TryGetValue(material, out var rendererSlots) || !IsUvMapped(material, property)) return 0;

                Vector2 tiling = material.GetTextureScale(property);
                double tilingArea = Math.Abs((double)tiling.x * tiling.y);
                if (tilingArea <= 0) return 0;

                foreach (var (renderer, submesh) in rendererSlots)
                {
                    if (!seen.Add((renderer, submesh, material, property))) continue;
                    if (!meshes.TryGetValue(renderer, out var data))
                    {
                        data = MeshData.Create(renderer);
                        meshes[renderer] = data;
                    }
                    if (data == null) return 0;
                    data.AddSamples(submesh, tilingArea, target, samples);
                }
            }
            return WeightedPercentile(samples);
        }

        private static Dictionary<Material, List<(Renderer, int)>> CollectSlots(IEnumerable<Renderer> renderers)
        {
            var slots = new Dictionary<Material, List<(Renderer, int)>>();
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Mesh mesh = GetMesh(renderer);
                if (mesh == null) continue;

                Material[] materials = renderer.sharedMaterials;
                // Extra material slots draw the last submesh again
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) continue;
                    if (!slots.TryGetValue(materials[i], out var list)) slots[materials[i]] = list = new List<(Renderer, int)>();
                    list.Add((renderer, Mathf.Min(i, mesh.subMeshCount - 1)));
                }
            }
            return slots;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer skinned:
                    return skinned.sharedMesh;
                case MeshRenderer _:
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    return filter != null ? filter.sharedMesh : null;
                default:
                    return null;
            }
        }

        /// <summary>Area-weighted percentile of requirements; collapsed-UV triangles count as zero.</summary>
        internal static double WeightedPercentile(List<(double Required, double Area)> samples)
        {
            if (samples.Count == 0) return 0;
            samples.Sort((a, b) => a.Required.CompareTo(b.Required));
            double total = 0;
            foreach (var s in samples) total += s.Area;

            double threshold = total * Percentile;
            double cumulative = 0;
            foreach (var s in samples)
            {
                cumulative += s.Area;
                if (cumulative >= threshold) return s.Required;
            }
            return samples[samples.Count - 1].Required;
        }

        /// <summary>A renderer's mesh in world scale (skinned meshes in their current pose).</summary>
        private sealed class MeshData : IDisposable
        {
            private readonly Mesh mesh;
            private readonly Vector3[] vertices;
            private readonly Vector2[] uvs;
            private readonly Mesh baked;

            private MeshData(Mesh mesh, Vector3[] vertices, Vector2[] uvs, Mesh baked)
            {
                this.mesh = mesh;
                this.vertices = vertices;
                this.uvs = uvs;
                this.baked = baked;
            }

            public static MeshData Create(Renderer renderer)
            {
                Mesh mesh = GetMesh(renderer);
                // Mesh data stays readable in the editor even with Read/Write off
                if (mesh == null) return null;
                Vector2[] uvs = mesh.uv;
                if (uvs == null || uvs.Length != mesh.vertexCount) return null;

                Vector3[] vertices;
                Mesh baked = null;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    baked = new Mesh();
                    skinned.BakeMesh(baked, true);
                    vertices = baked.vertices;
                    if (vertices.Length != mesh.vertexCount)
                    {
                        Object.DestroyImmediate(baked);
                        return null;
                    }
                }
                else
                {
                    vertices = mesh.vertices;
                }

                // Translation doesn't change areas, so directions are enough
                Matrix4x4 matrix = renderer.transform.localToWorldMatrix;
                for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyVector(vertices[i]);
                return new MeshData(mesh, vertices, uvs, baked);
            }

            public void AddSamples(int submesh, double tilingArea, float target, List<(double Required, double Area)> samples)
            {
                if (submesh < 0 || submesh >= mesh.subMeshCount || mesh.GetTopology(submesh) != MeshTopology.Triangles) return;

                int[] indices = mesh.GetTriangles(submesh);
                double target2 = (double)target * target;
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];

                    // m² to cm²
                    double surface = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).magnitude * 0.5 * 10000.0;
                    if (surface < MinSurfaceArea) continue;

                    Vector2 uvA = uvs[a], uvB = uvs[b], uvC = uvs[c];
                    double uvArea = Math.Abs((double)(uvB.x - uvA.x) * (uvC.y - uvA.y) - (double)(uvC.x - uvA.x) * (uvB.y - uvA.y)) * 0.5 * tilingArea;

                    // density² = uvArea * pixels / surface  =>  pixels = target² * surface / uvArea
                    double required = uvArea < MinUvArea ? 0 : target2 * surface / uvArea;
                    samples.Add((required, surface));
                }
            }

            public void Dispose()
            {
                if (baked != null) Object.DestroyImmediate(baked);
            }
        }
    }
}
