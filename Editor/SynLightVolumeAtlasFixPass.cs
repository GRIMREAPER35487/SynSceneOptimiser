using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Keeps the VRC Light Volumes atlas in step with its bakes. Light Volumes only applies new bakes and the
    /// Downscale Volumes setting when the atlas is packed, and a build ships the atlas file as it is, so an atlas
    /// packed before the latest bake (or before Downscale Volumes was changed) silently ships old, full-size data.
    /// Before a VRChat build or entering Play Mode, an out-of-date atlas is re-packed with Light Volumes' own
    /// "Pack Light Volumes" action and the build is started again by the user once it finishes.
    /// </summary>
    [InitializeOnLoad]
    public class SynLightVolumeAtlasFixPass : SynOptimizationPass
    {
        public override string Id => "LightVolumeAtlasFixPass";
        public override string Name => "Light Volumes Atlas Auto Re-pack";
        public override string Description => "Re-packs the VRC Light Volumes atlas before builds and Play Mode when it is older than the volume bakes or does not match the Downscale Volumes setting.";
        public override string Category => "Lighting & Probes";
        public override int Priority => 11;
        public override SynProtectionTargets ProtectionTargets => SynProtectionTargets.None;
        public override string Tab => "General Fixes";
        public override bool RunInPreview => false;

        // A re-pack that leaves the atlas still out of date (e.g. Light Volumes reported an error) must not block
        // every following build, so a build is only stopped once per re-pack attempt.
        private static double lastRepackTime = -1000;
        private const double RepackGraceSeconds = 600;

        static SynLightVolumeAtlasFixPass()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            EditorGUILayout.HelpBox("Light Volumes only applies new bakes and the Downscale Volumes setting when the atlas is packed, and builds ship the atlas file as it is. When the atlas is out of date, this re-packs it before a build or Play Mode; the build is stopped once so you can start it again after packing finishes.", MessageType.Info);

            AtlasStatus status = CheckActiveScene();
            MessageType type = status.State == AtlasState.OutOfDate ? MessageType.Warning : MessageType.None;
            EditorGUILayout.HelpBox(status.Message, type);

            using (new EditorGUI.DisabledScope(status.Manager == null || !CanRepack() || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Re-pack Now")) Repack(status.Manager);
            }
        }

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            // The atlas cannot be packed while building or playing; this only reports what will ship
            AtlasStatus status = Check(scene);
            if (status.State == AtlasState.OutOfDate)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] {Name}: {status.Message}");
                SynPipelineCompactor.LogChange(Name, status.Message);
            }
        }

        // -------------------------------------------------------------------------------------------------------
        // Hooks
        // -------------------------------------------------------------------------------------------------------

        private static bool IsPassEnabled()
        {
            return SynSceneOptimizerSettings.GetBool("Pass_LightVolumeAtlasFixPass_Enabled", true);
        }

        /// <summary>
        /// Runs before a VRChat build or upload. Returns false to stop it while the atlas is re-packed.
        /// </summary>
        internal static bool BeforeBuild()
        {
            if (!IsPassEnabled()) return true;
            AtlasStatus status = CheckActiveScene();

            if (IsRepacking())
            {
                EditorUtility.DisplayDialog("Light Volumes", "The Light Volumes atlas is still being packed. Start the build again when it has finished.", "OK");
                return false;
            }
            if (status.State != AtlasState.OutOfDate || !CanRepack()) return true;
            if (EditorApplication.timeSinceStartup - lastRepackTime < RepackGraceSeconds)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Light Volumes atlas still looks out of date after re-packing ({status.Message}). Building with it as it is; check the Light Volume Manager for errors.");
                return true;
            }

            Repack(status.Manager);
            EditorUtility.DisplayDialog("Light Volumes",
                $"The Light Volumes atlas was out of date:\n\n{status.Message}\n\nIt is being re-packed now (progress in the bottom-right corner). Start the build again when it has finished.", "OK");
            return false;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode || !IsPassEnabled()) return;
            if (!SynSceneOptimizerSettings.GetBool("AutoOptimizePlayMode", true)) return;

            AtlasStatus status = CheckActiveScene();
            if (status.State != AtlasState.OutOfDate || !CanRepack() || IsRepacking()) return;
            if (EditorApplication.timeSinceStartup - lastRepackTime < RepackGraceSeconds) return;

            // Packing needs Edit Mode, so Play Mode is cancelled for this attempt
            EditorApplication.isPlaying = false;
            Debug.LogWarning($"[SYN SCENE OPTIMIZER] Light Volumes atlas was out of date ({status.Message}). Re-packing it; enter Play Mode again when it has finished.");
            EditorApplication.delayCall += () => Repack(status.Manager);
        }

        // -------------------------------------------------------------------------------------------------------
        // Detection
        // -------------------------------------------------------------------------------------------------------

        internal enum AtlasState { NoLightVolumes, UpToDate, OutOfDate }

        internal struct AtlasStatus
        {
            public AtlasState State;
            public string Message;
            public Component Manager;
        }

        private static AtlasStatus CheckActiveScene()
        {
            return Check(SceneManager.GetActiveScene());
        }

        internal static AtlasStatus Check(Scene scene)
        {
            Component manager = FindManager(scene);
            if (manager == null)
            {
                return new AtlasStatus { State = AtlasState.NoLightVolumes, Message = "No Light Volume Manager in this scene." };
            }

            var so = new SerializedObject(manager);
            Texture atlas = so.FindProperty("LightVolumeAtlasBase")?.objectReferenceValue as Texture;
            if (atlas == null) atlas = so.FindProperty("LightVolumeAtlas")?.objectReferenceValue as Texture;
            int downscale = so.FindProperty("DownscaleVolumes")?.intValue ?? 0;

            // Baked source textures of every registered volume
            var sources = new List<Texture3D>();
            SerializedProperty instances = so.FindProperty("LightVolumeInstances");
            for (int i = 0; instances != null && i < instances.arraySize; i++)
            {
                if (!(instances.GetArrayElementAtIndex(i).objectReferenceValue is Component volume)) continue;
                var vso = new SerializedObject(volume);
                if (vso.FindProperty("Bake")?.boolValue == false) continue;
                foreach (string field in new[] { "Texture0", "Texture1", "Texture2" })
                {
                    if (vso.FindProperty(field)?.objectReferenceValue is Texture3D tex) sources.Add(tex);
                }
            }
            if (sources.Count == 0)
            {
                return new AtlasStatus { State = AtlasState.UpToDate, Message = "No baked Light Volumes to pack.", Manager = manager };
            }

            string reason = FindOutOfDateReason(atlas, sources, downscale);
            return reason == null
                ? new AtlasStatus { State = AtlasState.UpToDate, Message = "Light Volumes atlas is up to date.", Manager = manager }
                : new AtlasStatus { State = AtlasState.OutOfDate, Message = reason, Manager = manager };
        }

        private static string FindOutOfDateReason(Texture atlas, List<Texture3D> sources, int downscale)
        {
            if (atlas == null) return "The atlas has not been packed yet.";

            // 1. A volume was baked after the atlas was packed
            string atlasPath = AssetDatabase.GetAssetPath(atlas);
            if (!string.IsNullOrEmpty(atlasPath) && File.Exists(atlasPath))
            {
                DateTime packed = File.GetLastWriteTimeUtc(atlasPath);
                foreach (Texture3D source in sources)
                {
                    string sourcePath = AssetDatabase.GetAssetPath(source);
                    if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) continue;
                    DateTime baked = File.GetLastWriteTimeUtc(sourcePath);
                    if (baked > packed.AddSeconds(5))
                    {
                        return $"The atlas was packed on {packed.ToLocalTime():yyyy-MM-dd HH:mm}, before the volume bake of {baked.ToLocalTime():yyyy-MM-dd HH:mm}.";
                    }
                }
            }

            // 2. The atlas size does not match Downscale Volumes. Each downscale step halves every axis (1/8 of the
            //    voxels); packing adds some padding and gaps, so only a clear mismatch counts.
            if (atlas is Texture3D atlas3D)
            {
                double atlasVoxels = (double)atlas3D.width * atlas3D.height * atlas3D.depth;
                double fullVoxels = 0;
                foreach (Texture3D source in sources) fullVoxels += (double)source.width * source.height * source.depth;

                if (downscale > 0 && atlasVoxels > fullVoxels * 0.5)
                {
                    return $"Downscale Volumes is set to x{1 << downscale}, but the atlas ({atlas3D.width}x{atlas3D.height}x{atlas3D.depth}) was packed at full size.";
                }
                if (downscale == 0 && atlasVoxels < fullVoxels * 0.4)
                {
                    return $"Downscale Volumes is off, but the atlas ({atlas3D.width}x{atlas3D.height}x{atlas3D.depth}) was packed downscaled.";
                }
            }
            return null;
        }

        private static Component FindManager(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour != null && behaviour.GetType().Name == "LightVolumeManager" && !behaviour.CompareTag("EditorOnly")) return behaviour;
                }
            }
            return null;
        }

        // -------------------------------------------------------------------------------------------------------
        // Re-packing through Light Volumes' own "Pack Light Volumes" action
        // -------------------------------------------------------------------------------------------------------

        private static Type backendType;
        private static bool backendSearched;

        private static Type GetBackendType()
        {
            if (backendSearched) return backendType;
            backendSearched = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType("VRCLightVolumes.LightVolumeManagerEditorBackend");
                if (type != null)
                {
                    backendType = type;
                    break;
                }
            }
            return backendType;
        }

        private static MethodInfo GetGenerateAtlasMethod()
        {
            Type type = GetBackendType();
            if (type == null) return null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name == "GenerateAtlas" && method.GetParameters().Length == 1) return method;
            }
            return null;
        }

        private static bool CanRepack()
        {
            return GetGenerateAtlasMethod() != null;
        }

        private static bool IsRepacking()
        {
            Type type = GetBackendType();
            FieldInfo routine = type?.GetField("_atlasRoutine", BindingFlags.Static | BindingFlags.NonPublic);
            return routine != null && routine.GetValue(null) != null;
        }

        private static void Repack(Component manager)
        {
            MethodInfo generate = GetGenerateAtlasMethod();
            if (manager == null || generate == null) return;
            try
            {
                generate.Invoke(null, new object[] { manager });
                lastRepackTime = EditorApplication.timeSinceStartup;
                Debug.Log("[SYN SCENE OPTIMIZER] Re-packing the Light Volumes atlas.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not start Light Volumes atlas packing: {(e.InnerException ?? e).Message}");
            }
        }
    }

    /// <summary>
    /// Runs the Light Volumes atlas check before the VRChat SDK starts a build or upload.
    /// </summary>
    public class SynLightVolumeAtlasBuildCheck : IVRCSDKBuildRequestedCallback
    {
        public int callbackOrder => -100;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
        {
            if (requestedBuildType != VRCSDKRequestedBuildType.Scene) return true;
            return SynLightVolumeAtlasFixPass.BeforeBuild();
        }
    }
}
