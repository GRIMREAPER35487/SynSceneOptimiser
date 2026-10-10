using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimization pass that scans the scene to locate geometrically identical meshes (same vertex data, UVs, normals, indices),
    /// redirects all renderers to point to a single master mesh reference, and queues their materials for GPU Instancing.
    /// </summary>
    public class SynMeshDeduplicatorPass : SynOptimizationPass
    {
        public override string Id => "SynMeshDeduplicatorPass";
        public override string Name => "Mesh Deduplicator & Instancer";
        public override string Description => "Finds geometrically identical meshes in the scene, consolidates them to share a single master Mesh instance, and queues their materials for GPU instancing.";
        public override string Category => "Structural Cleanup";
        public override int Priority => 30;

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            bool dedupMeshes = SynSceneOptimizerSettings.GetBool("MeshDeduplicator_DeduplicateMeshes", true);
            bool newDedupMeshes = EditorGUILayout.Toggle(
                new GUIContent("Deduplicate Meshes", "Find geometrically identical meshes and consolidate them to point to a single master mesh reference."), 
                dedupMeshes
            );
            if (newDedupMeshes != dedupMeshes)
            {
                SynSceneOptimizerSettings.SetBool("MeshDeduplicator_DeduplicateMeshes", newDedupMeshes);
            }

            bool queueInstancing = SynSceneOptimizerSettings.GetBool("MeshDeduplicator_QueueInstancing", true);
            bool newQueueInstancing = EditorGUILayout.Toggle(
                new GUIContent("Queue GPU Instancing", "Queue materials used by the consolidated meshes to have GPU Instancing enabled in the staging system."), 
                queueInstancing
            );
            if (newQueueInstancing != queueInstancing)
            {
                SynSceneOptimizerSettings.SetBool("MeshDeduplicator_QueueInstancing", newQueueInstancing);
            }

            bool disableBatchingMeshes = SynSceneOptimizerSettings.GetBool("MeshDeduplicator_DisableBatchingOnMeshes", true);
            bool newDisableBatchingMeshes = EditorGUILayout.Toggle(
                new GUIContent("Disable Batching on Meshes", "Remove the Batching Static flag on GameObjects whose meshes are consolidated, letting GPU instancing work."), 
                disableBatchingMeshes
            );
            if (newDisableBatchingMeshes != disableBatchingMeshes)
            {
                SynSceneOptimizerSettings.SetBool("MeshDeduplicator_DisableBatchingOnMeshes", newDisableBatchingMeshes);
            }

            int minInstances = SynSceneOptimizerSettings.GetInt("MeshDeduplicator_MinInstances", 4);
            int newMinInstances = Mathf.Max(2, EditorGUILayout.IntField(
                new GUIContent("Min Instances To Unbatch", "Only renderers drawing the same mesh with the same materials can be GPU instanced. Static batching is only removed from such groups when they have at least this many renderers; everything else keeps Batching Static."),
                minInstances
            ));
            if (newMinInstances != minInstances)
            {
                SynSceneOptimizerSettings.SetInt("MeshDeduplicator_MinInstances", newMinInstances);
            }

            bool logConsolidation = SynSceneOptimizerSettings.GetBool("MeshDeduplicator_LogConsolidation", true);
            bool newLogConsolidation = EditorGUILayout.Toggle(
                new GUIContent("Log Consolidation Details", "Log details of which meshes were consolidated and how many renderers were updated."), 
                logConsolidation
            );
            if (newLogConsolidation != logConsolidation)
            {
                SynSceneOptimizerSettings.SetBool("MeshDeduplicator_LogConsolidation", newLogConsolidation);
            }
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool dedupEnabled = SynSceneOptimizerSettings.GetBool("MeshDeduplicator_DeduplicateMeshes", true);
            bool instancingEnabled = SynSceneOptimizerSettings.GetBool("MeshDeduplicator_QueueInstancing", true);
            bool disableBatchingOnMeshes = SynSceneOptimizerSettings.GetBool("MeshDeduplicator_DisableBatchingOnMeshes", true);
            int minInstances = Mathf.Max(2, SynSceneOptimizerSettings.GetInt("MeshDeduplicator_MinInstances", 4));
            bool logEnabled = SynSceneOptimizerSettings.GetBool("MeshDeduplicator_LogConsolidation", true);

            if (!dedupEnabled) return;

            var groups = new Dictionary<string, List<Mesh>>();
            var meshToRenderers = new Dictionary<Mesh, List<Renderer>>();

            // 1. Gather all active meshes and group by vertexCount & submeshCount
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (SynSceneQuery.IsVideoComponentDetected(r)) continue;
                if (IsEditorOnly(r.transform)) continue;

                Mesh mesh = GetMesh(r);
                if (mesh == null || mesh.vertexCount == 0) continue;
                if (SynProtectionData.IsProtected(mesh)) continue;

                bool hasProtectedMat = false;
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat != null && SynProtectionData.IsProtected(mat))
                    {
                        hasProtectedMat = true;
                        break;
                    }
                }
                if (hasProtectedMat) continue;

                if (!meshToRenderers.ContainsKey(mesh))
                {
                    meshToRenderers[mesh] = new List<Renderer>();

                    string key = $"{mesh.vertexCount}_{mesh.subMeshCount}";
                    if (!groups.ContainsKey(key))
                    {
                        groups[key] = new List<Mesh>();
                    }
                    groups[key].Add(mesh);
                }
                meshToRenderers[mesh].Add(r);
            }

            // 2. Perform multi-threaded geometric comparison inside each group
            var meshMap = new ConcurrentDictionary<Mesh, Mesh>();
            int duplicateMeshCount = 0;

            // Capture snapshots on the main thread for candidate meshes in multi-item groups
            var candidateGroups = new List<List<Mesh>>();
            var snapshotMap = new Dictionary<Mesh, MeshGeometrySnapshot>();

            foreach (var kvp in groups)
            {
                if (kvp.Value.Count > 1)
                {
                    candidateGroups.Add(kvp.Value);
                    foreach (var mesh in kvp.Value)
                    {
                        if (!snapshotMap.ContainsKey(mesh))
                        {
                            snapshotMap[mesh] = MeshGeometrySnapshot.Capture(mesh);
                        }
                    }
                }
            }

            if (candidateGroups.Count == 0)
            {
                return;
            }

            // Execute parallel geometric comparison across all CPU threads
            Parallel.ForEach(candidateGroups, meshesInGroup =>
            {
                var masterSnapshots = new List<MeshGeometrySnapshot>();

                foreach (Mesh mesh in meshesInGroup)
                {
                    MeshGeometrySnapshot snap = snapshotMap[mesh];
                    MeshGeometrySnapshot foundMaster = null;

                    foreach (MeshGeometrySnapshot master in masterSnapshots)
                    {
                        if (snap.IsIdenticalTo(master))
                        {
                            foundMaster = master;
                            break;
                        }
                    }

                    if (foundMaster != null)
                    {
                        meshMap[snap.UnityMesh] = foundMaster.UnityMesh;
                        System.Threading.Interlocked.Increment(ref duplicateMeshCount);
                    }
                    else
                    {
                        masterSnapshots.Add(snap);
                        meshMap[snap.UnityMesh] = snap.UnityMesh;
                    }
                }
            });

            if (duplicateMeshCount == 0)
            {
                return;
            }

            // 3. Redirect duplicate meshes to their master mesh
            int totalRenderersUpdated = 0;
            var consolidatedFamilies = new Dictionary<Mesh, List<Renderer>>(); // master mesh -> every renderer now using it

            foreach (var kvp in meshMap)
            {
                Mesh duplicateMesh = kvp.Key;
                Mesh masterMesh = kvp.Value;
                if (duplicateMesh == masterMesh) continue;

                if (!consolidatedFamilies.TryGetValue(masterMesh, out var family))
                {
                    family = new List<Renderer>();
                    if (meshToRenderers.TryGetValue(masterMesh, out var masterRenderers)) family.AddRange(masterRenderers);
                    consolidatedFamilies[masterMesh] = family;
                }

                if (!meshToRenderers.TryGetValue(duplicateMesh, out var affectedRenderers)) continue;
                foreach (Renderer r in affectedRenderers)
                {
                    if (r is MeshRenderer)
                    {
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        if (mf != null)
                        {
                            SynPipelineCompactor.RecordMeshReplacement(r, mf.sharedMesh);
                            SynPipelineCompactor.RetargetMeshColliders(r, mf.sharedMesh, masterMesh);
                            mf.sharedMesh = masterMesh;
                            EditorUtility.SetDirty(mf);
                            totalRenderersUpdated++;
                        }
                    }
                    else if (r is SkinnedMeshRenderer smr)
                    {
                        SynPipelineCompactor.RecordMeshReplacement(r, smr.sharedMesh);
                        SynPipelineCompactor.RetargetMeshColliders(r, smr.sharedMesh, masterMesh);
                        smr.sharedMesh = masterMesh;
                        EditorUtility.SetDirty(smr);
                        totalRenderersUpdated++;
                    }
                    family.Add(r);
                }
            }

            // 4. GPU instancing only merges renderers that draw the same mesh with the same material list.
            //    Static batching is traded away only for such groups, and only when they are large enough;
            //    every other renderer keeps Batching Static so draw calls never go up.
            var instanceGroups = new Dictionary<string, List<Renderer>>();
            foreach (var family in consolidatedFamilies)
            {
                foreach (Renderer r in family.Value)
                {
                    if (r == null) continue;
                    var key = new System.Text.StringBuilder().Append(family.Key.GetInstanceID());
                    foreach (Material mat in r.sharedMaterials)
                    {
                        key.Append(':').Append(mat != null ? mat.GetInstanceID() : 0);
                    }
                    string groupKey = key.ToString();
                    if (!instanceGroups.TryGetValue(groupKey, out var group))
                    {
                        group = new List<Renderer>();
                        instanceGroups[groupKey] = group;
                    }
                    group.Add(r);
                }
            }

            HashSet<Material> materialsToQueue = new HashSet<Material>();
            int unbatchedCount = 0;
            foreach (var group in instanceGroups.Values)
            {
                if (group.Count < minInstances) continue;

                foreach (Renderer r in group)
                {
                    // Per-renderer MaterialPropertyBlocks (e.g. Bakery RNM/SH lighting data) prevent instancing,
                    // so those renderers keep static batching
                    if (r.HasPropertyBlock()) continue;

                    bool willInstance = instancingEnabled || AllMaterialsInstanced(r);
                    if (!willInstance) continue;

                    if (instancingEnabled)
                    {
                        foreach (Material mat in r.sharedMaterials)
                        {
                            if (mat != null) materialsToQueue.Add(mat);
                        }
                    }

                    if (disableBatchingOnMeshes && ShouldDisableBatching(r))
                    {
                        StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                        if ((flags & StaticEditorFlags.BatchingStatic) != 0)
                        {
                            GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags & ~StaticEditorFlags.BatchingStatic);
                            unbatchedCount++;
                        }
                    }
                }
            }

            int instancingCount = 0;
            foreach (Material mat in materialsToQueue)
            {
                var matState = SynPipelineCompactor.GetStagingState(mat);
                if (matState != null && !matState.IsGPUInstanced)
                {
                    matState.IsGPUInstanced = true;
                    matState.IsDirty = true;
                    if (!matState.AppliedPassTags.Contains("Inst"))
                    {
                        matState.AppliedPassTags.Add("Inst");
                    }
                    instancingCount++;
                }
            }

            // 5. Log consolidation result
            string logMsg = string.Format("Consolidated {0} duplicate meshes across {1} renderers. Queued {2} materials for GPU Instancing and moved {3} renderers from static batching to instancing.",
                duplicateMeshCount, totalRenderersUpdated, instancingCount, unbatchedCount);

            SynPipelineCompactor.LogChange("Mesh Deduplicator", logMsg);

            if (logEnabled)
            {
                Debug.Log($"[SYN SCENE OPTIMIZER] Mesh Deduplicator: {logMsg}");
            }
        }

        private Mesh GetMesh(Renderer r)
        {
            if (r is MeshRenderer)
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null) return mf.sharedMesh;
            }
            else if (r is SkinnedMeshRenderer)
            {
                SkinnedMeshRenderer smr = (SkinnedMeshRenderer)r;
                return smr.sharedMesh;
            }
            return null;
        }

        private static bool AllMaterialsInstanced(Renderer r)
        {
            foreach (Material mat in r.sharedMaterials)
            {
                if (mat != null && !mat.enableInstancing) return false;
            }
            return true;
        }

        private bool ShouldDisableBatching(Renderer r)
        {
            Mesh m = GetMesh(r);
            if (m == null) return false;
            // Protect low-poly meshes (under 1000 vertices) from losing static batching
            if (m.vertexCount < 1000) return false;
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

        private bool AreMeshesIdentical(Mesh a, Mesh b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (a.vertexCount != b.vertexCount) return false;
            if (a.subMeshCount != b.subMeshCount) return false;

            // 1. Compare positions
            Vector3[] verticesA = a.vertices;
            Vector3[] verticesB = b.vertices;
            if (verticesA.Length != verticesB.Length) return false;
            for (int i = 0; i < verticesA.Length; i++)
            {
                if (verticesA[i] != verticesB[i]) return false;
            }

            // 2. Compare normals
            Vector3[] normalsA = a.normals;
            Vector3[] normalsB = b.normals;
            if (normalsA.Length != normalsB.Length) return false;
            if (normalsA.Length > 0)
            {
                for (int i = 0; i < normalsA.Length; i++)
                {
                    if (normalsA[i] != normalsB[i]) return false;
                }
            }

            // 3. Compare tangents
            Vector4[] tangentsA = a.tangents;
            Vector4[] tangentsB = b.tangents;
            if (tangentsA.Length != tangentsB.Length) return false;
            if (tangentsA.Length > 0)
            {
                for (int i = 0; i < tangentsA.Length; i++)
                {
                    if (tangentsA[i] != tangentsB[i]) return false;
                }
            }

            // 4. Compare UVs (channels 0 to 3)
            for (int channel = 0; channel < 4; channel++)
            {
                List<Vector4> uvsA = new List<Vector4>();
                List<Vector4> uvsB = new List<Vector4>();
                a.GetUVs(channel, uvsA);
                b.GetUVs(channel, uvsB);
                if (uvsA.Count != uvsB.Count) return false;
                for (int i = 0; i < uvsA.Count; i++)
                {
                    if (uvsA[i] != uvsB[i]) return false;
                }
            }

            // 5. Compare Colors
            Color32[] colorsA = a.colors32;
            Color32[] colorsB = b.colors32;
            if (colorsA.Length != colorsB.Length) return false;
            if (colorsA.Length > 0)
            {
                for (int i = 0; i < colorsA.Length; i++)
                {
                    if (colorsA[i].r != colorsB[i].r ||
                        colorsA[i].g != colorsB[i].g ||
                        colorsA[i].b != colorsB[i].b ||
                        colorsA[i].a != colorsB[i].a) return false;
                }
            }

            // 6. Compare submesh topologies and indices
            for (int i = 0; i < a.subMeshCount; i++)
            {
                if (a.GetTopology(i) != b.GetTopology(i)) return false;

                int[] indicesA = a.GetIndices(i);
                int[] indicesB = b.GetIndices(i);
                if (indicesA.Length != indicesB.Length) return false;
                for (int j = 0; j < indicesA.Length; j++)
                {
                    if (indicesA[j] != indicesB[j]) return false;
                }
            }

            // 7. Compare bone weights
            BoneWeight[] bwA = a.boneWeights;
            BoneWeight[] bwB = b.boneWeights;
            if (bwA.Length != bwB.Length) return false;
            if (bwA.Length > 0)
            {
                for (int i = 0; i < bwA.Length; i++)
                {
                    if (bwA[i].boneIndex0 != bwB[i].boneIndex0 ||
                        bwA[i].boneIndex1 != bwB[i].boneIndex1 ||
                        bwA[i].boneIndex2 != bwB[i].boneIndex2 ||
                        bwA[i].boneIndex3 != bwB[i].boneIndex3 ||
                        Mathf.Abs(bwA[i].weight0 - bwB[i].weight0) > 0.0001f ||
                        Mathf.Abs(bwA[i].weight1 - bwB[i].weight1) > 0.0001f ||
                        Mathf.Abs(bwA[i].weight2 - bwB[i].weight2) > 0.0001f ||
                        Mathf.Abs(bwA[i].weight3 - bwB[i].weight3) > 0.0001f) return false;
                }
            }

            // 8. Compare bind poses
            Matrix4x4[] bpA = a.bindposes;
            Matrix4x4[] bpB = b.bindposes;
            if (bpA.Length != bpB.Length) return false;
            if (bpA.Length > 0)
            {
                for (int i = 0; i < bpA.Length; i++)
                {
                    if (bpA[i] != bpB[i]) return false;
                }
            }

            return true;
        }

        private class MeshGeometrySnapshot
        {
            public Mesh UnityMesh;
            public int VertexCount;
            public int SubMeshCount;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector4[] Tangents;
            public List<Vector4>[] UVs;
            public Color32[] Colors32;
            public MeshTopology[] Topologies;
            public int[][] Indices;
            public BoneWeight[] BoneWeights;
            public Matrix4x4[] BindPoses;
            public BlendShapeSnapshot[] BlendShapes;
            public ulong FastChecksum;

            private const int UVChannelCount = 8;

            public class BlendShapeSnapshot
            {
                public string Name;
                public float[] FrameWeights;
                public Vector3[][] DeltaVertices;
                public Vector3[][] DeltaNormals;
                public Vector3[][] DeltaTangents;
            }

            public static MeshGeometrySnapshot Capture(Mesh mesh)
            {
                var snap = new MeshGeometrySnapshot
                {
                    UnityMesh = mesh,
                    VertexCount = mesh.vertexCount,
                    SubMeshCount = mesh.subMeshCount,
                    Vertices = mesh.vertices,
                    Normals = mesh.normals,
                    Tangents = mesh.tangents,
                    Colors32 = mesh.colors32,
                    BoneWeights = mesh.boneWeights,
                    BindPoses = mesh.bindposes,
                    Topologies = new MeshTopology[mesh.subMeshCount],
                    Indices = new int[mesh.subMeshCount][],
                    UVs = new List<Vector4>[UVChannelCount],
                    BlendShapes = new BlendShapeSnapshot[mesh.blendShapeCount]
                };

                for (int i = 0; i < UVChannelCount; i++)
                {
                    snap.UVs[i] = new List<Vector4>();
                    mesh.GetUVs(i, snap.UVs[i]);
                }

                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    int frameCount = mesh.GetBlendShapeFrameCount(s);
                    var shape = new BlendShapeSnapshot
                    {
                        Name = mesh.GetBlendShapeName(s),
                        FrameWeights = new float[frameCount],
                        DeltaVertices = new Vector3[frameCount][],
                        DeltaNormals = new Vector3[frameCount][],
                        DeltaTangents = new Vector3[frameCount][]
                    };
                    for (int f = 0; f < frameCount; f++)
                    {
                        shape.FrameWeights[f] = mesh.GetBlendShapeFrameWeight(s, f);
                        shape.DeltaVertices[f] = new Vector3[mesh.vertexCount];
                        shape.DeltaNormals[f] = new Vector3[mesh.vertexCount];
                        shape.DeltaTangents[f] = new Vector3[mesh.vertexCount];
                        mesh.GetBlendShapeFrameVertices(s, f, shape.DeltaVertices[f], shape.DeltaNormals[f], shape.DeltaTangents[f]);
                    }
                    snap.BlendShapes[s] = shape;
                }

                ulong checksum = 17;
                checksum = checksum * 31 + (ulong)snap.VertexCount;
                checksum = checksum * 31 + (ulong)snap.SubMeshCount;
                checksum = checksum * 31 + (ulong)snap.BlendShapes.Length;

                for (int i = 0; i < snap.SubMeshCount; i++)
                {
                    snap.Topologies[i] = mesh.GetTopology(i);
                    snap.Indices[i] = mesh.GetIndices(i);
                    checksum = checksum * 31 + (ulong)snap.Indices[i].Length;
                }

                if (snap.Vertices.Length > 0)
                {
                    checksum = checksum * 31 + (ulong)snap.Vertices[0].GetHashCode();
                    checksum = checksum * 31 + (ulong)snap.Vertices[snap.Vertices.Length - 1].GetHashCode();
                    checksum = checksum * 31 + (ulong)snap.Vertices[snap.Vertices.Length / 2].GetHashCode();
                }

                snap.FastChecksum = checksum;
                return snap;
            }

            public bool IsIdenticalTo(MeshGeometrySnapshot other)
            {
                if (ReferenceEquals(this, other)) return true;
                if (FastChecksum != other.FastChecksum) return false;
                if (VertexCount != other.VertexCount || SubMeshCount != other.SubMeshCount) return false;

                // 1. Compare positions
                for (int i = 0; i < Vertices.Length; i++)
                {
                    if (Vertices[i] != other.Vertices[i]) return false;
                }

                // 2. Compare normals
                if ((Normals == null) != (other.Normals == null)) return false;
                if (Normals != null)
                {
                    if (Normals.Length != other.Normals.Length) return false;
                    for (int i = 0; i < Normals.Length; i++)
                    {
                        if (Normals[i] != other.Normals[i]) return false;
                    }
                }

                // 3. Compare tangents
                if ((Tangents == null) != (other.Tangents == null)) return false;
                if (Tangents != null)
                {
                    if (Tangents.Length != other.Tangents.Length) return false;
                    for (int i = 0; i < Tangents.Length; i++)
                    {
                        if (Tangents[i] != other.Tangents[i]) return false;
                    }
                }

                // 4. Compare UVs (all channels)
                for (int channel = 0; channel < UVChannelCount; channel++)
                {
                    var uvsA = UVs[channel];
                    var uvsB = other.UVs[channel];
                    if (uvsA.Count != uvsB.Count) return false;
                    for (int i = 0; i < uvsA.Count; i++)
                    {
                        if (uvsA[i] != uvsB[i]) return false;
                    }
                }

                // 5. Compare Colors
                if (Colors32.Length != other.Colors32.Length) return false;
                for (int i = 0; i < Colors32.Length; i++)
                {
                    if (Colors32[i].r != other.Colors32[i].r ||
                        Colors32[i].g != other.Colors32[i].g ||
                        Colors32[i].b != other.Colors32[i].b ||
                        Colors32[i].a != other.Colors32[i].a) return false;
                }

                // 6. Compare submesh topologies and indices
                for (int i = 0; i < SubMeshCount; i++)
                {
                    if (Topologies[i] != other.Topologies[i]) return false;
                    if (Indices[i].Length != other.Indices[i].Length) return false;
                    for (int j = 0; j < Indices[i].Length; j++)
                    {
                        if (Indices[i][j] != other.Indices[i][j]) return false;
                    }
                }

                // 7. Compare bone weights
                if (BoneWeights.Length != other.BoneWeights.Length) return false;
                for (int i = 0; i < BoneWeights.Length; i++)
                {
                    if (BoneWeights[i].boneIndex0 != other.BoneWeights[i].boneIndex0 ||
                        BoneWeights[i].boneIndex1 != other.BoneWeights[i].boneIndex1 ||
                        BoneWeights[i].boneIndex2 != other.BoneWeights[i].boneIndex2 ||
                        BoneWeights[i].boneIndex3 != other.BoneWeights[i].boneIndex3 ||
                        Mathf.Abs(BoneWeights[i].weight0 - other.BoneWeights[i].weight0) > 0.0001f ||
                        Mathf.Abs(BoneWeights[i].weight1 - other.BoneWeights[i].weight1) > 0.0001f ||
                        Mathf.Abs(BoneWeights[i].weight2 - other.BoneWeights[i].weight2) > 0.0001f ||
                        Mathf.Abs(BoneWeights[i].weight3 - other.BoneWeights[i].weight3) > 0.0001f) return false;
                }

                // 8. Compare bind poses
                if (BindPoses.Length != other.BindPoses.Length) return false;
                for (int i = 0; i < BindPoses.Length; i++)
                {
                    if (BindPoses[i] != other.BindPoses[i]) return false;
                }

                // 9. Compare blend shapes (names drive animator/Udon bindings, deltas drive the result)
                if (BlendShapes.Length != other.BlendShapes.Length) return false;
                for (int s = 0; s < BlendShapes.Length; s++)
                {
                    var a = BlendShapes[s];
                    var b = other.BlendShapes[s];
                    if (a.Name != b.Name || a.FrameWeights.Length != b.FrameWeights.Length) return false;
                    for (int f = 0; f < a.FrameWeights.Length; f++)
                    {
                        if (a.FrameWeights[f] != b.FrameWeights[f]) return false;
                        if (!SequenceEqual(a.DeltaVertices[f], b.DeltaVertices[f])) return false;
                        if (!SequenceEqual(a.DeltaNormals[f], b.DeltaNormals[f])) return false;
                        if (!SequenceEqual(a.DeltaTangents[f], b.DeltaTangents[f])) return false;
                    }
                }

                return true;
            }

            private static bool SequenceEqual(Vector3[] a, Vector3[] b)
            {
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i]) return false;
                }
                return true;
            }
        }
    }
}
