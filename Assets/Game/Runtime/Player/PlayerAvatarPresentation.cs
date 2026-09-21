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
        [SerializeField] private AvatarPresentation presentation;
        private readonly SyncVar<AvatarId> selected = new();
        private PlayerMotor motor;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private PlayerPresentation player;
        private PlayerInputReader input;
        private PlayerNetworkState state;
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
            graphics = player.Graphics;
            var capsule = GetComponent<CapsuleCollider>();
            capsuleSole = capsule.center - Vector3.up * (capsule.height * 0.5f);
            selected.OnChange += IdentityChanged;
            seating.PresentationContextChanged += ContextChanged;
            carry.PresentationContextChanged += ContextChanged;
            presentation.FallbackChanged += player.SetFallbackVisible;
            presentation.InputSource = CaptureInput;
        }

        internal void Initialize() => selected.Value = presentation.Registry.DefaultId;
        public override void OnStartClient()
        {
            presentation.Configure(presentation.Registry, !IsOwner, state.Snapshot.SpawnSlot / 8f);
            ResolveSelected(selected.Value);
            player.SetFallbackVisible(!IsOwner && presentation.Binding == null);
            contextDirty = IsOwner;
        }
        public override void OnStopClient() => presentation.SetVisual(false);
        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            hasReceived = hasPending = false;
            presentation.SetVisual(!IsOwner && IsClientInitialized);
            player.SetFallbackVisible(!IsOwner && presentation.Binding == null);
            contextDirty = IsOwner;
        }
        public override void OnOwnershipServer(NetworkConnection previousOwner) => hasServerSample = false;
        public override void OnSpawnServer(NetworkConnection connection)
        {
            var baseline = hasServerSample ? serverSample : new AvatarLookSample
            { ControlRevision = motor.ControlRevision, Angles = AvatarLookSample.Pack(0f, 0f, Attached) };
            TargetLook(connection, baseline);
        }
        private bool Attached => seating.Seated || carry.IsCarried && !carry.ReleasePreview;

        private void IdentityChanged(AvatarId oldValue, AvatarId value, bool asServer)
        { if (IsClientInitialized) ResolveSelected(value); }
        private void ResolveSelected(AvatarId id)
        { if (presentation.RequestedId != id) presentation.RequestAvatar(id); }

        public void RequestAvatar(AvatarId id)
        {
            if (!IsOwner || !IsClientInitialized) return;
            presentation.RequestAvatar(id);
            ServerAvatar(id);
        }
        public void SetAvatarServer(AvatarId id)
        {
            if (!IsServerInitialized || !presentation.Registry.TryResolve(id, out _)) return;
            selected.Value = id;
        }
        [ServerRpc] private void ServerAvatar(AvatarId id)
        {
            if (presentation.Registry.TryResolve(id, out _)) selected.Value = id;
            else TargetAvatar(Owner, selected.Value);
        }
        [TargetRpc] private void TargetAvatar(NetworkConnection connection, AvatarId id) => presentation.RequestAvatar(id);

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
            if (!IsClientInitialized || !IsOwner || seating.AwaitingReference) return;
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
                Grounded = motor.Grounded, Mode = motor.Mode, Seated = seating.Seated, Carried = carry.IsCarried,
                Carrying = carry.IsCarrying, Pending = seating.AwaitingReference || seating.PlacementPending,
                ReleasePreview = carry.ReleasePreview, LookPitch = pitch,
                LookYaw = graphics.eulerAngles.y + (Attached ? relativeYaw : 0f),
                WalkSpeed = motor.WalkSpeed, SprintSpeed = motor.SprintSpeed, GroundMask = motor.GroundLayers,
                ControlRevision = motor.ControlRevision, ResetRevision = motor.ResetRevision
            };

        private void OnDestroy()
        {
            selected.OnChange -= IdentityChanged;
            if (seating) seating.PresentationContextChanged -= ContextChanged;
            if (carry) carry.PresentationContextChanged -= ContextChanged;
            if (presentation) { presentation.FallbackChanged -= player.SetFallbackVisible; presentation.InputSource = null; }
        }
    }
}
