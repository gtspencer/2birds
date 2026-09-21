using System;
using UnityEngine;

namespace TwoBirds
{
    [Serializable]
    public struct HeldItemPoseSettings
    {
        [Tooltip("Item origin in world metres along the follow bone axes.")] public Vector3 GripPosition;
        [Tooltip("Item rotation in degrees relative to the follow bone.")] public Vector3 GripEuler;
        [Tooltip("Follow point relative to the shoulder in arm lengths: right, up, torso forward.")] public Vector3 HoldPosition;
        [Tooltip("Wrist rotation in degrees relative to the torso.")] public Vector3 HoldWristEuler;
        [Tooltip("Charge curve control point in shoulder-relative arm lengths.")] public Vector3 ChargeControlPosition;
        [Tooltip("Charged follow point in shoulder-relative arm lengths.")] public Vector3 ChargedPosition;
        [Tooltip("Charged wrist rotation in degrees relative to the torso.")] public Vector3 ChargedWristEuler;
        [Min(0f), Tooltip("Visual charge duration in seconds, independent of throw strength.")] public float ChargePoseDuration;
        [Range(0.01f, 0.85f)] public float FollowReachFraction;
        [Min(0f)] public float MaximumFollowDuration, EndPosePauseDuration, ReturnBlendDuration;

        public static HeldItemPoseSettings Default => new()
        {
            HoldPosition = new Vector3(0.15f, -0.40f, 0.50f),
            ChargeControlPosition = new Vector3(0.35f, 0.25f, 0.45f),
            ChargedPosition = new Vector3(0.20f, 0.65f, -0.25f),
            ChargedWristEuler = new Vector3(-70f, 0f, 15f),
            ChargePoseDuration = 0.35f, FollowReachFraction = 0.85f,
            MaximumFollowDuration = 0.20f, ReturnBlendDuration = 0.20f
        };
    }

    internal readonly struct HeldItemPose
    {
        internal readonly Vector3 FollowPosition, WristPosition;
        internal readonly Quaternion WristRotation, FollowRotation;
        internal readonly Pose Item;

        internal HeldItemPose(Vector3 follow, Quaternion wrist, AvatarSettings avatar, in HeldItemPoseData item)
        {
            FollowPosition = follow;
            WristRotation = wrist;
            WristPosition = follow - wrist * (avatar.Generated.RightWristToFollowPosition * avatar.Scale);
            FollowRotation = wrist * avatar.Generated.RightWristToFollowRotation;
            Item = new Pose(follow + FollowRotation * item.Settings.GripPosition, FollowRotation * item.GripRotation);
        }
    }

    internal readonly struct HeldItemPoseData
    {
        internal readonly HeldItemPoseSettings Settings;
        internal readonly Quaternion GripRotation, HoldRotation, ChargedRotation;
        internal HeldItemPoseData(ItemDefinition definition)
        {
            Settings = definition.HandPose;
            GripRotation = Quaternion.Euler(Settings.GripEuler);
            HoldRotation = Quaternion.Euler(Settings.HoldWristEuler);
            ChargedRotation = Quaternion.Euler(Settings.ChargedWristEuler);
        }
        internal float Reach => Mathf.Clamp(Settings.FollowReachFraction, 0.01f, 0.85f);
    }

    internal readonly struct HeldItemBodyFrame
    {
        internal readonly Vector3 Shoulder;
        internal readonly Quaternion Rotation;
        internal readonly float ArmLength;
        internal HeldItemBodyFrame(AvatarSettings settings, in AvatarPresentationInput input, Quaternion torso, float seatedWeight)
        {
            float scale = settings.Scale;
            Rotation = torso * Quaternion.Euler(0f, settings.YawOffset, 0f);
            ArmLength = (settings.Generated.RightArm.x + settings.Generated.RightArm.y) * scale;
            Vector3 standing = input.SolePosition + torso * (settings.StandingOffset + (input.Carried ? settings.CarriedOffset : Vector3.zero))
                + Rotation * (settings.Generated.RightShoulder * scale);
            Vector3 seated = input.Facing.position + input.Facing.rotation * settings.SeatedPelvisOffset
                + Rotation * ((settings.Generated.RightShoulder - settings.Generated.Hips) * scale);
            Shoulder = Vector3.Lerp(standing, seated, seatedWeight);
        }
        internal Vector3 ToWorld(Vector3 position) => Shoulder + Rotation * (position * ArmLength);
        internal Vector3 ToLocal(Vector3 position) => Quaternion.Inverse(Rotation) * (position - Shoulder) / ArmLength;
    }

    internal static class HeldItemPoseCalculation
    {
        internal static HeldItemPose Resolve(Vector3 follow, Quaternion wrist, in HeldItemBodyFrame body,
            AvatarSettings avatar, in HeldItemPoseData item, out bool beyondReach)
        {
            Vector3 offset = wrist * (avatar.Generated.RightWristToFollowPosition * avatar.Scale);
            Vector3 delta = follow - offset - body.Shoulder;
            float reach = body.ArmLength * item.Reach;
            beyondReach = delta.sqrMagnitude > reach * reach;
            return new HeldItemPose(body.Shoulder + Vector3.ClampMagnitude(delta, reach) + offset, wrist, avatar, item);
        }

        internal static HeldItemPose Hold(in HeldItemBodyFrame body, AvatarSettings avatar, in HeldItemPoseData item) =>
            Resolve(body.ToWorld(item.Settings.HoldPosition), body.Rotation * item.HoldRotation, body, avatar, item, out _);

        internal static HeldItemPose Charge(in HeldItemBodyFrame body, AvatarSettings avatar, in HeldItemPoseData item,
            Vector3 start, Quaternion rotation, float progress)
        {
            float t = Mathf.SmoothStep(0f, 1f, progress), v = 1f - t;
            Vector3 position = v * v * start + 2f * v * t * item.Settings.ChargeControlPosition + t * t * item.Settings.ChargedPosition;
            return Resolve(body.ToWorld(position), body.Rotation * Quaternion.Slerp(rotation, item.ChargedRotation, t), body, avatar, item, out _);
        }
    }
}
