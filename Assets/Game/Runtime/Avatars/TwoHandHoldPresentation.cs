using UnityEngine;

namespace TwoBirds
{
    internal sealed class TwoHandHoldPresentation
    {
        internal enum RecoveryStage : byte { Follow, Pause, Return, Finished }
        internal Pose Left { get; private set; }
        internal Pose Right { get; private set; }
        internal float LeftWeight { get; private set; }
        internal float RightWeight { get; private set; }
        internal float FrameWeight { get; private set; }
        internal RecoveryStage Stage { get; private set; }
        internal bool Following { get; private set; }
        internal bool Releasing { get; private set; }
        private Pose startLeft, startRight, retainedLeft, retainedRight;
        private float startLeftWeight, startRightWeight, startFrameWeight;
        private double returnStart;

        internal static float Reach(in HeldItemPoseSettings settings) => Mathf.Clamp(settings.FollowReachFraction, 0.01f, 0.85f);

        internal static HeldItemBodyFrame Body(PlayerAvatarPresentation owner, AvatarSettings settings)
        {
            var avatar = owner.Presentation;
            bool remote = !owner.IsOwner && avatar.Binding != null;
            var input = remote ? avatar.Input : owner.CurrentPlacement;
            bool attached = input.Seated || input.Carried && !input.ReleasePreview;
            Quaternion torso = remote ? avatar.transform.rotation : attached ? input.Facing.rotation :
                Quaternion.Euler(0f, input.Facing.rotation.eulerAngles.y, 0f);
            float seated = avatar.Binding != null ? avatar.State.Weights[(int)AvatarPose.Seated] : input.Seated ? 1f : 0f;
            return new HeldItemBodyFrame(settings, avatar.Registry, input, torso, seated, true);
        }

        internal void Hold(Pose left, Pose right)
        {
            Left = left; Right = right;
            LeftWeight = RightWeight = FrameWeight = 1f;
            Releasing = false;
        }

        internal void BeginRelease(Pose left, Pose right, in HeldItemBodyFrame body)
        {
            Hold(left, right);
            startLeft = retainedLeft = HeavyItemPoseCalculation.ToLocal(left, body);
            startRight = retainedRight = HeavyItemPoseCalculation.ToLocal(right, body);
            startLeftWeight = startRightWeight = startFrameWeight = 1f;
            returnStart = -1d;
            Stage = RecoveryStage.Follow;
            Following = Releasing = true;
        }

        internal void Retarget(in HeldItemBodyFrame body, double elapsed)
        {
            if (Stage != RecoveryStage.Return) return;
            retainedLeft = HeavyItemPoseCalculation.ToLocal(Left, body);
            retainedRight = HeavyItemPoseCalculation.ToLocal(Right, body);
            startLeftWeight = LeftWeight; startRightWeight = RightWeight; startFrameWeight = FrameWeight;
            returnStart = elapsed;
        }

        internal float ReturnProgress(in HeldItemPoseSettings settings, double elapsed)
        {
            double pause = Mathf.Max(0f, settings.MaximumFollowDuration) + Mathf.Max(0f, settings.EndPosePauseDuration);
            double start = System.Math.Max(pause, returnStart);
            double end = pause + Mathf.Max(0f, settings.ReturnBlendDuration);
            return elapsed < start ? 0f : end <= start ? 1f : Mathf.Clamp01((float)((elapsed - start) / (end - start)));
        }

        internal float Frame(in HeldItemPoseSettings settings, double elapsed, float destination) =>
            Mathf.Lerp(startFrameWeight, destination, Mathf.SmoothStep(0f, 1f, ReturnProgress(settings, elapsed)));

