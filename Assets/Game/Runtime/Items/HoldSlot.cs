using System;
using UnityEngine;

namespace TwoBirds
{
    public enum HoldSlotMode { Hand = 0, Heavy = 1, Slingshot = 2 }

    [CreateAssetMenu(menuName = "Two Birds/Hold Slot")]
    public sealed class HoldSlot : ScriptableObject
    {
        public event Action ContentChanged;
        public void NotifyContentChanged() => ContentChanged?.Invoke();
        private void OnValidate() => NotifyContentChanged();
        public HoldSlotMode Mode;
        [Tooltip("Required for every pose target the mode uses, in both views.")] public GripPoseTable Defaults = new();
        [Tooltip("Used when the item has no Grip Fingers.")] public AnimationClip Fingers;
        [Min(0f)] public float MaximumFollowDuration = 0.2f, EndPosePauseDuration, ReturnBlendDuration = 0.2f;
        [Range(0.01f, 0.85f)] public float FollowReachFraction = 0.85f;
    }
}
