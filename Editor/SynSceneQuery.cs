using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

namespace Synthos.SynSceneOptimizer
{
    public static class SynSceneQuery
    {
        private static readonly List<Renderer> CachedRenderers = new List<Renderer>();
        private static bool isCached = false;

        public static List<Renderer> GetAllRenderers(Scene scene)
        {
            if (isCached && CachedRenderers.Count > 0)
            {
                return CachedRenderers;
            }

            CachedRenderers.Clear();
            var found = new HashSet<Renderer>();

            // 1. Scan from provided scene
            if (scene.IsValid() && scene.isLoaded)
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r != null && (r is MeshRenderer || r is SkinnedMeshRenderer))
                        {
                            found.Add(r);
                        }
                    }
                }
            }

            // 2. Also scan all loaded scenes in the Hierarchy
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene sc = SceneManager.GetSceneAt(s);
                if (!sc.isLoaded) continue;
                foreach (GameObject root in sc.GetRootGameObjects())
                {
                    if (root == null) continue;
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r != null && (r is MeshRenderer || r is SkinnedMeshRenderer))
                        {
                            found.Add(r);
                        }
                    }
                }
            }

            // 3. Fallback: Find in active scene hierarchy
            if (found.Count == 0)
            {
                Renderer[] allRenderers = Resources.FindObjectsOfTypeAll<Renderer>();
                foreach (Renderer r in allRenderers)
                {
                    if (r != null && (r is MeshRenderer || r is SkinnedMeshRenderer) && !EditorUtility.IsPersistent(r.gameObject))
                    {
                        found.Add(r);
                    }
                }
            }

            CachedRenderers.AddRange(found);
            isCached = true;
            return CachedRenderers;
        }

        public static void ClearCache()
        {
            CachedRenderers.Clear();
            isCached = false;
        }

        public static bool IsVideoComponentDetected(Component component)
        {
            if (component == null) return false;
            Transform current = component.transform;
            while (current != null)
            {
                Component[] components = current.GetComponents<Component>();
                foreach (Component comp in components)
                {
                    if (comp == null) continue;
                    System.Type type = comp.GetType();
                    string fullName = type.FullName ?? "";
                    string name = type.Name;

                    if (fullName.Contains("ArchiTech.ProTV") ||
                        fullName.Contains("VideoPlayer") ||
                        fullName.Contains("VPManager") ||
                        fullName.Contains("TVManager") ||
                        fullName.Contains("VRCAvatarPedestal") ||
                        name.Contains("VideoPlayer") ||
                        name.Contains("VPManager") ||
                        name.Contains("TVManager") ||
                        name.Contains("VRCAvatarPedestal"))
                    {
                        return true;
                    }
                }
                current = current.parent;
            }
            return false;
        }

        public static GameObject FindGameObjectByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root != null && root.name == parts[0])
                    {
                        Transform cur = root.transform;
                        bool match = true;
                        for (int i = 1; i < parts.Length; i++)
                        {
                            Transform child = null;
                            for (int c = 0; c < cur.childCount; c++)
                            {
                                var ch = cur.GetChild(c);
                                if (ch != null && ch.name == parts[i])
                                {
                                    child = ch;
                                    break;
                                }
                            }
                            if (child == null) { match = false; break; }
                            cur = child;
                        }
                        if (match && cur != null) return cur.gameObject;
                    }
                }
            }
            return null;
        }
    }
}
