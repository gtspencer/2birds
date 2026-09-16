using UnityEngine;

namespace TwoBirds
{
    public sealed class BirdHabitatVolume : MonoBehaviour
    {
        [Min(1)] public ushort Id, Biome = 1;
        public BirdHabitatKind Kind;
        public Vector3 Size = new(30f, 10f, 30f);
        [Tooltip("World-space surface height. Water volumes must be upright.")]
        public float WaterHeight;
        public bool Contains(Vector3 point) => new Bounds(Vector3.zero, Size).Contains(transform.InverseTransformPoint(point));
        public Vector3 Sample()
        {
            Vector3 point = transform.TransformPoint(new Vector3(Random.Range(-0.5f, 0.5f) * Size.x,
                Random.Range(-0.5f, 0.5f) * Size.y, Random.Range(-0.5f, 0.5f) * Size.z));
            if (Kind == BirdHabitatKind.Water) point.y = WaterHeight;
            return point;
        }
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Kind == BirdHabitatKind.Water ? Color.cyan : Kind == BirdHabitatKind.Air ? Color.white : Color.green;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, Size);
        }
    }
}
