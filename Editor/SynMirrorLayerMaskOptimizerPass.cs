using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimization pass that sanitizes VRChat Mirror culling masks to exclude high-overhead, non-essential layers.
    /// </summary>
    public class SynMirrorLayerMaskOptimizerPass : SynOptimizationPass
    {
        public override string Id => "synthos.mirror_layer_optimizer";
        public override string Name => "Mirror Layer Mask Stripping";
        public override string Description => "Finds VRChat mirrors and strips heavy, non-essential layers (like UI, Water, Hand Gestures, and custom heavy layers) from their culling masks.";

        public override int Priority => 20; // Run early-mid
        public override string Category => "Structural Cleanup";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            // Fetch settings
            bool stripUI = SynSceneOptimizerSettings.GetBool("Mirrors_StripUI", true);
            bool stripWater = SynSceneOptimizerSettings.GetBool("Mirrors_StripWater", true);
            bool stripStereo = SynSceneOptimizerSettings.GetBool("Mirrors_StripStereo", true);
            bool logOptimized = SynSceneOptimizerSettings.GetBool("Mirrors_LogOptimized", true);
            string customStripInput = SynSceneOptimizerSettings.GetString("Mirrors_CustomStrip", "");
            bool verbose = SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false);

            // Build layer mask of layers to strip
            int layersToStripMask = 0;

            if (stripUI)
            {
                int uiLayer = LayerMask.NameToLayer("UI");
                int uiMenuLayer = LayerMask.NameToLayer("UiMenu");
                if (uiLayer >= 0) layersToStripMask |= (1 << uiLayer);
                if (uiMenuLayer >= 0) layersToStripMask |= (1 << uiMenuLayer);
            }
            if (stripWater)
            {
                int waterLayer = LayerMask.NameToLayer("Water");
                if (waterLayer >= 0) layersToStripMask |= (1 << waterLayer);
            }
            if (stripStereo)
            {
                int stereoLeftLayer = LayerMask.NameToLayer("StereoLeft");
                int stereoRightLayer = LayerMask.NameToLayer("StereoRight");
                if (stereoLeftLayer >= 0) layersToStripMask |= (1 << stereoLeftLayer);
                if (stereoRightLayer >= 0) layersToStripMask |= (1 << stereoRightLayer);
            }

            // Custom layers
            if (!string.IsNullOrWhiteSpace(customStripInput))
            {
                var split = customStripInput.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var s in split)
                {
                    string trimmed = s.Trim();
                    int layerIndex = LayerMask.NameToLayer(trimmed);
                    if (layerIndex >= 0 && layerIndex <= 31)
                    {
                        layersToStripMask |= (1 << layerIndex);
                    }
                    else
                    {
                        if (verbose)
                        {
                            Debug.LogWarning($"[MirrorOptimizer] Custom layer '{trimmed}' not found in project. Skipping.");
                        }
                    }
                }
            }

            if (layersToStripMask == 0) return;

            int totalMirrors = 0;
            int optimizedCount = 0;

            var rootObjects = scene.GetRootGameObjects();
            var behaviours = new List<MonoBehaviour>();

            foreach (var root in rootObjects)
            {
                if (root != null)
                {
                    behaviours.AddRange(root.GetComponentsInChildren<MonoBehaviour>(true));
                }
            }

            foreach (var c in behaviours)
            {
                if (c == null || SynProtectionData.IsProtected(c.gameObject)) continue;

                string typeName = c.GetType().FullName;
                if (typeName == "VRC.SDKBase.VRC_MirrorReflection" || 
                    typeName == "VRC.SDK3.Components.VRCMirrorReflection" || 
                    typeName == "VRCSDK2.VRC_MirrorReflection")
                {
                    totalMirrors++;

                    var reflectLayersProp = c.GetType().GetProperty("reflectLayers", BindingFlags.Public | BindingFlags.Instance);
                    if (reflectLayersProp != null)
                    {
                        try
                        {
                            LayerMask originalMask = (LayerMask)reflectLayersProp.GetValue(c);
                            
                            // Check if mirror has any of the target strip layers enabled
                            if ((originalMask.value & layersToStripMask) != 0)
                            {
                                int newMaskValue = originalMask.value & ~layersToStripMask;
                                reflectLayersProp.SetValue(c, (LayerMask)newMaskValue);
                                EditorUtility.SetDirty(c.gameObject);
                                optimizedCount++;

                                if (logOptimized)
                                {
                                    // Log details about which layers were stripped
                                    var strippedNames = new List<string>();
                                    for (int i = 0; i < 32; i++)
                                    {
                                        if ((layersToStripMask & (1 << i)) != 0 && (originalMask.value & (1 << i)) != 0)
                                        {
                                            string layerName = LayerMask.LayerToName(i);
                                            strippedNames.Add(string.IsNullOrEmpty(layerName) ? $"Layer {i}" : layerName);
                                        }
                                    }
                                    Debug.Log($"[MirrorOptimizer] Stripped layers [<b>{string.Join(", ", strippedNames)}</b>] from Mirror culling mask on <b>{c.gameObject.name}</b>.");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[MirrorOptimizer] Error accessing reflectLayers property on {c.gameObject.name}: {e.Message}");
                        }
                    }
                }
            }

            if (optimizedCount > 0)
            {
                SynPipelineCompactor.LogChange(
                    "Mirror Layer Mask Stripping",
                    string.Format("Sanitized culling masks on {0}/{1} scene mirrors.", optimizedCount, totalMirrors)
                );
            }
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            bool stripUI = SynSceneOptimizerSettings.GetBool("Mirrors_StripUI", true);
            bool newStripUI = EditorGUILayout.Toggle(new GUIContent("Strip UI Layers", "If checked, strips 'UI' (Layer 5) and 'UiMenu' (Layer 12) from mirrors. UI elements do not need to be rendered in mirrors."), stripUI);
            if (newStripUI != stripUI)
            {
                SynSceneOptimizerSettings.SetBool("Mirrors_StripUI", newStripUI);
            }

            bool stripWater = SynSceneOptimizerSettings.GetBool("Mirrors_StripWater", true);
            bool newStripWater = EditorGUILayout.Toggle(new GUIContent("Strip Water Layer", "If checked, strips 'Water' (Layer 4) from mirrors. Water shaders are highly demanding and unnecessary in reflections."), stripWater);
            if (newStripWater != stripWater)
            {
                SynSceneOptimizerSettings.SetBool("Mirrors_StripWater", newStripWater);
            }

            bool stripStereo = SynSceneOptimizerSettings.GetBool("Mirrors_StripStereo", true);
            bool newStripStereo = EditorGUILayout.Toggle(new GUIContent("Strip Stereo Eye Layers", "If checked, strips VR stereo eye rendering layers ('StereoLeft' and 'StereoRight') from mirrors, which are redundant in standard mirrors."), stripStereo);
            if (newStripStereo != stripStereo)
            {
                SynSceneOptimizerSettings.SetBool("Mirrors_StripStereo", newStripStereo);
            }

            bool logOpt = SynSceneOptimizerSettings.GetBool("Mirrors_LogOptimized", true);
            bool newLogOpt = EditorGUILayout.Toggle(new GUIContent("Log Optimized Mirrors", "If checked, prints details about which layers were stripped from mirrors in the scene."), logOpt);
            if (newLogOpt != logOpt)
            {
                SynSceneOptimizerSettings.SetBool("Mirrors_LogOptimized", newLogOpt);
            }

            string customStrip = SynSceneOptimizerSettings.GetString("Mirrors_CustomStrip", "");
            string newCustomStrip = EditorGUILayout.TextField(new GUIContent("Custom Layers (CSV)", "Comma-separated list of additional layer names to strip from mirrors (e.g. Shadows, Effects, Particles)."), customStrip);
            if (newCustomStrip != customStrip)
            {
                SynSceneOptimizerSettings.SetString("Mirrors_CustomStrip", newCustomStrip);
            }
        }
    }
}
