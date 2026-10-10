using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;
using System.Collections.Generic;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
[System.Serializable]
    public class SynPersistentObjectReference
    {
        public UnityEngine.Object directAsset;
        public string globalObjectId;
        public string scenePath;
        public string name;

        [System.NonSerialized]
        private UnityEngine.Object _resolvedObj;

        // Per-pipeline-run resolution against the scene being optimized (see SynProtectionData.BeginRun)
        [System.NonSerialized]
        private UnityEngine.Object _runResolvedObj;
        [System.NonSerialized]
        private int _runResolvedId = -1;

        public SynPersistentObjectReference() { }

        public SynPersistentObjectReference(UnityEngine.Object obj)
        {
            Set(obj);
        }

        public void Set(UnityEngine.Object obj)
        {
            _resolvedObj = obj;
            if (obj == null)
            {
                directAsset = null;
                globalObjectId = "";
                scenePath = "";
                name = "";
                return;
            }

            name = obj.name;
            if (EditorUtility.IsPersistent(obj))
            {
                directAsset = obj;
                globalObjectId = "";
                scenePath = "";
            }
            else if (obj is GameObject go)
            {
                directAsset = null;
                scenePath = GetHierarchyPath(go.transform);
                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
            }
            else if (obj is Component comp)
            {
                directAsset = null;
                scenePath = GetHierarchyPath(comp.transform);
                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(comp.gameObject).ToString();
            }
        }

        public UnityEngine.Object Resolve()
        {
            // During a pipeline run, resolve inside the scene being optimized. The editor-scene object cached in
            // _resolvedObj is a different instance from the build/play copy and would never match it.
            if (SynProtectionData.HasActiveRun)
            {
                if (directAsset != null) return directAsset;
                if (_runResolvedId != SynProtectionData.ActiveRunId)
                {
                    _runResolvedId = SynProtectionData.ActiveRunId;
                    _runResolvedObj = ResolveInScene(SynProtectionData.ActiveRunScene);
                }
                return _runResolvedObj;
            }

            if (_resolvedObj != null) return _resolvedObj;

            // 1. If direct asset (Mesh, Material, Texture, Prefab)
            if (directAsset != null)
            {
                _resolvedObj = directAsset;
                return directAsset;
            }

            // 2. If Scene Object via GlobalObjectId
            if (!string.IsNullOrEmpty(globalObjectId))
            {
                if (GlobalObjectId.TryParse(globalObjectId, out GlobalObjectId gid))
                {
                    var o = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                    if (o != null)
                    {
                        _resolvedObj = o;
                        return o;
                    }
                }
            }

            // 3. Fallback: Search hierarchy by path (finds active and inactive objects)
            if (!string.IsNullOrEmpty(scenePath))
            {
                var go = SynSceneQuery.FindGameObjectByPath(scenePath);
                if (go != null)
                {
                    _resolvedObj = go;
                    return go;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a scene reference inside a specific scene: by GlobalObjectId when it points into that scene,
        /// otherwise by hierarchy path.
        /// </summary>
        public GameObject ResolveInScene(Scene scene)
        {
            if (!string.IsNullOrEmpty(globalObjectId) && GlobalObjectId.TryParse(globalObjectId, out GlobalObjectId gid))
            {
                var o = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                GameObject go = o as GameObject ?? (o as Component)?.gameObject;
                if (go != null && go.scene == scene) return go;
            }

            return string.IsNullOrEmpty(scenePath) ? null : SynSceneQuery.FindGameObjectByPath(scene, scenePath);
        }

        internal static string GetHierarchyPath(Transform t)
        {
            if (t == null) return "";
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }

    public class SynProtectionData : ScriptableObject
    {
        // Persistent Scene GameObject references
        public List<SynPersistentObjectReference> protectedSceneObjects = new List<SynPersistentObjectReference>();
        
        // Legacy list for backwards compatibility
        public List<GameObject> protectedGameObjects = new List<GameObject>();
        
        public List<Material> protectedMaterials = new List<Material>();
        public List<Mesh> protectedMeshes = new List<Mesh>();
        public List<Texture2D> protectedTextures = new List<Texture2D>();
        public List<string> protectedTextureNamePatterns = new List<string>();

        // Persistent lists for Mesh Simplifier (Preservation and Skip lists)
        public List<SynPersistentObjectReference> simplifierPreserveList = new List<SynPersistentObjectReference>();
        public List<SynPersistentObjectReference> simplifierSkipList = new List<SynPersistentObjectReference>();

        private static SynProtectionData instance;
        // Must match SynLegacyMigration.TargetSettingsPath; "Assets/SynSceneOptimiser" is a legacy folder
        private const string DefaultAssetPath = "Assets/SynSceneOptimizer/SynProtectionSettings.asset";

        public static SynProtectionData GetInstance()
        {
            if (instance == null)
            {
                // 1. Search for an existing SynProtectionData asset. Only copies under Assets/ are used:
                //    anything under Packages/ is read-only or replaced on package update.
                string projectPath = null;
                string packagePath = null;
                foreach (string guid in AssetDatabase.FindAssets("t:SynProtectionData"))
                {
                    string foundPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (foundPath.StartsWith("Assets/"))
                    {
                        projectPath = foundPath;
                        break;
                    }
                    if (packagePath == null) packagePath = foundPath;
                }

                if (projectPath != null)
                {
                    instance = AssetDatabase.LoadAssetAtPath<SynProtectionData>(projectPath);
                }

                // 2. If not found in Assets, create one at the default path. Settings that older versions
                //    stored inside the package folder are carried over so user edits are not lost.
                if (instance == null)
                {
                    EnsureAssetFolder(Path.GetDirectoryName(DefaultAssetPath).Replace('\\', '/'));

                    if (packagePath != null && AssetDatabase.CopyAsset(packagePath, DefaultAssetPath))
                    {
                        Debug.Log($"[SYN SCENE OPTIMIZER] Moved protection settings out of the package folder: '{packagePath}' -> '{DefaultAssetPath}'.");
                        instance = AssetDatabase.LoadAssetAtPath<SynProtectionData>(DefaultAssetPath);
                    }

                    if (instance == null)
                    {
                        instance = ScriptableObject.CreateInstance<SynProtectionData>();
                        AssetDatabase.CreateAsset(instance, DefaultAssetPath);
                        AssetDatabase.SaveAssetIfDirty(instance);
                    }
                }
            }
            return instance;
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        // ---- Pipeline run scope: protected objects resolved once against the scene being optimized ----
        private static HashSet<GameObject> runProtectedObjects;
        public static bool HasActiveRun { get; private set; }
        public static Scene ActiveRunScene { get; private set; }
        public static int ActiveRunId { get; private set; }

        /// <summary>
        /// Resolves every protected scene object inside <paramref name="scene"/> (the build or play copy) so
        /// IsProtected matches the objects actually being optimized, in O(depth) per check.
        /// </summary>
        public static void BeginRun(Scene scene)
        {
            ActiveRunScene = scene;
            ActiveRunId++;
            HasActiveRun = true;
            runProtectedObjects = new HashSet<GameObject>();

            var data = GetInstance();
            if (data == null) return;

            foreach (var refObj in data.protectedSceneObjects)
            {
                if (refObj?.Resolve() is GameObject go) runProtectedObjects.Add(go);
            }

            foreach (var legacy in data.protectedGameObjects)
            {
                if (legacy == null) continue;
                GameObject match = legacy.scene == scene
                    ? legacy
                    : SynSceneQuery.FindGameObjectByPath(scene, SynPersistentObjectReference.GetHierarchyPath(legacy.transform));
                if (match != null) runProtectedObjects.Add(match);
            }
        }

        public static void EndRun()
        {
            HasActiveRun = false;
            runProtectedObjects = null;
        }

        public static bool IsProtected(GameObject go)
        {
            if (go == null) return false;

            if (HasActiveRun && runProtectedObjects != null)
            {
                for (Transform ancestor = go.transform; ancestor != null; ancestor = ancestor.parent)
                {
                    if (runProtectedObjects.Contains(ancestor.gameObject)) return true;
                }
                return false;
            }

            var data = GetInstance();
            if (data == null) return false;

            Transform t = go.transform;
            while (t != null)
            {
                // Check legacy list
                if (data.protectedGameObjects.Contains(t.gameObject))
                {
                    return true;
                }

                // Check persistent scene objects list
                foreach (var refObj in data.protectedSceneObjects)
                {
                    var resolved = refObj.Resolve() as GameObject;
                    if (resolved != null && resolved == t.gameObject)
                    {
                        return true;
                    }
                }

                t = t.parent;
            }
            return false;
        }

        public static bool IsProtected(Material mat)
        {
            if (mat == null) return false;
            var data = GetInstance();
            if (data == null) return false;
            return data.protectedMaterials.Contains(mat);
        }

        public static bool IsProtected(Mesh mesh)
        {
            if (mesh == null) return false;
            var data = GetInstance();
            if (data == null) return false;
            return data.protectedMeshes.Contains(mesh);
        }

        public static bool IsProtected(Texture2D tex)
        {
            if (tex == null) return false;
            var data = GetInstance();
            if (data == null) return false;
            if (data.protectedTextures.Contains(tex)) return true;

            if (data.protectedTextureNamePatterns != null && data.protectedTextureNamePatterns.Count > 0)
            {
                string lowerName = tex.name.ToLowerInvariant();
                foreach (var pattern in data.protectedTextureNamePatterns)
                {
                    if (!string.IsNullOrEmpty(pattern) && lowerName.Contains(pattern.Trim().ToLowerInvariant()))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // Unity lightmap files ("Lightmap-0_comp_light.exr") and Bakery outputs ("<scene>_LMA0_final", "_RNM0")
        private static readonly System.Text.RegularExpressions.Regex LightmapNamePattern = new System.Text.RegularExpressions.Regex(
            @"(^lightmap-\d+_comp_)|(_lma\d+)|(_rnm[0-2]$)|(^reflectionprobe-\d+$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly string[] LightingOutputFolders = { "bakerylightmaps", "vrclightvolumes", "lightvolumes", "ftlightmaps" };

        /// <summary>
        /// True for baked lighting data (lightmaps, directional maps, shadowmasks, Bakery/light volume outputs).
        /// Uses the texture's import type, the scene's lightmap list and known output folders; file-name matching is
        /// a narrow last resort so ordinary textures that merely contain "lightmap" or "volume" are not protected.
        /// </summary>
        public static bool IsBakeryAsset(string path, Texture tex = null)
        {
            if (string.IsNullOrEmpty(path) && tex != null)
            {
                path = AssetDatabase.GetAssetPath(tex);
            }
            if (string.IsNullOrEmpty(path)) return false;

            // 1. Import type
            if (AssetImporter.GetAtPath(path) is TextureImporter importer &&
                (importer.textureType == TextureImporterType.Lightmap ||
                 importer.textureType == TextureImporterType.DirectionalLightmap ||
                 importer.textureType == TextureImporterType.Shadowmask))
            {
                return true;
            }

            // 2. Referenced by the open scenes' lightmap data
            if (tex != null && LightmapSettings.lightmaps != null)
            {
                foreach (LightmapData lm in LightmapSettings.lightmaps)
                {
                    if (lm != null && (lm.lightmapColor == tex || lm.lightmapDir == tex || lm.shadowMask == tex)) return true;
                }
            }

            // 3. Inside a known lighting output folder
            string[] folders = Path.GetDirectoryName(path).Replace('\\', '/').ToLowerInvariant().Split('/');
            foreach (string folder in folders)
            {
                foreach (string output in LightingOutputFolders)
                {
                    if (folder == output) return true;
                }
            }

            // 4. Lightmapper output file names
            return LightmapNamePattern.IsMatch(Path.GetFileNameWithoutExtension(path));
        }

        public static bool IsMochieAsset(string path, Texture tex = null)
        {
            if (string.IsNullOrEmpty(path) && tex != null)
            {
                path = AssetDatabase.GetAssetPath(tex);
            }

            if (!string.IsNullOrEmpty(path))
            {
                string lowerPath = path.Replace('\\', '/').ToLowerInvariant();
                if (lowerPath.Contains("/mochie/") || 
                    lowerPath.StartsWith("assets/mochie/") || 
                    lowerPath.Contains("assets/mochie") || 
                    lowerPath.Contains("mochieshaders"))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsBakeryAsset(UnityEngine.Object obj)
        {
            if (obj == null) return false;
            string path = AssetDatabase.GetAssetPath(obj);
            Texture tex = obj as Texture;
            return IsBakeryAsset(path, tex);
        }

        public static bool IsProtected(Texture tex)
        {
            if (tex == null) return false;
            if (IsBakeryAsset("", tex)) return true;

            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return false;
            string lowerPath = path.Replace('\\', '/').ToLowerInvariant();
            return lowerPath.StartsWith("packages/") || lowerPath.Contains("transientcache");
        }
    }
}
