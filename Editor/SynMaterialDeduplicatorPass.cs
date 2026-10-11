using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Points renderers that use exact duplicate materials (identical shader, keywords, textures and values) at one
    /// shared copy, so they can share static batches and instanced draws. Script and Udon references to the
    /// duplicates are pointed at the same copy, so material swaps and comparisons keep working. Runs before the
    /// mesh, instancing and palette passes so they all see fewer materials. Changes only the build or Play Mode
    /// copy of the scene; material assets are never edited.
    /// </summary>
    public class SynMaterialDeduplicatorPass : SynOptimizationPass
    {
        public override string Id => "SynMaterialDeduplicatorPass";
        public override string Name => "Material Deduplicator";
        public override string Description => "Merges materials that are exact copies of each other (same shader, keywords, textures and values) so objects using them can batch together. Script and Udon references are updated too. The Material Audit shows what would merge.";
        public override string Category => "Batching & Instancing";
        public override int Priority => 8;
        public override string Tab => "Optimizers";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            var animated = CollectAnimatedMaterials(scene);

            // Materials in use, from renderers this pass may change
            var usage = new Dictionary<Material, List<Renderer>>();
            int skippedRenderers = 0;
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (SynProtectionData.IsProtected(r.gameObject) || SynSceneQuery.IsVideoComponentDetected(r))
                {
                    skippedRenderers++;
                    continue;
                }
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    if (!usage.TryGetValue(m, out var list))
                    {
                        list = new List<Renderer>();
                        usage[m] = list;
                    }
                    if (!list.Contains(r)) list.Add(r);
                }
            }

            var remap = new Dictionary<Material, Material>();
            int leftAlone = 0;
            foreach (var group in usage.Keys.GroupBy(m => SynMaterialAnalysis.Signature(m, includeMainTiling: true)))
            {
                var members = group.ToList();
                if (members.Count < 2) continue;

                // Protected materials and materials an animation swaps in stay exactly as they are
                var mergeable = members.Where(m => !SynProtectionData.IsProtected(m) && !animated.Contains(m)).ToList();
                leftAlone += members.Count - mergeable.Count;
                if (mergeable.Count < 2) continue;

                // Keep the most used one (ties: the one saved as an asset, then by name) so the fewest renderers change
                Material keep = mergeable
                    .OrderByDescending(m => usage[m].Count)
                    .ThenByDescending(m => !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(m)))
                    .ThenBy(m => m.name)
                    .First();
                foreach (Material m in mergeable)
                {
                    if (m != keep) remap[m] = keep;
                }
            }

            if (remap.Count == 0)
            {
                SynPipelineCompactor.LogChange("Material Deduplicator", leftAlone > 0
                    ? $"No duplicate materials to merge ({leftAlone} duplicates left alone: protected or swapped by an animation)."
                    : "No duplicate materials found.");
                return;
            }

            int renderersChanged = 0;
            foreach (var kvp in usage)
            {
                if (!remap.ContainsKey(kvp.Key)) continue;
                foreach (Renderer r in kvp.Value)
                {
                    Material[] mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null && remap.TryGetValue(mats[i], out Material keep))
                        {
                            mats[i] = keep;
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        r.sharedMaterials = mats;
                        renderersChanged++;
                    }
                }
            }

            int referencesRewritten = RewriteScriptReferences(scene, remap);
            int kept = remap.Values.Distinct().Count();

            SynPipelineCompactor.LogChange("Material Deduplicator",
                $"Merged {remap.Count + kept} duplicate materials into {kept} ({remap.Count} fewer) on {renderersChanged} renderers; pointed {referencesRewritten} script/Udon references at the kept copies." +
                (leftAlone > 0 ? $" {leftAlone} duplicates left alone (protected, swapped by an animation or held by a protected script)." : ""));
        }

        /// <summary>
        /// Points serialized material references on the scene's scripts at the kept copy: UdonSharp fields and the
        /// reference list an UdonBehaviour rebuilds its variables from (UdonSharp copies its fields into Udon during
        /// the build, so both sides are rewritten).
        /// </summary>
        private static int RewriteScriptReferences(Scene scene, Dictionary<Material, Material> remap)
        {
            int count = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || SynProtectionData.IsProtected(behaviour.gameObject)) continue;

                    var so = new SerializedObject(behaviour);
                    var iterator = so.GetIterator();
                    bool changed = false;
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                            iterator.objectReferenceValue is Material mat &&
                            remap.TryGetValue(mat, out Material keep))
                        {
                            iterator.objectReferenceValue = keep;
                            changed = true;
                            count++;
                        }
                    }
                    if (changed) so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            return count;
        }

        // Materials an animation clip assigns to a renderer, or that a protected script holds
        private static HashSet<Material> CollectAnimatedMaterials(Scene scene)
        {
            var materials = new HashSet<Material>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
                {
                    if (animator.runtimeAnimatorController == null) continue;
                    foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                    {
                        if (clip == null) continue;
                        foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        {
                            foreach (ObjectReferenceKeyframe key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                            {
                                if (key.value is Material m) materials.Add(m);
                            }
                        }
                    }
                }

                // Scripts on protected objects aren't rewritten, so what they hold must stay as it is
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || !SynProtectionData.IsProtected(behaviour.gameObject)) continue;
                    var iterator = new SerializedObject(behaviour).GetIterator();
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Material m) materials.Add(m);
                    }
                }
            }
            return materials;
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Only exact copies are merged, so nothing looks different. Materials swapped in by animations and protected materials are left alone.", MessageType.None);
            if (GUILayout.Button(new GUIContent("Open Material Audit...", "Shows which materials in the open scene are duplicates."), GUILayout.Height(22)))
            {
                SynMaterialAuditWindow.ShowWindow();
            }
        }
    }
}
