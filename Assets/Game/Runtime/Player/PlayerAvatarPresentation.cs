using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Serializing;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public struct AvatarLookSample
    {
        public ushort Sequence, Angles;
        public uint ControlRevision;
        public bool Attached => (Angles & 0x8000) != 0;
        public float Pitch => (Angles & 127) * (178f / 126f) - 89f;
        public float Yaw => ((Angles >> 7) & 255) * (360f / 256f) - 180f;
        public static ushort Pack(float pitch, float yaw, bool attached)
        {
            int p = Mathf.RoundToInt((Mathf.Clamp(pitch, -89f, 89f) + 89f) * (126f / 178f));
            int y = attached ? Mathf.RoundToInt(Mathf.Repeat(yaw + 180f, 360f) * (256f / 360f)) & 255 : 0;
            return (ushort)(p | y << 7 | (attached ? 0x8000 : 0));
        }
    }

    public static class AvatarLookSerializer
    {
        public static void WriteAvatarLookSample(this Writer writer, AvatarLookSample value)
        { writer.WriteUInt16Unpacked(value.Sequence); writer.WriteUInt32(value.ControlRevision); writer.WriteUInt16Unpacked(value.Angles); }
        public static AvatarLookSample ReadAvatarLookSample(this Reader reader) => new()
        { Sequence = reader.ReadUInt16Unpacked(), ControlRevision = reader.ReadUInt32(), Angles = reader.ReadUInt16Unpacked() };
    }

    [DefaultExecutionOrder(100)]
    public sealed class PlayerAvatarPresentation : NetworkBehaviour
    {
        internal PlayerHandPresentation Hands { get; private set; }
        internal PlayerEmote Emote { get; private set; }
        [SerializeField] private AvatarPresentation presentation;
        private readonly SyncVar<AvatarAppearance> selected = new();
        private AvatarAppearance desired;
        private AvatarAppearanceStore store;
        private AvatarCosmeticPresentation cosmetics;
        public AvatarAppearance Appearance => desired?.Clone();
        private PlayerMotor motor;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private PlayerPresentation player;
        private PlayerInputReader input;
        private PlayerNetworkState state;
        private PlayerHealth health;
        private Transform graphics;
        private Vector3 capsuleSole;
        private AvatarLookSample serverSample, received, pending;
        private bool hasServerSample, hasReceived, hasPending, smoothInitialized, contextDirty;
        private float nextSend, lastSend, pitch, relativeYaw;
        private ushort sequence, lastAngles;
        public AvatarPresentation Presentation => presentation;

        private void Awake()
        {
            motor = GetComponent<PlayerMotor>(); seating = GetComponent<PlayerSeating>();
            carry = GetComponent<PlayerCarry>(); player = GetComponent<PlayerPresentation>();
            input = GetComponent<PlayerInputReader>(); state = GetComponent<PlayerNetworkState>();
            health = GetComponent<PlayerHealth>();
            health.LifeChanged += LifeChanged;
            graphics = player.Graphics;
            var capsule = GetComponent<CapsuleCollider>();
            capsuleSole = capsule.center - Vector3.up * (capsule.height * 0.5f);
            selected.OnChange += IdentityChanged;
            presentation.DidBind += BindCosmetics;
            presentation.WillUnbind += UnbindCosmetics;
            seating.PresentationContextChanged += ContextChanged;
            carry.PresentationContextChanged += ContextChanged;
            presentation.FallbackChanged += player.SetFallbackVisible;
            presentation.InputSource = CaptureInput;
            Hands = gameObject.AddComponent<PlayerHandPresentation>();
            Hands.Initialize(this);
            Emote = gameObject.AddComponent<PlayerEmote>();
            Emote.Initialize(this);
            Emote.Changed += EmoteChanged;
        }

        internal void Initialize()
        {
            presentation.Configure(SessionController.Instance.Avatars, false);
            selected.Value = SessionController.Instance.Appearance.Resolve(new AvatarAppearance { Avatar = presentation.Registry.DefaultId });
        }
        public override void OnStartClient()
        {
            presentation.Configure(SessionController.Instance.Avatars, true, state.Snapshot.SpawnSlot / 8f);
            RefreshBody();
            BindStore();
            if (!IsOwner) ResolveSelected(selected.Value);
            if (health.IsAlive) Hands.StartPresentation();
            player.SetFallbackVisible(!IsOwner && presentation.Binding == null);
            contextDirty = IsOwner;
        }
        public override void OnStopClient() { DetachStore(); Hands.StopPresentation(); Emote.ResetLocal(); presentation.SetVisual(false); }
        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            Emote.ResetLocal();
            Hands.StopPresentation();
            BindStore();
            if (!IsOwner) ResolveSelected(selected.Value);
            hasReceived = hasPending = false;
            RefreshBody();
            player.SetFallbackVisible(!IsOwner && presentation.Binding == null);
            contextDirty = IsOwner;
            if (IsClientInitialized && health.IsAlive) Hands.StartPresentation();
        }
        public override void OnOwnershipServer(NetworkConnection previousOwner) => hasServerSample = false;
        public override void OnSpawnServer(NetworkConnection connection)
        {
            var baseline = hasServerSample ? serverSample : new AvatarLookSample
            { ControlRevision = motor.ControlRevision, Angles = AvatarLookSample.Pack(0f, 0f, Attached) };
            TargetLook(connection, baseline);
        }
        private bool Attached => seating.Seated || carry.IsCarried && !carry.ReleasePreview;

        private void RefreshBody()
        {
            presentation.IgnoresHandTargets = IsOwner;
            presentation.SetDormant(IsOwner && health.IsAlive && !Emote.Presenting);
            presentation.SetVisual(IsClientInitialized);
        }
        private void EmoteChanged() { if (!Emote.Active) contextDirty = true; RefreshBody(); }

        private void IdentityChanged(AvatarAppearance oldValue, AvatarAppearance value, bool asServer)
        { if (IsClientInitialized && !IsOwner) ResolveSelected(value); }
        private void ResolveSelected(AvatarAppearance value)
        {
            if (value == null) return;
            desired = SessionController.Instance.Appearance.Resolve(value);
            if (presentation.RequestedId != desired.Avatar) presentation.RequestAvatar(desired.Avatar);
            if (presentation.Binding?.Id == desired.Avatar) cosmetics?.Apply(desired);
            Hands.AppearanceChanged();
        }
        private void BindCosmetics(AvatarBinding binding)
        {
            cosmetics?.Dispose();
            var session = SessionController.Instance;
            cosmetics = new AvatarCosmeticPresentation(binding, session.Hats, session.Tattoos);
            cosmetics.Apply(desired);
        }
        private void UnbindCosmetics(AvatarBinding binding) { cosmetics?.Dispose(); cosmetics = null; }
        private void BindStore()
        {
            if (IsOwner && store != null) return;
            DetachStore();
            if (!IsOwner || !IsClientInitialized) return;
            store = SessionController.Instance.Appearance;
            store.CommittedChanged += CommitAppearance;
            CommitAppearance(store.Committed);
        }
        private void DetachStore()
        { if (store != null) store.CommittedChanged -= CommitAppearance; store = null; }
        private void CommitAppearance(AvatarAppearance value)
        {
            ResolveSelected(value);
            if (IsOwner && IsClientInitialized) ServerAppearance(value.Clone());
        }

        public void RequestAvatar(AvatarId id)
        {
            if (!IsOwner || !IsClientInitialized) return;
            if (!presentation.Registry.TryResolve(id, out var entry)) return;
            SessionController.Instance.Appearance.Commit(AvatarTattooPlacement.Transfer(
                SessionController.Instance.Appearance.Committed, entry));
        }
