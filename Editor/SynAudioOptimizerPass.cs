using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimization pass that automatically configures AudioClip compression and load settings based on size.
    /// Ports and integrates the logic from the project's manual AudioAutoOptimizer.
    /// </summary>
    public class SynAudioOptimizerPass : SynOptimizationPass
    {
        public override string Id => "synthos.audio_optimizer";
        public override string Name => "Audio Clip Optimizer";
        public override string Description => "Optimizes AudioClip import settings (Vorbis/Streaming for loops, ADPCM for short hits) to reduce memory usage and build size.";

        public override int Priority => 30; // Run early-mid
        public override string Category => "Audio";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            string scanMode = SynSceneOptimizerSettings.GetString("Audio_ScanMode", "SceneOnly");
            float vorbisQuality = SynSceneOptimizerSettings.GetFloat("Audio_VorbisQuality", 0.7f);
            bool logOptimized = SynSceneOptimizerSettings.GetBool("Audio_LogOptimized", true);
            bool verbose = SynSceneOptimizerSettings.GetBool("EnableVerboseLogging", false);

            var clipsToOptimize = new HashSet<AudioClip>();

            if (scanMode == "EntireProject")
            {
                // Scan all audio clips in the project asset database
                string[] guids = AssetDatabase.FindAssets("t:AudioClip");
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    if (clip != null)
                    {
                        clipsToOptimize.Add(clip);
                    }
                }
            }
            else
            {
                // Scan only audio clips referenced by AudioSources in the scene
                var rootObjects = scene.GetRootGameObjects();
                foreach (var root in rootObjects)
                {
                    var sources = root.GetComponentsInChildren<AudioSource>(true);
                    foreach (var source in sources)
                    {
                        if (source == null || SynProtectionData.IsProtected(source.gameObject))
                        {
                            continue;
                        }
                        if (source.clip != null)
                        {
                            clipsToOptimize.Add(source.clip);
                        }
                    }
                }
            }

            int optimizedCount = 0;
            int skippedCount = 0;
            int errorCount = 0;

            foreach (var clip in clipsToOptimize)
            {
                string path = AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(path) || path.StartsWith("Packages/") || path.StartsWith("Library/"))
                {
                    continue; // Skip packages and built-in clips
                }

                var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer == null)
                {
                    errorCount++;
                    continue;
                }

                // Get file size
                long fileSize = 0;
                try
                {
                    FileInfo fileInfo = new FileInfo(Path.Combine(Application.dataPath, "..", path));
                    if (fileInfo.Exists)
                    {
                        fileSize = fileInfo.Length;
                    }
                }
                catch (Exception e)
                {
                    if (verbose)
                    {
                        Debug.LogWarning($"[AudioOptimizer] Failed to get file size for {clip.name}: {e.Message}");
                    }
                    continue;
                }

                AudioImporterSampleSettings originalSettings = importer.defaultSampleSettings;
                AudioImporterSampleSettings newSettings = originalSettings;

                // Determine target settings based on size
                // LEVEL 1: Big Loops (> 1MB) - Vorbis + Streaming
                if (fileSize > 1024 * 1024)
                {
                    newSettings.loadType = AudioClipLoadType.Streaming;
                    newSettings.compressionFormat = AudioCompressionFormat.Vorbis;
                    newSettings.quality = vorbisQuality;
                    newSettings.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
                    newSettings.sampleRateOverride = 44100;
                    newSettings.preloadAudioData = false;
                }
                // LEVEL 2: Interaction Sounds (100KB - 1MB) - ADPCM + Compressed In Memory
                else if (fileSize > 100 * 1024)
                {
                    newSettings.loadType = AudioClipLoadType.CompressedInMemory;
                    newSettings.compressionFormat = AudioCompressionFormat.ADPCM;
                    newSettings.preloadAudioData = false;
                }
                // LEVEL 3: Quick Hits (< 100KB) - ADPCM + Decompress On Load
                else
                {
                    newSettings.loadType = AudioClipLoadType.DecompressOnLoad;
                    newSettings.compressionFormat = AudioCompressionFormat.ADPCM;
                    newSettings.preloadAudioData = true;
                }

                // Check if changes are needed to avoid redundant re-imports
                if (AreSettingsEqual(originalSettings, newSettings))
                {
                    skippedCount++;
                    continue;
                }

                // Apply changes and reimport
                try
                {
                    importer.defaultSampleSettings = newSettings;
                    importer.SaveAndReimport();
                    optimizedCount++;

                    if (logOptimized)
                    {
                        string sizeText = FormatBytes(fileSize);
                        Debug.Log($"[AudioOptimizer] Optimized clip <b>{clip.name}</b> ({sizeText}). LoadType: {newSettings.loadType}, Format: {newSettings.compressionFormat}, Preload: {newSettings.preloadAudioData}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[AudioOptimizer] Failed to save settings for clip {clip.name}: {e.Message}");
                    errorCount++;
                }
            }

            if (optimizedCount > 0)
            {
                SynPipelineCompactor.LogChange(
                    "Audio Optimizer",
                    string.Format("Optimized {0} clips ({1} already optimized, {2} errors).", optimizedCount, skippedCount, errorCount)
                );
            }
        }

        private bool AreSettingsEqual(AudioImporterSampleSettings a, AudioImporterSampleSettings b)
        {
            return a.loadType == b.loadType &&
                   a.compressionFormat == b.compressionFormat &&
                   Mathf.Approximately(a.quality, b.quality) &&
                   a.sampleRateSetting == b.sampleRateSetting &&
                   a.sampleRateOverride == b.sampleRateOverride &&
                   a.preloadAudioData == b.preloadAudioData;
        }

        private string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            string scanMode = SynSceneOptimizerSettings.GetString("Audio_ScanMode", "SceneOnly");
            int modeIndex = scanMode == "EntireProject" ? 1 : 0;
            string[] modes = { "Scene References Only", "Entire Project" };
            
            int newModeIndex = EditorGUILayout.Popup(new GUIContent("Scan Mode", "Scene References Only: Only optimizes clips currently assigned to AudioSources in the scene. Entire Project: Scans and optimizes every audio file in the project."), modeIndex, modes);
            if (newModeIndex != modeIndex)
            {
                SynSceneOptimizerSettings.SetString("Audio_ScanMode", newModeIndex == 1 ? "EntireProject" : "SceneOnly");
            }

            float quality = SynSceneOptimizerSettings.GetFloat("Audio_VorbisQuality", 0.7f);
            float newQuality = EditorGUILayout.Slider(new GUIContent("Vorbis Quality", "Compression quality for loops / large audio files (Vorbis format)."), quality, 0.1f, 1.0f);
            if (!Mathf.Approximately(newQuality, quality))
            {
                SynSceneOptimizerSettings.SetFloat("Audio_VorbisQuality", newQuality);
            }

            bool logOpt = SynSceneOptimizerSettings.GetBool("Audio_LogOptimized", true);
            bool newLogOpt = EditorGUILayout.Toggle(new GUIContent("Log Optimized Clips", "If checked, prints details about which audio clips were re-imported and optimized to the console."), logOpt);
            if (newLogOpt != logOpt)
            {
                SynSceneOptimizerSettings.SetBool("Audio_LogOptimized", newLogOpt);
            }
        }
    }
}
