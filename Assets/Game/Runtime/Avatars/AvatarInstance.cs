using System;
using UnityEngine;
using UniVRM10;

namespace TwoBirds
{
    [DisallowMultipleComponent, RequireComponent(typeof(Animator), typeof(Vrm10Instance))]
    public sealed class AvatarInstance : MonoBehaviour
    {
        private AvatarPresentation host;
        private Animator animator;
        private Vrm10Instance vrm;
        private Vrm10Runtime runtime;
        private AvatarAnimationGraph graph;
        private AvatarHumanoidIK ik;
        private Renderer[] renderers;
        private bool[] rendererStates;
        private bool released, springsRegistered, evaluating, resetRequested;
        private uint serial, ikSerial;
        private Vector3 seatedHips;
        private float scale;
        private HumanPoseHandler editorPoseHandler;
        private HumanPose editorPose;
        internal AvatarBinding Binding { get; private set; }
        internal AvatarSettings Settings { get; private set; }
        internal float NameAnchorOffset { get; private set; }
        internal Transform Head { get; private set; }
        internal bool Initialized { get; private set; }
        private bool physical;
        internal void SetPhysical(bool value)
        {
            physical = value;
            if (!Initialized) return;
            animator.enabled = !value;
            ApplyFeatures();
            if (!value) ResetMotion();
        }

        internal void Stage(AvatarPresentation owner, AvatarRegistry.Entry entry, ulong generation)
        {
            AvatarContentValidation.Validate(gameObject, entry.Settings);
            host = owner; Settings = entry.Settings;
            scale = Settings.Scale;
            NameAnchorOffset = (Settings.Generated.Height - Settings.Generated.Head.y) * scale + Settings.NamePanelOffset;
            animator = GetComponent<Animator>(); vrm = GetComponent<Vrm10Instance>();
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.fireEvents = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = false;
            vrm.UpdateType = Vrm10Instance.UpdateTypes.None;
            vrm.enabled = false;
            renderers = GetComponentsInChildren<Renderer>(true);
            rendererStates = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) { rendererStates[i] = renderers[i].enabled; renderers[i].enabled = false; }
            transform.localScale = Vector3.one * scale;
            var bones = new Transform[(int)HumanBodyBones.LastBone];
            for (int i = 0; i < bones.Length; i++) bones[i] = animator.GetBoneTransform((HumanBodyBones)i);
            Head = bones[(int)HumanBodyBones.Head];
            Binding = new AvatarBinding(entry.Id, Settings, animator, bones, generation);
        }

        internal void RefreshMeasurements()
        {
            if (!Initialized) return;
            Binding.RefreshMeasurements(); scale = Settings.Scale; transform.localScale = Vector3.one * scale;
            NameAnchorOffset = (Settings.Generated.Height - Settings.Generated.Head.y) * scale + Settings.NamePanelOffset;
            ik = new AvatarHumanoidIK(host, Binding);
        }

        internal void Initialize(AvatarAnimationSet clips)
        {
            Place(0f);
            runtime = vrm.Runtime;
            GetComponent<AvatarSpringRuntimeProvider>().Initialization.GetAwaiter().GetResult();
            springsRegistered = true;
            animator.enabled = true;
            graph = new AvatarAnimationGraph(animator, clips);
            if (host.EditorPreview)
            {
                editorPoseHandler = new HumanPoseHandler(animator.avatar, transform);
                editorPoseHandler.GetHumanPose(ref editorPose);
                Array.Clear(editorPose.muscles, 0, editorPose.muscles.Length);
                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    if (HumanTrait.MuscleName[i] is "Left Arm Down-Up" or "Right Arm Down-Up") editorPose.muscles[i] = -0.45f;
            }
            graph.SampleSeated();
            seatedHips = transform.InverseTransformPoint(Binding.GetBone(HumanBodyBones.Hips).position);
            ik = new AvatarHumanoidIK(host, Binding);
            if (editorPoseHandler != null) editorPoseHandler.SetHumanPose(ref editorPose);
            Initialized = true;
            ApplyFeatures();
        }

