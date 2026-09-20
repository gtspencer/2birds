using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace TwoBirds
{
    internal enum AvatarPose { Locomotion, Jump, Fall, Seated, Count }

    internal sealed class AvatarAnimationState
    {
        internal readonly float[] Direction = { 1f, 0f, 0f, 0f };
        internal readonly float[] Weights = new float[(int)AvatarPose.Count];
        private readonly float[] from = new float[(int)AvatarPose.Count];
        internal float Phase, IdleTime, AirTime, FallTime, SeatedTime, Speed, Motion, Run;
        private float transition, duration, previousVertical;
        private int landingFrames;
        private bool initialized, attached;
        private AvatarPose pose;

        internal AvatarAnimationState() => Weights[(int)AvatarPose.Locomotion] = 1f;
        internal void Seed(float seed) { Phase = seed; IdleTime = seed; }
        internal void Advance(in AvatarPresentationInput input, float bodyYaw, AvatarSettings settings,
            AvatarAnimationSet clips, float dt)
        {
            float alpha = AvatarPresentation.Smooth(dt, 0.06f);
            Vector3 velocity = Quaternion.Euler(0f, -bodyYaw, 0f) * input.WorldVelocity;
            float speed = new Vector2(velocity.x, velocity.z).magnitude;
            bool constrained = input.Seated || input.Carried && !input.ReleasePreview || input.Pending;
            Speed = Mathf.Lerp(Speed, constrained ? 0f : speed, alpha);
            float sum = Mathf.Abs(velocity.x) + Mathf.Abs(velocity.z);
            if (sum > 0.001f)
            {
                Direction[0] = Mathf.Lerp(Direction[0], Mathf.Max(velocity.z, 0f) / sum, alpha);
                Direction[1] = Mathf.Lerp(Direction[1], Mathf.Max(-velocity.z, 0f) / sum, alpha);
                Direction[2] = Mathf.Lerp(Direction[2], Mathf.Max(-velocity.x, 0f) / sum, alpha);
                Direction[3] = Mathf.Lerp(Direction[3], Mathf.Max(velocity.x, 0f) / sum, alpha);
            }
            Motion = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.25f, Speed));
            Run = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(input.WalkSpeed, input.SprintSpeed, Speed));
            AvatarPose next = pose;
            bool grounded = input.Grounded && input.Mode != MovementMode.External && !input.ReleasePreview;
            if (!input.Pending)
            {
                if (input.Seated) next = AvatarPose.Seated;
                else if (input.Carried && !input.ReleasePreview) next = AvatarPose.Locomotion;
                else
                {
                    landingFrames = grounded && velocity.y <= 0.5f ? landingFrames + 1 : 0;
                    if (grounded && (!initialized || landingFrames >= 2)) next = AvatarPose.Locomotion;
                    else if (!grounded)
                    {
                        if (!initialized || pose == AvatarPose.Locomotion || pose == AvatarPose.Seated ||
                            previousVertical <= 0f && velocity.y > 0.5f)
                        {
                            next = velocity.y > 0.5f ? AvatarPose.Jump : AvatarPose.Fall;
                            AirTime = !initialized ? clips.AscentDuration : 0f;
                        }
                        if (velocity.y < -0.1f) next = AvatarPose.Fall;
                    }
                }
            }
            if (!initialized)
            {
                for (int i = 0; i < Weights.Length; i++) Weights[i] = i == (int)next ? 1f : 0f;
                pose = next;
                initialized = true;
            }
            if (next != pose || constrained != attached)
            {
                Array.Copy(Weights, from, Weights.Length);
                duration = constrained != attached || next == AvatarPose.Seated || pose == AvatarPose.Seated ? 0.18f :
                    next == AvatarPose.Locomotion ? 0.15f : pose == AvatarPose.Jump && next == AvatarPose.Fall ? 0.12f : 0.10f;
                transition = 0f;
                pose = next;
            }
            if (duration > 0f)
            {
                transition = Mathf.Min(transition + dt, duration);
                float t = Mathf.SmoothStep(0f, 1f, transition / duration);
                for (int i = 0; i < Weights.Length; i++) Weights[i] = Mathf.Lerp(from[i], i == (int)pose ? 1f : 0f, t);
            }
            attached = constrained;
            previousVertical = velocity.y;
            IdleTime = Mathf.Repeat(IdleTime + dt, clips.Idle.length);
            SeatedTime = Mathf.Repeat(SeatedTime + dt, clips.Seated.length);
            FallTime = Mathf.Repeat(FallTime + dt, clips.Fall.length);
            if (pose == AvatarPose.Jump) AirTime = Mathf.Min(AirTime + dt, clips.AscentDuration);
            float cycles = 0f;
            float minimumCycles = 0f, maximumCycles = float.PositiveInfinity;
            for (int i = 0; i < 8; i++)
            {
                var slot = clips.GetLocomotion(i);
                float weight = Direction[i % 4] * (i < 4 ? 1f - Run : Run);
                if (weight <= 0f) continue;
                float nominal = slot.NominalSpeed * settings.Generated.HumanScale * settings.Scale / slot.ReferenceHumanScale;
                float ratio = Mathf.Clamp(Speed / nominal * settings.PlaybackMultiplier, AvatarAnimationSet.MinimumPlayback, AvatarAnimationSet.MaximumPlayback);
                cycles += weight * ratio / slot.Clip.length;
                minimumCycles = Mathf.Max(minimumCycles, AvatarAnimationSet.MinimumPlayback / slot.Clip.length);
                maximumCycles = Mathf.Min(maximumCycles, AvatarAnimationSet.MaximumPlayback / slot.Clip.length);
            }
            if (Motion > 0f) Phase = Mathf.Repeat(Phase + Mathf.Clamp(cycles * Motion, minimumCycles, maximumCycles) * dt, 1f);
        }
    }

    internal sealed class AvatarAnimationGraph : IDisposable
    {
        private PlayableGraph graph;
        private readonly AnimationClipPlayable[] locomotion = new AnimationClipPlayable[8];
        private AnimationClipPlayable idle, jump, fall, seated;
        private AnimationMixerPlayable walk, run, gait, motion, states;
        private readonly AvatarAnimationSet clips;

        internal AvatarAnimationGraph(Animator animator, AvatarAnimationSet clips)
        {
            this.clips = clips;
            try
            {
                graph = PlayableGraph.Create("Avatar presentation");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                walk = AnimationMixerPlayable.Create(graph, 4);
                run = AnimationMixerPlayable.Create(graph, 4);
                for (int i = 0; i < 8; i++)
                {
                    locomotion[i] = Create(clips.GetLocomotion(i).Clip);
                    graph.Connect(locomotion[i], 0, i < 4 ? walk : run, i % 4);
                }
                idle = Create(clips.Idle); jump = Create(clips.Jump);
                fall = Create(clips.Fall); seated = Create(clips.Seated);
                gait = AnimationMixerPlayable.Create(graph, 2);
                graph.Connect(walk, 0, gait, 0); graph.Connect(run, 0, gait, 1);
                motion = AnimationMixerPlayable.Create(graph, 2);
                graph.Connect(idle, 0, motion, 0); graph.Connect(gait, 0, motion, 1);
                states = AnimationMixerPlayable.Create(graph, (int)AvatarPose.Count);
                graph.Connect(motion, 0, states, (int)AvatarPose.Locomotion); graph.Connect(jump, 0, states, (int)AvatarPose.Jump);
                graph.Connect(fall, 0, states, (int)AvatarPose.Fall); graph.Connect(seated, 0, states, (int)AvatarPose.Seated);
                var layers = AnimationLayerMixerPlayable.Create(graph, 1);
                graph.Connect(states, 0, layers, 0);
                layers.SetInputWeight(0, 1f);
                var output = AnimationPlayableOutput.Create(graph, "Humanoid", animator);
                output.SetSourcePlayable(layers);
                graph.Play();
            }
            catch { Dispose(); throw; }
        }

        private AnimationClipPlayable Create(AnimationClip clip)
        {
            var node = AnimationClipPlayable.Create(graph, clip);
            node.SetApplyPlayableIK(true);
            node.SetApplyFootIK(false);
            node.SetSpeed(0);
            return node;
        }

        internal void SampleSeated()
        {
            for (int i = 0; i < (int)AvatarPose.Count; i++) states.SetInputWeight(i, i == (int)AvatarPose.Seated ? 1f : 0f);
            seated.SetTime(0);
            graph.Evaluate(0f);
        }

        internal void Evaluate(AvatarAnimationState state)
        {
            for (int i = 0; i < 8; i++)
            {
                var slot = clips.GetLocomotion(i);
                locomotion[i].SetTime(Mathf.Repeat(state.Phase + slot.CycleOffset, 1f) * slot.Clip.length);
                (i < 4 ? walk : run).SetInputWeight(i % 4, state.Direction[i % 4]);
            }
            gait.SetInputWeight(0, 1f - state.Run); gait.SetInputWeight(1, state.Run);
            motion.SetInputWeight(0, 1f - state.Motion); motion.SetInputWeight(1, state.Motion);
            idle.SetTime(state.IdleTime);
            jump.SetTime(Mathf.Lerp(clips.AscentStart, clips.AscentEnd, state.AirTime / clips.AscentDuration) * clips.Jump.length);
            fall.SetTime(state.FallTime);
            seated.SetTime(state.SeatedTime);
            for (int i = 0; i < state.Weights.Length; i++) states.SetInputWeight(i, state.Weights[i]);
            graph.Evaluate(0f);
        }

        public void Dispose() { if (graph.IsValid()) graph.Destroy(); }
    }
}
