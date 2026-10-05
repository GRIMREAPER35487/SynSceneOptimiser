using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    public class SynSceneOptimizerWindow : EditorWindow
    {
        private List<SynOptimizationPass> discoveredPasses = new List<SynOptimizationPass>();
        private SynSceneOptimizerSettings settingsInstance = new SynSceneOptimizerSettings();
        private Vector2 scrollPosition = Vector2.zero;
        private Vector2 fixesScrollPosition = Vector2.zero;
        private int selectedTab = 0;
        private readonly string[] tabNames = new string[] { "Optimizers", "General Fixes" };

        private double _lastStatsUpdateTime = -10.0;
        private int _cachedActivePlatCount;
        private long _cachedActivePlatBytes;
        private int _cachedPcCount;
        private long _cachedPcBytes;
        private int _cachedAndroidCount;
        private long _cachedAndroidBytes;
        private int _cachedIosCount;
        private long _cachedIosBytes;
        private string _cachedPlatformName = "";

        private void RefreshCacheStats()
        {
            string currentPlat = SynAssetCache.GetPlatformName();
            _cachedPlatformName = currentPlat;

            SynAssetCache.GetCacheStats(out _cachedActivePlatCount, out _cachedActivePlatBytes, currentPlat);
            SynAssetCache.GetCacheStats(out _cachedPcCount, out _cachedPcBytes, "PC");
            SynAssetCache.GetCacheStats(out _cachedAndroidCount, out _cachedAndroidBytes, "Android");
            SynAssetCache.GetCacheStats(out _cachedIosCount, out _cachedIosBytes, "iOS");
        }

        [MenuItem("Window/Synthos/Syn Scene Optimizer")]
        public static void ShowWindow()
        {
            var window = GetWindow<SynSceneOptimizerWindow>("Syn Scene Optimizer");
            window.minSize = new Vector2(450, 500);
            window.Show();
        }

        private void OnEnable()
        {
            selectedTab = EditorPrefs.GetInt("SynSceneOptimizer_SelectedTab", 0);
            DiscoverPasses();
            RefreshCacheStats();
        }

        private void DiscoverPasses()
        {
            discoveredPasses.Clear();
            Assembly assembly = Assembly.GetExecutingAssembly();
            Type[] types = assembly.GetTypes();

            foreach (Type type in types)
            {
                if (type.IsSubclassOf(typeof(SynOptimizationPass)) && !type.IsAbstract)
                {
                    try
                    {
                        var pass = (SynOptimizationPass)Activator.CreateInstance(type);
                        discoveredPasses.Add(pass);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError(string.Format("[SYN SCENE OPTIMIZER] Failed to instantiate pass {0}: {1}", type.Name, e.Message));
                    }
                }
            }

            // Sort by Priority ascending
            discoveredPasses.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        private void OnGUI()
        {
            EditorGUI.showMixedValue = false;

            // Title Header with Premium Aesthetics styling
            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("SYN SCENE OPTIMIZER", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Quick access to sub-tools
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Open VRAM Analyzer", GUILayout.Width(150), GUILayout.Height(20)))
            {
                SynVRAMAnalyzerWindow.ShowWindow();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            int newTab = GUILayout.Toolbar(selectedTab, tabNames, GUILayout.Height(25));
            if (newTab != selectedTab)
            {
                selectedTab = newTab;
                EditorPrefs.SetInt("SynSceneOptimizer_SelectedTab", selectedTab);
            }
            EditorGUILayout.Space(5);

            if (selectedTab == 0)
            {
                // Master enable/disable all override toggle
            GUILayout.BeginHorizontal();
            bool globalEnabled = SynSceneOptimizerSettings.GetBool("GlobalPassesEnabled", true);
            bool newGlobalEnabled = EditorGUILayout.ToggleLeft("Enable All Passes", globalEnabled, EditorStyles.boldLabel);
            if (newGlobalEnabled != globalEnabled)
            {
                SynSceneOptimizerSettings.SetBool("GlobalPassesEnabled", newGlobalEnabled);
            }
            GUILayout.EndHorizontal();

            // Automation settings
            GUILayout.BeginHorizontal();
            bool autoPlay = SynSceneOptimizerSettings.GetBool("AutoOptimizePlayMode", true);
            bool newAutoPlay = EditorGUILayout.ToggleLeft("Auto Run in Play Mode", autoPlay);
            if (newAutoPlay != autoPlay)
            {
                SynSceneOptimizerSettings.SetBool("AutoOptimizePlayMode", newAutoPlay);
            }

            bool autoBuild = SynSceneOptimizerSettings.GetBool("AutoOptimizeBuild", true);
            bool newAutoBuild = EditorGUILayout.ToggleLeft("Auto Run on Build", autoBuild);
            if (newAutoBuild != autoBuild)
            {
                SynSceneOptimizerSettings.SetBool("AutoOptimizeBuild", newAutoBuild);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool enableLogging = SynSceneOptimizerSettings.GetBool("EnableConsoleLogging", true);
            bool newEnableLogging = EditorGUILayout.ToggleLeft("Enable Console Logging", enableLogging);
            if (newEnableLogging != enableLogging)
            {
                SynSceneOptimizerSettings.SetBool("EnableConsoleLogging", newEnableLogging);
            }

            bool verboseLogging = SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false);
            bool newVerboseLogging = EditorGUILayout.ToggleLeft("Enable Verbose Logging", verboseLogging);
            if (newVerboseLogging != verboseLogging)
            {
                SynSceneOptimizerSettings.SetBool("EnableVerboseLogging", newVerboseLogging);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool forceInstancing = SynSceneOptimizerSettings.GetBool("ForceGPUInstancingOnBaked", true);
            bool newForceInstancing = EditorGUILayout.ToggleLeft("Force GPU Instancing on Baked Materials", forceInstancing);
            if (newForceInstancing != forceInstancing)
            {
                SynSceneOptimizerSettings.SetBool("ForceGPUInstancingOnBaked", newForceInstancing);
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Cache Management Panel (Per-Platform)
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("OPTIMIZATION CACHE (PER-PLATFORM)", EditorStyles.boldLabel);
            GUIContent refreshContent = EditorGUIUtility.IconContent("Refresh");
            if (refreshContent == null || refreshContent.image == null)
            {
                refreshContent = new GUIContent("Refresh", "Refresh cache statistics from disk");
            }
            else
            {
                refreshContent.tooltip = "Refresh cache statistics from disk";
            }
            if (GUILayout.Button(refreshContent, EditorStyles.miniButton, GUILayout.Width(28), GUILayout.Height(18)))
            {
                RefreshCacheStats();
            }
            GUILayout.EndHorizontal();
            
            string currentPlat = SynAssetCache.GetPlatformName();
            string platDisplayName = SynAssetCache.GetPlatformDisplayName();

            if (currentPlat != _cachedPlatformName)
            {
                RefreshCacheStats();
            }

            double activeMb = _cachedActivePlatBytes / (1024.0 * 1024.0);

            EditorGUILayout.LabelField($"Active Target Platform: {platDisplayName}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Active Platform Cache ({currentPlat}): {_cachedActivePlatCount} assets ({activeMb:F2} MB)", EditorStyles.miniLabel);

            // Per platform mini breakdown
            EditorGUILayout.LabelField($"Breakdown -> PC: {_cachedPcCount} files ({_cachedPcBytes / (1024.0 * 1024.0):F1} MB) | Android: {_cachedAndroidCount} files ({_cachedAndroidBytes / (1024.0 * 1024.0):F1} MB) | iOS: {_cachedIosCount} files ({_cachedIosBytes / (1024.0 * 1024.0):F1} MB)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Cache Path: {SynAssetCache.GetPlatformCachePath(currentPlat)}", EditorStyles.miniLabel);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"Clear {currentPlat} Cache", GUILayout.Height(22)))
            {
                if (EditorUtility.DisplayDialog($"Clear {currentPlat} Cache", $"Are you sure you want to clear the optimization cache for {platDisplayName}? Other platforms will remain intact.", "Yes, Clear Cache", "Cancel"))
                {
                    SynAssetCache.PurgeCache(null, currentPlat);
                    RefreshCacheStats();
                    Debug.Log($"[SYN SCENE OPTIMIZER] Optimizer Cache for {currentPlat} purged successfully.");
                }
            }

            if (GUILayout.Button("Clear All Platforms", GUILayout.Height(22)))
            {
                if (EditorUtility.DisplayDialog("Clear All Optimization Caches", "Are you sure you want to clear the optimization cache for ALL platforms (PC, Android, iOS)?", "Yes, Clear All", "Cancel"))
                {
                    SynAssetCache.PurgeCache();
                    RefreshCacheStats();
                    Debug.Log("[SYN SCENE OPTIMIZER] All Optimizer Caches purged successfully.");
                }
            }

            if (GUILayout.Button("Open Cache Folder", GUILayout.Height(22)))
            {
                SynAssetCache.EnsureDirectoriesExist(currentPlat);
                EditorUtility.RevealInFinder(SynAssetCache.GetPlatformCachePath(currentPlat));
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            EditorGUI.BeginDisabledGroup(!globalEnabled);
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            DrawPassList("Optimizers");
            EditorGUILayout.EndScrollView();
            EditorGUI.EndDisabledGroup();
            }
            else if (selectedTab == 1)
            {
                fixesScrollPosition = EditorGUILayout.BeginScrollView(fixesScrollPosition);
                DrawPassList("General Fixes");
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        }

        private void DrawPassList(string targetTab)
        {
            Dictionary<string, List<SynOptimizationPass>> groupedPasses = new Dictionary<string, List<SynOptimizationPass>>();
            foreach (var pass in discoveredPasses)
            {
                if (pass.IsHidden) continue;
                bool isGeneralFix = pass.Tab == "General Fixes";
                if (targetTab == "General Fixes" && !isGeneralFix) continue;
                if (targetTab != "General Fixes" && isGeneralFix) continue;

                if (!groupedPasses.ContainsKey(pass.Category))
                {
                    groupedPasses[pass.Category] = new List<SynOptimizationPass>();
                }
                groupedPasses[pass.Category].Add(pass);
            }

            if (groupedPasses.Count == 0)
            {
                EditorGUILayout.HelpBox($"No passes found for {targetTab}.", MessageType.Info);
                return;
            }

            foreach (var group in groupedPasses)
            {
                EditorGUILayout.LabelField(group.Key, EditorStyles.boldLabel);
                EditorGUI.indentLevel++;

                foreach (var pass in group.Value)
                {
                    string toggleKey = string.Format("Pass_{0}_Enabled", pass.Id);
                    bool isEnabled = SynSceneOptimizerSettings.GetBool(toggleKey, true);

                    GUILayout.BeginVertical(EditorStyles.helpBox);
                    
                    bool newEnabled = EditorGUILayout.ToggleLeft(
                        new GUIContent(pass.Name, pass.Description),
                        isEnabled,
                        EditorStyles.boldLabel
                    );

                    if (newEnabled != isEnabled)
                    {
                        SynSceneOptimizerSettings.SetBool(toggleKey, newEnabled);
                    }

                    if (newEnabled)
                    {
                        EditorGUI.indentLevel++;
                        float oldLabelWidth = EditorGUIUtility.labelWidth;
                        EditorGUIUtility.labelWidth = 230f;

                        pass.DrawGUI(settingsInstance);

                        EditorGUIUtility.labelWidth = oldLabelWidth;
                        EditorGUI.indentLevel--;
                    }

                    GUILayout.EndVertical();
                    EditorGUILayout.Space(2);
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(10);
            }
        }
    }
}
