using UnityEngine;

namespace TwoBirds
{
    internal sealed class AvatarHumanoidIK
    {
        private struct Foot
        {
            internal AvatarIKGoal Goal;
            internal Transform Hip, Bone;
            internal float Length, Adjustment, Correction, Confidence, Weight;
            internal Vector3 Offset, Normal, Position;
            internal Quaternion GoalToSole, RestRotation, AuthoredRotation, Rotation;
        }
        private readonly AvatarPresentation host;
        private readonly AvatarSettings settings;
        private readonly Animator animator;
        private readonly Transform head;
        private Foot left, right;
        private float groundedWeight, pelvis;
        private Vector3 lookDirection;
        private bool calibrated;
        private readonly float height;
        internal float DeltaTime;
        internal Vector3 LookTarget { get; private set; }

        internal AvatarHumanoidIK(AvatarPresentation host, AvatarBinding binding)
        {
            this.host = host; settings = binding.Settings; animator = binding.Animator;
            height = settings.VisualHeight;
            head = binding.GetBone(HumanBodyBones.Head);
            var data = settings.Generated;
            left = new Foot { Goal = AvatarIKGoal.LeftFoot, Hip = binding.GetBone(HumanBodyBones.LeftUpperLeg),
                Length = (data.LeftLeg.x + data.LeftLeg.y) * settings.Scale, Offset = data.LeftSoleToGoal * settings.Scale,
                Bone = binding.GetBone(HumanBodyBones.LeftFoot), RestRotation = data.LeftFootRestRotation,
                Adjustment = settings.LeftSoleAdjustment, AuthoredRotation = settings.LeftFootRotation };
            right = new Foot { Goal = AvatarIKGoal.RightFoot, Hip = binding.GetBone(HumanBodyBones.RightUpperLeg),
                Length = (data.RightLeg.x + data.RightLeg.y) * settings.Scale, Offset = data.RightSoleToGoal * settings.Scale,
                Bone = binding.GetBone(HumanBodyBones.RightFoot), RestRotation = data.RightFootRestRotation,
                Adjustment = settings.RightSoleAdjustment, AuthoredRotation = settings.RightFootRotation };
            Reset();
        }

        internal void Reset()
        {
            left.Correction = right.Correction = left.Weight = right.Weight = pelvis = groundedWeight = 0f;
            left.Normal = right.Normal = Vector3.up;
            lookDirection = Vector3.zero;
        }

        internal void Apply()
        {
            if (!calibrated)
            {
                Calibrate(ref left); Calibrate(ref right);
                calibrated = true;
            }
            bool attached = host.Input.Seated || host.Input.Carried && !host.Input.ReleasePreview || host.Input.Pending;
            bool grounded = host.Input.Grounded && host.Input.Mode != MovementMode.External && !host.Input.ReleasePreview;
            if (!host.FootIkEnabled || attached)
            {
                groundedWeight = pelvis = 0f;
                ZeroFoot(AvatarIKGoal.LeftFoot); ZeroFoot(AvatarIKGoal.RightFoot);
            }
            else
            {
                groundedWeight = Mathf.MoveTowards(groundedWeight, grounded ? 1f : 0f, DeltaTime / (grounded ? 0.12f : 0.08f));
                Probe(ref left, grounded); Probe(ref right, grounded);
                float correction = 0f, confidence = 0f;
                if (left.Confidence > 0.25f) { correction = left.Correction; confidence = left.Confidence; }
                if (right.Confidence > 0.25f && (confidence == 0f || right.Correction < correction))
                { correction = right.Correction; confidence = right.Confidence; }
                float target = Mathf.Clamp(correction * confidence, -0.06f * height, 0.025f * height);
                pelvis = Mathf.Lerp(pelvis, target, AvatarPresentation.Smooth(DeltaTime, 0.08f));
                float shift = pelvis * settings.PelvisCorrection * groundedWeight;
                animator.bodyPosition += Vector3.up * shift;
                ApplyFoot(ref left, shift); ApplyFoot(ref right, shift);
            }
            ApplyHead();
        }

        private void Calibrate(ref Foot foot)
        {
            Quaternion sole = foot.Bone.rotation * Quaternion.Inverse(foot.RestRotation);
            foot.GoalToSole = Quaternion.Inverse(animator.GetIKRotation(foot.Goal)) * sole;
            foot.Offset += Quaternion.Inverse(sole) * (animator.GetIKPosition(foot.Goal) - foot.Bone.position);
        }

