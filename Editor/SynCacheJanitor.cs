using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Keeps the optimizer cache from growing forever. Every pipeline run records which cache files it read or
    /// wrote; files that no build has used for the retention period are deleted. The manifest lives in Library/
    /// so it is per-machine and never committed.
    /// </summary>
    public static class SynCacheJanitor
    {
        private const string ManifestPath = "Library/SynSceneOptimizer/CacheUsage.txt";

        public static int RetentionDays => Mathf.Max(1, SynSceneOptimizerSettings.GetInt("CacheRetentionDays", 30));

        /// <summary>
        /// Marks the given cache asset paths as used now.
        /// </summary>
        public static void RecordRun(IEnumerable<string> usedPaths)
        {
            var manifest = Load();
            long now = DateTime.UtcNow.Ticks;
            foreach (string path in usedPaths)
            {
                if (!string.IsNullOrEmpty(path)) manifest[path] = now;
            }
            Save(manifest);
        }

        /// <summary>
        /// Deletes cache files not used by any run in the last <paramref name="retentionDays"/> days.
        /// Files the manifest has never seen (e.g. created by older versions) get a full grace period first.
        /// </summary>
        public static int RemoveStaleEntries(int retentionDays)
        {
            var manifest = Load();
            long now = DateTime.UtcNow.Ticks;
            long cutoff = now - TimeSpan.FromDays(retentionDays).Ticks;

            var existing = new HashSet<string>();
            var stale = new List<string>();
            foreach (string platform in SynAssetCache.Platforms)
            {
                foreach (string category in SynAssetCache.Categories)
                {
                    string dir = $"{SynAssetCache.BaseCachePath}{platform}/{category}";
                    if (!Directory.Exists(dir)) continue;

                    foreach (string file in Directory.GetFiles(dir))
                    {
                        if (file.EndsWith(".meta")) continue;
                        string path = file.Replace('\\', '/');
                        existing.Add(path);

                        if (!manifest.TryGetValue(path, out long lastUsed))
                        {
                            manifest[path] = now;
                        }
                        else if (lastUsed < cutoff)
                        {
                            stale.Add(path);
                        }
                    }
                }
            }

            if (stale.Count > 0)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (string path in stale)
                    {
                        AssetDatabase.DeleteAsset(path);
                        manifest.Remove(path);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
                SynAssetCache.ClearMemoryCache();
            }

            // Forget files that were removed by other means (manual clears, purges)
            var forgotten = new List<string>();
            foreach (string path in manifest.Keys)
            {
                if (!existing.Contains(path)) forgotten.Add(path);
            }
            foreach (string path in forgotten) manifest.Remove(path);

            Save(manifest);
            return stale.Count;
        }

        /// <summary>
        /// Runs cleanup once the current build has finished (the build blocks the editor loop, so delayCall
        /// fires afterwards). Assets are never deleted while a build is still reading them.
        /// </summary>
        public static void ScheduleCleanupAfterBuild()
        {
            EditorApplication.delayCall += () =>
            {
                if (BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode) return;
                int removed = RemoveStaleEntries(RetentionDays);
                if (removed > 0)
                {
                    Debug.Log($"[SYN SCENE OPTIMIZER] Cache cleanup: removed {removed} cached assets unused for {RetentionDays}+ days.");
                }
            };
        }

        private static Dictionary<string, long> Load()
        {
            var manifest = new Dictionary<string, long>();
            if (!File.Exists(ManifestPath)) return manifest;

            try
            {
                foreach (string line in File.ReadAllLines(ManifestPath))
                {
                    int tab = line.LastIndexOf('\t');
                    if (tab <= 0) continue;
                    if (long.TryParse(line.Substring(tab + 1), out long ticks))
                    {
                        manifest[line.Substring(0, tab)] = ticks;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not read cache usage manifest: {e.Message}");
            }
            return manifest;
        }

        private static void Save(Dictionary<string, long> manifest)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath));
                var lines = new List<string>(manifest.Count);
                foreach (var kvp in manifest) lines.Add($"{kvp.Key}\t{kvp.Value}");
                File.WriteAllLines(ManifestPath, lines);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not write cache usage manifest: {e.Message}");
            }
        }
    }
}
