using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimizes the audio clips a scene uses without touching the user's files: each clip that needs different
    /// import settings is copied (with its import settings) into the cache, re-imported there with settings chosen
    /// from its length, and every scene reference (AudioSources, Udon/UdonSharp fields) is pointed at the copy.
    /// </summary>
    public class SynAudioOptimizerPass : SynOptimizationPass
    {
        public override string Id => "synthos.audio_optimizer";
        public override string Name => "Audio Clip Optimizer";
        public override string Description => "Picks compression and load settings for the scene's audio clips from their length (streamed Vorbis for music, compressed Vorbis for medium clips, ADPCM for short effects). Works on cached copies, so your audio files are never modified.";

        public override int Priority => 30; // Run early-mid
        public override string Category => "Audio";

        private class PendingAudioCopy
        {
            public AudioClip Source;
            public string CopyPath;
            public AudioImporterSampleSettings Settings;
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            float vorbisQuality = SynSceneOptimizerSettings.GetFloat("Audio_VorbisQuality", 0.7f);
            float streamAbove = SynSceneOptimizerSettings.GetFloat("Audio_StreamAboveSeconds", 30f);
            float decompressBelow = SynSceneOptimizerSettings.GetFloat("Audio_DecompressBelowSeconds", 3f);
            bool logOptimized = SynSceneOptimizerSettings.GetBool("Audio_LogOptimized", true);

            // 1. Every clip the scene can play: AudioSources plus clips referenced by scripts (Udon, UdonSharp, ...)
            var clips = new HashSet<AudioClip>();
            ForEachClipReference(scene, (clip, assign) => clips.Add(clip));
            if (clips.Count == 0) return;

            string importerPlatform = GetImporterPlatformName(SynAssetCache.GetCurrentTargetPlatform());
            var replacements = new Dictionary<AudioClip, AudioClip>();
            var pending = new List<PendingAudioCopy>();
            int alreadyOptimal = 0;

            foreach (AudioClip clip in clips)
            {
                string path = AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(path) || path.StartsWith(SynAssetCache.BaseCachePath)) continue;
                if (!(AssetImporter.GetAtPath(path) is AudioImporter importer)) continue;

                AudioImporterSampleSettings current = importer.ContainsSampleSettingsOverride(importerPlatform)
                    ? importer.GetOverrideSampleSettings(importerPlatform)
                    : importer.defaultSampleSettings;
                AudioImporterSampleSettings target = ChooseSettings(current, clip.length, vorbisQuality, streamAbove, decompressBelow);

                if (AreSettingsEqual(current, target))
                {
                    alreadyOptimal++;
                    continue;
                }

                string hash = SynAssetCache.ComputeCompositeHash(
                    "AudioOpt_v1",
                    SynAssetCache.GetAssetIdentityHash(clip),
                    target.loadType.ToString(),
                    target.compressionFormat.ToString(),
                    target.quality.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    target.sampleRateSetting.ToString(),
                    target.preloadAudioData.ToString());

                if (SynAssetCache.TryGetCachedAsset<AudioClip>(SynAssetCache.AudioCategory, hash, out AudioClip cached))
                {
                    replacements[clip] = cached;
                    continue;
                }

                pending.Add(new PendingAudioCopy
                {
                    Source = clip,
                    CopyPath = SynAssetCache.GetAssetPath(SynAssetCache.AudioCategory, hash, clip.name, Path.GetExtension(path)),
                    Settings = target
                });
            }

            if (pending.Count > 0)
            {
                ImportOptimizedCopies(pending, importerPlatform, replacements);
            }

            if (replacements.Count == 0) return;

            // 2. Point every scene reference at the optimized copies
            int referencesUpdated = 0;
            ForEachClipReference(scene, (clip, assign) =>
            {
                if (replacements.TryGetValue(clip, out AudioClip copy))
                {
                    assign(copy);
                    referencesUpdated++;
                }
            });

            if (logOptimized)
            {
                foreach (var kvp in replacements)
                {
                    var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(kvp.Value)) as AudioImporter;
                    var s = importer != null ? importer.defaultSampleSettings : default;
                    Debug.Log($"[AudioOptimizer] <b>{kvp.Key.name}</b> ({kvp.Key.length:F1}s) -> {s.compressionFormat}, {s.loadType}");
                }
            }

            string msg = $"Optimized {replacements.Count} audio clips ({alreadyOptimal} already optimal) and updated {referencesUpdated} scene references. Source files unchanged.";
            Debug.Log($"[SYN SCENE OPTIMIZER] Audio Optimizer: {msg}");
            SynPipelineCompactor.LogChange("Audio Optimizer", msg);
        }

        /// <summary>
        /// Length-based settings. Long clips (music, ambience) stream from disk so they never sit in RAM; medium
        /// clips stay Vorbis-compressed in memory; short effects are ADPCM decompressed on load for instant playback.
        /// </summary>
        private static AudioImporterSampleSettings ChooseSettings(AudioImporterSampleSettings current, float length, float vorbisQuality, float streamAbove, float decompressBelow)
        {
            var s = current;
            s.sampleRateSetting = AudioSampleRateSetting.OptimizeSampleRate;

            if (length >= streamAbove)
            {
                s.loadType = AudioClipLoadType.Streaming;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = vorbisQuality;
                s.preloadAudioData = false;
            }
            else if (length >= decompressBelow)
            {
                s.loadType = AudioClipLoadType.CompressedInMemory;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = vorbisQuality;
                s.preloadAudioData = true;
            }
            else
            {
                s.loadType = AudioClipLoadType.DecompressOnLoad;
                s.compressionFormat = AudioCompressionFormat.ADPCM;
                s.preloadAudioData = true;
            }
            return s;
        }

        private static bool AreSettingsEqual(AudioImporterSampleSettings a, AudioImporterSampleSettings b)
        {
            return a.loadType == b.loadType &&
                   a.compressionFormat == b.compressionFormat &&
                   (a.compressionFormat != AudioCompressionFormat.Vorbis || Mathf.Approximately(a.quality, b.quality)) &&
                   a.sampleRateSetting == b.sampleRateSetting &&
                   a.preloadAudioData == b.preloadAudioData;
        }

        /// <summary>
        /// Copies the source clips (with their import settings) into the cache, applies the chosen settings and
        /// re-imports them. Runs outside the pipeline's StartAssetEditing batch so the copies load immediately.
        /// </summary>
        private static void ImportOptimizedCopies(List<PendingAudioCopy> pending, string importerPlatform, Dictionary<AudioClip, AudioClip> replacements)
        {
            using (SynAssetDatabaseScope.Suspend())
            {
                string folder = SynAssetCache.GetCategoryPath(SynAssetCache.AudioCategory).TrimEnd('/');
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.Refresh();
                }

                var copied = new List<PendingAudioCopy>();
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var p in pending)
                    {
                        string sourcePath = AssetDatabase.GetAssetPath(p.Source);
                        if (File.Exists(p.CopyPath) || AssetDatabase.CopyAsset(sourcePath, p.CopyPath))
                        {
                            copied.Add(p);
                        }
                        else
                        {
                            Debug.LogWarning($"[AudioOptimizer] Could not copy '{sourcePath}' into the cache. Keeping original.");
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var p in copied)
                    {
                        if (!(AssetImporter.GetAtPath(p.CopyPath) is AudioImporter importer)) continue;
                        importer.defaultSampleSettings = p.Settings;
                        importer.SetOverrideSampleSettings(importerPlatform, p.Settings);
                        importer.SaveAndReimport();
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                foreach (var p in copied)
                {
                    AudioClip copy = AssetDatabase.LoadAssetAtPath<AudioClip>(p.CopyPath);
                    if (copy == null) continue;
                    replacements[p.Source] = copy;
                    SynAssetCache.RecordUsage(p.CopyPath);
                }
            }
        }

        /// <summary>
        /// Visits every AudioClip reference in the scene: AudioSource.clip and any serialized AudioClip field of a
        /// script (UdonBehaviour public variables and UdonSharp fields are both serialized object references).
        /// The callback receives the clip and an action that replaces that reference.
        /// </summary>
        private static void ForEachClipReference(Scene scene, Action<AudioClip, Action<AudioClip>> visit)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (AudioSource source in root.GetComponentsInChildren<AudioSource>(true))
                {
                    if (source == null || source.clip == null || SynProtectionData.IsProtected(source.gameObject)) continue;
                    AudioSource target = source;
                    visit(source.clip, copy => target.clip = copy);
                }

                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || SynProtectionData.IsProtected(behaviour.gameObject)) continue;

                    var serialized = new SerializedObject(behaviour);
                    bool changed = false;
                    SerializedProperty iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (!(iterator.objectReferenceValue is AudioClip clip)) continue;

                        SerializedProperty property = iterator.Copy();
                        visit(clip, copy =>
                        {
                            property.objectReferenceValue = copy;
                            changed = true;
                        });
                    }

                    if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        private static string GetImporterPlatformName(SynTargetPlatform platform)
        {
            switch (platform)
            {
                case SynTargetPlatform.Android: return "Android";
                case SynTargetPlatform.iOS: return "iOS";
                default: return "Standalone";
            }
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Optimizes the clips this scene uses (AudioSources and Udon/UdonSharp references). Optimized copies are kept in the optimizer cache; your audio files are never modified.", MessageType.Info);

            float streamAbove = SynSceneOptimizerSettings.GetFloat("Audio_StreamAboveSeconds", 30f);
            float newStreamAbove = Mathf.Max(1f, EditorGUILayout.FloatField(new GUIContent("Stream Clips Longer Than (s)", "Clips at least this long (music, ambience) are streamed from disk with Vorbis compression so they never sit in memory."), streamAbove));
            if (!Mathf.Approximately(newStreamAbove, streamAbove)) SynSceneOptimizerSettings.SetFloat("Audio_StreamAboveSeconds", newStreamAbove);

            float decompressBelow = SynSceneOptimizerSettings.GetFloat("Audio_DecompressBelowSeconds", 3f);
            float newDecompressBelow = Mathf.Clamp(EditorGUILayout.FloatField(new GUIContent("Decompress Clips Shorter Than (s)", "Clips shorter than this (effects, UI sounds) use ADPCM decompressed on load for instant, low-CPU playback. Clips in between stay Vorbis-compressed in memory."), decompressBelow), 0f, newStreamAbove);
            if (!Mathf.Approximately(newDecompressBelow, decompressBelow)) SynSceneOptimizerSettings.SetFloat("Audio_DecompressBelowSeconds", newDecompressBelow);

            float quality = SynSceneOptimizerSettings.GetFloat("Audio_VorbisQuality", 0.7f);
            float newQuality = EditorGUILayout.Slider(new GUIContent("Vorbis Quality", "Compression quality for streamed and compressed (Vorbis) clips."), quality, 0.1f, 1.0f);
            if (!Mathf.Approximately(newQuality, quality)) SynSceneOptimizerSettings.SetFloat("Audio_VorbisQuality", newQuality);

            bool logOpt = SynSceneOptimizerSettings.GetBool("Audio_LogOptimized", true);
            bool newLogOpt = EditorGUILayout.Toggle(new GUIContent("Log Optimized Clips", "Print which clips were optimized and the settings chosen."), logOpt);
            if (newLogOpt != logOpt) SynSceneOptimizerSettings.SetBool("Audio_LogOptimized", newLogOpt);

            if (GUILayout.Button(new GUIContent("Reset Audio Import Settings...", "Put audio clips back to Unity's default import settings, e.g. to undo changes older versions of this pass made to your files.")))
            {
                SynAudioResetWindow.Open();
            }
        }
    }
}
