#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TwoBirds
{
    internal sealed class GripGhostHand : IDisposable
    {
        private readonly GameObject holder, root;
        private readonly Transform hand, hips;
        private readonly AvatarSettings settings;
        private readonly bool right;
        internal AvatarId Avatar { get; }
        internal AnimationClip Fingers { get; }

        internal GripGhostHand(AvatarRegistry.Entry entry, AvatarAnimationSet clips, AnimationClip fingers, bool right, int layer, Material material)
        {
            Avatar = entry.Id; Fingers = fingers; settings = entry.Settings; this.right = right;
            holder = new GameObject($"Ghost {(right ? "right" : "left")} hand source");
            holder.SetActive(false);
            var instance = Object.Instantiate(entry.Prefab, holder.transform);
            StripBehaviours(instance);
            instance.transform.localScale = Vector3.one * settings.Scale;
            var animator = instance.GetComponent<Animator>();
            animator.runtimeAnimatorController = null; animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.enabled = true;
            holder.SetActive(true);
            var graph = PlayableGraph.Create("Ghost hand");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var basis = AnimationClipPlayable.Create(graph, clips.Idle);
                basis.SetSpeed(0); basis.SetTime(0); basis.SetApplyPlayableIK(false); basis.SetApplyFootIK(false);
                var layers = new AvatarFingerLayers(graph, basis);
                layers.Select(right, fingers); layers.Advance(0f, 0f);
                AnimationPlayableOutput.Create(graph, "Ghost hand", animator).SetSourcePlayable(layers.Output);
                graph.Play(); graph.Evaluate(0f);
                layers.Dispose();
            }
            finally { graph.Destroy(); }
            hand = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            root = new GameObject($"Ghost {(right ? "right" : "left")} hand");
            hand.SetParent(root.transform, true);
            animator.enabled = false;
            hips.localScale = Vector3.zero;
            foreach (var target in new[] { holder, root })
            {
                foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMaterials.Length).ToArray();
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    if (renderer is SkinnedMeshRenderer skinned) skinned.updateWhenOffscreen = true;
                }
                foreach (var child in target.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = layer;
            }
        }

        private static void StripBehaviours(GameObject instance)
        {
            var remaining = new List<Component>();
            foreach (var component in instance.GetComponentsInChildren<Component>(true))
                if (component is MonoBehaviour or Joint or Collider or Rigidbody) remaining.Add(component);
            for (bool removed = true; removed && remaining.Count > 0;)
            {
                removed = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var component = remaining[i];
                    if (!component) { remaining.RemoveAt(i); continue; }
                    if (remaining.Exists(other => other != component && Requires(other, component))) continue;
                    Object.DestroyImmediate(component); remaining.RemoveAt(i); removed = true;
                }
            }
        }

        private static bool Requires(Component owner, Component required) =>
            owner && owner.gameObject == required.gameObject &&
            owner.GetType().GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>().Any(attribute =>
                attribute.m_Type0 != null && attribute.m_Type0.IsInstanceOfType(required) ||
                attribute.m_Type1 != null && attribute.m_Type1.IsInstanceOfType(required) ||
                attribute.m_Type2 != null && attribute.m_Type2.IsInstanceOfType(required));

        internal void Place(Pose palm)
        {
            var data = AvatarPalmCalibration.Measurements(settings);
            Quaternion wrist = palm.rotation * Quaternion.Inverse(right ? data.RightWristToPalmRotation : data.LeftWristToPalmRotation);
            hand.SetPositionAndRotation(palm.position - wrist * ((right ? data.RightWristToPalmPosition : data.LeftWristToPalmPosition) * settings.Scale), wrist);
            hips.position = hand.position;
        }

        internal void SetVisible(bool visible)
        {
            if (holder.activeSelf != visible) holder.SetActive(visible);
            if (root.activeSelf != visible) root.SetActive(visible);
        }

        public void Dispose()
        {
            if (root) Object.Destroy(root);
            if (holder) Object.Destroy(holder);
        }
    }
}
#endif
