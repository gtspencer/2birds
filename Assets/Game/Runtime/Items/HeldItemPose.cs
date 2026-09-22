using System;
using UnityEngine;

namespace TwoBirds
{
    [Serializable]
    public struct HeldItemPoseSettings
    {
        [Tooltip("Palm point relative to the shoulder in arm lengths: right, up, torso forward.")] public Vector3 HoldPosition;
        [Tooltip("Palm rotation in degrees relative to the torso.")] public Vector3 HoldWristEuler;
        [Tooltip("Charge curve control point in shoulder-relative arm lengths.")] public Vector3 ChargeControlPosition;
        [Tooltip("Charged follow point in shoulder-relative arm lengths.")] public Vector3 ChargedPosition;
        [Tooltip("Charged palm rotation in degrees relative to the torso.")] public Vector3 ChargedWristEuler;
        [Min(0f), Tooltip("Visual charge duration in seconds, independent of throw strength.")] public float ChargePoseDuration;
        [Range(0.01f, 0.85f)] public float FollowReachFraction;
        [Min(0f)] public float MaximumFollowDuration, EndPosePauseDuration, ReturnBlendDuration;

        public static HeldItemPoseSettings Default => new()
        {
            HoldPosition = new Vector3(0.15f, -0.40f, 0.50f),
            ChargeControlPosition = new Vector3(0.35f, 0.25f, 0.45f),
            ChargedPosition = new Vector3(0.20f, 0.65f, -0.25f),
            ChargedWristEuler = new Vector3(-70f, 0f, 195f),
            ChargePoseDuration = 0.35f, FollowReachFraction = 0.85f,
            MaximumFollowDuration = 0.20f, ReturnBlendDuration = 0.20f
        };
    }

    [Serializable]
    public struct HeldItemSpatialSettings
    {
        public Vector3 HoldPosition, HoldWristEuler, ChargeControlPosition, ChargedPosition, ChargedWristEuler;
        public static HeldItemSpatialSettings From(HeldItemPoseSettings value) => new()
        {
            HoldPosition = value.HoldPosition, HoldWristEuler = value.HoldWristEuler,
            ChargeControlPosition = value.ChargeControlPosition, ChargedPosition = value.ChargedPosition,
            ChargedWristEuler = value.ChargedWristEuler
        };
        public static HeldItemSpatialSettings FirstPersonDefault => new()
        {
            HoldPosition = new(-0.05f, -0.25f, 0.78f), HoldWristEuler = new(0f, 0f, 15f),
            ChargeControlPosition = new(0.05f, -0.05f, 0.72f), ChargedPosition = new(0.18f, 0.12f, 0.5f),
            ChargedWristEuler = new(-35f, 0f, 100f)
        };
    }

    internal readonly struct HeldItemPose
    {
        internal readonly Vector3 FollowPosition, WristPosition;
        internal readonly Quaternion WristRotation, FollowRotation;
        internal readonly Pose Item;

        internal HeldItemPose(Vector3 follow, Quaternion wrist, AvatarSettings avatar, in HeldItemPoseData item)
            : this(follow, wrist, avatar.Generated, avatar.Scale, item) { }
        internal HeldItemPose(Vector3 follow, Quaternion wrist, AvatarSettings.GeneratedSkeleton data, float scale, in HeldItemPoseData item)
        {
            FollowPosition = follow;
            WristRotation = wrist;
            WristPosition = follow - wrist * (data.RightWristToPalmPosition * scale);
            FollowRotation = wrist * data.RightWristToPalmRotation;
            Item = new Pose(follow + FollowRotation * item.GripPosition, FollowRotation * item.GripRotation);
        }
    }

    internal readonly struct HeldItemPoseData
    {
        internal readonly HeldItemPoseSettings Settings;
        internal readonly HeldItemSpatialSettings Spatial;
        internal readonly Vector3 GripPosition;
        internal readonly Quaternion GripRotation, HoldRotation, ChargedRotation;
        private readonly bool firstPerson;
        internal HeldItemPoseData(ItemDefinition definition, HeldItemSettings defaults, bool firstPerson = false)
        {
            this.firstPerson = firstPerson;
            Settings = definition.OverrideHoldSettings ? definition.HandPose : defaults.HoldSettings;
            Spatial = firstPerson ? definition.OverrideFirstPersonPose ? definition.FirstPersonPose : defaults.FirstPersonPose :
                HeldItemSpatialSettings.From(Settings);
            GripPosition = definition.GripPosition;
            GripRotation = Quaternion.Euler(definition.GripEuler);
            HoldRotation = Quaternion.Euler(Spatial.HoldWristEuler);
            ChargedRotation = Quaternion.Euler(Spatial.ChargedWristEuler);
        }
        internal float Reach => Mathf.Clamp(Settings.FollowReachFraction, 0.01f, 0.85f);
        internal Vector3 HoldPosition(AvatarSettings avatar) => Spatial.HoldPosition +
            (firstPerson && avatar ? avatar.FirstPersonHoldOffset : Vector3.zero);
    }

