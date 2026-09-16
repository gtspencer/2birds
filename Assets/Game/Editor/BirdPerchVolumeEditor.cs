using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    [CustomEditor(typeof(BirdPerchVolume))]
    public sealed class BirdPerchVolumeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var volume = (BirdPerchVolume)target;
            if (GUILayout.Button("Generate perches")) Generate(volume);
            if (GUILayout.Button("Clear generated perches")) Clear(volume);
        }

        private static void Clear(BirdPerchVolume volume)
        {
            foreach (var perch in volume.GetComponentsInChildren<BirdPerch>(true))
                if (perch.GeneratedBy == volume) Undo.DestroyObjectImmediate(perch.gameObject);
        }

        private static void Generate(BirdPerchVolume volume)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Generate bird perches");
            var ids = new HashSet<ushort>();
            var positions = new List<Vector3>();
            var reusable = new List<ushort>();
            foreach (var perch in Object.FindObjectsByType<BirdPerch>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (perch.gameObject.scene != volume.gameObject.scene) continue;
                if (perch.GeneratedBy == volume && perch.transform.IsChildOf(volume.transform)) reusable.Add(perch.Id);
                else { ids.Add(perch.Id); positions.Add(perch.transform.position); }
            }
            reusable.Sort();
            Clear(volume);
            var random = new System.Random(volume.Seed);
            int made = 0, nextId = 1;
            for (int attempt = 0; attempt < volume.Count * 30 && made < volume.Count; attempt++)
            {
                Vector3 point = volume.transform.TransformPoint(Vector3.Scale(volume.Size,
                    new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f)));
                Vector3 normal = Vector3.up;
                if (volume.Kind == BirdPerchKind.Ground)
                {
                    if (!Physics.Raycast(point + Vector3.up * volume.Size.y, Vector3.down, out var hit,
                        volume.Size.y * 2f, volume.GroundMask, QueryTriggerInteraction.Ignore) ||
                        Vector3.Angle(hit.normal, Vector3.up) > volume.MaximumSlope) continue;
                    point = hit.point;
                    normal = hit.normal;
                }
                bool crowded = false;
                foreach (var existing in positions)
                    if ((existing - point).sqrMagnitude < volume.Spacing * volume.Spacing) { crowded = true; break; }
                if (crowded || Physics.CheckSphere(point + normal * (volume.Clearance + 0.02f), volume.Clearance,
                    volume.SolidMask, QueryTriggerInteraction.Ignore)) continue;
                while (nextId <= ushort.MaxValue && ids.Contains((ushort)nextId)) nextId++;
                ushort id = made < reusable.Count && reusable[made] != 0 && !ids.Contains(reusable[made])
                    ? reusable[made] : nextId <= ushort.MaxValue ? (ushort)nextId : (ushort)0;
                if (id == 0) { Debug.LogError("Bird perch ID capacity exceeded.", volume); break; }
                var go = new GameObject($"Perch {id}");
                Undo.RegisterCreatedObjectUndo(go, "Create perch");
                go.transform.SetParent(volume.transform);
                go.transform.SetPositionAndRotation(point, Quaternion.FromToRotation(Vector3.up, normal));
                var perch = Undo.AddComponent<BirdPerch>(go);
                perch.Id = id;
                perch.Biome = volume.Biome;
                perch.Kind = volume.Kind;
                perch.Clearance = volume.Clearance;
                perch.GeneratedBy = volume;
                ids.Add(id);
                positions.Add(point);
                made++;
            }
            Undo.CollapseUndoOperations(group);
            if (made < volume.Count) Debug.LogWarning($"Only {made}/{volume.Count} suitable perches fit this volume.", volume);
        }
    }
}
