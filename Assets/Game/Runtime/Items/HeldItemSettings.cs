using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Held Item Settings")]
    public sealed class HeldItemSettings : ScriptableObject
    {
        public event System.Action ContentChanged;
        private void OnValidate() => ContentChanged?.Invoke();
        public HeldItemSpatialSettings FirstPersonPose = HeldItemSpatialSettings.FirstPersonDefault;
        public HeldItemPoseSettings HoldSettings = HeldItemPoseSettings.Default;
    }
}
