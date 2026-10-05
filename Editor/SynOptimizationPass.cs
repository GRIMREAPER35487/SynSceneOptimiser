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

        public virtual void DrawGUI(SynSceneOptimizerSettings settings) { }

        public abstract void Execute(Scene scene, List<Renderer> renderers);
    }
}
