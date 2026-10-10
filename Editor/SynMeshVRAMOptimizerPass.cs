using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    public class SynMeshVRAMOptimizerPass : SynOptimizationPass
    {
        public override string Id => "SynMeshVRAMOptimizerPass";
        public override string Name => "Mesh Memory Optimizer";
        public override string Description => "Shrinks mesh VRAM by converting UV3 slice storage to compact Vector2 format, stripping unused vertex channels, and purging redundant mesh clones.";
        public override string Category => "Memory & Assets";
        public override int Priority => 1400; // Runs right after SynVRAMOptimizerPass (1300)

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Optimizes mesh geometry memory by converting UV3 slice storage to 2-component Vector2 (saving 50% UV3 VRAM), stripping unused vertex channels (tangents/colors), and cleaning up intermediate mesh clones.", MessageType.Info);

            bool compactUV3 = SynSceneOptimizerSettings.GetBool("MeshOpt_CompactUV3", true);
            bool newCompactUV3 = EditorGUILayout.Toggle(new GUIContent("Compact UV3 Slice Format (Vector2)", "Stores Texture Array slice indices in 2-component Vector2 instead of 4-component Vector4, cutting UV3 memory in half."), compactUV3);
            if (newCompactUV3 != compactUV3) SynSceneOptimizerSettings.SetBool("MeshOpt_CompactUV3", newCompactUV3);

            bool stripUnused = SynSceneOptimizerSettings.GetBool("MeshOpt_StripUnusedChannels", true);
            bool newStripUnused = EditorGUILayout.Toggle(new GUIContent("Strip Unused Vertex Channels", "Strips empty tangents, vertex colors, and unused UV channels from scene meshes."), stripUnused);
            if (newStripUnused != stripUnused) SynSceneOptimizerSettings.SetBool("MeshOpt_StripUnusedChannels", newStripUnused);

            bool purgeClones = SynSceneOptimizerSettings.GetBool("MeshOpt_PurgeIntermediateClones", true);
            bool newPurgeClones = EditorGUILayout.Toggle(new GUIContent("Purge Intermediate Mesh Clones", "Cleans up unreferenced intermediate mesh clones after optimization completes to free Editor memory."), purgeClones);
            if (newPurgeClones != purgeClones) SynSceneOptimizerSettings.SetBool("MeshOpt_PurgeIntermediateClones", newPurgeClones);
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool compactUV3 = SynSceneOptimizerSettings.GetBool("MeshOpt_CompactUV3", true);
            bool stripUnused = SynSceneOptimizerSettings.GetBool("MeshOpt_StripUnusedChannels", true);
            bool purgeClones = SynSceneOptimizerSettings.GetBool("MeshOpt_PurgeIntermediateClones", true);

            int meshesOptimized = 0;
            int clonesCreated = 0;
            var meshToRenderers = new Dictionary<Mesh, List<Renderer>>();

            foreach (Renderer r in renderers)
            {
                if (r == null || IsEditorOnly(r.transform) || SynProtectionData.IsProtected(r.gameObject)) continue;

                Mesh mesh = GetMesh(r);
                if (mesh == null || mesh.vertexCount == 0 || SynProtectionData.IsProtected(mesh)) continue;

                if (!meshToRenderers.ContainsKey(mesh))
                {
                    meshToRenderers[mesh] = new List<Renderer>();
                }
                meshToRenderers[mesh].Add(r);
            }

            foreach (var kvp in meshToRenderers)
            {
                Mesh originalMesh = kvp.Key;
                List<Renderer> affectedRenderers = kvp.Value;

                // Only compact UV3 when it is stored with 3-4 components but z/w carry no data, so
                // shaders reading uv3.zw (slice indices, custom data) never lose information
                bool needsCompactUV3 = false;
                if (compactUV3 && originalMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord3)
                    && originalMesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord3) > 2)
                {
                    var testUvs = new List<Vector4>();
                    originalMesh.GetUVs(3, testUvs);
                    needsCompactUV3 = testUvs.Count > 0 && AreZWComponentsZero(testUvs);
                }

                bool needsStripTangents = stripUnused && originalMesh.tangents != null && originalMesh.tangents.Length > 0 && IsAllZero(originalMesh.tangents);
                bool needsStripColors = stripUnused && originalMesh.colors != null && originalMesh.colors.Length > 0 && IsAllDefaultColor(originalMesh.colors);

                if (!needsCompactUV3 && !needsStripTangents && !needsStripColors)
                {
                    continue;
                }

                string meshHash = SynAssetCache.ComputeCompositeHash(
                    "MeshMemoryOpt_v3",
                    SynAssetCache.GetAssetIdentityHash(originalMesh),
                    needsCompactUV3.ToString(),
                    needsStripTangents.ToString(),
                    needsStripColors.ToString()
                );

                if (!SynAssetCache.TryGetCachedAsset<Mesh>(SynAssetCache.MeshesCategory, meshHash, out Mesh targetMesh))
                {
                    Mesh clonedMesh = UnityEngine.Object.Instantiate(originalMesh);
                    clonedMesh.name = $"{originalMesh.name}_MemOpt";

                    if (needsCompactUV3)
                    {
                        var uvs4 = new List<Vector4>();
                        clonedMesh.GetUVs(3, uvs4);
                        if (uvs4.Count > 0)
                        {
                            var uvs2 = new List<Vector2>(uvs4.Count);
                            for (int i = 0; i < uvs4.Count; i++)
                            {
                                uvs2.Add(new Vector2(uvs4[i].x, uvs4[i].y));
                            }
                            clonedMesh.SetUVs(3, uvs2);
                        }
                    }

                    if (needsStripTangents)
                    {
                        clonedMesh.tangents = null;
                    }

                    if (needsStripColors)
                    {
                        clonedMesh.colors = null;
                    }

                    targetMesh = SynAssetCache.SaveCachedAsset(clonedMesh, SynAssetCache.MeshesCategory, meshHash, $"{originalMesh.name}_MemOpt");
                    clonesCreated++;
                }

                if (targetMesh != null)
                {
                    foreach (var r in affectedRenderers)
                    {
                        if (r != null)
                        {
                            SetMesh(r, targetMesh);
                        }
                    }
                    meshesOptimized++;
                }
            }

            // 3. Purge unreferenced intermediate mesh clones
            // UnloadUnusedAssetsImmediate takes seconds in large projects, so only pay for it when clones were made
            if (purgeClones && clonesCreated > 0)
            {
                EditorUtility.UnloadUnusedAssetsImmediate();
            }

            if (meshesOptimized > 0)
            {
                Debug.Log($"[SynSceneOptimizer] Mesh Memory Optimizer: Compacted UV3 and stripped unused vertex attributes across {meshesOptimized} scene meshes.");
                SynPipelineCompactor.LogChange(
                    "Mesh Memory Optimizer",
                    string.Format("Compacted UV3 and stripped unused vertex channels across {0} scene meshes.", meshesOptimized)
                );
            }
        }

        private Mesh GetMesh(Renderer r)
        {
            if (r is MeshRenderer mr)
            {
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            else if (r is SkinnedMeshRenderer smr)
            {
                return smr.sharedMesh;
            }
            return null;
        }

        private void SetMesh(Renderer r, Mesh mesh)
        {
            if (r is MeshRenderer mr)
            {
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf != null)
                {
                    SynPipelineCompactor.RecordMeshReplacement(r, mf.sharedMesh);
                    SynPipelineCompactor.RetargetMeshColliders(r, mf.sharedMesh, mesh);
                    mf.sharedMesh = mesh;
                    EditorUtility.SetDirty(mf);
                }
            }
            else if (r is SkinnedMeshRenderer smr)
            {
                SynPipelineCompactor.RecordMeshReplacement(r, smr.sharedMesh);
                SynPipelineCompactor.RetargetMeshColliders(r, smr.sharedMesh, mesh);
                smr.sharedMesh = mesh;
                EditorUtility.SetDirty(smr);
            }
        }

        private bool IsAllZero(Vector4[] vectors)
        {
            for (int i = 0; i < vectors.Length; i++)
            {
                if (vectors[i] != Vector4.zero) return false;
            }
            return true;
        }

        private bool AreZWComponentsZero(List<Vector4> uvs)
        {
            for (int i = 0; i < uvs.Count; i++)
            {
                if (uvs[i].z != 0f || uvs[i].w != 0f) return false;
            }
            return true;
        }

        private bool IsAllDefaultColor(Color[] colors)
        {
            if (colors == null || colors.Length == 0) return true;
            // Only uniform white matches what shaders read when the channel is absent. Uniform black or
            // clear is often an intentional mask (emission/AO/wind) and must be kept.
            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] != Color.white) return false;
            }
            return true;
        }

        private bool IsEditorOnly(Transform t)
        {
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }
    }
}