        private void Probe(ref Foot foot, bool grounded)
        {
            float h = height;
            Vector3 animated = animator.GetIKPosition(foot.Goal);
            Quaternion rotation = animator.GetIKRotation(foot.Goal);
            Quaternion soleRotation = rotation * foot.GoalToSole;
            Vector3 sole = animated - soleRotation * foot.Offset - Vector3.up * foot.Adjustment;
            foot.Position = animated;
            foot.Rotation = rotation;
            foot.Confidence = 0f;
            if (!grounded || !Physics.Raycast(sole + Vector3.up * (0.12f * h), Vector3.down, out var hit,
                0.24f * h, host.Input.GroundMask, QueryTriggerInteraction.Ignore))
            { foot.Weight = Mathf.MoveTowards(foot.Weight, 0f, DeltaTime / 0.08f); return; }
            float slope = Vector3.Angle(Vector3.up, hit.normal);
            Vector3 targetSole = hit.point + hit.normal * (0.003f * h);
            float delta = targetSole.y - sole.y;
            float displacement = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.06f * h, 0.12f * h, Mathf.Abs(delta)));
            float stride = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.015f * h, 0.075f * h, sole.y - host.Input.SolePosition.y));
            float contact = Mathf.Lerp(1f, stride, host.State.Motion);
            float slopeWeight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(45f, 55f, slope));
            float reach = Reach(Vector3.Distance(foot.Hip.position, targetSole + soleRotation * foot.Offset), foot.Length);
            foot.Confidence = contact * displacement * slopeWeight * reach;
            if (foot.Confidence <= 0f)
            { foot.Weight = Mathf.MoveTowards(foot.Weight, 0f, DeltaTime / 0.08f); return; }
            float alpha = AvatarPresentation.Smooth(DeltaTime, 0.04f);
            foot.Correction = Mathf.Lerp(foot.Correction, delta, alpha);
            foot.Normal = Vector3.Slerp(foot.Normal, hit.normal, alpha);
            Vector3 forward = Vector3.ProjectOnPlane(soleRotation * Vector3.forward, foot.Normal);
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.ProjectOnPlane(host.transform.forward, foot.Normal);
            Quaternion tilt = Quaternion.LookRotation(forward, foot.Normal);
            tilt = Quaternion.RotateTowards(soleRotation, tilt, 35f);
            foot.Position = sole + Vector3.up * (foot.Correction + foot.Adjustment) + tilt * foot.Offset;
            foot.Rotation = tilt * Quaternion.Inverse(foot.GoalToSole) * foot.AuthoredRotation;
            foot.Weight = Mathf.Lerp(foot.Weight, foot.Confidence, alpha);
        }

        private void ApplyFoot(ref Foot foot, float pelvisShift)
        {
            Vector3 hip = foot.Hip.position + Vector3.up * pelvisShift;
            Vector3 offset = foot.Position - hip;
            float weight = foot.Weight * groundedWeight * settings.FootCorrection * Reach(offset.magnitude, foot.Length);
            animator.SetIKPositionWeight(foot.Goal, weight);
            animator.SetIKRotationWeight(foot.Goal, weight);
            animator.SetIKPosition(foot.Goal, hip + Vector3.ClampMagnitude(offset, foot.Length * 0.99f));
            animator.SetIKRotation(foot.Goal, foot.Rotation);
        }

        private static float Reach(float distance, float length) =>
            1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.98f, 1.03f, distance / length));
        private void ZeroFoot(AvatarIKGoal goal) { animator.SetIKPositionWeight(goal, 0f); animator.SetIKRotationWeight(goal, 0f); }

        private void ApplyHead()
        {
            if (host.EditorPreview || !host.HeadLookEnabled) { animator.SetLookAtWeight(0f); return; }
            Vector3 local = Quaternion.Inverse(host.transform.rotation) *
                (Quaternion.Euler(host.Input.LookPitch, host.Input.LookYaw, 0f) * Vector3.forward);
            float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -70f, 70f);
            float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -40f, 50f);
            Vector3 direction = host.transform.rotation * Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            lookDirection = lookDirection == Vector3.zero ? direction : Vector3.Slerp(lookDirection, direction, AvatarPresentation.Smooth(DeltaTime, 0.05f));
            LookTarget = head.position + lookDirection * (3f * height);
            animator.SetLookAtWeight(1f, 0.15f, 1f, 0f, 0.5f);
            animator.SetLookAtPosition(LookTarget);
        }

    }
}
