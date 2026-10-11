using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// A set of materials that could become one: either exact duplicates, or materials identical except for the
    /// main texture's tiling/offset, which could be baked into mesh UVs instead.
    /// </summary>
    public class SynMaterialGroup
    {
        public bool ExactDuplicates;
        public string ShaderName;
        public string MainTextureName;
        public List<Material> Materials = new List<Material>();
        public Dictionary<Material, int> RendererCounts = new Dictionary<Material, int>();
        public Dictionary<Material, Vector4> Tilings = new Dictionary<Material, Vector4>();
        public int RendererCount;

        // Tiling variants only
        public bool CanBakeTiling;
        public string BlockReason;
        public int MeshCopiesNeeded;   // meshes used with more than one tiling need one copy per extra tiling

        public int MaterialsSaved => Materials.Count - 1;
    }

    /// <summary>
    /// Read-only analysis of the materials scene renderers use: which are duplicates, and which differ only by
    /// tiling. Nothing in the scene or project is changed.
    /// </summary>
    public static class SynMaterialAnalysis
    {
        public class Report
        {
            public int MaterialCount;
            public int RendererCount;
            public List<SynMaterialGroup> Groups = new List<SynMaterialGroup>();
        }

        public static Report AnalyzeScene(Scene scene)
        {
            var report = new Report();
            var usage = new Dictionary<Material, List<Renderer>>();

            foreach (Renderer r in SynSceneQuery.GetAllRenderers(scene))
            {
                if (r == null) continue;
                report.RendererCount++;
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    if (!usage.TryGetValue(m, out var list))
                    {
                        list = new List<Renderer>();
                        usage[m] = list;
                    }
                    if (!list.Contains(r)) list.Add(r);
                }
            }
            report.MaterialCount = usage.Count;

            // Exact duplicates first; whatever is left can still be a tiling variant of something
            var exactGroups = usage.Keys.GroupBy(m => Signature(m, includeMainTiling: true)).Where(g => g.Count() > 1).ToList();
            var inExactGroup = new HashSet<Material>();
            foreach (var g in exactGroups)
            {
                var group = NewGroup(g.ToList(), usage, exact: true);
                report.Groups.Add(group);
                foreach (Material m in g) inExactGroup.Add(m);
            }

            // One representative per exact-duplicate set, so duplicates and tiling variants aren't counted twice
            var representatives = usage.Keys.Where(m => !inExactGroup.Contains(m)).ToList();
            representatives.AddRange(exactGroups.Select(g => g.First()));

            foreach (var g in representatives.GroupBy(m => Signature(m, includeMainTiling: false)).Where(g => g.Count() > 1))
            {
                var group = NewGroup(g.ToList(), usage, exact: false);
                EvaluateTilingBake(group, usage);
                report.Groups.Add(group);
            }

            report.Groups = report.Groups
                .OrderByDescending(gr => gr.ExactDuplicates || gr.CanBakeTiling)
                .ThenByDescending(gr => gr.MaterialsSaved)
                .ThenByDescending(gr => gr.RendererCount)
                .ToList();
            return report;
        }

        private static SynMaterialGroup NewGroup(List<Material> materials, Dictionary<Material, List<Renderer>> usage, bool exact)
        {
            var group = new SynMaterialGroup
            {
                ExactDuplicates = exact,
                ShaderName = materials[0].shader.name,
            };
            string mainProp = GetMainTextureProperty(materials[0]);
            Texture mainTex = mainProp != null ? materials[0].GetTexture(mainProp) : null;
            group.MainTextureName = mainTex != null ? mainTex.name : "(no main texture)";

            foreach (Material m in materials.OrderBy(m => m.name))
            {
                group.Materials.Add(m);
                int count = usage[m].Count;
                group.RendererCounts[m] = count;
                group.RendererCount += count;
                if (mainProp != null)
                {
                    Vector2 s = m.GetTextureScale(mainProp), o = m.GetTextureOffset(mainProp);
                    group.Tilings[m] = new Vector4(s.x, s.y, o.x, o.y);
                }
            }
            return group;
        }

        /// <summary>
        /// Whether the main tiling can move into the mesh UVs: the shader must read its primary maps from UV0 with
        /// that tiling only (no triplanar, world-space UVs, rotation or scrolling), and the renderers must be plain
        /// mesh renderers so their mesh can be copied.
        /// </summary>
        private static void EvaluateTilingBake(SynMaterialGroup group, Dictionary<Material, List<Renderer>> usage)
        {
            string shader = group.ShaderName.ToLowerInvariant();
            bool mochie = shader.Contains("mochie/standard");
            bool unityStandard = group.ShaderName == "Standard" || group.ShaderName == "Standard (Specular setup)";

            if (!mochie && !unityStandard)
            {
                group.BlockReason = "this shader isn't one the optimizer knows how to bake tiling for (Mochie Standard / Unity Standard)";
                return;
            }

            foreach (Material m in group.Materials)
            {
                if (mochie)
                {
                    if (m.IsKeywordEnabled("_TRIPLANAR_ON")) { group.BlockReason = $"'{m.name}' uses triplanar mapping"; return; }
                    if (GetInt(m, "_UVMainSet") != 0) { group.BlockReason = $"'{m.name}' reads its main maps from a UV set other than UV0 (or world/local space)"; return; }
                    if (GetFloat(m, "_UVMainRotation") != 0f) { group.BlockReason = $"'{m.name}' rotates its UVs"; return; }
                    if (m.HasProperty("_UVMainScroll") && m.GetVector("_UVMainScroll") != Vector4.zero) { group.BlockReason = $"'{m.name}' scrolls its UVs"; return; }
                    if (m.IsKeywordEnabled("_PARALLAX_ON") || m.IsKeywordEnabled("_PARALLAXMAP")) { group.BlockReason = $"'{m.name}' uses parallax, which scales with the main tiling"; return; }
                }

                foreach (Renderer r in usage[m])
                {
                    if (!(r is MeshRenderer) || r.GetComponent<MeshFilter>()?.sharedMesh == null)
                    {
                        group.BlockReason = $"'{m.name}' is used on '{r.name}', which isn't a plain mesh renderer";
                        return;
                    }
                    if (SynProtectionData.IsProtected(r.gameObject))
                    {
                        group.BlockReason = $"'{m.name}' is used on protected object '{r.name}'";
                        return;
                    }
                }
            }

            // A mesh shared by renderers with different tilings needs a UV copy per extra tiling
            var tilingsPerMesh = new Dictionary<Mesh, HashSet<Vector4>>();
            foreach (Material m in group.Materials)
            {
                foreach (Renderer r in usage[m])
                {
                    Mesh mesh = r.GetComponent<MeshFilter>().sharedMesh;
                    if (!tilingsPerMesh.TryGetValue(mesh, out var set))
                    {
                        set = new HashSet<Vector4>();
                        tilingsPerMesh[mesh] = set;
                    }
                    set.Add(group.Tilings.TryGetValue(m, out Vector4 t) ? t : new Vector4(1, 1, 0, 0));
                }
            }
            group.MeshCopiesNeeded = tilingsPerMesh.Values.Sum(set => set.Contains(new Vector4(1, 1, 0, 0)) ? set.Count - 1 : set.Count);
            group.CanBakeTiling = true;
        }

        public static string GetMainTextureProperty(Material m)
        {
            Shader shader = m.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture &&
                    (shader.GetPropertyFlags(i) & ShaderPropertyFlags.MainTexture) != 0)
                {
                    return shader.GetPropertyName(i);
                }
            }
            return m.HasProperty("_MainTex") ? "_MainTex" : null;
        }

        /// <summary>Everything that affects how the material renders, optionally without the main texture's tiling.</summary>
        public static string Signature(Material m, bool includeMainTiling)
        {
            var sb = new StringBuilder();
            Shader shader = m.shader;
            string mainProp = GetMainTextureProperty(m);
            sb.Append(shader.GetInstanceID()).Append('|').Append(m.renderQueue).Append('|').Append(m.enableInstancing).Append('|').Append(m.doubleSidedGI);
            foreach (string kw in m.shaderKeywords.OrderBy(k => k)) sb.Append("|k:").Append(kw);
            for (int p = 0; p < m.passCount; p++)
            {
                string pass = m.GetPassName(p);
                if (!m.GetShaderPassEnabled(pass)) sb.Append("|off:").Append(pass);
            }
            foreach (string tag in new[] { "RenderType" }) sb.Append("|t:").Append(m.GetTag(tag, false));

            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                sb.Append('|').Append(name).Append('=');
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Texture:
                        Texture tex = m.GetTexture(name);
                        sb.Append(tex != null ? tex.GetInstanceID() : 0);
                        if (includeMainTiling || name != mainProp)
                        {
                            sb.Append('@').Append(F(m.GetTextureScale(name))).Append('+').Append(F(m.GetTextureOffset(name)));
                        }
                        break;
                    case ShaderPropertyType.Color:
                        sb.Append(F(m.GetColor(name)));
                        break;
                    case ShaderPropertyType.Vector:
                        sb.Append(F(m.GetVector(name)));
                        break;
                    case ShaderPropertyType.Int:
                        sb.Append(m.GetInteger(name));
                        break;
                    default:
                        sb.Append(m.GetFloat(name).ToString("R", CultureInfo.InvariantCulture));
                        break;
                }
            }
            return sb.ToString();
        }

        private static string F(Vector4 v) => string.Join(",", v.x.ToString("R", CultureInfo.InvariantCulture), v.y.ToString("R", CultureInfo.InvariantCulture), v.z.ToString("R", CultureInfo.InvariantCulture), v.w.ToString("R", CultureInfo.InvariantCulture));
        private static string F(Vector2 v) => F(new Vector4(v.x, v.y, 0, 0));
        private static string F(Color c) => F((Vector4)c);

        private static int GetInt(Material m, string prop) => m.HasProperty(prop) ? Mathf.RoundToInt(m.GetFloat(prop)) : 0;
        private static float GetFloat(Material m, string prop) => m.HasProperty(prop) ? m.GetFloat(prop) : 0f;
    }

    /// <summary>Window → Synthos → Material Audit: which materials could be merged, and why others can't.</summary>
    public class SynMaterialAuditWindow : EditorWindow
    {
        private SynMaterialAnalysis.Report report;
        private string scannedScene;
        private Vector2 scroll;
        private bool showBlocked = true;
        private readonly HashSet<SynMaterialGroup> expanded = new HashSet<SynMaterialGroup>();

        [MenuItem("Window/Synthos/Material Audit")]
        public static void ShowWindow()
        {
            var window = GetWindow<SynMaterialAuditWindow>("Material Audit");
            window.minSize = new Vector2(560, 320);
            window.Scan();
        }

        private void OnEnable()
        {
            if (report == null) Scan();
        }

        private void Scan()
        {
            Scene scene = SceneManager.GetActiveScene();
            SynSceneQuery.ClearCache();
            report = SynMaterialAnalysis.AnalyzeScene(scene);
            scannedScene = scene.name;
            expanded.Clear();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Scene: {scannedScene}", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            showBlocked = GUILayout.Toggle(showBlocked, "Show groups that can't merge", GUILayout.Width(190));
            if (GUILayout.Button("Rescan", GUILayout.Width(80))) Scan();
            EditorGUILayout.EndHorizontal();

            if (report == null) return;
            if (report.Groups.Any(g => g.Materials.Any(m => m == null)))
            {
                EditorGUILayout.HelpBox("Materials changed since the last scan.", MessageType.Warning);
                if (GUILayout.Button("Rescan")) Scan();
                return;
            }

            var exact = report.Groups.Where(g => g.ExactDuplicates).ToList();
            var bakeable = report.Groups.Where(g => !g.ExactDuplicates && g.CanBakeTiling).ToList();
            var blocked = report.Groups.Where(g => !g.ExactDuplicates && !g.CanBakeTiling).ToList();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"{report.MaterialCount} materials on {report.RendererCount} renderers in the open scene (before optimization).");
            EditorGUILayout.LabelField($"Exact duplicates: {exact.Sum(g => g.Materials.Count)} materials could be {exact.Count} ({exact.Sum(g => g.MaterialsSaved)} fewer), with no mesh changes.", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Same except tiling: {bakeable.Sum(g => g.Materials.Count)} materials could be {bakeable.Count} ({bakeable.Sum(g => g.MaterialsSaved)} fewer) by baking tiling into mesh UVs, needing {bakeable.Sum(g => g.MeshCopiesNeeded)} extra mesh copies.", EditorStyles.miniLabel);
            if (blocked.Count > 0)
            {
                EditorGUILayout.LabelField($"{blocked.Sum(g => g.Materials.Count)} more materials differ only by tiling but can't be baked (reasons below).", EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.HelpBox("Fewer materials means more objects can share a static batch or an instanced draw, and fewer SetPass calls. The palette and other passes may merge some of these already; this shows the scene as saved.", MessageType.None);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (SynMaterialGroup group in report.Groups)
            {
                if (!showBlocked && !group.ExactDuplicates && !group.CanBakeTiling) continue;
                DrawGroup(group);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawGroup(SynMaterialGroup group)
        {
            string kind = group.ExactDuplicates ? "Duplicates" : group.CanBakeTiling ? "Tiling only" : "Tiling only (can't bake)";
            string label = $"[{kind}]  {group.MainTextureName}  -  {group.Materials.Count} materials, {group.RendererCount} renderers  ({group.ShaderName})";
            bool open = expanded.Contains(group);
            bool newOpen = EditorGUILayout.Foldout(open, label, true);
            if (newOpen != open)
            {
                if (newOpen) expanded.Add(group); else expanded.Remove(group);
            }
            if (!newOpen) return;

            EditorGUI.indentLevel++;
            if (!group.ExactDuplicates)
            {
                EditorGUILayout.LabelField(group.CanBakeTiling
                    ? $"Could become 1 material by baking tiling into mesh UVs ({group.MeshCopiesNeeded} extra mesh copies for meshes used with several tilings)."
                    : $"Can't bake: {group.BlockReason}.", EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("Identical settings: could simply become 1 material.", EditorStyles.miniLabel);
            }

            foreach (Material m in group.Materials)
            {
                EditorGUILayout.BeginHorizontal();
                string tiling = group.Tilings.TryGetValue(m, out Vector4 t) ? $"tiling {t.x:0.###} x {t.y:0.###}, offset {t.z:0.###}, {t.w:0.###}" : "";
                EditorGUILayout.LabelField($"{m.name}   {tiling}   ({group.RendererCounts[m]} renderers)", EditorStyles.miniLabel);
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    Selection.activeObject = m;
                    EditorGUIUtility.PingObject(m);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }
    }
}
