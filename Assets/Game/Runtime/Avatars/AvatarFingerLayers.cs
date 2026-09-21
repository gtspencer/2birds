using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace TwoBirds
{
    internal sealed class AvatarFingerLayers : IDisposable
    {
        private sealed class Clip
        {
            internal AnimationClip Asset;
            internal AnimationClipPlayable Node;
            internal float Weight, StartWeight;
        }
        private sealed class Hand
        {
            internal AvatarMask Mask;
            internal AnimationMixerPlayable Mixer;
            internal readonly List<Clip> Clips = new();
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
                var hand = hands[i] = new Hand { Mask = new AvatarMask(), Mixer = AnimationMixerPlayable.Create(graph, 1) };
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
                if (hand.OpenNode.IsValid()) { hand.Mixer.DisconnectInput(0); graph.DestroyPlayable(hand.OpenNode); }
                hand.OpenNode = AnimationClipPlayable.Create(graph, open);
                hand.OpenNode.SetApplyPlayableIK(false); hand.OpenNode.SetApplyFootIK(false); hand.OpenNode.SetSpeed(0);
                graph.Connect(hand.OpenNode, 0, hand.Mixer, 0);
            }
            if (hand.Selected == clip) return;
            bool initial = !hand.Selected;
            hand.Selected = clip;
            if (!clip) { Output.SetInputWeight(right ? 2 : 1, 0f); return; }
            int index = hand.Clips.FindIndex(value => value.Asset == clip);
            if (index < 0)
            {
                index = hand.Clips.FindIndex(value => value.Weight <= 0f);
                if (index < 0)
                {
                    index = hand.Clips.Count;
                    hand.Clips.Add(new Clip());
                    hand.Mixer.SetInputCount(hand.Clips.Count + 1);
                }
                var slot = hand.Clips[index];
                if (slot.Node.IsValid())
                {
                    hand.Mixer.DisconnectInput(index + 1);
                    graph.DestroyPlayable(slot.Node);
                }
                slot.Asset = clip;
                slot.Node = AnimationClipPlayable.Create(graph, clip);
                slot.Node.SetApplyPlayableIK(false); slot.Node.SetApplyFootIK(false); slot.Node.SetSpeed(0); slot.Node.SetTime(0);
                graph.Connect(slot.Node, 0, hand.Mixer, index + 1);
            }
            hand.Active = index;
            foreach (var slot in hand.Clips) slot.StartWeight = initial ? 0f : slot.Weight;
            hand.Blend = initial ? 1f : 0f;
            Output.SetInputWeight(right ? 2 : 1, 1f);
        }

        internal void Advance(float dt, float duration = 0.12f)
        {
            foreach (var hand in hands)
            {
                hand.Blend = duration <= 0f ? 1f : Mathf.MoveTowards(hand.Blend, 1f, dt / duration);
                hand.OpenWeight = Mathf.Lerp(hand.OpenWeight, hand.DesiredOpen, AvatarPresentation.Smooth(dt, 0.1f));
                for (int i = 0; i < hand.Clips.Count; i++)
                {
                    var slot = hand.Clips[i];
                    slot.Weight = Mathf.Lerp(slot.StartWeight, i == hand.Active ? 1f : 0f, hand.Blend);
                    hand.Mixer.SetInputWeight(i + 1, slot.Weight * (1f - hand.OpenWeight));
                }
                hand.Mixer.SetInputWeight(0, hand.OpenWeight);
            }
        }

        public void Dispose()
        {
            foreach (var hand in hands) if (hand.Mask) UnityEngine.Object.Destroy(hand.Mask);
        }
    }
}
