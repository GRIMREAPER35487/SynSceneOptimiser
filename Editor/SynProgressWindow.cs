using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Synthos.SynSceneOptimizer
{
    public class SynProgressWindow : EditorWindow
    {
        private Label statusLabel;
        private Label titleLabel;
        private ProgressBar progressBar;

        private static MethodInfo repaintImmediatelyMethod;

        static SynProgressWindow()
        {
            try
            {
                repaintImmediatelyMethod = typeof(EditorWindow).GetMethod("RepaintImmediately", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            catch { }
        }

        public static SynProgressWindow Create(string title = "SYN SCENE OPTIMIZER")
        {
            var window = CreateInstance<SynProgressWindow>();
            
            Rect mainPos = GetEditorMainWindowPos();
            Vector2 size = new Vector2(520, 200);
            Rect popupRect = new Rect(
                mainPos.xMin + (mainPos.width - size.x) * 0.5f,
                mainPos.yMin + (mainPos.height - size.y) * 0.4f,
                size.x,
                size.y
            );

            window.position = popupRect;
            window.titleContent = new GUIContent("Syn Scene Optimizer");
            window.ShowPopup();
            return window;
        }

        public void OnEnable()
        {
            VisualElement root = rootVisualElement;
            root.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.14f, 1.0f));
            root.style.paddingTop = 20;
            root.style.paddingBottom = 20;
            root.style.paddingLeft = 25;
            root.style.paddingRight = 25;

            // Header Container
            VisualElement header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 15;

            titleLabel = new Label("SYN SCENE OPTIMIZER");
            titleLabel.style.fontSize = 16;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.color = new StyleColor(new Color(0.35f, 0.75f, 1.0f, 1.0f)); // Synthos Cyan Tint
            header.Add(titleLabel);

            root.Add(header);

            // Progress Bar
            progressBar = new ProgressBar();
            progressBar.value = 0;
            progressBar.title = "0%";
            progressBar.style.height = 24;
            progressBar.style.marginBottom = 15;
            root.Add(progressBar);

            // Status Label
            statusLabel = new Label("Preparing optimization pipeline...");
            statusLabel.style.fontSize = 12;
            statusLabel.style.color = new StyleColor(new Color(0.85f, 0.85f, 0.88f, 1.0f));
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            root.Add(statusLabel);
        }

        public void UpdateProgress(float progressNormalized, string statusText)
        {
            float percent = Mathf.Clamp01(progressNormalized) * 100f;
            if (progressBar != null)
            {
                progressBar.value = percent;
                progressBar.title = $"{Math.Round(percent)}%";
            }
            if (statusLabel != null)
            {
                statusLabel.text = statusText;
            }

            RepaintNow();
        }

        private void RepaintNow()
        {
            try
            {
                if (repaintImmediatelyMethod != null)
                {
                    repaintImmediatelyMethod.Invoke(this, null);
                }
                else
                {
                    Repaint();
                }
            }
            catch
            {
                Repaint();
            }
        }

        private static Rect GetEditorMainWindowPos()
        {
            try
            {
                var containerWindowType = typeof(Editor).Assembly.GetType("UnityEditor.ContainerWindow");
                if (containerWindowType != null)
                {
                    var showModeField = containerWindowType.GetField("m_ShowMode", BindingFlags.Instance | BindingFlags.NonPublic);
                    var positionProperty = containerWindowType.GetProperty("position", BindingFlags.Instance | BindingFlags.Public);
                    var windows = Resources.FindObjectsOfTypeAll(containerWindowType);

                    foreach (var win in windows)
                    {
                        var showMode = (int)showModeField.GetValue(win);
                        if (showMode == 4) // 4 = MainWindow
                        {
                            return (Rect)positionProperty.GetValue(win, null);
                        }
                    }
                }
            }
            catch { }

            return new Rect(100, 100, 1280, 720);
        }
    }
}
