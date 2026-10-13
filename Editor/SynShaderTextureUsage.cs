using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Finds which texture properties a material's shader variant actually samples, by compiling that variant and
    /// reading its texture bindings. A texture in a slot behind a disabled shader feature (e.g. Mochie's rain sheet
    /// with rain off) is never sampled but Unity still ships it, because the material references it.
    /// Results are cached in Library, since each variant takes a few seconds to compile.
    /// </summary>
    public static class SynShaderTextureUsage
    {
        private const string CachePath = "Library/SynSceneOptimizer/ShaderTextureUsage.txt";
        private const string UnknownMarker = "?";

        // Global keywords a world variant is commonly compiled with. A shader may sample a material texture only
        // under one of these (e.g. #ifdef LIGHTMAP_ON), so each pass is compiled with and without the ones it declares.
        private static readonly string[] TypicalGlobalKeywords =
        {
            "DIRECTIONAL", "LIGHTMAP_ON", "DIRLIGHTMAP_COMBINED", "DYNAMICLIGHTMAP_ON", "LIGHTPROBE_SH",
            "SHADOWS_SCREEN", "SHADOWS_SHADOWMASK", "LIGHTMAP_SHADOW_MIXING", "VERTEXLIGHT_ON", "FOG_LINEAR",
            "SHADOWS_DEPTH", "INSTANCING_ON", "STEREO_INSTANCING_ON"
        };

        private static readonly ShaderType[] Stages = { ShaderType.Vertex, ShaderType.Fragment, ShaderType.Geometry, ShaderType.Hull, ShaderType.Domain };

        private static Dictionary<string, string> cache;
        private static bool cacheDirty;

        /// <summary>
        /// Returns the texture properties the variant samples, or null when that can't be determined safely
        /// (compile error, fixed-function pass, or not cached and <paramref name="allowCompile"/> is false).
        /// </summary>
        public static HashSet<string> GetSampledTextures(Shader shader, IEnumerable<string> materialKeywords, BuildTarget target, bool allowCompile = true)
        {
            if (shader == null) return null;

            var keywords = new List<string>();
            foreach (string keyword in materialKeywords)
            {
                // Stale keywords the shader doesn't declare don't change the variant, so they stay out of the key
                if (!string.IsNullOrEmpty(keyword) && shader.keywordSpace.FindKeyword(keyword).isValid && !keywords.Contains(keyword))
                    keywords.Add(keyword);
            }
            keywords.Sort(StringComparer.Ordinal);

            GetCompilerPlatform(target, out ShaderCompilerPlatform compilerPlatform, out BuildTarget compileTarget);
            string key = GetShaderId(shader) + "|" + compilerPlatform + "|" + string.Join(" ", keywords);

            LoadCache();
            if (cache.TryGetValue(key, out string cached))
                return cached == UnknownMarker ? null : new HashSet<string>(cached.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            if (!allowCompile) return null;

            HashSet<string> result = Compile(shader, keywords.ToArray(), compilerPlatform, compileTarget);
            cache[key] = result == null ? UnknownMarker : string.Join(",", result);
            cacheDirty = true;
            return result;
        }

        /// <summary>
        /// Empties every texture slot the material's current variant never samples. For materials the optimizer
        /// generates itself (no script can reach them). Returns how many slots were cleared.
        /// </summary>
        public static int ClearUnsampledTextures(Material mat, BuildTarget target)
        {
            if (mat == null || mat.shader == null) return 0;
            HashSet<string> sampled = GetSampledTextures(mat.shader, mat.shaderKeywords, target);
            if (sampled == null) return 0;

            int cleared = 0;
            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                if (mat.GetTexture(propName) != null && !sampled.Contains(propName))
                {
                    mat.SetTexture(propName, null);
                    cleared++;
                }
            }
            return cleared;
        }

        public static void SaveCache()
        {
            if (!cacheDirty || cache == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                var lines = new List<string>(cache.Count);
                foreach (var kvp in cache) lines.Add(kvp.Key + "\t" + kvp.Value);
                File.WriteAllLines(CachePath, lines);
                cacheDirty = false;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not save the shader texture usage cache: {e.Message}");
            }
        }

        private static HashSet<string> Compile(Shader shader, string[] materialKeywords, ShaderCompilerPlatform platform, BuildTarget target)
        {
            try
            {
                ShaderData data = ShaderUtil.GetShaderData(shader);
                if (data == null || data.SubshaderCount == 0) return null;

                var sampled = new HashSet<string>(StringComparer.Ordinal);
                for (int s = 0; s < data.SubshaderCount; s++)
                {
                    ShaderData.Subshader subshader = data.GetSubshader(s);
                    for (int p = 0; p < subshader.PassCount; p++)
                    {
                        ShaderData.Pass pass = subshader.GetPass(p);
                        if (pass.IsGrabPass) continue;

                        string[] withGlobals = AddDeclaredGlobals(shader, new PassIdentifier((uint)s, (uint)p), materialKeywords);
                        bool hasProgram = false;
                        foreach (ShaderType stage in Stages)
                        {
                            if (!pass.HasShaderStage(stage)) continue;
                            hasProgram = true;
                            if (!AddBindings(pass, stage, materialKeywords, platform, target, sampled)) return null;
                            if (withGlobals != null && !AddBindings(pass, stage, withGlobals, platform, target, sampled)) return null;
                        }

                        // A fixed-function pass binds textures without a program, so its usage can't be read
                        if (!hasProgram) return null;
                    }
                }
                return sampled;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not inspect shader '{shader.name}': {e.Message}");
                return null;
            }
        }

        private static bool AddBindings(ShaderData.Pass pass, ShaderType stage, string[] keywords, ShaderCompilerPlatform platform, BuildTarget target, HashSet<string> sampled)
        {
            ShaderData.VariantCompileInfo info = pass.CompileVariant(stage, keywords, platform, target);
            if (!info.Success || info.TextureBindings == null) return false;
            foreach (ShaderData.TextureBindingInfo binding in info.TextureBindings)
            {
                if (!string.IsNullOrEmpty(binding.Name)) sampled.Add(binding.Name);
            }
            return true;
        }

        // The material keywords plus the typical global keywords this pass declares, or null if it declares none
        private static string[] AddDeclaredGlobals(Shader shader, PassIdentifier passId, string[] materialKeywords)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (LocalKeyword keyword in ShaderUtil.GetPassKeywords(shader, passId)) declared.Add(keyword.name);

            var result = new List<string>(materialKeywords);
            foreach (string global in TypicalGlobalKeywords)
            {
                if (declared.Contains(global) && !result.Contains(global)) result.Add(global);
            }
            return result.Count == materialKeywords.Length ? null : result.ToArray();
        }

        private static void GetCompilerPlatform(BuildTarget target, out ShaderCompilerPlatform platform, out BuildTarget compileTarget)
        {
            switch (target)
            {
                case BuildTarget.Android:
                    platform = ShaderCompilerPlatform.GLES3x;
                    compileTarget = BuildTarget.Android;
                    break;
                case BuildTarget.iOS:
                    platform = ShaderCompilerPlatform.Metal;
                    compileTarget = BuildTarget.iOS;
                    break;
                default:
                    platform = ShaderCompilerPlatform.D3D;
                    compileTarget = BuildTarget.StandaloneWindows64;
                    break;
            }
        }

        // Identifies the shader source: the dependency hash changes when the shader or its includes change
        private static string GetShaderId(Shader shader)
        {
            string path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets", StringComparison.Ordinal) && !path.StartsWith("Packages", StringComparison.Ordinal))
                return "builtin:" + shader.name + ":" + Application.unityVersion;
            return AssetDatabase.AssetPathToGUID(path) + ":" + AssetDatabase.GetAssetDependencyHash(path);
        }

        private static void LoadCache()
        {
            if (cache != null) return;
            cache = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(CachePath)) return;
                foreach (string line in File.ReadAllLines(CachePath))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0) cache[line.Substring(0, tab)] = line.Substring(tab + 1);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not read the shader texture usage cache: {e.Message}");
            }
        }
    }
}
