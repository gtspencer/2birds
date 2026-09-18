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

            int errors = 0;
            var registry = FindItemRegistry();
            if (registry == null)
            {
                log.Add(("No ItemRegistry asset found — ItemIds will not be resolved.", MessageType.Error));
                errors++;
            }
            else
            {
                for (int i = 0; i < registry.Items.Length; i++)
                {
                    var definition = registry.Items[i];
                    if (definition == null)
                    {
                        log.Add(($"ItemRegistry ID {i + 1}: no ItemDefinition assigned.", MessageType.Error));
                        errors++;
                        continue;
                    }
                    if (definition.WorldPrefab == null)
                    {
                        log.Add(($"ItemDefinition '{definition.name}': no WorldPrefab assigned.", MessageType.Error));
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
                errors += ValidateWorldItem(p.gameObject, path);

                if (p.Item == null)
                {
                    log.Add(($"'{path}': no ItemDefinition assigned.", MessageType.Warning));
                }
                else if (registry != null && registry.GetId(p.Item) == 0)
                {
                    log.Add(($"'{path}': ItemDefinition '{p.Item.name}' not found in ItemRegistry.", MessageType.Error));
                    errors++;
                }
                if (registry != null && registry.Get(p.ItemId) == null)
                {
                    log.Add(($"'{path}': ItemId {p.ItemId} does not resolve to an ItemDefinition.", MessageType.Error));
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
                    log.Add(($"BirdPerch duplicate Id {oldId} on '{HierarchyPath(p.transform)}': cannot reassign, Id capacity exceeded.", MessageType.Error));
                    errors++;
                    continue;
                }
                p.Id = ++maxPerchId;
                seenPerchIds.Add(p.Id);
                EditorUtility.SetDirty(p);
                log.Add(($"BirdPerch duplicate Id {oldId} on '{HierarchyPath(p.transform)}' — reassigned to {p.Id}.", MessageType.Warning));
                sceneDirty = true;
            }

            int perchesAssigned = 0;
            foreach (var p in perches)
            {
                if (p.Id != 0) continue;
                if (maxPerchId >= ushort.MaxValue)
                {
                    log.Add(($"BirdPerch '{HierarchyPath(p.transform)}': cannot assign Id, capacity exceeded.", MessageType.Error));
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
                    log.Add(($"BirdPerch '{HierarchyPath(p.transform)}': Biome is 0.", MessageType.Error));
                    errors++;
                }
            }

            var spawnZones = Object.FindObjectsByType<BirdSpawnZone>();
            var seenZoneIds = new HashSet<ushort>();
            foreach (var z in spawnZones)
            {
                if (z.Id == 0)
                {
                    log.Add(($"BirdSpawnZone '{HierarchyPath(z.transform)}': Id is 0.", MessageType.Error));
                    errors++;
                }
                else if (!seenZoneIds.Add(z.Id))
                {
                    log.Add(($"BirdSpawnZone '{HierarchyPath(z.transform)}': duplicate Id {z.Id}.", MessageType.Error));
                    errors++;
                }
                if (z.Biome == 0)
                {
                    log.Add(($"BirdSpawnZone '{HierarchyPath(z.transform)}': Biome is 0.", MessageType.Error));
                    errors++;
                }
            }

            var habitatVolumes = Object.FindObjectsByType<BirdHabitatVolume>();
            var seenHabitatIds = new HashSet<ushort>();
            foreach (var h in habitatVolumes)
            {
                if (h.Id == 0)
                {
                    log.Add(($"BirdHabitatVolume '{HierarchyPath(h.transform)}': Id is 0.", MessageType.Error));
                    errors++;
                }
                else if (!seenHabitatIds.Add(h.Id))
                {
                    log.Add(($"BirdHabitatVolume '{HierarchyPath(h.transform)}': duplicate Id {h.Id}.", MessageType.Error));
                    errors++;
                }
                if (h.Biome == 0)
                {
                    log.Add(($"BirdHabitatVolume '{HierarchyPath(h.transform)}': Biome is 0.", MessageType.Error));
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
                log.Add(($"'{path}': no Rigidbody on the root.", MessageType.Error));
                errors++;
            }
            if (root.GetComponent<OfflineRigidbody>() == null)
            {
                log.Add(($"'{path}': no OfflineRigidbody on the root.", MessageType.Error));
                errors++;
            }
            var item = root.GetComponent<WorldItem>();
            if (item == null)
            {
                log.Add(($"'{path}': no WorldItem on the root.", MessageType.Error));
                errors++;
            }
            else if (new SerializedObject(item).FindProperty("visualRoot").objectReferenceValue == null)
            {
                log.Add(($"'{path}': WorldItem has no VisualRoot reference.", MessageType.Error));
                errors++;
            }
            if (root.GetComponentInChildren<Collider>(true) == null)
            {
                log.Add(($"'{path}': no Collider — raycasts will not detect this pickup.", MessageType.Error));
                errors++;
            }
            if (root.GetComponentInChildren<MeshRenderer>(true) == null
                && root.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                log.Add(($"'{path}': no Renderer — pickup will be invisible.", MessageType.Warning));
            return errors;
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
