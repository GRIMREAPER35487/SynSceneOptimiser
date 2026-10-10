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

        // Scene the cached renderer list belongs to; a request for a different scene rebuilds it
        private static Scene cachedScene;

        /// <summary>
        /// Mesh and skinned renderers of exactly this scene (active and inactive). Other loaded scenes are never
        /// included: a preview runs on a temporary copy while the user's scene stays open, and must not touch it.
        /// </summary>
        public static List<Renderer> GetAllRenderers(Scene scene)
        {
            if (isCached && cachedScene == scene)
            {
                return CachedRenderers;
            }

            CachedRenderers.Clear();
            cachedScene = scene;
            isCached = true;

            if (!scene.IsValid() || !scene.isLoaded) return CachedRenderers;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r != null && (r is MeshRenderer || r is SkinnedMeshRenderer))
                    {
                        CachedRenderers.Add(r);
                    }
                }
            }
            return CachedRenderers;
        }

        public static void ClearCache()
        {
            CachedRenderers.Clear();
            VideoTransformCache.Clear();
            isCached = false;
        }

        // Per-run memo: whether a transform or any of its parents carries a video player / pedestal component
        private static readonly Dictionary<Transform, bool> VideoTransformCache = new Dictionary<Transform, bool>();

        public static bool IsVideoComponentDetected(Component component)
        {
            return component != null && IsVideoTransform(component.transform);
        }

        private static bool IsVideoTransform(Transform t)
        {
            if (t == null) return false;
            if (VideoTransformCache.TryGetValue(t, out bool cached)) return cached;

            bool result = HasVideoComponent(t) || IsVideoTransform(t.parent);
            VideoTransformCache[t] = result;
            return result;
        }

        private static bool HasVideoComponent(Transform t)
        {
            foreach (Component comp in t.GetComponents<Component>())
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
            return false;
        }

        public static GameObject FindGameObjectByPath(string path)
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                GameObject found = FindGameObjectByPath(scene, path);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Finds a GameObject by "Root/Child/Grandchild" path inside one scene (active and inactive objects).
        /// </summary>
        public static GameObject FindGameObjectByPath(Scene scene, string path)
        {
            if (string.IsNullOrEmpty(path) || !scene.IsValid() || !scene.isLoaded) return null;
            string[] parts = path.Split('/');

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null || root.name != parts[0]) continue;

                Transform cur = root.transform;
                for (int i = 1; i < parts.Length && cur != null; i++)
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
                    cur = child;
                }
                if (cur != null) return cur.gameObject;
            }
            return null;
        }
    }
}