#if UNITY_INCLUDE_INSTRUMENTATION
        public void RequestAuthoringAvatar(AvatarId id)
        {
            if (!IsOwner || !IsClientInitialized || !presentation.Registry.TryResolve(id, out var entry)) return;
            CommitAppearance(AvatarTattooPlacement.Transfer(desired ?? new AvatarAppearance(), entry));
        }
#endif
        public void SetAvatarServer(AvatarId id)
        {
            if (!IsServerInitialized || !presentation.Registry.TryResolve(id, out var entry)) return;
            selected.Value = AvatarTattooPlacement.Transfer(selected.Value ?? new AvatarAppearance(), entry);
        }
        [ServerRpc] private void ServerAppearance(AvatarAppearance value)
        {
            var resolved = SessionController.Instance.Appearance.Resolve(value);
            if (!resolved.Equals(selected.Value)) selected.Value = resolved.Clone();
        }

        private void ContextChanged()
        {
            contextDirty = true;
            if (hasPending && pending.ControlRevision == motor.ControlRevision && !seating.AwaitingReference)
            { var sample = pending; hasPending = false; ApplyLook(sample, true); }
            else if (hasReceived && received.ControlRevision != motor.ControlRevision)
            { hasReceived = false; relativeYaw = 0f; }
        }

        private void LateUpdate()
        {
            if (health.IsDowned || !IsClientInitialized || !IsOwner || seating.AwaitingReference || Emote.Active) return;
            float now = Time.unscaledTime;
            if (!contextDirty && now < nextSend) return;
            var aim = player.AimPose;
            float yaw = seating.Seated ? seating.AttachmentLookYaw : Mathf.DeltaAngle(graphics.eulerAngles.y, aim.rotation.eulerAngles.y);
            ushort angles = AvatarLookSample.Pack(input.Pitch, yaw, Attached);
            if (!contextDirty && angles == lastAngles && now - lastSend < 0.5f) return;
            var sample = new AvatarLookSample { Sequence = ++sequence, ControlRevision = motor.ControlRevision, Angles = angles };
            ServerLook(sample, contextDirty ? Channel.Reliable : Channel.Unreliable);
            contextDirty = false; lastAngles = angles; lastSend = now; nextSend = now + 0.05f;
        }

        [ServerRpc] private void ServerLook(AvatarLookSample sample, Channel channel = Channel.Unreliable)
        {
            if (hasServerSample && (!Newer(sample.Sequence, serverSample.Sequence) || sample.ControlRevision < serverSample.ControlRevision) ||
                sample.ControlRevision < motor.ControlRevision) return;
            serverSample = sample; hasServerSample = true;
            if (IsClientInitialized && !IsOwner) ReceiveLook(sample, false);
            ObserversLook(sample, channel);
        }
        [ObserversRpc(ExcludeOwner = true)] private void ObserversLook(AvatarLookSample sample, Channel channel = Channel.Unreliable)
        { if (!IsServerInitialized) ReceiveLook(sample, false); }
        internal void SendEmote(byte id) => ServerEmote(id);
        [ServerRpc] private void ServerEmote(byte id) { if (IsClientInitialized && !IsOwner) Emote.Receive(id); ObserversEmote(id); }
        [ObserversRpc(ExcludeOwner = true)] private void ObserversEmote(byte id) { if (!IsServerInitialized) Emote.Receive(id); }
        [TargetRpc] private void TargetLook(NetworkConnection connection, AvatarLookSample sample)
        { if (!IsOwner) ReceiveLook(sample, true); }
        private static bool Newer(ushort value, ushort previous) => (short)(value - previous) > 0;

        private void ReceiveLook(AvatarLookSample sample, bool baseline)
        {
            if (sample.ControlRevision < motor.ControlRevision ||
                hasReceived && (!Newer(sample.Sequence, received.Sequence) || sample.ControlRevision < received.ControlRevision) ||
                hasPending && (!Newer(sample.Sequence, pending.Sequence) || sample.ControlRevision < pending.ControlRevision)) return;
            if (sample.ControlRevision > motor.ControlRevision || seating.AwaitingReference)
            { pending = sample; hasPending = true; return; }
            ApplyLook(sample, baseline || !hasReceived);
        }
        private void ApplyLook(AvatarLookSample sample, bool snap)
        {
            if (sample.Attached != Attached) return;
            received = sample; hasReceived = true;
            if (snap || !smoothInitialized)
            { pitch = sample.Pitch; relativeYaw = sample.Attached ? sample.Yaw : 0f; smoothInitialized = true; }
        }

        private AvatarPresentationInput CaptureInput()
        {
            float alpha = AvatarPresentation.Smooth(Mathf.Min(Time.deltaTime, 0.05f), 0.06f);
            if (hasReceived)
            {
                pitch = Mathf.Lerp(pitch, received.Pitch, alpha);
                relativeYaw = Mathf.LerpAngle(relativeYaw, received.Attached ? received.Yaw : 0f, alpha);
            }
            return CurrentPlacement;
        }

        internal AvatarPresentationInput CurrentPlacement => new AvatarPresentationInput
            {
                Facing = new Pose(graphics.position, graphics.rotation), SolePosition = graphics.TransformPoint(capsuleSole),
                WorldVelocity = carry.ReleasePreview ? carry.PreviewVelocity : motor.Body.linearVelocity,
                Grounded = motor.Grounded, Mode = motor.Mode, Seated = seating.Seated, Driver = seating.IsDriver, Carried = carry.IsCarried,
                Carrying = carry.IsCarrying, Pending = seating.AwaitingReference || seating.PlacementPending,
                ReleasePreview = carry.ReleasePreview, LookPitch = pitch,
                LookYaw = graphics.eulerAngles.y + (Attached ? relativeYaw : 0f),
                WalkSpeed = motor.WalkSpeed, SprintSpeed = motor.SprintSpeed, GroundMask = motor.GroundLayers,
                ControlRevision = motor.ControlRevision, ResetRevision = motor.ResetRevision
            };

        private void OnDestroy()
        {
            DetachStore(); cosmetics?.Dispose();
            if (presentation) { presentation.DidBind -= BindCosmetics; presentation.WillUnbind -= UnbindCosmetics; }
            if (health) health.LifeChanged -= LifeChanged;
            if (Emote) Emote.Changed -= EmoteChanged;
            selected.OnChange -= IdentityChanged;
            if (seating) seating.PresentationContextChanged -= ContextChanged;
            if (carry) carry.PresentationContextChanged -= ContextChanged;
            if (presentation) { presentation.FallbackChanged -= player.SetFallbackVisible; presentation.InputSource = null; }
        }

        private void LifeChanged()
        {
            if (!IsClientInitialized) return;
            if (health.IsDowned) Hands.StopPresentation();
            else
            {
                presentation.FinishRagdoll(CurrentPlacement);
                Hands.StartPresentation();
            }
            RefreshBody();
            ContextChanged();
        }
    }
}
