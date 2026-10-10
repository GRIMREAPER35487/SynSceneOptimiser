using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Safe build-time fixes for world UI: stops drawing fully transparent graphics, turns off Raycast Target where
    /// nothing can be clicked, and flattens tiny Z offsets that stop elements batching. Changes only the build or
    /// Play Mode copy of the scene.
    /// </summary>
    public class SynUIOptimizerPass : SynOptimizationPass
    {
        public override string Id => "SynUIOptimizerPass";
        public override string Name => "UI Optimizer";
        public override string Description => "Cheaper world UI: skips drawing fully transparent graphics, turns off Raycast Target on graphics that can't be clicked, and flattens tiny Z offsets so canvases batch better. Open the UI Audit to see what each canvas costs.";
        public override string Category => "UI";
        public override int Priority => 60;
        public override string Tab => "Optimizers";

        private const string CullTransparentKey = "UIOptimizer_CullTransparent";
        private const string RaycastTargetsKey = "UIOptimizer_RaycastTargets";
        private const string FlattenZKey = "UIOptimizer_FlattenZ";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool cullTransparent = SynSceneOptimizerSettings.GetBool(CullTransparentKey, true);
            bool raycastTargets = SynSceneOptimizerSettings.GetBool(RaycastTargetsKey, true);
            bool flattenZ = SynSceneOptimizerSettings.GetBool(FlattenZKey, true);

            int culled = 0, transparentNow = 0, raycastsOff = 0, flattened = 0, canvasCount = 0;
            foreach (Canvas canvas in SynUIAnalysis.GetCanvases(scene))
            {
                if (canvas == null || SynProtectionData.IsProtected(canvas.gameObject)) continue;
                if (SynSceneQuery.IsVideoComponentDetected(canvas)) continue;
                canvasCount++;

                if (cullTransparent)
                {
                    culled += SynUIAnalysis.FixTransparentCulling(canvas, true, out int nowTransparent);
                    transparentNow += nowTransparent;
                }
                if (raycastTargets) raycastsOff += SynUIAnalysis.FixRaycastTargets(canvas, true);
                if (flattenZ) flattened += SynUIAnalysis.FlattenTinyZOffsets(canvas, true);
            }

            SynPipelineCompactor.LogChange(
                "UI Optimizer",
                $"Checked {canvasCount} canvases: {culled} graphics now skip drawing while fully transparent ({transparentNow} are transparent right now), turned off Raycast Target on {raycastsOff} unclickable graphics, flattened {flattened} tiny Z offsets.");

            // A short cost summary, so the report shows which canvases are worth a closer look
            var top = SynUIAnalysis.AnalyzeScene(scene, findRuntimeToggles: false).Where(r => r.IsActive).Take(3).ToList();
            if (top.Count > 0)
            {
                string summary = string.Join(", ", top.Select(r => $"{r.Canvas.name} (~{r.EstimatedDraws} draws)"));
                SynPipelineCompactor.LogChange("UI Optimizer", $"Most expensive canvases after fixes: {summary}. Open Window > Synthos > UI Audit for details.");
            }
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("The fixes below apply automatically when this pass is on. Open the UI Audit to see what each canvas costs and why.", MessageType.Info);

            // Distance culling is a scene setup, not a build step, so say plainly when it isn't doing anything
            var culler = SynCanvasCullerSetup.FindCuller(SceneManager.GetActiveScene());
            int managed = culler != null && culler.canvases != null ? culler.canvases.Count(c => c != null) : 0;
            if (managed == 0)
            {
                EditorGUILayout.HelpBox("Distance culling does nothing until you configure it. Open the UI Audit, choose canvases and click Add Distance Culling.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox($"Distance culling: {managed} canvas(es) managed.", MessageType.None);
            }

            if (GUILayout.Button(new GUIContent("Configure / Open UI Audit...", "Shows every canvas in the open scene with its estimated draw calls and what can be improved."), GUILayout.Height(22)))
            {
                SynUIAuditWindow.ShowWindow();
            }

            EditorGUILayout.Space(2);
            DrawToggle(CullTransparentKey, "Skip Drawing Transparent Graphics",
                "Turns on Cull Transparent Mesh, so graphics at zero alpha are not drawn. Fades still work and clicks still register. Mask graphics and custom UI shaders are left alone.");
            DrawToggle(RaycastTargetsKey, "Raycast Target Off When Unclickable",
                "Turns off Raycast Target on text and images that no button, toggle, slider or scroll view uses. Graphics overlapping a clickable element are kept, since they may block clicks on purpose. Saves CPU only.");
            DrawToggle(FlattenZKey, "Flatten Tiny Z Offsets",
                $"Moves elements less than {SynUIAnalysis.TinyZOffsetMeters * 1000f:0.#} mm in front of or behind their canvas back onto it, so they batch with the rest. Larger offsets, tilted and animated elements are left alone.");
        }

        private static void DrawToggle(string key, string label, string tooltip)
        {
            bool value = SynSceneOptimizerSettings.GetBool(key, true);
            bool newValue = EditorGUILayout.Toggle(new GUIContent(label, tooltip), value);
            if (newValue != value) SynSceneOptimizerSettings.SetBool(key, newValue);
        }
    }
}
