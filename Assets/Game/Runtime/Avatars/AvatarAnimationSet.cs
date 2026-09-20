using System;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Avatar Animation Set")]
    public sealed class AvatarAnimationSet : ScriptableObject
    {
        public const float MinimumPlayback = 0.65f, MaximumPlayback = 1.8f;
        public event Action ContentChanged;
        private void OnValidate() => ContentChanged?.Invoke();
        public AnimationClip Idle, Jump, Fall, Seated;
        public LocomotionClip WalkForward, WalkBackward, WalkLeft, WalkRight;
        public LocomotionClip RunForward, RunBackward, RunLeft, RunRight;
        [Range(0f, 1f)] public float AscentStart;
        [Range(0f, 1f)] public float AscentEnd = 0.5f;
        [Min(0.01f)] public float AscentDuration = 0.25f;

        [Serializable]
        public struct LocomotionClip
        {
            public AnimationClip Clip;
            [Min(0.01f)] public float NominalSpeed, ReferenceHumanScale;
            [Range(0f, 1f)] public float CycleOffset;
        }

        public LocomotionClip GetLocomotion(int index) => index switch
        {
            0 => WalkForward, 1 => WalkBackward, 2 => WalkLeft, 3 => WalkRight,
            4 => RunForward, 5 => RunBackward, 6 => RunLeft, 7 => RunRight,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        public void SetLocomotion(int index, LocomotionClip value)
        {
            switch (index)
            {
                case 0: WalkForward = value; break; case 1: WalkBackward = value; break;
                case 2: WalkLeft = value; break; case 3: WalkRight = value; break;
                case 4: RunForward = value; break; case 5: RunBackward = value; break;
                case 6: RunLeft = value; break; case 7: RunRight = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public bool IsComplete
        {
            get
            {
                if (!Idle || !Jump || !Fall || !Seated || Idle.length <= 0f || Jump.length <= 0f || Fall.length <= 0f || Seated.length <= 0f ||
                    !float.IsFinite(AscentStart) || !float.IsFinite(AscentEnd) || !float.IsFinite(AscentDuration) ||
                    AscentStart < 0f || AscentEnd > 1f || AscentEnd <= AscentStart || AscentDuration <= 0f) return false;
                float minimumCycles = 0f, maximumCycles = float.PositiveInfinity;
                for (int i = 0; i < 8; i++)
                {
                    var slot = GetLocomotion(i);
                    if (!slot.Clip || slot.Clip.length <= 0f || !float.IsFinite(slot.NominalSpeed) || !float.IsFinite(slot.ReferenceHumanScale) ||
                        !float.IsFinite(slot.CycleOffset) || slot.NominalSpeed <= 0f || slot.ReferenceHumanScale <= 0f) return false;
                    minimumCycles = Mathf.Max(minimumCycles, MinimumPlayback / slot.Clip.length);
                    maximumCycles = Mathf.Min(maximumCycles, MaximumPlayback / slot.Clip.length);
                }
                // All directions and gaits can overlap while blending.
                return minimumCycles <= maximumCycles;
            }
        }
    }
}
