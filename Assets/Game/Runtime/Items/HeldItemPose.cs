using UnityEngine;

namespace TwoBirds
{
    internal readonly struct HeldItemPoseData
    {
        internal readonly HoldModePoses Poses;
        internal readonly ItemPalmContact RightContact, LeftContact, PullingContact;
        internal readonly AnimationClip Fingers;
        internal readonly Vector3 PrefabScale;
        internal readonly ItemHoldMode HoldMode;
        internal readonly Quaternion PrefabRotation;
        internal readonly ItemReleaseSphere Sphere;
        internal readonly float ReleaseRadius;
        internal bool TwoHand => HoldMode == ItemHoldMode.TwoHand;
        internal HeldItemPoseData(ItemDefinition definition, HeldItemSettings defaults, WorldItem worldItem = null)
        {
            HoldMode = definition.HoldMode;
            PrefabRotation = definition.WorldPrefab.transform.localRotation;
            var geometry = worldItem ? worldItem : definition.WorldPrefab.GetComponent<WorldItem>();
            if (geometry) geometry.CacheReleaseGeometry();
            Sphere = geometry ? geometry.ReleaseSphere : default;
            ReleaseRadius = geometry ? geometry.ReleaseRadius : 0f;
            Poses = defaults.Poses(definition.HoldMode);
            Fingers = definition.GripFingers;
            RightContact = definition.RightPalmContact;
            LeftContact = definition.LeftPalmContact;
            PullingContact = definition is SlingshotDefinition pulling ? pulling.PullingPalmContact : default;
            PrefabScale = definition.WorldPrefab.transform.localScale;
        }
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
        internal Pose CenterToWorld(Pose pose) => new(CenterToWorld(pose.position), Rotation * pose.rotation);
        internal Pose CenterToLocal(Pose pose) => new(CenterToLocal(pose.position), Quaternion.Inverse(Rotation) * pose.rotation);
        internal HeldItemBodyFrame WithMeasurements(AvatarSettings.GeneratedSkeleton measurements, float scale) =>
            new(Center + Rotation * ((measurements.RightShoulder - measurements.LeftShoulder) * (scale * 0.5f)), Rotation, measurements, scale);
    }

    internal static class HeldItemPoseCalculation
    {
        private static readonly Vector3 FallbackPalm = new(0.15f, -0.40f, 0.50f), FallbackCenter = new(0f, -0.40f, 0.50f);

        internal static Pose PalmFromItem(Pose root, ItemPalmContact contact, Vector3 scale) =>
            new(root.position + root.rotation * Vector3.Scale(scale, contact.Position), root.rotation * contact.Rotation);

        internal static Pose ItemFromPalm(Pose palm, ItemPalmContact contact, Vector3 scale)
        {
            Quaternion rotation = palm.rotation * Quaternion.Inverse(contact.Rotation);
            return new Pose(palm.position - rotation * Vector3.Scale(scale, contact.Position), rotation);
        }

        internal static Pose TwoHandAnchor(Pose rightPalm, Pose leftPalm, ItemPalmContact right, ItemPalmContact left, Vector3 scale)
        {
            Quaternion rotation = Quaternion.Slerp(rightPalm.rotation * Quaternion.Inverse(right.Rotation),
                leftPalm.rotation * Quaternion.Inverse(left.Rotation), 0.5f);
            Vector3 rightGrip = Vector3.Scale(scale, right.Position), leftGrip = Vector3.Scale(scale, left.Position);
            Vector3 gripAxis = rotation * (leftGrip - rightGrip), palmAxis = leftPalm.position - rightPalm.position;
            if (gripAxis.sqrMagnitude > 0.000001f && palmAxis.sqrMagnitude > 0.000001f)
                rotation = Quaternion.FromToRotation(gripAxis, palmAxis) * rotation;
            return new Pose((rightPalm.position + leftPalm.position) * 0.5f - rotation * ((rightGrip + leftGrip) * 0.5f), rotation);
        }

        internal static Pose Fallback(in HeldItemBodyFrame body, in HeldItemPoseData data)
        {
            if (!data.TwoHand) return ItemFromPalm(new Pose(body.ToWorld(FallbackPalm), body.Rotation), data.RightContact, data.PrefabScale);
            Quaternion rotation = body.Rotation * data.PrefabRotation;
            return new Pose(body.CenterToWorld(FallbackCenter) - rotation * data.Sphere.Center, rotation);
        }
    }
}
