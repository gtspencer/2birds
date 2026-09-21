using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace TwoBirds
{
    internal sealed class AvatarFingerLayers : IDisposable
    {
        private sealed class Hand
        {
            internal AvatarMask Mask;
            internal AnimationMixerPlayable Mixer;
            internal readonly AnimationClipPlayable[] Nodes = new AnimationClipPlayable[2];
            internal AnimationClip Selected, Open;
            internal AnimationClipPlayable OpenNode;
            internal float OpenWeight, DesiredOpen;
            internal int Active;
            internal float Blend = 1f;
        }
        private readonly PlayableGraph graph;
        private readonly Hand[] hands = new Hand[2];
        internal AnimationLayerMixerPlayable Output { get; }

        internal AvatarFingerLayers(PlayableGraph graph, Playable basis)
        {
            this.graph = graph;
            Output = AnimationLayerMixerPlayable.Create(graph, 3);
            graph.Connect(basis, 0, Output, 0);
            Output.SetInputWeight(0, 1f);
            for (int i = 0; i < 2; i++)
            {
                var hand = hands[i] = new Hand { Mask = new AvatarMask(), Mixer = AnimationMixerPlayable.Create(graph, 3) };
                for (int part = 0; part < (int)AvatarMaskBodyPart.LastBodyPart; part++)
                    hand.Mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)part,
                        part == (int)(i == 0 ? AvatarMaskBodyPart.LeftFingers : AvatarMaskBodyPart.RightFingers));
                graph.Connect(hand.Mixer, 0, Output, i + 1);
                Output.SetLayerMaskFromAvatarMask((uint)(i + 1), hand.Mask);
            }
        }

        internal void Select(bool right, AnimationClip clip, AnimationClip open = null, float openWeight = 0f)
        {
            var hand = hands[right ? 1 : 0];
            hand.DesiredOpen = open ? Mathf.Clamp01(openWeight) : 0f;
            if (open && hand.Open != open)
            {
                hand.Open = open;
                if (hand.OpenNode.IsValid()) { hand.Mixer.DisconnectInput(2); graph.DestroyPlayable(hand.OpenNode); }
                hand.OpenNode = AnimationClipPlayable.Create(graph, open);
                hand.OpenNode.SetApplyPlayableIK(false); hand.OpenNode.SetApplyFootIK(false); hand.OpenNode.SetSpeed(0);
                graph.Connect(hand.OpenNode, 0, hand.Mixer, 2);
            }
            if (hand.Selected == clip) return;
            bool initial = !hand.Selected;
            hand.Selected = clip;
            if (!clip) { Output.SetInputWeight(right ? 2 : 1, 0f); return; }
            hand.Active = 1 - hand.Active;
            int index = hand.Active;
            if (hand.Nodes[index].IsValid())
            {
                hand.Mixer.DisconnectInput(index);
                graph.DestroyPlayable(hand.Nodes[index]);
            }
            var node = AnimationClipPlayable.Create(graph, clip);
            node.SetApplyPlayableIK(false); node.SetApplyFootIK(false); node.SetSpeed(0); node.SetTime(0);
            hand.Nodes[index] = node;
            graph.Connect(node, 0, hand.Mixer, index);
            hand.Blend = initial ? 1f : 0f;
            Output.SetInputWeight(right ? 2 : 1, 1f);
        }

        internal void Advance(float dt, float duration = 0.12f)
        {
            foreach (var hand in hands)
            {
                hand.Blend = duration <= 0f ? 1f : Mathf.MoveTowards(hand.Blend, 1f, dt / duration);
                hand.OpenWeight = Mathf.Lerp(hand.OpenWeight, hand.DesiredOpen, AvatarPresentation.Smooth(dt, 0.1f));
                hand.Mixer.SetInputWeight(hand.Active, hand.Blend * (1f - hand.OpenWeight));
                hand.Mixer.SetInputWeight(1 - hand.Active, (1f - hand.Blend) * (1f - hand.OpenWeight));
                hand.Mixer.SetInputWeight(2, hand.OpenWeight);
            }
        }

        public void Dispose()
        {
            foreach (var hand in hands) if (hand.Mask) UnityEngine.Object.Destroy(hand.Mask);
        }
    }
}
