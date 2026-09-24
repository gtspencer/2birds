using UnityEngine;

namespace TwoBirds
{
    internal sealed class AvatarArmIK
    {
        private sealed class Arm
        {
            internal Transform Upper, Lower, Hand;
            internal float Weight;
            internal bool Seeded;
        }
        private readonly AvatarBinding binding;
        private readonly Arm left, right;

        internal AvatarArmIK(AvatarBinding binding)
        {
            this.binding = binding;
            Arm Bind(bool rightHand) => new()
            {
                Upper = binding.GetBone(rightHand ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm),
                Lower = binding.GetBone(rightHand ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm),
                Hand = binding.GetBone(rightHand ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand)
            };
            left = Bind(false); right = Bind(true);
        }

        internal void Solve(AvatarHandTargets targets, float dt)
        {
            Pose body = binding.Body;
            Solve(left, false, targets.Resolve(AvatarIKGoal.LeftHand), targets.Body, body, dt);
            Solve(right, true, targets.Resolve(AvatarIKGoal.RightHand), targets.Body, body, dt);
        }

        internal static float SoftReach(float distance, float reach)
        {
            float start = reach * 0.85f;
            if (distance <= start) return distance;
            float range = reach - start;
            return start + range * (1f - Mathf.Exp(-(distance - start) / range));
        }

        private void Solve(Arm arm, bool rightHand, AvatarHandTargets.Target target, Pose prepared, Pose body, float dt)
        {
            if (!target.Transform) return;
            var data = binding.Measurements;
            float scale = binding.Scale;
            float length = (rightHand ? data.RightArm.x + data.RightArm.y : data.LeftArm.x + data.LeftArm.y) * scale;
            Pose palm = new(target.Transform.position, target.Transform.rotation);
            if (target.BodyRelative) palm = AvatarHandTargets.Rebase(palm, prepared, body);
            Quaternion wrist = palm.rotation * Quaternion.Inverse(rightHand ? data.RightWristToPalmRotation : data.LeftWristToPalmRotation);
            Vector3 root = arm.Upper.position;
            Vector3 delta = palm.position - wrist * ((rightHand ? data.RightWristToPalmPosition : data.LeftWristToPalmPosition) * scale) - root;
            float reach = target.MaximumReach > 0f ? Mathf.Min(target.MaximumReach, 0.98f) : 0.98f;
            float weight = target.Contact ? 1f - Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(reach - 0.08f, reach, delta.magnitude / length)) : 1f;
            arm.Weight = target.Contact && arm.Seeded ? Mathf.Lerp(arm.Weight, weight, AvatarPresentation.Smooth(dt, 0.08f)) : weight;
            arm.Seeded = true;
            float cap = length * reach, distance = delta.magnitude;
            if (distance > cap * 0.85f) delta *= SoftReach(distance, cap) / distance;
            float positionWeight = target.Position * arm.Weight, rotationWeight = target.Rotation * arm.Weight;
            if (positionWeight <= 0f && rotationWeight <= 0f) return;
            Vector3 end = Vector3.Lerp(arm.Hand.position, root + delta, positionWeight);
            Quaternion rotation = Quaternion.Slerp(arm.Hand.rotation, wrist, rotationWeight);
            if ((end - arm.Hand.position).sqrMagnitude > 0.00000001f)
            {
                float upper = Vector3.Distance(root, arm.Lower.position);
                float lower = Vector3.Distance(arm.Lower.position, arm.Hand.position);
                Vector3 direction = (end - root).normalized;
                float span = Mathf.Clamp(Vector3.Distance(root, end), Mathf.Abs(upper - lower) + 0.0001f, upper + lower - 0.0001f);
                Vector3 bend = Vector3.ProjectOnPlane(arm.Lower.position - root, direction).normalized;
                if (bend.sqrMagnitude < 0.001f) bend = Vector3.ProjectOnPlane(arm.Upper.forward, direction).normalized;
                float along = (upper * upper - lower * lower + span * span) / (2f * span);
                Vector3 elbow = root + direction * along + bend * Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));
                arm.Upper.rotation = Quaternion.FromToRotation(arm.Lower.position - root, elbow - root) * arm.Upper.rotation;
                arm.Lower.rotation = Quaternion.FromToRotation(arm.Hand.position - arm.Lower.position,
                    root + direction * span - arm.Lower.position) * arm.Lower.rotation;
            }
            arm.Hand.rotation = rotation;
        }
    }
}
