using UnityEngine;

namespace TwoBirds
{
    public enum AvatarHandSource : byte { Free, Item, Carry, Contact }

    public sealed class AvatarHandTargets
    {
        internal struct Target
        {
            internal Transform Transform;
            internal AvatarHandSource Source;
            internal float Position, Rotation, MaximumReach, OpenWeight;
            internal bool Contact, BodyRelative;
            internal AnimationClip Fingers;
        }
        private readonly Target[,] candidates = new Target[2, 4];
        internal Pose Body { get; private set; } = Pose.identity;
        internal void SetBody(Pose body) => Body = body;

        public void Set(AvatarIKGoal hand, AvatarHandSource source, Transform target, float position = 1f,
            float rotation = 1f, float maximumReach = 0.98f, AnimationClip fingers = null, float openWeight = 0f, bool bodyRelative = false)
        {
            candidates[hand == AvatarIKGoal.LeftHand ? 0 : 1, (int)source] = new Target
            {
                Transform = target, Source = source, Position = Mathf.Clamp01(position), Rotation = Mathf.Clamp01(rotation),
                MaximumReach = maximumReach, OpenWeight = openWeight, Contact = source == AvatarHandSource.Contact, Fingers = fingers,
                BodyRelative = bodyRelative
            };
        }
        public void Clear(AvatarIKGoal hand, AvatarHandSource source) =>
            candidates[hand == AvatarIKGoal.LeftHand ? 0 : 1, (int)source] = default;
        internal Target Resolve(AvatarIKGoal hand)
        {
            int index = hand == AvatarIKGoal.LeftHand ? 0 : 1;
            for (int i = 3; i >= 0; i--)
                if (candidates[index, i].Transform) return candidates[index, i];
            return default;
        }

        internal static Pose Rebase(Pose pose, Pose from, Pose to)
        {
            Quaternion rotation = to.rotation * Quaternion.Inverse(from.rotation);
            return new Pose(to.position + rotation * (pose.position - from.position), rotation * pose.rotation);
        }
    }
}
