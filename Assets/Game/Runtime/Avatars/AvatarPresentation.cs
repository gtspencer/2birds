using System;
using UnityEngine;

namespace TwoBirds
{
    public struct AvatarPresentationInput
    {
        public Vector3 WorldVelocity, SolePosition;
        public Pose Facing;
        public bool Grounded, Seated, Driver, Carried, Carrying, Pending, ReleasePreview;
        public MovementMode Mode;
        public float LookYaw, LookPitch, WalkSpeed, SprintSpeed;
        public int GroundMask;
        public uint ControlRevision, ResetRevision;
    }

    public sealed class AvatarBinding
    {
        public AvatarId Id { get; }
        public AvatarSettings Settings { get; }
        public Animator Animator { get; }
        public ulong Generation { get; }
        public AvatarSettings.GeneratedSkeleton Measurements { get; private set; }
        public float Scale => Settings.Scale;
        public Transform LeftCarryGrip { get; }
        public Transform RightCarryGrip { get; }
        public bool HasCarryGrips => LeftCarryGrip && RightCarryGrip && LeftCarryGrip != RightCarryGrip;
        private readonly Transform[] bones;
        private readonly bool firstPerson;
        internal AvatarBinding(AvatarId id, AvatarSettings settings, Animator animator, Transform[] bones, ulong generation, bool firstPerson = false)
        {
            Id = id; Settings = settings; Animator = animator; this.bones = bones; Generation = generation; this.firstPerson = firstPerson;
            Measurements = AvatarPalmCalibration.Measurements(settings, firstPerson);
            if (firstPerson) return;
            var skeleton = GetBone(HumanBodyBones.Hips);
            if (!skeleton) return;
            Transform left = null, right = null;
            int leftCount = 0, rightCount = 0;
            foreach (var child in skeleton.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == "LeftHandGrip") { left = child; leftCount++; }
                if (child.name == "RightHandGrip") { right = child; rightCount++; }
            }
            if (leftCount == 1 && rightCount == 1) { LeftCarryGrip = left; RightCarryGrip = right; }
        }
        internal void RefreshMeasurements() => Measurements = AvatarPalmCalibration.Measurements(Settings, firstPerson);
        public Transform GetBone(HumanBodyBones bone) => bones[(int)bone];
        internal Pose Palm(bool right)
        {
            var wrist = GetBone(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            Vector3 offset = right ? Measurements.RightWristToPalmPosition : Measurements.LeftWristToPalmPosition;
            Quaternion rotation = right ? Measurements.RightWristToPalmRotation : Measurements.LeftWristToPalmRotation;
            return new Pose(wrist.position + wrist.rotation * (offset * Scale), wrist.rotation * rotation);
        }
        internal Pose Body => new((GetBone(HumanBodyBones.LeftUpperArm).position + GetBone(HumanBodyBones.RightUpperArm).position) * 0.5f,
            Animator.transform.rotation);
    }

