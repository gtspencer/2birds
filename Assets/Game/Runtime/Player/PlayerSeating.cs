using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public enum SeatRequestResult : byte { Completed, Occupied, Busy, Blocked, NoPairSeat }

    [DefaultExecutionOrder(20)]
    public sealed class PlayerSeating : NetworkBehaviour
    {
        internal static readonly Dictionary<int, PlayerSeating> Players = new();
        private static readonly Dictionary<int, (PlayerControlTransition state, bool impulse)> unresolved = new();
        public static PlayerSeating Local { get; private set; }
        private readonly RaycastHit[] pathHits = new RaycastHit[64];
        private CapsuleCollider capsule;
        private PlayerInventory inventory;
        private PlayerPresentation presentation;
        private PlayerNetworkState networkState;
        internal PlayerCarry Carry { get; private set; }
        internal bool ServerRequestPending { get; set; }
        private PlayerControlTransition current;
        private GolfCartNetwork exitCart;
        private uint requestId;
        private float retryTime, seatYaw, lookOffset;
        private int clearanceMask;
        private PlayerPlacement placement;
        internal PlayerHealth Health { get; private set; }
        internal bool CanGameplayActions => networkState.CanGameplayActions;
        private bool receivedState;
        private string requestFeedback;
        private float feedbackUntil;
        public string RequestFeedback => Time.unscaledTime < feedbackUntil ? requestFeedback : "";
        public GolfCartNetwork Cart { get; private set; }
        public int SeatIndex { get; private set; } = -1;
        public uint Revision { get; private set; }
        internal uint EffectReset => inventory.Effects.Reset;
        public bool Seated => Cart != null && SeatIndex >= 0;
        public bool IsDriver => Seated && SeatIndex == 0;
        public bool PlacementPending { get; private set; }
        public bool AwaitingReference { get; private set; }
        public bool TransitionPending { get; private set; }
        public event System.Action PresentationContextChanged;
        public float AttachmentLookYaw => lookOffset;
        public PlayerInputReader Input { get; private set; }
        public PlayerMotor Motor { get; private set; }
        public bool CanEquip => CanGameplayActions && !AwaitingReference && (!Carry || !Carry.IsCarried) && !IsDriver && !PlacementPending && !TransitionPending;
        internal float WorldYaw => Seated ? UpdateSeatHeading() + lookOffset : Input.Yaw;

        private void Awake()
        {
            current.Cart = -1;
            current.Seat = -1;
            Motor = GetComponent<PlayerMotor>();
            Input = GetComponent<PlayerInputReader>();
            inventory = GetComponent<PlayerInventory>();
            presentation = GetComponent<PlayerPresentation>();
            networkState = GetComponent<PlayerNetworkState>();
            Carry = GetComponent<PlayerCarry>();
            capsule = GetComponent<CapsuleCollider>();
            clearanceMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("CartSeat", "ItemHeld", "PlayerItemHitbox", "BirdBody", "BirdQuery");
            clearanceMask &= ~LayerMask.GetMask("PlayerRagdoll", "PlayerEffectReceiver");
            placement = GetComponent<PlayerPlacement>();
            Health = GetComponent<PlayerHealth>();
        }

        public override void OnStartNetwork()
        {
            Players[ObjectId] = this;
            ResolvePending();
        }
        public override void OnStartClient() { if (IsOwner) Local = this; }
        public override void OnSpawnServer(NetworkConnection connection)
        {
            var state = CaptureCurrent();
            state.ItemAction = networkState.ItemAction;
            TargetCurrent(connection, state);
        }

        internal PlayerControlTransition CaptureCurrent()
        {
            var state = current;
            state.Player = ObjectId;
            state.Revision = Revision;
            state.ControlRevision = Motor.ControlRevision;
            state.Cart = Cart ? Cart.ObjectId : -1;
            state.Seat = (sbyte)SeatIndex;
            state.Generation = Motor.ImpactGeneration;
            state.EffectReset = EffectReset;
            state.Position = Motor.Body.position;
            state.Rotation = Motor.Body.rotation;
            state.Velocity = Motor.Suspended ? Vector3.zero : Motor.Body.linearVelocity;
            state.Ejection = Vector3.zero;
            state.CrashDamage = false;
            state.Recovery = Motor.RemainingRecovery;
            state.ContextOnly = false;
            state.ItemAction = default;
            state.Role = Carry ? Carry.Role : CarryRole.Free;
            state.Partner = Carry && Carry.Partner ? Carry.Partner.ObjectId : -1;
            state.Immunity = Carry ? Carry.RemainingImmunity : 0f;
            return state;
        }
        [TargetRpc] private void TargetCurrent(NetworkConnection connection, PlayerControlTransition state)
        {
            if (IsServerInitialized) return;
            if (receivedState && state.ControlRevision == current.ControlRevision && !AwaitingReference)
                networkState.ApplyControlState(state.ItemAction);
            else Receive(state, false);
        }

        internal static void Receive(PlayerControlTransition state, bool impulse)
        {
            if (Players.TryGetValue(state.Player, out var player))
            {
                if (!player.networkState.LifeReady)
                {
                    if (!unresolved.TryGetValue(state.Player, out var queued) || queued.state.ControlRevision <= state.ControlRevision)
                        unresolved[state.Player] = (state, impulse);
                    return;
                }
                if (player.receivedState && state.ControlRevision <= player.current.ControlRevision) return;
                if (unresolved.TryGetValue(state.Player, out var newer) && newer.state.ControlRevision > state.ControlRevision) return;
                bool ready = (state.Seat < 0 || GolfCartNetwork.Carts.ContainsKey(state.Cart)) &&
                    (state.Role == CarryRole.Free || PlayerCarry.Players.ContainsKey(state.Partner));
                if (ready)
                {
                    player.Apply(state, impulse);
                    unresolved.Remove(state.Player);
                    return;
                }
                player.AwaitingReference = true;
                player.Motor.ApplyPlacement(true, state.ControlRevision, state.Generation, state.Position, state.Rotation, Vector3.zero);
                player.inventory.Hitbox.SetSuspended(true);
                player.presentation.SetSeated(true);
                player.Input.ClearContext();
                player.PresentationContextChanged?.Invoke();
            }
            if (!unresolved.TryGetValue(state.Player, out var old) || state.ControlRevision >= old.state.ControlRevision)
                unresolved[state.Player] = (state, impulse);
        }

        internal static void ForgetCart(int id)
        {
            foreach (var player in Players.Values) player.Motor.ForgetCartContact(id);
            foreach (int playerId in new List<int>(unresolved.Keys))
                if (unresolved[playerId].state.Cart == id) unresolved.Remove(playerId);
        }

        internal static void ResolvePending()
        {
            foreach (int id in new List<int>(unresolved.Keys))
            {
                var entry = unresolved[id];
                Receive(entry.state, entry.impulse);
            }
        }

        internal void Request(GolfCartNetwork cart, int destination)
        {
            if (!CanGameplayActions || !IsOwner || AwaitingReference || TransitionPending || PlacementPending || !Input.GameplayActive ||
                Carry && (Carry.IsCarried || Carry.RequestPending)) return;
            int partner = Carry && Carry.IsCarrying && Carry.Partner ? Carry.Partner.ObjectId : -1;
            uint partnerRevision = partner >= 0 ? Carry.Partner.Seating.Motor.ControlRevision : 0;
            TransitionPending = true;
            Input.ClearContext();
            inventory.ApplyControlPermissions();
            PresentationContextChanged?.Invoke();
            if (IsServerInitialized) cart.Request(this, ++requestId, Revision, Motor.ControlRevision, cart.StateRevision, cart.Epoch, destination, partner, partnerRevision);
            else ServerRequest(++requestId, Revision, Motor.ControlRevision, cart.ObjectId, cart.StateRevision, cart.Epoch, destination, partner, partnerRevision);
        }

        public void RequestExit() { if (Seated) Request(Cart, -1); }
        [ServerRpc] private void ServerRequest(uint request, uint revision, uint controlRevision, int cartId, uint cartRevision, uint epoch, int destination, int partner, uint partnerRevision)
        {
            if (GolfCartNetwork.Carts.TryGetValue(cartId, out var cart)) cart.Request(this, request, revision, controlRevision, cartRevision, epoch, destination, partner, partnerRevision);
            else CompleteRequest(request, SeatRequestResult.Busy);
        }
        internal void CompleteRequest(uint request, SeatRequestResult result = SeatRequestResult.Completed)
        {
            ServerRequestPending = false;
            if (IsOwner) FinishRequest(request, result);
            else if (Owner.IsActive) TargetRequest(Owner, request, result);
        }
        [TargetRpc] private void TargetRequest(NetworkConnection connection, uint request, SeatRequestResult result) => FinishRequest(request, result);
        private void FinishRequest(uint request, SeatRequestResult result)
        {
            if (request != requestId) return;
            TransitionPending = false;
            Input.ClearContext();
            inventory.ApplyControlPermissions();
            PresentationContextChanged?.Invoke();
            requestFeedback = result switch
            {
                SeatRequestResult.NoPairSeat => "No seat for carried player.",
                SeatRequestResult.Occupied => "That seat is occupied.",
                SeatRequestResult.Busy => "Cart is busy. Try again.",
                SeatRequestResult.Blocked => "No clear space to exit or recover the cart.",
                _ => ""
            };
            feedbackUntil = Time.unscaledTime + 3f;
        }

        private void Apply(PlayerControlTransition state, bool impulse)
        {
            if (receivedState && state.ControlRevision <= current.ControlRevision) return;
            receivedState = true;
            AwaitingReference = false;
            float worldYaw = WorldYaw;
            PlayerRagdollSeed? crashSeed = impulse && state.CrashDamage && IsOwner && Health.IsAlive
                ? Health.CaptureDownSeed(state.Velocity + state.Ejection) : null;

            Pose? releasePreview = Carry && Carry.ReleasePreview && state.Role == CarryRole.Free &&
                state.Seat < 0 && !state.PlacementPending ? new Pose(presentation.Graphics.position, presentation.Graphics.rotation) : null;
            exitCart = Cart;
            inventory.Effects.ResetEffects(state.EffectReset);
            state.EffectReset = EffectReset;
            current = state;
            Revision = state.Revision;
            SeatIndex = state.Seat;
            PlacementPending = state.PlacementPending;
            Cart = state.Seat >= 0 ? GolfCartNetwork.Carts[state.Cart] : null;
            if (state.Seat < 0 && exitCart == null) GolfCartNetwork.Carts.TryGetValue(state.Cart, out exitCart);
            TransitionPending = false;
            ServerRequestPending = false;
            if (Carry) Carry.Install(state.Role, state.Partner, state.Immunity);
            if (!Health.PreserveDownInput) Input.ClearContext();
            if (Seated)
            {
                lookOffset = 0f;
                seatYaw = Cart.Controller.Heading + (SeatIndex >= 2 ? 180f : 0f);
                var anchor = Cart.GetSeat(SeatIndex).PhysicalRider;
                state.Position = anchor.position;
                state.Rotation = anchor.rotation;
            }
            else Input.SetWorldYaw(worldYaw);
            bool suspended = Health.IsDowned || Seated || PlacementPending || Carry && Carry.IsCarried;
            if (state.ContextOnly && !Motor.Suspended && Health.IsAlive) Motor.SetControlRevision(state.ControlRevision);
            else
            {
                Motor.ApplyPlacement(suspended, state.ControlRevision, state.Generation, state.Position, state.Rotation, state.Velocity,
                    state.Recovery > 0f ? state.Recovery : 0.2f);
                inventory.Hitbox.SetSuspended(suspended);
                presentation.SetSeated(suspended, releasePreview);
            }
            if (!suspended && !state.ContextOnly) Motor.SuppressExitLaunch(exitCart);
            inventory.ApplyControlPermissions();

            networkState.ApplyControlState(state.ItemAction);
            if (impulse && state.CrashDamage && IsOwner && Health.IsAlive)
            {
                current.CrashDamage = false;
                Health.ApplyDamage(Health.Settings.CrashEjectionDamage, Vector3.zero, crashSeed);
            }
            if (impulse && Health.IsAlive && !Seated && !PlacementPending && state.Ejection != Vector3.zero && IsOwner)
                Motor.SubmitWorldImpact(state.Ejection, 0.2f);
            PresentationContextChanged?.Invoke();
        }

        internal void ShowFeedback(string message)
        {
            requestFeedback = message;
            feedbackUntil = Time.unscaledTime + 3f;
        }

        internal bool TryCarryPlacement(Vector3 origin, float yaw, CapsuleCollider carrier, bool forced, out Vector3 position)
        {
            float spacing = capsule.radius + (carrier ? carrier.radius : capsule.radius) + 0.12f;
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 start = origin + Vector3.up * 0.06f;
            for (int i = 0; i < 5; i++)
            {
                float angle = i switch { 0 => 0f, 1 => 45f, 2 => -45f, 3 => 90f, _ => -90f };
                position = start + rotation * (Quaternion.Euler(0f, angle, 0f) * Vector3.forward) * spacing;
                if (!CapsuleClear(position, null)) continue;
                Vector3 travel = position - start;
                CapsulePoints(start, out var bottom, out var top);
                int count = Physics.CapsuleCastNonAlloc(bottom, top, capsule.radius, travel.normalized, pathHits,
                    travel.magnitude, clearanceMask, QueryTriggerInteraction.Ignore);
                bool clear = count < pathHits.Length;
                for (int j = 0; j < count && clear; j++)
                    if (pathHits[j].collider != capsule && pathHits[j].collider != carrier) clear = false;
                if (clear) return true;
            }
            position = forced && IsServerInitialized ? Motor.SpawnPoint : origin;
            return forced && IsServerInitialized && CapsuleClear(position, null);
        }

        internal void AddLook(float yaw) => lookOffset = Mathf.Repeat(lookOffset + yaw + 180f, 360f) - 180f;
        private float UpdateSeatHeading()
        {
            Vector3 forward = Vector3.ProjectOnPlane((Cart.GetSeat(SeatIndex).VisualRider.rotation * Vector3.forward), Vector3.up);
            if (forward.sqrMagnitude > 0.01f) seatYaw = Quaternion.LookRotation(forward).eulerAngles.y;
            return seatYaw;
        }
        internal Pose AimPose => new(Cart.GetSeat(SeatIndex).VisualEye.position, Quaternion.Euler(Input.Pitch, WorldYaw, 0f));
        public Vector3 PointVelocity
        {
            get
            {
                if (!Seated) return Motor.Body.linearVelocity;
                return Cart.Controller.Body.GetPointVelocity(Cart.GetSeat(SeatIndex).PhysicalRider.position);
            }
        }

        internal void RefreshPhysicalAttachment()
        {
            if (!Seated || Health.IsDowned) return;
            var physical = Cart.GetSeat(SeatIndex).PhysicalRider;
            Motor.Body.position = physical.position;
            Motor.Body.rotation = physical.rotation;
            transform.SetPositionAndRotation(physical.position, physical.rotation);
        }

        private void LateUpdate()
        {
            if (!IsServerInitialized && !IsClientInitialized) return;
            if (Seated && Health.IsAlive)
            {
                var seat = Cart.GetSeat(SeatIndex);
                RefreshPhysicalAttachment();
                var visual = seat.VisualRider;
                presentation.Graphics.SetPositionAndRotation(visual.position, visual.rotation);
            }
            if (PlacementPending && IsServerInitialized && Time.unscaledTime >= retryTime)
            {
                retryTime = Time.unscaledTime + 0.25f;
                Vector3 position;
                bool clear = current.LifePlacement ? placement.TrySpawn(Motor.SpawnPoint, out position) :
                    current.CarryPlacement ? TryCarryPlacement(current.Position, current.Rotation.eulerAngles.y, null, true, out position) :
                    TryExit(current.Position, null, out position);
                if (clear)
                {
                    var state = current;
                    state.Revision++;
                    state.ControlRevision = Motor.ControlRevision + 1;
                    state.ContextOnly = false;
                    state.Generation = Motor.ImpactGeneration + 1;
                    state.Position = position;
                    state.PlacementPending = false;
                    state.CrashDamage = false;
                    state.Immunity = Carry ? Carry.RemainingImmunity : 0f;
                    Apply(state, true);
                    ObserversPlacement(state);
                }
            }
        }
        [ObserversRpc] private void ObserversPlacement(PlayerControlTransition state) { if (!IsServerInitialized) Receive(state, true); }

        internal bool TryExit(Vector3 desired, List<Vector3> reserved, out Vector3 position)
        {
            return TryExitNear(desired, reserved, out position) ||
                TryExitNear(Motor.SpawnPoint, reserved, out position);
        }

        private bool TryExitNear(Vector3 desired, List<Vector3> reserved, out Vector3 position) => placement.TryExitNear(desired, reserved, out position);
        internal bool CapsuleClear(Vector3 position, List<Vector3> reserved) => placement.CapsuleClear(position, reserved);
        internal void CapsulePoints(Vector3 position, out Vector3 bottom, out Vector3 top) => placement.CapsulePoints(position, out bottom, out top);

        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            if (IsOwner) Local = this;
            else if (Local == this) Local = null;
        }

        public override void OnStopNetwork()
        {
            Players.Remove(ObjectId);
            unresolved.Remove(ObjectId);
            foreach (int id in new List<int>(unresolved.Keys))
                if (unresolved[id].state.Role != CarryRole.Free && unresolved[id].state.Partner == ObjectId) unresolved.Remove(id);
            if (Local == this) Local = null;
        }
    }
}
