using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Synthos.SynSceneOptimizer
{
    public enum SynTargetPlatform
    {
        PC,
        Android,
        iOS
    }

    /// <summary>
    /// High-performance deterministic, per-platform caching system for Syn Scene Optimizer.
    /// Manages persistent, collision-free assets under BaseCachePath/{Platform}/.
    /// Guaranteed 100% 'Same Name Safe' using GUIDs, Sub-Asset Identifiers, Dependency Hashes, and Platform isolation.
    /// Supports PC, Android (Quest/Mobile), and iOS.
    /// </summary>
    public static class SynAssetCache
    {
        public static string BaseCachePath
        {
            get
            {
                string custom = SynSceneOptimizerSettings.GetString("CustomCachePath", "");
                if (!string.IsNullOrEmpty(custom))
                {
                    return custom.Replace('\\', '/').TrimEnd('/') + "/";
                }
                return "Assets/SynSceneOptimizer_Cache/";
            }
        }

        public static string TransientCachePath
        {
            get
            {
                string path = $"{BaseCachePath}Transient/";
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                return path;
            }
        }

        public static string LastBuildVRAMReportPath => $"{TransientCachePath}LastBuildVRAMReport.json";
        public static string LastPlayModeVRAMReportPath => $"{TransientCachePath}LastPlayModeVRAMReport.json";
        
        public const string MeshesCategory = "Meshes";
        public const string PalettesCategory = "Palettes";
        public const string TextureArraysCategory = "TextureArrays";
        public const string TexturesCategory = "Textures";
        public const string MaterialsCategory = "Materials";
        public const string AtlasesCategory = "Atlases";
        public const string OtherCategory = "Other";

        public static readonly string[] Platforms = new[] { "PC", "Android", "iOS" };

        public static readonly string[] Categories = new[]
        {
            MeshesCategory,
            PalettesCategory,
            TextureArraysCategory,
            TexturesCategory,
            MaterialsCategory,
            AtlasesCategory,
            OtherCategory
        };

        // In-memory lookup cache to avoid repeated disk queries during a single session
        private static readonly Dictionary<string, Object> MemoryCache = new Dictionary<string, Object>();

        #region Platform Resolution

        public static SynTargetPlatform GetCurrentTargetPlatform()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            if (target == BuildTarget.Android) return SynTargetPlatform.Android;
            if (target == BuildTarget.iOS) return SynTargetPlatform.iOS;
            return SynTargetPlatform.PC;
        }

        public static string GetPlatformName(SynTargetPlatform? platform = null)
        {
            var p = platform ?? GetCurrentTargetPlatform();
            switch (p)
            {
                case SynTargetPlatform.Android: return "Android";
                case SynTargetPlatform.iOS: return "iOS";
                default: return "PC";
            }
        }

        public static string GetPlatformDisplayName(string platform = null)
        {
            string p = platform ?? GetPlatformName();
            switch (p)
            {
                case "Android": return "Android (Quest / Mobile)";
                case "iOS": return "iOS (VRChat iOS)";
                default: return "PC (Windows Standalone)";
            }
        }

        #endregion

        #region Directory Management

        public static void EnsureDirectoriesExist(string platform = null)
        {
            if (!Directory.Exists(BaseCachePath))
            {
                Directory.CreateDirectory(BaseCachePath);
            }

            string[] targetPlatforms = string.IsNullOrEmpty(platform) ? Platforms : new[] { platform };

            foreach (string plat in targetPlatforms)
            {
                string platPath = Path.Combine(BaseCachePath, plat).Replace('\\', '/');
                if (!Directory.Exists(platPath))
                {
                    Directory.CreateDirectory(platPath);
                }

                foreach (string category in Categories)
                {
                    string categoryPath = Path.Combine(platPath, category).Replace('\\', '/');
                    if (!Directory.Exists(categoryPath))
                    {
                        Directory.CreateDirectory(categoryPath);
                    }
                }
            }
        }

        public static string GetPlatformCachePath(string platform = null)
        {
            string plat = platform ?? GetPlatformName();
            return $"{BaseCachePath}{plat}/";
        }

        public static string GetCategoryPath(string category, string platform = null)
        {
            string plat = platform ?? GetPlatformName();
            EnsureDirectoriesExist(plat);
            return $"{BaseCachePath}{plat}/{category}/";
        }

        public static string GetAssetPath(string category, string hashKey, string cleanName, string extension, string platform = null)
        {
            string catPath = GetCategoryPath(category, platform);
            string safeName = Regex.Replace(cleanName ?? "Asset", @"[^a-zA-Z0-9_]", "_");
            if (safeName.Length > 24) safeName = safeName.Substring(0, 24);
            return $"{catPath}{safeName}_{hashKey}{extension}";
        }

        #endregion

        #region Hash Generation (Same-Name Safe & Platform-Isolated)

        /// <summary>
        /// Gets the globally unique asset identifier (GUID + SubAsset FileID + Type + Name + DependencyHash).
        /// </summary>
        public static string GetAssetIdentityHash(Object asset)
        {
            if (asset == null) return "null";

            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path))
            {
                // In-memory asset: fallback to instance signature
                return $"InMemory_{asset.GetType().Name}_{asset.name}_{asset.GetInstanceID()}";
            }

            string guid = AssetDatabase.AssetPathToGUID(path);
            string depHash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            string assetName = !string.IsNullOrEmpty(asset.name) ? asset.name : "Unnamed";
            string typeName = asset.GetType().Name;
            
            // Query local file identifier (handles sub-assets, FBX components, scene objects, prefabs)
            long localId = 0;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string extractedGuid, out localId))
            {
                string effectiveGuid = !string.IsNullOrEmpty(extractedGuid) ? extractedGuid : guid;
                if (localId != 0)
                {
                    return $"{effectiveGuid}_{localId}_{typeName}_{assetName}_{depHash}";
                }
            }

            // Fallback for assets without distinct localId: always bind to asset name and type
            return $"{guid}_{typeName}_{assetName}_{depHash}";
        }

        /// <summary>
        /// Computes SHA256 string from a collection of string tokens, seeded with platform context.
        /// </summary>
        public static string ComputeCompositeHash(params string[] tokens)
        {
            return ComputeCompositeHashWithPlatform(GetPlatformName(), tokens);
        }

        /// <summary>
        /// Computes SHA256 string explicitly for a specific platform.
        /// </summary>
        public static string ComputeCompositeHashWithPlatform(string platform, params string[] tokens)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                sb.Append("Plat:").Append(platform ?? GetPlatformName()).Append("|");
                foreach (string token in tokens)
                {
                    sb.Append(token).Append("|");
                }
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                byte[] hashBytes = sha.ComputeHash(bytes);
                
                var hashSb = new StringBuilder(64);
                foreach (byte b in hashBytes)
                {
                    hashSb.Append(b.ToString("x2"));
                }
                return hashSb.ToString();
            }
        }

        /// <summary>
        /// Computes deterministic hash for a Mesh Simplification operation.
        /// </summary>
        public static string ComputeMeshHash(Mesh mesh, float ratio, string platform, int vertexCount, bool dynamicTiers = false, string extraParams = "")
        {
            string plat = !string.IsNullOrEmpty(platform) ? platform : GetPlatformName();
            string assetId = GetAssetIdentityHash(mesh);
            string geomSignature = $"{mesh.name}_{mesh.vertexCount}_{mesh.subMeshCount}_{mesh.triangles.Length}";
            return ComputeCompositeHashWithPlatform(plat, "MeshSimplifier", assetId, geomSignature, ratio.ToString("F4"), plat, vertexCount.ToString(), dynamicTiers.ToString(), extraParams);
        }

        /// <summary>
        /// Computes deterministic hash for a Color Palette Atlas operation.
        /// </summary>
        public static string ComputePaletteHash(IEnumerable<Material> materials, string shaderLayout, int atlasWidth, int atlasHeight, string platform = null)
        {
            string plat = platform ?? GetPlatformName();
            var tokens = new List<string> { "PaletteAtlas", shaderLayout, atlasWidth.ToString(), atlasHeight.ToString() };
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                tokens.Add(GetAssetIdentityHash(mat));
            }
            return ComputeCompositeHashWithPlatform(plat, tokens.ToArray());
        }

        /// <summary>
        /// Computes deterministic hash for a Texture2DArray operation.
        /// </summary>
        public static string ComputeTextureArrayHash(IEnumerable<Texture2D> textures, int width, int height, TextureFormat format, bool mipmaps, FilterMode filter, string platform = null)
        {
            string plat = platform ?? GetPlatformName();
            var tokens = new List<string> { "TextureArray", width.ToString(), height.ToString(), format.ToString(), mipmaps.ToString(), filter.ToString() };
            foreach (var tex in textures)
            {
                if (tex == null) continue;
                tokens.Add(GetAssetIdentityHash(tex));
            }
            return ComputeCompositeHashWithPlatform(plat, tokens.ToArray());
        }

        /// <summary>
        /// Computes deterministic hash for a single Texture optimization operation.
        /// </summary>
        public static string ComputeTextureHash(Texture2D texture, string passTag, int width, int height, string extraParams = "", string platform = null)
        {
            string plat = platform ?? GetPlatformName();
            string assetId = GetAssetIdentityHash(texture);
            return ComputeCompositeHashWithPlatform(plat, passTag ?? "SmartTexture", assetId, width.ToString(), height.ToString(), extraParams);
        }

        /// <summary>
        /// Computes deterministic hash for a Staged Virtual Material.
        /// </summary>
        public static string ComputeMaterialHash(Material originalMat, string passTag, IDictionary<string, float> floats, IDictionary<string, Vector4> vectors, IDictionary<string, Texture> textures, IEnumerable<string> keywords, string platform = null)
        {
            string plat = platform ?? GetPlatformName();
            var tokens = new List<string> { "StagedMat", GetAssetIdentityHash(originalMat), passTag ?? "" };
            if (originalMat != null)
            {
                tokens.Add($"matName:{originalMat.name}");
                if (originalMat.shader != null) tokens.Add($"shader:{originalMat.shader.name}");
            }
            if (floats != null)
            {
                foreach (var kvp in floats) tokens.Add($"{kvp.Key}:{kvp.Value:F4}");
            }
            if (vectors != null)
            {
                foreach (var kvp in vectors) tokens.Add($"{kvp.Key}:{kvp.Value}");
            }
            if (textures != null)
            {
                foreach (var kvp in textures) tokens.Add($"{kvp.Key}:{GetAssetIdentityHash(kvp.Value)}");
            }
            if (keywords != null)
            {
                foreach (var kw in keywords) tokens.Add($"kw:{kw}");
            }
            return ComputeCompositeHashWithPlatform(plat, tokens.ToArray());
        }

        #endregion

        #region Cache Retrieval & Storage

        /// <summary>
        /// Attempts to find a cached asset by category and hash key for the current or specified platform.
        /// </summary>
        public static bool TryGetCachedAsset<T>(string category, string hashKey, out T cachedAsset, string platform = null) where T : Object
        {
            return TryGetCachedAsset<T>(category, hashKey, null, out cachedAsset, platform);
        }

        /// <summary>
        /// Attempts to find a cached asset by category, hash key, and optional cleanName for exact matching.
        /// </summary>
        public static bool TryGetCachedAsset<T>(string category, string hashKey, string cleanName, out T cachedAsset, string platform = null) where T : Object
        {
            cachedAsset = null;
            if (string.IsNullOrEmpty(hashKey)) return false;

            string plat = platform ?? GetPlatformName();

            // 1. Check in-memory session cache
            string memKey = !string.IsNullOrEmpty(cleanName) 
                ? $"{plat}_{category}_{cleanName}_{hashKey}" 
                : $"{plat}_{category}_{hashKey}";
            if (MemoryCache.TryGetValue(memKey, out Object memObj) && memObj != null && memObj is T typedMemObj)
            {
                cachedAsset = typedMemObj;
                return true;
            }

            if (!string.IsNullOrEmpty(cleanName))
            {
                string genericMemKey = $"{plat}_{category}_{hashKey}";
                if (MemoryCache.TryGetValue(genericMemKey, out Object genMemObj) && genMemObj != null && genMemObj is T typedGenMemObj)
                {
                    cachedAsset = typedGenMemObj;
                    return true;
                }
            }

            // 2. Check on-disk cache directory
            string categoryDir = GetCategoryPath(category, plat);
            if (!Directory.Exists(categoryDir)) return false;

            // Check specific cleanName file first if provided
            if (!string.IsNullOrEmpty(cleanName))
            {
                string safeName = Regex.Replace(cleanName, @"[^a-zA-Z0-9_]", "_");
                if (safeName.Length > 24) safeName = safeName.Substring(0, 24);
                string[] exactFiles = Directory.GetFiles(categoryDir, $"{safeName}_{hashKey}.*");
                foreach (string file in exactFiles)
                {
                    if (file.EndsWith(".meta")) continue;
                    string unityPath = file.Replace('\\', '/');
                    T asset = AssetDatabase.LoadAssetAtPath<T>(unityPath);
                    if (asset != null)
                    {
                        MemoryCache[memKey] = asset;
                        cachedAsset = asset;
                        return true;
                    }
                }
            }

            string[] files = Directory.GetFiles(categoryDir, $"*_{hashKey}.*");
            foreach (string file in files)
            {
                if (file.EndsWith(".meta")) continue;
                string unityPath = file.Replace('\\', '/');
                T asset = AssetDatabase.LoadAssetAtPath<T>(unityPath);
                if (asset != null)
                {
                    MemoryCache[memKey] = asset;
                    cachedAsset = asset;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Saves an asset to the persistent deterministic per-platform cache.
        /// </summary>
        public static T SaveCachedAsset<T>(T asset, string category, string hashKey, string originalName = "", string platform = null) where T : Object
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            string plat = platform ?? GetPlatformName();
            EnsureDirectoriesExist(plat);

            string cleanName = !string.IsNullOrEmpty(originalName) ? originalName : asset.name;
            string extension = ".asset";

            if (typeof(T) == typeof(Material))
            {
                extension = ".mat";
                Material mat = (Material)(object)asset;
                if (mat != null && mat.HasProperty("_BlendMode") && mat.HasProperty("_AlphaToMask"))
                {
                    float blendMode = mat.GetFloat("_BlendMode");
                    mat.SetFloat("_AlphaToMask", blendMode == 1.0f ? 1.0f : 0.0f);
                }
            }
            else if (typeof(T) == typeof(Texture2D))
            {
                extension = ".asset";
                Texture2D tex = (Texture2D)(object)asset;
                if (cleanName.Contains("Palette") || category == PalettesCategory)
                {
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                }
            }

            string fullPath = GetAssetPath(category, hashKey, cleanName, extension, plat);

            // If an asset already exists at this path, update it safely without corrupting serialized/native data
            if (File.Exists(fullPath))
            {
                T existingAsset = AssetDatabase.LoadAssetAtPath<T>(fullPath);
                if (existingAsset != null)
                {
                    if (typeof(T) == typeof(Material))
                    {
                        Material existingMat = (Material)(object)existingAsset;
                        Material sourceMat = (Material)(object)asset;
                        existingMat.CopyPropertiesFromMaterial(sourceMat);
                        existingMat.shaderKeywords = sourceMat.shaderKeywords;
                        EditorUtility.SetDirty(existingMat);
                        AssetDatabase.SaveAssets();

                        string existingKey = $"{plat}_{category}_{hashKey}";
                        MemoryCache[existingKey] = existingAsset;
                        return existingAsset;
                    }
                    else if (typeof(T) == typeof(Mesh) || typeof(T) == typeof(Texture2D))
                    {
                        // Mesh native vertex buffers and Texture2D native pixel buffers cannot be updated via CopySerialized. Re-create asset cleanly.
                        AssetDatabase.DeleteAsset(fullPath);
                    }
                    else
                    {
                        EditorUtility.CopySerialized(asset, existingAsset);
                        EditorUtility.SetDirty(existingAsset);
                        AssetDatabase.SaveAssets();

                        string existingKey = $"{plat}_{category}_{hashKey}";
                        MemoryCache[existingKey] = existingAsset;
                        return existingAsset;
                    }
                }
                else
                {
                    AssetDatabase.DeleteAsset(fullPath);
                }
            }

            AssetDatabase.CreateAsset(asset, fullPath);

            T savedAsset = AssetDatabase.LoadAssetAtPath<T>(fullPath);

            string key = $"{plat}_{category}_{hashKey}";
            MemoryCache[key] = savedAsset != null ? savedAsset : asset;

            return savedAsset != null ? savedAsset : asset;
        }

        #endregion

        #region Cache Management

        public static void ClearMemoryCache()
        {
            MemoryCache.Clear();
        }

        public static void GetCacheStats(out int totalFiles, out long totalBytes, string platform = null)
        {
            totalFiles = 0;
            totalBytes = 0;

            string targetPath = string.IsNullOrEmpty(platform) ? BaseCachePath : GetPlatformCachePath(platform);

            if (!Directory.Exists(targetPath)) return;

            string[] files = Directory.GetFiles(targetPath, "*.*", SearchOption.AllDirectories);
            foreach (string file in files)
            {
                if (file.EndsWith(".meta")) continue;
                totalFiles++;
                var fi = new FileInfo(file);
                totalBytes += fi.Length;
            }
        }

        public static void PurgeCache(string category = null, string platform = null)
        {
            ClearMemoryCache();
            AssetDatabase.StartAssetEditing();
            try
            {
                if (string.IsNullOrEmpty(platform))
                {
                    // Purge entire cache across all platforms
                    if (Directory.Exists(BaseCachePath))
                    {
                        AssetDatabase.DeleteAsset(BaseCachePath.TrimEnd('/'));
                    }
                    EnsureDirectoriesExist();
                }
                else
                {
                    if (string.IsNullOrEmpty(category))
                    {
                        // Purge specific platform cache
                        string platPath = GetPlatformCachePath(platform).TrimEnd('/');
                        if (Directory.Exists(platPath))
                        {
                            AssetDatabase.DeleteAsset(platPath);
                        }
                        EnsureDirectoriesExist(platform);
                    }
                    else
                    {
                        // Purge specific category inside platform
                        string catPath = GetCategoryPath(category, platform).TrimEnd('/');
                        if (Directory.Exists(catPath))
                        {
                            AssetDatabase.DeleteAsset(catPath);
                        }
                        EnsureDirectoriesExist(platform);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
        }

        #endregion
    }

    /// <summary>
    /// Disposable scope that wraps AssetDatabase mutations in StartAssetEditing / StopAssetEditing,
    /// preventing per-file import freezes and committing all cache updates in a single batch.
    /// </summary>
    public class SynAssetDatabaseScope : IDisposable
    {
        private static bool isScopeActive = false;
        private readonly bool isRootScope;

        public SynAssetDatabaseScope()
        {
            if (!isScopeActive)
            {
                AssetDatabase.StartAssetEditing();
                isScopeActive = true;
                isRootScope = true;
            }
            else
            {
                isRootScope = false;
            }
        }

        public void Dispose()
        {
            if (isRootScope && isScopeActive)
            {
                try
                {
                    AssetDatabase.StopAssetEditing();
                }
                finally
                {
                    isScopeActive = false;
                    AssetDatabase.SaveAssets();
                }
            }
        }
    }
}
