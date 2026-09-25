using System;
using UnityEngine;

namespace TwoBirds
{
    internal sealed class HandContactPresentation : IDisposable
    {
        private readonly Transform[] palms = new Transform[2], hints = new Transform[2];
        private readonly AvatarHandContact[] bound = new AvatarHandContact[2];
        private readonly float[] weights = new float[2], hintWeights = new float[2], blends = new float[2], reaches = new float[2];
        private readonly AnimationClip[] fingers = new AnimationClip[2];

        internal HandContactPresentation(Transform host)
        {
            for (int i = 0; i < 2; i++)
            {
                palms[i] = new GameObject(i == 0 ? "LeftContactPalm" : "RightContactPalm").transform;
                palms[i].SetParent(host, false);
                hints[i] = new GameObject(i == 0 ? "LeftContactHint" : "RightContactHint").transform;
                hints[i].SetParent(host, false);
            }
        }

        internal void Submit(AvatarHandTargets targets, AvatarHandContact left, AvatarHandContact right,
            AvatarId avatar, bool firstPerson, AnimationClip defaultFingers, float dt)
        {
            Submit(targets, 0, AvatarIKGoal.LeftHand, left, avatar, firstPerson, defaultFingers, dt);
            Submit(targets, 1, AvatarIKGoal.RightHand, right, avatar, firstPerson, defaultFingers, dt);
        }

        private void Submit(AvatarHandTargets targets, int index, AvatarIKGoal hand, AvatarHandContact contact,
            AvatarId avatar, bool firstPerson, AnimationClip defaultFingers, float dt)
        {
            if (contact && contact != bound[index])
            {
                blends[index] = contact.BlendTime; reaches[index] = contact.MaximumReach;
                fingers[index] = contact.Fingers ? contact.Fingers : defaultFingers;
            }
            bound[index] = contact;
            if (contact)
            {
                var palm = contact.Palm(avatar, firstPerson);
                palms[index].SetPositionAndRotation(palm.position, palm.rotation);
                bool hinted = contact.TryHint(avatar, firstPerson, out var hint);
                if (hinted) hints[index].position = hint;
                hintWeights[index] = hinted ? 1f : 0f;
            }
            weights[index] = blends[index] <= 0f ? contact ? 1f : 0f :
                Mathf.MoveTowards(weights[index], contact ? 1f : 0f, dt / blends[index]);
            if (weights[index] <= 0f && !contact) { targets.Clear(hand, AvatarHandSource.Contact); return; }
            targets.Set(hand, AvatarHandSource.Contact, palms[index], weights[index], weights[index], reaches[index], fingers[index],
                hint: hints[index], hintWeight: hintWeights[index]);
        }

        internal void Clear(AvatarHandTargets targets)
        {
            targets.Clear(AvatarIKGoal.LeftHand, AvatarHandSource.Contact);
            targets.Clear(AvatarIKGoal.RightHand, AvatarHandSource.Contact);
            Array.Clear(weights, 0, 2); Array.Clear(bound, 0, 2);
        }

        public void Dispose()
        {
            foreach (var palm in palms) if (palm) UnityEngine.Object.Destroy(palm.gameObject);
            foreach (var hint in hints) if (hint) UnityEngine.Object.Destroy(hint.gameObject);
        }
    }
}
