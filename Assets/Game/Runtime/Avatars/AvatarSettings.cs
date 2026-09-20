using System;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Avatar Settings")]
    public sealed class AvatarSettings : ScriptableObject
    {
        [Header("Authored tuning")]
        [Min(0.01f)] public float VisualHeight = 1.8f;
        public Vector3 StandingOffset, SeatedPelvisOffset, CarriedOffset;
        public float YawOffset;
        [Range(0.5f, 2f)] public float PlaybackMultiplier = 1f;
        public float LeftSoleAdjustment, RightSoleAdjustment;
        public Quaternion LeftFootRotation = Quaternion.identity, RightFootRotation = Quaternion.identity;
        [Range(0f, 1f)] public float FootCorrection = 1f, PelvisCorrection = 1f;
        [Header("Generated source and skeleton")]
        public AvatarId Id;
        public GeneratedSkeleton Generated;
        public float Scale => VisualHeight / Generated.Height;
        public event Action ContentChanged;
        private void OnValidate() => ContentChanged?.Invoke();

        [Serializable]
        public struct GeneratedSkeleton
        {
            public string SourceGuid;
            public GameObject Source;
            public Avatar HumanoidAvatar;
            public int FormatVersion;
            public Bounds Bounds;
            public float Height, SolePlane, HumanScale;
            public Vector3 Hips, Head, LeftShoulder, RightShoulder;
            public Vector2 LeftLeg, RightLeg, LeftArm, RightArm;
            public Vector3 LeftSoleToGoal, RightSoleToGoal;
            public Quaternion LeftFootRestRotation, RightFootRestRotation;
        }
    }
}
