using System;
using System.Collections.Generic;
using UnityEngine;
using UniVRM10;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Avatar Settings")]
    public sealed class AvatarSettings : ScriptableObject
    {
        public const int CurrentFormatVersion = 3;
        [Header("Authored tuning")]
        public string DisplayName;
        [Min(0.01f)] public float VisualHeight = 1.8f;
        public Vector3 StandingOffset, SeatedPelvisOffset, CarriedOffset;
        public float YawOffset;
        [Range(0.5f, 2f)] public float PlaybackMultiplier = 1f;
        public float LeftSoleAdjustment, RightSoleAdjustment;
        public Quaternion LeftFootRotation = Quaternion.identity, RightFootRotation = Quaternion.identity;
        [Range(0f, 1f)] public float FootCorrection = 1f, PelvisCorrection = 1f;
        public HumanBodyBones LeftHandFollowBone = HumanBodyBones.LeftHand;
        public List<SpringChain> AdditionalSprings = new();
        [Header("Generated source and skeleton")]
        public AvatarId Id;
        public GeneratedSkeleton Generated;
        public float Scale => VisualHeight / Generated.Height;
        public event Action ContentChanged;
        private void OnValidate() => ContentChanged?.Invoke();

        [Serializable]
        public sealed class SpringChain
        {
            public string Name = "Spring";
            [AvatarBonePath] public string Root, Tip;
            [Min(0f)] public float Stiffness = 1f;
            [Range(0f, 1f)] public float Drag = 0.4f;
            [Min(0f)] public float GravityPower;
            public Vector3 GravityDirection = Vector3.down;
            [Min(0f)] public float JointRadius = 0.02f;
            public List<SpringCollider> Colliders = new();
        }

        [Serializable]
        public sealed class SpringCollider
        {
            [AvatarBonePath] public string Bone;
            public Vector3 Offset;
            [Min(0f)] public float Radius = 0.05f;
        }

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
            public Vector3 RightWristToPalmPosition;
            public Quaternion RightWristToPalmRotation;
        }
    }

    public sealed class AvatarBonePathAttribute : PropertyAttribute { }

    public static class AvatarContentValidation
    {
        public static readonly HumanBodyBones[] RequiredBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand
        };

        public static void Validate(GameObject root, AvatarSettings settings)
        {
            if (!root || !settings) throw new InvalidOperationException("Avatar prefab/settings are missing.");
            var data = settings.Generated;
            if (data.FormatVersion != AvatarSettings.CurrentFormatVersion)
                throw new InvalidOperationException("Unsupported generated skeleton version; process the source again.");
            if (!Positive(data.Height) || !Positive(data.HumanScale) || !Positive(settings.VisualHeight) || !Positive(settings.Scale) ||
                !Finite(data.Bounds.center) || !Positive(data.Bounds.size) || !float.IsFinite(data.SolePlane) ||
                !Positive(data.LeftLeg) || !Positive(data.RightLeg) || !Positive(data.LeftArm) || !Positive(data.RightArm) ||
                !Finite(data.Hips) || !Finite(data.Head) || !Finite(data.LeftShoulder) || !Finite(data.RightShoulder) ||
                !Finite(data.LeftSoleToGoal) || !Finite(data.RightSoleToGoal) ||
                !Rotation(data.LeftFootRestRotation) || !Rotation(data.RightFootRestRotation) ||
                !Finite(data.RightWristToPalmPosition) || !Rotation(data.RightWristToPalmRotation))
                throw new InvalidOperationException("Avatar dimensions or skeleton measurements are invalid; process the source again.");
            if (!Finite(settings.StandingOffset) || !Finite(settings.SeatedPelvisOffset) || !Finite(settings.CarriedOffset) ||
                !float.IsFinite(settings.YawOffset) || !Positive(settings.PlaybackMultiplier) ||
                !float.IsFinite(settings.LeftSoleAdjustment) || !float.IsFinite(settings.RightSoleAdjustment) ||
                !float.IsFinite(settings.FootCorrection) || !float.IsFinite(settings.PelvisCorrection) ||
                !Rotation(settings.LeftFootRotation) || !Rotation(settings.RightFootRotation))
                throw new InvalidOperationException("Avatar authored tuning contains invalid values.");
            var animator = root.GetComponent<Animator>();
            var vrm = root.GetComponent<Vrm10Instance>();
            if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman ||
                animator.avatar != data.HumanoidAvatar)
                throw new InvalidOperationException("Avatar prefab does not match its generated Humanoid Avatar.");
            if (!vrm || !vrm.Vrm || !root.GetComponent<UniHumanoid.Humanoid>() ||
                !root.GetComponent<AvatarInstance>() || !root.GetComponent<AvatarSpringRuntimeProvider>())
                throw new InvalidOperationException("Avatar prefab is missing required runtime components.");
            foreach (var bone in RequiredBones)
                if (!animator.GetBoneTransform(bone)) throw new InvalidOperationException($"Missing required Humanoid mapping: {bone}.");
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (renderer.enabled && mesh && mesh.vertexCount > 0 && renderer.sharedMaterials.Length > 0) return;
            }
            throw new InvalidOperationException("Avatar prefab contains no usable mesh renderer.");
        }

        private static bool Positive(float value) => float.IsFinite(value) && value > 0f;
        private static bool Positive(Vector2 value) => Positive(value.x) && Positive(value.y);
        private static bool Positive(Vector3 value) => Positive(value.x) && Positive(value.y) && Positive(value.z);
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Rotation(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y) &&
            float.IsFinite(value.z) && float.IsFinite(value.w) && Quaternion.Dot(value, value) > 0f;
    }
}
