using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// On-disk record of temporary TextureImporter changes made during a build or Play Mode session.
    /// Stored under Library/ so pending reverts survive domain reloads and editor restarts.
    /// </summary>
    public static class SynImporterRevertJournal
    {
        private const string JournalPath = "Library/SynSceneOptimizer/ImporterRevertJournal.txt";

        public class Entry
        {
            public bool StreamingWasOff;
            public bool MipmapsWereOff;
            public int OriginalMipFilter = -1;
        }

        public static Dictionary<string, Entry> Load()
        {
            var entries = new Dictionary<string, Entry>();
            if (!File.Exists(JournalPath)) return entries;

            try
            {
                foreach (string line in File.ReadAllLines(JournalPath))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 4 || string.IsNullOrEmpty(parts[0])) continue;
                    entries[parts[0]] = new Entry
                    {
                        StreamingWasOff = parts[1] == "1",
                        MipmapsWereOff = parts[2] == "1",
                        OriginalMipFilter = int.TryParse(parts[3], out int filter) ? filter : -1
                    };
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not read importer revert journal: {e.Message}");
            }
            return entries;
        }

        public static void Save(Dictionary<string, Entry> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));
            var lines = new List<string>(entries.Count);
            foreach (var kvp in entries)
            {
                lines.Add($"{kvp.Key}\t{(kvp.Value.StreamingWasOff ? 1 : 0)}\t{(kvp.Value.MipmapsWereOff ? 1 : 0)}\t{kvp.Value.OriginalMipFilter}");
            }
            File.WriteAllLines(JournalPath, lines);
        }

        public static void Clear()
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
        }

        public static bool HasPendingReverts => File.Exists(JournalPath);
    }

    /// <summary>
    /// Reverts any importer changes left pending by a crash, restart, or a build pipeline that did not
    /// fire IPostprocessBuildWithReport (e.g. VRChat SDK asset bundle builds).
    /// </summary>
    [InitializeOnLoad]
    public static class SynImporterRevertOnLoad
    {
        static SynImporterRevertOnLoad()
        {
            EditorApplication.delayCall += RevertIfIdle;
        }

        public static void RevertIfIdle()
        {
            if (!SynImporterRevertJournal.HasPendingReverts) return;

            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer)
            {
                return; // Play Mode exit and post-build hooks handle these cases
            }

            SynEnableMipStreamingPass.RevertImporterChanges();
        }
    }
}
