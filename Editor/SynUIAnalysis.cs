using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// What one canvas costs to draw, as far as can be told without rendering it. Graphics belong to the closest
    /// canvas above them, so a nested canvas gets its own entry.
    /// </summary>
    public class SynCanvasReport
    {
        public Canvas Canvas;
        public string Path;
        public bool IsProtected;
        public bool IsNested;
        public bool IsActive;
        public RenderMode RenderMode;
        public Vector2 WorldSize;

        public int GraphicCount;
        public int VisibleGraphicCount;
        public int TextureCount;
        public int MaterialCount;
        public int EstimatedDraws;
        public int MaskCount;
        public int NestedCanvasCount;
        public int OffPlaneCount;
        public List<string> Shaders = new List<string>();
        public bool IsAnimated;
        public bool IsScriptReferenced;

        // What the UI Optimizer pass would change on this canvas
        // Graphics that get "skip when transparent", and how many of those are fully transparent right now
        // (the ones that actually stop being drawn)
        public int FixableTransparent;
        public int TransparentNow;
        public bool IsVideoPlayer;
        public int FixableRaycastTargets;
        public int FixableZOffsets;

        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Shared UI analysis used by the UI Audit window and the UI Optimizer pass, so both always agree on what
    /// is safe to change.
    /// </summary>
    public static class SynUIAnalysis
    {
        // Offsets this close to the canvas plane are invisible at any distance (0.5 mm)
        public const float TinyZOffsetMeters = 0.0005f;

        private const float PlaneEpsilon = 1e-6f;
        private const float TiltDotThreshold = 0.99999f;

        /// <param name="findRuntimeToggles">Also look for animations and scripts that switch canvases (slower on large scenes).</param>
        public static List<SynCanvasReport> AnalyzeScene(Scene scene, bool findRuntimeToggles = true)
        {
            var reports = new List<SynCanvasReport>();
            if (!scene.IsValid() || !scene.isLoaded) return reports;

            var animatedCanvases = findRuntimeToggles ? CollectAnimatedCanvases(scene) : new HashSet<Canvas>();
            var scriptReferences = findRuntimeToggles ? CollectScriptReferences(scene) : new HashSet<Object>();

            foreach (Canvas canvas in GetCanvases(scene))
            {
                reports.Add(AnalyzeCanvas(canvas, animatedCanvases, scriptReferences));
            }

            return reports.OrderByDescending(r => r.EstimatedDraws).ToList();
        }

        public static List<Canvas> GetCanvases(Scene scene)
        {
            var canvases = new List<Canvas>();
            if (!scene.IsValid() || !scene.isLoaded) return canvases;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                canvases.AddRange(root.GetComponentsInChildren<Canvas>(true));
            }
            return canvases;
        }

        /// <summary>The closest canvas at or above <paramref name="t"/>, the one that batches its graphic.</summary>
        public static Canvas GetOwningCanvas(Transform t)
        {
            for (Transform current = t; current != null; current = current.parent)
            {
                if (current.TryGetComponent(out Canvas canvas)) return canvas;
            }
            return null;
        }

        public static Canvas GetRootCanvas(Transform t)
        {
            Canvas root = null;
            for (Transform current = t; current != null; current = current.parent)
            {
                if (current.TryGetComponent(out Canvas canvas)) root = canvas;
            }
            return root;
        }

        /// <summary>Graphics that this canvas batches itself (graphics under a nested canvas are excluded).</summary>
        public static List<Graphic> GetOwnedGraphics(Canvas canvas)
        {
            var owned = new List<Graphic>();
            foreach (Graphic g in canvas.GetComponentsInChildren<Graphic>(true))
            {
                if (GetOwningCanvas(g.transform) == canvas) owned.Add(g);
            }
            return owned;
        }

        private static SynCanvasReport AnalyzeCanvas(Canvas canvas, HashSet<Canvas> animatedCanvases, HashSet<Object> scriptReferences)
        {
            var report = new SynCanvasReport
            {
                Canvas = canvas,
                Path = SynPersistentObjectReference.GetHierarchyPath(canvas.transform),
                IsProtected = SynProtectionData.IsProtected(canvas.gameObject),
                IsVideoPlayer = SynSceneQuery.IsVideoComponentDetected(canvas),
                IsNested = canvas.transform.parent != null && GetOwningCanvas(canvas.transform.parent) != null,
                IsActive = canvas.isActiveAndEnabled,
                RenderMode = canvas.renderMode,
                IsAnimated = animatedCanvases.Contains(canvas),
                IsScriptReferenced = scriptReferences.Contains(canvas) || scriptReferences.Contains(canvas.gameObject),
            };

            if (canvas.transform is RectTransform canvasRect)
            {
                Vector3 scale = canvasRect.lossyScale;
                report.WorldSize = new Vector2(Mathf.Abs(canvasRect.rect.width * scale.x), Mathf.Abs(canvasRect.rect.height * scale.y));
            }

            foreach (Canvas child in canvas.GetComponentsInChildren<Canvas>(true))
            {
                if (child != canvas && GetOwningCanvas(child.transform.parent) == canvas) report.NestedCanvasCount++;
            }

            List<Graphic> graphics = GetOwnedGraphics(canvas);
            report.GraphicCount = graphics.Count;

            var textures = new HashSet<Texture>();
            var materials = new HashSet<Material>();
            var shaders = new HashSet<string>();
            int draws = 0;
            (Material, Texture) lastKey = default;
            bool hasLast = false;
            bool usesSupersampledUI = false;

            foreach (Graphic g in graphics)
            {
                if (!IsVisible(g)) continue;
                report.VisibleGraphicCount++;

                Material mat = g.material;
                Texture tex = g.mainTexture;
                if (tex != null) textures.Add(tex);
                if (mat != null)
                {
                    materials.Add(mat);
                    if (mat.shader != null)
                    {
                        shaders.Add(mat.shader.name);
                        if (mat.shader.name.Contains("Supersampled UI")) usesSupersampledUI = true;
                    }
                }

                // Every change of material or texture in draw order can start a new batch. Unity can sometimes
                // reorder elements that don't overlap, so this is an upper estimate.
                var key = (mat, tex);
                if (!hasLast || !Equals(key, lastKey)) draws++;
                lastKey = key;
                hasLast = true;

                if (g.TryGetComponent(out Mask mask) && mask.isActiveAndEnabled)
                {
                    report.MaskCount++;
                }

                if (IsOffPlane(g.rectTransform, canvas)) report.OffPlaneCount++;
            }

            // A Mask draws its stencil and clears it again afterwards
            report.EstimatedDraws = draws + report.MaskCount * 2;
            report.TextureCount = textures.Count;
            report.MaterialCount = materials.Count;
            report.Shaders = shaders.OrderBy(s => s).ToList();

            // Same skips as the UI Optimizer pass, so the audit never promises fixes the build won't make
            if (!report.IsProtected && !report.IsVideoPlayer)
            {
                report.FixableTransparent = FixTransparentCulling(canvas, false, out report.TransparentNow);
                report.FixableRaycastTargets = FixRaycastTargets(canvas, false);
                report.FixableZOffsets = FlattenTinyZOffsets(canvas, false);
            }

            AddWarnings(report, usesSupersampledUI);
            return report;
        }

        private static void AddWarnings(SynCanvasReport report, bool usesSupersampledUI)
        {
            if (report.MaskCount > 0)
            {
                report.Warnings.Add($"{report.MaskCount} Mask component(s): each adds about 2 draws and splits batching. RectMask2D is cheaper for plain rectangles.");
            }
            if (report.TextureCount > 4)
            {
                report.Warnings.Add($"Uses {report.TextureCount} different textures. Switching texture can start a new draw; packing the sprites into one atlas would help.");
            }
            if (report.OffPlaneCount > 0)
            {
                string fixable = report.FixableZOffsets > 0 ? $" {report.FixableZOffsets} are tiny offsets the UI Optimizer flattens." : "";
                report.Warnings.Add($"{report.OffPlaneCount} element(s) are not flat on the canvas (moved forward/back or tilted), which can stop them batching with the rest.{fixable}");
            }
            if (report.TransparentNow > 0)
            {
                report.Warnings.Add($"{report.TransparentNow} fully transparent graphic(s) are still drawn. The UI Optimizer stops drawing them; clicks still work.");
            }
            if (report.FixableRaycastTargets > 0)
            {
                report.Warnings.Add($"{report.FixableRaycastTargets} graphic(s) have Raycast Target on but can't be clicked (CPU cost only). The UI Optimizer turns it off.");
            }
            if (usesSupersampledUI)
            {
                report.Warnings.Add("Uses VRChat's Supersampled UI shader: sharper text, but it samples each pixel several times.");
            }
            if (report.NestedCanvasCount > 0)
            {
                report.Warnings.Add($"{report.NestedCanvasCount} nested canvas(es), each batched on its own (listed separately).");
            }
        }

        public static bool IsVisible(Graphic g)
        {
            if (g == null || !g.isActiveAndEnabled) return false;
            if (g.color.a <= 0.001f) return false;
            if (IsEmptyText(g)) return false;

            for (Transform t = g.transform; t != null; t = t.parent)
            {
                if (t.TryGetComponent(out CanvasGroup group) && group.enabled && group.alpha <= 0.001f) return false;
            }
            return true;
        }

        private static bool IsEmptyText(Graphic g)
        {
            if (g is Text text) return string.IsNullOrEmpty(text.text);

            // TextMeshPro is optional, so read its text by reflection
            var property = g.GetType().GetProperty("text", typeof(string));
            if (property != null && g.GetType().Name.Contains("TextMeshPro"))
            {
                return string.IsNullOrEmpty(property.GetValue(g) as string);
            }
            return false;
        }

        /// <summary>
        /// A graphic is clickable if it or any parent receives pointer events (buttons, scroll views, sliders,
        /// event triggers). Unity passes a click up the whole hierarchy until something handles it.
        /// </summary>
        public static bool IsInteractive(Graphic g)
        {
            for (Transform t = g.transform; t != null; t = t.parent)
            {
                foreach (MonoBehaviour behaviour in t.GetComponents<MonoBehaviour>())
                {
                    if (behaviour is IEventSystemHandler) return true;
                }
            }
            return false;
        }

        public static bool IsOffPlane(RectTransform rt, Canvas canvas)
        {
            if (canvas.renderMode != RenderMode.WorldSpace) return false;
            Transform plane = canvas.transform;
            float distance = Vector3.Dot(rt.position - plane.position, plane.forward);
            float facing = Mathf.Abs(Vector3.Dot(rt.forward, plane.forward));
            return Mathf.Abs(distance) > PlaneEpsilon || facing < TiltDotThreshold;
        }

        // Shaders known to multiply by vertex alpha, so a graphic at zero alpha really draws nothing
        private static bool UsesStandardUIShader(Graphic g)
        {
            Material mat = g.material;
            if (mat == null || mat == g.defaultMaterial) return true;
            if (mat.shader == null) return false;
            string name = mat.shader.name;
            return name.StartsWith("UI/") || name.StartsWith("TextMeshPro/") || name.Contains("Supersampled UI");
        }

        /// <summary>
        /// Turns on "Cull Transparent Mesh" so graphics at zero alpha are not drawn. Unity re-checks the alpha every
        /// time the graphic changes, so fades keep working. Returns how many graphics were (or would be) changed;
        /// <paramref name="transparentNow"/> is how many of them are fully transparent at the moment.
        /// </summary>
        public static int FixTransparentCulling(Canvas canvas, bool apply, out int transparentNow)
        {
            int count = 0;
            transparentNow = 0;
            foreach (Graphic g in GetOwnedGraphics(canvas))
            {
                if (g == null || SynProtectionData.IsProtected(g.gameObject)) continue;
                // Graphic.canvasRenderer adds one when missing; the audit must never change the open scene
                CanvasRenderer cr = g.GetComponent<CanvasRenderer>();
                if (cr == null || cr.cullTransparentMesh) continue;

                // A Mask's graphic is often invisible on purpose but must still draw for the stencil to work
                if (g.TryGetComponent(out Mask _)) continue;
                if (!UsesStandardUIShader(g)) continue;

                count++;
                if (g.isActiveAndEnabled && g.color.a <= 0.001f) transparentNow++;
                if (apply) cr.cullTransparentMesh = true;
            }
            return count;
        }

        /// <summary>
        /// Turns off Raycast Target on graphics that nothing can receive clicks from. A graphic that overlaps a
        /// clickable element is kept, since it may be blocking clicks on purpose.
        /// </summary>
        public static int FixRaycastTargets(Canvas canvas, bool apply)
        {
            Canvas root = GetRootCanvas(canvas.transform);
            if (root == null || root.GetComponent<BaseRaycaster>() == null) return 0;

            var interactiveRects = new List<Rect>();
            foreach (Graphic g in root.GetComponentsInChildren<Graphic>(true))
            {
                if (g.raycastTarget && IsInteractive(g)) interactiveRects.Add(GetRectInCanvas(g.rectTransform, root));
            }

            int count = 0;
            foreach (Graphic g in GetOwnedGraphics(canvas))
            {
                if (g == null || !g.raycastTarget || SynProtectionData.IsProtected(g.gameObject)) continue;
                if (IsInteractive(g)) continue;

                Rect rect = GetRectInCanvas(g.rectTransform, root);
                if (interactiveRects.Any(r => r.Overlaps(rect))) continue;

                count++;
                if (apply) g.raycastTarget = false;
            }
            return count;
        }

        private static readonly Vector3[] CornerBuffer = new Vector3[4];

        private static Rect GetRectInCanvas(RectTransform rt, Canvas root)
        {
            rt.GetWorldCorners(CornerBuffer);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            foreach (Vector3 corner in CornerBuffer)
            {
                Vector3 local = root.transform.InverseTransformPoint(corner);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        /// <summary>
        /// Moves elements that sit a hair (under 0.5 mm) in front of or behind the canvas plane back onto it, so
        /// they can batch again. Parents are fixed before children, so a fixed container also fixes its contents.
        /// </summary>
        public static int FlattenTinyZOffsets(Canvas canvas, bool apply)
        {
            if (canvas.renderMode != RenderMode.WorldSpace) return 0;
            Transform plane = canvas.transform;
            int count = 0;

            foreach (RectTransform rt in canvas.GetComponentsInChildren<RectTransform>(true))
            {
                if (rt == plane || GetOwningCanvas(rt) != canvas) continue;
                if (SynProtectionData.IsProtected(rt.gameObject)) continue;
                if (Mathf.Abs(Vector3.Dot(rt.forward, plane.forward)) < TiltDotThreshold) continue;
                if (rt.GetComponentInParent<Animator>(true) != null) continue;

                float distance = Vector3.Dot(rt.position - plane.position, plane.forward);
                if (Mathf.Abs(distance) <= PlaneEpsilon || Mathf.Abs(distance) >= TinyZOffsetMeters) continue;

                count++;
                if (apply) rt.position -= plane.forward * distance;
            }
            return count;
        }

        // Canvases whose Canvas component is switched by an animation clip
        private static HashSet<Canvas> CollectAnimatedCanvases(Scene scene)
        {
            var animated = new HashSet<Canvas>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
                {
                    if (animator.runtimeAnimatorController == null) continue;
                    foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                    {
                        if (clip == null) continue;
                        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
                        {
                            if (binding.type != typeof(Canvas)) continue;
                            Transform target = animator.transform.Find(binding.path);
                            if (target == null && string.IsNullOrEmpty(binding.path)) target = animator.transform;
                            if (target != null && target.TryGetComponent(out Canvas canvas)) animated.Add(canvas);
                        }
                    }
                }
            }
            return animated;
        }

        // Every object referenced by a serialized field of any script in the scene (Udon public variables and
        // UdonSharp proxy fields are serialized object references too)
        private static HashSet<Object> CollectScriptReferences(Scene scene)
        {
            var references = new HashSet<Object>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    // Buttons stay in: their click events can toggle canvases
                    if (behaviour == null || behaviour is Graphic) continue;
                    var iterator = new SerializedObject(behaviour).GetIterator();
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue != null)
                        {
                            references.Add(iterator.objectReferenceValue);
                        }
                    }
                }
            }
            return references;
        }
    }
}
