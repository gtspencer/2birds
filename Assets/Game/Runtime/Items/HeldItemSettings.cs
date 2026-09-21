using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Held Item Settings")]
    public sealed class HeldItemSettings : ScriptableObject
    {
        public HeldItemPoseSettings HoldSettings = HeldItemPoseSettings.Default;
    }
}
