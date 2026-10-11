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
        public override string Description => "Cheaper world UI: skips drawing fully transparent graphics, turns off Raycast Target on graphics that can't be clicked, flattens tiny Z offsets so canvases batch better, and can pack UI sprites into atlases. Open the UI Audit to see what each canvas costs.";
        public override string Category => "UI";
        public override int Priority => 60;
        public override string Tab => "Optimizers";

        private const string CullTransparentKey = "UIOptimizer_CullTransparent";
        private const string RaycastTargetsKey = "UIOptimizer_RaycastTargets";
        private const string FlattenZKey = "UIOptimizer_FlattenZ";
        private const string SpriteAtlasKey = "UIOptimizer_SpriteAtlas";
        private const string SpriteAtlasRewriteKey = "UIOptimizer_SpriteAtlasRewriteRefs";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            bool cullTransparent = SynSceneOptimizerSettings.GetBool(CullTransparentKey, true);
            bool raycastTargets = SynSceneOptimizerSettings.GetBool(RaycastTargetsKey, true);
            bool flattenZ = SynSceneOptimizerSettings.GetBool(FlattenZKey, true);
            bool spriteAtlas = SynSceneOptimizerSettings.GetBool(SpriteAtlasKey, false);

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

            if (spriteAtlas)
            {
                bool mobile = SynAssetCache.GetCurrentTargetPlatform() != SynTargetPlatform.PC;
                bool rewrite = SynSceneOptimizerSettings.GetBool(SpriteAtlasRewriteKey, true);
                var atlas = SynUISpriteAtlas.Run(scene, mobile ? 1024 : 2048, rewrite);
                SynPipelineCompactor.LogChange(
                    "UI Optimizer",
                    atlas.SpritesPacked > 0
                        ? $"Packed {atlas.SpritesPacked} UI sprites into {atlas.Atlases} atlas(es), used by {atlas.ImagesChanged} images; pointed {atlas.ReferencesRewritten} script/button references at the atlas copies. {atlas.SpritesSkipped} sprites were left alone ({(rewrite ? "animated, " : "used by scripts, ")}tiled, custom shader, larger than {SynUISpriteAtlas.MaxSpriteSize} px or used elsewhere)."
                        : $"No UI sprites to pack ({atlas.SpritesSkipped} left alone: swapped at runtime, tiled, custom shader, larger than {SynUISpriteAtlas.MaxSpriteSize} px or used elsewhere).");
            }

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
            DrawToggle(SpriteAtlasKey, "Pack UI Sprites into Atlases (Beta)",
                $"Packs the sprites of UI images into shared atlases so a panel's images can draw together instead of one draw per texture. Sprites swapped at runtime (scripts, Udon, button sprite swaps, animations), tiled images, custom UI shaders and sprites over {SynUISpriteAtlas.MaxSpriteSize} px are left alone. Atlases are cached per platform (up to 2048 on PC, 1024 on Quest).",
                false);
            if (SynSceneOptimizerSettings.GetBool(SpriteAtlasKey, false))
            {
                EditorGUI.indentLevel++;
                DrawToggle(SpriteAtlasRewriteKey, "Include Sprites Scripts Use",
                    "Also packs sprites held by scripts, Udon/UdonSharp variables and button sprite swaps, and points those references at the atlas copies, so swaps keep batching and sprite comparisons in scripts still match. Sprites changed by animations, or that a script loads from an asset outside the scene, are not covered.");
                EditorGUI.indentLevel--;
            }
            DrawToggle(FlattenZKey, "Flatten Tiny Z Offsets",
                $"Moves elements less than {SynUIAnalysis.TinyZOffsetMeters * 1000f:0.#} mm in front of or behind their canvas back onto it, so they batch with the rest. Larger offsets, tilted and animated elements are left alone.");
        }

        private static void DrawToggle(string key, string label, string tooltip, bool defaultValue = true)
        {
            bool value = SynSceneOptimizerSettings.GetBool(key, defaultValue);
            bool newValue = EditorGUILayout.Toggle(new GUIContent(label, tooltip), value);
            if (newValue != value) SynSceneOptimizerSettings.SetBool(key, newValue);
        }
    }
}
