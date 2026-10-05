using System;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Unified asset registration facade for Syn Scene Optimizer.
    /// Routes all asset storage directly through SynAssetCache for deterministic, collision-proof caching.
    /// </summary>
    public static class SynAssetRegistry
    {
        private const string LegacyTransientPath = "Assets/SynSceneOpti_v2/Editor/TransientCache/";

        public static T RegisterTempAsset<T>(T asset, string fileName) where T : UnityEngine.Object
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            // Determine appropriate category
            string category = SynAssetCache.OtherCategory;
            if (typeof(T) == typeof(Mesh))
            {
                category = SynAssetCache.MeshesCategory;
            }
            else if (typeof(T) == typeof(Material))
            {
                category = SynAssetCache.MaterialsCategory;
            }
            else if (typeof(T) == typeof(Texture2D))
            {
                category = fileName.Contains("Palette") ? SynAssetCache.PalettesCategory : SynAssetCache.AtlasesCategory;
            }
            else if (typeof(T) == typeof(Texture2DArray))
            {
                category = SynAssetCache.TextureArraysCategory;
            }

            // Compute hash from fileName or asset signature
            string hashKey = SynAssetCache.ComputeCompositeHash("LegacyReg", fileName, asset.name);
            return SynAssetCache.SaveCachedAsset(asset, category, hashKey, fileName);
        }

        /// <summary>
        /// Cleans up any legacy transient folder from previous versions and clears in-memory caches.
        /// </summary>
        public static void FlushTransientCache()
        {
            SynAssetCache.ClearMemoryCache();

            // Clean legacy transient folder if present
            if (Directory.Exists(LegacyTransientPath))
            {
                AssetDatabase.DeleteAsset(LegacyTransientPath.TrimEnd('/'));
            }

            // Clean active transient folder if present
            string activeTransient = SynAssetCache.TransientCachePath;
            if (Directory.Exists(activeTransient))
            {
                AssetDatabase.DeleteAsset(activeTransient.TrimEnd('/'));
            }
        }
    }
}
