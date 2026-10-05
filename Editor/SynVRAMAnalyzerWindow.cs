using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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

        private ScanTarget currentTarget = ScanTarget.ActiveScene;
        private string activeScenePath = "";
        
        // Scan Results
        private bool hasScanned = false;
        private string scannedSceneName = "";
        private string reportTimestamp = "";
        private long totalVramBytes = 0;
        private long textureVramBytes = 0;
        private long meshVramBytes = 0;
        private long environmentalVramBytes = 0;

        // Details Lists
        private List<TextureReportEntry> textureReport = new List<TextureReportEntry>();
        private List<MeshReportEntry> meshReport = new List<MeshReportEntry>();
        private List<EnvReportEntry> envReport = new List<EnvReportEntry>();
        private List<WarningEntry> warningsList = new List<WarningEntry>();

        // Selected item for reference tracing
        private object selectedReportEntry = null;
        private List<string> selectedReferences = new List<string>();
        private List<string> deepProjectReferences = new List<string>();

        // GUI State
        private int activeTab = 0; // 0: Overview, 1: Textures, 2: Meshes, 3: Lightmaps & Probes, 4: Warnings
        private Vector2 scrollPos = Vector2.zero;
        private Vector2 refScrollPos = Vector2.zero;

        // Sort States
        private string textureSortColumn = "Size";
        private bool textureSortAscending = false;
        private string meshSortColumn = "Size";
        private bool meshSortAscending = false;

        // Baseline Compare State
        private long baselineVramBytes = -1;

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

            GUILayout.Space(5);

            string fileStatusMessage = "";
            bool canScan = true;
            string targetPath = GetTargetPath(currentTarget);

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

            // Main Data Table
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
                    sb.AppendLine("Name\tType\tDetails\tVRAM Size\tPath");
                    foreach (var env in envReport)
                    {
                        sb.AppendLine($"{env.Name}\t{env.Type}\t{env.Details}\t{FormatBytes(env.VramSize)}\t{AssetDatabase.GetAssetPath(env.Asset)}");
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
            ScanResults results = ProfileScene(scenePath);

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
            selectedReportEntry = null;
            selectedReferences.Clear();

            hasScanned = true;
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
                        VramSize = sEnv.VramSize
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
                selectedReportEntry = null;
                selectedReferences.Clear();

                hasScanned = true;
            }
            catch (Exception e)
            {
                Debug.LogError("[SYN VRAM ANALYZER] Failed to load cached report JSON: " + e.Message);
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
                        VramSize = env.VramSize
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
            GUILayout.Label($"Scene: {scannedSceneName} ({reportTimestamp})", EditorStyles.miniLabel);
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
            GUILayout.Label($"■ Env (Lightmaps/Probes): {FormatBytes(environmentalVramBytes)} ({envPct * 100:F1}%)", EditorStyles.miniLabel);
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
                if (tex.Format.Contains("RGBA32") || tex.Format.Contains("RGB24") || tex.Format.Contains("ARGB")) uncompressedTexturesCount++;
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

        private void DrawTexturesTab()
        {
            // Table Header with sort buttons
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Asset Name", EditorStyles.boldLabel, GUILayout.Width(200))) ToggleTextureSort("Name");
            if (GUILayout.Button("Res", EditorStyles.boldLabel, GUILayout.Width(70))) ToggleTextureSort("Res");
            if (GUILayout.Button("Format", EditorStyles.boldLabel, GUILayout.Width(120))) ToggleTextureSort("Format");
            if (GUILayout.Button("Mips", EditorStyles.boldLabel, GUILayout.Width(40))) ToggleTextureSort("Mips");
            if (GUILayout.Button("VRAM Size", EditorStyles.boldLabel, GUILayout.ExpandWidth(true))) ToggleTextureSort("Size");
            GUILayout.EndHorizontal();

            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            foreach (var tex in textureReport)
            {
                bool isSelected = (selectedReportEntry == tex);
                GUIStyle rowStyle = new GUIStyle(GUI.skin.label);
                if (isSelected)
                {
                    rowStyle.normal.background = Texture2D.whiteTexture;
                    rowStyle.normal.textColor = Color.black;
                }

                GUILayout.BeginHorizontal();
                
                // Color indicator for dangerous uncompressed or massive textures
                string indicator = "";
                if (tex.VramSize >= 8 * 1024 * 1024) indicator = "🔴 "; // Danger: >8MB
                else if (tex.Format.Contains("RGBA32") || tex.Format.Contains("RGB24")) indicator = "🟡 "; // Warn

                if (GUILayout.Button($"{indicator}{tex.Name}", rowStyle, GUILayout.Width(200)))
                {
                    SelectAsset(tex);
                }

                GUILayout.Label($"{tex.Width}x{tex.Height}", rowStyle, GUILayout.Width(70));
                GUILayout.Label(tex.Format, rowStyle, GUILayout.Width(120));
                GUILayout.Label(tex.HasMipmaps ? "Yes" : "No", rowStyle, GUILayout.Width(40));
                GUILayout.Label(FormatBytes(tex.VramSize), rowStyle, GUILayout.ExpandWidth(true));

                GUILayout.EndHorizontal();
            }
        }

        private void DrawMeshesTab()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Mesh Name", EditorStyles.boldLabel, GUILayout.Width(240))) ToggleMeshSort("Name");
            if (GUILayout.Button("Verts", EditorStyles.boldLabel, GUILayout.Width(80))) ToggleMeshSort("Verts");
            if (GUILayout.Button("Submeshes", EditorStyles.boldLabel, GUILayout.Width(80))) ToggleMeshSort("Submeshes");
            if (GUILayout.Button("VRAM Size", EditorStyles.boldLabel, GUILayout.ExpandWidth(true))) ToggleMeshSort("Size");
            GUILayout.EndHorizontal();

            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            foreach (var mesh in meshReport)
            {
                bool isSelected = (selectedReportEntry == mesh);
                GUIStyle rowStyle = new GUIStyle(GUI.skin.label);
                if (isSelected)
                {
                    rowStyle.normal.background = Texture2D.whiteTexture;
                    rowStyle.normal.textColor = Color.black;
                }

                GUILayout.BeginHorizontal();

                string indicator = "";
                if (mesh.VertexCount >= 50000) indicator = "🔴 "; // Heavy mesh

                if (GUILayout.Button($"{indicator}{mesh.Name}", rowStyle, GUILayout.Width(240)))
                {
                    SelectAsset(mesh);
                }

                GUILayout.Label(mesh.VertexCount.ToString("N0"), rowStyle, GUILayout.Width(80));
                GUILayout.Label(mesh.SubmeshCount.ToString(), rowStyle, GUILayout.Width(80));
                GUILayout.Label(FormatBytes(mesh.VramSize), rowStyle, GUILayout.ExpandWidth(true));

                GUILayout.EndHorizontal();
            }
        }

        private void DrawEnvironmentTab()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Asset / Component", EditorStyles.boldLabel, GUILayout.Width(240));
            GUILayout.Label("Type", EditorStyles.boldLabel, GUILayout.Width(120));
            GUILayout.Label("Resolution / Details", EditorStyles.boldLabel, GUILayout.Width(160));
            GUILayout.Label("VRAM Size", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            foreach (var env in envReport)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(env.Name, GUI.skin.label, GUILayout.Width(240)))
                {
                    if (env.Asset != null) EditorGUIUtility.PingObject(env.Asset);
                }
                GUILayout.Label(env.Type, GUILayout.Width(120));
                GUILayout.Label(env.Details, GUILayout.Width(160));
                GUILayout.Label(FormatBytes(env.VramSize), GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
            }
        }

        private void DrawWarningsTab()
        {
            if (warningsList.Count == 0)
            {
                GUILayout.Label("No critical VRAM optimization warnings found in the scene! Good job.", EditorStyles.boldLabel);
                return;
            }

            foreach (var warn in warningsList)
            {
                GUILayout.BeginVertical(EditorStyles.helpBox);
                
                GUILayout.BeginHorizontal();
                string prefix = warn.Severity == "Error" ? "🔴 " : (warn.Severity == "Warning" ? "🟡 " : "ℹ️ ");
                GUILayout.Label($"{prefix}{warn.Message}", EditorStyles.wordWrappedLabel);
                
                if (warn.Asset != null)
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Inspect", GUILayout.Width(60)))
                    {
                        EditorGUIUtility.PingObject(warn.Asset);
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
                GUILayout.Space(2);
            }
        }

        private void DrawReferenceTracingPanel()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
            GUILayout.Label("Asset Reference Trace Inspector", EditorStyles.boldLabel);

            if (selectedReportEntry == null)
            {
                GUILayout.Label("Select a Texture or Mesh above to inspect its active scene reference links.", EditorStyles.miniLabel);
                return;
            }

            string assetPath = "";
            if (selectedReportEntry is TextureReportEntry texEntry) assetPath = AssetDatabase.GetAssetPath(texEntry.TextureAsset);
            else if (selectedReportEntry is MeshReportEntry meshEntry) assetPath = AssetDatabase.GetAssetPath(meshEntry.MeshAsset);

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

        private ScanResults ProfileScene(string scenePath)
        {
            var results = new ScanResults();

            Scene activeScene = SceneManager.GetActiveScene();
            bool isCurrent = (activeScene.path == scenePath);
            Scene targetScene;

            if (isCurrent)
            {
                targetScene = activeScene;
            }
            else
            {
                targetScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }

            try
            {
                List<Renderer> renderers = new List<Renderer>();
                GameObject[] rootObjects = targetScene.GetRootGameObjects();
                foreach (GameObject root in rootObjects)
                {
                    if (root == null) continue;
                    Renderer[] rComponents = root.GetComponentsInChildren<Renderer>(true);
                    foreach (var r in rComponents)
                    {
                        if (r != null && (r is MeshRenderer || r is SkinnedMeshRenderer))
                        {
                            renderers.Add(r);
                        }
                    }
                }

                var texturesMap = new Dictionary<Texture, TextureReportEntry>();
                var meshesMap = new Dictionary<Mesh, MeshReportEntry>();

                foreach (Renderer r in renderers)
                {
                    Mesh mesh = null;
                    if (r is MeshRenderer)
                    {
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        if (mf != null) mesh = mf.sharedMesh;
                    }
                    else if (r is SkinnedMeshRenderer smr)
                    {
                        mesh = smr.sharedMesh;
                    }

                    if (mesh != null)
                    {
                        if (!meshesMap.TryGetValue(mesh, out MeshReportEntry mEntry))
                        {
                            mEntry = new MeshReportEntry
                            {
                                MeshAsset = mesh,
                                Name = mesh.name,
                                VertexCount = mesh.vertexCount,
                                SubmeshCount = mesh.subMeshCount,
                                IndexFormat = mesh.indexFormat.ToString(),
                                VramSize = EstimateMeshSize(mesh)
                            };
                            meshesMap[mesh] = mEntry;
                            results.MeshBytes += mEntry.VramSize;

                            if (mEntry.VertexCount >= 80000)
                            {
                                results.Warnings.Add(new WarningEntry
                                {
                                    Asset = mesh,
                                    Message = $"Extremely detailed high-poly mesh '{mesh.name}' ({mesh.vertexCount:N0} vertices) loaded in VRAM.",
                                    Severity = "Warning"
                                });
                            }
                        }
                        string rPath = GetHierarchyPathStatic(r.transform);
                        if (!mEntry.ReferencingGameObjectPaths.Contains(rPath))
                        {
                            mEntry.ReferencingGameObjectPaths.Add(rPath);
                        }
                    }

                    Material[] sharedMats = r.sharedMaterials;
                    foreach (Material mat in sharedMats)
                    {
                        if (mat == null) continue;
                        Shader shader = mat.shader;
                        if (shader == null) continue;

                        int propCount = ShaderUtil.GetPropertyCount(shader);
                        for (int i = 0; i < propCount; i++)
                        {
                            if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                            {
                                string propName = ShaderUtil.GetPropertyName(shader, i);
                                Texture tex = mat.GetTexture(propName);
                                if (tex != null)
                                {
                                    if (!texturesMap.TryGetValue(tex, out TextureReportEntry tEntry))
                                    {
                                        tEntry = new TextureReportEntry
                                        {
                                            TextureAsset = tex,
                                            Name = tex.name,
                                            Width = tex.width,
                                            Height = tex.height,
                                            Format = GetTextureFormatString(tex),
                                            HasMipmaps = HasMipmapsEnabled(tex),
                                            VramSize = EstimateTextureSize(tex)
                                        };
                                        texturesMap[tex] = tEntry;
                                        results.TextureBytes += tEntry.VramSize;

                                        if (tEntry.VramSize >= 16 * 1024 * 1024)
                                        {
                                            results.Warnings.Add(new WarningEntry
                                            {
                                                Asset = tex,
                                                Message = $"Large texture asset '{tex.name}' consumes {FormatBytes(tEntry.VramSize)} of VRAM ({tex.width}x{tex.height}).",
                                                Severity = "Error"
                                            });
                                        }

                                        if (tEntry.Format.Contains("RGBA32") || tEntry.Format.Contains("RGB24"))
                                        {
                                            results.Warnings.Add(new WarningEntry
                                            {
                                                Asset = tex,
                                                Message = $"Texture '{tex.name}' is uncompressed ({tEntry.Format}). Convert to DXT/BC7 format.",
                                                Severity = "Warning"
                                            });
                                        }

                                        if (!tEntry.HasMipmaps)
                                        {
                                            results.Warnings.Add(new WarningEntry
                                            {
                                                Asset = tex,
                                                Message = $"Texture '{tex.name}' has Mipmaps disabled. This causes visual aliasing (shimmering) and bad VRAM performance.",
                                                Severity = "Warning"
                                            });
                                        }
                                    }

                                    if (!tEntry.ReferencingMaterials.Contains(mat))
                                    {
                                        tEntry.ReferencingMaterials.Add(mat);
                                    }
                                    string rPath = GetHierarchyPathStatic(r.transform);
                                    if (!tEntry.ReferencingGameObjectPaths.Contains(rPath))
                                    {
                                        tEntry.ReferencingGameObjectPaths.Add(rPath);
                                    }
                                }
                            }
                        }
                    }
                }

                results.Textures = new List<TextureReportEntry>(texturesMap.Values);
                results.Meshes = new List<MeshReportEntry>(meshesMap.Values);

                LightmapData[] lightmaps = LightmapSettings.lightmaps;
                if (lightmaps != null)
                {
                    for (int i = 0; i < lightmaps.Length; i++)
                    {
                        var lm = lightmaps[i];
                        if (lm == null) continue;
                        if (lm.lightmapColor != null)
                        {
                            long size = EstimateTextureSize(lm.lightmapColor);
                            results.EnvAssets.Add(new EnvReportEntry
                            {
                                Asset = lm.lightmapColor,
                                Name = $"Lightmap Color Map {i}",
                                Type = "Lightmap",
                                Details = $"{lm.lightmapColor.width}x{lm.lightmapColor.height} ({GetTextureFormatString(lm.lightmapColor)})",
                                VramSize = size
                            });
                            results.EnvBytes += size;
                        }
                        if (lm.lightmapDir != null)
                        {
                            long size = EstimateTextureSize(lm.lightmapDir);
                            results.EnvAssets.Add(new EnvReportEntry
                            {
                                Asset = lm.lightmapDir,
                                Name = $"Lightmap Direction Map {i}",
                                Type = "Lightmap",
                                Details = $"{lm.lightmapDir.width}x{lm.lightmapDir.height} ({GetTextureFormatString(lm.lightmapDir)})",
                                VramSize = size
                            });
                            results.EnvBytes += size;
                        }
                    }
                }

                ReflectionProbe[] probes = GameObject.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var probe in probes)
                {
                    if (probe == null || probe.bakedTexture == null) continue;
                    long size = EstimateTextureSize(probe.bakedTexture);
                    results.EnvAssets.Add(new EnvReportEntry
                    {
                        Asset = probe.bakedTexture,
                        Name = probe.name,
                        Type = "Reflection Probe",
                        Details = $"Resolution: {probe.resolution} (Cubemap)",
                        VramSize = size
                    });
                    results.EnvBytes += size;
                }

                Material skyboxMat = RenderSettings.skybox;
                if (skyboxMat != null)
                {
                    Shader skyboxShader = skyboxMat.shader;
                    if (skyboxShader != null)
                    {
                        int propCount = ShaderUtil.GetPropertyCount(skyboxShader);
                        for (int i = 0; i < propCount; i++)
                        {
                            if (ShaderUtil.GetPropertyType(skyboxShader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                            {
                                string propName = ShaderUtil.GetPropertyName(skyboxShader, i);
                                Texture tex = skyboxMat.GetTexture(propName);
                                if (tex != null)
                                {
                                    long size = EstimateTextureSize(tex);
                                    results.EnvAssets.Add(new EnvReportEntry
                                    {
                                        Asset = tex,
                                        Name = $"Skybox: {tex.name}",
                                        Type = "Skybox Texture",
                                        Details = $"{tex.width}x{tex.height} ({GetTextureFormatString(tex)})",
                                        VramSize = size
                                    });
                                    results.EnvBytes += size;
                                }
                            }
                        }
                    }
                }

                results.TotalBytes = results.TextureBytes + results.MeshBytes + results.EnvBytes;
            }
            finally
            {
                if (!isCurrent)
                {
                    EditorSceneManager.CloseScene(targetScene, true);
                }
            }

            return results;
        }

        private long EstimateTextureSize(Texture tex)
        {
            if (tex == null) return 0;

            long size = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
            if (size > 0) return size;

            int width = tex.width;
            int height = tex.height;
            bool mipmap = true;
            float bpp = 8.0f;

            if (tex is Texture2D t2d)
            {
                mipmap = t2d.mipmapCount > 1;
                bpp = GetBitsPerPixel(t2d.format);
            }
            else if (tex is Cubemap cube)
            {
                bpp = GetBitsPerPixel(cube.format);
                double byteSize = (width * height * 6.0 * bpp) / 8.0;
                return (long)byteSize;
            }

            double pixels = width * height;
            if (tex is Texture2DArray arr)
            {
                pixels *= arr.depth;
            }

            double bytes = (pixels * bpp) / 8.0;
            if (mipmap)
            {
                bytes *= 1.333333;
            }

            return (long)bytes;
        }

        private long EstimateMeshSize(Mesh mesh)
        {
            if (mesh == null) return 0;

            long size = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(mesh);
            if (size > 0) return size;

            int vertexCount = mesh.vertexCount;
            int indexCount = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                indexCount += (int)mesh.GetIndexCount(i);
            }

            int vertexStride = 12;
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal)) vertexStride += 12;
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent)) vertexStride += 16;
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color)) vertexStride += 4;
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)) vertexStride += 8;
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1)) vertexStride += 8;
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord2)) vertexStride += 8;
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord3)) vertexStride += 8;

            int indexStride = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32 ? 4 : 2;

            return (long)vertexCount * vertexStride + (long)indexCount * indexStride;
        }

        private string GetTextureFormatString(Texture tex)
        {
            if (tex is Texture2D t2d) return t2d.format.ToString();
            if (tex is Texture2DArray arr) return arr.format.ToString();
            if (tex is Cubemap cube) return cube.format.ToString();
            return "Unknown";
        }

        private bool HasMipmapsEnabled(Texture tex)
        {
            if (tex is Texture2D t2d) return t2d.mipmapCount > 1;
            if (tex is Texture2DArray arr) return arr.mipmapCount > 1;
            if (tex is Cubemap cube) return cube.mipmapCount > 1;
            return false;
        }

        private float GetBitsPerPixel(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC2_RGB:
                    return 4.0f;
                case TextureFormat.DXT5:
                case TextureFormat.BC7:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_HDR_4x4:
                    return 8.0f;
                case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_HDR_8x8:
                    return 2.0f;
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_HDR_6x6:
                    return 3.56f;
                case TextureFormat.RGB24:
                    return 24.0f;
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                    return 32.0f;
                case TextureFormat.Alpha8:
                    return 8.0f;
                default:
                    return 8.0f;
            }
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
