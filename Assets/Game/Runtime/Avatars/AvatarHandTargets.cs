using UnityEngine;

namespace TwoBirds
{
    public enum AvatarHandSource : byte { Free, Item, Contact }

    public sealed class AvatarHandTargets
    {
        internal struct Target
        {
            internal Transform Transform;
            internal float Position, Rotation, MaximumReach, OpenWeight;
            internal bool Contact;
            internal AnimationClip Fingers;
        }
        private readonly Target[,] candidates = new Target[2, 3];

        public void Set(AvatarIKGoal hand, AvatarHandSource source, Transform target, float position = 1f,
            float rotation = 1f, float maximumReach = 0.98f, AnimationClip fingers = null, float openWeight = 0f)
        {
            candidates[hand == AvatarIKGoal.LeftHand ? 0 : 1, (int)source] = new Target
            {
                Transform = target, Position = Mathf.Clamp01(position), Rotation = Mathf.Clamp01(rotation),
                MaximumReach = maximumReach, OpenWeight = openWeight, Contact = source == AvatarHandSource.Contact, Fingers = fingers
            };
        }
        public void Clear(AvatarIKGoal hand, AvatarHandSource source) =>
            candidates[hand == AvatarIKGoal.LeftHand ? 0 : 1, (int)source] = default;
        internal Target Resolve(AvatarIKGoal hand)
        {
            int index = hand == AvatarIKGoal.LeftHand ? 0 : 1;
            for (int i = 2; i >= 0; i--)
                if (candidates[index, i].Transform) return candidates[index, i];
            return default;
        }
    }
}
