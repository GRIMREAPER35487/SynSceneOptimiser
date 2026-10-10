using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Progress display for pipeline runs. Uses Unity's cancelable progress bar, which keeps repainting and reports
    /// Cancel clicks while the pipeline runs synchronously on the main thread (a custom popup window cannot).
    /// </summary>
    public class SynProgressWindow
    {
        private readonly string title;

        public bool CancelRequested { get; private set; }

        private SynProgressWindow(string title)
        {
            this.title = title;
        }

        public static SynProgressWindow Create(string title = "Syn Scene Optimizer")
        {
            return new SynProgressWindow(title);
        }

        /// <summary>
        /// Updates the bar. Returns true once the user has pressed Cancel.
        /// </summary>
        public bool UpdateProgress(float progressNormalized, string statusText)
        {
            if (EditorUtility.DisplayCancelableProgressBar(title, statusText, Mathf.Clamp01(progressNormalized)))
            {
                CancelRequested = true;
            }
            return CancelRequested;
        }

        public void Close()
        {
            EditorUtility.ClearProgressBar();
        }
    }
}
