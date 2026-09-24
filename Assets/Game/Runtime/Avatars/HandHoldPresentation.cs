using UnityEngine;

namespace TwoBirds
{
    internal sealed class HandHoldPresentation
    {
        internal enum RecoveryStage : byte { Follow, Pause, Return, Finished }
        internal Pose Left { get; private set; }
        internal Pose Right { get; private set; }
        internal float LeftWeight { get; private set; }
        internal float RightWeight { get; private set; }
        internal RecoveryStage Stage { get; private set; }
        internal bool Following { get; private set; }
        internal bool Releasing { get; private set; }
        private Pose startLeft, startRight, retainedLeft, retainedRight;
        private float frameWeight, startLeftWeight, startRightWeight, startFrameWeight;
        private double returnStart;
        private bool twoHanded;

        internal static float Reach(in HoldModePoses poses) => Mathf.Clamp(poses.FollowReachFraction, 0.01f, 0.85f);

        internal void Hold(Pose left, Pose right)
        {
            Left = left; Right = right;
            LeftWeight = RightWeight = frameWeight = 1f;
            Releasing = false;
        }

        internal void BeginRelease(Pose left, Pose right, in HeldItemBodyFrame body, bool twoHanded = true)
        {
            Hold(left, right);
            this.twoHanded = twoHanded;
            LeftWeight = startLeftWeight = twoHanded ? 1f : 0f;
            startLeft = retainedLeft = body.CenterToLocal(left);
            startRight = retainedRight = body.CenterToLocal(right);
            startRightWeight = startFrameWeight = 1f;
            returnStart = -1d;
            Stage = RecoveryStage.Follow;
            Following = Releasing = true;
        }

        internal void Retarget(in HeldItemBodyFrame body, double elapsed)
        {
            if (Stage != RecoveryStage.Return) return;
            retainedLeft = body.CenterToLocal(Left);
            retainedRight = body.CenterToLocal(Right);
            startLeftWeight = LeftWeight; startRightWeight = RightWeight; startFrameWeight = frameWeight;
            returnStart = elapsed;
        }

        internal float ReturnProgress(in HoldModePoses poses, double elapsed)
        {
            double pause = Mathf.Max(0f, poses.MaximumFollowDuration) + Mathf.Max(0f, poses.EndPosePauseDuration);
            double start = System.Math.Max(pause, returnStart);
            double end = pause + Mathf.Max(0f, poses.ReturnBlendDuration);
            return elapsed < start ? 0f : end <= start ? 1f : Mathf.Clamp01((float)((elapsed - start) / (end - start)));
        }

        internal float Frame(in HoldModePoses poses, double elapsed, float destination) =>
            Mathf.Lerp(startFrameWeight, destination, Mathf.SmoothStep(0f, 1f, ReturnProgress(poses, elapsed)));

        internal void Sample(Pose liveLeft, Pose liveRight, bool available, bool unavailable, in HeldItemBodyFrame body,
            in HoldModePoses settings, int environmentMask, double elapsed, Pose? destinationLeft, Pose? destinationRight,
            float destinationLeftWeight = 0f, float destinationRightWeight = 0f, float destinationFrameWeight = 0f)
        {
            double followEnd = Mathf.Max(0f, settings.MaximumFollowDuration);
            double pauseEnd = followEnd + Mathf.Max(0f, settings.EndPosePauseDuration);
            Left = body.CenterToWorld(retainedLeft);
            Right = body.CenterToWorld(retainedRight);
            if (Following && (unavailable || elapsed >= followEnd)) Following = false;
            if (Following && available)
            {
                float t = LeanTween.easeOutSine(0f, 1f, Mathf.Clamp01((float)elapsed / 0.08f));
                var left = Blend(body.CenterToWorld(startLeft), liveLeft, t);
                var right = Blend(body.CenterToWorld(startRight), liveRight, t);
                float reach = Reach(settings);
                var data = body.Measurements;
                Vector3 leftWrist = left.position - left.rotation * Quaternion.Inverse(data.LeftWristToPalmRotation) * (data.LeftWristToPalmPosition * body.Scale);
                Vector3 rightWrist = right.position - right.rotation * Quaternion.Inverse(data.RightWristToPalmRotation) * (data.RightWristToPalmPosition * body.Scale);
                Following = Vector3.Distance(rightWrist, body.Shoulder) <= body.ArmLength * reach &&
                    !Physics.Linecast(body.Shoulder, right.position, environmentMask, QueryTriggerInteraction.Ignore) &&
                    !Physics.Linecast(Right.position, right.position, environmentMask, QueryTriggerInteraction.Ignore) &&
                    (!twoHanded || Vector3.Distance(leftWrist, body.LeftShoulder) <= body.LeftArmLength * reach &&
                        !Physics.Linecast(body.LeftShoulder, left.position, environmentMask, QueryTriggerInteraction.Ignore) &&
                        !Physics.Linecast(Left.position, left.position, environmentMask, QueryTriggerInteraction.Ignore));
                if (Following)
                {
                    Left = left; Right = right;
                    retainedLeft = body.CenterToLocal(left);
                    retainedRight = body.CenterToLocal(right);
                }
            }
            Stage = elapsed < followEnd ? RecoveryStage.Follow : elapsed < pauseEnd ? RecoveryStage.Pause : RecoveryStage.Return;
            float progress = ReturnProgress(settings, elapsed);
            float blend = Mathf.SmoothStep(0f, 1f, progress);
            if (Stage == RecoveryStage.Return)
            {
                if (destinationLeft.HasValue) Left = Blend(Left, destinationLeft.Value, blend);
                if (destinationRight.HasValue) Right = Blend(Right, destinationRight.Value, blend);
                if (progress >= 1f) Stage = RecoveryStage.Finished;
            }
            LeftWeight = Mathf.Lerp(startLeftWeight, destinationLeftWeight, blend);
            RightWeight = Mathf.Lerp(startRightWeight, destinationRightWeight, blend);
            frameWeight = Frame(settings, elapsed, destinationFrameWeight);
        }

        private static Pose Blend(Pose from, Pose to, float t) =>
            new(Vector3.Lerp(from.position, to.position, t), Quaternion.Slerp(from.rotation, to.rotation, t));

        internal static void Submit(AvatarHandTargets targets, AvatarHandSource source, Transform leftTarget, Transform rightTarget,
            Pose left, Pose right, float leftWeight, float rightWeight, float reach, AnimationClip fingers, bool bodyRelative)
        {
            leftTarget.SetPositionAndRotation(left.position, left.rotation);
            rightTarget.SetPositionAndRotation(right.position, right.rotation);
            if (leftWeight > 0f) targets.Set(AvatarIKGoal.LeftHand, source, leftTarget, leftWeight, leftWeight, reach, fingers, bodyRelative: bodyRelative);
            else targets.Clear(AvatarIKGoal.LeftHand, source);
            if (rightWeight > 0f) targets.Set(AvatarIKGoal.RightHand, source, rightTarget, rightWeight, rightWeight, reach, fingers, bodyRelative: bodyRelative);
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
            LeftWeight = RightWeight = frameWeight = 0f;
            returnStart = -1d;
            Stage = RecoveryStage.Finished;
        }
    }
}
