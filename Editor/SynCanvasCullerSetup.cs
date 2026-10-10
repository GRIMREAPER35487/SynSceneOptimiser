using System.Collections.Generic;
using System.IO;
using System.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Editor side of <see cref="SynCanvasDistanceCuller"/>: creates the manager object in the open scene and adds or
    /// removes canvases. Everything is a normal, undoable scene edit the user can see and adjust; nothing is added
    /// during builds.
    /// </summary>
    public static class SynCanvasCullerSetup
    {
        public const string ManagerName = "SynCanvasCuller";
        private const string ProgramAssetFolder = "Assets/SynSceneOptimizer/Udon";
        private const string ProgramAssetPath = ProgramAssetFolder + "/SynCanvasDistanceCuller.asset";

        public const float MinSuggestedDistance = 10f;

        public static SynCanvasDistanceCuller FindCuller(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var culler = root.GetComponentInChildren<SynCanvasDistanceCuller>(true);
                if (culler != null) return culler;
            }
            return null;
        }

        /// <summary>Distance at which a canvas is about too small to read: ten times its largest side, at least 10 m.</summary>
        public static float SuggestDistance(Canvas canvas)
        {
            var rt = canvas.transform as RectTransform;
            if (rt == null) return MinSuggestedDistance;
            Vector3 scale = rt.lossyScale;
            float size = Mathf.Max(Mathf.Abs(rt.rect.width * scale.x), Mathf.Abs(rt.rect.height * scale.y));
            return Mathf.Max(MinSuggestedDistance, Mathf.Ceil(size * 10f));
        }

        /// <summary>Returns the culling distance for <paramref name="canvas"/>, or a negative value if it isn't managed.</summary>
        public static float GetManagedDistance(SynCanvasDistanceCuller culler, Canvas canvas)
        {
            if (culler == null || culler.canvases == null) return -1f;
            int index = System.Array.IndexOf(culler.canvases, canvas);
            return index >= 0 && culler.distances != null && index < culler.distances.Length ? culler.distances[index] : -1f;
        }

        public static void AddCanvases(Scene scene, IEnumerable<Canvas> toAdd)
        {
            var added = toAdd.Where(c => c != null && c.renderMode == RenderMode.WorldSpace).ToList();
            if (added.Count == 0) return;
            if (!EnsureProgramAsset()) return;

            SynCanvasDistanceCuller culler = FindCuller(scene) ?? CreateCuller(scene);
            if (culler == null) return;

            var entries = ReadEntries(culler);
            foreach (Canvas canvas in added)
            {
                if (entries.Any(e => e.Canvas == canvas)) continue;
                entries.Add(new Entry { Canvas = canvas, Distance = SuggestDistance(canvas) });
            }
            WriteEntries(culler, entries, "Add Distance Culling");
        }

        public static void RemoveCanvas(Scene scene, Canvas canvas)
        {
            SynCanvasDistanceCuller culler = FindCuller(scene);
            if (culler == null) return;
            var entries = ReadEntries(culler);
            entries.RemoveAll(e => e.Canvas == canvas || e.Canvas == null);
            WriteEntries(culler, entries, "Remove Distance Culling");
        }

        public static void SetDistance(Scene scene, Canvas canvas, float distance)
        {
            SynCanvasDistanceCuller culler = FindCuller(scene);
            if (culler == null) return;
            var entries = ReadEntries(culler);
            foreach (Entry e in entries)
            {
                if (e.Canvas == canvas) e.Distance = Mathf.Max(1f, distance);
            }
            WriteEntries(culler, entries, "Change Culling Distance");
        }

        /// <summary>Re-measures every managed canvas (after canvases are resized) and drops deleted ones.</summary>
        public static void Refresh(Scene scene)
        {
            SynCanvasDistanceCuller culler = FindCuller(scene);
            if (culler == null) return;
            var entries = ReadEntries(culler);
            entries.RemoveAll(e => e.Canvas == null);
            WriteEntries(culler, entries, "Refresh Distance Culling");
        }

        private class Entry
        {
            public Canvas Canvas;
            public float Distance;
        }

        private static List<Entry> ReadEntries(SynCanvasDistanceCuller culler)
        {
            var entries = new List<Entry>();
            if (culler.canvases == null) return entries;
            for (int i = 0; i < culler.canvases.Length; i++)
            {
                float distance = culler.distances != null && i < culler.distances.Length ? culler.distances[i] : MinSuggestedDistance;
                entries.Add(new Entry { Canvas = culler.canvases[i], Distance = distance });
            }
            return entries;
        }

        private static void WriteEntries(SynCanvasDistanceCuller culler, List<Entry> entries, string undoName)
        {
            Undo.RecordObject(culler, undoName);
            culler.canvases = entries.Select(e => e.Canvas).ToArray();
            culler.distances = entries.Select(e => e.Distance).ToArray();
            culler.localRects = entries.Select(e => GetLocalRect(e.Canvas)).ToArray();
            culler.colliders = entries.Select(e => e.Canvas != null ? e.Canvas.GetComponent<Collider>() : null).ToArray();

            // UdonSharp keeps the values on a backing UdonBehaviour; copy them over so the world uses them
            UdonSharpEditorUtility.CopyProxyToUdon(culler);
            EditorUtility.SetDirty(culler);
            PrefabUtility.RecordPrefabInstancePropertyModifications(culler);
            EditorSceneManager.MarkSceneDirty(culler.gameObject.scene);
        }

        private static Vector4 GetLocalRect(Canvas canvas)
        {
            if (canvas == null || !(canvas.transform is RectTransform rt)) return Vector4.zero;
            Rect r = rt.rect;
            return new Vector4(r.xMin, r.yMin, r.xMax, r.yMax);
        }

        private static SynCanvasDistanceCuller CreateCuller(Scene scene)
        {
            var go = new GameObject(ManagerName);
            Undo.RegisterCreatedObjectUndo(go, "Add Distance Culling");
            SceneManager.MoveGameObjectToScene(go, scene);
            return UdonSharpUndo.AddComponent<SynCanvasDistanceCuller>(go);
        }

        /// <summary>
        /// Every UdonSharp script needs a program asset. Unity can't create assets inside Packages/, so it lives in the
        /// project, like UdonSharp's own "create script" flow does for package scripts.
        /// </summary>
        private static bool EnsureProgramAsset()
        {
            if (UdonSharpProgramAsset.GetProgramAssetForClass(typeof(SynCanvasDistanceCuller)) != null) return true;

            MonoScript script = AssetDatabase.FindAssets("SynCanvasDistanceCuller t:MonoScript")
                .Select(guid => AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid)))
                .FirstOrDefault(s => s != null && s.GetClass() == typeof(SynCanvasDistanceCuller));
            if (script == null)
            {
                Debug.LogError("[SYN SCENE OPTIMIZER] Could not find the SynCanvasDistanceCuller script. Try reimporting the package.");
                return false;
            }

            Directory.CreateDirectory(ProgramAssetFolder);
            AssetDatabase.Refresh();

            var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            programAsset.sourceCsScript = script;
            AssetDatabase.CreateAsset(programAsset, ProgramAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ProgramAssetPath, ImportAssetOptions.ForceSynchronousImport);

            UdonSharpProgramAsset.CompileAllCsPrograms(true);
            return UdonSharpProgramAsset.GetProgramAssetForClass(typeof(SynCanvasDistanceCuller)) != null;
        }
    }
}
