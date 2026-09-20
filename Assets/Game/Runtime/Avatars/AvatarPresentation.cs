using System;
using UnityEngine;

namespace TwoBirds
{
    public struct AvatarPresentationInput
    {
        public Vector3 WorldVelocity, SolePosition;
        public Pose Facing;
        public bool Grounded, Seated, Carried, Carrying, Pending, ReleasePreview;
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
        private readonly Transform[] bones;
        internal AvatarBinding(AvatarId id, AvatarSettings settings, Animator animator, Transform[] bones, ulong generation)
        { Id = id; Settings = settings; Animator = animator; this.bones = bones; Generation = generation; }
        public Transform GetBone(HumanBodyBones bone) => bones[(int)bone];
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
        internal readonly AvatarAnimationState State = new();
        internal AvatarPresentationInput Input;
        internal bool AnimationEnabled { get; private set; } = true;
        internal bool FootIkEnabled { get; private set; } = true;
        internal bool HeadLookEnabled { get; private set; } = true;
        internal bool SpringsEnabled { get; private set; } = true;
        internal float BodyYaw { get; private set; }
        internal uint RequestGeneration { get; private set; }
        internal bool NeedsPreparation { get; private set; }
        internal struct HandTarget { internal Transform Target; internal float Position, Rotation; }
        internal HandTarget LeftHand, RightHand;
        private AvatarInstance active, candidate;
        private AvatarRegistry.Entry candidateEntry;
        private AvatarSettings subscribedSettings;
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
        }
        private void OnDisable()
        {
            if (registry) registry.ContentChanged -= Rebind;
            if (subscribedSettings) subscribedSettings.ContentChanged -= Rebind;
            SetVisual(false);
        }

        public void Configure(AvatarRegistry value, bool render, float phaseSeed = 0f)
        {
            if (registry) registry.ContentChanged -= Rebind;
            registry = value;
            if (isActiveAndEnabled && registry) registry.ContentChanged += Rebind;
            State.Seed(phaseSeed);
            SetVisual(render);
        }

        public void SetVisual(bool value)
        {
            if (visual == value) return;
            visual = value;
            if (value)
            {
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
                FallbackChanged?.Invoke(false);
            }
        }

        public void RequestAvatar(AvatarId id)
        {
            RequestedId = id;
            RequestGeneration++;
            NeedsPreparation = false;
            if (!registry || !registry.TryResolve(id, out var entry))
            {
                Debug.LogError($"Avatar {id} cannot be resolved; repair its registry entry or process its source.", this);
                return;
            }
            Resolved = entry;
            if (subscribedSettings) subscribedSettings.ContentChanged -= Rebind;
            subscribedSettings = entry.Settings;
            if (isActiveAndEnabled) subscribedSettings.ContentChanged += Rebind;
            IdentityResolved?.Invoke(entry);
            NeedsPreparation = visual;
        }

        private void Rebind() { if (RequestedId.IsValid) RequestAvatar(RequestedId); }
        public void SetInput(in AvatarPresentationInput input) => Input = input;
        public bool TryGetNameAnchor(out Vector3 position)
        {
            if (active)
            {
                position = active.Head.position + Vector3.up * (0.12f * active.VisualHeight);
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

        public void SetHandTarget(AvatarIKGoal hand, Transform target, float positionWeight, float rotationWeight)
        {
            var value = new HandTarget { Target = target, Position = Mathf.Clamp01(positionWeight), Rotation = Mathf.Clamp01(rotationWeight) };
            if (hand == AvatarIKGoal.LeftHand) LeftHand = value;
            else if (hand == AvatarIKGoal.RightHand) RightHand = value;
            else throw new ArgumentException("Only hand goals are supported.", nameof(hand));
        }
        public void ClearHandTarget(AvatarIKGoal hand) => SetHandTarget(hand, null, 0f, 0f);

        internal void UpdateInput(float dt, bool gap)
        {
            if (InputSource != null) Input = InputSource();
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
            if (AnimationEnabled && registry && registry.Animations && registry.Animations.IsComplete)
            {
                var settings = active ? active.Settings : Resolved?.Settings;
                if (settings) State.Advance(Input, BodyYaw, settings, registry.Animations, dt);
            }
            if (reset)
            {
                if (active) active.ResetMotion();
                if (candidate && candidate.Initialized) candidate.ResetMotion();
            }
        }

        internal void Prepare(Transform staging)
        {
            NeedsPreparation = false;
            CancelCandidate();
            candidateEntry = Resolved;
            candidateRequest = RequestGeneration;
            if (!registry.Animations || !registry.Animations.IsComplete) throw new InvalidOperationException("Process the complete shared avatar animation set first.");
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
            if (candidate && candidateRequest != RequestGeneration) CancelCandidate();
            if (active) active.Evaluate(dt);
            if (!candidate || Time.frameCount <= candidateFrame) return;
            try
            {
                if (!candidate.Initialized)
                {
                    candidate.gameObject.SetActive(true);
                    candidate.Initialize(registry.Animations);
                }
                candidate.Evaluate(dt, true);
            }
            catch (Exception exception) { PreparationFailed(exception); }
        }

        internal void Commit()
        {
            if (!candidate || !candidate.Initialized || Time.frameCount <= candidateFrame + 1 || candidateRequest != RequestGeneration) return;
            using (AvatarPresentationSystem.CommitMarker.Auto())
            {
                var old = active;
                if (old) WillUnbind?.Invoke(old.Binding);
                active = candidate; candidate = null;
                DidBind?.Invoke(active.Binding);
                if (old) old.SetVisible(false);
                active.SetVisible(true);
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
