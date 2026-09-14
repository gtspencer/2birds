using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(ItemPlacementZone))]
    public sealed class ItemPlacementZoneEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (GUILayout.Button("Place Items"))
                PlaceItems((ItemPlacementZone)target);
            if (GUILayout.Button("Clear Placed Items"))
                ClearItems((ItemPlacementZone)target);
        }

        private void PlaceItems(ItemPlacementZone zone)
        {
            if (zone.Item == null || zone.Item.WorldPrefab == null)
            {
                Debug.LogError("ItemPlacementZone: Item or WorldPrefab not set.");
                return;
            }

            byte itemId = 0;
            if (zone.Registry != null)
                itemId = zone.Registry.GetId(zone.Item);

            var positions = GeneratePositions(zone);
            Undo.SetCurrentGroupName("Place Items");
            int group = Undo.GetCurrentGroup();

            foreach (var pos in positions)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(zone.Item.WorldPrefab, zone.transform);
                Undo.RegisterCreatedObjectUndo(go, "Place Item");
                go.transform.position = pos;
                go.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

                var pickup = go.GetComponent<BakedPickup>();
                if (pickup == null)
                    pickup = Undo.AddComponent<BakedPickup>(go);
                pickup.Item = zone.Item;
                if (itemId != 0)
                    pickup.ItemId = itemId;
            }

            Undo.CollapseUndoOperations(group);
            Debug.Log($"Placed {positions.Count} items in zone.");
        }

        private List<Vector3> GeneratePositions(ItemPlacementZone zone)
        {
            var results = new List<Vector3>();
            int maxAttempts = zone.Count * 20;
            int attempts = 0;

            while (results.Count < zone.Count && attempts < maxAttempts)
            {
                attempts++;
                Vector3 candidate = zone.Shape == ZoneShape.Sphere
                    ? RandomInSphere(zone)
                    : RandomInBox(zone);

                bool tooClose = false;
                foreach (var existing in results)
                {
                    if (Vector3.Distance(candidate, existing) < zone.MinSpacing)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                if (Physics.Raycast(
                    new Vector3(candidate.x, zone.transform.position.y + zone.RaycastHeight, candidate.z),
                    Vector3.down, out var hit, zone.RaycastHeight * 2f, zone.GroundLayer))
                {
                    results.Add(hit.point);
                }
                else
                {
                    results.Add(candidate);
                }
            }

            if (results.Count < zone.Count)
                Debug.LogWarning($"Could only place {results.Count}/{zone.Count} items (spacing too tight for zone size).");

            return results;
        }

        private static Vector3 RandomInSphere(ItemPlacementZone zone)
        {
            var local = Random.insideUnitSphere * zone.Radius;
            local.y = 0f;
            return zone.transform.position + local;
        }

        private static Vector3 RandomInBox(ItemPlacementZone zone)
        {
            var half = zone.BoxSize * 0.5f;
            var local = new Vector3(
                Random.Range(-half.x, half.x),
                0f,
                Random.Range(-half.z, half.z));
            return zone.transform.position + zone.transform.rotation * local;
        }

        private static void ClearItems(ItemPlacementZone zone)
        {
            Undo.SetCurrentGroupName("Clear Placed Items");
            int group = Undo.GetCurrentGroup();
            for (int i = zone.transform.childCount - 1; i >= 0; i--)
                Undo.DestroyObjectImmediate(zone.transform.GetChild(i).gameObject);
            Undo.CollapseUndoOperations(group);
        }

        private void OnSceneGUI()
        {
            var zone = (ItemPlacementZone)target;
            if (zone.Shape == ZoneShape.Sphere)
            {
                Handles.color = new Color(0.2f, 0.8f, 0.3f, 0.12f);
                Handles.DrawSolidDisc(zone.transform.position, Vector3.up, zone.Radius);
                Handles.color = new Color(0.2f, 0.8f, 0.3f, 0.6f);
                Handles.DrawWireDisc(zone.transform.position, Vector3.up, zone.Radius);
            }
            else
            {
                Handles.color = new Color(0.2f, 0.8f, 0.3f, 0.6f);
                var matrix = Handles.matrix;
                Handles.matrix = zone.transform.localToWorldMatrix;
                Handles.DrawWireCube(Vector3.zero, new Vector3(zone.BoxSize.x, 0.01f, zone.BoxSize.z));
                Handles.matrix = matrix;
            }
        }
    }
}