    public sealed class AvatarPresentation : MonoBehaviour
    {
        [SerializeField] private AvatarRegistry registry;
        public AvatarRegistry Registry => registry;
        public AvatarId RequestedId { get; private set; }
        public AvatarRegistry.Entry Resolved { get; private set; }
        public AvatarBinding Binding => active ? active.Binding : null;
        public event Action<AvatarRegistry.Entry> IdentityResolved;
        public event Action<AvatarBinding> WillUnbind, DidBind;
        public event Action<bool> FallbackChanged;
        public event Action BeforeEvaluation;
        internal event Action<AvatarBinding, float> PreparingHands;
        internal event Action<AvatarBinding> HandsEvaluated;
        internal event Action<AvatarBinding> PosingHands;
        internal void PoseHands(AvatarBinding binding) => PosingHands?.Invoke(binding);
        internal bool EvaluatesTargets => !Physical && system && !Failed && Binding != null;
        internal void PrepareTargets() { if (!Physical) BeforeEvaluation?.Invoke(); }
        internal AvatarPresentation HandDependency { get; set; }
        internal bool Physical { get; private set; }
        private bool lifeLocked, hasDeferredIdentity;
        private AvatarId deferredIdentity;
        internal readonly AvatarAnimationState State = new();
        internal AvatarPresentationInput Input;
        internal bool AnimationEnabled { get; private set; } = true;
        internal bool EditorPreview;
        internal Vector3 EditorLookTarget;
        internal bool FootIkEnabled { get; private set; } = true;
        internal bool HeadLookEnabled { get; private set; } = true;
        internal bool SpringsEnabled { get; private set; } = true;
        internal float BodyYaw { get; private set; }
        internal uint RequestGeneration { get; private set; }
        internal bool NeedsPreparation { get; private set; }
        internal bool CanPrepare => NeedsPreparation && (!lifeLocked || !active);
        internal bool Failed { get; private set; }
        public AvatarHandTargets HandTargets { get; } = new();
        private static readonly AvatarHandTargets noTargets = new();
        internal bool IgnoresHandTargets { get; set; }
        internal AvatarHandTargets BodyTargets => IgnoresHandTargets ? noTargets : HandTargets;
        internal bool Dormant { get; private set; }
        internal bool Idle => Dormant && !NeedsPreparation && !candidate;
        private bool waking;
        private AvatarInstance active, candidate;
        private AvatarRegistry.Entry candidateEntry;
        private AvatarSettings subscribedSettings;
        private AvatarAnimationSet subscribedAnimations;
        private bool animationsValid;
        private AvatarPresentationSystem system;
        private uint candidateRequest, resetRevision, controlRevision;
        private ulong bindingGeneration;
        private int candidateFrame;
        private bool visual, facingInitialized, turning, moving, wasAttached, hasPosition;
        private Vector3 previousPosition;
        internal Func<AvatarPresentationInput> InputSource;

        private void OnEnable()
        {
            if (registry) registry.ContentChanged += Rebind;
            if (subscribedSettings) subscribedSettings.ContentChanged += Rebind;
            Rebind();
            RefreshAnimations();
            UpdateRegistration();
        }
        private void OnDisable()
        {
            if (registry) registry.ContentChanged -= Rebind;
            if (subscribedSettings) subscribedSettings.ContentChanged -= Rebind;
            if (subscribedAnimations) subscribedAnimations.ContentChanged -= Rebind;
            subscribedAnimations = null;
            UpdateRegistration();
        }

        public void Configure(AvatarRegistry value, bool render, float phaseSeed = 0f)
        {
            if (registry) registry.ContentChanged -= Rebind;
            registry = value;
            if (isActiveAndEnabled && registry) registry.ContentChanged += Rebind;
            State.Seed(phaseSeed);
            RefreshAnimations();
            SetVisual(render);
        }

        public void SetVisual(bool value)
        {
            visual = value;
            UpdateRegistration();
        }

        private void UpdateRegistration()
        {
            if (visual && isActiveAndEnabled)
            {
                if (system) return;
                system = AvatarPresentationSystem.ForScene(gameObject.scene);
                system.Register(this);
                if (Resolved != null) NeedsPreparation = true;
                FallbackChanged?.Invoke(!active);
            }
            else
            {
                if (system) system.Unregister(this);
                ReleaseInstances();
                system = null;
                facingInitialized = hasPosition = false;
                FallbackChanged?.Invoke(false);
            }
        }

        public void RequestAvatar(AvatarId id)
        {
            if (lifeLocked) { deferredIdentity = id; hasDeferredIdentity = true; return; }
            RequestedId = id;
            RequestGeneration++;
            NeedsPreparation = false;
            if (!registry || !registry.TryResolve(id, out var entry))
            {
                Debug.LogError($"Avatar {id} cannot be resolved; repair its registry entry or process its source.", this);
                return;
            }
            Resolved = entry;
            Failed = false;
            if (subscribedSettings) subscribedSettings.ContentChanged -= Rebind;
            subscribedSettings = entry.Settings;
            if (isActiveAndEnabled) subscribedSettings.ContentChanged += Rebind;
            IdentityResolved?.Invoke(entry);
            RefreshAnimations();
            NeedsPreparation = visual && isActiveAndEnabled;
        }

