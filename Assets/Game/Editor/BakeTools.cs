using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TwoBirds.Editor
{
    public sealed class BakeWindow : EditorWindow
    {
        private Vector2 scroll;
        private readonly List<(string message, MessageType type)> log = new();

        [MenuItem("Two Birds/Bake World")]
        public static void ShowWindow()
        {
            GetWindow<BakeWindow>("Bake World");
        }

        private void OnGUI()
        {
            if (GUILayout.Button("Bake", GUILayout.Height(28)))
                Bake();

            EditorGUILayout.Space();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var (message, type) in log)
                EditorGUILayout.HelpBox(message, type);
            EditorGUILayout.EndScrollView();
        }

        private void Bake()
        {
            log.Clear();

            var registry = FindItemRegistry();
            if (registry == null)
                log.Add(("No ItemRegistry asset found — ItemIds will not be resolved.", MessageType.Warning));

            var pickups = Object.FindObjectsByType<BakedPickup>(FindObjectsSortMode.None);
            if (pickups.Length == 0)
            {
                log.Add(("No BakedPickup components found in scene.", MessageType.Info));
                Repaint();
                return;
            }

            var seenIds = new HashSet<uint>();
            var duplicates = new List<BakedPickup>();
            uint maxId = 0;

            foreach (var p in pickups)
            {
                if (p.BakedId == 0) continue;
                if (p.BakedId > maxId) maxId = p.BakedId;
                if (!seenIds.Add(p.BakedId))
                    duplicates.Add(p);
            }

            foreach (var p in duplicates)
            {
                uint oldId = p.BakedId;
                p.BakedId = ++maxId;
                seenIds.Add(p.BakedId);
                EditorUtility.SetDirty(p);
                log.Add(($"Duplicate BakedId {oldId} on '{HierarchyPath(p.transform)}' — reassigned to {p.BakedId}.", MessageType.Warning));
            }

            int assigned = 0;
            foreach (var p in pickups)
            {
                if (p.BakedId != 0) continue;
                p.BakedId = ++maxId;
                seenIds.Add(p.BakedId);
                assigned++;
                EditorUtility.SetDirty(p);
            }

            int errors = 0;
            foreach (var p in pickups)
            {
                if (p.Item != null && registry != null)
                {
                    byte id = registry.GetId(p.Item);
                    if (id != 0 && p.ItemId != id)
                    {
                        p.ItemId = id;
                        EditorUtility.SetDirty(p);
                    }
                }

                string path = HierarchyPath(p.transform);

                if (p.GetComponentInChildren<Collider>() == null)
                {
                    log.Add(($"'{path}': no Collider — raycasts will not detect this pickup.", MessageType.Error));
                    errors++;
                }

                if (p.GetComponentInChildren<MeshRenderer>() == null
                    && p.GetComponentInChildren<SkinnedMeshRenderer>() == null)
                {
                    log.Add(($"'{path}': no Renderer — pickup will be invisible.", MessageType.Warning));
                }

                if (p.Item == null)
                {
                    log.Add(($"'{path}': no ItemDefinition assigned.", MessageType.Warning));
                }
                else if (registry != null && registry.GetId(p.Item) == 0)
                {
                    log.Add(($"'{path}': ItemDefinition '{p.Item.name}' not found in ItemRegistry.", MessageType.Error));
                    errors++;
                }
            }

            if (assigned > 0 || duplicates.Count > 0)
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            log.Insert(0, (
                $"Bake complete: {pickups.Length} pickups, {assigned} new IDs assigned, {duplicates.Count} duplicates fixed, {errors} error(s).",
                errors > 0 ? MessageType.Warning : MessageType.Info));

            Repaint();
        }

        private static string HierarchyPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        private static ItemRegistry FindItemRegistry()
        {
            var guids = AssetDatabase.FindAssets("t:ItemRegistry");
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<ItemRegistry>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }
    }
}
