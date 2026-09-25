using UnityEngine;

namespace TwoBirds
{
    internal readonly struct HeldItemPoseData
    {
        internal readonly HoldClass Class;
        internal readonly AnimationClip Fingers;
        internal readonly ItemHoldMode HoldMode;
        internal readonly Vector3 PouchOffset;
        internal readonly ItemReleaseSphere Sphere;
        internal readonly float ReleaseRadius;
        internal bool TwoHand => HoldMode == ItemHoldMode.TwoHand;
        internal HeldItemPoseData(ItemDefinition definition)
        {
            Class = definition.HoldClass;
            HoldMode = definition.HoldMode;
            var geometry = definition.WorldPrefab.GetComponent<WorldItem>();
            if (geometry) geometry.CacheReleaseGeometry();
            Sphere = geometry ? geometry.ReleaseSphere : default;
            ReleaseRadius = geometry ? geometry.ReleaseRadius : 0f;
            Fingers = definition.GripFingers;
            PouchOffset = definition is SlingshotDefinition slingshot ? slingshot.PouchOffset : default;
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

    public static class HeldItemPoseCalculation
    {
        private static readonly Vector3 FallbackPalm = new(0.15f, -0.40f, 0.50f), FallbackCenter = new(0f, -0.40f, 0.50f);

        public static Pose Compose(Pose frame, Pose offset) =>
            new(frame.position + frame.rotation * offset.position, frame.rotation * offset.rotation);

        public static Pose Inverse(Pose pose)
        {
            Quaternion rotation = Quaternion.Inverse(pose.rotation);
            return new Pose(rotation * -pose.position, rotation);
        }

        internal static Pose GripFrame(Pose right, Pose left, Quaternion body)
        {
            Vector3 x = right.position - left.position;
            x = x.sqrMagnitude > 0.000001f ? x.normalized : body * Vector3.right;
            Vector3 fingers = Vector3.ProjectOnPlane(right.rotation * Vector3.forward + left.rotation * Vector3.forward, x);
            Vector3 z = Vector3.ProjectOnPlane(body * Vector3.forward, x).normalized;
            // Fingers pointing at each other leave no usable direction; lean on the body's forward instead.
            if (fingers.sqrMagnitude > 0.000001f) z = Vector3.Slerp(z, fingers.normalized, Mathf.InverseLerp(0.2f, 0.6f, fingers.magnitude));
            return new Pose((right.position + left.position) * 0.5f, Quaternion.LookRotation(z, Vector3.Cross(z, x)));
        }

        internal static Pose FallbackFrame(in HeldItemBodyFrame body, in HeldItemPoseData data) =>
            new(data.TwoHand ? body.CenterToWorld(FallbackCenter) : body.ToWorld(FallbackPalm), body.Rotation);
    }
}
