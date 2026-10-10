using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    public enum SynRunMode
    {
        Build,
        PlayMode,
        Preview
    }

    [Serializable]
    public class SynPassResult
    {
        public string Name;
        public string Status;
        public double Seconds;
        public List<string> Messages = new List<string>();
    }

    /// <summary>
    /// What the last pipeline run (build, Play Mode or preview) did, pass by pass. Saved to Library/ so it can be
    /// reopened after the build finishes or the editor restarts.
    /// </summary>
    [Serializable]
    public class SynRunReport
    {
        private const string ReportPath = "Library/SynSceneOptimizer/LastRunReport.json";

        public string Mode;
        public string SceneName;
        public string Platform;
        public string Timestamp;
        public double TotalSeconds;
        public bool Cancelled;
        public long VramBeforeBytes = -1;
        public long VramAfterBytes = -1;
        public long TextureBeforeBytes = -1;
        public long TextureAfterBytes = -1;
        public long MeshBeforeBytes = -1;
        public long MeshAfterBytes = -1;
        public List<SynPassResult> Passes = new List<SynPassResult>();

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, JsonUtility.ToJson(this, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not save run report: {e.Message}");
            }
        }

        public static SynRunReport Load()
        {
            try
            {
                return File.Exists(ReportPath) ? JsonUtility.FromJson<SynRunReport>(File.ReadAllText(ReportPath)) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not read run report: {e.Message}");
                return null;
            }
        }
    }

    public class SynRunReportWindow : EditorWindow
    {
        private SynRunReport report;
        private Vector2 scroll;
        private readonly HashSet<int> expanded = new HashSet<int>();

        [MenuItem("Window/Synthos/Last Optimization Report")]
        public static void ShowReport()
        {
            var window = GetWindow<SynRunReportWindow>("Optimization Report");
            window.minSize = new Vector2(520, 320);
            window.report = SynRunReport.Load();
            window.expanded.Clear();
            window.Repaint();
        }

        private void OnEnable()
        {
            if (report == null) report = SynRunReport.Load();
        }

        private void OnGUI()
        {
            if (report == null)
            {
                EditorGUILayout.HelpBox("No optimization run recorded yet. Enter Play Mode, build, or use Preview in the Syn Scene Optimizer window.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(report.Mode == nameof(SynRunMode.Preview) ? "PREVIEW (DRY RUN) - your scene was not changed" : report.Mode.ToUpperInvariant() + " RUN", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Scene: {report.SceneName}    Platform: {report.Platform}    {report.Timestamp}    Took {report.TotalSeconds:F1}s", EditorStyles.miniLabel);
            if (report.Cancelled)
            {
                EditorGUILayout.HelpBox("This run was cancelled before all passes finished.", MessageType.Warning);
            }

            if (report.VramBeforeBytes >= 0 && report.VramAfterBytes >= 0)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawDelta("Total VRAM", report.VramBeforeBytes, report.VramAfterBytes);
                DrawDelta("Textures", report.TextureBeforeBytes, report.TextureAfterBytes);
                DrawDelta("Meshes", report.MeshBeforeBytes, report.MeshAfterBytes);
                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("Refresh", GUILayout.Width(80)))
            {
                report = SynRunReport.Load();
                if (report == null) return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = 0; i < report.Passes.Count; i++)
            {
                var pass = report.Passes[i];
                string marker = pass.Status == "OK" ? "OK" : pass.Status.ToUpperInvariant();
                bool open = expanded.Contains(i);
                bool newOpen = EditorGUILayout.Foldout(open, $"[{marker}]  {pass.Name}  ({pass.Seconds:F2}s, {pass.Messages.Count} notes)", true);
                if (newOpen != open)
                {
                    if (newOpen) expanded.Add(i); else expanded.Remove(i);
                }
                if (!newOpen) continue;

                EditorGUI.indentLevel++;
                if (pass.Messages.Count == 0)
                {
                    EditorGUILayout.LabelField("No changes reported.", EditorStyles.miniLabel);
                }
                foreach (string message in pass.Messages)
                {
                    EditorGUILayout.LabelField(message, EditorStyles.wordWrappedMiniLabel);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndScrollView();
        }

        private static void DrawDelta(string label, long before, long after)
        {
            if (before < 0 || after < 0) return;
            double mbBefore = before / (1024.0 * 1024.0);
            double mbAfter = after / (1024.0 * 1024.0);
            double percent = before > 0 ? (after - before) * 100.0 / before : 0;
            EditorGUILayout.LabelField(label, $"{mbBefore:F1} MB  ->  {mbAfter:F1} MB  ({percent:+0;-0;0}%)");
        }
    }
}
