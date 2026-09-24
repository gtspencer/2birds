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

        public static HeldItemPoseSettings HeavyDefault => new()
        {
            HoldPosition = new(0f, -0.40f, 0.50f),
            ChargeControlPosition = new(0f, 0.10f, 0.70f),
            ChargedPosition = new(0f, 0.60f, 0.25f),
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
        internal readonly Pose LeftPalm;
        internal readonly bool Heavy;
        internal readonly Pose RequestedRight, RequestedLeft;
        internal readonly bool ReachLimited, Unreachable;

        internal HeldItemPose(Vector3 follow, Quaternion wrist, AvatarSettings avatar, in HeldItemPoseData item)
            : this(follow, wrist, AvatarPalmCalibration.Measurements(avatar), avatar.Scale, item) { }
        internal HeldItemPose(Vector3 follow, Quaternion wrist, AvatarSettings.GeneratedSkeleton data, float scale, in HeldItemPoseData item, Pose? requested = null, bool limited = false)
        {
            ReachLimited = limited; Unreachable = false; RequestedLeft = default;
            RequestedRight = requested ?? new Pose(follow, wrist * data.RightWristToPalmRotation);
            LeftPalm = default; Heavy = false;
            FollowPosition = follow;
            WristRotation = wrist;
            WristPosition = follow - wrist * (data.RightWristToPalmPosition * scale);
            FollowRotation = wrist * data.RightWristToPalmRotation;
            Item = HeldItemPoseCalculation.ItemFromPalm(new Pose(follow, FollowRotation), item.RightContact, item.PrefabScale);
        }

        internal HeldItemPose(in HeldItemPose evaluated, in HeldItemPose requested)
        {
            FollowPosition = evaluated.FollowPosition; WristPosition = evaluated.WristPosition;
            WristRotation = evaluated.WristRotation; FollowRotation = evaluated.FollowRotation;
            Item = evaluated.Item; LeftPalm = evaluated.LeftPalm; Heavy = evaluated.Heavy;
            RequestedRight = requested.RequestedRight; RequestedLeft = requested.RequestedLeft;
            ReachLimited = requested.ReachLimited; Unreachable = requested.Unreachable;
        }

        internal HeldItemPose(Pose root, Pose left, Pose right, in HeldItemBodyFrame body, bool heavy = true, Pose? requestedRight = null, Pose? requestedLeft = null, bool unreachable = false)
        {
            Item = root; LeftPalm = left; Heavy = heavy;
            RequestedRight = requestedRight ?? right; RequestedLeft = requestedLeft ?? left;
            ReachLimited = false; Unreachable = unreachable;
            FollowPosition = right.position; FollowRotation = right.rotation;
            WristRotation = right.rotation * Quaternion.Inverse(body.Measurements.RightWristToPalmRotation);
            WristPosition = right.position - WristRotation * (body.Measurements.RightWristToPalmPosition * body.Scale);
        }
    }

    internal readonly struct HeldItemPoseData
    {
        internal readonly HeldItemPoseSettings Settings;
        internal readonly HeldItemSpatialSettings Spatial;
        internal readonly SlingshotChargePoseSettings SlingshotCharge;
        internal readonly ItemPalmContact RightContact, LeftContact, PullingContact;
        internal readonly Vector3 PrefabScale;
        internal readonly Quaternion HoldRotation, ChargedRotation;
        internal readonly ItemHoldMode HoldMode;
        internal readonly Quaternion PrefabRotation;
        internal readonly ItemReleaseSphere Sphere;
        internal readonly float ReleaseRadius;
        internal bool Heavy => HoldMode == ItemHoldMode.Heavy;
        private readonly bool firstPerson;
        internal HeldItemPoseData(ItemDefinition definition, HeldItemSettings defaults, bool firstPerson = false, WorldItem worldItem = null)
        {
            this.firstPerson = firstPerson;
            HoldMode = definition.HoldMode;
            PrefabRotation = definition.WorldPrefab.transform.localRotation;
            var geometry = worldItem ? worldItem : definition.WorldPrefab.GetComponent<WorldItem>();
            if (geometry) geometry.CacheReleaseGeometry();
            Sphere = geometry ? geometry.ReleaseSphere : default;
            ReleaseRadius = geometry ? geometry.ReleaseRadius : 0f;
            Settings = defaults.ResolveHold(definition);
            Spatial = defaults.ResolveSpatial(definition, firstPerson);
            RightContact = definition.RightPalmContact;
            LeftContact = definition.LeftPalmContact;
            PullingContact = definition is SlingshotDefinition pulling ? pulling.PullingPalmContact : default;
            PrefabScale = definition.WorldPrefab.transform.localScale;
            HoldRotation = Quaternion.Euler(Spatial.HoldWristEuler);
            SlingshotCharge = definition is SlingshotDefinition slingshot ? defaults.ResolveCharge(slingshot, firstPerson) : default;
            ChargedRotation = Quaternion.Euler(definition is SlingshotDefinition ? SlingshotCharge.ChargedPalmEuler : Spatial.ChargedWristEuler);
        }
        internal float Reach => Mathf.Clamp(Settings.FollowReachFraction, 0.01f, 0.85f);
        internal Vector3 HoldPosition(AvatarSettings avatar) => Spatial.HoldPosition +
            (firstPerson && !Heavy && avatar ? avatar.FirstPersonHoldOffset : Vector3.zero);
    }

    internal readonly struct HeldItemBodyFrame
    {
        internal readonly Vector3 Shoulder;
        private readonly Vector3 leftShoulder;
        internal readonly Quaternion Rotation;
        internal readonly float ArmLength, Scale;
        internal readonly AvatarSettings.GeneratedSkeleton Measurements;
        internal HeldItemBodyFrame(Vector3 shoulder, Quaternion rotation, AvatarSettings.GeneratedSkeleton measurements, float scale,
            Vector3? leftShoulder = null)
        {
            Shoulder = shoulder; Rotation = rotation; Measurements = measurements; Scale = scale;
            this.leftShoulder = leftShoulder ?? shoulder + rotation * ((measurements.LeftShoulder - measurements.RightShoulder) * scale);
            ArmLength = (measurements.RightArm.x + measurements.RightArm.y) * scale;
        }
        internal HeldItemBodyFrame(AvatarSettings settings, AvatarRegistry registry, in AvatarPresentationInput input, Quaternion torso, float seatedWeight, bool heavy = false)
        {
            float scale = settings.Scale;
            Scale = scale; Measurements = AvatarPalmCalibration.Measurements(settings);
            Rotation = torso * Quaternion.Euler(0f, settings.YawOffset, 0f);
            ArmLength = (settings.Generated.RightArm.x + settings.Generated.RightArm.y) * scale;
            Vector3 standing = input.SolePosition + torso * (settings.StandingOffset + (input.Carried ? settings.CarriedOffset : Vector3.zero))
                + Rotation * ((settings.Generated.RightShoulder - Vector3.up * (heavy ? settings.Generated.SolePlane : 0f)) * scale);
            Vector3 seated = input.Facing.position + input.Facing.rotation *
                AvatarDriverPose.SeatedOffset(settings, registry, input.Seated && input.Driver)
                + Rotation * ((settings.Generated.RightShoulder - settings.Generated.Hips) * scale);
            Shoulder = Vector3.Lerp(standing, seated, seatedWeight);
            leftShoulder = Shoulder + Rotation * ((Measurements.LeftShoulder - Measurements.RightShoulder) * scale);
        }
        internal Vector3 ToWorld(Vector3 position) => Shoulder + Rotation * (position * ArmLength);
        internal Vector3 ToLocal(Vector3 position) => Quaternion.Inverse(Rotation) * (position - Shoulder) / ArmLength;
        internal Vector3 LeftShoulder => leftShoulder;
        internal Vector3 Center => (Shoulder + LeftShoulder) * 0.5f;
        internal float LeftArmLength => (Measurements.LeftArm.x + Measurements.LeftArm.y) * Scale;
        internal Vector3 CenterToWorld(Vector3 position) => Center + Rotation * (position * ((ArmLength + LeftArmLength) * 0.5f));
        internal Vector3 CenterToLocal(Vector3 position) => Quaternion.Inverse(Rotation) * (position - Center) / ((ArmLength + LeftArmLength) * 0.5f);
        internal HeldItemBodyFrame WithMeasurements(AvatarSettings.GeneratedSkeleton measurements, float scale) =>
            new(Center + Rotation * ((measurements.RightShoulder - measurements.LeftShoulder) * (scale * 0.5f)), Rotation, measurements, scale);
    }

    internal static class HeavyItemPoseCalculation
    {
        internal const float MaximumReach = 0.98f;

        internal static HeldItemPose FromItem(Pose root, in HeldItemBodyFrame body, in HeldItemPoseData data) =>
            new(root, HeldItemPoseCalculation.PalmFromItem(root, data.LeftContact, data.PrefabScale),
                HeldItemPoseCalculation.PalmFromItem(root, data.RightContact, data.PrefabScale), body);

        internal static ItemReleaseReach Reach(Pose root, in HeldItemBodyFrame body, in HeldItemPoseData data)
        {
            var pose = FromItem(root, body, data);
            Quaternion leftWrist = pose.LeftPalm.rotation * Quaternion.Inverse(body.Measurements.LeftWristToPalmRotation);
            Vector3 left = pose.LeftPalm.position - leftWrist * (body.Measurements.LeftWristToPalmPosition * body.Scale);
            return new ItemReleaseReach(body.Shoulder + root.position - pose.WristPosition, body.ArmLength * data.Reach,
                body.LeftShoulder + root.position - left, body.LeftArmLength * data.Reach, true);
        }

        internal static bool InReach(in HeldItemPose pose, in HeldItemBodyFrame body, float reach)
        {
            Quaternion leftWrist = pose.LeftPalm.rotation * Quaternion.Inverse(body.Measurements.LeftWristToPalmRotation);
            Vector3 left = pose.LeftPalm.position - leftWrist * (body.Measurements.LeftWristToPalmPosition * body.Scale);
            return Vector3.Distance(pose.WristPosition, body.Shoulder) <= body.ArmLength * reach &&
                Vector3.Distance(left, body.LeftShoulder) <= body.LeftArmLength * reach;
        }

        internal static Pose Blend(Pose from, Pose to, float t) =>
            new(Vector3.Lerp(from.position, to.position, t), Quaternion.Slerp(from.rotation, to.rotation, t));

        internal static HeldItemPose Place(Vector3 center, Quaternion rotation, in HeldItemBodyFrame body, in HeldItemPoseData data)
        {
            Quaternion orientation = body.Rotation * rotation * data.PrefabRotation;
            Pose root = new(body.CenterToWorld(center) - orientation * data.Sphere.Center, orientation);
            var pose = FromItem(root, body, data);
            return new HeldItemPose(root, pose.LeftPalm, new Pose(pose.FollowPosition, pose.FollowRotation), body,
                unreachable: !InReach(pose, body, MaximumReach));
        }

        internal static Pose ToLocal(Pose pose, in HeldItemBodyFrame body) =>
            new(body.CenterToLocal(pose.position), Quaternion.Inverse(body.Rotation) * pose.rotation);
        internal static Pose ToWorld(Pose pose, in HeldItemBodyFrame body) =>
            new(body.CenterToWorld(pose.position), body.Rotation * pose.rotation);
    }

    internal static class HeldItemPoseCalculation
    {
        internal static Pose PalmFromItem(Pose root, ItemPalmContact contact, Vector3 scale) =>
            new(root.position + root.rotation * Vector3.Scale(scale, contact.Position), root.rotation * contact.Rotation);

        internal static Pose ItemFromPalm(Pose palm, ItemPalmContact contact, Vector3 scale)
        {
            Quaternion rotation = palm.rotation * Quaternion.Inverse(contact.Rotation);
            return new Pose(palm.position - rotation * Vector3.Scale(scale, contact.Position), rotation);
        }

        internal static HeldItemPose FromItem(Pose pose, in HeldItemBodyFrame body, in HeldItemPoseData item)
        {
            if (item.Heavy) return HeavyItemPoseCalculation.FromItem(pose, body, item);
            var palm = PalmFromItem(pose, item.RightContact, item.PrefabScale);
            return new HeldItemPose(palm.position,
                palm.rotation * Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body.Measurements, body.Scale, item);
        }

        internal static HeldItemPose Resolve(Vector3 follow, Quaternion wrist, in HeldItemBodyFrame body,
            AvatarSettings avatar, in HeldItemPoseData item, out bool beyondReach, bool soften = true, float effectiveReach = 0f)
        {
            Vector3 offset = wrist * (body.Measurements.RightWristToPalmPosition * body.Scale);
            Vector3 delta = follow - offset - body.Shoulder;
            float reach = body.ArmLength * (effectiveReach > 0f ? effectiveReach : item.Reach);
            beyondReach = delta.sqrMagnitude > reach * reach;
            float distance = delta.magnitude;
            if (soften && distance > reach * 0.85f) delta *= AvatarArmIK.SoftReach(distance, reach) / distance;
            else delta = Vector3.ClampMagnitude(delta, reach);
            return new HeldItemPose(body.Shoulder + delta + offset, wrist, body.Measurements, body.Scale, item,
                new Pose(follow, wrist * body.Measurements.RightWristToPalmRotation),
                beyondReach || (body.Shoulder + delta + offset - follow).sqrMagnitude > 0.000001f);
        }

        internal static HeldItemPose Hold(in HeldItemBodyFrame body, AvatarSettings avatar, in HeldItemPoseData item) =>
            item.Heavy ? HeavyItemPoseCalculation.Place(item.HoldPosition(avatar), item.HoldRotation, body, item) :
            Resolve(body.ToWorld(item.HoldPosition(avatar)), body.Rotation * item.HoldRotation *
                Quaternion.Inverse(body.Measurements.RightWristToPalmRotation), body, avatar, item, out _);

        internal static float SlingshotReach(in HeldItemPose pose, in HeldItemBodyFrame body, in HeldItemPoseData item) =>
            Mathf.Clamp(Vector3.Distance(pose.WristPosition, body.Shoulder) / body.ArmLength, item.Reach, 0.98f);

        internal static HeldItemPose SlingshotCharge(in HeldItemBodyFrame body, in HeldItemPoseData item,
            SlingshotPresentation slingshot, in HeldItemPose hold, in HeldItemPose start, float progress, bool firstPerson, Pose camera,
            Quaternion aim, out float reach)
        {
            float amount = Mathf.Clamp01(progress);
            Vector3 forkOffset = slingshot.ForkMidpoint;
            Vector3 holdFork = hold.Item.position + hold.Item.rotation * forkOffset;
            Pose frame = hold.Item;
            if (firstPerson)
            {
                Vector3 right = camera.rotation * Vector3.right;
                frame.rotation = body.Rotation * item.ChargedRotation * Quaternion.Inverse(item.RightContact.Rotation);
                frame.position = holdFork - right * Vector3.Dot(holdFork - camera.position, right) - frame.rotation * forkOffset;
            }
            else
            {
                Vector3 leftShoulder = body.Shoulder + body.Rotation *
                    ((body.Measurements.LeftShoulder - body.Measurements.RightShoulder) * body.Scale);
                Vector3 center = (body.Shoulder + leftShoulder) * 0.5f;
                float height = Vector3.Dot(holdFork - center, body.Rotation * Vector3.up);
                Vector3 origin = center + aim * (Vector3.up * height);
                Vector3 forward = aim * Vector3.forward;
                frame.rotation = aim * item.ChargedRotation * Quaternion.Inverse(item.RightContact.Rotation);
                frame.position = origin - frame.rotation * forkOffset;
                var holding = FromItem(frame, body, item);
                Quaternion palm = frame.rotation * item.PullingContact.Rotation;
                Quaternion wrist = palm * Quaternion.Inverse(body.Measurements.LeftWristToPalmRotation);
                Vector3 pulling = frame.position + frame.rotation * slingshot.Pouch(amount, item.SlingshotCharge.PullingHandDrawOffset) +
                    frame.rotation * Vector3.Scale(item.PrefabScale, item.PullingContact.Position) - wrist * (body.Measurements.LeftWristToPalmPosition * body.Scale);
                float leftReach = (body.Measurements.LeftArm.x + body.Measurements.LeftArm.y) * body.Scale * 0.85f;
                ReachInterval(holding.WristPosition - body.Shoulder, forward, body.ArmLength * 0.98f,
                    out float holdingNear, out float holdingFar);
                ReachInterval(pulling - leftShoulder, forward, leftReach, out _, out float pullingFar);
                frame.position += forward * Mathf.Clamp(pullingFar, holdingNear, holdingFar);
            }
            frame.position += (firstPerson ? camera.rotation : aim) * item.SlingshotCharge.ChargedPositionOffset;
            frame.position = Vector3.Lerp(start.Item.position, frame.position, amount);
            frame.rotation = Quaternion.Slerp(start.Item.rotation, frame.rotation, amount);
            var result = FromItem(frame, body, item);
            if (!firstPerson)
            {
                Vector3 delta = result.WristPosition - body.Shoulder;
                Vector3 correction = Vector3.ClampMagnitude(delta, body.ArmLength * 0.98f) - delta;
                result = new HeldItemPose(result.FollowPosition + correction,
                    result.WristRotation, body.Measurements, body.Scale, item, result.RequestedRight,
                    limited: correction.sqrMagnitude > 0.000001f);
            }
            reach = SlingshotReach(result, body, item);
            return result;
        }

        private static void ReachInterval(Vector3 offset, Vector3 forward, float radius, out float near, out float far)
        {
            float along = Vector3.Dot(offset, forward);
            float discriminant = radius * radius - (offset.sqrMagnitude - along * along);
            // A missed sphere collapses to the closest point on the aim line.
            float extent = Mathf.Sqrt(Mathf.Max(0f, discriminant));
            near = -along - extent; far = -along + extent;
        }

        internal static HeldItemPose Charge(in HeldItemBodyFrame body, AvatarSettings avatar, in HeldItemPoseData item,
            Vector3 start, Quaternion rotation, float progress)
        {
            float t = LeanTween.easeOutBack(0f, 1f, Mathf.Clamp01(progress), 0.65f), v = 1f - t;
            Vector3 position = v * v * start + 2f * v * t * item.Spatial.ChargeControlPosition + t * t * item.Spatial.ChargedPosition;
            if (item.Heavy) return HeavyItemPoseCalculation.Place(position,
                Quaternion.SlerpUnclamped(rotation, item.ChargedRotation, t), body, item);
            Quaternion wrist = body.Rotation * Quaternion.SlerpUnclamped(rotation, item.ChargedRotation, t) *
                Quaternion.Inverse(body.Measurements.RightWristToPalmRotation);
            return Resolve(body.ToWorld(position), wrist, body, avatar, item, out _);
        }
    }
}