        private void Rebind()
        {
            if (active) active.RefreshMeasurements();
            if (candidate && candidate.Initialized) candidate.RefreshMeasurements();
            if (RequestedId.IsValid) RequestAvatar(RequestedId);
        }
        private void RefreshAnimations()
        {
            if (subscribedAnimations) subscribedAnimations.ContentChanged -= Rebind;
            subscribedAnimations = registry ? registry.Animations : null;
            animationsValid = subscribedAnimations && subscribedAnimations.IsComplete;
            if (isActiveAndEnabled && subscribedAnimations) subscribedAnimations.ContentChanged += Rebind;
        }
        public void SetInput(in AvatarPresentationInput input) => Input = input;
        public bool TryGetNameAnchor(out Vector3 position)
        {
            if (active)
            {
                position = active.Head.position + Vector3.up * active.NameAnchorOffset;
                return true;
            }
            position = default;
            return false;
        }

        public void SetFeatures(bool animation, bool footIk, bool headLook, bool springs)
        {
            if (AnimationEnabled == animation && FootIkEnabled == footIk && HeadLookEnabled == headLook && SpringsEnabled == springs) return;
            AnimationEnabled = animation; FootIkEnabled = footIk; HeadLookEnabled = headLook; SpringsEnabled = springs;
            if (active) active.ApplyFeatures();
            if (candidate && candidate.Initialized) candidate.ApplyFeatures();
        }

        internal void SetDormant(bool value)
        {
            if (Dormant == value) return;
            Dormant = value;
            if (value) State.ClearEmote(); else waking = true;
            if (active) active.SetDormant(value);
            if (candidate && candidate.Initialized) candidate.ApplyFeatures();
        }

        public void SetHandTarget(AvatarIKGoal hand, Transform target, float positionWeight, float rotationWeight, float maximumReach = 0f)
        {
            HandTargets.Set(hand, AvatarHandSource.Item, target, positionWeight, rotationWeight, maximumReach);
        }
        public void ClearHandTarget(AvatarIKGoal hand) => HandTargets.Clear(hand, AvatarHandSource.Item);

        internal void UpdateInput(float dt, bool gap)
        {
            if (Physical) return;
            if (!lifeLocked && InputSource != null) Input = InputSource();
            if (waking) { facingInitialized = false; gap = true; }
            bool attached = Input.Seated || Input.Carried && !Input.ReleasePreview || Input.Pending && wasAttached;
            float facing = Input.Facing.rotation.eulerAngles.y;
            if (!facingInitialized) { BodyYaw = facing; facingInitialized = true; }
            if (attached) BodyYaw = facing;
            else
            {
                float speed = new Vector2(Input.WorldVelocity.x, Input.WorldVelocity.z).magnitude;
                moving = moving ? speed >= 0.08f : speed > 0.15f;
                float residual = Mathf.DeltaAngle(BodyYaw, Input.LookYaw);
                if (Mathf.Abs(residual) > 45f) turning = true;
                else if (Mathf.Abs(residual) < 15f) turning = false;
                if (moving || turning)
                {
                    float delta = Mathf.DeltaAngle(BodyYaw, facing) * Smooth(dt, moving ? 0.06f : 0.16f);
                    BodyYaw += Mathf.Clamp(delta, -(moving ? 360f : 180f) * dt, (moving ? 360f : 180f) * dt);
                }
            }
            transform.SetPositionAndRotation(Input.Facing.position, attached ? Input.Facing.rotation : Quaternion.Euler(0f, BodyYaw, 0f));
            bool reset = gap || resetRevision != Input.ResetRevision || controlRevision != Input.ControlRevision || attached != wasAttached ||
                hasPosition && !attached && (transform.position - previousPosition).sqrMagnitude > 4f;
            previousPosition = transform.position; hasPosition = true;
            resetRevision = Input.ResetRevision; controlRevision = Input.ControlRevision; wasAttached = attached;
            if (AnimationEnabled && animationsValid)
            {
                var settings = active ? active.Settings : Resolved?.Settings;
                if (settings && waking) { State.Snap(Input, BodyYaw, settings, registry.Animations); waking = false; }
                else if (settings) State.Advance(Input, BodyYaw, settings, registry.Animations, dt);
            }
            if (reset)
            {
                if (active) active.ResetMotion();
                if (candidate && candidate.Initialized) candidate.ResetMotion();
            }
        }

