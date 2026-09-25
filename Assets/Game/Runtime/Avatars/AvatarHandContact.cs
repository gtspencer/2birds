using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed class AvatarHandContact : MonoBehaviour
    {
        public AvatarIKGoal Hand;
        public AnimationClip Fingers;
        [Range(0.1f, 0.98f)] public float MaximumReach = 0.98f;
        [Min(0f)] public float BlendTime = 0.15f;
        [Tooltip("Frame the elbow hint is stored in, so turning the palm's parent does not swing the elbow. Defaults to the parent.")]
        public Transform HintFrame;
        [Tooltip("Contact defaults: palm in the parent frame, elbow hint in the hint frame.")]
        public GripPoseTable Poses = new();
        public List<AvatarGripPoses> AvatarPoses = new();
        internal Transform HintSpace => HintFrame ? HintFrame : transform.parent;

        internal bool TryResolve(GripTarget target, AvatarId avatar, bool firstPerson, out Pose local, out GripLayer layer)
        {
            var overrides = GripPoses.Find(AvatarPoses, avatar);
            if (overrides != null && overrides.TryGet(target, firstPerson, out local)) { layer = GripLayer.Avatar; return true; }
            if (Poses.TryGet(target, firstPerson, out local)) { layer = GripLayer.Contact; return true; }
            layer = GripLayer.None;
            return false;
        }

        internal Pose Palm(AvatarId avatar, bool firstPerson)
        {
            var parent = transform.parent;
#if UNITY_INCLUDE_INSTRUMENTATION
            if (LiveView == firstPerson) return new Pose(transform.position, transform.rotation);
#endif
            if (!parent || !TryResolve(GripTarget.ContactPalm, avatar, firstPerson, out var local, out _))
                return new Pose(transform.position, transform.rotation);
            return new Pose(parent.TransformPoint(local.position), parent.rotation * local.rotation);
        }

        internal bool TryHint(AvatarId avatar, bool firstPerson, out Vector3 world)
        {
#if UNITY_INCLUDE_INSTRUMENTATION
            if (LiveView == firstPerson && authoredHint)
            {
                world = authoredHint.position;
                return hintDefined || (authoredHint.localPosition - appliedHint).sqrMagnitude > GripPoses.DirtyDistance * GripPoses.DirtyDistance;
            }
#endif
            var space = HintSpace;
            if (space && TryResolve(GripTarget.ContactElbow, avatar, firstPerson, out var local, out _))
            {
                world = space.TransformPoint(local.position);
                return true;
            }
            world = default;
            return false;
        }

#if UNITY_INCLUDE_INSTRUMENTATION
        public static bool? LiveView;
        private Transform authoredHint;
        private Pose rest, appliedPalm;
        private Vector3 appliedHint;
        private GripLayer palmLayer, hintLayer;
        private bool hintDefined, rested;

        public Transform AuthoredHint
        {
            get
            {
                if (authoredHint) return authoredHint;
                authoredHint = new GameObject($"{name} Elbow Hint").transform;
                authoredHint.SetParent(HintSpace, false);
                return authoredHint;
            }
        }

        public void ApplyAuthored(AvatarId avatar, bool firstPerson)
        {
            if (!rested) { rest = new Pose(transform.localPosition, transform.localRotation); rested = true; }
            var palm = TryResolve(GripTarget.ContactPalm, avatar, firstPerson, out var local, out palmLayer) ? local : rest;
            transform.SetLocalPositionAndRotation(palm.position, palm.rotation);
            appliedPalm = new Pose(transform.localPosition, transform.localRotation);
            var hint = AuthoredHint;
            hintDefined = TryResolve(GripTarget.ContactElbow, avatar, firstPerson, out var elbow, out hintLayer);
            if (hintDefined) hint.localPosition = elbow.position;
            else hint.position = transform.position + HintSpace.rotation * (Vector3.down * 0.3f);
            hint.localRotation = Quaternion.identity;
            appliedHint = hint.localPosition;
        }

        public bool TryAuthored(GripTarget target, out Transform authored, out Pose stored, out GripLayer layer, out bool dirty)
        {
            if (target == GripTarget.ContactPalm)
            {
                authored = transform; layer = palmLayer;
                stored = new Pose(transform.localPosition, transform.localRotation);
                dirty = GripPoses.Differs(stored, appliedPalm);
                return true;
            }
            if (target == GripTarget.ContactElbow)
            {
                authored = AuthoredHint; layer = hintLayer;
                stored = new Pose(authored.localPosition, Quaternion.identity);
                dirty = (stored.position - appliedHint).sqrMagnitude > GripPoses.DirtyDistance * GripPoses.DirtyDistance;
                return true;
            }
            authored = null; stored = default; layer = GripLayer.None; dirty = false;
            return false;
        }

        public void CopyPoses(AvatarHandContact source)
        {
            Poses.CopyFrom(source.Poses);
            AvatarPoses.Clear();
            foreach (var entry in source.AvatarPoses)
            {
                var copy = new AvatarGripPoses { Avatar = entry.Avatar };
                copy.Poses.CopyFrom(entry.Poses);
                AvatarPoses.Add(copy);
            }
        }

        private void OnDestroy() { if (authoredHint) Destroy(authoredHint.gameObject); }
#endif
    }
}
