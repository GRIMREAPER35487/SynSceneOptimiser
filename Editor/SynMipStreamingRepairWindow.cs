using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Finds texture import settings that older versions of the Mip Streaming pass changed permanently
    /// (forced mipmaps on UI/lookup textures, Kaiser mip filter, streaming left on) and lets the user undo them.
    /// Older versions never recorded original values, so findings are heuristic: the ones the tool almost
    /// certainly caused are pre-selected, the rest are listed for review.
    /// </summary>
    public class SynMipStreamingRepairWindow : EditorWindow
    {
        internal enum IssueKind
        {
            ForcedMipmapsOnUI,
            KaiserFilter,
            StreamingOnIneligible,
            MipmapsOnLookup
        }

        internal class Finding
        {
            public IssueKind Kind;
            public string Path;
            public bool Selected;
        }

        private List<Finding> findings = new List<Finding>();
        private readonly Dictionary<IssueKind, bool> foldouts = new Dictionary<IssueKind, bool>();
        private Vector2 scroll;

        [MenuItem("Window/Synthos/Repair Mip Streaming Changes")]
        public static void Open()
        {
            var window = GetWindow<SynMipStreamingRepairWindow>("Mip Streaming Repair");
            window.minSize = new Vector2(560, 360);
            window.Scan();
        }

        private void Scan()
        {
            findings = ScanProject();
            Repaint();
        }

        internal static List<Finding> ScanProject()
        {
            // Finish any pending revert from the current version first so its temporary Kaiser/streaming
            // changes aren't mistaken for leftovers from older versions
            if (SynImporterRevertJournal.HasPendingReverts && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SynEnableMipStreamingPass.RevertImporterChanges();
            }

            var results = new List<Finding>();
            string cacheRoot = SynAssetCache.BaseCachePath;
            string[] guids = AssetDatabase.FindAssets("t:Texture2D");

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (i % 100 == 0 && EditorUtility.DisplayCancelableProgressBar("Scanning texture import settings", path, (float)i / guids.Length))
                    {
                        break;
                    }

                    if (path.StartsWith(cacheRoot) || !IsWritable(path)) continue;
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;
                    if (!importer.mipmapEnabled && !importer.streamingMipmaps) continue;

                    importer.GetSourceTextureWidthAndHeight(out int width, out int height);
                    string name = System.IO.Path.GetFileNameWithoutExtension(path);
                    bool isUI = importer.textureType == TextureImporterType.Sprite || importer.textureType == TextureImporterType.GUI;
                    bool isLookup = SynMipStreamingEligibility.IsLookupShape(width, height, name) || importer.filterMode == FilterMode.Point;
                    bool isPalette = path.ToLowerInvariant().Contains("palette") || name.ToLowerInvariant().Contains("colorsheet");

                    if (importer.mipmapEnabled)
                    {
                        // Unity disables mipmaps for Sprite/GUI textures by default, so mips here were almost certainly forced
                        if (isUI)
                        {
                            results.Add(new Finding { Kind = IssueKind.ForcedMipmapsOnUI, Path = path, Selected = true });
                        }
                        // Default textures have mipmaps on out of the box, so this may be the user's own setting
                        else if (importer.textureType == TextureImporterType.Default && (isLookup || isPalette))
                        {
                            results.Add(new Finding { Kind = IssueKind.MipmapsOnLookup, Path = path, Selected = false });
                        }

                        if (importer.mipmapFilter == TextureImporterMipFilter.KaiserFilter)
                        {
                            results.Add(new Finding { Kind = IssueKind.KaiserFilter, Path = path, Selected = true });
                        }
                    }

                    if (importer.streamingMipmaps)
                    {
                        bool ineligible = !importer.mipmapEnabled || isUI || isLookup || isPalette
                            || importer.textureType == TextureImporterType.Cookie
                            || importer.textureShape != TextureImporterShape.Texture2D;
                        if (ineligible)
                        {
                            results.Add(new Finding { Kind = IssueKind.StreamingOnIneligible, Path = path, Selected = true });
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return results;
        }

        // Registry, built-in and git packages are read-only; only project and embedded/local packages can be fixed
        private static bool IsWritable(string path)
        {
            if (path.StartsWith("Assets/")) return true;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            return package != null && (package.source == UnityEditor.PackageManager.PackageSource.Embedded
                || package.source == UnityEditor.PackageManager.PackageSource.Local);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Older versions of the Mip Streaming pass changed texture import settings permanently: they forced mipmaps onto UI, sprite and lookup textures, set the Kaiser mip filter, and could leave streaming switched on.\n\n" +
                "Those versions did not record the original values, so review the list. Items the tool almost certainly changed are pre-selected.",
                MessageType.Info);

            if (GUILayout.Button("Rescan Project", GUILayout.Height(24)))
            {
                Scan();
            }

            if (findings.Count == 0)
            {
                EditorGUILayout.HelpBox("No leftover changes found.", MessageType.None);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (IssueKind kind in Enum.GetValues(typeof(IssueKind)))
            {
                List<Finding> group = findings.Where(f => f.Kind == kind).ToList();
                if (group.Count == 0) continue;

                int selectedCount = group.Count(f => f.Selected);
                foldouts.TryGetValue(kind, out bool open);
                foldouts[kind] = EditorGUILayout.Foldout(open, $"{Title(kind)}  ({selectedCount}/{group.Count} selected)", true, EditorStyles.foldoutHeader);
                if (!foldouts[kind]) continue;

                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField(Explanation(kind), EditorStyles.wordWrappedMiniLabel);

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
                    if (GUILayout.Button(f.Path, EditorStyles.label))
                    {
                        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(f.Path));
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(6);
            }
            EditorGUILayout.EndScrollView();

            int totalSelected = findings.Count(f => f.Selected);
            EditorGUI.BeginDisabledGroup(totalSelected == 0);
            if (GUILayout.Button($"Repair {totalSelected} Selected", GUILayout.Height(30)))
            {
                ApplySelected();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void ApplySelected()
        {
            var byPath = findings.Where(f => f.Selected).GroupBy(f => f.Path).ToList();
            if (byPath.Count == 0) return;

            if (!EditorUtility.DisplayDialog(
                "Repair Texture Import Settings",
                $"Change import settings on {byPath.Count} textures and reimport them?\n\nThis can't be undone with Ctrl+Z. Commit or back up your project first if you use version control.",
                "Repair", "Cancel"))
            {
                return;
            }

            int repaired = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var group in byPath)
                {
                    if (!(AssetImporter.GetAtPath(group.Key) is TextureImporter importer)) continue;

                    foreach (var f in group)
                    {
                        switch (f.Kind)
                        {
                            case IssueKind.ForcedMipmapsOnUI:
                            case IssueKind.MipmapsOnLookup:
                                importer.mipmapEnabled = false;
                                importer.streamingMipmaps = false;
                                break;
                            case IssueKind.KaiserFilter:
                                importer.mipmapFilter = TextureImporterMipFilter.BoxFilter;
                                break;
                            case IssueKind.StreamingOnIneligible:
                                importer.streamingMipmaps = false;
                                break;
                        }
                    }

                    try
                    {
                        importer.SaveAndReimport();
                        repaired++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not repair import settings on '{group.Key}': {e.Message}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            SynMipStreamingRepairPrompt.MarkHandled();
            Debug.Log($"[SYN SCENE OPTIMIZER] Mip Streaming Repair: restored import settings on {repaired} textures.");
            Scan();
        }

        private static string Title(IssueKind kind)
        {
            switch (kind)
            {
                case IssueKind.ForcedMipmapsOnUI: return "Mipmaps forced onto UI / sprite textures";
                case IssueKind.KaiserFilter: return "Kaiser mip filter left on";
                case IssueKind.StreamingOnIneligible: return "Streaming on textures that can't benefit";
                case IssueKind.MipmapsOnLookup: return "Mipmaps on lookup / ramp / palette textures (review)";
                default: return kind.ToString();
            }
        }

        private static string Explanation(IssueKind kind)
        {
            switch (kind)
            {
                case IssueKind.ForcedMipmapsOnUI:
                    return "Unity turns mipmaps off for Sprite and GUI textures by default, so these were almost certainly changed by the optimizer. They use a third more memory and can look blurry. Fix: turn mipmaps and streaming off.";
                case IssueKind.KaiserFilter:
                    return "Older versions set the Kaiser filter on every scene texture and never reverted it. Unity's default is Box. Uncheck any texture you set to Kaiser yourself. Fix: set the mip filter back to Box.";
                case IssueKind.StreamingOnIneligible:
                    return "Mip streaming is on for UI, sprite, cookie, lookup, palette or non-mipmapped textures, where Unity can't pick a mip level. Fix: turn streaming off.";
                case IssueKind.MipmapsOnLookup:
                    return "Lookup tables, ramps, gradients, palettes and point-filtered textures with mipmaps can sample wrong values at a distance. Unity's Default texture type has mipmaps on out of the box, so the optimizer may not have caused this; nothing here is pre-selected. Fix: turn mipmaps and streaming off.";
                default:
                    return "";
            }
        }
    }

    /// <summary>
    /// One-time prompt for projects that ran an older optimizer version: offers to open the repair window
    /// when leftover import changes are found.
    /// </summary>
    [InitializeOnLoad]
    internal static class SynMipStreamingRepairPrompt
    {
        private static string PrefKey => "Synthos.SynSceneOptimizer.MipRepairHandled." + Application.dataPath;
        private const string SessionKey = "Synthos.SynSceneOptimizer.MipRepairPromptShown";

        static SynMipStreamingRepairPrompt()
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

            // Only projects where an older optimizer actually ran have a cache folder
            if (!Directory.Exists(SynAssetCache.BaseCachePath.TrimEnd('/')))
            {
                return;
            }

            int likely = SynMipStreamingRepairWindow.ScanProject().Count(f => f.Selected);
            if (likely == 0)
            {
                MarkHandled();
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex(
                "Synthos Scene Optimizer",
                $"An older version of the Mip Streaming pass permanently changed import settings on {likely} texture settings in this project (forced mipmaps, Kaiser filter, streaming).\n\nReview and undo those changes now?",
                "Review", "Not Now", "Don't Ask Again");

            if (choice == 0)
            {
                MarkHandled();
                SynMipStreamingRepairWindow.Open();
            }
            else if (choice == 2)
            {
                MarkHandled();
            }
        }
    }
}
