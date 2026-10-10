using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Meshia.MeshSimplification;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimization pass that automatically decimates high-poly meshes at build time
    /// using the Meshia Mesh Simplification package, supporting PC/Mobile profiles, Scene Triangle Capping,
    /// a Custom Preservation List (with gentle ratio reduction), and a Skip List (never decimate).
    /// </summary>
    public class SynMeshSimplifierPass : SynOptimizationPass
    {
        public override string Id => "synthos.mesh_simplifier";
        public override string Name => "Mesh Simplifier (Powered by Meshia)";
        public override string Description => "Scans the scene for meshes exceeding a triangle threshold and simplifies them to reduce build size and improve runtime performance while preserving lightmaps and borders.";
        public override string Category => "Optimization";
        public override int Priority => 9000; // Run very late

        // Static fields for dry run results caching
        private static bool showDryRunResults = false;
        private static int dryRunUnsimplifiable = 0;
        private static int dryRunSimplifiable = 0;
        private static float dryRunRatio = 0f;
        private static int dryRunSimplifiableCount = 0;
        private static int dryRunTotalRenderers = 0;
        private static int dryRunPredictedTotal = 0;

        // Custom Preservation List (with custom decimation ratio option)
        private static List<SynPersistentObjectReference> localPreserveList => SynProtectionData.GetInstance().simplifierPreserveList;

        // Skip List (absolutely untouched / 100% triangles retained)
        private static List<SynPersistentObjectReference> localSkipList => SynProtectionData.GetInstance().simplifierSkipList;

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EnsureLocalPreserveListLoaded();
            EnsureLocalSkipListLoaded();

            // 1. Target Platform Profile Selection
            string targetPlatform = SynSceneOptimizerSettings.GetString("MeshSimplifier_TargetPlatform", "Auto");
            string[] platforms = { "Auto (Active Build Target)", "Force PC (Windows)", "Force Mobile (Quest, Android, iOS)" };
            int platformIndex = targetPlatform == "PC" ? 1 : (targetPlatform == "Mobile" ? 2 : 0);
            int newPlatformIndex = EditorGUILayout.Popup(
                new GUIContent("Target Platform Profile", "Select which platform settings profile to execute. 'Auto' uses the current active build target."), 
                platformIndex, 
                platforms
            );
            if (newPlatformIndex != platformIndex)
            {
                string val = newPlatformIndex == 1 ? "PC" : (newPlatformIndex == 2 ? "Mobile" : "Auto");
                SynSceneOptimizerSettings.SetString("MeshSimplifier_TargetPlatform", val);
                showDryRunResults = false; // Reset prediction on switch
            }

            EditorGUILayout.Space(5);

            // Display GUI Warning if last run overflowed the budget
            bool lastRunOverflowed = EditorPrefs.GetBool("MeshSimplifier_LastRunOverflowed", false);
            if (lastRunOverflowed)
            {
                int lastRunUnsimplifiable = EditorPrefs.GetInt("MeshSimplifier_LastRunUnsimplifiable", 0);
                int lastRunCap = EditorPrefs.GetInt("MeshSimplifier_LastRunCap", 250000);
                
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.HelpBox(
                    $"[BUDGET OVERFLOW WARNING]\n" +
                    $"Your scene's unsimplifiable meshes ({lastRunUnsimplifiable:N0} tris) already exceed your target cap of {lastRunCap:N0} tris.\n\n" +
                    $"The optimizer was forced to decimate all other meshes to the absolute 10% safety limit.\n\n" +
                    $"To fix this, raise your Target Cap or lower your Min Triangle threshold.", 
                    MessageType.Warning
                );
                
                int suggestedCap = lastRunUnsimplifiable + 50000;
                if (GUILayout.Button($"Increase Target Cap to {suggestedCap:N0} Tris"))
                {
                    SynSceneOptimizerSettings.SetInt("MeshSimplifier_Mobile_SceneTriangleCap", suggestedCap);
                    EditorPrefs.SetBool("MeshSimplifier_LastRunOverflowed", false);
                    EditorUtility.SetDirty(SynProtectionData.GetInstance()); // Force GUI repaint
                    showDryRunResults = false;
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(5);
            }

            // 2. PC Settings Profile
            EditorGUILayout.LabelField("PC Settings Profile", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            bool pcEnabled = SynSceneOptimizerSettings.GetBool("MeshSimplifier_PC_Enabled", true);
            bool newPcEnabled = EditorGUILayout.Toggle(new GUIContent("Enable on PC", "Should mesh simplification run when building/debugging for PC Standalone?"), pcEnabled);
            if (newPcEnabled != pcEnabled)
            {
                SynSceneOptimizerSettings.SetBool("MeshSimplifier_PC_Enabled", newPcEnabled);
                showDryRunResults = false;
            }

            EditorGUI.BeginDisabledGroup(!newPcEnabled);
            int pcMinTriangleCount = SynSceneOptimizerSettings.GetInt("MeshSimplifier_PC_MinTriangleCount", 25000);
            int newPcMinTriangleCount = EditorGUILayout.IntField(new GUIContent("PC Min Triangle Count", "PC: Only meshes with at least this many triangles will be simplified."), pcMinTriangleCount);
            if (newPcMinTriangleCount < 300) newPcMinTriangleCount = 300;
            if (newPcMinTriangleCount != pcMinTriangleCount)
            {
                SynSceneOptimizerSettings.SetInt("MeshSimplifier_PC_MinTriangleCount", newPcMinTriangleCount);
                showDryRunResults = false;
            }

            float pcTargetRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_PC_TargetRatio", 0.7f);
            float newPcTargetRatio = EditorGUILayout.Slider(new GUIContent("PC Target Triangle Ratio", "PC: Target ratio of triangles to keep (e.g. 0.70 keeps 70% of triangles)."), pcTargetRatio, 0.05f, 0.95f);
            if (!Mathf.Approximately(newPcTargetRatio, pcTargetRatio))
            {
                SynSceneOptimizerSettings.SetFloat("MeshSimplifier_PC_TargetRatio", newPcTargetRatio);
                showDryRunResults = false;
            }
            EditorGUI.EndDisabledGroup();
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(5);

            // 3. Mobile Settings Profile
            EditorGUILayout.LabelField("Mobile / Quest Settings Profile", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            bool mobileEnabled = SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_Enabled", true);
            bool newMobileEnabled = EditorGUILayout.Toggle(new GUIContent("Enable on Mobile", "Should mesh simplification run when building/debugging for Quest (Android/iOS)?"), mobileEnabled);
            if (newMobileEnabled != mobileEnabled)
            {
                SynSceneOptimizerSettings.SetBool("MeshSimplifier_Mobile_Enabled", newMobileEnabled);
                showDryRunResults = false;
            }

            EditorGUI.BeginDisabledGroup(!newMobileEnabled);
            int mobileMinTriangleCount = SynSceneOptimizerSettings.GetInt("MeshSimplifier_Mobile_MinTriangleCount", 4000);
            int newMobileMinTriangleCount = EditorGUILayout.IntField(new GUIContent("Mobile Min Triangle Count", "Mobile: Only meshes with at least this many triangles will be simplified."), mobileMinTriangleCount);
            if (newMobileMinTriangleCount < 300) newMobileMinTriangleCount = 300;
            if (newMobileMinTriangleCount != mobileMinTriangleCount)
            {
                SynSceneOptimizerSettings.SetInt("MeshSimplifier_Mobile_MinTriangleCount", newMobileMinTriangleCount);
                showDryRunResults = false;
            }

            bool mobileUseDynamicTiers = SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_UseDynamicTiers", true);
            bool newMobileUseDynamicTiers = EditorGUILayout.Toggle(new GUIContent("Use Dynamic Tiers", "Mobile: Automatically reduce heavier meshes more aggressively than lighter ones (e.g. 80% reduction for >50k tris, 50% for >8k tris, etc.)."), mobileUseDynamicTiers);
            if (newMobileUseDynamicTiers != mobileUseDynamicTiers)
            {
                SynSceneOptimizerSettings.SetBool("MeshSimplifier_Mobile_UseDynamicTiers", newMobileUseDynamicTiers);
                showDryRunResults = false;
            }

            bool mobileUseSceneCap = SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_UseSceneCap", false);
            bool newMobileUseSceneCap = EditorGUILayout.Toggle(new GUIContent("Use Scene Triangle Cap", "Mobile: Scale the simplification dynamically to hit a hard cap for the total scene triangle count."), mobileUseSceneCap);
            if (newMobileUseSceneCap != mobileUseSceneCap)
            {
                SynSceneOptimizerSettings.SetBool("MeshSimplifier_Mobile_UseSceneCap", newMobileUseSceneCap);
                showDryRunResults = false;
            }

            EditorGUI.BeginDisabledGroup(!newMobileUseSceneCap);
            int mobileSceneTriangleCap = SynSceneOptimizerSettings.GetInt("MeshSimplifier_Mobile_SceneTriangleCap", 250000);
            int newMobileSceneTriangleCap = EditorGUILayout.IntField(new GUIContent("Target Scene Cap (Tris)", "Mobile: The maximum allowed total scene triangle count when using the Scene Cap feature."), mobileSceneTriangleCap);
            if (newMobileSceneTriangleCap < 10000) newMobileSceneTriangleCap = 10000;
            if (newMobileSceneTriangleCap != mobileSceneTriangleCap)
            {
                SynSceneOptimizerSettings.SetInt("MeshSimplifier_Mobile_SceneTriangleCap", newMobileSceneTriangleCap);
                showDryRunResults = false;
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(newMobileUseSceneCap);
            float mobileTargetRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_Mobile_TargetRatio", 0.5f);
            float newMobileTargetRatio = EditorGUILayout.Slider(new GUIContent("Mobile Target Ratio", "Mobile: Target ratio of triangles to keep (e.g. 0.50 keeps 50% of triangles). Only used if Dynamic Tiers & Scene Cap are both off."), mobileTargetRatio, 0.05f, 0.95f);
            if (!Mathf.Approximately(newMobileTargetRatio, mobileTargetRatio))
            {
                SynSceneOptimizerSettings.SetFloat("MeshSimplifier_Mobile_TargetRatio", newMobileTargetRatio);
                showDryRunResults = false;
            }
            EditorGUI.EndDisabledGroup();

            // 3.1 Advanced Dynamic Tiers Configuration Foldout
            if (mobileUseDynamicTiers)
            {
                EditorGUILayout.Space(2);
                bool advancedOpen = EditorGUILayout.Foldout(SynSceneOptimizerSettings.GetBool("MeshSimplifier_AdvancedOpen", false), "Advanced Dynamic Tiers Config");
                SynSceneOptimizerSettings.SetBool("MeshSimplifier_AdvancedOpen", advancedOpen);
                if (advancedOpen)
                {
                    EditorGUI.indentLevel++;
                    
                    float t50k = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_50k", 0.20f);
                    float newT50k = EditorGUILayout.Slider("50k+ Tri Mesh Ratio", t50k, 0.10f, 0.95f);
                    if (!Mathf.Approximately(newT50k, t50k)) { SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_50k", newT50k); showDryRunResults = false; }

                    float t20k = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_20k", 0.35f);
                    float newT20k = EditorGUILayout.Slider("20k-50k Tri Mesh Ratio", t20k, 0.10f, 0.95f);
                    if (!Mathf.Approximately(newT20k, t20k)) { SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_20k", newT20k); showDryRunResults = false; }

                    float t8k = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_8k", 0.50f);
                    float newT8k = EditorGUILayout.Slider("8k-20k Tri Mesh Ratio", t8k, 0.10f, 0.95f);
                    if (!Mathf.Approximately(newT8k, t8k)) { SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_8k", newT8k); showDryRunResults = false; }

                    float t4k = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_4k", 0.65f);
                    float newT4k = EditorGUILayout.Slider("4k-8k Tri Mesh Ratio", t4k, 0.10f, 0.95f);
                    if (!Mathf.Approximately(newT4k, t4k)) { SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_4k", newT4k); showDryRunResults = false; }

                    float tDef = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_Default", 0.80f);
                    float newTDef = EditorGUILayout.Slider("Under 4k Tri Mesh Ratio", tDef, 0.10f, 0.95f);
                    if (!Mathf.Approximately(newTDef, tDef)) { SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_Default", newTDef); showDryRunResults = false; }

                    if (GUILayout.Button("Reset Tiers to Defaults"))
                    {
                        SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_50k", 0.20f);
                        SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_20k", 0.35f);
                        SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_8k", 0.50f);
                        SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_4k", 0.65f);
                        SynSceneOptimizerSettings.SetFloat("MeshSimplifier_TierRatio_Default", 0.80f);
                        showDryRunResults = false;
                    }

                    EditorGUI.indentLevel--;
                }
            }

            EditorGUI.EndDisabledGroup();
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(5);
            bool skipLightmapped = SynSceneOptimizerSettings.GetBool("MeshSimplifier_SkipLightmapped", true);
            bool newSkipLightmapped = EditorGUILayout.Toggle(new GUIContent("Skip Lightmapped Meshes", "Never decimate renderers that use baked lightmaps. Decimating after a bake distorts the lightmap UVs and causes smearing or black seams."), skipLightmapped);
            if (newSkipLightmapped != skipLightmapped)
            {
                SynSceneOptimizerSettings.SetBool("MeshSimplifier_SkipLightmapped", newSkipLightmapped);
                showDryRunResults = false;
            }

            // 4. Custom Preservation List GUI (With gentle decimation slider)
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Mesh Simplifier Preservation List (Custom Ratio)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            bool usePreserveRatio = SynSceneOptimizerSettings.GetBool("MeshSimplifier_UsePreserveRatio", false);
            bool newUsePreserveRatio = EditorGUILayout.Toggle(new GUIContent("Apply Custom Ratio", "If enabled, preserved items will be simplified to a fixed ratio (e.g. 80%) instead of being completely skipped."), usePreserveRatio);
            if (newUsePreserveRatio != usePreserveRatio)
            {
                SynSceneOptimizerSettings.SetBool("MeshSimplifier_UsePreserveRatio", newUsePreserveRatio);
                showDryRunResults = false;
            }

            EditorGUI.BeginDisabledGroup(!newUsePreserveRatio);
            float preserveRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_PreserveRatio", 0.80f);
            float newPreserveRatio = EditorGUILayout.Slider(new GUIContent("Preservation Ratio", "Target ratio for preserved items (e.g. 0.80 keeps 80% triangles)."), preserveRatio, 0.05f, 0.95f);
            if (!Mathf.Approximately(newPreserveRatio, preserveRatio))
            {
                SynSceneOptimizerSettings.SetFloat("MeshSimplifier_PreserveRatio", newPreserveRatio);
                showDryRunResults = false;
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(2);

            var protData = SynProtectionData.GetInstance();
            var localPreserveList = protData.simplifierPreserveList;
            var localSkipList = protData.simplifierSkipList;

            int listSize = localPreserveList.Count;
            int newSize = EditorGUILayout.IntField("Preserved Items Count", listSize);
            if (newSize != listSize)
            {
                if (newSize < 0) newSize = 0;
                while (localPreserveList.Count < newSize) localPreserveList.Add(new SynPersistentObjectReference());
                while (localPreserveList.Count > newSize) localPreserveList.RemoveAt(localPreserveList.Count - 1);
                EditorUtility.SetDirty(protData);
                AssetDatabase.SaveAssets();
                showDryRunResults = false;
            }

            for (int i = 0; i < localPreserveList.Count; i++)
            {
                var refObj = localPreserveList[i] ?? (localPreserveList[i] = new SynPersistentObjectReference());
                var currentObj = refObj.Resolve();

                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                var newObj = EditorGUILayout.ObjectField($"Item {i}", currentObj, typeof(UnityEngine.Object), true);
                if (EditorGUI.EndChangeCheck())
                {
                    refObj.Set(newObj);
                    EditorUtility.SetDirty(protData);
                    AssetDatabase.SaveAssets();
                    showDryRunResults = false;
                }
                if (GUILayout.Button("-", GUILayout.Width(20)))
                {
                    localPreserveList.RemoveAt(i);
                    EditorUtility.SetDirty(protData);
                    AssetDatabase.SaveAssets();
                    showDryRunResults = false;
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            // Drag and Drop Area for Preservation List
            Rect dropAreaPreserve = GUILayoutUtility.GetRect(0.0f, 30.0f, GUILayout.ExpandWidth(true));
            GUI.Box(dropAreaPreserve, "Drag & Drop GameObjects, Prefabs or Meshes here to preserve (with custom ratio)");
            Event evt = Event.current;
            if (dropAreaPreserve.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (UnityEngine.Object draggedObject in DragAndDrop.objectReferences)
                        {
                            if (draggedObject != null)
                            {
                                bool exists = false;
                                foreach (var p in localPreserveList)
                                {
                                    if (p != null && p.Resolve() == draggedObject) { exists = true; break; }
                                }
                                if (!exists)
                                {
                                    localPreserveList.Add(new SynPersistentObjectReference(draggedObject));
                                }
                            }
                        }
                        EditorUtility.SetDirty(protData);
                        AssetDatabase.SaveAssets();
                        showDryRunResults = false;
                    }
                }
            }
            EditorGUI.indentLevel--;

            // 5. Skip List GUI (Never simplify under any conditions)
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Mesh Simplifier Skip List (Never Decimate / 100% Retained)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            int skipSize = localSkipList.Count;
            int newSkipSize = EditorGUILayout.IntField("Skipped Items Count", skipSize);
            if (newSkipSize != skipSize)
            {
                if (newSkipSize < 0) newSkipSize = 0;
                while (localSkipList.Count < newSkipSize) localSkipList.Add(new SynPersistentObjectReference());
                while (localSkipList.Count > newSkipSize) localSkipList.RemoveAt(localSkipList.Count - 1);
                EditorUtility.SetDirty(protData);
                AssetDatabase.SaveAssets();
                showDryRunResults = false;
            }

            for (int i = 0; i < localSkipList.Count; i++)
            {
                var refObj = localSkipList[i] ?? (localSkipList[i] = new SynPersistentObjectReference());
                var currentObj = refObj.Resolve();

                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                var newObj = EditorGUILayout.ObjectField($"Item {i}", currentObj, typeof(UnityEngine.Object), true);
                if (EditorGUI.EndChangeCheck())
                {
                    refObj.Set(newObj);
                    EditorUtility.SetDirty(protData);
                    AssetDatabase.SaveAssets();
                    showDryRunResults = false;
                }
                if (GUILayout.Button("-", GUILayout.Width(20)))
                {
                    localSkipList.RemoveAt(i);
                    EditorUtility.SetDirty(protData);
                    AssetDatabase.SaveAssets();
                    showDryRunResults = false;
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            // Drag and Drop Area for Skip List
            Rect dropAreaSkip = GUILayoutUtility.GetRect(0.0f, 30.0f, GUILayout.ExpandWidth(true));
            GUI.Box(dropAreaSkip, "Drag & Drop GameObjects, Prefabs or Meshes here to SKIP (never decimate)");
            evt = Event.current;
            if (dropAreaSkip.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (UnityEngine.Object draggedObject in DragAndDrop.objectReferences)
                        {
                            if (draggedObject != null)
                            {
                                bool exists = false;
                                foreach (var p in localSkipList)
                                {
                                    if (p != null && p.Resolve() == draggedObject) { exists = true; break; }
                                }
                                if (!exists)
                                {
                                    localSkipList.Add(new SynPersistentObjectReference(draggedObject));
                                }
                            }
                        }
                        EditorUtility.SetDirty(protData);
                        AssetDatabase.SaveAssets();
                        showDryRunResults = false;
                    }
                }
            }
            EditorGUI.indentLevel--;
            EditorGUI.indentLevel--;

            // 6. Dry Run Button & Prediction Panel
            EditorGUILayout.Space(10);
            if (GUILayout.Button("Scan Scene (Dry Run Prediction)", GUILayout.Height(25)))
            {
                RunDryRun();
            }

            if (showDryRunResults)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Dry Run Prediction Results:", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;

                bool activeIsMobile = false;
                if (targetPlatform == "PC") activeIsMobile = false;
                else if (targetPlatform == "Mobile") activeIsMobile = true;
                else
                {
                    #if UNITY_EDITOR
                    BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
                    activeIsMobile = activeTarget == BuildTarget.Android || activeTarget == BuildTarget.iOS;
                    #endif
                }

                string modeLabel = activeIsMobile ? "Mobile/Quest Profile" : "PC Profile";
                EditorGUILayout.LabelField($"Active Profile: {modeLabel}");
                EditorGUILayout.LabelField($"Unsimplifiable Base: {dryRunUnsimplifiable:N0} tris (protected or below threshold)");
                EditorGUILayout.LabelField($"Simplifiable Geometry: {dryRunSimplifiable:N0} tris ({dryRunSimplifiableCount} meshes across {dryRunTotalRenderers} objects)");

                float predictedFinal = dryRunPredictedTotal;
                EditorGUILayout.LabelField($"Calculated Average Ratio: {dryRunRatio * 100f:F1}%");

                // Highlight color for results
                GUIStyle resultStyle = new GUIStyle(EditorStyles.label);
                resultStyle.fontStyle = FontStyle.Bold;
                int platformCap = activeIsMobile ? SynSceneOptimizerSettings.GetInt("MeshSimplifier_Mobile_SceneTriangleCap", 250000) : 1000000;
                
                if (predictedFinal <= platformCap || !activeIsMobile || !SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_UseSceneCap", false))
                {
                    resultStyle.normal.textColor = new Color(0.1f, 0.6f, 0.1f); // Dark Green
                    EditorGUILayout.LabelField($"Estimated Scene Total: {predictedFinal:N0} tris", resultStyle);
                    
                    if (activeIsMobile && SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_UseSceneCap", false))
                    {
                        EditorGUILayout.LabelField("Budget Status: Fits cap cleanly!", EditorStyles.miniLabel);
                    }
                }
                else
                {
                    resultStyle.normal.textColor = new Color(0.8f, 0.5f, 0.0f); // Dark Orange/Yellow
                    EditorGUILayout.LabelField($"Estimated Scene Total: {predictedFinal:N0} tris", resultStyle);

                    if (dryRunUnsimplifiable >= platformCap)
                    {
                        EditorGUILayout.HelpBox($"WARNING: Unsimplifiable base ({dryRunUnsimplifiable:N0} tris) already exceeds your target cap of {platformCap:N0} tris. Simplifiable meshes will be forced to the 10% safety limit.", MessageType.Error);
                    }
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.EndVertical();
            }
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            // Determine active platform profile mode
            string targetPlatform = SynSceneOptimizerSettings.GetString("MeshSimplifier_TargetPlatform", "Auto");
            bool isMobileMode = false;
            if (targetPlatform == "PC")
            {
                isMobileMode = false;
            }
            else if (targetPlatform == "Mobile")
            {
                isMobileMode = true;
            }
            else
            {
                // Auto
                #if UNITY_EDITOR
                BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
                isMobileMode = activeTarget == BuildTarget.Android || activeTarget == BuildTarget.iOS;
                #else
                isMobileMode = false;
                #endif
            }

            // Verify if enabled for active profile mode
            bool pcEnabled = SynSceneOptimizerSettings.GetBool("MeshSimplifier_PC_Enabled", true);
            bool mobileEnabled = SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_Enabled", true);
            if (isMobileMode && !mobileEnabled) return;
            if (!isMobileMode && !pcEnabled) return;

            // Load configuration parameters for selected mode
            int minTriangleCount;
            bool useDynamicTiers = false;
            float targetRatio;
            bool useSceneCap = false;
            int sceneTriangleCap = 250000;

            if (isMobileMode)
            {
                minTriangleCount = SynSceneOptimizerSettings.GetInt("MeshSimplifier_Mobile_MinTriangleCount", 4000);
                useDynamicTiers = SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_UseDynamicTiers", true);
                targetRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_Mobile_TargetRatio", 0.5f);
                useSceneCap = SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_UseSceneCap", false);
                sceneTriangleCap = SynSceneOptimizerSettings.GetInt("MeshSimplifier_Mobile_SceneTriangleCap", 250000);
            }
            else
            {
                minTriangleCount = SynSceneOptimizerSettings.GetInt("MeshSimplifier_PC_MinTriangleCount", 25000);
                targetRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_PC_TargetRatio", 0.7f);
            }

            // Ensure isolated lists are loaded
            EnsureLocalPreserveListLoaded();
            EnsureLocalSkipListLoaded();

            // Auto-Tune Tier Ratios if using Scene Cap and Dynamic Tiers before execution
            autoTunedTierRatios = null;
            if (isMobileMode && useSceneCap && useDynamicTiers)
            {
                AutoTuneTierRatios(sceneTriangleCap);
            }

            // Extract all meshes and game objects from the local preserve list
            var locallyProtectedMeshes = new HashSet<Mesh>();
            var locallyProtectedGameObjects = new HashSet<GameObject>();
            foreach (var refObj in localPreserveList)
            {
                if (refObj == null) continue;
                var obj = refObj.Resolve();
                if (obj == null) continue;

                if (obj is GameObject go)
                {
                    locallyProtectedGameObjects.Add(go);
                    foreach (var childRenderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh m = null;
                        if (childRenderer is MeshRenderer)
                        {
                            var mf = childRenderer.GetComponent<MeshFilter>();
                            if (mf != null) m = mf.sharedMesh;
                        }
                        else if (childRenderer is SkinnedMeshRenderer smr)
                        {
                            m = smr.sharedMesh;
                        }
                        if (m != null) locallyProtectedMeshes.Add(m);
                    }
                }
                else if (obj is Mesh meshAsset)
                {
                    locallyProtectedMeshes.Add(meshAsset);
                }
            }

            // Extract all meshes and game objects from the local skip list (never decimate)
            var locallySkippedMeshes = new HashSet<Mesh>();
            var locallySkippedGameObjects = new HashSet<GameObject>();
            foreach (var refObj in localSkipList)
            {
                if (refObj == null) continue;
                var obj = refObj.Resolve();
                if (obj == null) continue;

                if (obj is GameObject go)
                {
                    locallySkippedGameObjects.Add(go);
                    foreach (var childRenderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh m = null;
                        if (childRenderer is MeshRenderer)
                        {
                            var mf = childRenderer.GetComponent<MeshFilter>();
                            if (mf != null) m = mf.sharedMesh;
                        }
                        else if (childRenderer is SkinnedMeshRenderer smr)
                        {
                            m = smr.sharedMesh;
                        }
                        if (m != null) locallySkippedMeshes.Add(m);
                    }
                }
                else if (obj is Mesh meshAsset)
                {
                    locallySkippedMeshes.Add(meshAsset);
                }
            }

            bool usePreserveRatio = SynSceneOptimizerSettings.GetBool("MeshSimplifier_UsePreserveRatio", false);
            float preserveRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_PreserveRatio", 0.80f);
            bool skipLightmapped = SynSceneOptimizerSettings.GetBool("MeshSimplifier_SkipLightmapped", true);

            // Gather all active scene meshes and group by original unique Mesh
            var meshToRenderers = new Dictionary<Mesh, List<Renderer>>();
            var preservedRenderers = new HashSet<Renderer>();
            
            // Sum counts for every single instance/renderer in the scene to match actual camera render counts
            int totalUnsimplifiableRenderedTris = 0;
            int totalSimplifiableRenderedTris = 0;

            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;
                if (IsEditorOnly(r.transform)) continue;

                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh != null)
                    {
                        totalUnsimplifiableRenderedTris += GetTriangleCount(smr.sharedMesh);
                    }
                    continue;
                }

                Mesh mesh = null;
                if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }

                if (mesh == null || mesh.vertexCount == 0) continue;

                int triCount = GetTriangleCount(mesh);

                // Earlier passes (dedup, memory optimizer, palettes) may have swapped the mesh, so user
                // lists are matched against both the current and the pre-pipeline mesh
                Mesh originalMesh = SynPipelineCompactor.GetOriginalMesh(r, mesh);

                // Check global protection settings
                bool isMeshGloballyProtected = SynProtectionData.IsProtected(mesh) || SynProtectionData.IsProtected(originalMesh);
                bool isGoGloballyProtected = SynProtectionData.IsProtected(r.gameObject);

                // Check local skip list settings (always untouchable)
                bool isMeshLocallySkipped = locallySkippedMeshes.Contains(mesh) || locallySkippedMeshes.Contains(originalMesh);
                bool isGoLocallySkipped = IsLocalGoProtected(r.gameObject, locallySkippedGameObjects);

                // Check local preservation list settings
                bool isMeshLocallyProtected = locallyProtectedMeshes.Contains(mesh) || locallyProtectedMeshes.Contains(originalMesh);
                bool isGoLocallyProtected = IsLocalGoProtected(r.gameObject, locallyProtectedGameObjects);

                // Baked lightmaps were rendered against the full-resolution UV2 layout; decimating afterwards
                // collapses small UV charts and smears the lightmap. 0xFFFE = static with Scale In Lightmap 0 (probe-lit).
                bool isLightmapped = r.lightmapIndex >= 0 && r.lightmapIndex < 0xFFFE;

                bool isGloballyProtected = isMeshGloballyProtected || isGoGloballyProtected;
                bool isLocallySkipped = isMeshLocallySkipped || isGoLocallySkipped;
                bool isLocallyProtected = isMeshLocallyProtected || isGoLocallyProtected;

                if (isGloballyProtected || isLocallySkipped || (skipLightmapped && isLightmapped))
                {
                    totalUnsimplifiableRenderedTris += triCount;
                }
                else if (isLocallyProtected)
                {
                    if (usePreserveRatio)
                    {
                        preservedRenderers.Add(r);
                        // Add to simplifiable list so it gets processed
                        if (!meshToRenderers.ContainsKey(mesh))
                        {
                            meshToRenderers[mesh] = new List<Renderer>();
                        }
                        meshToRenderers[mesh].Add(r);
                        
                        // It contributes to the scene at the fixed preserveRatio
                        totalUnsimplifiableRenderedTris += (int)(triCount * preserveRatio);
                    }
                    else
                    {
                        totalUnsimplifiableRenderedTris += triCount;
                    }
                }
                else
                {
                    bool qualifies = triCount >= minTriangleCount;
                    if (qualifies)
                    {
                        if (!meshToRenderers.ContainsKey(mesh))
                        {
                            meshToRenderers[mesh] = new List<Renderer>();
                        }
                        meshToRenderers[mesh].Add(r);

                        // Add to simplifiable sum for every renderer using this mesh
                        totalSimplifiableRenderedTris += triCount;
                    }
                    else
                    {
                        // Add to unsimplifiable sum for every renderer using this mesh
                        totalUnsimplifiableRenderedTris += triCount;
                    }
                }
            }

            if (meshToRenderers.Count == 0)
            {
                return;
            }

            // Calculate scaling factor to meet Target Scene Triangle Cap (only uniform fallback uses scale factor now)
            float scaleFactor = 1.0f;
            if (isMobileMode && useSceneCap)
            {
                int budget = sceneTriangleCap - totalUnsimplifiableRenderedTris;

                if (!useDynamicTiers)
                {
                    // Uniform target ratio (across all instances)
                    if (totalSimplifiableRenderedTris > 0)
                    {
                        scaleFactor = budget <= 0 ? 0.0f : (float)budget / (totalSimplifiableRenderedTris * targetRatio);
                    }
                }

                if (budget <= 0)
                {
                    EditorPrefs.SetBool("MeshSimplifier_LastRunOverflowed", true);
                    EditorPrefs.SetInt("MeshSimplifier_LastRunUnsimplifiable", totalUnsimplifiableRenderedTris);
                    EditorPrefs.SetInt("MeshSimplifier_LastRunCap", sceneTriangleCap);

                    Debug.LogWarning(string.Format("[SYN SCENE OPTIMIZER] WARNING: Unsimplifiable meshes (below threshold or protected) already contain {0} triangles, which exceeds your target cap of {1} triangles. Forcing maximum reduction (10%) on all other meshes.",
                        totalUnsimplifiableRenderedTris, sceneTriangleCap));
                }
                else
                {
                    EditorPrefs.SetBool("MeshSimplifier_LastRunOverflowed", false);
                    Debug.Log(string.Format("[SYN SCENE OPTIMIZER] Scene Cap Info: Unsimplifiable = {0} tris, Simplifiable = {1} tris, Budget = {2} tris",
                        totalUnsimplifiableRenderedTris, totalSimplifiableRenderedTris, budget));
                }
            }
            else
            {
                EditorPrefs.SetBool("MeshSimplifier_LastRunOverflowed", false);
            }

            string modeName = isMobileMode ? "Mobile/Quest" : "PC";

            // Prepare batch simplification parameters with deterministic cache check
            var parameters = new List<(Mesh Mesh, MeshSimplificationTarget Target, MeshSimplifierOptions Options, Mesh Destination)>();
            var meshHashKeys = new Dictionary<Mesh, string>();
            var simplifiedResults = new Dictionary<Mesh, Mesh>();
            int cacheHitCount = 0;

            foreach (var kvp in meshToRenderers)
            {
                Mesh originalMesh = kvp.Key;

                float ratio;
                bool isMeshLocallyProtected = kvp.Value.Exists(preservedRenderers.Contains);

                if (isMeshLocallyProtected && usePreserveRatio)
                {
                    ratio = preserveRatio;
                }
                else if (isMobileMode && useSceneCap)
                {
                    if (useDynamicTiers)
                    {
                        // Ratios have already been auto-tuned dynamically to hit the cap
                        ratio = GetTierRatio(GetTriangleCount(originalMesh));
                    }
                    else
                    {
                        ratio = Mathf.Clamp(targetRatio * scaleFactor, 0.10f, 0.95f);
                    }
                }
                else if (useDynamicTiers)
                {
                    ratio = GetTierRatio(GetTriangleCount(originalMesh));
                }
                else
                {
                    ratio = targetRatio;
                }

                // Deterministic cache check
                string meshHashKey = SynAssetCache.ComputeMeshHash(originalMesh, ratio, modeName, originalMesh.vertexCount, useDynamicTiers);
                meshHashKeys[originalMesh] = meshHashKey;

                if (SynAssetCache.TryGetCachedAsset<Mesh>(SynAssetCache.MeshesCategory, meshHashKey, out Mesh cachedMesh))
                {
                    simplifiedResults[originalMesh] = cachedMesh;
                    cacheHitCount++;
                }
                else
                {
                    Mesh simplifiedMesh = new Mesh();
                    simplifiedMesh.name = originalMesh.name + "_Simplified";

                    var target = new MeshSimplificationTarget
                    {
                        Kind = MeshSimplificationTargetKind.RelativeTriangleCount,
                        Value = ratio
                    };

                    var options = MeshSimplifierOptions.Default;
                    options.PreserveBorderEdges = true;
                    options.UseBarycentricCoordinateInterpolation = false;
                    options.UseBarycentricCoordinateInterpolationForUV = false;

                    parameters.Add((originalMesh, target, options, simplifiedMesh));
                }
            }

            // Execute simplification job batch in parallel for uncached meshes only
            if (parameters.Count > 0)
            {
                MeshSimplifier.SimplifyBatch(parameters);

                for (int i = 0; i < parameters.Count; i++)
                {
                    Mesh originalMesh = parameters[i].Mesh;
                    Mesh simplifiedMesh = parameters[i].Destination;
                    string meshHashKey = meshHashKeys[originalMesh];

                    string cleanName = Regex.Replace(originalMesh.name, @"[^a-zA-Z0-9_]", "");
                    Mesh savedMesh = SynAssetCache.SaveCachedAsset(simplifiedMesh, SynAssetCache.MeshesCategory, meshHashKey, cleanName);
                    simplifiedResults[originalMesh] = savedMesh;
                }
            }

            int totalRenderersAffected = 0;

            // Re-link references on scene objects using simplified or cached meshes
            foreach (var kvp in meshToRenderers)
            {
                Mesh originalMesh = kvp.Key;
                if (!simplifiedResults.TryGetValue(originalMesh, out Mesh finalMesh) || finalMesh == null)
                {
                    continue;
                }

                var list = kvp.Value;
                foreach (Renderer r in list)
                {
                    if (r is MeshRenderer)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf != null)
                        {
                            mf.sharedMesh = finalMesh;
                            EditorUtility.SetDirty(mf);
                            totalRenderersAffected++;
                        }
                    }
                    else if (r is SkinnedMeshRenderer smr)
                    {
                        smr.sharedMesh = finalMesh;
                        EditorUtility.SetDirty(smr);
                        totalRenderersAffected++;
                    }
                }
            }

            // Construct logging messages
            string logMsg;
            string cacheNote = cacheHitCount > 0 ? $" ({cacheHitCount} reused from cache)" : "";

            if (isMobileMode && useSceneCap)
            {
                if (useDynamicTiers)
                {
                    logMsg = string.Format("[{0}] Simplified {1} meshes{2} using Auto-Tuned Dynamic Tiers to target scene cap of {3} rendered triangles across {4} renderers.",
                        modeName, meshToRenderers.Count, cacheNote, sceneTriangleCap, totalRenderersAffected);
                }
                else
                {
                    logMsg = string.Format("[{0}] Simplified {1} meshes{2} using calculated uniform ratio {3:F0}% to target scene cap of {4} rendered triangles across {5} renderers.",
                        modeName, meshToRenderers.Count, cacheNote, targetRatio * 100f, sceneTriangleCap, totalRenderersAffected);
                }
            }
            else if (useDynamicTiers)
            {
                logMsg = string.Format("[{0}] Simplified {1} meshes{2} using Dynamic Tiered Reduction across {3} renderers.",
                    modeName, meshToRenderers.Count, cacheNote, totalRenderersAffected);
            }
            else
            {
                logMsg = string.Format("[{0}] Simplified {1} meshes{2} (reduced to {3:F0}% triangles) across {4} renderers.",
                    modeName, meshToRenderers.Count, cacheNote, targetRatio * 100f, totalRenderersAffected);
            }

            SynPipelineCompactor.LogChange("Mesh Simplifier", logMsg);
            Debug.Log("[SYN SCENE OPTIMIZER] " + logMsg);
        }

        // Per-run ratios computed by AutoTuneTierRatios (50k, 20k, 8k, 4k, default); null = use the user's sliders
        private static float[] autoTunedTierRatios;

        private float GetTierRatio(int triCount)
        {
            if (autoTunedTierRatios != null)
            {
                if (triCount >= 50000) return autoTunedTierRatios[0];
                if (triCount >= 20000) return autoTunedTierRatios[1];
                if (triCount >= 8000) return autoTunedTierRatios[2];
                if (triCount >= 4000) return autoTunedTierRatios[3];
                return autoTunedTierRatios[4];
            }

            if (triCount >= 50000)
                return SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_50k", 0.20f);
            if (triCount >= 20000)
                return SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_20k", 0.35f);
            if (triCount >= 8000)
                return SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_8k", 0.50f);
            if (triCount >= 4000)
                return SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_4k", 0.65f);
            return SynSceneOptimizerSettings.GetFloat("MeshSimplifier_TierRatio_Default", 0.80f);
        }

        private static int GetTriangleCount(Mesh mesh)
        {
            int count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                count += (int)(mesh.GetIndexCount(i) / 3);
            }
            return count;
        }

        private static bool IsEditorOnly(Transform t)
        {
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }

        private static bool IsLocalGoProtected(GameObject go, HashSet<GameObject> localGOs)
        {
            if (go == null) return false;
            Transform t = go.transform;
            while (t != null)
            {
                if (localGOs.Contains(t.gameObject)) return true;
                t = t.parent;
            }
            return false;
        }

        private void RunDryRun()
        {
            SynSceneQuery.ClearCache();
            Scene scene = SceneManager.GetActiveScene();
            List<Renderer> renderers = SynSceneQuery.GetAllRenderers(scene);

            bool isMobileMode = false;
            string targetPlatform = SynSceneOptimizerSettings.GetString("MeshSimplifier_TargetPlatform", "Auto");
            if (targetPlatform == "PC")
            {
                isMobileMode = false;
            }
            else if (targetPlatform == "Mobile")
            {
                isMobileMode = true;
            }
            else
            {
                #if UNITY_EDITOR
                BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
                isMobileMode = activeTarget == BuildTarget.Android || activeTarget == BuildTarget.iOS;
                #endif
            }

            int minTriangleCount;
            bool useDynamicTiers = false;
            float targetRatio;
            bool useSceneCap = false;
            int sceneTriangleCap = 250000;

            if (isMobileMode)
            {
                minTriangleCount = SynSceneOptimizerSettings.GetInt("MeshSimplifier_Mobile_MinTriangleCount", 4000);
                useDynamicTiers = SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_UseDynamicTiers", true);
                targetRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_Mobile_TargetRatio", 0.5f);
                useSceneCap = SynSceneOptimizerSettings.GetBool("MeshSimplifier_Mobile_UseSceneCap", false);
                sceneTriangleCap = SynSceneOptimizerSettings.GetInt("MeshSimplifier_Mobile_SceneTriangleCap", 250000);
            }
            else
            {
                minTriangleCount = SynSceneOptimizerSettings.GetInt("MeshSimplifier_PC_MinTriangleCount", 25000);
                targetRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_PC_TargetRatio", 0.7f);
            }

            // Ensure isolated local lists are loaded
            EnsureLocalPreserveListLoaded();
            EnsureLocalSkipListLoaded();

            // Auto-Tune Tier Ratios if using Scene Cap and Dynamic Tiers during Dry Run
            autoTunedTierRatios = null;
            if (isMobileMode && useSceneCap && useDynamicTiers)
            {
                AutoTuneTierRatios(sceneTriangleCap);
            }

            // Extract all meshes and game objects from the local preserve list
            var locallyProtectedMeshes = new HashSet<Mesh>();
            var locallyProtectedGameObjects = new HashSet<GameObject>();
            foreach (var refObj in localPreserveList)
            {
                if (refObj == null) continue;
                var obj = refObj.Resolve();
                if (obj == null) continue;

                if (obj is GameObject go)
                {
                    locallyProtectedGameObjects.Add(go);
                    foreach (var childRenderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh m = null;
                        if (childRenderer is MeshRenderer)
                        {
                            var mf = childRenderer.GetComponent<MeshFilter>();
                            if (mf != null) m = mf.sharedMesh;
                        }
                        else if (childRenderer is SkinnedMeshRenderer smr)
                        {
                            m = smr.sharedMesh;
                        }
                        if (m != null) locallyProtectedMeshes.Add(m);
                    }
                }
                else if (obj is Mesh meshAsset)
                {
                    locallyProtectedMeshes.Add(meshAsset);
                }
            }

            // Extract all meshes and game objects from the local skip list (never decimate)
            var locallySkippedMeshes = new HashSet<Mesh>();
            var locallySkippedGameObjects = new HashSet<GameObject>();
            foreach (var refObj in localSkipList)
            {
                if (refObj == null) continue;
                var obj = refObj.Resolve();
                if (obj == null) continue;

                if (obj is GameObject go)
                {
                    locallySkippedGameObjects.Add(go);
                    foreach (var childRenderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh m = null;
                        if (childRenderer is MeshRenderer)
                        {
                            var mf = childRenderer.GetComponent<MeshFilter>();
                            if (mf != null) m = mf.sharedMesh;
                        }
                        else if (childRenderer is SkinnedMeshRenderer smr)
                        {
                            m = smr.sharedMesh;
                        }
                        if (m != null) locallySkippedMeshes.Add(m);
                    }
                }
                else if (obj is Mesh meshAsset)
                {
                    locallySkippedMeshes.Add(meshAsset);
                }
            }

            bool usePreserveRatio = SynSceneOptimizerSettings.GetBool("MeshSimplifier_UsePreserveRatio", false);
            float preserveRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_PreserveRatio", 0.80f);

            int totalUnsimplifiableRenderedTris = 0;
            int totalSimplifiableRenderedTris = 0;
            int uniqueSimplifiableCount = 0;
            int totalRenderersAffected = 0;

            var meshToRenderers = new Dictionary<Mesh, List<Renderer>>();

            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;
                if (IsEditorOnly(r.transform)) continue;

                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh != null)
                    {
                        totalUnsimplifiableRenderedTris += GetTriangleCount(smr.sharedMesh);
                    }
                    continue;
                }

                Mesh mesh = null;
                if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }

                if (mesh == null || mesh.vertexCount == 0) continue;

                int triCount = GetTriangleCount(mesh);

                // Check global protection settings
                bool isMeshGloballyProtected = SynProtectionData.IsProtected(mesh);
                bool isGoGloballyProtected = SynProtectionData.IsProtected(r.gameObject);

                // Check local skip list settings
                bool isMeshLocallySkipped = locallySkippedMeshes.Contains(mesh);
                bool isGoLocallySkipped = IsLocalGoProtected(r.gameObject, locallySkippedGameObjects);

                // Check local preservation list settings
                bool isMeshLocallyProtected = locallyProtectedMeshes.Contains(mesh);
                bool isGoLocallyProtected = IsLocalGoProtected(r.gameObject, locallyProtectedGameObjects);

                bool isGloballyProtected = isMeshGloballyProtected || isGoGloballyProtected;
                bool isLocallySkipped = isMeshLocallySkipped || isGoLocallySkipped;
                bool isLocallyProtected = isMeshLocallyProtected || isGoLocallyProtected;

                if (isGloballyProtected || isLocallySkipped)
                {
                    totalUnsimplifiableRenderedTris += triCount;
                }
                else if (isLocallyProtected)
                {
                    if (usePreserveRatio)
                    {
                        if (!meshToRenderers.ContainsKey(mesh))
                        {
                            meshToRenderers[mesh] = new List<Renderer>();
                        }
                        meshToRenderers[mesh].Add(r);
                        totalUnsimplifiableRenderedTris += (int)(triCount * preserveRatio);
                    }
                    else
                    {
                        totalUnsimplifiableRenderedTris += triCount;
                    }
                }
                else
                {
                    bool qualifies = triCount >= minTriangleCount;
                    if (qualifies)
                    {
                        if (!meshToRenderers.ContainsKey(mesh))
                        {
                            meshToRenderers[mesh] = new List<Renderer>();
                        }
                        meshToRenderers[mesh].Add(r);
                        totalSimplifiableRenderedTris += triCount;
                    }
                    else
                    {
                        totalUnsimplifiableRenderedTris += triCount;
                    }
                }
            }

            uniqueSimplifiableCount = meshToRenderers.Count;
            foreach (var kvp in meshToRenderers)
            {
                totalRenderersAffected += kvp.Value.Count;
            }

            float finalAvgRatio = targetRatio;
            float predictedSimplifiableTris = 0f;

            if (isMobileMode && useSceneCap)
            {
                int budget = sceneTriangleCap - totalUnsimplifiableRenderedTris;

                if (useDynamicTiers)
                {
                    // Compute average ratio based on newly auto-tuned ratios
                    float sumOfFinalRatios = 0f;
                    foreach (var kvp in meshToRenderers)
                    {
                        Mesh originalMesh = kvp.Key;
                        int triCount = GetTriangleCount(originalMesh);
                        float tierRatio = GetTierRatio(triCount);
                        
                        bool isMeshLocallyProtected = locallyProtectedMeshes.Contains(originalMesh) || IsLocalGoProtected(kvp.Value[0].gameObject, locallyProtectedGameObjects);
                        if (isMeshLocallyProtected && usePreserveRatio)
                        {
                            sumOfFinalRatios += preserveRatio * kvp.Value.Count;
                            // Triangles are already accounted for in totalUnsimplifiableRenderedTris, so do not add here
                        }
                        else
                        {
                            sumOfFinalRatios += tierRatio * kvp.Value.Count;
                            predictedSimplifiableTris += triCount * tierRatio * kvp.Value.Count;
                        }
                    }
                    finalAvgRatio = totalRenderersAffected > 0 ? sumOfFinalRatios / totalRenderersAffected : 0f;
                }
                else
                {
                    float scaleFactor = 1.0f;
                    if (totalSimplifiableRenderedTris > 0)
                    {
                        scaleFactor = budget <= 0 ? 0.0f : (float)budget / (totalSimplifiableRenderedTris * targetRatio);
                    }
                    
                    float sumOfFinalRatios = 0f;
                    foreach (var kvp in meshToRenderers)
                    {
                        Mesh originalMesh = kvp.Key;
                        int triCount = GetTriangleCount(originalMesh);
                        
                        bool isMeshLocallyProtected = locallyProtectedMeshes.Contains(originalMesh) || IsLocalGoProtected(kvp.Value[0].gameObject, locallyProtectedGameObjects);
                        if (isMeshLocallyProtected && usePreserveRatio)
                        {
                            sumOfFinalRatios += preserveRatio * kvp.Value.Count;
                        }
                        else
                        {
                            float ratio = Mathf.Clamp(targetRatio * scaleFactor, 0.10f, 0.95f);
                            sumOfFinalRatios += ratio * kvp.Value.Count;
                            predictedSimplifiableTris += triCount * ratio * kvp.Value.Count;
                        }
                    }
                    finalAvgRatio = totalRenderersAffected > 0 ? sumOfFinalRatios / totalRenderersAffected : 0f;
                }
            }
            else if (useDynamicTiers)
            {
                float sumOfFinalRatios = 0f;
                foreach (var kvp in meshToRenderers)
                {
                    Mesh originalMesh = kvp.Key;
                    int triCount = GetTriangleCount(originalMesh);
                    float tierRatio = GetTierRatio(triCount);
                    
                    bool isMeshLocallyProtected = locallyProtectedMeshes.Contains(originalMesh) || IsLocalGoProtected(kvp.Value[0].gameObject, locallyProtectedGameObjects);
                    if (isMeshLocallyProtected && usePreserveRatio)
                    {
                        sumOfFinalRatios += preserveRatio * kvp.Value.Count;
                    }
                    else
                    {
                        sumOfFinalRatios += tierRatio * kvp.Value.Count;
                        predictedSimplifiableTris += triCount * tierRatio * kvp.Value.Count;
                    }
                }
                finalAvgRatio = totalRenderersAffected > 0 ? sumOfFinalRatios / totalRenderersAffected : 0f;
            }
            else
            {
                float sumOfFinalRatios = 0f;
                foreach (var kvp in meshToRenderers)
                {
                    bool isMeshLocallyProtected = locallyProtectedMeshes.Contains(kvp.Key) || IsLocalGoProtected(kvp.Value[0].gameObject, locallyProtectedGameObjects);
                    if (isMeshLocallyProtected && usePreserveRatio)
                    {
                        sumOfFinalRatios += preserveRatio * kvp.Value.Count;
                    }
                    else
                    {
                        sumOfFinalRatios += targetRatio * kvp.Value.Count;
                        predictedSimplifiableTris += GetTriangleCount(kvp.Key) * targetRatio * kvp.Value.Count;
                    }
                }
                finalAvgRatio = totalRenderersAffected > 0 ? sumOfFinalRatios / totalRenderersAffected : 0f;
            }

            // Save results to static fields for GUI drawing
            showDryRunResults = true;
            dryRunUnsimplifiable = totalUnsimplifiableRenderedTris;
            dryRunSimplifiable = totalSimplifiableRenderedTris;
            dryRunRatio = finalAvgRatio;
            dryRunSimplifiableCount = uniqueSimplifiableCount;
            dryRunTotalRenderers = totalRenderersAffected;
            dryRunPredictedTotal = totalUnsimplifiableRenderedTris + (int)predictedSimplifiableTris;
        }

        private static void EnsureLocalPreserveListLoaded() { }

        private static void SaveLocalPreserveList()
        {
            var prot = SynProtectionData.GetInstance();
            if (prot != null)
            {
                EditorUtility.SetDirty(prot);
                AssetDatabase.SaveAssets();
            }
        }

        private static void EnsureLocalSkipListLoaded() { }

        private static void SaveLocalSkipList()
        {
            var prot = SynProtectionData.GetInstance();
            if (prot != null)
            {
                EditorUtility.SetDirty(prot);
                AssetDatabase.SaveAssets();
            }
        }

        private static string GetGameObjectPath(GameObject obj)
        {
            string path = obj.name;
            Transform current = obj.transform;
            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }
            return path;
        }

        private static void AutoTuneTierRatios(int targetCap)
        {
            Scene scene = SceneManager.GetActiveScene();
            List<Renderer> renderers = SynSceneQuery.GetAllRenderers(scene);

            int minTriangleCount = SynSceneOptimizerSettings.GetInt("MeshSimplifier_Mobile_MinTriangleCount", 4000);

            // Group simplifiable renderers by mesh and count how many triangles are in each tier
            int totalUnsimplifiableTris = 0;
            
            // Tier original triangle counts
            int tris50k = 0;
            int tris20k = 0;
            int tris8k = 0;
            int tris4k = 0;
            int trisDef = 0;

            // Extract all meshes used by the objects in the global protection list
            var protectedMeshesFromGOs = new HashSet<Mesh>();
            var protData = SynProtectionData.GetInstance();
            if (protData != null)
            {
                foreach (GameObject go in protData.protectedGameObjects)
                {
                    if (go == null) continue;
                    foreach (var childRenderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh m = null;
                        if (childRenderer is MeshRenderer)
                        {
                            var mf = childRenderer.GetComponent<MeshFilter>();
                            if (mf != null) m = mf.sharedMesh;
                        }
                        else if (childRenderer is SkinnedMeshRenderer smr)
                        {
                            m = smr.sharedMesh;
                        }
                        if (m != null) protectedMeshesFromGOs.Add(m);
                    }
                }
            }

            // Extract local preserve list meshes
            var locallyProtectedMeshes = new HashSet<Mesh>();
            var locallyProtectedGameObjects = new HashSet<GameObject>();
            foreach (var refObj in localPreserveList)
            {
                if (refObj == null) continue;
                var obj = refObj.Resolve();
                if (obj == null) continue;

                if (obj is GameObject go)
                {
                    locallyProtectedGameObjects.Add(go);
                    foreach (var childRenderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh m = null;
                        if (childRenderer is MeshRenderer)
                        {
                            var mf = childRenderer.GetComponent<MeshFilter>();
                            if (mf != null) m = mf.sharedMesh;
                        }
                        else if (childRenderer is SkinnedMeshRenderer smr)
                        {
                            m = smr.sharedMesh;
                        }
                        if (m != null) locallyProtectedMeshes.Add(m);
                    }
                }
                else if (obj is Mesh meshAsset)
                {
                    locallyProtectedMeshes.Add(meshAsset);
                }
            }

            // Extract local skip list meshes
            var locallySkippedMeshes = new HashSet<Mesh>();
            var locallySkippedGameObjects = new HashSet<GameObject>();
            foreach (var refObj in localSkipList)
            {
                if (refObj == null) continue;
                var obj = refObj.Resolve();
                if (obj == null) continue;

                if (obj is GameObject go)
                {
                    locallySkippedGameObjects.Add(go);
                    foreach (var childRenderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh m = null;
                        if (childRenderer is MeshRenderer)
                        {
                            var mf = childRenderer.GetComponent<MeshFilter>();
                            if (mf != null) m = mf.sharedMesh;
                        }
                        else if (childRenderer is SkinnedMeshRenderer smr)
                        {
                            m = smr.sharedMesh;
                        }
                        if (m != null) locallySkippedMeshes.Add(m);
                    }
                }
                else if (obj is Mesh meshAsset)
                {
                    locallySkippedMeshes.Add(meshAsset);
                }
            }

            bool usePreserveRatio = SynSceneOptimizerSettings.GetBool("MeshSimplifier_UsePreserveRatio", false);
            float preserveRatio = SynSceneOptimizerSettings.GetFloat("MeshSimplifier_PreserveRatio", 0.80f);

            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;
                if (IsEditorOnly(r.transform)) continue;

                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh != null)
                    {
                        totalUnsimplifiableTris += GetTriangleCount(smr.sharedMesh);
                    }
                    continue;
                }

                Mesh mesh = null;
                if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }

                if (mesh == null || mesh.vertexCount == 0) continue;

                int triCount = GetTriangleCount(mesh);
                
                bool isMeshGloballyProtected = SynProtectionData.IsProtected(mesh);
                bool isGoGloballyProtected = SynProtectionData.IsProtected(r.gameObject);
                bool isMeshLocallySkipped = locallySkippedMeshes.Contains(mesh);
                bool isGoLocallySkipped = IsLocalGoProtected(r.gameObject, locallySkippedGameObjects);
                bool isMeshLocallyProtected = locallyProtectedMeshes.Contains(mesh);
                bool isGoLocallyProtected = IsLocalGoProtected(r.gameObject, locallyProtectedGameObjects);

                bool isGloballyProtected = isMeshGloballyProtected || isGoGloballyProtected;
                bool isLocallySkipped = isMeshLocallySkipped || isGoLocallySkipped;
                bool isLocallyProtected = isMeshLocallyProtected || isGoLocallyProtected;

                if (isGloballyProtected || isLocallySkipped)
                {
                    totalUnsimplifiableTris += triCount;
                }
                else if (isLocallyProtected)
                {
                    if (usePreserveRatio)
                    {
                        totalUnsimplifiableTris += (int)(triCount * preserveRatio);
                    }
                    else
                    {
                        totalUnsimplifiableTris += triCount;
                    }
                }
                else
                {
                    bool qualifies = triCount >= minTriangleCount;
                    if (qualifies)
                    {
                        if (triCount >= 50000) tris50k += triCount;
                        else if (triCount >= 20000) tris20k += triCount;
                        else if (triCount >= 8000) tris8k += triCount;
                        else if (triCount >= 4000) tris4k += triCount;
                        else trisDef += triCount;
                    }
                    else
                    {
                        totalUnsimplifiableTris += triCount;
                    }
                }
            }

            int budget = targetCap - totalUnsimplifiableTris;
            if (budget <= 0)
            {
                // Can't fit, force everything to 10%
                autoTunedTierRatios = new[] { 0.10f, 0.10f, 0.10f, 0.10f, 0.10f };
                return;
            }

            // Top-down greedy solver:
            // Ratios default: 50k -> 0.20, 20k -> 0.35, 8k -> 0.50, 4k -> 0.65, Default -> 0.80
            float r50k = 0.20f;
            float r20k = 0.35f;
            float r8k = 0.50f;
            float r4k = 0.65f;
            float rDef = 0.80f;

            float currentSum = tris50k * r50k + tris20k * r20k + tris8k * r8k + tris4k * r4k + trisDef * rDef;

            if (currentSum > budget)
            {
                // We need to reduce ratios. Let's start with r50k (from 0.20 down to 0.10)
                float neededReduction = currentSum - budget;
                float possible50kReduction = tris50k * (r50k - 0.10f);

                if (possible50kReduction >= neededReduction && tris50k > 0)
                {
                    r50k -= neededReduction / tris50k;
                }
                else
                {
                    r50k = 0.10f;
                    currentSum = tris50k * r50k + tris20k * r20k + tris8k * r8k + tris4k * r4k + trisDef * rDef;
                    neededReduction = currentSum - budget;

                    // Reduce r20k (from 0.35 down to 0.10)
                    float possible20kReduction = tris20k * (r20k - 0.10f);
                    if (possible20kReduction >= neededReduction && tris20k > 0)
                    {
                        r20k -= neededReduction / tris20k;
                    }
                    else
                    {
                        r20k = 0.10f;
                        currentSum = tris50k * r50k + tris20k * r20k + tris8k * r8k + tris4k * r4k + trisDef * rDef;
                        neededReduction = currentSum - budget;

                        // Reduce r8k (from 0.50 down to 0.10)
                        float possible8kReduction = tris8k * (r8k - 0.10f);
                        if (possible8kReduction >= neededReduction && tris8k > 0)
                        {
                            r8k -= neededReduction / tris8k;
                        }
                        else
                        {
                            r8k = 0.10f;
                            currentSum = tris50k * r50k + tris20k * r20k + tris8k * r8k + tris4k * r4k + trisDef * rDef;
                            neededReduction = currentSum - budget;

                            // Reduce r4k (from 0.65 down to 0.10)
                            float possible4kReduction = tris4k * (r4k - 0.10f);
                            if (possible4kReduction >= neededReduction && tris4k > 0)
                            {
                                r4k -= neededReduction / tris4k;
                            }
                            else
                            {
                                r4k = 0.10f;
                                currentSum = tris50k * r50k + tris20k * r20k + tris8k * r8k + tris4k * r4k + trisDef * rDef;
                                neededReduction = currentSum - budget;

                                // Reduce rDef (from 0.80 down to 0.10)
                                float possibleDefReduction = trisDef * (rDef - 0.10f);
                                if (possibleDefReduction >= neededReduction && trisDef > 0)
                                {
                                    rDef -= neededReduction / trisDef;
                                }
                                else
                                {
                                    rDef = 0.10f;
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                // We have leftover budget! We can increase ratios to preserve quality.
                // Let's start by raising the default tier (from 0.80 up to 0.95)
                float extraBudget = budget - currentSum;
                float possibleDefIncrease = trisDef * (0.95f - rDef);
                if (possibleDefIncrease >= extraBudget && trisDef > 0)
                {
                    rDef += extraBudget / trisDef;
                }
                else
                {
                    rDef = 0.95f;
                    currentSum = tris50k * r50k + tris20k * r20k + tris8k * r8k + tris4k * r4k + trisDef * rDef;
                    extraBudget = budget - currentSum;

                    // Increase r4k (from 0.65 up to 0.95)
                    float possible4kIncrease = tris4k * (0.95f - r4k);
                    if (possible4kIncrease >= extraBudget && tris4k > 0)
                    {
                        r4k += extraBudget / tris4k;
                    }
                    else
                    {
                        r4k = 0.95f;
                        currentSum = tris50k * r50k + tris20k * r20k + tris8k * r8k + tris4k * r4k + trisDef * rDef;
                        extraBudget = budget - currentSum;

                        // Increase r8k (from 0.50 up to 0.95)
                        float possible8kIncrease = tris8k * (0.95f - r8k);
                        if (possible8kIncrease >= extraBudget && tris8k > 0)
                        {
                            r8k += extraBudget / tris8k;
                        }
                        else
                        {
                            r8k = 0.95f;
                            currentSum = tris50k * r50k + tris20k * r20k + tris8k * r8k + tris4k * r4k + trisDef * rDef;
                            extraBudget = budget - currentSum;

                            // Increase r20k (from 0.35 up to 0.95)
                            float possible20kIncrease = tris20k * (0.95f - r20k);
                            if (possible20kIncrease >= extraBudget && tris20k > 0)
                            {
                                r20k += extraBudget / tris20k;
                            }
                            else
                            {
                                r20k = 0.95f;
                                currentSum = tris50k * r50k + tris20k * r20k + tris8k * r8k + tris4k * r4k + trisDef * rDef;
                                extraBudget = budget - currentSum;

                                // Increase r50k (from 0.20 up to 0.95)
                                float possible50kIncrease = tris50k * (0.95f - r50k);
                                if (possible50kIncrease >= extraBudget && tris50k > 0)
                                {
                                    r50k += extraBudget / tris50k;
                                }
                                else
                                {
                                    r50k = 0.95f;
                                }
                            }
                        }
                    }
                }
            }

            // Use the tuned ratios for this run only; the user's tier sliders are never overwritten
            autoTunedTierRatios = new[] { r50k, r20k, r8k, r4k, rDef };
        }
    }
}