    internal readonly struct HeldItemBodyFrame
    {
        internal readonly Vector3 Shoulder;
        internal readonly Quaternion Rotation;
        internal readonly float ArmLength, Scale;
        internal readonly AvatarSettings.GeneratedSkeleton Measurements;
        internal HeldItemBodyFrame(Vector3 shoulder, Quaternion rotation, AvatarSettings.GeneratedSkeleton measurements, float scale)
        {
            Shoulder = shoulder; Rotation = rotation; Measurements = measurements; Scale = scale;
            ArmLength = (measurements.RightArm.x + measurements.RightArm.y) * scale;
        }
        internal HeldItemBodyFrame(AvatarSettings settings, AvatarRegistry registry, in AvatarPresentationInput input, Quaternion torso, float seatedWeight)
        {
            float scale = settings.Scale;
            Scale = scale; Measurements = settings.Generated;
            Rotation = torso * Quaternion.Euler(0f, settings.YawOffset, 0f);
            ArmLength = (settings.Generated.RightArm.x + settings.Generated.RightArm.y) * scale;
            Vector3 standing = input.SolePosition + torso * (settings.StandingOffset + (input.Carried ? settings.CarriedOffset : Vector3.zero))
                + Rotation * (settings.Generated.RightShoulder * scale);
            Vector3 seated = input.Facing.position + input.Facing.rotation *
                AvatarDriverPose.SeatedOffset(settings, registry, input.Seated && input.Driver)
                + Rotation * ((settings.Generated.RightShoulder - settings.Generated.Hips) * scale);
            Shoulder = Vector3.Lerp(standing, seated, seatedWeight);
        }
        internal Vector3 ToWorld(Vector3 position) => Shoulder + Rotation * (position * ArmLength);
        internal Vector3 ToLocal(Vector3 position) => Quaternion.Inverse(Rotation) * (position - Shoulder) / ArmLength;
    }

    internal static class HeldItemPoseCalculation
    {
        internal static HeldItemPose FromItem(Pose pose, in HeldItemBodyFrame body, in HeldItemPoseData item)
        {
            Quaternion palm = pose.rotation * Quaternion.Inverse(item.GripRotation);
            return new HeldItemPose(pose.position - palm * item.GripPosition,
                palm * Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body.Measurements, body.Scale, item);
        }

        internal static HeldItemPose Resolve(Vector3 follow, Quaternion wrist, in HeldItemBodyFrame body,
            AvatarSettings avatar, in HeldItemPoseData item, out bool beyondReach, bool soften = true)
        {
            Vector3 offset = wrist * (body.Measurements.RightWristToPalmPosition * body.Scale);
            Vector3 delta = follow - offset - body.Shoulder;
            float reach = body.ArmLength * item.Reach;
            beyondReach = delta.sqrMagnitude > reach * reach;
            float distance = delta.magnitude;
            float softStart = reach * 0.85f;
            if (soften && distance > softStart)
            {
                float softRange = reach - softStart;
                delta *= (softStart + softRange * (1f - Mathf.Exp(-(distance - softStart) / softRange))) / distance;
            }
            else delta = Vector3.ClampMagnitude(delta, reach);
            return new HeldItemPose(body.Shoulder + delta + offset, wrist, body.Measurements, body.Scale, item);
        }

        internal static HeldItemPose Hold(in HeldItemBodyFrame body, AvatarSettings avatar, in HeldItemPoseData item) =>
            Resolve(body.ToWorld(item.HoldPosition(avatar)), body.Rotation * item.HoldRotation *
                Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body, avatar, item, out _);

        internal static HeldItemPose Charge(in HeldItemBodyFrame body, AvatarSettings avatar, in HeldItemPoseData item,
            Vector3 start, Quaternion rotation, float progress)
        {
            float t = LeanTween.easeOutBack(0f, 1f, Mathf.Clamp01(progress), 0.65f), v = 1f - t;
            Vector3 position = v * v * start + 2f * v * t * item.Spatial.ChargeControlPosition + t * t * item.Spatial.ChargedPosition;
            Quaternion wrist = body.Rotation * Quaternion.SlerpUnclamped(rotation, item.ChargedRotation, t) *
                Quaternion.Inverse(body.Measurements.RightWristToPalmRotation);
            return Resolve(body.ToWorld(position), wrist, body, avatar, item, out _);
        }
    }
}
