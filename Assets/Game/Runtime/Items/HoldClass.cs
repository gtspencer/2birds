using System;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Hold Class")]
    public sealed class HoldClass : ScriptableObject
    {
        public event Action ContentChanged;
        public void NotifyContentChanged() => ContentChanged?.Invoke();
        private void OnValidate() => NotifyContentChanged();
        public ItemHoldMode Mode;
        public HoldClassView ThirdPerson, FirstPerson;
        [Min(0f), Tooltip("Visual charge duration in seconds. Slingshot follows ThrowChargeTime instead.")] public float ChargePoseDuration = 0.35f;
        [Min(0f)] public float MaximumFollowDuration = 0.2f, EndPosePauseDuration, ReturnBlendDuration = 0.2f;
        [Range(0.01f, 0.85f)] public float FollowReachFraction = 0.85f;
        public HoldClassView View(bool firstPerson) => firstPerson ? FirstPerson : ThirdPerson;

        internal float Spread(bool firstPerson, float charge)
        {
            var view = View(firstPerson);
            float charged = view.Charged && view.ChargedSpread > 0f ? view.ChargedSpread : view.HoldSpread;
            return Mathf.Lerp(view.HoldSpread, charged, charge);
        }
    }

    [Serializable]
    public struct HoldClassView
    {
        public AnimationClip Hold, Charged;
        [Tooltip("TwoHand only. Palm-to-palm metres, written when the pose is baked.")] public float HoldSpread, ChargedSpread;
    }
}
