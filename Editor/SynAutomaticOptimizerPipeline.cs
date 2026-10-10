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
            SynAutomaticOptimizerPipeline.RunPipelineOnScene(scene);

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
            SynAutomaticOptimizerPipeline.RunPipelineOnScene(scene);

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
            SynProgressWindow progress = null;
            var failedPasses = new List<string>();
            try
            {
                SynProtectionData.BeginRun(scene);

                if (!Application.isPlaying && !Application.isBatchMode)
                {
                    progress = SynProgressWindow.Create("SYN SCENE OPTIMIZER");
                    progress.UpdateProgress(0.05f, "Preparing optimization pipeline...");
                }

                using (new SynAssetDatabaseScope())
                {
                    // Check if optimizer settings changed since last build/run, auto-clear cache if altered
                    string currentSig = SynSceneOptimizerSettings.GetSettingsSignature();
                    string platform = SynAssetCache.GetPlatformName();
                    string lastSigKey = "SynLastSettingsSig_" + platform;
                    string lastSig = EditorPrefs.GetString(lastSigKey, "");

                    if (!string.IsNullOrEmpty(lastSig) && lastSig != currentSig)
                    {
                        Debug.Log($"[SYN SCENE OPTIMIZER] Optimizer settings changed! Automatically clearing '{platform}' cache for fresh build.");
                        SynAssetCache.PurgeCache(null, platform);
                    }
                    EditorPrefs.SetString(lastSigKey, currentSig);

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
                        List<SynOptimizationPass> passes = GetSortedPasses();
                        int totalPasses = passes.Count;
                        int activePassIndex = 0;

                        foreach (var pass in passes)
                        {
                            if (pass.IsEnabled)
                            {
                                activePassIndex++;
                                float progressVal = (float)activePassIndex / Mathf.Max(1, totalPasses);
                                if (progress != null)
                                {
                                    progress.UpdateProgress(progressVal, $"[{activePassIndex}/{totalPasses}] Executing {pass.Name}...");
                                }

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
                                }
                            }
                        }
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
                if (!Application.isPlaying)
                {
                    SynCacheJanitor.ScheduleCleanupAfterBuild();
                }

                if (failedPasses.Count > 0)
                {
                    string summary = $"{failedPasses.Count} optimization pass(es) failed partway: {string.Join(", ", failedPasses)}. " +
                                     "Their changes may be incomplete. See the errors above for details.";

                    // A half-applied pass can ship a half-optimized world (e.g. half-palettized), so builds stop by default
                    if (!Application.isPlaying && SynSceneOptimizerSettings.GetBool("StopBuildOnPassError", true))
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
