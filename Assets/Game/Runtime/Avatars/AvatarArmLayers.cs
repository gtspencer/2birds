using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace TwoBirds
{
    internal struct AvatarArmPose
    {
        internal const int Slots = 3;
        internal HoldClass A, B, C;
        internal float WeightA, WeightB, WeightC;
        internal HoldClass ChargeClass;
        internal float Charge, Pitch;
    }

    internal sealed class AvatarArmLayers : IDisposable
    {
        private sealed class Arm
        {
            internal AvatarMask Mask;
            internal AnimationMixerPlayable Mixer;
            internal readonly AnimationClip[] Clips = new AnimationClip[AvatarArmPose.Slots * 2];
            internal readonly AnimationClipPlayable[] Nodes = new AnimationClipPlayable[AvatarArmPose.Slots * 2];
        }
        private readonly PlayableGraph graph;
        private readonly bool firstPerson;
        private readonly Arm[] arms = new Arm[2];
        private readonly HoldClass[] classes = new HoldClass[AvatarArmPose.Slots];
        internal AnimationLayerMixerPlayable Output { get; }
        internal float Weight(bool right) => Output.GetInputWeight(right ? 2 : 1);

        internal AvatarArmLayers(PlayableGraph graph, Playable basis, bool firstPerson)
        {
            this.graph = graph; this.firstPerson = firstPerson;
            Output = AnimationLayerMixerPlayable.Create(graph, 3);
            graph.Connect(basis, 0, Output, 0);
            Output.SetInputWeight(0, 1f);
            for (int i = 0; i < 2; i++)
            {
                var arm = arms[i] = new Arm { Mask = new AvatarMask(), Mixer = AnimationMixerPlayable.Create(graph, AvatarArmPose.Slots * 2) };
                for (int part = 0; part < (int)AvatarMaskBodyPart.LastBodyPart; part++)
                    arm.Mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)part,
                        part == (int)(i == 0 ? AvatarMaskBodyPart.LeftArm : AvatarMaskBodyPart.RightArm));
                graph.Connect(arm.Mixer, 0, Output, i + 1);
                Output.SetLayerMaskFromAvatarMask((uint)(i + 1), arm.Mask);
                Output.SetInputWeight(i + 1, 0f);
            }
        }

        internal void Set(in AvatarArmPose pose)
        {
            Assign(0, pose.A); Assign(1, pose.B); Assign(2, pose.C);
            for (int i = 0; i < 2; i++)
            {
                var arm = arms[i];
                float total = 0f;
                for (int slot = 0; slot < AvatarArmPose.Slots; slot++)
                    if (arm.Clips[slot * 2]) total += Weight(pose, slot);
                Output.SetInputWeight(i + 1, Mathf.Clamp01(total));
                for (int slot = 0; slot < AvatarArmPose.Slots; slot++)
                {
                    float share = total > 0f && arm.Clips[slot * 2] ? Weight(pose, slot) / total : 0f;
                    float charge = arm.Clips[slot * 2 + 1] && classes[slot] == pose.ChargeClass ? Mathf.Clamp01(pose.Charge) : 0f;
                    arm.Mixer.SetInputWeight(slot * 2, share * (1f - charge));
                    arm.Mixer.SetInputWeight(slot * 2 + 1, share * charge);
                }
            }
        }

        private static float Weight(in AvatarArmPose pose, int slot) => slot switch { 0 => pose.WeightA, 1 => pose.WeightB, _ => pose.WeightC };

        private void Assign(int slot, HoldClass value)
        {
            var previous = classes[slot];
            if (previous == value) return;
            classes[slot] = value;
            if (previous && Array.IndexOf(classes, previous) < 0) previous.ContentChanged -= Rebind;
            if (value && !HeldElsewhere(value, slot)) value.ContentChanged += Rebind;
            Bind(slot, false);
        }

        private bool HeldElsewhere(HoldClass value, int slot)
        {
            for (int i = 0; i < AvatarArmPose.Slots; i++) if (i != slot && classes[i] == value) return true;
            return false;
        }

        private void Rebind() { for (int slot = 0; slot < AvatarArmPose.Slots; slot++) Bind(slot, true); }

        private void Bind(int slot, bool force)
        {
            var owner = classes[slot];
            var view = owner ? owner.View(firstPerson) : default;
            for (int i = 0; i < 2; i++)
            {
                bool used = owner && (i == 1 || owner.Mode != ItemHoldMode.OneHand);
                Bind(arms[i], slot * 2, used ? view.Hold : null, force);
                Bind(arms[i], slot * 2 + 1, used ? view.Charged : null, force);
            }
        }

        private void Bind(Arm arm, int input, AnimationClip clip, bool force)
        {
            if (!force && arm.Clips[input] == clip) return;
            if (arm.Nodes[input].IsValid()) { arm.Mixer.DisconnectInput(input); graph.DestroyPlayable(arm.Nodes[input]); }
            arm.Clips[input] = clip; arm.Nodes[input] = default;
            if (!clip) return;
            var node = arm.Nodes[input] = AnimationClipPlayable.Create(graph, clip);
            node.SetApplyPlayableIK(false); node.SetApplyFootIK(false); node.SetSpeed(0); node.SetTime(0);
            graph.Connect(node, 0, arm.Mixer, input);
        }

        public void Dispose()
        {
            for (int slot = 0; slot < AvatarArmPose.Slots; slot++)
                if (classes[slot] && Array.IndexOf(classes, classes[slot]) == slot) classes[slot].ContentChanged -= Rebind;
            foreach (var arm in arms) if (arm?.Mask) UnityEngine.Object.Destroy(arm.Mask);
        }
    }
}
