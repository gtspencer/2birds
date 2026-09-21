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
            internal bool Seeded;
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

        internal Pose Palm(bool rightHand)
        {
            var hand = rightHand ? right : left;
            return new Pose(hand.Wrist.position + hand.Wrist.rotation * hand.PalmOffset, hand.Wrist.rotation * hand.PalmRotation);
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

        private void Calibrate(Hand hand, AvatarIKGoal goal)
        {
            Quaternion inverse = Quaternion.Inverse(hand.Wrist.rotation);
            hand.GoalOffset = inverse * (animator.GetIKPosition(goal) - hand.Wrist.position);
            hand.BoneToGoal = inverse * animator.GetIKRotation(goal);
        }

        private void Apply(Hand hand, AvatarIKGoal goal, AvatarIKHint hint, AvatarHandTargets.Target target, float dt, float side)
        {
            float weight = 0f;
            if (target.Transform)
            {
                Quaternion wrist = target.Transform.rotation * Quaternion.Inverse(hand.PalmRotation);
                Vector3 delta = target.Transform.position - wrist * hand.PalmOffset - hand.Shoulder.position;
                float reach = target.MaximumReach > 0f ? Mathf.Min(target.MaximumReach, 0.98f) : 0.98f;
                weight = target.Contact ? 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(reach - 0.08f, reach, delta.magnitude / hand.Length)) : 1f;
                hand.Weight = target.Contact && hand.Seeded ? Mathf.Lerp(hand.Weight, weight, AvatarPresentation.Smooth(dt, 0.08f)) : weight;
                hand.Seeded = true;
                animator.SetIKPosition(goal, hand.Shoulder.position + Vector3.ClampMagnitude(delta, hand.Length * reach) + wrist * hand.GoalOffset);
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