        internal void Prepare(Transform staging)
        {
            if (lifeLocked && active) return;
            NeedsPreparation = false;
            CancelCandidate();
            candidateEntry = Resolved;
            candidateRequest = RequestGeneration;
            if (!animationsValid) throw new InvalidOperationException("Process the complete shared avatar animation set first.");
            GameObject root = Instantiate(candidateEntry.Prefab, staging);
            try
            {
                candidate = root.GetComponent<AvatarInstance>();
                if (!candidate) throw new InvalidOperationException("Processed prefab needs AvatarInstance on its root Animator.");
                candidate.Stage(this, candidateEntry, ++bindingGeneration);
                root.transform.SetParent(transform, false);
                root.SetActive(false);
                candidateFrame = Time.frameCount;
            }
            catch { if (!candidate) Destroy(root); else CancelCandidate(); throw; }
        }

        internal void Evaluate(float dt)
        {
            if (Physical) return;
            if (candidate && candidateRequest != RequestGeneration) CancelCandidate();
            if (active && !Dormant)
            {
                PreparingHands?.Invoke(active.Binding, dt);
                active.Evaluate(dt);
            }
            if (!candidate || Time.frameCount <= candidateFrame) return;
            try
            {
                if (!candidate.Initialized)
                {
                    candidate.gameObject.SetActive(true);
                    candidate.Initialize(registry.Animations);
                }
                PreparingHands?.Invoke(candidate.Binding, 0f);
                candidate.Evaluate(0f, true);
            }
            catch (Exception exception) { PreparationFailed(exception); }
        }

        internal void Commit()
        {
            if (Physical) return;
            if (!candidate || !candidate.Initialized || Time.frameCount <= candidateFrame + 1 || candidateRequest != RequestGeneration)
            { if (active) HandsEvaluated?.Invoke(active.Binding); return; }
            using (AvatarPresentationSystem.CommitMarker.Auto())
            {
                var old = active;
                if (old) WillUnbind?.Invoke(old.Binding);
                active = candidate; candidate = null;
                DidBind?.Invoke(active.Binding);
                if (!Physical)
                {
                    PreparingHands?.Invoke(active.Binding, 0f);
                    active.Evaluate(0f, true);
                    HandsEvaluated?.Invoke(active.Binding);
                }
                if (old) old.SetVisible(false);
                active.SetVisible(true);
                if (Dormant) active.SetDormant(true);
                FallbackChanged?.Invoke(false);
                if (old) old.Release();
            }
        }

        internal void PreparationFailed(Exception exception)
        {
            CancelCandidate();
            Debug.LogError($"Avatar {RequestedId} preparation failed: {exception.Message}", this);
            FallbackChanged?.Invoke(visual && !active);
        }

        internal void PrepareRagdoll(AvatarPresentationInput input)
        {
            SetDormant(false);
            Input = input;
            lifeLocked = true;
            SetVisual(true);
            UpdateInput(0f, true);
            if (Resolved != null && animationsValid) State.Snap(Input, BodyYaw, Resolved.Settings, registry.Animations);
            if (active) active.Evaluate(0f, true);
        }
        internal void SetPhysical(bool value)
        {
            Physical = value;
            if (active) active.SetPhysical(value);
        }
        internal void FinishRagdoll(AvatarPresentationInput input)
        {
            lifeLocked = false;
            Input = input;
            Input.WorldVelocity = Vector3.zero;
            Input.Grounded = true;
            Input.Mode = MovementMode.Walking;
            State.ClearEmote();
            if (Resolved != null && animationsValid) State.Snap(Input, input.Facing.rotation.eulerAngles.y, Resolved.Settings, registry.Animations);
            UpdateInput(0f, true);
            if (active) active.Evaluate(0f, true);
            if (hasDeferredIdentity)
            {
                hasDeferredIdentity = false;
                RequestAvatar(deferredIdentity);
            }
        }
        internal void PresentationFailed(Exception exception)
        {
            Failed = true;
            ReleaseInstances();
            Debug.LogError($"Avatar {RequestedId} presentation stopped: {exception}", this);
            FallbackChanged?.Invoke(visual && isActiveAndEnabled);
        }
        private void CancelCandidate() { if (candidate) candidate.Release(); candidate = null; }
        internal void ReleaseInstances()
        {
            NeedsPreparation = false;
            CancelCandidate();
            if (active) { WillUnbind?.Invoke(active.Binding); active.Release(); active = null; }
        }
        internal static float Smooth(float dt, float halfLife) => 1f - Mathf.Pow(0.5f, dt / halfLife);
    }
}
