using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Held Item Settings")]
    public sealed class HeldItemSettings : ScriptableObject
    {
        public event System.Action ContentChanged;
        public void NotifyContentChanged() => ContentChanged?.Invoke();
        private void OnValidate() => NotifyContentChanged();
        public HoldModePoses OneHand = HoldModePoses.Default, TwoHand = HoldModePoses.Default, Slingshot = HoldModePoses.Default;
        public HoldModePoses Poses(ItemHoldMode mode) => mode switch
        { ItemHoldMode.TwoHand => TwoHand, ItemHoldMode.Slingshot => Slingshot, _ => OneHand };
    }

    [System.Serializable]
    public struct HoldModePoses
    {
        public AnimationClip ThirdPersonHold, ThirdPersonCharged, FirstPersonHold, FirstPersonCharged;
        [Min(0f), Tooltip("Visual charge duration in seconds. Slingshot follows ThrowChargeTime instead.")] public float ChargePoseDuration;
        [Min(0f)] public float MaximumFollowDuration, EndPosePauseDuration, ReturnBlendDuration;
        [Range(0.01f, 0.85f)] public float FollowReachFraction;
        public static HoldModePoses Default => new()
        { ChargePoseDuration = 0.35f, FollowReachFraction = 0.85f, MaximumFollowDuration = 0.20f, ReturnBlendDuration = 0.20f };
        internal AnimationClip Hold(bool firstPerson) => firstPerson ? FirstPersonHold : ThirdPersonHold;
        internal AnimationClip Charged(bool firstPerson) => firstPerson ? FirstPersonCharged : ThirdPersonCharged;
    }
}
