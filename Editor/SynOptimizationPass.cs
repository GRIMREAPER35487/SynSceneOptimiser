using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    public abstract class SynOptimizationPass
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract string Category { get; }
        public abstract int Priority { get; }
        public virtual bool IsHidden => false;
        public virtual string Tab => "Optimizers";

        // Passes that permanently rewrite the user's own asset import settings are opt-in and flagged in the UI
        public virtual bool ModifiesSourceAssets => false;
        public virtual bool EnabledByDefault => !ModifiesSourceAssets;

        // Passes that change things outside the scene (project import settings, the open editor scenes) are
        // skipped when previewing
        public virtual bool RunInPreview => true;

        public string ToggleKey => string.Format("Pass_{0}_Enabled", Id);
        public bool IsEnabled => SynSceneOptimizerSettings.GetBool(ToggleKey, EnabledByDefault);

        public virtual void DrawGUI(SynSceneOptimizerSettings settings) { }

        public abstract void Execute(Scene scene, List<Renderer> renderers);
    }
}