        internal void Sample(Pose liveLeft, Pose liveRight, bool available, bool unavailable, in HeldItemBodyFrame body,
            in HeldItemPoseSettings settings, int environmentMask, double elapsed, Pose destinationLeft, Pose destinationRight,
            float destinationLeftWeight = 0f, float destinationRightWeight = 0f, float destinationFrameWeight = 0f)
        {
            double followEnd = Mathf.Max(0f, settings.MaximumFollowDuration);
            double pauseEnd = followEnd + Mathf.Max(0f, settings.EndPosePauseDuration);
            Left = HeavyItemPoseCalculation.ToWorld(retainedLeft, body);
            Right = HeavyItemPoseCalculation.ToWorld(retainedRight, body);
            if (Following && (unavailable || elapsed >= followEnd)) Following = false;
            if (Following && available)
            {
                float t = LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01((float)elapsed / 0.08f));
                var left = HeavyItemPoseCalculation.Blend(HeavyItemPoseCalculation.ToWorld(startLeft, body), liveLeft, t);
                var right = HeavyItemPoseCalculation.Blend(HeavyItemPoseCalculation.ToWorld(startRight, body), liveRight, t);
                float reach = Reach(settings);
                var data = body.Measurements;
                Vector3 leftWrist = left.position - left.rotation * Quaternion.Inverse(data.LeftWristToPalmRotation) * (data.LeftWristToPalmPosition * body.Scale);
                Vector3 rightWrist = right.position - right.rotation * Quaternion.Inverse(data.RightWristToPalmRotation) * (data.RightWristToPalmPosition * body.Scale);
                Following = Vector3.Distance(leftWrist, body.LeftShoulder) <= body.LeftArmLength * reach &&
                    Vector3.Distance(rightWrist, body.Shoulder) <= body.ArmLength * reach &&
                    !Physics.Linecast(body.Shoulder, right.position, environmentMask, QueryTriggerInteraction.Ignore) &&
                    !Physics.Linecast(body.LeftShoulder, left.position, environmentMask, QueryTriggerInteraction.Ignore) &&
                    !Physics.Linecast(Right.position, right.position, environmentMask, QueryTriggerInteraction.Ignore) &&
                    !Physics.Linecast(Left.position, left.position, environmentMask, QueryTriggerInteraction.Ignore);
                if (Following)
                {
                    Left = left; Right = right;
                    retainedLeft = HeavyItemPoseCalculation.ToLocal(left, body);
                    retainedRight = HeavyItemPoseCalculation.ToLocal(right, body);
                }
            }
            Stage = elapsed < followEnd ? RecoveryStage.Follow : elapsed < pauseEnd ? RecoveryStage.Pause : RecoveryStage.Return;
            float progress = ReturnProgress(settings, elapsed);
            float blend = Mathf.SmoothStep(0f, 1f, progress);
            if (Stage == RecoveryStage.Return)
            {
                Left = HeavyItemPoseCalculation.Blend(Left, destinationLeft, blend);
                Right = HeavyItemPoseCalculation.Blend(Right, destinationRight, blend);
                if (progress >= 1f) Stage = RecoveryStage.Finished;
            }
            LeftWeight = Mathf.Lerp(startLeftWeight, destinationLeftWeight, blend);
            RightWeight = Mathf.Lerp(startRightWeight, destinationRightWeight, blend);
            FrameWeight = Frame(settings, elapsed, destinationFrameWeight);
        }

        internal static void Submit(AvatarHandTargets targets, AvatarHandSource source, Transform leftTarget, Transform rightTarget,
            Pose left, Pose right, float leftWeight, float rightWeight, float reach, AnimationClip fingers)
        {
            leftTarget.SetPositionAndRotation(left.position, left.rotation);
            rightTarget.SetPositionAndRotation(right.position, right.rotation);
            if (leftWeight > 0f) targets.Set(AvatarIKGoal.LeftHand, source, leftTarget, leftWeight, leftWeight, reach, fingers);
            else targets.Clear(AvatarIKGoal.LeftHand, source);
            if (rightWeight > 0f) targets.Set(AvatarIKGoal.RightHand, source, rightTarget, rightWeight, rightWeight, reach, fingers);
            else targets.Clear(AvatarIKGoal.RightHand, source);
        }

        internal static void Clear(AvatarHandTargets targets, AvatarHandSource source)
        {
            targets.Clear(AvatarIKGoal.LeftHand, source);
            targets.Clear(AvatarIKGoal.RightHand, source);
        }

        internal void Reset()
        {
            Releasing = Following = false;
            LeftWeight = RightWeight = FrameWeight = 0f;
            Stage = RecoveryStage.Finished;
        }
    }
}
