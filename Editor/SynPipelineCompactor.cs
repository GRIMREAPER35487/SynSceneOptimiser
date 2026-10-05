using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    public class SynVirtualMaterialState
    {
        public Material OriginalMaterial { get; set; }
        public Shader OriginalShader { get; set; }
        public Shader TargetShader { get; set; }
        public Dictionary<string, Texture> TrackedTextures { get; set; } = new Dictionary<string, Texture>();
        public Dictionary<string, Vector4> TrackedVectors { get; set; } = new Dictionary<string, Vector4>();
        public Dictionary<string, float> TrackedFloats { get; set; } = new Dictionary<string, float>();
        public HashSet<string> TrackedKeywords { get; set; } = new HashSet<string>();
        public bool IsDirty { get; set; }
        public List<string> AppliedPassTags { get; private set; } = new List<string>();

        // Analysis cache
        public bool IsGPUInstanced { get; set; }
        public bool HasVertexManipulation { get; set; }
    }

    public class SynVirtualMeshState
    {
        public Mesh OriginalMesh { get; set; }
        public List<Vector2> CustomUV0Channels { get; set; } = new List<Vector2>();
        public List<Vector4> CustomUV2Channels { get; set; } = new List<Vector4>();
        public List<Vector4> CustomUV3Channels { get; set; } = new List<Vector4>();
        public bool IsDirty { get; set; }
        public List<string> AppliedPassTags { get; private set; } = new List<string>();
    }

    public class SynVirtualRendererState
    {
        public Renderer OriginalRenderer { get; set; }
        public StaticEditorFlags StaticFlags { get; set; }
        public bool IsBatchingStatic { get; set; }
    }

    public static class SynPipelineCompactor
    {
        private static readonly Dictionary<Material, SynVirtualMaterialState> MaterialCache = new Dictionary<Material, SynVirtualMaterialState>();
        private static readonly Dictionary<Mesh, SynVirtualMeshState> MeshCache = new Dictionary<Mesh, SynVirtualMeshState>();
        private static readonly Dictionary<Renderer, SynVirtualRendererState> RendererCache = new Dictionary<Renderer, SynVirtualRendererState>();

        public struct FloatPropertyAssignment
        {
            public string Name;
            public float Value;
        }

        public struct VectorPropertyAssignment
        {
            public string Name;
            public Vector4 Value;
        }

        public static readonly Dictionary<Renderer, List<FloatPropertyAssignment>> RendererFloatAssignments = new Dictionary<Renderer, List<FloatPropertyAssignment>>();
        public static readonly Dictionary<Renderer, List<VectorPropertyAssignment>> RendererVectorAssignments = new Dictionary<Renderer, List<VectorPropertyAssignment>>();

        public static readonly List<string> PipelineLogSummary = new List<string>();
        private static readonly System.Diagnostics.Stopwatch PipelineStopwatch = new System.Diagnostics.Stopwatch();

        public static void LogChange(string passName, string message)
        {
            PipelineLogSummary.Add(string.Format(" • [{0}] {1}", passName, message));
        }

        public static void BeginStagingContext(Scene scene)
        {
            MaterialCache.Clear();
            MeshCache.Clear();
            RendererCache.Clear();
            RendererFloatAssignments.Clear();
            RendererVectorAssignments.Clear();
            PipelineLogSummary.Clear();

            PipelineStopwatch.Reset();
            PipelineStopwatch.Start();

            List<Renderer> renderers = SynSceneQuery.GetAllRenderers(scene);
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;

                // Cache renderer state and static flags
                if (!RendererCache.ContainsKey(r))
                {
                    StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                    var rState = new SynVirtualRendererState
                    {
                        OriginalRenderer = r,
                        StaticFlags = flags,
                        IsBatchingStatic = (flags & StaticEditorFlags.BatchingStatic) != 0
                    };
                    RendererCache[r] = rState;
                }

                // Cache materials
                Material[] sharedMats = r.sharedMaterials;
                foreach (Material mat in sharedMats)
                {
                    if (mat == null) continue;
                    if (!MaterialCache.ContainsKey(mat))
                    {
                        var state = new SynVirtualMaterialState
                        {
                            OriginalMaterial = mat,
                            OriginalShader = mat.shader,
                            TargetShader = mat.shader,
                            IsDirty = false,
                            IsGPUInstanced = mat.enableInstancing,
                            TrackedKeywords = new HashSet<string>(mat.shaderKeywords)
                        };

                        // Map active properties
                        Shader shader = mat.shader;
                        if (shader != null)
                        {
                            int propCount = ShaderUtil.GetPropertyCount(shader);
                            for (int i = 0; i < propCount; i++)
                            {
                                string propName = ShaderUtil.GetPropertyName(shader, i);
                                ShaderUtil.ShaderPropertyType propType = ShaderUtil.GetPropertyType(shader, i);

                                if (propType == ShaderUtil.ShaderPropertyType.TexEnv)
                                {
                                    state.TrackedTextures[propName] = mat.GetTexture(propName);
                                }
                                else if (propType == ShaderUtil.ShaderPropertyType.Vector || propType == ShaderUtil.ShaderPropertyType.Color)
                                {
                                    state.TrackedVectors[propName] = mat.GetVector(propName);
                                }
                                else if (propType == ShaderUtil.ShaderPropertyType.Float || propType == ShaderUtil.ShaderPropertyType.Range)
                                {
                                    state.TrackedFloats[propName] = mat.GetFloat(propName);
                                }
                            }
                        }

                        // Determine vertex manipulation
                        bool hasVertexDeform = false;
                        if (shader != null)
                        {
                            if (mat.IsKeywordEnabled("_VERTEX_MANIPULATION_ON") || 
                                mat.IsKeywordEnabled("_VERTEX_MANIPULATION") || 
                                mat.IsKeywordEnabled("_WIND_ON"))
                            {
                                hasVertexDeform = true;
                            }
                            if (mat.HasProperty("_VertexManipulationToggle") && mat.GetFloat("_VertexManipulationToggle") > 0.0f) hasVertexDeform = true;
                            if (mat.HasProperty("_WindToggle") && mat.GetFloat("_WindToggle") > 0.0f) hasVertexDeform = true;
                        }
                        if (hasVertexDeform || mat.name.ToLower().Contains("fan") || SynShaderAnalyzer.HasVertexManipulation(mat))
                        {
                            state.HasVertexManipulation = true;
                        }

                        MaterialCache[mat] = state;
                    }
                }

                // Cache mesh
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

                if (mesh != null && !MeshCache.ContainsKey(mesh))
                {
                    var state = new SynVirtualMeshState
                    {
                        OriginalMesh = mesh,
                        IsDirty = false
                    };

                    // Cache UV0 channel
                    List<Vector2> uv0 = new List<Vector2>();
                    mesh.GetUVs(0, uv0);
                    if (uv0.Count < mesh.vertexCount)
                    {
                        uv0 = new List<Vector2>(new Vector2[mesh.vertexCount]);
                    }
                    state.CustomUV0Channels = uv0;

                    // Cache UV2 channel
                    List<Vector4> uv2 = new List<Vector4>();
                    mesh.GetUVs(1, uv2);

                    // Ensure the list is at least vertexCount elements
                    if (uv2.Count < mesh.vertexCount)
                    {
                        uv2 = new List<Vector4>(new Vector4[mesh.vertexCount]);
                    }
                    state.CustomUV2Channels = uv2;

                    // Cache UV3 channel (maps to v.uv3 in Mochie standard shaders)
                    List<Vector4> uv3 = new List<Vector4>();
                    mesh.GetUVs(3, uv3);

                    if (uv3.Count < mesh.vertexCount)
                    {
                        uv3 = new List<Vector4>(new Vector4[mesh.vertexCount]);
                    }
                    state.CustomUV3Channels = uv3;

                    MeshCache[mesh] = state;
                }
            }
        }

        public static IEnumerable<SynVirtualMaterialState> GetAllStagedMaterials()
        {
            return MaterialCache.Values;
        }

        public static SynVirtualMaterialState GetStagingState(Material mat)
        {
            if (mat == null) return null;
            if (SynProtectionData.IsProtected(mat)) return null;
            if (MaterialCache.TryGetValue(mat, out var state))
            {
                return state;
            }

            // Dynamically register materials created mid-pipeline (e.g. by texture arrays/palettes)
            var newState = new SynVirtualMaterialState
            {
                OriginalMaterial = mat,
                OriginalShader = mat.shader,
                TargetShader = mat.shader,
                IsDirty = false,
                IsGPUInstanced = mat.enableInstancing,
                TrackedKeywords = new HashSet<string>(mat.shaderKeywords)
            };

            Shader shader = mat.shader;
            if (shader != null)
            {
                int propCount = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < propCount; i++)
                {
                    string propName = ShaderUtil.GetPropertyName(shader, i);
                    ShaderUtil.ShaderPropertyType propType = ShaderUtil.GetPropertyType(shader, i);

                    if (propType == ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        newState.TrackedTextures[propName] = mat.GetTexture(propName);
                    }
                    else if (propType == ShaderUtil.ShaderPropertyType.Vector || propType == ShaderUtil.ShaderPropertyType.Color)
                    {
                        newState.TrackedVectors[propName] = mat.GetVector(propName);
                    }
                    else if (propType == ShaderUtil.ShaderPropertyType.Float || propType == ShaderUtil.ShaderPropertyType.Range)
                    {
                        newState.TrackedFloats[propName] = mat.GetFloat(propName);
                    }
                }
            }

            MaterialCache[mat] = newState;
            return newState;
        }

        public static SynVirtualMeshState GetMeshStagingState(Mesh mesh)
        {
            if (mesh == null) return null;
            if (MeshCache.TryGetValue(mesh, out var state))
            {
                return state;
            }
            return null;
        }

        public static SynVirtualRendererState GetRendererStagingState(Renderer r)
        {
            if (r == null) return null;
            if (RendererCache.TryGetValue(r, out var state))
            {
                return state;
            }
            return null;
        }

        public static void CommitStagingContext(Scene scene)
        {
            int generatedAssetCount = 0;
            var bakedMaterials = new Dictionary<Material, Material>();
            var bakedMeshes = new Dictionary<Mesh, Mesh>();

            // 1. Bake dirty meshes
            foreach (var kvp in MeshCache)
            {
                SynVirtualMeshState state = kvp.Value;
                if (state.IsDirty)
                {
                    string suffix = state.AppliedPassTags.Count > 0 ? "_" + string.Join("_", state.AppliedPassTags) : "";
                    string cleanName = Regex.Replace(state.OriginalMesh.name, @"[^a-zA-Z0-9_]", "");
                    string meshHashKey = SynAssetCache.ComputeCompositeHash("StagedMesh", SynAssetCache.GetAssetIdentityHash(state.OriginalMesh), suffix);

                    if (SynAssetCache.TryGetCachedAsset<Mesh>(SynAssetCache.MeshesCategory, meshHashKey, out Mesh cachedMesh))
                    {
                        bakedMeshes[state.OriginalMesh] = cachedMesh;
                    }
                    else
                    {
                        Mesh newMesh = Object.Instantiate(state.OriginalMesh);
                        
                        // Set UV0 channel from cached staging data
                        if (state.CustomUV0Channels != null && state.CustomUV0Channels.Count == newMesh.vertexCount)
                        {
                            newMesh.SetUVs(0, state.CustomUV0Channels);
                        }

                        // Set UV2 channel from cached staging data
                        if (state.CustomUV2Channels != null && state.CustomUV2Channels.Count == newMesh.vertexCount)
                        {
                            newMesh.SetUVs(1, state.CustomUV2Channels);
                        }

                        // Set UV3 channel from cached staging data
                        if (state.CustomUV3Channels != null && state.CustomUV3Channels.Count == newMesh.vertexCount)
                        {
                            newMesh.SetUVs(3, state.CustomUV3Channels);
                        }

                        string fileName = string.Format("{0}_SynBaked{1}", cleanName, suffix);
                        Mesh savedMesh = SynAssetCache.SaveCachedAsset(newMesh, SynAssetCache.MeshesCategory, meshHashKey, fileName);
                        bakedMeshes[state.OriginalMesh] = savedMesh;
                        generatedAssetCount++;
                    }
                }
            }

            // 2. Bake dirty materials
            foreach (var kvp in MaterialCache)
            {
                SynVirtualMaterialState state = kvp.Value;
                if (state.IsDirty)
                {
                    string suffix = state.AppliedPassTags.Count > 0 ? "_" + string.Join("_", state.AppliedPassTags) : "";
                    string cleanName = Regex.Replace(state.OriginalMaterial.name, @"[^a-zA-Z0-9_]", "");
                    string matHashKey = SynAssetCache.ComputeMaterialHash(state.OriginalMaterial, suffix, state.TrackedFloats, state.TrackedVectors, state.TrackedTextures, state.TrackedKeywords);

                    if (SynAssetCache.TryGetCachedAsset<Material>(SynAssetCache.MaterialsCategory, matHashKey, out Material cachedMat))
                    {
                        bakedMaterials[state.OriginalMaterial] = cachedMat;
                    }
                    else
                    {
                        Material newMat = Object.Instantiate(state.OriginalMaterial);
                        
                        // Swap shader if target shader is different
                        if (state.TargetShader != state.OriginalShader)
                        {
                            newMat.shader = state.TargetShader;
                        }

                        bool forceInstancing = SynSceneOptimizerSettings.GetBool("ForceGPUInstancingOnBaked", true);
                        newMat.enableInstancing = forceInstancing ? true : state.IsGPUInstanced;

                        // Apply active properties
                        foreach (var texKvp in state.TrackedTextures)
                        {
                            if (newMat.HasProperty(texKvp.Key))
                            {
                                newMat.SetTexture(texKvp.Key, texKvp.Value);
                            }
                        }
                        foreach (var vecKvp in state.TrackedVectors)
                        {
                            if (newMat.HasProperty(vecKvp.Key))
                            {
                                newMat.SetVector(vecKvp.Key, vecKvp.Value);
                            }
                        }
                        foreach (var floatKvp in state.TrackedFloats)
                        {
                            if (newMat.HasProperty(floatKvp.Key))
                            {
                                newMat.SetFloat(floatKvp.Key, floatKvp.Value);
                            }
                        }

                        // Apply tracked keywords
                        newMat.shaderKeywords = new List<string>(state.TrackedKeywords).ToArray();

                        // Clean ghost texture references from the serialized data
                        var serializedNewMat = new SerializedObject(newMat);
                        var texEnvs = serializedNewMat.FindProperty("m_SavedProperties.m_TexEnvs");
                        if (texEnvs != null && texEnvs.isArray)
                        {
                            for (int i = texEnvs.arraySize - 1; i >= 0; i--)
                            {
                                var prop = texEnvs.GetArrayElementAtIndex(i);
                                var nameProp = prop.FindPropertyRelative("first");
                                if (nameProp != null)
                                {
                                    string texPropName = nameProp.stringValue;
                                    if (state.TrackedTextures.ContainsKey(texPropName) && state.TrackedTextures[texPropName] == null)
                                    {
                                        texEnvs.DeleteArrayElementAtIndex(i);
                                    }
                                }
                            }
                            serializedNewMat.ApplyModifiedProperties();
                        }

                        string fileName = string.Format("{0}_SynBaked{1}", cleanName, suffix);
                        Material savedMat = SynAssetCache.SaveCachedAsset(newMat, SynAssetCache.MaterialsCategory, matHashKey, fileName);
                        bakedMaterials[state.OriginalMaterial] = savedMat;
                        generatedAssetCount++;
                    }
                }
            }

            // 3. Re-link active scene renderers
            List<Renderer> renderers = SynSceneQuery.GetAllRenderers(scene);
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;

                // Protect screening check
                if (SynSceneQuery.IsVideoComponentDetected(r))
                {
                    continue;
                }

                // Swap materials
                Material[] sharedMats = r.sharedMaterials;
                bool matSwapped = false;
                for (int i = 0; i < sharedMats.Length; i++)
                {
                    // Do not overwrite slots already assigned to a palette or atlas material by earlier passes
                    if (sharedMats[i] != null && (sharedMats[i].name.Contains("Palette") || sharedMats[i].name.Contains("Atlas")))
                    {
                        continue;
                    }

                    if (sharedMats[i] != null && bakedMaterials.TryGetValue(sharedMats[i], out Material bakedMat))
                    {
                        sharedMats[i] = bakedMat;
                        matSwapped = true;
                    }
                }
                if (matSwapped)
                {
                    r.sharedMaterials = sharedMats;
                    EditorUtility.SetDirty(r);
                }

                // Swap meshes
                if (r is MeshRenderer)
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null && !mf.sharedMesh.name.Contains("Palettized") && !mf.sharedMesh.name.Contains("Atlas"))
                    {
                        if (bakedMeshes.TryGetValue(mf.sharedMesh, out Mesh bakedMesh))
                        {
                            mf.sharedMesh = bakedMesh;
                            EditorUtility.SetDirty(mf);
                        }
                    }
                }
                else if (r is SkinnedMeshRenderer)
                {
                    SkinnedMeshRenderer smr = (SkinnedMeshRenderer)r;
                    if (smr.sharedMesh != null && !smr.sharedMesh.name.Contains("Palettized") && !smr.sharedMesh.name.Contains("Atlas"))
                    {
                        if (bakedMeshes.TryGetValue(smr.sharedMesh, out Mesh bakedMesh))
                        {
                            smr.sharedMesh = bakedMesh;
                            EditorUtility.SetDirty(smr);
                        }
                    }
                }
            }

            // 4. Apply material property blocks (non-static targets)
            foreach (var kvp in RendererFloatAssignments)
            {
                Renderer r = kvp.Key;
                if (r == null) continue;

                SynDynamicPropertyBlock comp = r.gameObject.GetComponent<SynDynamicPropertyBlock>();
                if (comp == null)
                {
                    comp = r.gameObject.AddComponent<SynDynamicPropertyBlock>();
                }
                foreach (var assign in kvp.Value)
                {
                    comp.floats.RemoveAll(f => f.name == assign.Name);
                    comp.floats.Add(new SynDynamicPropertyBlock.FloatProp { name = assign.Name, value = assign.Value });
                }
                EditorUtility.SetDirty(comp);
                comp.Apply();
            }

            foreach (var kvp in RendererVectorAssignments)
            {
                Renderer r = kvp.Key;
                if (r == null) continue;

                SynDynamicPropertyBlock comp = r.gameObject.GetComponent<SynDynamicPropertyBlock>();
                if (comp == null)
                {
                    comp = r.gameObject.AddComponent<SynDynamicPropertyBlock>();
                }
                foreach (var assign in kvp.Value)
                {
                    comp.vectors.RemoveAll(v => v.name == assign.Name);
                    comp.vectors.Add(new SynDynamicPropertyBlock.VectorProp { name = assign.Name, value = assign.Value });
                }
                EditorUtility.SetDirty(comp);
                comp.Apply();
            }

            PipelineStopwatch.Stop();
            double seconds = PipelineStopwatch.Elapsed.TotalSeconds;

            // 5. Output compilation report
            string report = string.Format(
                "========================================================================\n" +
                "[SYN SCENE OPTIMIZER] COMPILATION REPORT\n" +
                "========================================================================\n" +
                "{0}\n" +
                "------------------------------------------------------------------------\n" +
                " ATOMIC BAKE: Generated [{1}] transient assets safely in {2:F2}s.\n" +
                "========================================================================",
                string.Join("\n", PipelineLogSummary),
                generatedAssetCount,
                seconds
            );

            bool showConsoleLogs = SynSceneOptimizerSettings.GetBool("EnableConsoleLogging", true);
            if (showConsoleLogs)
            {
                if (!string.IsNullOrEmpty(SessionState.GetString("SynOriginalActiveScene", "")) && !EditorApplication.isPlaying)
                {
                    SessionState.SetString("SynOptimizerReport", report);
                }
                else
                {
                    Debug.Log(report);
                }
            }
        }
    }
}
