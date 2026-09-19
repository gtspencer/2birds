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
        public bool UsePlacementYOffset;
        public float PlacementYOffset;
        public bool RandomizeXRotation;
        public Vector2 XRotationRange = new(-10f, 10f);
        public bool RandomizeYRotation = true;
        public Vector2 YRotationRange = new(0f, 360f);
        public bool RandomizeZRotation;
        public Vector2 ZRotationRange = new(-10f, 10f);
        public bool RandomizeScale;
        public Vector2 ScaleRange = Vector2.one;

        private void Reset()
        {
            GroundLayer = LayerMask.GetMask("Environment", "Ground");
        }

        private void OnValidate()
        {
            XRotationRange.y = Mathf.Max(XRotationRange.x, XRotationRange.y);
            YRotationRange.y = Mathf.Max(YRotationRange.x, YRotationRange.y);
            ZRotationRange.y = Mathf.Max(ZRotationRange.x, ZRotationRange.y);
            ScaleRange.x = Mathf.Max(0f, ScaleRange.x);
            ScaleRange.y = Mathf.Max(ScaleRange.x, ScaleRange.y);
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
