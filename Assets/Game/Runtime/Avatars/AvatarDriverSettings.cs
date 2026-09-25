using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Avatar Driver Settings")]
    public sealed class AvatarDriverSettings : ScriptableObject
    {
        [Tooltip("Additional driver body offset in seat-relative metres: X right, Y up, Z forward. Other seats are unaffected. Avatars can override this.")]
        public Vector3 DriverSeatedOffset = AvatarDriverPose.DefaultSeatedOffset;
    }
}
