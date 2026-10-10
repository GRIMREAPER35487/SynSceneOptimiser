using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Resets audio clip import settings back to Unity's defaults. Versions before 1.1 rewrote the user's own clip
    /// import settings permanently; the exact combinations they wrote are recognised and pre-selected, and any
    /// other customised clip is listed for review so it can be reset too if wanted.
    /// </summary>
    public class SynAudioResetWindow : EditorWindow
    {
        internal enum Origin
        {
            OlderOptimizerStreaming,
            OlderOptimizerCompressed,
            OlderOptimizerShortClip,
            OtherCustomized
        }

        internal class Finding
        {
            public Origin Origin;
            public string Path;
            public string Summary;
            public bool Selected;
        }

        private List<Finding> findings = new List<Finding>();
        private readonly Dictionary<Origin, bool> foldouts = new Dictionary<Origin, bool>();
        private Vector2 scroll;

        [MenuItem("Window/Synthos/Reset Audio Import Settings")]
        public static void Open()
        {
            var window = GetWindow<SynAudioResetWindow>("Audio Import Reset");
            window.minSize = new Vector2(560, 360);
            window.Scan();
        }

        /// <summary>
        /// Unity's default sample settings for a newly imported clip.
        /// </summary>
        internal static AudioImporterSampleSettings ApplyUnityDefaults(AudioImporterSampleSettings s)
        {
            s.loadType = AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = 1f;
            s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            s.sampleRateOverride = 44100;
            s.preloadAudioData = true;
            return s;
        }

        private static bool IsUnityDefault(AudioImporterSampleSettings s)
        {
            return s.loadType == AudioClipLoadType.DecompressOnLoad &&
                   s.compressionFormat == AudioCompressionFormat.Vorbis &&
                   Mathf.Approximately(s.quality, 1f) &&
                   s.sampleRateSetting == AudioSampleRateSetting.PreserveSampleRate &&
                   s.preloadAudioData;
        }

        /// <summary>
        /// The exact settings older versions of the audio pass wrote (by file size: &gt;1 MB, 100 KB-1 MB, &lt;100 KB).
        /// </summary>
        private static Origin Classify(AudioImporterSampleSettings s)
        {
            if (s.loadType == AudioClipLoadType.Streaming && s.compressionFormat == AudioCompressionFormat.Vorbis &&
                s.sampleRateSetting == AudioSampleRateSetting.OverrideSampleRate && s.sampleRateOverride == 44100 && !s.preloadAudioData)
                return Origin.OlderOptimizerStreaming;

            if (s.loadType == AudioClipLoadType.CompressedInMemory && s.compressionFormat == AudioCompressionFormat.ADPCM && !s.preloadAudioData)
                return Origin.OlderOptimizerCompressed;

            if (s.loadType == AudioClipLoadType.DecompressOnLoad && s.compressionFormat == AudioCompressionFormat.ADPCM && s.preloadAudioData)
                return Origin.OlderOptimizerShortClip;

            return Origin.OtherCustomized;
        }

        private void Scan()
        {
            findings = ScanProject();
            Repaint();
        }

        internal static List<Finding> ScanProject()
        {
            var results = new List<Finding>();
            string cacheRoot = SynAssetCache.BaseCachePath;

            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith(cacheRoot) || !SynMipStreamingRepairWindow.IsWritable(path)) continue;
                if (!(AssetImporter.GetAtPath(path) is AudioImporter importer)) continue;

                AudioImporterSampleSettings s = importer.defaultSampleSettings;
                if (IsUnityDefault(s)) continue;

                Origin origin = Classify(s);
                results.Add(new Finding
                {
                    Origin = origin,
                    Path = path,
                    Summary = $"{s.loadType}, {s.compressionFormat}" + (s.compressionFormat == AudioCompressionFormat.Vorbis ? $" {s.quality * 100f:F0}%" : ""),
                    Selected = origin != Origin.OtherCustomized
                });
            }
            return results;
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Resets audio clips to Unity's default import settings (Decompress On Load, Vorbis 100%, preserve sample rate, preload on).\n\n" +
                "Versions of the Audio Clip Optimizer before 1.1 changed your clips' settings permanently. Clips matching the exact settings those versions wrote are pre-selected; other customised clips are listed unselected. Platform-specific overrides are not touched.",
                MessageType.Info);

            if (GUILayout.Button("Rescan Project", GUILayout.Height(24)))
            {
                Scan();
            }

            if (findings.Count == 0)
            {
                EditorGUILayout.HelpBox("All audio clips already use Unity's default import settings.", MessageType.None);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (Origin origin in Enum.GetValues(typeof(Origin)))
            {
                List<Finding> group = findings.Where(f => f.Origin == origin).ToList();
                if (group.Count == 0) continue;

                int selectedCount = group.Count(f => f.Selected);
                foldouts.TryGetValue(origin, out bool open);
                foldouts[origin] = EditorGUILayout.Foldout(open, $"{Title(origin)}  ({selectedCount}/{group.Count} selected)", true, EditorStyles.foldoutHeader);
                if (!foldouts[origin]) continue;

                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField(Explanation(origin), EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button("Select All", EditorStyles.miniButtonLeft, GUILayout.Width(80))) group.ForEach(f => f.Selected = true);
                if (GUILayout.Button("Select None", EditorStyles.miniButtonRight, GUILayout.Width(80))) group.ForEach(f => f.Selected = false);
                EditorGUILayout.EndHorizontal();

                foreach (var f in group)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(EditorGUI.indentLevel * 15);
                    f.Selected = EditorGUILayout.Toggle(f.Selected, GUILayout.Width(18));
                    if (GUILayout.Button($"{f.Path}   [{f.Summary}]", EditorStyles.label))
                    {
                        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AudioClip>(f.Path));
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(6);
            }
            EditorGUILayout.EndScrollView();

            int totalSelected = findings.Count(f => f.Selected);
            EditorGUI.BeginDisabledGroup(totalSelected == 0);
            if (GUILayout.Button($"Reset {totalSelected} Selected To Unity Defaults", GUILayout.Height(30)))
            {
                ResetSelected();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void ResetSelected()
        {
            var selected = findings.Where(f => f.Selected).ToList();
            if (selected.Count == 0) return;

            if (!EditorUtility.DisplayDialog(
                "Reset Audio Import Settings",
                $"Reset {selected.Count} audio clips to Unity's default import settings and reimport them?\n\nThis can't be undone with Ctrl+Z. Commit or back up your project first if you use version control.",
                "Reset", "Cancel"))
            {
                return;
            }

            int resetCount = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var f in selected)
                {
                    if (!(AssetImporter.GetAtPath(f.Path) is AudioImporter importer)) continue;
                    try
                    {
                        importer.defaultSampleSettings = ApplyUnityDefaults(importer.defaultSampleSettings);
                        importer.SaveAndReimport();
                        resetCount++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not reset import settings on '{f.Path}': {e.Message}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            SynAudioResetPrompt.MarkHandled();
            Debug.Log($"[SYN SCENE OPTIMIZER] Audio Import Reset: restored Unity default import settings on {resetCount} clips.");
            Scan();
        }

        private static string Title(Origin origin)
        {
            switch (origin)
            {
                case Origin.OlderOptimizerStreaming: return "Large clips set to streaming by an older optimizer";
                case Origin.OlderOptimizerCompressed: return "Medium clips set to ADPCM compressed by an older optimizer";
                case Origin.OlderOptimizerShortClip: return "Short clips set to ADPCM by an older optimizer";
                case Origin.OtherCustomized: return "Other customised clips (review)";
                default: return origin.ToString();
            }
        }

        private static string Explanation(Origin origin)
        {
            switch (origin)
            {
                case Origin.OlderOptimizerStreaming:
                    return "Streaming + Vorbis + 44.1 kHz override + preload off: the exact settings older versions wrote for files over 1 MB.";
                case Origin.OlderOptimizerCompressed:
                    return "Compressed In Memory + ADPCM + preload off: the exact settings older versions wrote for files between 100 KB and 1 MB.";
                case Origin.OlderOptimizerShortClip:
                    return "Decompress On Load + ADPCM: what older versions wrote for files under 100 KB. You may also have chosen ADPCM yourself for short effects; untick any you want to keep.";
                case Origin.OtherCustomized:
                    return "Clips with other non-default settings, most likely chosen by you. Nothing here is pre-selected.";
                default:
                    return "";
            }
        }
    }

    /// <summary>
    /// One-time prompt for projects where an older optimizer ran: offers the reset window if clips still carry the
    /// settings older versions of the audio pass wrote.
    /// </summary>
    [InitializeOnLoad]
    internal static class SynAudioResetPrompt
    {
        private static string PrefKey => "Synthos.SynSceneOptimizer.AudioResetHandled." + Application.dataPath;
        private const string SessionKey = "Synthos.SynSceneOptimizer.AudioResetPromptShown";

        static SynAudioResetPrompt()
        {
            EditorApplication.delayCall += CheckOnce;
        }

        public static void MarkHandled()
        {
            EditorPrefs.SetBool(PrefKey, true);
        }

        private static void CheckOnce()
        {
            if (Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorPrefs.GetBool(PrefKey, false) || SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);

            if (!Directory.Exists(SynAssetCache.BaseCachePath.TrimEnd('/'))) return;

            int likely = SynAudioResetWindow.ScanProject().Count(f => f.Selected);
            if (likely == 0)
            {
                MarkHandled();
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex(
                "Synthos Scene Optimizer",
                $"An older version of the Audio Clip Optimizer permanently changed the import settings of {likely} audio clips in this project.\n\nThe optimizer now works on copies and never changes your clips. Review and reset them to Unity's defaults?",
                "Review", "Not Now", "Don't Ask Again");

            if (choice == 0)
            {
                MarkHandled();
                SynAudioResetWindow.Open();
            }
            else if (choice == 2)
            {
                MarkHandled();
            }
        }
    }
}
