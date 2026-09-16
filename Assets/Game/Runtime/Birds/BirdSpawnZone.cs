using System;
using UnityEngine;

namespace TwoBirds
{
    [Serializable] public struct BirdWeight { public BirdSpecies Species; [Min(0f)] public float Weight; }

    public sealed class BirdSpawnZone : MonoBehaviour
    {
        [Min(1)] public ushort Id, Biome = 1;
        public Vector3 Size = new(20f, 10f, 20f);
        [Min(0)] public int Minimum = 5, Maximum = 10;
        public Vector2 ReplacementSeconds = new(3f, 8f);
        public BirdWeight[] Species = Array.Empty<BirdWeight>();
        public bool Contains(Vector3 point) => new Bounds(Vector3.zero, Size).Contains(transform.InverseTransformPoint(point));
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, Size);
        }
    }
}
