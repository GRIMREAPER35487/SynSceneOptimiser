using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimization pass that completely destroys all EditorOnly-tagged GameObjects and their child hierarchies
    /// in the temporary build/play scene before packaging, preventing unused textures, meshes, and audio clips
    /// from being pulled into the VRChat world bundle as build dependencies.
    /// </summary>
    public class SynEditorOnlyPrunerPass : SynOptimizationPass
    {
        public override string Id => "SynEditorOnlyPrunerPass";
        public override string Name => "EditorOnly & Build Leak Pruner";
        public override string Description => "Nukes all EditorOnly-tagged objects and orphaned build artifacts in the temporary staging scene, completely preventing their textures and meshes from leaking into the final world file.";
        public override string Category => "Structural Cleanup";
        public override int Priority => 1; // Runs first before all other passes!

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Automatically destroys all 'EditorOnly' tagged objects and branches in the temporary staging scene before the world is packaged for VRChat. Your actual project scene remains 100% untouched.", MessageType.Info);

            bool nukeEditorOnly = SynSceneOptimizerSettings.GetBool("EditorOnlyPruner_NukeTagged", true);
            bool newNukeEditorOnly = EditorGUILayout.Toggle(new GUIContent("Nuke EditorOnly Objects", "Destroys all objects tagged 'EditorOnly' in the temporary build scene."), nukeEditorOnly);
            if (newNukeEditorOnly != nukeEditorOnly)
            {
                SynSceneOptimizerSettings.SetBool("EditorOnlyPruner_NukeTagged", newNukeEditorOnly);
            }

            bool stripEmptyRenderers = SynSceneOptimizerSettings.GetBool("EditorOnlyPruner_StripEmptyRenderers", true);
            bool newStripEmpty = EditorGUILayout.Toggle(new GUIContent("Strip Empty/Broken Renderers", "Removes MeshRenderers with missing meshes or empty material arrays to prevent build asset leaks."), stripEmptyRenderers);
            if (newStripEmpty != stripEmptyRenderers)
            {
                SynSceneOptimizerSettings.SetBool("EditorOnlyPruner_StripEmptyRenderers", newStripEmpty);
            }

            bool verbose = SynSceneOptimizerSettings.GetBool("EditorOnlyPruner_VerboseLog", false);
            bool newVerbose = EditorGUILayout.Toggle(new GUIContent("Log Pruned Objects", "Print detailed list of pruned GameObjects in the console during build."), verbose);
            if (newVerbose != verbose)
            {
                SynSceneOptimizerSettings.SetBool("EditorOnlyPruner_VerboseLog", newVerbose);
            }
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool nukeEditorOnly = SynSceneOptimizerSettings.GetBool("EditorOnlyPruner_NukeTagged", true);
            bool stripEmptyRenderers = SynSceneOptimizerSettings.GetBool("EditorOnlyPruner_StripEmptyRenderers", true);
            bool verbose = SynSceneOptimizerSettings.GetBool("EditorOnlyPruner_VerboseLog", false) || SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false);

            if (!scene.IsValid() || !scene.isLoaded) return;

            int prunedObjectCount = 0;
            int strippedRendererCount = 0;
            var prunedNames = new List<string>();

            // 1. Nuke EditorOnly-tagged GameObjects and branches (protecting VRChat SDK & ClientSim)
            if (nukeEditorOnly)
            {
                var rootObjects = scene.GetRootGameObjects();
                var objectsToDestroy = new List<GameObject>();

                foreach (var root in rootObjects)
                {
                    if (root == null) continue;
                    FindEditorOnlyObjectsRecursive(root.transform, objectsToDestroy);
                }

                foreach (var go in objectsToDestroy)
                {
                    if (go != null)
                    {
                        prunedObjectCount++;
                        if (prunedNames.Count < 25) prunedNames.Add(go.name);

                        // Remove from the active renderers list so downstream passes don't process it
                        renderers.RemoveAll(r => r == null || r.gameObject == go || r.transform.IsChildOf(go.transform));

                        if (Application.isPlaying)
                        {
                            UnityEngine.Object.Destroy(go);
                        }
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(go);
                        }
                    }
                }
            }

            // 2. Strip structurally broken or empty renderers that leak memory
            if (stripEmptyRenderers)
            {
                for (int i = renderers.Count - 1; i >= 0; i--)
                {
                    var r = renderers[i];
                    if (r == null)
                    {
                        renderers.RemoveAt(i);
                        continue;
                    }

                    // If renderer has no materials or null array
                    var mats = r.sharedMaterials;
                    bool hasValidMat = false;
                    if (mats != null && mats.Length > 0)
                    {
                        foreach (var m in mats)
                        {
                            if (m != null) { hasValidMat = true; break; }
                        }
                    }

                    // Check mesh validity
                    bool hasValidMesh = false;
                    if (r is MeshRenderer mr)
                    {
                        var filter = mr.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh != null && filter.sharedMesh.vertexCount > 0)
                        {
                            hasValidMesh = true;
                        }
                    }
                    else if (r is SkinnedMeshRenderer smr)
                    {
                        if (smr.sharedMesh != null && smr.sharedMesh.vertexCount > 0)
                        {
                            hasValidMesh = true;
                        }
                    }

                    if (!hasValidMat || !hasValidMesh)
                    {
                        strippedRendererCount++;
                        if (verbose)
                        {
                            Debug.Log($"[SynEditorOnlyPrunerPass] Stripped broken/empty renderer on '{r.gameObject.name}' (ValidMat: {hasValidMat}, ValidMesh: {hasValidMesh})");
                        }

                        // Remove component to prevent build bundle dependency resolution
                        renderers.RemoveAt(i);
                        if (Application.isPlaying)
                        {
                            UnityEngine.Object.Destroy(r);
                        }
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(r);
                        }
                    }
                }
            }

            if (verbose && prunedObjectCount > 0)
            {
                Debug.Log($"[SynEditorOnlyPrunerPass] Pruned {prunedObjectCount} EditorOnly GameObjects. Samples: {string.Join(", ", prunedNames)}");
            }

            SynPipelineCompactor.LogChange(
                "EditorOnly Pruner",
                string.Format("Pruned {0} EditorOnly GameObjects and {1} empty renderers from build scene.", prunedObjectCount, strippedRendererCount)
            );
        }

        private void FindEditorOnlyObjectsRecursive(Transform current, List<GameObject> results)
        {
            if (current == null) return;

            if (current.CompareTag("EditorOnly"))
            {
                // In PlayMode, protect only the essential VRChat runtime infrastructure
                if (Application.isPlaying)
                {
                    bool isVrcSystem = current.GetComponent<VRC.SDKBase.VRC_SceneDescriptor>() != null ||
                                       current.name.StartsWith("ClientSim", StringComparison.OrdinalIgnoreCase) ||
                                       current.name.Equals("EventSystem", StringComparison.OrdinalIgnoreCase);

                    if (isVrcSystem)
                    {
                        // Protect this system root, but continue scanning its children to purge any other EditorOnly objects!
                        for (int i = 0; i < current.childCount; i++)
                        {
                            FindEditorOnlyObjectsRecursive(current.GetChild(i), results);
                        }
                        return;
                    }
                }

                // All other EditorOnly props, test rigs, and reference objects get purged in both PlayMode and BuildMode!
                results.Add(current.gameObject);
                return;
            }

            for (int i = 0; i < current.childCount; i++)
            {
                FindEditorOnlyObjectsRecursive(current.GetChild(i), results);
            }
        }
    }
}
