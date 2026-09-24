using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace TwoBirds
{
    internal struct AvatarArmPose
    {
        internal HeldItemSettings Settings;
        internal float OneHand, TwoHand, Slingshot, Charge, Pitch;
        internal ItemHoldMode ChargeMode;
        internal float Weight(ItemHoldMode mode) => mode switch
        { ItemHoldMode.TwoHand => TwoHand, ItemHoldMode.Slingshot => Slingshot, _ => OneHand };
    }

    internal sealed class AvatarArmLayers : IDisposable
    {
        private sealed class Arm
        {
            internal AvatarMask Mask;
            internal AnimationMixerPlayable Mixer;
            internal readonly AnimationClip[] Clips = new AnimationClip[6];
            internal readonly AnimationClipPlayable[] Nodes = new AnimationClipPlayable[6];
        }
        private readonly PlayableGraph graph;
        private readonly bool firstPerson;
        private readonly Arm[] arms = new Arm[2];
        private HeldItemSettings settings;
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
                var arm = arms[i] = new Arm { Mask = new AvatarMask(), Mixer = AnimationMixerPlayable.Create(graph, 6) };
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
            if (pose.Settings && pose.Settings != settings)
            {
                if (settings) settings.ContentChanged -= Rebind;
                settings = pose.Settings; settings.ContentChanged += Rebind;
                Rebind();
            }
            for (int i = 0; i < 2; i++)
            {
                var arm = arms[i];
                float total = 0f;
                for (int mode = 0; mode < 3; mode++)
                    if (arm.Clips[mode * 2]) total += pose.Weight((ItemHoldMode)mode);
                Output.SetInputWeight(i + 1, Mathf.Clamp01(total));
                for (int mode = 0; mode < 3; mode++)
                {
                    float share = total > 0f && arm.Clips[mode * 2] ? pose.Weight((ItemHoldMode)mode) / total : 0f;
                    float charge = arm.Clips[mode * 2 + 1] && (ItemHoldMode)mode == pose.ChargeMode ? Mathf.Clamp01(pose.Charge) : 0f;
                    arm.Mixer.SetInputWeight(mode * 2, share * (1f - charge));
                    arm.Mixer.SetInputWeight(mode * 2 + 1, share * charge);
                }
            }
        }

        private void Rebind()
        {
            for (int i = 0; i < 2; i++)
                for (int mode = 0; mode < 3; mode++)
                {
                    bool used = i == 1 || mode != (int)ItemHoldMode.OneHand;
                    var poses = settings.Poses((ItemHoldMode)mode);
                    Bind(arms[i], mode * 2, used ? poses.Hold(firstPerson) : null);
                    Bind(arms[i], mode * 2 + 1, used ? poses.Charged(firstPerson) : null);
                }
        }

        private void Bind(Arm arm, int slot, AnimationClip clip)
        {
            if (arm.Clips[slot] == clip) return;
            if (arm.Nodes[slot].IsValid()) { arm.Mixer.DisconnectInput(slot); graph.DestroyPlayable(arm.Nodes[slot]); }
            arm.Clips[slot] = clip; arm.Nodes[slot] = default;
            if (!clip) return;
            var node = arm.Nodes[slot] = AnimationClipPlayable.Create(graph, clip);
            node.SetApplyPlayableIK(false); node.SetApplyFootIK(false); node.SetSpeed(0); node.SetTime(0);
            graph.Connect(node, 0, arm.Mixer, slot);
        }

        public void Dispose()
        {
            if (settings) settings.ContentChanged -= Rebind;
            foreach (var arm in arms) if (arm?.Mask) UnityEngine.Object.Destroy(arm.Mask);
        }
    }
}
