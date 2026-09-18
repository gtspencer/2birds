using UnityEngine;

namespace TwoBirds
{
    public enum ZoneShape { Sphere, Box }

    public sealed class ItemPlacementZone : MonoBehaviour
    {
        public ItemDefinition Item;
        public ItemRegistry Registry;
        public ZoneShape Shape = ZoneShape.Sphere;
        public float Radius = 5f;
        public Vector3 BoxSize = new Vector3(10f, 2f, 10f);
        [Min(1)] public int Count = 10;
        [Min(0f)] public float MinSpacing = 1f;
        public float RaycastHeight = 50f;
        public LayerMask GroundLayer = ~0;

        private void Reset()
        {
            GroundLayer = LayerMask.GetMask("Environment", "Ground");
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 0.3f, 0.25f);
            if (Shape == ZoneShape.Sphere)
            {
                Gizmos.DrawWireSphere(transform.position, Radius);
            }
            else
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawWireCube(Vector3.zero, BoxSize);
                Gizmos.matrix = Matrix4x4.identity;
            }
        }
    }
}
