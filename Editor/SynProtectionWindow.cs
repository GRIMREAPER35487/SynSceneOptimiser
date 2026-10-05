using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Synthos.SynSceneOptimizer
{
    public class SynProtectionWindow : EditorWindow
    {
        private Vector2 scrollPos;
        private SynProtectionData data;

        [MenuItem("Window/Synthos/Protected Objects")]
        [MenuItem("Tools/SynOptimizerFIX/Protected Objects")]
        public static void ShowWindow()
        {
            var window = GetWindow<SynProtectionWindow>("Protected Objects");
            window.minSize = new Vector2(400, 500);
            window.Show();
        }

        private void OnEnable()
        {
            data = SynProtectionData.GetInstance();
        }

        private void OnGUI()
        {
            if (data == null)
            {
                data = SynProtectionData.GetInstance();
                if (data == null)
                {
                    EditorGUILayout.HelpBox("Could not load or create protection settings data.", MessageType.Error);
                    return;
                }
            }

            // Sleek Header
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Space(10);
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleLeft
            };
            titleStyle.normal.textColor = new Color(0.0f, 0.9f, 0.46f); // Synthos neon green tint
            GUILayout.Label("PROTECTED SCENE OBJECTS", titleStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Space(10);
            GUILayout.Label("Assets and hierarchy objects in these lists will be completely bypassed by all optimizer passes.", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);

            // Drag and Drop Box
            var dropArea = GUILayoutUtility.GetRect(0.0f, 60.0f, GUILayout.ExpandWidth(true));
            var dropBoxStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = 12
            };
            dropBoxStyle.normal.textColor = Color.gray;

            GUI.Box(dropArea, "Drag & Drop GameObjects, Materials, or Meshes Here to Protect Them", dropBoxStyle);

            HandleDragAndDrop(dropArea);

            GUILayout.Space(10);

            // Clear All Button
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear All Protected Objects", GUILayout.Width(200), GUILayout.Height(25)))
            {
                if (EditorUtility.DisplayDialog("Clear All Protection", "Are you sure you want to clear all protected GameObjects, Materials, Meshes, and Textures?", "Yes", "No"))
                {
                    Undo.RecordObject(data, "Clear All Protected Objects");
                    data.protectedSceneObjects.Clear();
                    data.protectedGameObjects.Clear();
                    data.protectedMaterials.Clear();
                    data.protectedMeshes.Clear();
                    data.protectedTextures.Clear();
                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();
                }
            }
            GUILayout.Space(10);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            scrollPos = GUILayout.BeginScrollView(scrollPos);

            // 1. GameObjects list (Persistent Scene Objects)
            DrawCategoryHeader("Protected GameObjects (Hierarchy / Scene Props)", new Color(0.3f, 0.6f, 0.9f));
            
            // Auto-migrate legacy items if any
            if (data.protectedGameObjects.Count > 0)
            {
                foreach (var legacyGo in data.protectedGameObjects)
                {
                    if (legacyGo != null)
                    {
                        data.protectedSceneObjects.Add(new SynPersistentObjectReference(legacyGo));
                    }
                }
                data.protectedGameObjects.Clear();
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
            }

            DrawList(data.protectedSceneObjects, (refObj, index) =>
            {
                var currentGo = refObj.Resolve() as GameObject;
                EditorGUI.BeginChangeCheck();
                var newGo = (GameObject)EditorGUILayout.ObjectField(currentGo, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(data, "Update Protected GameObject");
                    refObj.Set(newGo);
                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssets();
                }
            }, index => {
                Undo.RecordObject(data, "Remove Protected GameObject");
                data.protectedSceneObjects.RemoveAt(index);
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
            });

            GUILayout.Space(15);

            // 2. Materials list
            DrawCategoryHeader("Protected Materials (Assets)", new Color(0.9f, 0.4f, 0.4f));
            DrawList(data.protectedMaterials, (mat, index) =>
            {
                EditorGUI.BeginChangeCheck();
                var newMat = (Material)EditorGUILayout.ObjectField(mat, typeof(Material), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(data, "Update Protected Material");
                    data.protectedMaterials[index] = newMat;
                    EditorUtility.SetDirty(data);
                }
            }, index => {
                Undo.RecordObject(data, "Remove Protected Material");
                data.protectedMaterials.RemoveAt(index);
                EditorUtility.SetDirty(data);
            });

            GUILayout.Space(15);

            // 3. Meshes list
            DrawCategoryHeader("Protected Meshes (Assets)", new Color(0.8f, 0.8f, 0.3f));
            DrawList(data.protectedMeshes, (mesh, index) =>
            {
                EditorGUI.BeginChangeCheck();
                var newMesh = (Mesh)EditorGUILayout.ObjectField(mesh, typeof(Mesh), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(data, "Update Protected Mesh");
                    data.protectedMeshes[index] = newMesh;
                    EditorUtility.SetDirty(data);
                }
            }, index => {
                Undo.RecordObject(data, "Remove Protected Mesh");
                data.protectedMeshes.RemoveAt(index);
                EditorUtility.SetDirty(data);
            });

            GUILayout.Space(15);

            // 4. Textures list
            DrawCategoryHeader("Protected Textures (Assets)", new Color(0.2f, 0.9f, 0.6f));
            DrawList(data.protectedTextures, (tex, index) =>
            {
                EditorGUI.BeginChangeCheck();
                var newTex = (Texture2D)EditorGUILayout.ObjectField(tex, typeof(Texture2D), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(data, "Update Protected Texture");
                    data.protectedTextures[index] = newTex;
                    EditorUtility.SetDirty(data);
                }
            }, index => {
                Undo.RecordObject(data, "Remove Protected Texture");
                data.protectedTextures.RemoveAt(index);
                EditorUtility.SetDirty(data);
            });

            GUILayout.EndScrollView();
            GUILayout.Space(10);
        }

        private void HandleDragAndDrop(Rect dropArea)
        {
            var evt = Event.current;
            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (!dropArea.Contains(evt.mousePosition))
                        break;

                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();

                        Undo.RecordObject(data, "Drag & Drop Protection Settings");
                        bool changed = false;

                        foreach (var draggedObject in DragAndDrop.objectReferences)
                        {
                            if (draggedObject is GameObject go)
                            {
                                bool exists = false;
                                foreach (var p in data.protectedSceneObjects)
                                {
                                    if (p.Resolve() == go) { exists = true; break; }
                                }
                                if (!exists)
                                {
                                    data.protectedSceneObjects.Add(new SynPersistentObjectReference(go));
                                    changed = true;
                                }
                            }
                            else if (draggedObject is Material mat)
                            {
                                if (!data.protectedMaterials.Contains(mat))
                                {
                                    data.protectedMaterials.Add(mat);
                                    changed = true;
                                }
                            }
                            else if (draggedObject is Mesh mesh)
                            {
                                if (!data.protectedMeshes.Contains(mesh))
                                {
                                    data.protectedMeshes.Add(mesh);
                                    changed = true;
                                }
                            }
                            else if (draggedObject is Texture2D tex)
                            {
                                if (!data.protectedTextures.Contains(tex))
                                {
                                    data.protectedTextures.Add(tex);
                                    changed = true;
                                }
                            }
                        }

                        if (changed)
                        {
                            EditorUtility.SetDirty(data);
                            AssetDatabase.SaveAssets();
                        }
                    }
                    break;
            }
        }

        private void DrawCategoryHeader(string label, Color color)
        {
            var bgStyle = new GUIStyle();
            bgStyle.normal.background = Texture2D.whiteTexture;

            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = color * 0.3f;
            GUILayout.BeginHorizontal(bgStyle);
            GUI.backgroundColor = prevColor;

            var labelStyle = new GUIStyle(EditorStyles.boldLabel);
            labelStyle.normal.textColor = color * 1.5f;
            GUILayout.Label(label, labelStyle);
            
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        private void DrawList<T>(List<T> list, System.Action<T, int> drawItem, System.Action<int> removeItem)
        {
            if (list == null || list.Count == 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(15);
                GUILayout.Label("(None protected)", EditorStyles.miniLabel);
                GUILayout.EndHorizontal();
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(15);
                drawItem(list[i], i);
                
                var prevColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                if (GUILayout.Button("X", GUILayout.Width(25)))
                {
                    removeItem(i);
                    AssetDatabase.SaveAssets();
                    GUI.backgroundColor = prevColor;
                    GUILayout.EndHorizontal();
                    break;
                }
                GUI.backgroundColor = prevColor;
                GUILayout.EndHorizontal();
            }
        }
    }
}
