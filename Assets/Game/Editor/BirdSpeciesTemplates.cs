using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    public static class BirdSpeciesTemplates
    {
        [MenuItem("Assets/Create/Two Birds/Birds/Templates/Small Bush Bird")]
        private static void Bush() => Create("Small Bush Bird", BirdBehavior.Bush,
            BirdCapabilities.Flight | BirdCapabilities.Tree | BirdCapabilities.Ground, 0.15f, 0.3f);

        [MenuItem("Assets/Create/Two Birds/Birds/Templates/Large Soaring Bird")]
        private static void Soaring() => Create("Large Soaring Bird", BirdBehavior.Soaring,
            BirdCapabilities.Flight | BirdCapabilities.Ground, 0.6f, 0.8f);

        [MenuItem("Assets/Create/Two Birds/Birds/Templates/Water Bird")]
        private static void Water() => Create("Water Bird", BirdBehavior.Water,
            BirdCapabilities.Flight | BirdCapabilities.Swim, 0.25f, 0.35f);

        [MenuItem("Assets/Create/Two Birds/Birds/Templates/Flightless Ground Bird")]
        private static void Ground() => Create("Flightless Ground Bird", BirdBehavior.Ground,
            BirdCapabilities.Ground, 0.25f, 0.35f);

        [MenuItem("Assets/Create/Two Birds/Birds/Templates/Short Flight Ground Bird")]
        private static void ShortFlight() => Create("Short Flight Ground Bird", BirdBehavior.Ground,
            BirdCapabilities.Ground | BirdCapabilities.ShortFlight, 0.25f, 0.35f);

        private static void Create(string name, BirdBehavior behavior, BirdCapabilities capabilities, float radius, float offset)
        {
            var used = new HashSet<ushort>();
            foreach (string guid in AssetDatabase.FindAssets("t:BirdSpecies"))
            {
                var existing = AssetDatabase.LoadAssetAtPath<BirdSpecies>(AssetDatabase.GUIDToAssetPath(guid));
                if (existing) used.Add(existing.Id);
            }
            int id = 1;
            while (id <= ushort.MaxValue && used.Contains((ushort)id)) id++;
            if (id > ushort.MaxValue) { Debug.LogError("Bird species ID capacity exceeded."); return; }
            var species = ScriptableObject.CreateInstance<BirdSpecies>();
            species.Id = (ushort)id; species.Behavior = behavior; species.Capabilities = capabilities;
            species.Radius = radius; species.LandingOffset = offset;
            if (behavior == BirdBehavior.Soaring) { species.FlightSpeed = 8f; species.FlightHeight = 5f; }
            if ((capabilities & BirdCapabilities.ShortFlight) != 0) { species.FlightSpeed = 4f; species.FlightHeight = 0.5f; species.ShortFlightDistance = 3f; }
            ProjectWindowUtil.CreateAsset(species, name + ".asset");
        }
    }
}
