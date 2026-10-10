using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Synthos.SynSceneOptimizer
{
    public class SynUploadHook : IProcessSceneWithReport
    {
        public int callbackOrder => -10000;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (Application.isPlaying) return;

            bool autoBuild = SynSceneOptimizerSettings.GetBool("AutoOptimizeBuild", true);
            if (!autoBuild) return;

            // Only run if the scene has a VRC_SceneDescriptor
            var descriptors = scene.GetRootGameObjects()
                .SelectMany(go => go.GetComponentsInChildren<VRC.SDKBase.VRC_SceneDescriptor>(true))
                .ToArray();
            if (descriptors.Length == 0) return;

            Debug.Log($"[SYN SCENE OPTIMIZER] Automatically optimizing scene '{scene.name}' in-place for Build.");
            SynAutomaticOptimizerPipeline.RunPipelineOnScene(scene, SynRunMode.Build);

            // VRChat SDK asset bundle builds do not always fire IPostprocessBuildWithReport; the build blocks the
            // editor loop, so this runs once it finishes and restores any temporary importer changes.
            EditorApplication.delayCall += SynImporterRevertOnLoad.RevertIfIdle;

            // Save VRAM report
            try
            {
                string reportPath = SynAssetCache.LastBuildVRAMReportPath;
                SynVRAMAnalyzerWindow.SaveSceneVRAMReport(scene, reportPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SYN VRAM ANALYZER] Failed to cache build VRAM report: " + e.Message);
            }
        }
    }

    public static class SynPlayHook
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void PlayHook()
        {
            bool autoPlay = SynSceneOptimizerSettings.GetBool("AutoOptimizePlayMode", true);
            if (!autoPlay) return;

            Scene scene = SceneManager.GetActiveScene();
            
            // Only run if the scene has a VRC_SceneDescriptor
            var descriptors = scene.GetRootGameObjects()
                .SelectMany(go => go.GetComponentsInChildren<VRC.SDKBase.VRC_SceneDescriptor>(true))
                .ToArray();
            if (descriptors.Length == 0) return;

            Debug.Log($"[SYN SCENE OPTIMIZER] Automatically optimizing active scene '{scene.name}' in-place for Play Mode.");
            SynAutomaticOptimizerPipeline.RunPipelineOnScene(scene, SynRunMode.PlayMode);

            // Save VRAM report
            try
            {
                string reportPath = SynAssetCache.LastPlayModeVRAMReportPath;
                SynVRAMAnalyzerWindow.SaveSceneVRAMReport(scene, reportPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SYN VRAM ANALYZER] Failed to cache playmode VRAM report: " + e.Message);
            }
        }
    }

    public class SynPostprocessBuildHook : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            // Revert temporary mipmap streaming settings
            SynAutomaticOptimizerPipeline.RevertMipStreamingTextures();

            // Clean up session memory and query cache after build completes
            SynAssetCache.ClearMemoryCache();
            SynSceneQuery.ClearCache();
        }
    }

    [InitializeOnLoad]
    public static class SynPlayModeCleanupHook
    {
        static SynPlayModeCleanupHook()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                // Revert temporary mipmap streaming settings
                SynAutomaticOptimizerPipeline.RevertMipStreamingTextures();

                SynAssetCache.ClearMemoryCache();
                SynSceneQuery.ClearCache();
            }
        }
    }

    public static class SynAutomaticOptimizerPipeline
    {
        public static void RevertMipStreamingTextures()
        {
            SynEnableMipStreamingPass.RevertImporterChanges();
        }

        public static void RunPipelineOnScene(Scene scene)
        {
            RunPipelineOnScene(scene, Application.isPlaying ? SynRunMode.PlayMode : SynRunMode.Build);
        }

        public static void RunPipelineOnScene(Scene scene, SynRunMode mode)
        {
            SynProgressWindow progress = null;
            var failedPasses = new List<string>();
            var report = new SynRunReport
            {
                Mode = mode.ToString(),
                SceneName = scene.name,
                Platform = SynAssetCache.GetPlatformName(),
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            var totalTimer = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                SynProtectionData.BeginRun(scene);

                if (!Application.isBatchMode)
                {
                    progress = SynProgressWindow.Create(mode == SynRunMode.Preview ? "Syn Scene Optimizer - Preview" : "Syn Scene Optimizer");
                    progress.UpdateProgress(0.02f, "Preparing optimization pipeline...");
                }

                using (new SynAssetDatabaseScope())
                {
                    // Check if optimizer settings changed since last build/run, auto-clear cache if altered
                    string currentSig = SynSceneOptimizerSettings.GetSettingsSignature();
                    string platform = SynAssetCache.GetPlatformName();
                    // Stored per project (Library/) because the cache it guards is per project
                    string lastSigPath = $"Library/SynSceneOptimizer/SettingsSignature_{platform}.txt";
                    string lastSig = File.Exists(lastSigPath) ? File.ReadAllText(lastSigPath) : "";

                    if (!string.IsNullOrEmpty(lastSig) && lastSig != currentSig)
                    {
                        Debug.Log($"[SYN SCENE OPTIMIZER] Optimizer settings changed! Automatically clearing '{platform}' cache for fresh build.");
                        SynAssetCache.PurgeCache(null, platform);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(lastSigPath));
                    File.WriteAllText(lastSigPath, currentSig);

                    // Clear query cache and start staging
                    SynSceneQuery.ClearCache();
                    SynPipelineCompactor.BeginStagingContext(scene);

                    List<Renderer> renderers = SynSceneQuery.GetAllRenderers(scene);

                    // Filter out GameObjects marked as protected by user settings
                    int initialCount = renderers.Count;
                    renderers.RemoveAll(r => r == null || SynProtectionData.IsProtected(r.gameObject));
                    int protectedCount = initialCount - renderers.Count;
                    if (protectedCount > 0)
                    {
                        Debug.Log($"[SYN SCENE OPTIMIZER] Protected Objects: Shielded {protectedCount} renderers belonging to protected GameObjects.");
                    }

                    bool globalEnabled = SynSceneOptimizerSettings.GetBool("GlobalPassesEnabled", true);
                    if (globalEnabled)
                    {
                        // Discovers all passes reflectively and sorts by Priority
                        List<SynOptimizationPass> passes = GetSortedPasses().FindAll(p => p.IsEnabled);
                        int totalPasses = passes.Count;

                        for (int index = 0; index < passes.Count; index++)
                        {
                            var pass = passes[index];
                            var result = new SynPassResult { Name = pass.Name, Status = "OK" };
                            report.Passes.Add(result);

                            if (report.Cancelled)
                            {
                                result.Status = "Cancelled";
                                continue;
                            }

                            if (mode == SynRunMode.Preview && !pass.RunInPreview)
                            {
                                result.Status = "Skipped";
                                result.Messages.Add("Not run in previews (changes things outside the scene); applied during real builds.");
                                continue;
                            }

                            if (progress != null && progress.UpdateProgress((index + 1f) / (totalPasses + 1f), $"[{index + 1}/{totalPasses}] {pass.Name}..."))
                            {
                                report.Cancelled = true;
                                result.Status = "Cancelled";
                                continue;
                            }

                            int logStart = SynPipelineCompactor.PipelineLogSummary.Count;
                            var passTimer = System.Diagnostics.Stopwatch.StartNew();
                            try
                            {
                                pass.Execute(scene, renderers);
                            }
                            catch (Exception e)
                            {
                                string errMsg = string.Format("Failed to execute pass {0}: {1}", pass.Name, e.ToString());
                                SynPipelineCompactor.LogChange(pass.Name, "ERROR: " + errMsg);
                                Debug.LogError("[SYN SCENE OPTIMIZER] " + errMsg);
                                failedPasses.Add(pass.Name);
                                result.Status = "Failed";
                            }
                            result.Seconds = passTimer.Elapsed.TotalSeconds;
                            for (int m = logStart; m < SynPipelineCompactor.PipelineLogSummary.Count; m++)
                            {
                                result.Messages.Add(SynPipelineCompactor.PipelineLogSummary[m].Trim());
                            }
                        }
                    }

                    // A cancelled build must not ship a partially optimized scene
                    if (report.Cancelled && mode == SynRunMode.Build)
                    {
                        totalTimer.Stop();
                        report.TotalSeconds = totalTimer.Elapsed.TotalSeconds;
                        report.Save();
                        throw new BuildFailedException("[SYN SCENE OPTIMIZER] Build cancelled by user during scene optimization.");
                    }

                    if (progress != null)
                    {
                        progress.UpdateProgress(0.98f, "Committing scene optimization changes...");
                    }

                    // Commit final baked results and re-link references
                    SynPipelineCompactor.CommitStagingContext(scene);
                }

                // Record which cache files this run used; stale ones are removed after builds finish
                SynCacheJanitor.RecordRun(SynAssetCache.GetSessionAssetPaths());
                if (mode == SynRunMode.Build)
                {
                    SynCacheJanitor.ScheduleCleanupAfterBuild();
                }

                totalTimer.Stop();
                report.TotalSeconds = totalTimer.Elapsed.TotalSeconds;
                report.Save();
                if (mode != SynRunMode.Preview && SynSceneOptimizerSettings.GetBool("ShowReportAfterRun", false))
                {
                    EditorApplication.delayCall += SynRunReportWindow.ShowReport;
                }

                if (failedPasses.Count > 0)
                {
                    string summary = $"{failedPasses.Count} optimization pass(es) failed partway: {string.Join(", ", failedPasses)}. " +
                                     "Their changes may be incomplete. See the errors above for details.";

                    // A half-applied pass can ship a half-optimized world (e.g. half-palettized), so builds stop by default
                    if (mode == SynRunMode.Build && SynSceneOptimizerSettings.GetBool("StopBuildOnPassError", true))
                    {
                        throw new BuildFailedException("[SYN SCENE OPTIMIZER] Build stopped: " + summary +
                            " Fix or disable the failing pass, or turn off 'Stop Build If A Pass Fails' in the Syn Scene Optimizer window.");
                    }

                    Debug.LogError("[SYN SCENE OPTIMIZER] " + summary);
                }
            }
            finally
            {
                SynProtectionData.EndRun();
                if (progress != null)
                {
                    progress.Close();
                }
            }
        }

        /// <summary>
        /// Dry run: saves a temporary copy of the active scene, runs the pipeline on that copy, measures VRAM before
        /// and after, then closes and deletes the copy. The user's scene is never modified. Cached assets created
        /// along the way are kept and reused by the next real build.
        /// </summary>
        public static void RunPreview()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Preview Optimization", "Exit Play Mode before running a preview.", "OK");
                return;
            }

            Scene source = SceneManager.GetActiveScene();
            if (!source.IsValid() || !source.isLoaded) return;

            SynVramTotals before = SynVRAMAnalyzerWindow.MeasureActiveScene();

            string tempPath = SynAssetCache.TransientCachePath + "SynOptimizerPreview.unity";
            if (!EditorSceneManager.SaveScene(source, tempPath, true))
            {
                Debug.LogError("[SYN SCENE OPTIMIZER] Preview: could not save a temporary copy of the scene.");
                return;
            }

            Scene preview = default;
            try
            {
                preview = EditorSceneManager.OpenScene(tempPath, OpenSceneMode.Additive);
                SceneManager.SetActiveScene(preview);

                RunPipelineOnScene(preview, SynRunMode.Preview);

                SynVramTotals after = SynVRAMAnalyzerWindow.MeasureActiveScene();
                var report = SynRunReport.Load();
                if (report != null)
                {
                    report.SceneName = source.name;
                    report.VramBeforeBytes = before.Total;
                    report.VramAfterBytes = after.Total;
                    report.TextureBeforeBytes = before.Textures;
                    report.TextureAfterBytes = after.Textures;
                    report.MeshBeforeBytes = before.Meshes;
                    report.MeshAfterBytes = after.Meshes;
                    report.Save();
                }
            }
            finally
            {
                if (source.IsValid()) SceneManager.SetActiveScene(source);
                if (preview.IsValid()) EditorSceneManager.CloseScene(preview, true);
                AssetDatabase.DeleteAsset(tempPath);
                SynAssetCache.ClearMemoryCache();
                SynSceneQuery.ClearCache();
            }

            SynRunReportWindow.ShowReport();
        }

        private static List<SynOptimizationPass> GetSortedPasses()
        {
            var list = new List<SynOptimizationPass>();
            Assembly assembly = Assembly.GetExecutingAssembly();
            Type[] types = assembly.GetTypes();

            foreach (Type type in types)
            {
                if (type.IsSubclassOf(typeof(SynOptimizationPass)) && !type.IsAbstract)
                {
                    try
                    {
                        var pass = (SynOptimizationPass)Activator.CreateInstance(type);
                        list.Add(pass);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError(string.Format("[SYN SCENE OPTIMIZER] Failed to instantiate pass {0}: {1}", type.Name, e.Message));
                    }
                }
            }

            // Id breaks priority ties so pass order is the same on every machine and every run
            list.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : string.CompareOrdinal(a.Id, b.Id));
            return list;
        }
    }
}
