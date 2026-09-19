using System.Collections.Generic;
using FishNet.Component.Prediction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TwoBirds.Editor
{
    public sealed class BakeWindow : EditorWindow
    {
        private Vector2 scroll;
        private readonly List<(string message, MessageType type)> log = new();
        private readonly Dictionary<string, Object> logTargets = new();

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
            {
                float height = EditorStyles.helpBox.CalcHeight(new GUIContent(message), position.width - 30f);
                var rect = GUILayoutUtility.GetRect(position.width - 30f, height);
                EditorGUI.HelpBox(rect, message, type);
                if (logTargets.TryGetValue(message, out var target) && target && GUI.Button(rect, GUIContent.none, GUIStyle.none))
                {
                    Selection.activeObject = target;
                    EditorGUIUtility.PingObject(target);
                    if (target is Component || target is GameObject)
                        SceneView.lastActiveSceneView?.FrameSelected();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void Bake()
        {
            log.Clear();
            logTargets.Clear();

            int errors = 0;
            var registry = FindItemRegistry();
            if (registry == null)
            {
                AddLog(("No ItemRegistry asset found — ItemIds will not be resolved.", MessageType.Error));
                errors++;
            }
            else
            {
                for (int i = 0; i < registry.Items.Length; i++)
                {
                    var definition = registry.Items[i];
                    if (definition == null)
                    {
                        AddLog(($"ItemRegistry ID {i + 1}: no ItemDefinition assigned.", MessageType.Error), registry);
                        errors++;
                        continue;
                    }
                    if (definition.WorldPrefab == null)
                    {
                        AddLog(($"ItemDefinition '{definition.name}': no WorldPrefab assigned.", MessageType.Error), definition);
                        errors++;
                        continue;
                    }
                    errors += ValidateWorldItem(definition.WorldPrefab, $"ItemDefinition '{definition.name}' prefab");
                }
            }

            var pickups = Object.FindObjectsByType<BakedPickup>();

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
                AddLog(($"Duplicate BakedId {oldId} on '{HierarchyPath(p.transform)}' — reassigned to {p.BakedId}.", MessageType.Warning), p);
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

            foreach (var p in pickups)
            {
                string path = HierarchyPath(p.transform);
                errors += ValidateWorldItem(p.gameObject, path);

                if (p.Item == null)
                {
                    AddLog(($"'{path}': no ItemDefinition assigned.", MessageType.Warning), p);
                }
                else if (registry != null && registry.GetId(p.Item) == 0)
                {
                    AddLog(($"'{path}': ItemDefinition '{p.Item.name}' not found in ItemRegistry.", MessageType.Error), p);
                    errors++;
                }
                if (registry != null && registry.Get(p.ItemId) == null)
                {
                    AddLog(($"'{path}': ItemId {p.ItemId} does not resolve to an ItemDefinition.", MessageType.Error), p);
                    errors++;
                }
            }

            bool sceneDirty = assigned > 0 || duplicates.Count > 0;

            var perches = Object.FindObjectsByType<BirdPerch>();
            var seenPerchIds = new HashSet<ushort>();
            var perchDuplicates = new List<BirdPerch>();
            ushort maxPerchId = 0;

            foreach (var p in perches)
            {
                if (p.Id == 0) continue;
                if (p.Id > maxPerchId) maxPerchId = p.Id;
                if (!seenPerchIds.Add(p.Id))
                    perchDuplicates.Add(p);
            }

            foreach (var p in perchDuplicates)
            {
                ushort oldId = p.Id;
                if (maxPerchId >= ushort.MaxValue)
                {
                    AddLog(($"BirdPerch duplicate Id {oldId} on '{HierarchyPath(p.transform)}': cannot reassign, Id capacity exceeded.", MessageType.Error), p);
                    errors++;
                    continue;
                }
                p.Id = ++maxPerchId;
                seenPerchIds.Add(p.Id);
                EditorUtility.SetDirty(p);
                AddLog(($"BirdPerch duplicate Id {oldId} on '{HierarchyPath(p.transform)}' — reassigned to {p.Id}.", MessageType.Warning), p);
                sceneDirty = true;
            }

            int perchesAssigned = 0;
            foreach (var p in perches)
            {
                if (p.Id != 0) continue;
                if (maxPerchId >= ushort.MaxValue)
                {
                    AddLog(($"BirdPerch '{HierarchyPath(p.transform)}': cannot assign Id, capacity exceeded.", MessageType.Error), p);
                    errors++;
                    continue;
                }
                p.Id = ++maxPerchId;
                perchesAssigned++;
                EditorUtility.SetDirty(p);
                sceneDirty = true;
            }

            foreach (var p in perches)
            {
                if (p.Biome == 0)
                {
                    AddLog(($"BirdPerch '{HierarchyPath(p.transform)}': Biome is 0.", MessageType.Error), p);
                    errors++;
                }
            }

            var spawnZones = Object.FindObjectsByType<BirdSpawnZone>();
            var seenZoneIds = new HashSet<ushort>();
            foreach (var z in spawnZones)
            {
                if (z.Id == 0)
                {
                    AddLog(($"BirdSpawnZone '{HierarchyPath(z.transform)}': Id is 0.", MessageType.Error), z);
                    errors++;
                }
                else if (!seenZoneIds.Add(z.Id))
                {
                    AddLog(($"BirdSpawnZone '{HierarchyPath(z.transform)}': duplicate Id {z.Id}.", MessageType.Error), z);
                    errors++;
                }
                if (z.Biome == 0)
                {
                    AddLog(($"BirdSpawnZone '{HierarchyPath(z.transform)}': Biome is 0.", MessageType.Error), z);
                    errors++;
                }
            }

            var habitatVolumes = Object.FindObjectsByType<BirdHabitatVolume>();
            var seenHabitatIds = new HashSet<ushort>();
            foreach (var h in habitatVolumes)
            {
                if (h.Id == 0)
                {
                    AddLog(($"BirdHabitatVolume '{HierarchyPath(h.transform)}': Id is 0.", MessageType.Error), h);
                    errors++;
                }
                else if (!seenHabitatIds.Add(h.Id))
                {
                    AddLog(($"BirdHabitatVolume '{HierarchyPath(h.transform)}': duplicate Id {h.Id}.", MessageType.Error), h);
                    errors++;
                }
                if (h.Biome == 0)
                {
                    AddLog(($"BirdHabitatVolume '{HierarchyPath(h.transform)}': Biome is 0.", MessageType.Error), h);
                    errors++;
                }
            }

            if (sceneDirty)
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            log.Insert(0, (
                $"Bake complete: {pickups.Length} pickups ({assigned} new, {duplicates.Count} dupes)" +
                $" · {perches.Length} perches ({perchesAssigned} new, {perchDuplicates.Count} dupes)" +
                $" · {spawnZones.Length} zones · {habitatVolumes.Length} habitats · {errors} error(s).",
                errors > 0 ? MessageType.Warning : MessageType.Info));

            Repaint();
        }

        private int ValidateWorldItem(GameObject root, string path)
        {
            int errors = 0;
            if (root.GetComponent<Rigidbody>() == null)
            {
                AddLog(($"'{path}': no Rigidbody on the root.", MessageType.Error), root);
                errors++;
            }
            if (root.GetComponent<OfflineRigidbody>() == null)
            {
                AddLog(($"'{path}': no OfflineRigidbody on the root.", MessageType.Error), root);
                errors++;
            }
            var item = root.GetComponent<WorldItem>();
            if (item == null)
            {
                AddLog(($"'{path}': no WorldItem on the root.", MessageType.Error), root);
                errors++;
            }
            else if (new SerializedObject(item).FindProperty("visualRoot").objectReferenceValue == null)
            {
                AddLog(($"'{path}': WorldItem has no VisualRoot reference.", MessageType.Error), root);
                errors++;
            }
            if (root.GetComponentInChildren<Collider>(true) == null)
            {
                AddLog(($"'{path}': no Collider — raycasts will not detect this pickup.", MessageType.Error), root);
                errors++;
            }
            if (root.GetComponentInChildren<MeshRenderer>(true) == null
                && root.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                AddLog(($"'{path}': no Renderer — pickup will be invisible.", MessageType.Warning), root);
            return errors;
        }

        private void AddLog((string message, MessageType type) entry, Object target = null)
        {
            log.Add(entry);
            if (target) logTargets[entry.message] = target;
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
