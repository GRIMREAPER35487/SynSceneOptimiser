using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    public class SynVRAMAnalyzerWindow : EditorWindow
    {
        private enum ScanTarget
        {
            ActiveScene,
            BakedPlayMode,
            BakedBuild
        }

        // Window state is serialized so a report survives domain reloads (entering Play Mode, script recompiles)
        [SerializeField] private ScanTarget currentTarget = ScanTarget.ActiveScene;
        [SerializeField] private EstimatePlatform estimatePlatform = EstimatePlatform.ActiveBuildTarget;
        private string activeScenePath = "";

        // Scan Results
        [SerializeField] private bool hasScanned = false;
        [SerializeField] private string scannedSceneName = "";
        [SerializeField] private string reportTimestamp = "";
        [SerializeField] private string scannedPlatform = "";
        [SerializeField] private long totalVramBytes = 0;
        [SerializeField] private long textureVramBytes = 0;
        [SerializeField] private long meshVramBytes = 0;
        [SerializeField] private long environmentalVramBytes = 0;

        // Details Lists
        [SerializeField] private List<TextureReportEntry> textureReport = new List<TextureReportEntry>();
        [SerializeField] private List<MeshReportEntry> meshReport = new List<MeshReportEntry>();
        [SerializeField] private List<EnvReportEntry> envReport = new List<EnvReportEntry>();
        [SerializeField] private List<WarningEntry> warningsList = new List<WarningEntry>();

        // Selected item for reference tracing
        private object selectedReportEntry = null;
        private List<string> selectedReferences = new List<string>();
        private List<string> deepProjectReferences = new List<string>();

        // GUI State
        [SerializeField] private int activeTab = 0; // 0: Overview, 1: Textures, 2: Meshes, 3: Environment, 4: Warnings
        [SerializeField] private string searchText = "";
        private Vector2 scrollPos = Vector2.zero;
        private Vector2 refScrollPos = Vector2.zero;
        private float viewportHeight = 400f;

        // Sort States
        [SerializeField] private string textureSortColumn = "Size";
        [SerializeField] private bool textureSortAscending = false;
        [SerializeField] private string meshSortColumn = "Size";
        [SerializeField] private bool meshSortAscending = false;
        [SerializeField] private string envSortColumn = "Size";
        [SerializeField] private bool envSortAscending = false;

        // Baseline Compare State
        [SerializeField] private long baselineVramBytes = -1;

        // Data structures for Window State (survives domain reload)
        [System.Serializable]
        private class TextureReportEntry
        {
            public Texture TextureAsset;
            public string Name;
            public int Width;
            public int Height;
            public string Format;
            public bool HasMipmaps;
            public long VramSize;
            public List<Material> ReferencingMaterials = new List<Material>();
            public List<string> ReferencingGameObjectPaths = new List<string>();
        }

        [System.Serializable]
        private class MeshReportEntry
        {
            public Mesh MeshAsset;
            public string Name;
            public int VertexCount;
            public int SubmeshCount;
            public string IndexFormat;
            public long VramSize;
            public List<string> ReferencingGameObjectPaths = new List<string>();
        }

        [System.Serializable]
        private class EnvReportEntry
        {
            public UnityEngine.Object Asset;
            public string Name;
            public string Type;
            public string Details;
            public long VramSize;
            public List<string> References = new List<string>();
        }

        [System.Serializable]
        private class WarningEntry
        {
            public UnityEngine.Object Asset;
            public string Message;
            public string Severity; // "Error", "Warning", "Info"
        }

        // Serialization Structures for JSON Cache
        [System.Serializable]
        private class SerializableTextureEntry
        {
            public string AssetPath;
            public string Name;
            public int Width;
            public int Height;
            public string Format;
            public bool HasMipmaps;
            public long VramSize;
            public List<string> ReferencingMaterialPaths = new List<string>();
            public List<string> ReferencingGameObjectPaths = new List<string>();
        }

        [System.Serializable]
        private class SerializableMeshEntry
        {
            public string AssetPath;
            public string Name;
            public int VertexCount;
            public int SubmeshCount;
            public string IndexFormat;
            public long VramSize;
            public List<string> ReferencingGameObjectPaths = new List<string>();
        }

        [System.Serializable]
        private class SerializableEnvEntry
        {
            public string AssetPath;
            public string Name;
            public string Type;
            public string Details;
            public long VramSize;
            public List<string> References = new List<string>();
        }

        [System.Serializable]
        private class SerializableWarningEntry
        {
            public string AssetPath;
            public string Message;
            public string Severity;
        }

        [System.Serializable]
        private class SerializableScanReport
        {
            public string ScannedSceneName;
            public string Timestamp;
            public long TotalVramBytes;
            public long TextureVramBytes;
            public long MeshVramBytes;
            public long EnvironmentalVramBytes;
            public List<SerializableTextureEntry> Textures = new List<SerializableTextureEntry>();
            public List<SerializableMeshEntry> Meshes = new List<SerializableMeshEntry>();
            public List<SerializableEnvEntry> EnvAssets = new List<SerializableEnvEntry>();
            public List<SerializableWarningEntry> Warnings = new List<SerializableWarningEntry>();
        }

        [MenuItem("Window/Synthos/VRAM Analyzer")]
        public static void ShowWindow()
        {
            var window = GetWindow<SynVRAMAnalyzerWindow>("VRAM Analyzer");
            window.minSize = new Vector2(550, 600);
            window.Show();
        }

        private void OnEnable()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid())
            {
                activeScenePath = activeScene.path;
            }
        }

        private void OnGUI()
        {
            // Title Header with Premium Aesthetics styling
            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("SYNTHOS VRAM ANALYZER", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            // Controls Panel
            GUILayout.BeginVertical(EditorStyles.helpBox);
            
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Scan Target Scene:", GUILayout.Width(130));
            currentTarget = (ScanTarget)EditorGUILayout.EnumPopup(currentTarget);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent("Estimate Memory For:", "Active Build Target: exact sizes from the textures as currently imported. PC / Quest: sizes estimated from each texture's import settings for that platform, so Quest memory can be checked while working on PC."), GUILayout.Width(130));
            var newPlatform = (EstimatePlatform)EditorGUILayout.EnumPopup(estimatePlatform);
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            string targetPath = GetTargetPath(currentTarget);
            if (newPlatform != estimatePlatform)
            {
                estimatePlatform = newPlatform;
                // Sizes depend on the platform, so a live report is rescanned straight away
                if (hasScanned && currentTarget == ScanTarget.ActiveScene && !EditorApplication.isPlaying) ExecuteLiveScan(targetPath);
            }

            string fileStatusMessage = "";
            bool canScan = true;

            if (EditorApplication.isPlaying && currentTarget != ScanTarget.ActiveScene)
            {
                fileStatusMessage = "Cannot scan closed scenes in Play Mode. Select 'Active Scene' to profile the running playmode scene.";
                canScan = false;
            }
            else if (currentTarget != ScanTarget.ActiveScene)
            {
                string cachedReportPath = GetCachedReportPath(currentTarget);
                if (File.Exists(cachedReportPath))
                {
                    fileStatusMessage = $"Found cached VRAM report from compiled scene run.";
                }
                else
                {
                    fileStatusMessage = "No cached VRAM report found for this target. Run play-mode or build first.";
                    canScan = false;
                }
            }
            else
            {
                fileStatusMessage = "Scanning the current active editor scene.";
            }

            if (!string.IsNullOrEmpty(fileStatusMessage))
            {
                GUIStyle labelStyle = new GUIStyle(EditorStyles.miniLabel);
                labelStyle.normal.textColor = canScan ? new Color(0.1f, 0.6f, 0.1f) : new Color(0.8f, 0.2f, 0.2f);
                EditorGUILayout.LabelField(fileStatusMessage, labelStyle);
            }

            GUILayout.Space(5);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Record Baseline VRAM", GUILayout.Height(30)))
            {
                RecordBaseline();
            }
            EditorGUI.BeginDisabledGroup(!canScan);
            GUI.backgroundColor = new Color(0.3f, 0.6f, 0.9f);
            if (GUILayout.Button("SCAN VRAM", GUILayout.Height(30)))
            {
                if (currentTarget == ScanTarget.ActiveScene)
                {
                    ExecuteLiveScan(targetPath);
                }
                else
                {
                    ExecuteLoadCached(GetCachedReportPath(currentTarget));
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!hasScanned);
            if (GUILayout.Button("Copy Page Details", GUILayout.Height(30), GUILayout.Width(130)))
            {
                CopyActivePageToClipboard();
            }
            EditorGUI.EndDisabledGroup();

            GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
            if (GUILayout.Button("Clear Data & Cache", GUILayout.Height(30), GUILayout.Width(130)))
            {
                if (EditorUtility.DisplayDialog("Clear VRAM Data & Cache?", "Are you sure you want to reset all stored scan data, delete cached VRAM reports, and clean up transient cache files?", "Clear", "Cancel"))
                {
                    ClearStoredData();
                }
            }
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            EditorGUILayout.Space(5);

            if (!hasScanned)
            {
                GUILayout.Label("No VRAM report loaded. Choose a target scene and click 'SCAN VRAM' to profile.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            // Overview Summary Dashboard
            DrawSummaryDashboard();

            EditorGUILayout.Space(5);

            // Tab Buttons
            string[] tabs = { "Overview", $"Textures ({textureReport.Count})", $"Meshes ({meshReport.Count})", $"Environment ({envReport.Count})", $"Warnings ({warningsList.Count})" };
            activeTab = GUILayout.Toolbar(activeTab, tabs);

            EditorGUILayout.Space(5);

            // Main Data Table (list headers stay above the scrolling rows)
            DrawTabHeader();
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));
            switch (activeTab)
            {
                case 0:
                    DrawOverviewTab();
                    break;
                case 1:
                    DrawTexturesTab();
                    break;
                case 2:
                    DrawMeshesTab();
                    break;
                case 3:
                    DrawEnvironmentTab();
                    break;
                case 4:
                    DrawWarningsTab();
                    break;
            }
            EditorGUILayout.EndScrollView();
            if (Event.current.type == EventType.Repaint)
            {
                viewportHeight = GUILayoutUtility.GetLastRect().height;
            }

            // Bottom Reference Panel
            DrawReferenceTracingPanel();
        }

        private string GetTargetPath(ScanTarget target)
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || string.IsNullOrEmpty(activeScene.path))
            {
                return "";
            }

            string baseScenePath = activeScene.path;
            baseScenePath = baseScenePath.Replace("_SynBakedPlayMode.unity", ".unity");
            baseScenePath = baseScenePath.Replace("_SynBakedBuild.unity", ".unity");

            if (target == ScanTarget.BakedPlayMode)
            {
                return baseScenePath.Replace(".unity", "_SynBakedPlayMode.unity");
            }
            else if (target == ScanTarget.BakedBuild)
            {
                return baseScenePath.Replace(".unity", "_SynBakedBuild.unity");
            }

            return baseScenePath;
        }

        private string GetCachedReportPath(ScanTarget target)
        {
            if (target == ScanTarget.BakedPlayMode)
            {
                return SynAssetCache.LastPlayModeVRAMReportPath;
            }
            else if (target == ScanTarget.BakedBuild)
            {
                return SynAssetCache.LastBuildVRAMReportPath;
            }
            return "";
        }

        private void RecordBaseline()
        {
            string targetPath = GetTargetPath(ScanTarget.ActiveScene);
            if (string.IsNullOrEmpty(targetPath)) return;

            ScanResults baseline = ProfileScene(targetPath);
            baselineVramBytes = baseline.TotalBytes;
            Debug.Log($"[SYN VRAM ANALYZER] Recorded baseline VRAM footprint: {FormatBytes(baselineVramBytes)}");
        }

        private void ClearStoredData()
        {
            hasScanned = false;
            scannedSceneName = "";
            reportTimestamp = "";
            totalVramBytes = 0;
            textureVramBytes = 0;
            meshVramBytes = 0;
            environmentalVramBytes = 0;
            baselineVramBytes = -1;
            selectedReportEntry = null;
            selectedReferences.Clear();

            textureReport.Clear();
            meshReport.Clear();
            envReport.Clear();
            warningsList.Clear();

            string playModePath = GetCachedReportPath(ScanTarget.BakedPlayMode);
            string buildPath = GetCachedReportPath(ScanTarget.BakedBuild);

            try
            {
                if (!string.IsNullOrEmpty(playModePath) && File.Exists(playModePath))
                {
                    File.Delete(playModePath);
                    if (File.Exists(playModePath + ".meta")) File.Delete(playModePath + ".meta");
                }
                if (!string.IsNullOrEmpty(buildPath) && File.Exists(buildPath))
                {
                    File.Delete(buildPath);
                    if (File.Exists(buildPath + ".meta")) File.Delete(buildPath + ".meta");
                }

                // Flush editor transient cache to clean up any leftover material or mesh assets on disk
                SynAssetRegistry.FlushTransientCache();
                SynSceneQuery.ClearCache();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SYN VRAM ANALYZER] Failed to delete cache files: " + e.Message);
            }

            AssetDatabase.Refresh();
            Debug.Log("[SYN VRAM ANALYZER] Cleared all stored scan data, deleted cached reports, and flushed transient cache.");
        }

        private void CopyActivePageToClipboard()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            switch (activeTab)
            {
                case 0: // Overview
                    sb.AppendLine($"VRAM Analyzer Overview - Scene: {scannedSceneName}");
                    sb.AppendLine($"Timestamp: {reportTimestamp}");
                    sb.AppendLine($"Total VRAM: {FormatBytes(totalVramBytes)}");
                    sb.AppendLine($"Texture VRAM: {FormatBytes(textureVramBytes)}");
                    sb.AppendLine($"Mesh VRAM: {FormatBytes(meshVramBytes)}");
                    sb.AppendLine($"Environment VRAM: {FormatBytes(environmentalVramBytes)}");
                    if (baselineVramBytes > 0)
                    {
                        sb.AppendLine($"Baseline VRAM: {FormatBytes(baselineVramBytes)}");
                        sb.AppendLine($"VRAM Saved: {FormatBytes(baselineVramBytes - totalVramBytes)}");
                    }
                    break;

                case 1: // Textures
                    sb.AppendLine("VRAM Analyzer - Textures List");
                    sb.AppendLine("Name\tResolution\tFormat\tMips\tVRAM Size\tPath");
                    foreach (var tex in textureReport)
                    {
                        sb.AppendLine($"{tex.Name}\t{tex.Width}x{tex.Height}\t{tex.Format}\t{(tex.HasMipmaps ? "Yes" : "No")}\t{FormatBytes(tex.VramSize)}\t{AssetDatabase.GetAssetPath(tex.TextureAsset)}");
                    }
                    break;

                case 2: // Meshes
                    sb.AppendLine("VRAM Analyzer - Meshes List");
                    sb.AppendLine("Name\tVertices\tSubmeshes\tIndex Format\tVRAM Size\tPath");
                    foreach (var m in meshReport)
                    {
                        sb.AppendLine($"{m.Name}\t{m.VertexCount}\t{m.SubmeshCount}\t{m.IndexFormat}\t{FormatBytes(m.VramSize)}\t{AssetDatabase.GetAssetPath(m.MeshAsset)}");
                    }
                    break;

                case 3: // Lightmaps & Probes (Environment)
                    sb.AppendLine("VRAM Analyzer - Environment Assets List");
                    sb.AppendLine("Name\tType\tDetails\tVRAM Size\tPath\tUsed By");
                    foreach (var env in envReport)
                    {
                        sb.AppendLine($"{env.Name}\t{env.Type}\t{env.Details}\t{FormatBytes(env.VramSize)}\t{AssetDatabase.GetAssetPath(env.Asset)}\t{string.Join("; ", env.References)}");
                    }
                    break;

                case 4: // Warnings
                    sb.AppendLine("VRAM Analyzer - Warnings List");
                    sb.AppendLine("Severity\tAsset\tMessage\tPath");
                    foreach (var warn in warningsList)
                    {
                        sb.AppendLine($"{warn.Severity}\t{(warn.Asset != null ? warn.Asset.name : "N/A")}\t{warn.Message}\t{(warn.Asset != null ? AssetDatabase.GetAssetPath(warn.Asset) : "N/A")}");
                    }
                    break;
            }

            GUIUtility.systemCopyBuffer = sb.ToString();
            Debug.Log($"[SYN VRAM ANALYZER] Copied {GetTabName(activeTab)} details to clipboard.");
        }

        private string GetTabName(int tab)
        {
            switch (tab)
            {
                case 0: return "Overview";
                case 1: return "Textures";
                case 2: return "Meshes";
                case 3: return "Environment";
                case 4: return "Warnings";
                default: return "Details";
            }
        }

        private void ExecuteLiveScan(string scenePath)
        {
            scannedSceneName = Path.GetFileNameWithoutExtension(scenePath);
            reportTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ScanResults results = ProfileScene(scenePath, showProgress: true);
            scannedPlatform = DescribeEstimatePlatform();

            totalVramBytes = results.TotalBytes;
            textureVramBytes = results.TextureBytes;
            meshVramBytes = results.MeshBytes;
            environmentalVramBytes = results.EnvBytes;

            textureReport = results.Textures;
            meshReport = results.Meshes;
            envReport = results.EnvAssets;
            warningsList = results.Warnings;

            SortTextures();
            SortMeshes();
            SortEnvironment();
            selectedReportEntry = null;
            selectedReferences.Clear();

            hasScanned = true;
        }

        private string DescribeEstimatePlatform()
        {
            bool activeIsMobile = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;
            switch (estimatePlatform)
            {
                case EstimatePlatform.PC: return activeIsMobile ? "PC (estimated from import settings)" : "PC";
                case EstimatePlatform.Quest: return activeIsMobile ? "Quest" : "Quest (estimated from import settings)";
                default: return activeIsMobile ? "Quest (active build target)" : "PC (active build target)";
            }
        }

        private void ExecuteLoadCached(string cachedReportPath)
        {
            if (!File.Exists(cachedReportPath)) return;

            try
            {
                string json = File.ReadAllText(cachedReportPath);
                var report = JsonUtility.FromJson<SerializableScanReport>(json);

                scannedSceneName = report.ScannedSceneName + " (Optimized)";
                reportTimestamp = report.Timestamp;
                scannedPlatform = "Build target of that run";
                totalVramBytes = report.TotalVramBytes;
                textureVramBytes = report.TextureVramBytes;
                meshVramBytes = report.MeshVramBytes;
                environmentalVramBytes = report.EnvironmentalVramBytes;

                textureReport.Clear();
                foreach (var sTex in report.Textures)
                {
                    var entry = new TextureReportEntry
                    {
                        TextureAsset = AssetDatabase.LoadAssetAtPath<Texture>(sTex.AssetPath),
                        Name = sTex.Name,
                        Width = sTex.Width,
                        Height = sTex.Height,
                        Format = sTex.Format,
                        HasMipmaps = sTex.HasMipmaps,
                        VramSize = sTex.VramSize,
                        ReferencingGameObjectPaths = sTex.ReferencingGameObjectPaths
                    };
                    foreach (var matPath in sTex.ReferencingMaterialPaths)
                    {
                        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                        if (mat != null) entry.ReferencingMaterials.Add(mat);
                    }
                    textureReport.Add(entry);
                }

                meshReport.Clear();
                foreach (var sMesh in report.Meshes)
                {
                    var entry = new MeshReportEntry
                    {
                        MeshAsset = AssetDatabase.LoadAssetAtPath<Mesh>(sMesh.AssetPath),
                        Name = sMesh.Name,
                        VertexCount = sMesh.VertexCount,
                        SubmeshCount = sMesh.SubmeshCount,
                        IndexFormat = sMesh.IndexFormat,
                        VramSize = sMesh.VramSize,
                        ReferencingGameObjectPaths = sMesh.ReferencingGameObjectPaths
                    };
                    meshReport.Add(entry);
                }

                envReport.Clear();
                foreach (var sEnv in report.EnvAssets)
                {
                    var entry = new EnvReportEntry
                    {
                        Asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sEnv.AssetPath),
                        Name = sEnv.Name,
                        Type = sEnv.Type,
                        Details = sEnv.Details,
                        VramSize = sEnv.VramSize,
                        References = sEnv.References ?? new List<string>()
                    };
                    envReport.Add(entry);
                }

                warningsList.Clear();
                foreach (var sWarn in report.Warnings)
                {
                    var entry = new WarningEntry
                    {
                        Asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sWarn.AssetPath),
                        Message = sWarn.Message,
                        Severity = sWarn.Severity
                    };
                    warningsList.Add(entry);
                }

                SortTextures();
                SortMeshes();
                SortEnvironment();
                selectedReportEntry = null;
                selectedReferences.Clear();

                hasScanned = true;
            }
            catch (Exception e)
            {
                Debug.LogError("[SYN VRAM ANALYZER] Failed to load cached report JSON: " + e.Message);
            }
        }

        /// <summary>
        /// Measures the active scene's VRAM without opening the window.
        /// </summary>
        public static SynVramTotals MeasureActiveScene()
        {
            var window = CreateInstance<SynVRAMAnalyzerWindow>();
            try
            {
                ScanResults results = window.ProfileScene(SceneManager.GetActiveScene().path);
                return new SynVramTotals
                {
                    Total = results.TotalBytes,
                    Textures = results.TextureBytes,
                    Meshes = results.MeshBytes,
                    Environment = results.EnvBytes
                };
            }
            finally
            {
                DestroyImmediate(window);
            }
        }

        public static void SaveSceneVRAMReport(Scene scene, string savePath)
        {
            // Create a transient instance in memory to scan without popping up the window or stealing focus
            var window = CreateInstance<SynVRAMAnalyzerWindow>();
            try
            {
                ScanResults results = window.ProfileScene(scene.path);
                
                var report = new SerializableScanReport
                {
                    ScannedSceneName = Path.GetFileNameWithoutExtension(scene.path),
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    TotalVramBytes = results.TotalBytes,
                    TextureVramBytes = results.TextureBytes,
                    MeshVramBytes = results.MeshBytes,
                    EnvironmentalVramBytes = results.EnvBytes
                };

                foreach (var tex in results.Textures)
                {
                    var sTex = new SerializableTextureEntry
                    {
                        AssetPath = AssetDatabase.GetAssetPath(tex.TextureAsset),
                        Name = tex.Name,
                        Width = tex.Width,
                        Height = tex.Height,
                        Format = tex.Format,
                        HasMipmaps = tex.HasMipmaps,
                        VramSize = tex.VramSize
                    };
                    foreach (var mat in tex.ReferencingMaterials)
                    {
                        if (mat != null) sTex.ReferencingMaterialPaths.Add(AssetDatabase.GetAssetPath(mat));
                    }
                    foreach (var goPath in tex.ReferencingGameObjectPaths)
                    {
                        sTex.ReferencingGameObjectPaths.Add(goPath);
                    }
                    report.Textures.Add(sTex);
                }

                foreach (var m in results.Meshes)
                {
                    var sMesh = new SerializableMeshEntry
                    {
                        AssetPath = AssetDatabase.GetAssetPath(m.MeshAsset),
                        Name = m.Name,
                        VertexCount = m.VertexCount,
                        SubmeshCount = m.SubmeshCount,
                        IndexFormat = m.IndexFormat,
                        VramSize = m.VramSize
                    };
                    foreach (var goPath in m.ReferencingGameObjectPaths)
                    {
                        sMesh.ReferencingGameObjectPaths.Add(goPath);
                    }
                    report.Meshes.Add(sMesh);
                }

                foreach (var env in results.EnvAssets)
                {
                    var sEnv = new SerializableEnvEntry
                    {
                        AssetPath = AssetDatabase.GetAssetPath(env.Asset),
                        Name = env.Name,
                        Type = env.Type,
                        Details = env.Details,
                        VramSize = env.VramSize,
                        References = new List<string>(env.References)
                    };
                    report.EnvAssets.Add(sEnv);
                }

                foreach (var warn in results.Warnings)
                {
                    var sWarn = new SerializableWarningEntry
                    {
                        AssetPath = AssetDatabase.GetAssetPath(warn.Asset),
                        Message = warn.Message,
                        Severity = warn.Severity
                    };
                    report.Warnings.Add(sWarn);
                }

                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonUtility.ToJson(report, true);
                File.WriteAllText(savePath, json);
            }
            finally
            {
                DestroyImmediate(window);
            }
        }

        private void DrawSummaryDashboard()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("VRAM Scan Report:", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"Scene: {scannedSceneName}    Platform: {scannedPlatform}    ({reportTimestamp})", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Total Est. VRAM:", GUILayout.Width(110));
            GUIStyle vramStyle = new GUIStyle(EditorStyles.boldLabel);
            vramStyle.fontSize = 16;
            vramStyle.normal.textColor = new Color(0.9f, 0.4f, 0.1f);
            GUILayout.Label(FormatBytes(totalVramBytes), vramStyle);
            
            if (baselineVramBytes > 0)
            {
                long savings = baselineVramBytes - totalVramBytes;
                if (savings > 0)
                {
                    GUIStyle savingsStyle = new GUIStyle(EditorStyles.boldLabel);
                    savingsStyle.normal.textColor = new Color(0.1f, 0.6f, 0.1f);
                    GUILayout.Label($"(Saved {FormatBytes(savings)} compared to baseline!)", savingsStyle);
                }
                else if (savings < 0)
                {
                    GUIStyle savingsStyle = new GUIStyle(EditorStyles.boldLabel);
                    savingsStyle.normal.textColor = new Color(0.8f, 0.2f, 0.2f);
                    GUILayout.Label($"(+{FormatBytes(-savings)} VRAM difference)", savingsStyle);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            // Progress bar style breakdown
            float total = totalVramBytes > 0 ? totalVramBytes : 1;
            float texPct = (float)textureVramBytes / total;
            float meshPct = (float)meshVramBytes / total;
            float envPct = (float)environmentalVramBytes / total;

            Rect rect = GUILayoutUtility.GetRect(18, 18, GUILayout.ExpandWidth(true));
            float width = rect.width;
            
            Rect texRect = new Rect(rect.x, rect.y, width * texPct, rect.height);
            Rect meshRect = new Rect(texRect.xMax, rect.y, width * meshPct, rect.height);
            Rect envRect = new Rect(meshRect.xMax, rect.y, width * envPct, rect.height);

            EditorGUI.DrawRect(texRect, new Color(0.3f, 0.6f, 0.9f));
            EditorGUI.DrawRect(meshRect, new Color(0.2f, 0.8f, 0.5f));
            EditorGUI.DrawRect(envRect, new Color(0.8f, 0.7f, 0.2f));

            GUILayout.BeginHorizontal();
            GUILayout.Label($"■ Textures: {FormatBytes(textureVramBytes)} ({texPct * 100:F1}%)", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"■ Meshes: {FormatBytes(meshVramBytes)} ({meshPct * 100:F1}%)", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"■ Environment (lighting, probes, skybox): {FormatBytes(environmentalVramBytes)} ({envPct * 100:F1}%)", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private void DrawOverviewTab()
        {
            GUILayout.Label("VRAM Optimization Summary", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This VRAM footprint represents the estimated video memory occupied by scene assets. " +
                "In VRChat, Quest avatars and worlds are strictly limited by physical mobile hardware memory limits. " +
                "Large uncompressed textures and extremely detailed meshes will trigger client lag and out-of-memory crashes.",
                MessageType.Info
            );

            GUILayout.Space(5);

            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Optimization Quick Stats:", EditorStyles.boldLabel);
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("Unique Textures Reference Count");
            GUILayout.FlexibleSpace();
            GUILayout.Label(textureReport.Count.ToString());
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Unique Mesh Assets Count");
            GUILayout.FlexibleSpace();
            GUILayout.Label(meshReport.Count.ToString());
            GUILayout.EndHorizontal();
            
            int uncompressedTexturesCount = 0;
            int missingMipmapsCount = 0;
            int largeTexturesCount = 0;
            foreach (var tex in textureReport)
            {
                if (IsUncompressedFormat(tex.Format)) uncompressedTexturesCount++;
                if (!tex.HasMipmaps) missingMipmapsCount++;
                if (tex.Width >= 2048 || tex.Height >= 2048) largeTexturesCount++;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Uncompressed Textures (High Cost)");
            GUILayout.FlexibleSpace();
            GUILayout.Label(uncompressedTexturesCount.ToString());
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Textures missing Mipmaps");
            GUILayout.FlexibleSpace();
            GUILayout.Label(missingMipmapsCount.ToString());
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("2K or Larger High-Resolution Textures");
            GUILayout.FlexibleSpace();
            GUILayout.Label(largeTexturesCount.ToString());
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Tables. Rows have a fixed height and only the rows inside the visible part of the list are drawn, so
        // scenes with thousands of assets stay responsive.
        // ---------------------------------------------------------------------------------------------------------

        private const float RowHeight = 20f;
        private static readonly Color SelectedRowColor = new Color(0.24f, 0.48f, 0.90f, 0.35f);
        private static readonly Color StripeRowColor = new Color(0f, 0f, 0f, 0.07f);
        private static readonly Color DangerColor = new Color(0.90f, 0.25f, 0.20f);
        private static readonly Color CautionColor = new Color(0.95f, 0.70f, 0.15f);
        private static GUIStyle rowLabelStyle;
        private static GUIStyle headerStyle;

        private static void EnsureStyles()
        {
            if (rowLabelStyle != null) return;
            rowLabelStyle = new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip, padding = new RectOffset(2, 2, 2, 2) };
            headerStyle = new GUIStyle(EditorStyles.boldLabel) { clipping = TextClipping.Clip, padding = new RectOffset(2, 2, 2, 2) };
        }

        private bool MatchesSearch(string name)
        {
            return string.IsNullOrEmpty(searchText) || (name != null && name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// The first column takes the remaining width; the others have the given widths, in order, on the right.
        /// </summary>
        private static Rect[] SplitColumns(Rect row, params float[] fixedWidths)
        {
            var columns = new Rect[fixedWidths.Length + 1];
            float x = row.xMax;
            for (int i = fixedWidths.Length - 1; i >= 0; i--)
            {
                x -= fixedWidths[i];
                columns[i + 1] = new Rect(x, row.y, fixedWidths[i], row.height);
            }
            columns[0] = new Rect(row.x, row.y, Mathf.Max(60f, x - row.x - 4f), row.height);
            return columns;
        }

        /// <summary>
        /// Header row above the list, outside the scroll view, narrowed by the scrollbar so columns line up.
        /// </summary>
        private Rect[] DrawHeaderRow(params float[] fixedWidths)
        {
            Rect row = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.ExpandWidth(true));
            row.width -= GUI.skin.verticalScrollbar.fixedWidth;
            return SplitColumns(row, fixedWidths);
        }

        private bool HeaderButton(Rect rect, string label, string column, string sortColumn, bool ascending)
        {
            string arrow = column == sortColumn ? (ascending ? " ▲" : " ▼") : "";
            return GUI.Button(rect, label + arrow, headerStyle);
        }

        private void DrawVirtualRows<T>(List<T> rows, Action<Rect, T, int> drawRow)
        {
            Rect area = GUILayoutUtility.GetRect(0, Mathf.Max(RowHeight, rows.Count * RowHeight), GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Layout) return;

            int first = Mathf.Max(0, Mathf.FloorToInt((scrollPos.y - area.y) / RowHeight));
            int last = Mathf.Min(rows.Count - 1, first + Mathf.CeilToInt(viewportHeight / RowHeight) + 1);
            for (int i = first; i <= last; i++)
            {
                drawRow(new Rect(area.x, area.y + i * RowHeight, area.width, RowHeight), rows[i], i);
            }
        }

        private static void DrawRowBackground(Rect row, int index, bool selected)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (selected) EditorGUI.DrawRect(row, SelectedRowColor);
            else if (index % 2 == 1) EditorGUI.DrawRect(row, StripeRowColor);
        }

        /// <summary>
        /// Small coloured square at the left of a name cell; the cell is narrowed past it.
        /// </summary>
        private static Rect DrawMarker(Rect cell, Color color)
        {
            if (color.a > 0f && Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(cell.x + 3f, cell.y + (cell.height - 7f) / 2f, 7f, 7f), color);
            }
            cell.xMin += 13f;
            return cell;
        }

        private void DrawTabHeader()
        {
            EnsureStyles();
            if (activeTab < 1 || activeTab > 3) return;

            GUILayout.BeginHorizontal();
            searchText = EditorGUILayout.TextField(searchText, EditorStyles.toolbarSearchField);
            GUILayout.EndHorizontal();

            switch (activeTab)
            {
                case 1:
                {
                    Rect[] c = DrawHeaderRow(90, 120, 44, 80);
                    if (HeaderButton(c[0], "Texture", "Name", textureSortColumn, textureSortAscending)) ToggleTextureSort("Name");
                    if (HeaderButton(c[1], "Resolution", "Res", textureSortColumn, textureSortAscending)) ToggleTextureSort("Res");
                    if (HeaderButton(c[2], "Format", "Format", textureSortColumn, textureSortAscending)) ToggleTextureSort("Format");
                    if (HeaderButton(c[3], "Mips", "Mips", textureSortColumn, textureSortAscending)) ToggleTextureSort("Mips");
                    if (HeaderButton(c[4], "VRAM", "Size", textureSortColumn, textureSortAscending)) ToggleTextureSort("Size");
                    break;
                }
                case 2:
                {
                    Rect[] c = DrawHeaderRow(90, 80, 80);
                    if (HeaderButton(c[0], "Mesh", "Name", meshSortColumn, meshSortAscending)) ToggleMeshSort("Name");
                    if (HeaderButton(c[1], "Vertices", "Verts", meshSortColumn, meshSortAscending)) ToggleMeshSort("Verts");
                    if (HeaderButton(c[2], "Submeshes", "Submeshes", meshSortColumn, meshSortAscending)) ToggleMeshSort("Submeshes");
                    if (HeaderButton(c[3], "VRAM", "Size", meshSortColumn, meshSortAscending)) ToggleMeshSort("Size");
                    break;
                }
                case 3:
                {
                    DrawEnvironmentTotals();
                    Rect[] c = DrawHeaderRow(170, 190, 80);
                    if (HeaderButton(c[0], "Asset / Component", "Name", envSortColumn, envSortAscending)) ToggleEnvSort("Name");
                    if (HeaderButton(c[1], "Type", "Type", envSortColumn, envSortAscending)) ToggleEnvSort("Type");
                    GUI.Label(c[2], "Resolution / Format", headerStyle);
                    if (HeaderButton(c[3], "VRAM", "Size", envSortColumn, envSortAscending)) ToggleEnvSort("Size");
                    break;
                }
            }
        }

        private void DrawTexturesTab()
        {
            var rows = textureReport.FindAll(t => MatchesSearch(t.Name));
            DrawVirtualRows(rows, (row, tex, index) =>
            {
                DrawRowBackground(row, index, selectedReportEntry == tex);
                Rect[] c = SplitColumns(row, 90, 120, 44, 80);
                Color marker = tex.VramSize >= 8 * 1024 * 1024 ? DangerColor : IsUncompressedFormat(tex.Format) ? CautionColor : Color.clear;
                Rect name = DrawMarker(c[0], marker);
                string path = tex.TextureAsset != null ? AssetDatabase.GetAssetPath(tex.TextureAsset) : "";
                if (GUI.Button(name, new GUIContent(tex.Name, path), rowLabelStyle)) SelectAsset(tex);
                GUI.Label(c[1], $"{tex.Width}x{tex.Height}", rowLabelStyle);
                GUI.Label(c[2], tex.Format, rowLabelStyle);
                GUI.Label(c[3], tex.HasMipmaps ? "Yes" : "No", rowLabelStyle);
                GUI.Label(c[4], FormatBytes(tex.VramSize), rowLabelStyle);
            });
        }

        private void DrawMeshesTab()
        {
            var rows = meshReport.FindAll(m => MatchesSearch(m.Name));
            DrawVirtualRows(rows, (row, mesh, index) =>
            {
                DrawRowBackground(row, index, selectedReportEntry == mesh);
                Rect[] c = SplitColumns(row, 90, 80, 80);
                Rect name = DrawMarker(c[0], mesh.VertexCount >= 50000 ? DangerColor : Color.clear);
                string path = mesh.MeshAsset != null ? AssetDatabase.GetAssetPath(mesh.MeshAsset) : "";
                if (GUI.Button(name, new GUIContent(mesh.Name, path), rowLabelStyle)) SelectAsset(mesh);
                GUI.Label(c[1], mesh.VertexCount.ToString("N0"), rowLabelStyle);
                GUI.Label(c[2], mesh.SubmeshCount.ToString(), rowLabelStyle);
                GUI.Label(c[3], FormatBytes(mesh.VramSize), rowLabelStyle);
            });
        }

        private void DrawEnvironmentTotals()
        {
            var totals = new Dictionary<string, long>();
            foreach (var env in envReport)
            {
                totals.TryGetValue(env.Type, out long sum);
                totals[env.Type] = sum + env.VramSize;
            }
            if (totals.Count == 0) return;

            var types = new List<string>(totals.Keys);
            types.Sort((a, b) => totals[b].CompareTo(totals[a]));
            var parts = new List<string>();
            foreach (string type in types) parts.Add($"{type}: {FormatBytes(totals[type])}");
            EditorGUILayout.LabelField(string.Join("    ", parts), EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawEnvironmentTab()
        {
            var rows = envReport.FindAll(e => MatchesSearch(e.Name) || MatchesSearch(e.Type));
            DrawVirtualRows(rows, (row, env, index) =>
            {
                DrawRowBackground(row, index, selectedReportEntry == env);
                Rect[] c = SplitColumns(row, 170, 190, 80);
                Rect name = DrawMarker(c[0], env.VramSize >= 8 * 1024 * 1024 ? DangerColor : Color.clear);
                string path = env.Asset != null ? AssetDatabase.GetAssetPath(env.Asset) : "";
                if (GUI.Button(name, new GUIContent(env.Name, path), rowLabelStyle)) SelectAsset(env);
                GUI.Label(c[1], env.Type, rowLabelStyle);
                GUI.Label(c[2], new GUIContent(env.Details, env.Details), rowLabelStyle);
                GUI.Label(c[3], FormatBytes(env.VramSize), rowLabelStyle);
            });
        }

        private void DrawWarningsTab()
        {
            if (warningsList.Count == 0)
            {
                GUILayout.Label("No VRAM warnings for this scene.", EditorStyles.boldLabel);
                return;
            }

            foreach (var warn in warningsList)
            {
                GUILayout.BeginHorizontal(EditorStyles.helpBox);
                string icon = warn.Severity == "Error" ? "console.erroricon.sml" : warn.Severity == "Warning" ? "console.warnicon.sml" : "console.infoicon.sml";
                GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(20), GUILayout.Height(18));
                GUILayout.Label(warn.Message, EditorStyles.wordWrappedLabel);
                if (warn.Asset != null && GUILayout.Button("Inspect", GUILayout.Width(60)))
                {
                    EditorGUIUtility.PingObject(warn.Asset);
                }
                GUILayout.EndHorizontal();
            }
        }

        private void ToggleEnvSort(string column)
        {
            if (envSortColumn == column)
            {
                envSortAscending = !envSortAscending;
            }
            else
            {
                envSortColumn = column;
                envSortAscending = column != "Size";
            }
            SortEnvironment();
        }

        private void SortEnvironment()
        {
            if (envSortColumn == "Name")
            {
                envReport.Sort((a, b) => envSortAscending ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) : string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase));
            }
            else if (envSortColumn == "Type")
            {
                envReport.Sort((a, b) => envSortAscending ? string.Compare(a.Type, b.Type, StringComparison.Ordinal) : string.Compare(b.Type, a.Type, StringComparison.Ordinal));
            }
            else
            {
                envReport.Sort((a, b) => envSortAscending ? a.VramSize.CompareTo(b.VramSize) : b.VramSize.CompareTo(a.VramSize));
            }
        }

        private void DrawReferenceTracingPanel()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
            GUILayout.Label("Asset Reference Trace Inspector", EditorStyles.boldLabel);

            if (selectedReportEntry == null)
            {
                GUILayout.Label("Select a texture, mesh or environment item above to see what in the scene uses it.", EditorStyles.miniLabel);
                return;
            }

            string assetPath = "";
            if (selectedReportEntry is TextureReportEntry texEntry) assetPath = AssetDatabase.GetAssetPath(texEntry.TextureAsset);
            else if (selectedReportEntry is MeshReportEntry meshEntry) assetPath = AssetDatabase.GetAssetPath(meshEntry.MeshAsset);
            else if (selectedReportEntry is EnvReportEntry envEntry && envEntry.Asset != null) assetPath = AssetDatabase.GetAssetPath(envEntry.Asset);

            if (!string.IsNullOrEmpty(assetPath))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Find Project Dependencies (Find what uses this file in Assets)", GUILayout.Height(24)))
                {
                    DeepSearchProjectReferences(assetPath);
                }
                GUILayout.EndHorizontal();
            }

            refScrollPos = EditorGUILayout.BeginScrollView(refScrollPos, GUILayout.Height(100));

            GUILayout.Label("Scene References (Direct usage in hierarchy):", EditorStyles.boldLabel);
            if (selectedReferences.Count == 0)
            {
                GUILayout.Label("This asset has no direct referencing components in the scene.", EditorStyles.miniLabel);
            }
            else
            {
                foreach (string refPath in selectedReferences)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("↳ " + refPath, EditorStyles.wordWrappedLabel);
                    GUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DeepSearchProjectReferences(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            var foundDeps = new List<string>();

            try
            {
                string[] allAssetPaths = AssetDatabase.GetAllAssetPaths();
                int progress = 0;
                foreach (var path in allAssetPaths)
                {
                    progress++;
                    if (progress % 100 == 0)
                    {
                        EditorUtility.DisplayProgressBar("Searching Dependencies", $"Checking {path}", (float)progress / allAssetPaths.Length);
                    }

                    if (path.StartsWith("Assets/"))
                    {
                        string[] dependencies = AssetDatabase.GetDependencies(path, false);
                        foreach (var dep in dependencies)
                        {
                            if (dep == assetPath && path != assetPath)
                            {
                                foundDeps.Add(path);
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (foundDeps.Count == 0)
            {
                EditorUtility.DisplayDialog("Dependency Search", "No dependencies found for this asset in the project. It appears to be unreferenced on disk.", "OK");
            }
            else
            {
                SynDependencyListWindow.ShowWindow(assetPath, foundDeps);
            }
        }

        private void SelectAsset(object entry)
        {
            selectedReportEntry = entry;
            selectedReferences.Clear();

            if (entry is TextureReportEntry tex)
            {
                if (tex.TextureAsset != null) EditorGUIUtility.PingObject(tex.TextureAsset);

                // Build trace chains
                foreach (var mat in tex.ReferencingMaterials)
                {
                    string matName = mat != null ? mat.name : "Null Material";
                    selectedReferences.Add($"[Material] {matName} (uses texture property)");
                }
                
                if (tex.ReferencingGameObjectPaths != null)
                {
                    foreach (var path in tex.ReferencingGameObjectPaths)
                    {
                        selectedReferences.Add($"[Scene Object] {path}");
                    }
                }
            }
            else if (entry is EnvReportEntry env)
            {
                if (env.Asset != null) EditorGUIUtility.PingObject(env.Asset);
                if (env.References != null) selectedReferences.AddRange(env.References);
            }
            else if (entry is MeshReportEntry mesh)
            {
                if (mesh.MeshAsset != null) EditorGUIUtility.PingObject(mesh.MeshAsset);

                if (mesh.ReferencingGameObjectPaths != null)
                {
                    foreach (var path in mesh.ReferencingGameObjectPaths)
                    {
                        selectedReferences.Add($"[Renderer Component] {path}");
                    }
                }
            }
        }

        private static string GetHierarchyPathStatic(Transform t)
        {
            if (t == null) return "";
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + " / " + path;
            }
            return path;
        }

        private void ToggleTextureSort(string column)
        {
            if (textureSortColumn == column)
            {
                textureSortAscending = !textureSortAscending;
            }
            else
            {
                textureSortColumn = column;
                textureSortAscending = false;
            }
            SortTextures();
        }

        private void SortTextures()
        {
            if (textureSortColumn == "Name")
            {
                textureReport.Sort((a, b) => textureSortAscending ? a.Name.CompareTo(b.Name) : b.Name.CompareTo(a.Name));
            }
            else if (textureSortColumn == "Res")
            {
                textureReport.Sort((a, b) => {
                    int sizeA = a.Width * a.Height;
                    int sizeB = b.Width * b.Height;
                    return textureSortAscending ? sizeA.CompareTo(sizeB) : sizeB.CompareTo(sizeA);
                });
            }
            else if (textureSortColumn == "Format")
            {
                textureReport.Sort((a, b) => textureSortAscending ? a.Format.CompareTo(b.Format) : b.Format.CompareTo(a.Format));
            }
            else if (textureSortColumn == "Mips")
            {
                textureReport.Sort((a, b) => textureSortAscending ? a.HasMipmaps.CompareTo(b.HasMipmaps) : b.HasMipmaps.CompareTo(a.HasMipmaps));
            }
            else if (textureSortColumn == "Size")
            {
                textureReport.Sort((a, b) => textureSortAscending ? a.VramSize.CompareTo(b.VramSize) : b.VramSize.CompareTo(a.VramSize));
            }
        }

        private void ToggleMeshSort(string column)
        {
            if (meshSortColumn == column)
            {
                meshSortAscending = !meshSortAscending;
            }
            else
            {
                meshSortColumn = column;
                meshSortAscending = false;
            }
            SortMeshes();
        }

        private void SortMeshes()
        {
            if (meshSortColumn == "Name")
            {
                meshReport.Sort((a, b) => meshSortAscending ? a.Name.CompareTo(b.Name) : b.Name.CompareTo(a.Name));
            }
            else if (meshSortColumn == "Verts")
            {
                meshReport.Sort((a, b) => meshSortAscending ? a.VertexCount.CompareTo(b.VertexCount) : b.VertexCount.CompareTo(a.VertexCount));
            }
            else if (meshSortColumn == "Size")
            {
                meshReport.Sort((a, b) => meshSortAscending ? a.VramSize.CompareTo(b.VramSize) : b.VramSize.CompareTo(a.VramSize));
            }
        }

        private class ScanResults
        {
            public long TotalBytes = 0;
            public long TextureBytes = 0;
            public long MeshBytes = 0;
            public long EnvBytes = 0;
            public List<TextureReportEntry> Textures = new List<TextureReportEntry>();
            public List<MeshReportEntry> Meshes = new List<MeshReportEntry>();
            public List<EnvReportEntry> EnvAssets = new List<EnvReportEntry>();
            public List<WarningEntry> Warnings = new List<WarningEntry>();
        }

        /// <summary>
        /// Everything one scan has counted, so each asset is counted once even when it is used in several places.
        /// </summary>
        private sealed class ScanContext
        {
            public readonly ScanResults Results = new ScanResults();
            public readonly HashSet<Texture> Counted = new HashSet<Texture>();
            public readonly Dictionary<Texture, TextureReportEntry> Textures = new Dictionary<Texture, TextureReportEntry>();
            public readonly Dictionary<Mesh, MeshReportEntry> Meshes = new Dictionary<Mesh, MeshReportEntry>();
            public readonly HashSet<Material> VisitedMaterials = new HashSet<Material>();
            public readonly HashSet<AnimationClip> VisitedClips = new HashSet<AnimationClip>();
            public readonly HashSet<TerrainData> VisitedTerrains = new HashSet<TerrainData>();
            public readonly Dictionary<UnityEngine.Object, EnvReportEntry> Environment = new Dictionary<UnityEngine.Object, EnvReportEntry>();
            // How the component being walked references the current object, e.g. "Bakery Volume.bakedTexture0"
            public string CurrentVia = "";
            public bool ShowProgress;
        }

        private ScanResults ProfileScene(string scenePath, bool showProgress = false)
        {
            Scene activeScene = SceneManager.GetActiveScene();
            bool isCurrent = (activeScene.path == scenePath);
            Scene targetScene = isCurrent ? activeScene : EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

            var ctx = new ScanContext { ShowProgress = showProgress };
            try
            {
                // Lighting first, so a lightmap or probe texture that a component also references counts as lighting
                CollectEnvironment(targetScene, ctx);
                CollectSceneReferences(targetScene, ctx);

                ScanResults results = ctx.Results;
                results.Textures = new List<TextureReportEntry>(ctx.Textures.Values);
                results.Meshes = new List<MeshReportEntry>(ctx.Meshes.Values);
                results.TotalBytes = results.TextureBytes + results.MeshBytes + results.EnvBytes;
                return results;
            }
            finally
            {
                if (ctx.ShowProgress) EditorUtility.ClearProgressBar();
                if (!isCurrent)
                {
                    EditorSceneManager.CloseScene(targetScene, true);
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Environment: lightmaps, shadowmasks, reflection probes and skybox
        // ---------------------------------------------------------------------------------------------------------

        private void CollectEnvironment(Scene scene, ScanContext ctx)
        {
            // Unity merges the lightmaps of every loaded scene into one list. With several scenes open, only the
            // entries this scene's renderers and terrains use belong to it.
            LightmapData[] lightmaps = LightmapSettings.lightmaps;
            if (lightmaps != null && lightmaps.Length > 0)
            {
                Dictionary<int, int> users = CountLightmapUsers(scene);
                bool onlyUsed = CountLoadedScenes() > 1;
                for (int i = 0; i < lightmaps.Length; i++)
                {
                    LightmapData lm = lightmaps[i];
                    users.TryGetValue(i, out int userCount);
                    if (lm == null || (onlyUsed && userCount == 0)) continue;
                    string source = $"Scene lighting data, used by {userCount} renderer(s) and terrain(s)";
                    AddEnvironmentTexture(ctx, lm.lightmapColor, $"Lightmap {i}", "Lightmap", source);
                    AddEnvironmentTexture(ctx, lm.lightmapDir, $"Directional Lightmap {i}", "Directional Lightmap", source);
                    AddEnvironmentTexture(ctx, lm.shadowMask, $"Shadowmask {i}", "Shadowmask", source);
                }
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (ReflectionProbe probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                {
                    if (probe == null || IsEditorOnly(probe.transform)) continue;
                    string probePath = $"{GetHierarchyPathStatic(probe.transform)} (Reflection Probe)";
                    switch (probe.mode)
                    {
                        case ReflectionProbeMode.Baked:
                            AddEnvironmentTexture(ctx, probe.bakedTexture, probe.name, "Reflection Probe", probePath);
                            break;
                        case ReflectionProbeMode.Custom:
                            AddEnvironmentTexture(ctx, probe.customBakedTexture, probe.name, "Reflection Probe (Custom)", probePath);
                            break;
                        case ReflectionProbeMode.Realtime:
                            // Rendered at runtime into a cubemap of the probe's resolution (HDR, with mipmaps)
                            int res = probe.resolution;
                            int bytesPerPixel = probe.hdr ? 8 : 4;
                            long size = (long)(res * (double)res * 6 * bytesPerPixel * 4.0 / 3.0);
                            AddEnvironmentEntry(ctx, probe, probe.name, "Reflection Probe (Realtime)", $"{res}x{res} cubemap, rendered at runtime", size).References.Add(probePath);
                            break;
                    }
                }
            }

            // RenderSettings belong to the active scene
            Material skyboxMat = RenderSettings.skybox;
            if (scene == SceneManager.GetActiveScene() && skyboxMat != null && skyboxMat.shader != null)
            {
                int propCount = ShaderUtil.GetPropertyCount(skyboxMat.shader);
                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(skyboxMat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    Texture tex = skyboxMat.GetTexture(ShaderUtil.GetPropertyName(skyboxMat.shader, i));
                    if (tex != null) AddEnvironmentTexture(ctx, tex, $"Skybox: {tex.name}", "Skybox", $"Lighting settings: skybox material '{skyboxMat.name}'");
                }
            }
        }

        private void AddEnvironmentTexture(ScanContext ctx, Texture tex, string name, string type, string source)
        {
            if (tex == null) return;
            if (ctx.Counted.Add(tex))
            {
                AddEnvironmentEntry(ctx, tex, name, type, DescribeTexture(tex), EstimateTextureSize(tex));
            }
            AddEnvironmentReference(ctx, tex, source);
        }

        private static EnvReportEntry AddEnvironmentEntry(ScanContext ctx, UnityEngine.Object asset, string name, string type, string details, long size)
        {
            var entry = new EnvReportEntry
            {
                Asset = asset,
                Name = name,
                Type = type,
                Details = details,
                VramSize = size
            };
            ctx.Results.EnvAssets.Add(entry);
            ctx.Results.EnvBytes += size;
            if (asset != null) ctx.Environment[asset] = entry;
            return entry;
        }

        private static void AddEnvironmentReference(ScanContext ctx, UnityEngine.Object asset, string reference)
        {
            if (asset == null || string.IsNullOrEmpty(reference)) return;
            if (ctx.Environment.TryGetValue(asset, out EnvReportEntry entry) && !entry.References.Contains(reference))
            {
                entry.References.Add(reference);
            }
        }

        private static Dictionary<int, int> CountLightmapUsers(Scene scene)
        {
            var users = new Dictionary<int, int>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    // 0xFFFE / 0xFFFF mark "not lightmapped"
                    if (r.lightmapIndex >= 0 && r.lightmapIndex < 0xFFFE)
                    {
                        users.TryGetValue(r.lightmapIndex, out int n);
                        users[r.lightmapIndex] = n + 1;
                    }
                }
                foreach (Terrain t in root.GetComponentsInChildren<Terrain>(true))
                {
                    if (t.lightmapIndex >= 0 && t.lightmapIndex < 0xFFFE)
                    {
                        users.TryGetValue(t.lightmapIndex, out int n);
                        users[t.lightmapIndex] = n + 1;
                    }
                }
            }
            return users;
        }

        private static int CountLoadedScenes()
        {
            int loaded = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isLoaded) loaded++;
            }
            return loaded;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Everything components reference: renderers, particles, UI, terrain, sprites, lights, cameras, Udon and
        // other scripts, and materials swapped in by animations. EditorOnly objects are stripped from builds.
        // ---------------------------------------------------------------------------------------------------------

        private void CollectSceneReferences(Scene scene, ScanContext ctx)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (ctx.ShowProgress)
                {
                    EditorUtility.DisplayProgressBar("VRAM Analyzer", $"Scanning {roots[i].name}", (float)i / roots.Length);
                }
                WalkGameObject(roots[i], ctx);
            }
        }

        private void WalkGameObject(GameObject go, ScanContext ctx)
        {
            if (go == null || go.CompareTag("EditorOnly")) return;

            foreach (Component component in go.GetComponents<Component>())
            {
                // Collider meshes are physics data, not GPU memory
                if (component == null || component is Transform || component is MeshCollider) continue;

                if (component is Animator animator && animator.runtimeAnimatorController != null)
                {
                    foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                    {
                        if (clip == null || !ctx.VisitedClips.Add(clip)) continue;
                        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        {
                            foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                            {
                                ctx.CurrentVia = $"animation '{clip.name}'";
                                VisitReference(key.value, component, ctx);
                            }
                        }
                    }
                }

                SerializedProperty iterator = new SerializedObject(component).GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue != null)
                    {
                        ctx.CurrentVia = $"{ObjectNames.NicifyVariableName(component.GetType().Name)}.{iterator.propertyPath}";
                        VisitReference(iterator.objectReferenceValue, component, ctx);
                    }
                }
            }

            foreach (Transform child in go.transform)
            {
                WalkGameObject(child.gameObject, ctx);
            }
        }

        private void VisitReference(UnityEngine.Object obj, Component user, ScanContext ctx)
        {
            switch (obj)
            {
                case Material mat:
                    VisitMaterial(mat, user, ctx);
                    break;
                case Sprite sprite:
                    AddTexture(sprite.texture, null, user, ctx);
                    break;
                case Texture tex:
                    AddTexture(tex, null, user, ctx);
                    break;
                case Mesh mesh:
                    AddMesh(mesh, user, ctx);
                    break;
                case TerrainData terrain when ctx.VisitedTerrains.Add(terrain):
                    foreach (Texture2D alphamap in terrain.alphamapTextures) AddTexture(alphamap, null, user, ctx);
                    foreach (TerrainLayer layer in terrain.terrainLayers)
                    {
                        if (layer == null) continue;
                        AddTexture(layer.diffuseTexture, null, user, ctx);
                        AddTexture(layer.normalMapTexture, null, user, ctx);
                        AddTexture(layer.maskMapTexture, null, user, ctx);
                    }
                    AddTexture(terrain.heightmapTexture, null, user, ctx);
                    break;
            }
        }

        private void VisitMaterial(Material mat, Component user, ScanContext ctx)
        {
            if (mat == null || mat.shader == null) return;
            bool firstVisit = ctx.VisitedMaterials.Add(mat);
            int propCount = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < propCount; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                Texture tex = mat.GetTexture(ShaderUtil.GetPropertyName(mat.shader, i));
                if (tex == null) continue;
                // Later users of an already visited material only add their object path
                if (firstVisit || ctx.Textures.ContainsKey(tex)) AddTexture(tex, mat, user, ctx);
            }
        }

        private void AddTexture(Texture tex, Material mat, Component user, ScanContext ctx)
        {
            if (tex == null) return;

            if (!ctx.Textures.TryGetValue(tex, out TextureReportEntry entry))
            {
                if (!ctx.Counted.Add(tex))
                {
                    // Already counted as lighting: note who else references it
                    if (user != null) AddEnvironmentReference(ctx, tex, DescribeReference(user, mat, ctx));
                    return;
                }

                // Baked lighting referenced by components (Bakery storage, light volumes) is environment memory
                string path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path) && SynProtectionData.IsBakeryAsset(path, tex))
                {
                    AddEnvironmentEntry(ctx, tex, tex.name, ClassifyLightingTexture(tex, path), DescribeTexture(tex), EstimateTextureSize(tex));
                    if (user != null) AddEnvironmentReference(ctx, tex, DescribeReference(user, mat, ctx));
                    return;
                }

                entry = new TextureReportEntry
                {
                    TextureAsset = tex,
                    Name = tex.name,
                    Width = tex.width,
                    Height = tex.height,
                    Format = GetTextureFormatString(tex),
                    HasMipmaps = HasMipmapsEnabled(tex),
                    VramSize = EstimateTextureSize(tex)
                };
                ctx.Textures[tex] = entry;
                ctx.Results.TextureBytes += entry.VramSize;
                AddTextureWarnings(tex, entry, path, ctx);
            }

            if (mat != null && !entry.ReferencingMaterials.Contains(mat)) entry.ReferencingMaterials.Add(mat);
            if (user != null)
            {
                string objectPath = GetHierarchyPathStatic(user.transform);
                if (!entry.ReferencingGameObjectPaths.Contains(objectPath)) entry.ReferencingGameObjectPaths.Add(objectPath);
            }
        }

        private static string DescribeReference(Component user, Material mat, ScanContext ctx)
        {
            string through = mat != null ? $", material '{mat.name}'" : "";
            return $"{GetHierarchyPathStatic(user.transform)} ({ctx.CurrentVia}{through})";
        }

        private static string ClassifyLightingTexture(Texture tex, string path)
        {
            if (tex is Texture3D) return "Light Volume";
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                if (importer.textureType == TextureImporterType.DirectionalLightmap) return "Directional Lightmap";
                if (importer.textureType == TextureImporterType.Shadowmask) return "Shadowmask";
            }
            return "Lightmap (Baked)";
        }

        private void AddTextureWarnings(Texture tex, TextureReportEntry entry, string path, ScanContext ctx)
        {
            if (entry.VramSize >= 16 * 1024 * 1024)
            {
                ctx.Results.Warnings.Add(new WarningEntry
                {
                    Asset = tex,
                    Message = $"Large texture '{tex.name}' uses {FormatBytes(entry.VramSize)} of VRAM ({tex.width}x{tex.height}).",
                    Severity = "Error"
                });
            }

            if (IsUncompressedFormat(entry.Format) && !(tex is RenderTexture))
            {
                ctx.Results.Warnings.Add(new WarningEntry
                {
                    Asset = tex,
                    Message = $"Texture '{tex.name}' is uncompressed ({entry.Format}). Use a compressed format (DXT/BC7 on PC, ASTC on Quest).",
                    Severity = "Warning"
                });
            }

            // UI and sprite textures are drawn at their own size and normally have no mipmaps
            bool isUiTexture = AssetImporter.GetAtPath(path) is TextureImporter importer &&
                               (importer.textureType == TextureImporterType.Sprite || importer.textureType == TextureImporterType.GUI);
            if (!entry.HasMipmaps && tex is Texture2D && !isUiTexture)
            {
                ctx.Results.Warnings.Add(new WarningEntry
                {
                    Asset = tex,
                    Message = $"Texture '{tex.name}' has mipmaps disabled. Surfaces seen from afar will shimmer and sample the full-size texture.",
                    Severity = "Info"
                });
            }
        }

        private void AddMesh(Mesh mesh, Component user, ScanContext ctx)
        {
            if (mesh == null) return;
            if (!ctx.Meshes.TryGetValue(mesh, out MeshReportEntry entry))
            {
                entry = new MeshReportEntry
                {
                    MeshAsset = mesh,
                    Name = mesh.name,
                    VertexCount = mesh.vertexCount,
                    SubmeshCount = mesh.subMeshCount,
                    IndexFormat = mesh.indexFormat.ToString(),
                    VramSize = EstimateMeshSize(mesh)
                };
                ctx.Meshes[mesh] = entry;
                ctx.Results.MeshBytes += entry.VramSize;

                if (entry.VertexCount >= 80000)
                {
                    ctx.Results.Warnings.Add(new WarningEntry
                    {
                        Asset = mesh,
                        Message = $"High-poly mesh '{mesh.name}' ({mesh.vertexCount:N0} vertices).",
                        Severity = "Warning"
                    });
                }
            }

            if (user != null)
            {
                string objectPath = GetHierarchyPathStatic(user.transform);
                if (!entry.ReferencingGameObjectPaths.Contains(objectPath)) entry.ReferencingGameObjectPaths.Add(objectPath);
            }
        }

        private static bool IsEditorOnly(Transform t)
        {
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }

        private static bool IsUncompressedFormat(string format)
        {
            return format.Contains("RGBA32") || format.Contains("RGB24") || format.Contains("ARGB32") ||
                   format.Contains("RGBAHalf") || format.Contains("RGBAFloat");
        }

        private string DescribeTexture(Texture tex)
        {
            string shape = tex is Cubemap ? " cubemap" : tex is Texture3D t3d ? $"x{t3d.depth} volume" : "";
            return $"{tex.width}x{tex.height}{shape} ({GetTextureFormatString(tex)})";
        }

        private enum EstimatePlatform { ActiveBuildTarget, PC, Quest }

        private bool IsEstimatingActivePlatform()
        {
            if (estimatePlatform == EstimatePlatform.ActiveBuildTarget) return true;
            bool activeIsMobile = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;
            return (estimatePlatform == EstimatePlatform.Quest) == activeIsMobile;
        }

        /// <summary>
        /// GPU memory of a texture. For the active build target this is exact (actual format, size and mip chain;
        /// unlike Profiler.GetRuntimeMemorySizeLong it excludes editor-only CPU copies). For the other platform it
        /// is estimated from the texture's import settings for that platform.
        /// </summary>
        private long EstimateTextureSize(Texture tex)
        {
            if (tex == null) return 0;

            if (!IsEstimatingActivePlatform() && tex is Texture2D && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) is TextureImporter importer)
            {
                return EstimateFromImporter(importer, estimatePlatform == EstimatePlatform.Quest);
            }

            int faces = 1;
            int layers = 1;
            int mipCount = 1;
            GraphicsFormat format;
            switch (tex)
            {
                case Texture2D t2d:
                    format = t2d.graphicsFormat;
                    mipCount = t2d.mipmapCount;
                    break;
                case Cubemap cube:
                    format = cube.graphicsFormat;
                    mipCount = cube.mipmapCount;
                    faces = 6;
                    break;
                case Texture2DArray arr:
                    format = arr.graphicsFormat;
                    mipCount = arr.mipmapCount;
                    layers = arr.depth;
                    break;
                case Texture3D t3d:
                    format = t3d.graphicsFormat;
                    mipCount = t3d.mipmapCount;
                    layers = t3d.depth;
                    break;
                case RenderTexture rt:
                {
                    // Colour (with its mip chain) plus the depth buffer, both multiplied by the MSAA sample count
                    int samples = Mathf.Max(1, rt.antiAliasing);
                    long color = 0;
                    int rtMips = rt.useMipMap ? Mathf.Max(1, rt.mipmapCount) : 1;
                    if (rt.graphicsFormat != GraphicsFormat.None)
                    {
                        for (int mip = 0; mip < rtMips; mip++)
                        {
                            color += (long)GraphicsFormatUtility.ComputeMipmapSize(Mathf.Max(1, rt.width >> mip), Mathf.Max(1, rt.height >> mip), rt.graphicsFormat);
                        }
                    }
                    int depthBytes = rt.depth == 0 ? 0 : rt.depth == 16 ? 2 : 4;
                    int volume = rt.dimension == TextureDimension.Cube ? 6 : Mathf.Max(1, rt.volumeDepth);
                    return (color * volume + (long)rt.width * rt.height * depthBytes) * samples;
                }
                default:
                    return UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
            }

            if (format == GraphicsFormat.None) return UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);

            long total = 0;
            for (int mip = 0; mip < Mathf.Max(1, mipCount); mip++)
            {
                int w = Mathf.Max(1, tex.width >> mip);
                int h = Mathf.Max(1, tex.height >> mip);
                int mipLayers = tex is Texture3D ? Mathf.Max(1, layers >> mip) : layers;
                total += (long)GraphicsFormatUtility.ComputeMipmapSize(w, h, format) * faces * mipLayers;
            }
            return total;
        }

        /// <summary>
        /// Estimates a texture's size on PC or Quest from its import settings: platform max size, chosen format
        /// (Automatic maps to what Unity picks: DXT1/DXT5/BC5/BC6H on PC, ASTC on Android) and mipmaps.
        /// </summary>
        private static long EstimateFromImporter(TextureImporter importer, bool quest)
        {
            TextureImporterPlatformSettings ps = importer.GetPlatformTextureSettings(quest ? "Android" : "Standalone");
            int maxSize = ps.overridden ? ps.maxTextureSize : importer.maxTextureSize;
            TextureImporterCompression compression = ps.overridden ? ps.textureCompression : importer.textureCompression;

            importer.GetSourceTextureWidthAndHeight(out int width, out int height);
            if (importer.npotScale != TextureImporterNPOTScale.None)
            {
                width = Mathf.NextPowerOfTwo(width);
                height = Mathf.NextPowerOfTwo(height);
            }
            int largest = Mathf.Max(width, height);
            if (largest > maxSize)
            {
                width = Mathf.Max(1, width * maxSize / largest);
                height = Mathf.Max(1, height * maxSize / largest);
            }

            bool hasAlpha = importer.alphaSource != TextureImporterAlphaSource.None && importer.DoesSourceTextureHaveAlpha();
            float bpp = ps.overridden && ps.format != TextureImporterFormat.Automatic
                ? GetImporterFormatBitsPerPixel(ps.format)
                : GetAutomaticBitsPerPixel(importer, compression, hasAlpha, quest);

            double bytes = width * (double)height * bpp / 8.0;
            if (importer.mipmapEnabled) bytes *= 4.0 / 3.0;
            return (long)bytes;
        }

        private static float GetAutomaticBitsPerPixel(TextureImporter importer, TextureImporterCompression compression, bool hasAlpha, bool quest)
        {
            bool hdr = importer.assetPath.EndsWith(".exr", StringComparison.OrdinalIgnoreCase) || importer.assetPath.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase);
            if (compression == TextureImporterCompression.Uncompressed) return hdr ? 64f : hasAlpha ? 32f : 24f;
            if (hdr && !quest) return 8f; // BC6H (HDR lightmaps, skyboxes, probes)

            if (quest)
            {
                // Unity's Android ASTC block size by compression quality
                switch (compression)
                {
                    case TextureImporterCompression.CompressedHQ: return 8f;    // ASTC 4x4
                    case TextureImporterCompression.CompressedLQ: return 2f;    // ASTC 8x8
                    default: return 3.56f;                                      // ASTC 6x6
                }
            }

            if (importer.textureType == TextureImporterType.NormalMap) return 8f;           // DXT5nm / BC5
            if (importer.textureType == TextureImporterType.SingleChannel) return 4f;       // BC4
            if (compression == TextureImporterCompression.CompressedHQ) return 8f;          // BC7
            return hasAlpha ? 8f : 4f;                                                       // DXT5 / DXT1
        }

        private static float GetImporterFormatBitsPerPixel(TextureImporterFormat format)
        {
            switch (format)
            {
                case TextureImporterFormat.DXT1:
                case TextureImporterFormat.DXT1Crunched:
                case TextureImporterFormat.BC4:
                case TextureImporterFormat.ETC_RGB4:
                case TextureImporterFormat.ETC2_RGB4:
                case TextureImporterFormat.ETC2_RGB4_PUNCHTHROUGH_ALPHA:
                    return 4f;
                case TextureImporterFormat.DXT5:
                case TextureImporterFormat.DXT5Crunched:
                case TextureImporterFormat.BC5:
                case TextureImporterFormat.BC6H:
                case TextureImporterFormat.BC7:
                case TextureImporterFormat.ETC2_RGBA8:
                case TextureImporterFormat.ASTC_4x4:
                case TextureImporterFormat.ASTC_HDR_4x4:
                case TextureImporterFormat.Alpha8:
                case TextureImporterFormat.R8:
                    return 8f;
                case TextureImporterFormat.ASTC_5x5:
                case TextureImporterFormat.ASTC_HDR_5x5:
                    return 5.12f;
                case TextureImporterFormat.ASTC_6x6:
                case TextureImporterFormat.ASTC_HDR_6x6:
                    return 3.56f;
                case TextureImporterFormat.ASTC_8x8:
                case TextureImporterFormat.ASTC_HDR_8x8:
                    return 2f;
                case TextureImporterFormat.ASTC_10x10:
                case TextureImporterFormat.ASTC_HDR_10x10:
                    return 1.28f;
                case TextureImporterFormat.ASTC_12x12:
                case TextureImporterFormat.ASTC_HDR_12x12:
                    return 0.89f;
                case TextureImporterFormat.R16:
                case TextureImporterFormat.RHalf:
                case TextureImporterFormat.RG16:
                    return 16f;
                case TextureImporterFormat.RGB24:
                    return 24f;
                case TextureImporterFormat.RGBA32:
                case TextureImporterFormat.ARGB32:
                case TextureImporterFormat.RFloat:
                case TextureImporterFormat.RGHalf:
                    return 32f;
                case TextureImporterFormat.RGBAHalf:
                case TextureImporterFormat.RGFloat:
                    return 64f;
                case TextureImporterFormat.RGBAFloat:
                    return 128f;
                default:
                    return 32f;
            }
        }

        /// <summary>
        /// GPU memory of a mesh: every vertex stream at its real stride plus the index buffer. Editor-side CPU
        /// copies (which Profiler.GetRuntimeMemorySizeLong includes) are not counted.
        /// </summary>
        private long EstimateMeshSize(Mesh mesh)
        {
            if (mesh == null) return 0;

            long vertexBytes = 0;
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++)
            {
                vertexBytes += (long)mesh.GetVertexBufferStride(stream) * mesh.vertexCount;
            }

            long indexCount = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                indexCount += mesh.GetIndexCount(i);
            }
            int indexStride = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32 ? 4 : 2;

            return vertexBytes + indexCount * indexStride;
        }

        private string GetTextureFormatString(Texture tex)
        {
            if (tex is Texture2D t2d) return t2d.format.ToString();
            if (tex is Texture2DArray arr) return arr.format.ToString();
            if (tex is Cubemap cube) return cube.format.ToString();
            if (tex is Texture3D t3d) return t3d.format.ToString();
            if (tex is RenderTexture rt) return $"{rt.graphicsFormat} (RenderTexture)";
            return tex.graphicsFormat.ToString();
        }

        private bool HasMipmapsEnabled(Texture tex)
        {
            if (tex is RenderTexture rt) return rt.useMipMap;
            if (tex is Texture2D t2d) return t2d.mipmapCount > 1;
            if (tex is Texture2DArray arr) return arr.mipmapCount > 1;
            if (tex is Cubemap cube) return cube.mipmapCount > 1;
            return false;
        }

        private string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            double val = bytes;
            int i = 0;
            while (val >= 1024 && i < suffixes.Length - 1)
            {
                val /= 1024;
                i++;
            }
            return $"{val:F2} {suffixes[i]}";
        }
    }

    public struct SynVramTotals
    {
        public long Total;
        public long Textures;
        public long Meshes;
        public long Environment;
    }

    public class SynDependencyListWindow : EditorWindow
    {
        private List<string> dependencies = new List<string>();
        private string assetPath = "";
        private Vector2 scrollPos = Vector2.zero;

        public static void ShowWindow(string assetPath, List<string> deps)
        {
            var window = GetWindow<SynDependencyListWindow>(true, "Asset Dependencies", true);
            window.assetPath = assetPath;
            window.dependencies = new List<string>(deps);
            window.minSize = new Vector2(500, 350);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            GUILayout.Label($"Dependencies for: {Path.GetFileName(assetPath)}", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(assetPath, EditorStyles.miniLabel, GUILayout.Height(18));
            
            EditorGUILayout.Space();
            if (GUILayout.Button("Copy References List to Clipboard", GUILayout.Height(30)))
            {
                EditorGUIUtility.systemCopyBuffer = string.Join("\n", dependencies);
                Debug.Log("[SYN VRAM ANALYZER] Copied dependencies to clipboard.");
                EditorUtility.DisplayDialog("Copy Success", "Copied references list to clipboard!", "OK");
            }

            EditorGUILayout.Space();
            GUILayout.Label($"Referenced by {dependencies.Count} assets in project:", EditorStyles.boldLabel);
            
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            foreach (var path in dependencies)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("↳ " + path, EditorStyles.wordWrappedLabel);
                if (GUILayout.Button("Ping", GUILayout.Width(50)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                }
                GUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
