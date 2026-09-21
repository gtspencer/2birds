using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace TwoBirds
{
    [DisallowMultipleComponent, RequireComponent(typeof(Animator))]
    public sealed class LocalFirstPersonHands : MonoBehaviour
    {
        private Animator animator;
        private PlayableGraph graph;
        private AvatarFingerLayers fingers;
        private AvatarHandIK ik;
        private AvatarHandTargets targets;
        private Renderer[] renderers;
        private bool[] rendererStates;
        private Vector3 shoulderCenter;
        private float deltaTime;
        private bool evaluating, applied;
        internal AvatarBinding Binding { get; private set; }
        internal HeldItemBodyFrame BodyFrame => new(Binding.GetBone(HumanBodyBones.RightUpperArm).position,
            transform.rotation, Binding.Measurements, Binding.Scale);

        internal void Initialize(AvatarRegistry.Entry entry, AvatarAnimationSet clips, AvatarHandTargets targets, ulong generation)
        {
            AvatarContentValidation.Validate(gameObject, entry.Settings, true);
            this.targets = targets;
            animator = GetComponent<Animator>();
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false; animator.fireEvents = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = true;
            transform.localScale = Vector3.one * entry.Settings.Scale;
            var bones = new Transform[(int)HumanBodyBones.LastBone];
            for (int i = 0; i < bones.Length; i++) bones[i] = animator.GetBoneTransform((HumanBodyBones)i);
            Binding = new AvatarBinding(entry.Id, entry.Settings, animator, bones, generation, true);
            renderers = GetComponentsInChildren<Renderer>(true);
            rendererStates = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) { rendererStates[i] = renderers[i].enabled; renderers[i].enabled = false; }
            graph = PlayableGraph.Create("Local hands");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var basis = AnimationClipPlayable.Create(graph, clips.Idle);
            basis.SetSpeed(0); basis.SetTime(0); basis.SetApplyPlayableIK(true); basis.SetApplyFootIK(false);
            fingers = new AvatarFingerLayers(graph, basis);
            var output = AnimationPlayableOutput.Create(graph, "Hands", animator);
            output.SetSourcePlayable(fingers.Output);
            graph.Play(); graph.Evaluate(0f);
            shoulderCenter = transform.InverseTransformPoint((bones[(int)HumanBodyBones.LeftUpperArm].position +
                bones[(int)HumanBodyBones.RightUpperArm].position) * 0.5f);
            ik = new AvatarHandIK(Binding);
        }

        internal void Place(Pose frame, Vector3 offset)
        {
            transform.SetPositionAndRotation(frame.position + frame.rotation *
                (offset + Binding.Settings.FirstPersonPlacementOffset - shoulderCenter * Binding.Scale), frame.rotation);
        }

        internal void Evaluate(float dt, AvatarAnimationSet clips)
        {
            var left = targets.Resolve(AvatarIKGoal.LeftHand); var right = targets.Resolve(AvatarIKGoal.RightHand);
            fingers.Select(false, left.Fingers ? left.Fingers : clips.RelaxedFingers, clips.OpenFingers, left.OpenWeight);
            fingers.Select(true, right.Fingers ? right.Fingers : clips.RelaxedFingers, clips.OpenFingers, right.OpenWeight);
            fingers.Advance(dt);
            deltaTime = dt; applied = false; evaluating = true;
            try { graph.Evaluate(0f); }
            finally { evaluating = false; }
        }

        private void OnAnimatorIK(int layer)
        {
            if (!evaluating || applied || layer != 0) return;
            applied = true; ik.Apply(targets, deltaTime);
        }

        internal void SetVisible(bool visible)
        {
            for (int i = 0; i < renderers.Length; i++) if (renderers[i]) renderers[i].enabled = visible && rendererStates[i];
        }
        private void OnDestroy()
        {
            fingers?.Dispose();
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
