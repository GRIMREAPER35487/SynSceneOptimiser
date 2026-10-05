using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Automatically detects and migrates legacy cache and configuration data from earlier
    /// iterations (Assets/SynSceneOpti_v2 and Assets/SynSceneOptimiser) into the modern
    /// package-compliant project locations while preserving all asset GUIDs and scene links.
    /// </summary>
    [InitializeOnLoad]
    public static class SynLegacyMigration
    {
        private const string MigrationCompletedKey = "Synthos.SynSceneOptimizer.Migration_v1_0_0";
        private const string TargetSettingsPath = "Assets/SynSceneOptimizer/SynProtectionSettings.asset";

        static SynLegacyMigration()
        {
            EditorApplication.delayCall += CheckAndMigrateLegacyData;
        }

        [MenuItem("Tools/Synthos/Migrate Legacy Optimizer Data", false, 100)]
        public static void ForceMigration()
        {
            ExecuteMigration(true);
        }

        private static void CheckAndMigrateLegacyData()
        {
            ExecuteMigration(false);
        }

        private static void ExecuteMigration(bool force)
        {
            try
            {
                string[] legacyFolders = new[]
                {
                    "Assets/SynSceneOpti_v2",
                    "Assets/SynSceneOptimiser"
                };

                bool hasLegacy = false;
                foreach (var folder in legacyFolders)
                {
                    if (Directory.Exists(folder))
                    {
                        hasLegacy = true;
                        break;
                    }
                }

                // Skip if no legacy folders and already marked as done (unless forced)
                if (!hasLegacy && !force && EditorPrefs.GetBool(MigrationCompletedKey, false))
                {
                    return;
                }

                int migratedCount = 0;

                // 1. Migrate Protection Settings asset
                string[] possibleOldSettings = new[]
                {
                    "Assets/SynSceneOpti_v2/SynProtectionSettings.asset",
                    "Assets/SynSceneOptimiser/SynProtectionSettings.asset"
                };

                foreach (var oldSettingPath in possibleOldSettings)
                {
                    if (File.Exists(oldSettingPath) && oldSettingPath != TargetSettingsPath)
                    {
                        if (!File.Exists(TargetSettingsPath))
                        {
                            string targetDir = Path.GetDirectoryName(TargetSettingsPath).Replace('\\', '/');
                            if (!Directory.Exists(targetDir))
                            {
                                Directory.CreateDirectory(targetDir);
                            }

                            string moveErr = AssetDatabase.MoveAsset(oldSettingPath, TargetSettingsPath);
                            if (string.IsNullOrEmpty(moveErr))
                            {
                                Debug.Log($"[SYN SCENE OPTIMIZER] Successfully migrated protection settings: '{oldSettingPath}' -> '{TargetSettingsPath}'.");
                                migratedCount++;
                                break;
                            }
                            else
                            {
                                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not move protection settings '{oldSettingPath}': {moveErr}");
                            }
                        }
                    }
                }

                // 2. Migrate Persistent Cache folder (Meshes, Palettes, Textures)
                string targetCache = SynAssetCache.BaseCachePath.TrimEnd('/');
                string[] possibleOldCaches = new[]
                {
                    "Assets/SynSceneOpti_v2/Cache",
                    "Assets/SynSceneOptimiser/Cache"
                };

                foreach (var oldCachePath in possibleOldCaches)
                {
                    if (Directory.Exists(oldCachePath) && oldCachePath != targetCache)
                    {
                        if (!Directory.Exists(targetCache))
                        {
                            string moveErr = AssetDatabase.MoveAsset(oldCachePath, targetCache);
                            if (string.IsNullOrEmpty(moveErr))
                            {
                                Debug.Log($"[SYN SCENE OPTIMIZER] Successfully migrated optimization cache: '{oldCachePath}' -> '{targetCache}'.");
                                migratedCount++;
                            }
                            else
                            {
                                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not move cache folder '{oldCachePath}': {moveErr}");
                            }
                        }
                        else
                        {
                            // If target cache already exists, move individual categories/files
                            MigrateDirectoryContents(oldCachePath, targetCache, ref migratedCount);
                        }
                    }
                }

                // 3. Migrate Transient Cache reports
                string targetTransient = SynAssetCache.TransientCachePath.TrimEnd('/');
                string[] possibleOldTransients = new[]
                {
                    "Assets/SynSceneOpti_v2/TransientCache",
                    "Assets/SynSceneOptimiser/TransientCache"
                };

                foreach (var oldTransient in possibleOldTransients)
                {
                    if (Directory.Exists(oldTransient) && oldTransient != targetTransient)
                    {
                        if (!Directory.Exists(targetTransient))
                        {
                            Directory.CreateDirectory(targetTransient);
                        }

                        foreach (var file in Directory.GetFiles(oldTransient, "*.json"))
                        {
                            string destFile = Path.Combine(targetTransient, Path.GetFileName(file));
                            if (!File.Exists(destFile))
                            {
                                File.Copy(file, destFile);
                                migratedCount++;
                            }
                        }
                    }
                }

                // 4. Clean up legacy directories if empty of assets
                foreach (var folder in legacyFolders)
                {
                    if (Directory.Exists(folder))
                    {
                        string[] allFiles = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
                        int nonMetaCount = 0;
                        foreach (var f in allFiles)
                        {
                            if (!f.EndsWith(".meta")) nonMetaCount++;
                        }

                        if (nonMetaCount == 0)
                        {
                            AssetDatabase.DeleteAsset(folder);
                            Debug.Log($"[SYN SCENE OPTIMIZER] Cleaned up obsolete legacy directory '{folder}'.");
                        }
                    }
                }

                if (migratedCount > 0)
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    Debug.Log($"[SYN SCENE OPTIMIZER] Legacy migration complete! Successfully transferred {migratedCount} item(s).");
                }
                else if (force)
                {
                    Debug.Log("[SYN SCENE OPTIMIZER] Legacy migration check completed. No legacy assets needed migration.");
                }

                EditorPrefs.SetBool(MigrationCompletedKey, true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SYN SCENE OPTIMIZER] Error during legacy migration: {ex}");
            }
        }

        private static void MigrateDirectoryContents(string sourceDir, string targetDir, ref int migratedCount)
        {
            if (!Directory.Exists(sourceDir)) return;
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string dirName = Path.GetFileName(subDir);
                string destSubDir = Path.Combine(targetDir, dirName).Replace('\\', '/');
                MigrateDirectoryContents(subDir, destSubDir, ref migratedCount);
            }

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                if (file.EndsWith(".meta")) continue;
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(targetDir, fileName).Replace('\\', '/');
                if (!File.Exists(destFile))
                {
                    string moveErr = AssetDatabase.MoveAsset(file.Replace('\\', '/'), destFile);
                    if (string.IsNullOrEmpty(moveErr))
                    {
                        migratedCount++;
                    }
                }
            }
        }
    }
}
