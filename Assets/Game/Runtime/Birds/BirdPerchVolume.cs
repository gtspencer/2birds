using UnityEngine;

namespace TwoBirds
{
    public sealed class BirdPerchVolume : MonoBehaviour
    {
        public ushort Biome = 1;
        public BirdPerchKind Kind;
        public Vector3 Size = new(10f, 8f, 10f);
        [Min(1)] public int Count = 20;
        [Min(0.01f)] public float Spacing = 1f, Clearance = 0.5f;
        public LayerMask GroundMask = 1, SolidMask = 1;
        [Range(0f, 80f)] public float MaximumSlope = 35f;
        public int Seed = 1;
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, Size);
        }
    }
}
