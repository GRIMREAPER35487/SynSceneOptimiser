using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    public class MaterialReflectionOptimizerPass : SynOptimizationPass
    {
        public override string Id => "MaterialReflectionOptimizerPass";
        public override string Name => "Material Reflection Optimizer";
        public override string Description => "Disables reflection probes on renderers that only use matte materials to reduce rendering overhead.";
        public override string Category => "Structural Cleanup";
        public override int Priority => 10;

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            float threshold = SynSceneOptimizerSettings.GetFloat("SmoothnessThreshold", 0.05f);
            float newThreshold = EditorGUILayout.Slider("Smoothness Threshold", threshold, 0f, 1f);
            if (Mathf.Abs(newThreshold - threshold) > 0.0001f)
            {
                SynSceneOptimizerSettings.SetFloat("SmoothnessThreshold", newThreshold);
            }
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            int optimizedCount = 0;
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;
                if (IsMirrorRenderer(r)) continue;

                bool hasProtected = false;
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat != null && SynProtectionData.IsProtected(mat)) hasProtected = true;
                }
                Mesh mesh = null;
                if (r is MeshRenderer)
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                else if (r is SkinnedMeshRenderer)
                {
                    mesh = ((SkinnedMeshRenderer)r).sharedMesh;
                }
                if (mesh != null && SynProtectionData.IsProtected(mesh)) hasProtected = true;
                if (hasProtected) continue;

                bool requiresReflection = false;
                Material[] sharedMats = r.sharedMaterials;
                foreach (Material mat in sharedMats)
                {
                    if (mat != null && SynShaderAnalyzer.RequiresReflectionFidelity(mat))
                    {
                        requiresReflection = true;
                        break;
                    }
                }

                // If no materials require reflection, turn reflection probe usage Off
                if (!requiresReflection)
                {
                    if (r.reflectionProbeUsage != ReflectionProbeUsage.Off)
                    {
                        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                        EditorUtility.SetDirty(r);
                        optimizedCount++;
                    }
                }
            }

            if (optimizedCount > 0)
            {
                SynPipelineCompactor.LogChange(
                    "Reflection Probe",
                    string.Format("Disabled reflection probes on {0} matte renderers.", optimizedCount)
                );
            }
        }

        private bool IsMirrorRenderer(Renderer r)
        {
            if (r == null) return false;
            
            // Check components on the GameObject
            var components = r.GetComponents<MonoBehaviour>();
            foreach (var c in components)
            {
                if (c == null) continue;
                string typeName = c.GetType().FullName;
                if (typeName != null && typeName.Contains("Mirror")) return true;
            }
            
            // Check materials and shaders
            Material[] sharedMats = r.sharedMaterials;
            foreach (Material mat in sharedMats)
            {
                if (mat == null) continue;
                if (mat.name.ToLower().Contains("mirror")) return true;
                if (mat.shader != null && mat.shader.name.ToLower().Contains("mirror")) return true;
            }
            
            return false;
        }
    }

    public class MaterialSlotTrimmerPass : SynOptimizationPass
    {
        public override string Id => "MaterialSlotTrimmerPass";
        public override string Name => "Material Slot Trimmer";
        public override string Description => "Truncates material list indices that exceed the underlying mesh submesh count, or clears them if no mesh is assigned.";
        public override string Category => "Structural Cleanup";
        public override int Priority => 20;

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            bool logTrim = SynSceneOptimizerSettings.GetBool("SlotTrimmer_LogTrimmed", true);
            bool newLogTrim = EditorGUILayout.Toggle(new GUIContent("Log Trimmed Slots", "If checked, prints details about which GameObjects had extra/empty material slots stripped to the console."), logTrim);
            if (newLogTrim != logTrim)
            {
                SynSceneOptimizerSettings.SetBool("SlotTrimmer_LogTrimmed", newLogTrim);
            }
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool logTrimmed = SynSceneOptimizerSettings.GetBool("SlotTrimmer_LogTrimmed", true);

            int totalRenderersChecked = 0;
            int trimmedCount = 0;
            int totalSlotsRemoved = 0;

            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;

                totalRenderersChecked++;

                Mesh mesh = null;
                if (r is MeshRenderer)
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                else if (r is SkinnedMeshRenderer)
                {
                    SkinnedMeshRenderer smr = (SkinnedMeshRenderer)r;
                    mesh = smr.sharedMesh;
                }

                bool hasProtected = false;
                if (mesh != null && SynProtectionData.IsProtected(mesh)) hasProtected = true;
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat != null && SynProtectionData.IsProtected(mat)) hasProtected = true;
                }
                if (hasProtected) continue;

                Material[] sharedMats = r.sharedMaterials;
                if (sharedMats == null || sharedMats.Length == 0) continue;

                int targetSize = sharedMats.Length;
                string reason = "";

                if (mesh == null)
                {
                    targetSize = 0;
                    reason = "Renderer has no Mesh assigned.";
                }
                else
                {
                    int submeshCount = mesh.subMeshCount;
                    if (sharedMats.Length > submeshCount)
                    {
                        targetSize = submeshCount;
                        reason = $"Renderer has {sharedMats.Length} slots but the Mesh only has {submeshCount} submeshes.";
                    }
                }

                if (sharedMats.Length > targetSize)
                {
                    int slotsRemoved = sharedMats.Length - targetSize;
                    Material[] trimmedMats = new Material[targetSize];

                    if (targetSize > 0)
                    {
                        System.Array.Copy(sharedMats, trimmedMats, targetSize);
                    }

                    r.sharedMaterials = trimmedMats;
                    EditorUtility.SetDirty(r);

                    trimmedCount++;
                    totalSlotsRemoved += slotsRemoved;

                    if (logTrimmed)
                    {
                        Debug.Log($"[SlotTrimmer] Trimmed {slotsRemoved} extra material slots from <b>{r.gameObject.name}</b>. Reason: {reason}");
                    }
                }
            }

            if (totalSlotsRemoved > 0)
            {
                SynPipelineCompactor.LogChange(
                    "Slot Trimmer",
                    string.Format("Material Slot Trimmer: Checked {0} renderers. Trimmed {1} renderers, removing {2} redundant slots.", 
                        totalRenderersChecked, trimmedCount, totalSlotsRemoved)
                );
            }
        }
    }

    public class LightProbeOptimizerPass : SynOptimizationPass
    {
        public override string Id => "LightProbeOptimizerPass";
        public override string Name => "Light Probe Optimizer";
        public override string Description => "Disables light probes on static renderers using baked lightmaps to reduce CPU/GPU sorting overhead.";
        public override string Category => "Structural Cleanup";
        public override int Priority => 15;

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            int optimizedCount = 0;

            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;

                // Check if renderer is static and has baked lightmaps
                // 0xFFFE = static with Scale In Lightmap 0: lit by probes, not a lightmap, so probes must stay on
                if (r.gameObject.isStatic && r.lightmapIndex >= 0 && r.lightmapIndex < 0xFFFE)
                {
                    if (r.lightProbeUsage != LightProbeUsage.Off)
                    {
                        r.lightProbeUsage = LightProbeUsage.Off;
                        EditorUtility.SetDirty(r);
                        optimizedCount++;
                    }
                }
            }

            if (optimizedCount > 0)
            {
                SynPipelineCompactor.LogChange(
                    "Light Probe",
                    string.Format("Disabled light probes on {0} static lightmapped renderers.", optimizedCount)
                );
            }
        }
    }

    public class GhostTexturePurgerPass : SynOptimizationPass
    {
        public override string Id => "GhostTexturePurgerPass";
        public override string Name => "Ghost Texture Purger";
        public override string Description => "Cleans up unused texture serialized references inside material data files to save VRAM, including textures behind shader features that are turned off.";
        public override string Category => "Structural Cleanup";
        public override int Priority => 25;

        private const string UnsampledKey = "GhostPurger_UnsampledSlots";

        /// <summary>Whether textures behind disabled shader features are cleared (also used for generated materials).</summary>
        public static bool ClearsUnsampledTextures =>
            SynSceneOptimizerSettings.GetBool("Pass_GhostTexturePurgerPass_Enabled", true) && SynSceneOptimizerSettings.GetBool(UnsampledKey, true);
        // Compiling variants is slow on the first build; the rest is picked up on later builds from the cache
        private const double CompileBudgetSeconds = 180;

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            bool unsampled = SynSceneOptimizerSettings.GetBool(UnsampledKey, true);
            bool newUnsampled = EditorGUILayout.Toggle(new GUIContent("Clear Disabled Feature Textures", "Also clears textures the material's shader never samples because their feature is turned off (e.g. a rain or detail texture with rain/detail disabled). Each shader variant is compiled once to check, then cached. Udon scripts are read to find every shader keyword they can turn on or off, and a texture is only cleared if none of those would make the shader read it."), unsampled);
            if (newUnsampled != unsampled)
            {
                SynSceneOptimizerSettings.SetBool(UnsampledKey, newUnsampled);
            }
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool checkUnsampled = SynSceneOptimizerSettings.GetBool(UnsampledKey, true);
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            SynUdonUsageScan scan = checkUnsampled ? SynUdonUsageScan.Run(scene) : null;

            HashSet<Material> processed = new HashSet<Material>();
            int purgedCount = 0;
            int unsampledCount = 0;
            int skippedForBudget = 0;
            var unsampledTextures = new HashSet<Texture>();
            var compileTimer = new System.Diagnostics.Stopwatch();

            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;

                Material[] sharedMats = r.sharedMaterials;
                foreach (Material mat in sharedMats)
                {
                    if (mat == null || processed.Contains(mat)) continue;
                    if (SynProtectionData.IsProtected(mat)) continue;
                    processed.Add(mat);

                    SynVirtualMaterialState matState = SynPipelineCompactor.GetStagingState(mat);
                    if (matState == null) continue;

                    SerializedObject serializedMat = new SerializedObject(mat);
                    SerializedProperty texEnvs = serializedMat.FindProperty("m_SavedProperties.m_TexEnvs");
                    if (texEnvs != null && texEnvs.isArray)
                    {
                        var keywords = new List<string>(matState.TrackedKeywords);
                        bool clearedAny = false;
                        for (int i = texEnvs.arraySize - 1; i >= 0; i--)
                        {
                            SerializedProperty prop = texEnvs.GetArrayElementAtIndex(i);
                            SerializedProperty nameProp = prop.FindPropertyRelative("first");
                            if (nameProp != null)
                            {
                                string texPropName = nameProp.stringValue;
                                // Only slots that still hold a texture cost anything; empty ghost slots are left alone
                                SerializedProperty texProp = prop.FindPropertyRelative("second.m_Texture");
                                bool holdsTexture = texProp != null && texProp.objectReferenceValue != null;
                                if (!holdsTexture || matState.TargetShader == null) continue;

                                if (!HasPropertyOnShader(matState.TargetShader, texPropName))
                                {
                                    matState.PurgedTextureProperties.Add(texPropName);
                                    matState.IsDirty = true;
                                    clearedAny = true;
                                    purgedCount++;
                                    continue;
                                }

                                if (!checkUnsampled || scan.KeywordsUnknown) continue;
                                // A script that reads this slot out of a material could show the texture elsewhere
                                if (scan.TextureReadsUnknown || scan.TextureReads.Contains(texPropName)) continue;

                                bool allowCompile = compileTimer.Elapsed.TotalSeconds < CompileBudgetSeconds;
                                compileTimer.Start();
                                bool? canSample = SynShaderTextureUsage.CanSample(matState.TargetShader, keywords, texPropName, scan, target, allowCompile);
                                compileTimer.Stop();
                                if (canSample == null && !allowCompile) skippedForBudget++;

                                // The shader declares the slot, but no variant this material can be in reads it
                                if (canSample == false)
                                {
                                    matState.UnsampledTextureProperties.Add(texPropName);
                                    matState.TrackedTextures[texPropName] = null;
                                    matState.IsDirty = true;
                                    clearedAny = true;
                                    unsampledCount++;
                                    unsampledTextures.Add(texProp.objectReferenceValue as Texture);
                                }
                            }
                        }

                        if (clearedAny)
                        {
                            if (!matState.AppliedPassTags.Contains("Purged"))
                            {
                                matState.AppliedPassTags.Add("Purged");
                            }
                        }
                    }
                }
            }

            SynShaderTextureUsage.SaveCache();

            if (purgedCount > 0)
            {
                SynPipelineCompactor.LogChange(
                    "Ghost Texture Purger",
                    string.Format("Cleared {0} unused texture references from material serialization blocks.", purgedCount)
                );
            }

            if (unsampledCount > 0)
            {
                var largest = new List<Texture>(unsampledTextures);
                largest.RemoveAll(t => t == null);
                largest.Sort((a, b) => ((long)b.width * b.height).CompareTo((long)a.width * a.height));
                var names = new List<string>();
                for (int i = 0; i < largest.Count && i < 3; i++) names.Add($"{largest[i].name} ({largest[i].width}x{largest[i].height})");

                SynPipelineCompactor.LogChange(
                    "Ghost Texture Purger",
                    $"Cleared {unsampledCount} texture slots whose shader feature is turned off ({unsampledTextures.Count} distinct textures, largest: {string.Join(", ", names)}). A texture leaves the build once nothing else references it."
                );
            }

            if (scan != null && scan.KeywordsUnknown)
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Ghost Texture Purger: textures behind disabled shader features were kept, because a script could turn on any feature: {string.Join("; ", scan.UnknownReasons)}.");
            }
            else if (scan != null && scan.Keywords.Count > 0)
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Ghost Texture Purger: Udon scripts can set these shader keywords, which were checked too: {string.Join(", ", scan.Keywords)}.");
            }

            if (skippedForBudget > 0)
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Ghost Texture Purger: checked shaders for {compileTimer.Elapsed.TotalSeconds:F0}s; {skippedForBudget} materials will be checked on the next build (results are cached).");
            }
        }

        private bool HasPropertyOnShader(Shader shader, string propName)
        {
            if (shader == null) return false;
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyName(shader, i) == propName)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
