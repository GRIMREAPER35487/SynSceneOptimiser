using UnityEngine;
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

        private static string GetHierarchyPath(Transform t)
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
        private const string DefaultAssetPath = "Assets/SynSceneOptimiser/SynProtectionSettings.asset";

        public static SynProtectionData GetInstance()
        {
            if (instance == null)
            {
                // 1. Search for any existing SynProtectionData asset anywhere in the project
                string[] guids = AssetDatabase.FindAssets("t:SynProtectionData");
                if (guids != null && guids.Length > 0)
                {
                    string foundPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                    instance = AssetDatabase.LoadAssetAtPath<SynProtectionData>(foundPath);
                }

                // 2. If not found anywhere, create one at default path in Assets
                if (instance == null)
                {
                    string dir = Path.GetDirectoryName(DefaultAssetPath).Replace('\\', '/');
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    instance = ScriptableObject.CreateInstance<SynProtectionData>();
                    AssetDatabase.CreateAsset(instance, DefaultAssetPath);
                    AssetDatabase.SaveAssets();
                }
            }
            return instance;
        }

        public static bool IsProtected(GameObject go)
        {
            if (go == null) return false;
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

        public static bool IsBakeryAsset(string path, Texture tex = null)
        {
            if (string.IsNullOrEmpty(path) && tex != null)
            {
                path = AssetDatabase.GetAssetPath(tex);
            }
            if (string.IsNullOrEmpty(path)) return false;

            string lowerPath = path.Replace('\\', '/').ToLowerInvariant();
            if (lowerPath.Contains("bakery") ||
                lowerPath.Contains("bakerylightmaps") ||
                lowerPath.Contains("vrclightvolumes") ||
                lowerPath.Contains("ftlightmaps") ||
                lowerPath.Contains("lightmap") ||
                lowerPath.Contains("lightvolume"))
            {
                return true;
            }

            if (tex != null)
            {
                string texName = tex.name.ToLowerInvariant();
                if (texName.Contains("bakery") ||
                    texName.Contains("lightmap") ||
                    texName.Contains("lightvolume") ||
                    texName.Contains("_volume"))
                {
                    return true;
                }
            }

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                if (importer.textureType == TextureImporterType.Lightmap)
                {
                    return true;
                }
            }

            return false;
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
