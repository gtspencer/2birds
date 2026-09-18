using UnityEngine;

namespace TwoBirds
{
    public sealed class BirdPerch : MonoBehaviour
    {
        public ushort Id, Biome = 1;
        public BirdPerchKind Kind;
        public float Clearance = 0.5f;
        public BirdPerchVolume GeneratedBy;
        public Vector3 Position(BirdSpecies species) => transform.position + transform.up * species.LandingOffset;
        private void OnDrawGizmos()
        {
            Gizmos.color = Color.HSVToRGB((Biome * 0.137f) % 1f, 0.65f, Kind == BirdPerchKind.Tree ? 1f : 0.7f);
            Gizmos.DrawWireSphere(transform.position, Clearance);
            Gizmos.DrawRay(transform.position, transform.forward * Clearance * 2f);
            Gizmos.DrawRay(transform.position, transform.up * Clearance);
        }
    }
}
