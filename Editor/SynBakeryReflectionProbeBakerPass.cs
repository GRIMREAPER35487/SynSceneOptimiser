using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Hooks into Bakery's lighting bake completion events and automatically triggers reflection probe baking
    /// so probes are never left unbaked, disabled, or cleared after baking lightmaps.
    /// </summary>
    [InitializeOnLoad]
    public class SynBakeryReflectionProbeBakerPass : SynOptimizationPass
    {
        public override string Id => "BakeryReflectionProbeBakerPass";
        public override string Name => "Auto Reflection Probe Baker (Bakery)";
        public override string Description => "Hooks into Bakery's lighting bake completion events and automatically triggers reflection probe baking so probes are never left unbaked or cleared.";
        public override string Category => "Lighting & Probes";
        public override int Priority => 10;
        public override string Tab => "General Fixes";

        private static bool isHooked = false;
        private static double lastBakeTriggerTime = 0.0;

        static SynBakeryReflectionProbeBakerPass()
        {
            // Delay initialization slightly to let editor and assemblies settle
            EditorApplication.delayCall += EnsureHooked;
        }

        public static Type GetBakeryRenderType()
        {
            Type ftType = Type.GetType("ftRenderLightmap, BakeryEditorAssembly");
            if (ftType != null) return ftType;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                ftType = asm.GetType("ftRenderLightmap");
                if (ftType != null) return ftType;
            }
            return null;
        }

        public static Type GetBakeryLightmapsType()
        {
            Type ftType = Type.GetType("ftLightmaps, BakeryRuntimeAssembly") ?? Type.GetType("ftLightmaps");
            if (ftType != null) return ftType;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                ftType = asm.GetType("ftLightmaps");
                if (ftType != null) return ftType;
            }
            return null;
        }

        public static bool IsBakeryInstalled()
        {
            return GetBakeryRenderType() != null;
        }

        public static void EnsureHooked()
        {
            if (isHooked) return;

            Type ftRenderType = GetBakeryRenderType();
            if (ftRenderType == null) return;

            try
            {
                EventInfo onFinishedFull = ftRenderType.GetEvent("OnFinishedFullRender", BindingFlags.Public | BindingFlags.Static);
                if (onFinishedFull != null)
                {
                    MethodInfo handlerMethod = typeof(SynBakeryReflectionProbeBakerPass).GetMethod(nameof(OnBakeryFinishedRender), BindingFlags.NonPublic | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(onFinishedFull.EventHandlerType, handlerMethod);
                    onFinishedFull.RemoveEventHandler(null, handler);
                    onFinishedFull.AddEventHandler(null, handler);
                }

                EventInfo onFinishedProbes = ftRenderType.GetEvent("OnFinishedProbes", BindingFlags.Public | BindingFlags.Static);
                if (onFinishedProbes != null)
                {
                    MethodInfo handlerMethod = typeof(SynBakeryReflectionProbeBakerPass).GetMethod(nameof(OnBakeryFinishedRender), BindingFlags.NonPublic | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(onFinishedProbes.EventHandlerType, handlerMethod);
                    onFinishedProbes.RemoveEventHandler(null, handler);
                    onFinishedProbes.AddEventHandler(null, handler);
                }

                EventInfo onPreFull = ftRenderType.GetEvent("OnPreFullRender", BindingFlags.Public | BindingFlags.Static);
                if (onPreFull != null)
                {
                    MethodInfo handlerMethod = typeof(SynBakeryReflectionProbeBakerPass).GetMethod(nameof(OnBakeryPreRender), BindingFlags.NonPublic | BindingFlags.Static);
                    Delegate handler = Delegate.CreateDelegate(onPreFull.EventHandlerType, handlerMethod);
                    onPreFull.RemoveEventHandler(null, handler);
                    onPreFull.AddEventHandler(null, handler);
                }

                isHooked = true;
                if (SynSceneOptimizerSettings.GetBool("EnableConsoleLogging", true))
                {
                    Debug.Log("[SYN SCENE OPTIMIZER] Auto Reflection Probe Baker successfully hooked into Bakery events.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SYN SCENE OPTIMIZER] Could not hook into Bakery events: " + ex.Message);
            }
        }

        private static void OnBakeryPreRender(object sender, EventArgs e)
        {
            bool isEnabled = SynSceneOptimizerSettings.GetBool("Pass_BakeryReflectionProbeBakerPass_Enabled", true);
            if (!isEnabled) return;

            // Make sure Bakery's own project setting is set so it doesn't suppress probes
            SetBakeryAutoRenderRefProbes(true);
        }

        private static void OnBakeryFinishedRender(object sender, EventArgs e)
        {
            bool isEnabled = SynSceneOptimizerSettings.GetBool("Pass_BakeryReflectionProbeBakerPass_Enabled", true);
            if (!isEnabled) return;

            // Debounce within 3 seconds
            if (EditorApplication.timeSinceStartup - lastBakeTriggerTime < 3.0) return;
            lastBakeTriggerTime = EditorApplication.timeSinceStartup;

            Debug.Log("[SYN SCENE OPTIMIZER] Bakery bake finished! Automatically triggering reflection probe bake...");

            // Queue slightly after Bakery coroutine completes
            EditorApplication.delayCall += () =>
            {
                TriggerBakeryReflectionProbeBake();
            };
        }

        public static void SetBakeryAutoRenderRefProbes(bool enable)
        {
            try
            {
                Type ftLightmapsType = GetBakeryLightmapsType();
                if (ftLightmapsType != null)
                {
                    MethodInfo getSettingsMethod = ftLightmapsType.GetMethod("GetProjectSettings", BindingFlags.Public | BindingFlags.Static);
                    if (getSettingsMethod != null)
                    {
                        object pstorage = getSettingsMethod.Invoke(null, null);
                        if (pstorage != null)
                        {
                            FieldInfo field = pstorage.GetType().GetField("autoRenderRefProbes", BindingFlags.Public | BindingFlags.Instance);
                            if (field != null && (bool)field.GetValue(pstorage) != enable)
                            {
                                field.SetValue(pstorage, enable);
                                EditorUtility.SetDirty((UnityEngine.Object)pstorage);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SYN SCENE OPTIMIZER] Failed to sync Bakery autoRenderRefProbes: " + ex.Message);
            }
        }

        public static void TriggerBakeryReflectionProbeBake()
        {
            // 1. Re-enable any reflection probes Bakery might have left disabled
            ReEnableAllReflectionProbes();

            Type ftRenderType = GetBakeryRenderType();
            if (ftRenderType != null)
            {
                try
                {
                    // Check if bake already in progress
                    FieldInfo inProgressField = ftRenderType.GetField("bakeInProgress", BindingFlags.Public | BindingFlags.Static);
                    if (inProgressField != null && (bool)inProgressField.GetValue(null))
                    {
                        Debug.Log("[SYN SCENE OPTIMIZER] Bakery is already actively processing a bake.");
                        return;
                    }

                    // Get Bakery window instance
                    FieldInfo instanceField = ftRenderType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    object bakeryInstance = instanceField != null ? instanceField.GetValue(null) : null;

                    if (bakeryInstance == null)
                    {
                        UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(ftRenderType);
                        if (windows != null && windows.Length > 0)
                        {
                            bakeryInstance = windows[0];
                        }
                    }

                    if (bakeryInstance != null)
                    {
                        MethodInfo renderProbesMethod = ftRenderType.GetMethod("RenderReflectionProbesButton", BindingFlags.Public | BindingFlags.Instance);
                        if (renderProbesMethod != null)
                        {
                            Debug.Log("[SYN SCENE OPTIMIZER] Firing Bakery RenderReflectionProbesButton...");
                            renderProbesMethod.Invoke(bakeryInstance, new object[] { false });
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SYN SCENE OPTIMIZER] Error firing Bakery probe bake, falling back to Unity: " + ex.Message);
                }
            }

            // 2. Fallback to Unity built-in probe bake if Bakery instance wasn't available
            BakeReflectionProbesUnityFallback();
        }

        public static void BakeReflectionProbesUnityFallback()
        {
            var bakeFunc = typeof(Lightmapping).GetMethod("BakeAllReflectionProbesSnapshots",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (bakeFunc != null)
            {
                Debug.Log("[SYN SCENE OPTIMIZER] Baking all reflection probes via Unity Lightmapping...");
                bakeFunc.Invoke(null, null);
            }
            else
            {
                Debug.LogWarning("[SYN SCENE OPTIMIZER] Lightmapping.BakeAllReflectionProbesSnapshots function not found.");
            }
        }

        public static int ReEnableAllReflectionProbes()
        {
            int reenabledCount = 0;
            var probes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>();
            foreach (var probe in probes)
            {
                if (probe != null && !probe.enabled && probe.gameObject.activeInHierarchy)
                {
                    probe.enabled = true;
                    EditorUtility.SetDirty(probe);
                    reenabledCount++;
                }
            }
            return reenabledCount;
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EnsureHooked();

            bool bakeryInstalled = IsBakeryInstalled();
            if (bakeryInstalled)
            {
                EditorGUILayout.HelpBox(
                    "Bakery is detected. When lighting finishes baking, this pass automatically catches Bakery's completion event and bakes all reflection probes.",
                    MessageType.Info
                );
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Bakery was not detected in this project. You can still manually bake reflection probes using the button below.",
                    MessageType.Warning
                );
            }

            // Sync with Bakery Project Settings toggle
            bool syncSettings = SynSceneOptimizerSettings.GetBool("BakeryRefl_SyncProjectSettings", true);
            bool newSyncSettings = EditorGUILayout.Toggle(
                new GUIContent("Sync Bakery Project Settings", "Ensures Bakery's native 'Always render reflection probes' setting stays enabled in Bakery Project Settings."),
                syncSettings
            );
            if (newSyncSettings != syncSettings)
            {
                SynSceneOptimizerSettings.SetBool("BakeryRefl_SyncProjectSettings", newSyncSettings);
                if (newSyncSettings)
                {
                    SetBakeryAutoRenderRefProbes(true);
                }
            }

            EditorGUILayout.Space(4);

            // Manual action buttons
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Bake Reflection Probes Now", GUILayout.Height(24)))
            {
                TriggerBakeryReflectionProbeBake();
            }

            if (GUILayout.Button("Re-Enable Probes", GUILayout.Height(24), GUILayout.Width(130)))
            {
                int count = ReEnableAllReflectionProbes();
                Debug.Log($"[SYN SCENE OPTIMIZER] Re-enabled {count} reflection probes in scene.");
            }
            GUILayout.EndHorizontal();
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            // Pipeline execution: Ensure any reflection probes disabled by previous bakes are re-enabled
            int reenabled = ReEnableAllReflectionProbes();
            if (reenabled > 0)
            {
                SynPipelineCompactor.LogChange("Reflection Probes", $"Re-enabled {reenabled} reflection probes.");
            }
        }
    }
}
