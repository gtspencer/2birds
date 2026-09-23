using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public enum CarryRole : byte { Free, Carrying, Carried }
    public enum CarryTransitionKind : byte { Pickup, Release }
    public enum CarryRequestResult : byte { Completed, Unavailable, Changed }

    public struct PlayerRelease
    {
        public Vector3 Position, Velocity;
        public float Yaw, Recovery;
        public bool PlacementPending;
        public ItemReleaseIntent Intent;
    }

    public struct CarryTransition
    {
        public CarryTransitionKind Kind;
        public int Carrier, Carried;
        public uint CarrierRevision, CarriedRevision, Generation, CarrierEffectReset, CarriedEffectReset;
        public PlayerRelease Release;
        public float Immunity;
    }

    internal readonly struct CarryPresentationRelease
    {
        internal readonly PlayerCarry Partner;
        internal readonly uint Revision, Request;
        internal readonly PlayerRelease Release;
        internal CarryPresentationRelease(PlayerCarry partner, uint revision, uint request, PlayerRelease release)
        { Partner = partner; Revision = revision; Request = request; Release = release; }
    }

    [DefaultExecutionOrder(25)]
    public sealed class PlayerCarry : NetworkBehaviour, IInteractable
    {
        internal static readonly Dictionary<int, PlayerCarry> Players = new();
        private static readonly Dictionary<int, CarryTransition> presentationReleases = new();
        public static PlayerCarry Local { get; private set; }
        [SerializeField] private GameSettings settings;
        private PlayerMotor motor;
        private PlayerInputReader input;
        private PlayerSeating seating;
        private PlayerPresentation presentation;
        private PlayerEquipment equipment;
        private PlayerHealth health;
        private CapsuleCollider capsule;
        private float immuneUntil, chargeStart, previewStart;
        private bool charging, preview;
        private PlayerRelease previewRelease;
        private uint nextRequest, pendingRequest;
        public CarryRole Role { get; private set; }
        public PlayerCarry Partner { get; private set; }
        public bool IsCarried => Role == CarryRole.Carried;
        public bool IsCarrying => Role == CarryRole.Carrying;
        public bool RequestPending => pendingRequest != 0;
        public bool IsCharging => charging;
        internal bool ReleasePreview => preview;
        public event System.Action PresentationContextChanged;
        internal event System.Action<CarryPresentationRelease> PreparingRelease, ReleaseStarted;
        internal event System.Action<uint> ReleaseRejected;
        internal Vector3 PreviewVelocity => Time.unscaledTime - previewStart < 0.25f
            ? previewRelease.Velocity + Physics.gravity * (Time.unscaledTime - previewStart) : Vector3.zero;

        private void SetPreview(bool value)
        {
            if (preview == value) return;
            preview = value;
            PresentationContextChanged?.Invoke();
        }
        public float Charge01 => charging ? Mathf.Clamp01((Time.unscaledTime - chargeStart) / settings.PlayerThrowChargeTime) : 0f;
        internal float RemainingImmunity => Mathf.Max(0f, immuneUntil - Time.unscaledTime);
        internal PlayerSeating Seating => seating;
        public string ActionText => "Pick up";
        public string InputActionPath => "Player/Interact";
        public string TooltipTextOverride => "";
        public bool HideTooltipText => false;
        public bool CanInteract => Local && Local.input.GameplayActive && !Local.input.InputSuppressed && Eligible(Local, this);
        internal bool PhysicalTarget(Collider target) => target == capsule && CanInteract;

        private void Awake()
        {
            motor = GetComponent<PlayerMotor>();
            input = GetComponent<PlayerInputReader>();
            seating = GetComponent<PlayerSeating>();
            presentation = GetComponent<PlayerPresentation>();
            equipment = GetComponent<PlayerEquipment>();
            health = GetComponent<PlayerHealth>();
            capsule = GetComponent<CapsuleCollider>();
        }

        public override void OnStartNetwork()
        {
            Players[ObjectId] = this;
            PlayerSeating.ResolvePending();
        }

        public override void OnStartClient() { if (IsOwner) Local = this; }
        public override void OnStartServer() => ServerManager.Objects.OnPreDestroyClientObjects += Disconnect;

        private static bool Free(PlayerCarry player) => player && player.isActiveAndEnabled && player.IsSpawned &&
            player.health.IsAlive && player.seating.CanGameplayActions && player.Role == CarryRole.Free && !player.RequestPending && !player.motor.Suspended &&
            !player.seating.Seated && !player.seating.PlacementPending && !player.seating.TransitionPending &&
            !player.seating.ServerRequestPending;

        private static bool Eligible(PlayerCarry carrier, PlayerCarry target) => carrier != target &&
            Free(carrier) && Free(target) && target.RemainingImmunity <= 0f;

        public void Interact()
        {
            if (CanInteract) Local.RequestPickup(this);
        }

        private void RequestPickup(PlayerCarry target)
        {
            if (!IsOwner || !Eligible(this, target) || !input.GameplayActive) return;
            uint request = ++nextRequest;
            if (IsServerInitialized) AcceptPickup(request, target.ObjectId, motor.ControlRevision, target.motor.ControlRevision);
            else
            {
                pendingRequest = request;
                ServerPickup(request, target.ObjectId, motor.ControlRevision, target.motor.ControlRevision);
            }
        }

        [ServerRpc]
        private void ServerPickup(uint request, int target, uint revision, uint targetRevision) =>
            AcceptPickup(request, target, revision, targetRevision);

        private void AcceptPickup(uint request, int targetId, uint revision, uint targetRevision)
        {
            if (!Players.TryGetValue(targetId, out var target) || !Eligible(this, target) ||
                revision != motor.ControlRevision || targetRevision != target.motor.ControlRevision)
            { Complete(request, CarryRequestResult.Unavailable); return; }
            var transition = new CarryTransition
            {
                Kind = CarryTransitionKind.Pickup, Carrier = ObjectId, Carried = targetId,
                CarrierRevision = motor.ControlRevision + 1, CarriedRevision = target.motor.ControlRevision + 1,
                CarrierEffectReset = seating.EffectReset, CarriedEffectReset = target.seating.EffectReset,
                Generation = target.motor.ImpactGeneration + 1
            };
            Commit(transition);
            Complete(request, CarryRequestResult.Completed);
        }

        internal void Install(CarryRole role, int partner, float immunity)
        {
            equipment.CancelUse();
            Role = role;
            Partner = null;
            if (role != CarryRole.Free && Players.TryGetValue(partner, out var found)) Partner = found;
            immuneUntil = Time.unscaledTime + immunity;
            pendingRequest = 0;
            SetPreview(false);
        }

        public void BeginUse()
        {
            if (!seating.CanGameplayActions || !IsOwner || !IsCarrying || charging || RequestPending || seating.TransitionPending || !input.GameplayActive) return;
            charging = true;
            chargeStart = Time.unscaledTime;
        }

        public void EndUse()
        {
            if (!charging) return;
            float speed = Mathf.Lerp(settings.PlayerThrowMinSpeed, settings.PlayerThrowMaxSpeed, Charge01);
            CancelUse();
            if (!input.GameplayActive) return;
            Vector3 velocity = presentation.AimPose.rotation * Vector3.forward * speed +
                motor.Body.linearVelocity * settings.PlayerThrowVelocityInheritance;
            float recovery = Mathf.Clamp(speed / Mathf.Max(0.01f, Physics.gravity.magnitude),
                settings.PlayerThrowRecoveryMin, settings.PlayerThrowRecoveryMax);
            RequestRelease(motor.Body.position, input.Yaw, velocity, recovery, false, ItemReleaseIntent.Throw);
        }

        public void CancelUse() => charging = false;

        public void Drop()
        {
            CancelUse();
            if (IsOwner && input.GameplayActive && !seating.TransitionPending)
                RequestRelease(motor.Body.position, input.Yaw, Vector3.zero, 0.2f, false);
        }

        internal void ImpactDrop(Vector3 origin, float yaw)
        {
            if (PredictionManager.IsReconciling || !IsCarrying || RequestPending) return;
            CancelUse();
            if (IsOwner || IsServerInitialized) RequestRelease(origin, yaw, Vector3.zero, 0.2f, true);
        }

        private void RequestRelease(Vector3 origin, float yaw, Vector3 velocity, float recovery, bool forced, ItemReleaseIntent intent = ItemReleaseIntent.Drop)
        {
            if (!IsCarrying || !Partner || RequestPending || SessionController.Instance.Phase == SessionPhase.Stopping) return;
            bool clear = Partner.seating.TryCarryPlacement(origin, yaw, capsule, forced, out var position);
            if (!clear && !forced) { seating.ShowFeedback("No clear space to release player."); return; }
            var release = new PlayerRelease { Position = position, Yaw = yaw, Velocity = velocity,
                Recovery = recovery, PlacementPending = !clear, Intent = intent };
            uint request = ++nextRequest;
            if (IsServerInitialized)
                AcceptRelease(request, Partner.ObjectId, motor.ControlRevision, Partner.motor.ControlRevision, release);
            else
            {
                pendingRequest = request;
                var notification = new CarryPresentationRelease(Partner, motor.ControlRevision + 1, request, release);
                PreparingRelease?.Invoke(notification);
                ReleaseStarted?.Invoke(notification);
                Partner.previewRelease = release;
                Partner.previewStart = Time.unscaledTime;
                Partner.SetPreview(!release.PlacementPending);
                Partner.UpdateAttachment();
                ServerRelease(request, Partner.ObjectId, motor.ControlRevision, Partner.motor.ControlRevision, release);
            }
        }

        [ServerRpc]
        private void ServerRelease(uint request, int partner, uint revision, uint partnerRevision, PlayerRelease release) =>
            AcceptRelease(request, partner, revision, partnerRevision, release);

        private void AcceptRelease(uint request, int partner, uint revision, uint partnerRevision, PlayerRelease release)
        {
            if (!IsCarrying || !Partner || Partner.ObjectId != partner || !Partner.IsCarried || Partner.Partner != this ||
                motor.ControlRevision != revision || Partner.motor.ControlRevision != partnerRevision ||
                !WorldItemRegistry.Finite(release.Position) || !WorldItemRegistry.Finite(release.Velocity) ||
                !float.IsFinite(release.Yaw) || !float.IsFinite(release.Recovery) || release.Recovery < 0f)
            { Complete(request, CarryRequestResult.Changed); return; }
            if (release.PlacementPending)
            {
                release.Position = Partner.motor.SpawnPoint;
                release.PlacementPending = !Partner.seating.CapsuleClear(release.Position, null);
            }
            Commit(new CarryTransition
            {
                Kind = CarryTransitionKind.Release, Carrier = ObjectId, Carried = partner,
                CarrierRevision = motor.ControlRevision + 1, CarriedRevision = Partner.motor.ControlRevision + 1,
                CarrierEffectReset = seating.EffectReset, CarriedEffectReset = Partner.seating.EffectReset,
                Generation = Partner.motor.ImpactGeneration + 1, Release = release, Immunity = settings.CarryImmunityDuration
            });
            Complete(request, CarryRequestResult.Completed);
        }

        private void Commit(CarryTransition transition)
        {
            ApplyTransition(transition);
            if (transition.Kind == CarryTransitionKind.Pickup)
                ObserversPickup(transition.Carrier, transition.Carried, transition.CarrierRevision, transition.CarriedRevision,
                    transition.Generation, transition.CarrierEffectReset, transition.CarriedEffectReset);
            else ObserversRelease(transition);
        }

        [ObserversRpc]
        private void ObserversPickup(int carrier, int carried, uint carrierRevision, uint carriedRevision, uint generation,
            uint carrierEffectReset, uint carriedEffectReset)
        {
            if (!IsServerInitialized) ApplyTransition(new CarryTransition
            {
                Kind = CarryTransitionKind.Pickup, Carrier = carrier, Carried = carried,
                CarrierRevision = carrierRevision, CarriedRevision = carriedRevision, Generation = generation,
                CarrierEffectReset = carrierEffectReset, CarriedEffectReset = carriedEffectReset
            });
        }

        [ObserversRpc]
        private void ObserversRelease(CarryTransition transition)
        {
            if (!IsServerInitialized) ApplyTransition(transition);
        }

        private static void ApplyTransition(CarryTransition transition)
        {
            bool pickup = transition.Kind == CarryTransitionKind.Pickup;
            var carrier = new PlayerControlTransition
            {
                Player = transition.Carrier, Cart = -1, Seat = -1, ControlRevision = transition.CarrierRevision,
                EffectReset = transition.CarrierEffectReset,
                ContextOnly = true, Role = pickup ? CarryRole.Carrying : CarryRole.Free,
                Partner = transition.Carried, Rotation = Quaternion.identity
            };
            if (Players.TryGetValue(transition.Carrier, out var source))
            {
                carrier.Revision = source.seating.Revision;
                carrier.Generation = source.motor.ImpactGeneration;
                carrier.Position = source.motor.Body.position;
                carrier.Rotation = source.motor.Body.rotation;
                carrier.Velocity = source.motor.Body.linearVelocity;
                carrier.Immunity = source.RemainingImmunity;
            }
            var carried = new PlayerControlTransition
            {
                Player = transition.Carried, Cart = -1, Seat = -1, ControlRevision = transition.CarriedRevision,
                EffectReset = transition.CarriedEffectReset,
                Generation = transition.Generation, Role = pickup ? CarryRole.Carried : CarryRole.Free,
                Partner = transition.Carrier, Position = transition.Release.Position,
                Rotation = Quaternion.Euler(0f, transition.Release.Yaw, 0f), Velocity = transition.Release.Velocity,
                Recovery = transition.Release.Recovery, PlacementPending = transition.Release.PlacementPending,
                CarryPlacement = true, Immunity = transition.Immunity
            };
            if (Players.TryGetValue(transition.Carried, out var target)) carried.Revision = target.seating.Revision;
            if (pickup && source)
            {
                carried.Position = source.motor.Body.position + source.motor.Body.rotation * source.settings.CarryOffset;
                carried.Rotation = source.motor.Body.rotation;
            }
            if (!pickup && (!presentationReleases.TryGetValue(transition.Carrier, out var queued) ||
                queued.CarrierRevision < transition.CarrierRevision) &&
                (!source || transition.CarrierRevision > source.motor.ControlRevision))
                presentationReleases[transition.Carrier] = transition;
            PlayerSeating.Receive(carrier, false);
            PlayerSeating.Receive(carried, false);
        }

        internal CarryPresentationRelease? PrepareControlRelease(uint revision)
        {
            if (!presentationReleases.TryGetValue(ObjectId, out var transition) || transition.CarrierRevision > revision) return null;
            presentationReleases.Remove(ObjectId);
            if (transition.CarrierRevision != revision || !IsCarrying || !Partner || Partner.ObjectId != transition.Carried) return null;
            var release = new CarryPresentationRelease(Partner, revision, pendingRequest, transition.Release);
            PreparingRelease?.Invoke(release);
            return release;
        }

        internal void CompleteControlRelease(CarryPresentationRelease? release)
        {
            if (release.HasValue) ReleaseStarted?.Invoke(release.Value);
        }

        private void Complete(uint request, CarryRequestResult result)
        {
            if (IsOwner) Finish(request, result);
            else if (Owner.IsActive) TargetResult(Owner, request, result);
        }

        [TargetRpc] private void TargetResult(NetworkConnection connection, uint request, CarryRequestResult result) => Finish(request, result);

        private void Finish(uint request, CarryRequestResult result)
        {
            if (request != nextRequest || !IsServerInitialized && pendingRequest != request) return;
            pendingRequest = 0;
            if (Partner && Partner.IsCarried && Partner.Partner == this)
            {
                Partner.SetPreview(false);
                Partner.UpdateAttachment();
            }
            if (result != CarryRequestResult.Completed)
            {
                ReleaseRejected?.Invoke(request);
                seating.ShowFeedback(result == CarryRequestResult.Unavailable ? "Player is no longer available." : "Carry changed before release.");
            }
        }

        private void LateUpdate()
        {
            if (IsCarried || preview) UpdateAttachment();
        }

        internal void RefreshPhysicalAttachment()
        {
            if (!IsCarried || !Partner || health.IsDowned) return;
            var body = Partner.motor.Body;
            var physical = Quaternion.Euler(0f, body.rotation.eulerAngles.y, 0f);
            motor.Body.position = body.position + physical * settings.CarryOffset;
            motor.Body.rotation = physical;
            transform.SetPositionAndRotation(motor.Body.position, motor.Body.rotation);
        }

        private void UpdateAttachment()
        {
            if (health.IsDowned) return;
            if (preview)
            {
                float elapsed = Mathf.Min(Time.unscaledTime - previewStart, 0.25f);
                var release = previewRelease;
                presentation.Graphics.SetPositionAndRotation(release.Position + release.Velocity * elapsed +
                    Physics.gravity * (0.5f * elapsed * elapsed), Quaternion.Euler(0f, release.Yaw, 0f));
                return;
            }
            if (!IsCarried || !Partner) return;
            RefreshPhysicalAttachment();
            var graphics = Partner.presentation.Graphics;
            var visual = Quaternion.Euler(0f, graphics.eulerAngles.y, 0f);
            presentation.Graphics.SetPositionAndRotation(graphics.position + visual * settings.CarryOffset, visual);
        }

        internal void BreakForReset()
        {
            if (!IsServerInitialized) return;
            var carrier = IsCarrying ? this : Partner;
            if (carrier && carrier.IsCarrying)
                carrier.RequestRelease(carrier.motor.Body.position, carrier.motor.Body.rotation.eulerAngles.y, Vector3.zero, 0.2f, true);
        }

        internal void ReleaseForLife()
        {
            if (!IsServerInitialized || !Partner) return;
            var survivor = Partner;
            var state = survivor.seating.CaptureCurrent();
            state.ControlRevision++;
            state.Generation++;
            state.Role = CarryRole.Free;
            state.Partner = -1;
            state.Velocity = survivor.health.CaptureVelocity;
            state.Position = survivor.health.LivingPosition;
            state.ContextOnly = survivor.IsCarrying;
            if (survivor.IsCarried)
            {
                state.PlacementPending = !survivor.seating.TryCarryPlacement(motor.Body.position,
                    motor.Body.rotation.eulerAngles.y, capsule, true, out state.Position);
                state.CarryPlacement = true;
            }
            PlayerSeating.Receive(state, false);
            survivor.ObserversSurvivor(state);
            Install(CarryRole.Free, -1, 0f);
        }

        private void Disconnect(NetworkConnection connection)
        {
            if (Owner == connection) ReleaseSurvivor();
        }

        private void ReleaseSurvivor()
        {
            if (!Partner || SessionController.Instance.Phase == SessionPhase.Stopping) return;
            var survivor = Partner;
            if (IsCarrying)
            {
                bool clear = survivor.seating.TryCarryPlacement(motor.Body.position, motor.Body.rotation.eulerAngles.y, capsule, true, out var position);
                var state = survivor.seating.CaptureCurrent();
                state.ControlRevision++;
                state.Generation++;
                state.Role = CarryRole.Free;
                state.Partner = -1;
                state.Position = position;
                state.Velocity = Vector3.zero;
                state.PlacementPending = !clear;
                state.CarryPlacement = true;
                state.Immunity = settings.CarryImmunityDuration;
                PlayerSeating.Receive(state, false);
                survivor.ObserversSurvivor(state);
            }
            else
            {
                var state = survivor.seating.CaptureCurrent();
                state.ControlRevision++;
                state.Role = CarryRole.Free;
                state.Partner = -1;
                state.ContextOnly = true;
                PlayerSeating.Receive(state, false);
                survivor.ObserversSurvivor(state);
            }
            Install(CarryRole.Free, -1, 0f);
        }

        [ObserversRpc] private void ObserversSurvivor(PlayerControlTransition state)
        {
            if (!IsServerInitialized) PlayerSeating.Receive(state, false);
        }

        public override void OnOwnershipServer(NetworkConnection previousOwner) => BreakForReset();
        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            CancelUse();
            pendingRequest = 0;
            SetPreview(false);
            if (Partner) Partner.SetPreview(false);
            if (IsOwner) Local = this;
            else if (Local == this) Local = null;
            PresentationContextChanged?.Invoke();
            if (Partner) Partner.PresentationContextChanged?.Invoke();
        }

        public override void OnStopServer()
        {
            ServerManager.Objects.OnPreDestroyClientObjects -= Disconnect;
            ReleaseSurvivor();
        }

        public override void OnStopNetwork()
        {
            if (Partner && Partner.Partner == this)
            {
                Partner.SetPreview(false);
                Partner.CancelUse();
                Partner.pendingRequest = 0;
                Partner.Partner = null;
                Partner.PresentationContextChanged?.Invoke();
            }
            presentationReleases.Remove(ObjectId);
            Players.Remove(ObjectId);
            if (Local == this) Local = null;
            Partner = null;
            Role = CarryRole.Free;
            SetPreview(false);
            charging = false;
            pendingRequest = 0;
        }
    }
}
