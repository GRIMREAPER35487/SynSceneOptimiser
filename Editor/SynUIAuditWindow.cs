using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Read-only overview of every canvas in the open scene: estimated draw calls, what drives them, and what the
    /// UI Optimizer pass would fix. Nothing in the scene is changed.
    /// </summary>
    public class SynUIAuditWindow : EditorWindow
    {
        private List<SynCanvasReport> reports;
        private string scannedScene;
        private Vector2 scroll;
        private bool hideInactive;
        private readonly HashSet<Canvas> expanded = new HashSet<Canvas>();

        [MenuItem("Window/Synthos/UI Audit")]
        public static void ShowWindow()
        {
            var window = GetWindow<SynUIAuditWindow>("UI Audit");
            window.minSize = new Vector2(560, 320);
            window.Scan();
        }

        private void OnEnable()
        {
            if (reports == null) Scan();
        }

        private void Scan()
        {
            Scene scene = SceneManager.GetActiveScene();
            reports = SynUIAnalysis.AnalyzeScene(scene);
            scannedScene = scene.name;
            expanded.RemoveWhere(c => c == null);
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Scene: {scannedScene}", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            hideInactive = GUILayout.Toggle(hideInactive, "Hide inactive", GUILayout.Width(100));
            if (GUILayout.Button("Rescan", GUILayout.Width(80))) Scan();
            EditorGUILayout.EndHorizontal();

            if (reports == null || reports.Count == 0)
            {
                EditorGUILayout.HelpBox("No canvases found in the active scene.", MessageType.Info);
                return;
            }

            // Canvases can be deleted while the window is open
            if (reports.Any(r => r.Canvas == null))
            {
                EditorGUILayout.HelpBox("The scene changed since the last scan.", MessageType.Warning);
                if (GUILayout.Button("Rescan")) Scan();
                return;
            }

            var shown = hideInactive ? reports.Where(r => r.IsActive).ToList() : reports;
            DrawTotals(shown);

            EditorGUILayout.HelpBox("Draw counts are estimates from material and texture changes in draw order (Unity can sometimes merge a few more). Use them to rank canvases; the Frame Debugger has the exact numbers.", MessageType.None);

            DrawHeader();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (SynCanvasReport report in shown) DrawRow(report);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawTotals(List<SynCanvasReport> shown)
        {
            var active = shown.Where(r => r.IsActive).ToList();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"{shown.Count} canvases ({active.Count} active)   ~{active.Sum(r => r.EstimatedDraws)} draws when all active canvases are in view   {active.Sum(r => r.VisibleGraphicCount)} visible graphics");
            EditorGUILayout.LabelField(
                $"UI Optimizer would fix: {shown.Sum(r => r.FixableTransparent)} transparent graphics, {shown.Sum(r => r.FixableRaycastTargets)} raycast targets, {shown.Sum(r => r.FixableZOffsets)} tiny Z offsets   |   Masks: {shown.Sum(r => r.MaskCount)}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        private static void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Canvas", EditorStyles.miniBoldLabel, GUILayout.MinWidth(180));
            GUILayout.Label("Draws", EditorStyles.miniBoldLabel, GUILayout.Width(50));
            GUILayout.Label("Graphics", EditorStyles.miniBoldLabel, GUILayout.Width(60));
            GUILayout.Label("Textures", EditorStyles.miniBoldLabel, GUILayout.Width(60));
            GUILayout.Label("Masks", EditorStyles.miniBoldLabel, GUILayout.Width(45));
            GUILayout.Label("Size (m)", EditorStyles.miniBoldLabel, GUILayout.Width(80));
            GUILayout.Label("", GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRow(SynCanvasReport report)
        {
            EditorGUILayout.BeginHorizontal();
            bool open = expanded.Contains(report.Canvas);
            string label = report.Canvas.name;
            if (!report.IsActive) label += "  (inactive)";
            if (report.IsProtected) label += "  [protected]";
            if (report.Warnings.Count > 0) label += $"  ({report.Warnings.Count})";

            bool newOpen = EditorGUILayout.Foldout(open, new GUIContent(label, report.Path), true);
            if (newOpen != open)
            {
                if (newOpen) expanded.Add(report.Canvas); else expanded.Remove(report.Canvas);
            }

            GUILayout.Label(report.EstimatedDraws.ToString(), GUILayout.Width(50));
            GUILayout.Label($"{report.VisibleGraphicCount}/{report.GraphicCount}", GUILayout.Width(60));
            GUILayout.Label(report.TextureCount.ToString(), GUILayout.Width(60));
            GUILayout.Label(report.MaskCount.ToString(), GUILayout.Width(45));
            GUILayout.Label(report.RenderMode == RenderMode.WorldSpace ? $"{report.WorldSize.x:0.##} x {report.WorldSize.y:0.##}" : "screen", GUILayout.Width(80));
            if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                Selection.activeGameObject = report.Canvas.gameObject;
                EditorGUIUtility.PingObject(report.Canvas.gameObject);
            }
            EditorGUILayout.EndHorizontal();

            if (!newOpen) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(report.Path, EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Materials: {report.MaterialCount}   Nested canvases: {report.NestedCanvasCount}   Off-plane elements: {report.OffPlaneCount}", EditorStyles.miniLabel);
            if (report.Shaders.Count > 0)
            {
                EditorGUILayout.LabelField("Shaders: " + string.Join(", ", report.Shaders), EditorStyles.miniLabel);
            }

            var toggledBy = new List<string>();
            if (report.IsAnimated) toggledBy.Add("an animation");
            if (report.IsScriptReferenced) toggledBy.Add("a script or button event");
            if (toggledBy.Count > 0)
            {
                EditorGUILayout.LabelField("Referenced by " + string.Join(" and ", toggledBy) + " (may be switched on and off at runtime)", EditorStyles.miniLabel);
            }

            if (report.Warnings.Count == 0)
            {
                EditorGUILayout.LabelField("Nothing to improve found.", EditorStyles.miniLabel);
            }
            foreach (string warning in report.Warnings)
            {
                EditorGUILayout.HelpBox(warning, MessageType.None);
            }
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }
    }
}
