using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>Kinds of protected entries a pass can be allowed to work on.</summary>
    [System.Flags]
    public enum SynProtectionTargets
    {
        None = 0,
        Objects = 1,
        Materials = 2,
        Meshes = 4,
        Textures = 8
    }

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

        // What the pass can change on a protected entry that allows it, for the Protected Objects allow lists.
        // Scene-wide passes that don't act on individual objects return None and are not listed.
        public virtual SynProtectionTargets ProtectionTargets => SynProtectionTargets.Objects;
        public virtual string ProtectionEffect => "Changes the object's components.";

        public virtual void DrawGUI(SynSceneOptimizerSettings settings) { }

        public abstract void Execute(Scene scene, List<Renderer> renderers);
    }
}