        private void Place(float seatedWeight)
        {
            transform.localRotation = Quaternion.Euler(0f, Settings.YawOffset, 0f);
            Vector3 standing = host.Input.SolePosition + host.transform.rotation *
                (Settings.StandingOffset + (host.Input.Carried ? Settings.CarriedOffset : Vector3.zero)) -
                transform.rotation * (Vector3.up * (Settings.Generated.SolePlane * scale));
            Vector3 sitting = host.Input.Facing.position + host.Input.Facing.rotation *
                AvatarDriverPose.SeatedOffset(Settings, host.Registry, host.Input.Seated && host.Input.Driver) -
                transform.rotation * (seatedHips * scale);
            transform.position = Vector3.Lerp(standing, sitting, seatedWeight);
        }

        internal void Evaluate(float dt, bool warmup = false)
        {
            if (Initialized && host.EditorPreview && !host.AnimationEnabled)
            {
                Place(0f);
                editorPoseHandler.SetHumanPose(ref editorPose);
                runtime.Process();
                return;
            }
            if (physical || !Initialized || !host.AnimationEnabled && !warmup) return;
            Place(host.State.Weights[(int)AvatarPose.Seated]);
            if (resetRequested)
            {
                if (springsRegistered) runtime.SpringBone.RestoreInitialTransform();
                resetRequested = false;
            }
            var clips = host.Registry.Animations;
            var leftHand = host.HandTargets.Resolve(AvatarIKGoal.LeftHand); var rightHand = host.HandTargets.Resolve(AvatarIKGoal.RightHand);
            graph.Fingers.Select(false, leftHand.Fingers ? leftHand.Fingers : clips.RelaxedFingers, clips.OpenFingers, leftHand.OpenWeight);
            graph.Fingers.Select(true, rightHand.Fingers ? rightHand.Fingers : clips.RelaxedFingers, clips.OpenFingers, rightHand.OpenWeight);
            graph.Fingers.Advance(dt);
            serial++;
            evaluating = true;
            ik.DeltaTime = dt;
            try { graph.Evaluate(host.State); }
            finally { evaluating = false; }
            if (host.EditorPreview && host.HeadLookEnabled) ApplyEditorHeadLook();
            using (AvatarPresentationSystem.VrmMarker.Auto())
            {
                if (host.HeadLookEnabled) runtime.LookAt.LookAtInput = new LookAtInput
                    { WorldPosition = host.EditorPreview ? host.EditorLookTarget : ik.LookTarget };
                runtime.Process();
            }
        }

        private void ApplyEditorHeadLook()
        {
            var local = transform.InverseTransformDirection(host.EditorLookTarget - Head.position);
            float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -45f, 45f);
            float pitch = Mathf.Clamp(-Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg, -25f, 25f);
            Head.rotation = transform.rotation * Quaternion.Euler(pitch, yaw, 0) * Quaternion.Inverse(transform.rotation) * Head.rotation;
        }

        internal void CorrectHands()
        {
            if (!physical && Initialized && host.AnimationEnabled) ik.CorrectHands();
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (!evaluating || layerIndex != 0 || ikSerial == serial) return;
            ikSerial = serial;
            using (AvatarPresentationSystem.IkMarker.Auto()) ik.Apply();
        }

        internal void ApplyFeatures()
        {
            if (!Initialized) return;
            ik.Reset();
            bool springs = !physical && host.AnimationEnabled && host.SpringsEnabled;
            if (springs != springsRegistered)
            {
                if (springs)
                {
                    if (!runtime.SpringBone.ReconstructSpringBone()) throw new InvalidOperationException("Native spring reconstruction failed.");
                }
                else
                {
                    if (!physical && host.AnimationEnabled) runtime.SpringBone.RestoreInitialTransform();
                    runtime.SpringBone.Dispose();
                }
                springsRegistered = springs;
            }
            if (!host.HeadLookEnabled) runtime.LookAt.SetYawPitchManually(0f, 0f);
        }

        internal void ResetMotion()
        {
            ik?.Reset();
            resetRequested = true;
        }

        internal void SetVisible(bool visible)
        {
            for (int i = 0; i < renderers.Length; i++) if (renderers[i]) renderers[i].enabled = visible && rendererStates[i];
        }

        internal void Release()
        {
            Dispose();
            gameObject.SetActive(false);
            Destroy(gameObject);
        }

        private void Dispose()
        {
            if (released) return;
            released = true;
            graph?.Dispose(); graph = null;
            editorPoseHandler?.Dispose(); editorPoseHandler = null;
            if (animator) animator.enabled = false;
            if (vrm) { vrm.enabled = false; vrm.DisposeRuntime(); }
            runtime = null; ik = null; springsRegistered = false;
        }
        private void OnDestroy() => Dispose();
    }
}
