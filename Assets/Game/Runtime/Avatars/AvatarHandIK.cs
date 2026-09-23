using UnityEngine;

namespace TwoBirds
{
    internal sealed class AvatarHandIK
    {
        private sealed class Hand
        {
            internal Transform Shoulder, Elbow, Wrist;
            internal Vector3 PalmOffset, GoalOffset;
            internal Quaternion PalmRotation, BoneToGoal;
            internal float Length, Weight;
            internal bool Seeded, HadCarryTarget;
            internal Vector3 TargetWrist;
            internal Quaternion TargetRotation;
        }
        private readonly Animator animator;
        private readonly Hand left, right;
        private bool calibrated;

        internal AvatarHandIK(AvatarBinding binding)
        {
            animator = binding.Animator;
            var data = binding.Measurements;
            Hand Bind(bool rightHand) => new()
            {
                Shoulder = binding.GetBone(rightHand ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm),
                Elbow = binding.GetBone(rightHand ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm),
                Wrist = binding.GetBone(rightHand ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand),
                PalmOffset = (rightHand ? data.RightWristToPalmPosition : data.LeftWristToPalmPosition) * binding.Scale,
                PalmRotation = rightHand ? data.RightWristToPalmRotation : data.LeftWristToPalmRotation,
                Length = (rightHand ? data.RightArm.x + data.RightArm.y : data.LeftArm.x + data.LeftArm.y) * binding.Scale
            };
            left = Bind(false); right = Bind(true);
        }

        internal void Apply(AvatarHandTargets targets, float dt)
        {
            if (!calibrated)
            {
                Calibrate(left, AvatarIKGoal.LeftHand); Calibrate(right, AvatarIKGoal.RightHand);
                calibrated = true;
            }
            Apply(left, AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, targets.Resolve(AvatarIKGoal.LeftHand), dt, -1f);
            Apply(right, AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, targets.Resolve(AvatarIKGoal.RightHand), dt, 1f);
        }

        internal void CorrectCarry(AvatarHandTargets targets)
        {
            Correct(left, targets.Resolve(AvatarIKGoal.LeftHand));
            Correct(right, targets.Resolve(AvatarIKGoal.RightHand));
        }

        private static void Correct(Hand hand, AvatarHandTargets.Target target)
        {
            if (!hand.HadCarryTarget || !target.Transform || target.Source != AvatarHandSource.Carry) return;
            Quaternion wrist = target.Transform.rotation * Quaternion.Inverse(hand.PalmRotation);
            Vector3 root = hand.Shoulder.position;
            Vector3 delta = target.Transform.position - wrist * hand.PalmOffset - root;
            Vector3 desired = root + Vector3.ClampMagnitude(delta, hand.Length * Mathf.Min(target.MaximumReach, 0.98f));
            Vector3 end = hand.Wrist.position + (desired - hand.TargetWrist) * target.Position;
            Quaternion rotation = Quaternion.Slerp(Quaternion.identity, wrist * Quaternion.Inverse(hand.TargetRotation), target.Rotation) * hand.Wrist.rotation;
            hand.TargetWrist = desired; hand.TargetRotation = wrist;
            if ((end - hand.Wrist.position).sqrMagnitude > 0.00000001f)
            {
                float upper = Vector3.Distance(root, hand.Elbow.position);
                float lower = Vector3.Distance(hand.Elbow.position, hand.Wrist.position);
                Vector3 direction = (end - root).normalized;
                float distance = Mathf.Clamp(Vector3.Distance(root, end), Mathf.Abs(upper - lower) + 0.0001f, upper + lower - 0.0001f);
                Vector3 bend = Vector3.ProjectOnPlane(hand.Elbow.position - root, direction).normalized;
                if (bend.sqrMagnitude < 0.001f) bend = Vector3.ProjectOnPlane(hand.Shoulder.forward, direction).normalized;
                float along = (upper * upper - lower * lower + distance * distance) / (2f * distance);
                Vector3 elbow = root + direction * along + bend * Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));
                hand.Shoulder.rotation = Quaternion.FromToRotation(hand.Elbow.position - root, elbow - root) * hand.Shoulder.rotation;
                hand.Elbow.rotation = Quaternion.FromToRotation(hand.Wrist.position - hand.Elbow.position,
                    root + direction * distance - hand.Elbow.position) * hand.Elbow.rotation;
            }
            hand.Wrist.rotation = rotation;
        }

        private void Calibrate(Hand hand, AvatarIKGoal goal)
        {
            Quaternion inverse = Quaternion.Inverse(hand.Wrist.rotation);
            hand.GoalOffset = inverse * (animator.GetIKPosition(goal) - hand.Wrist.position);
            hand.BoneToGoal = inverse * animator.GetIKRotation(goal);
        }

        private void Apply(Hand hand, AvatarIKGoal goal, AvatarIKHint hint, AvatarHandTargets.Target target, float dt, float side)
        {
            float weight = 0f;
            hand.HadCarryTarget = target.Transform && target.Source == AvatarHandSource.Carry;
            if (target.Transform)
            {
                Quaternion wrist = target.Transform.rotation * Quaternion.Inverse(hand.PalmRotation);
                Vector3 delta = target.Transform.position - wrist * hand.PalmOffset - hand.Shoulder.position;
                float reach = target.MaximumReach > 0f ? Mathf.Min(target.MaximumReach, 0.98f) : 0.98f;
                weight = target.Contact ? 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(reach - 0.08f, reach, delta.magnitude / hand.Length)) : 1f;
                hand.Weight = target.Contact && hand.Seeded ? Mathf.Lerp(hand.Weight, weight, AvatarPresentation.Smooth(dt, 0.08f)) : weight;
                hand.Seeded = true;
                hand.TargetWrist = hand.Shoulder.position + Vector3.ClampMagnitude(delta, hand.Length * reach);
                hand.TargetRotation = wrist;
                animator.SetIKPosition(goal, hand.TargetWrist + wrist * hand.GoalOffset);
                animator.SetIKRotation(goal, wrist * hand.BoneToGoal);
                animator.SetIKHintPosition(hint, hand.Shoulder.position + animator.transform.rotation *
                    (new Vector3(side * 0.35f, -0.45f, -0.1f) * hand.Length));
                weight = hand.Weight;
            }
            animator.SetIKPositionWeight(goal, target.Position * weight);
            animator.SetIKRotationWeight(goal, target.Rotation * weight);
            animator.SetIKHintPositionWeight(hint, target.Position * weight * 0.4f);
        }
    }
}
