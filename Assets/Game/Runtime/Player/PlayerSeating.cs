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
        private static readonly Dictionary<int, (SeatTransition state, bool impulse)> unresolved = new();
        public static PlayerSeating Local { get; private set; }
        private readonly Collider[] query = new Collider[64];
        private readonly RaycastHit[] pathHits = new RaycastHit[64];
        private CapsuleCollider capsule;
        private PlayerInventory inventory;
        private PlayerPresentation presentation;
        private PlayerNetworkState networkState;
        internal PlayerCarry Carry { get; private set; }
        internal bool ServerRequestPending { get; set; }
        private SeatTransition current;
        private GolfCartNetwork exitCart;
        private uint requestId;
        private float retryTime, seatYaw, lookOffset;
        private int clearanceMask, groundMask;
        private bool receivedState;
        private string requestFeedback;
        private float feedbackUntil;
        public string RequestFeedback => Time.unscaledTime < feedbackUntil ? requestFeedback : "";
        public GolfCartNetwork Cart { get; private set; }
        public int SeatIndex { get; private set; } = -1;
        public uint Revision { get; private set; }
        public bool Seated => Cart != null && SeatIndex >= 0;
        public bool IsDriver => Seated && SeatIndex == 0;
        public bool PlacementPending { get; private set; }
        public bool AwaitingReference { get; private set; }
        public bool TransitionPending { get; private set; }
        public PlayerInputReader Input { get; private set; }
        public PlayerMotor Motor { get; private set; }
        public bool CanEquip => !AwaitingReference && (!Carry || !Carry.IsCarried) && !IsDriver && !PlacementPending && !TransitionPending;
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
            groundMask = clearanceMask & ~LayerMask.GetMask("Player", "GolfCart", "ItemWorld");
        }

        public override void OnStartNetwork()
        {
            Players[ObjectId] = this;
            ResolvePending();
        }
        public override void OnStartClient() { if (IsOwner) Local = this; }
        public override void OnSpawnServer(NetworkConnection connection)
        {
            TargetCurrent(connection, CaptureCurrent());
        }

        internal SeatTransition CaptureCurrent()
        {
            var state = current;
            state.Player = ObjectId;
            state.Revision = Revision;
            state.ControlRevision = Motor.ControlRevision;
            state.Cart = Cart ? Cart.ObjectId : -1;
            state.Seat = (sbyte)SeatIndex;
            state.Generation = Motor.ImpactGeneration;
            state.Position = Motor.Body.position;
            state.Rotation = Motor.Body.rotation;
            state.Velocity = Motor.Suspended ? Vector3.zero : Motor.Body.linearVelocity;
            state.Ejection = Vector3.zero;
            state.Recovery = Motor.RemainingRecovery;
            state.ContextOnly = false;
            state.Role = Carry ? Carry.Role : CarryRole.Free;
            state.Partner = Carry && Carry.Partner ? Carry.Partner.ObjectId : -1;
            state.Immunity = Carry ? Carry.RemainingImmunity : 0f;
            return state;
        }
        [TargetRpc] private void TargetCurrent(NetworkConnection connection, SeatTransition state)
        {
            if (!IsServerInitialized) Receive(state, false);
        }

        internal static void Receive(SeatTransition state, bool impulse)
        {
            if (Players.TryGetValue(state.Player, out var player))
            {
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
            if (!IsOwner || AwaitingReference || TransitionPending || PlacementPending || !Input.GameplayActive ||
                Carry && (Carry.IsCarried || Carry.RequestPending)) return;
            int partner = Carry && Carry.IsCarrying && Carry.Partner ? Carry.Partner.ObjectId : -1;
            uint partnerRevision = partner >= 0 ? Carry.Partner.Seating.Motor.ControlRevision : 0;
            TransitionPending = true;
            Input.ClearContext();
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

        private void Apply(SeatTransition state, bool impulse)
        {
            if (receivedState && state.ControlRevision <= current.ControlRevision) return;
            receivedState = true;
            AwaitingReference = false;
            float worldYaw = WorldYaw;
            bool wasDriver = IsDriver;
            exitCart = Cart;
            current = state;
            Revision = state.Revision;
            SeatIndex = state.Seat;
            PlacementPending = state.PlacementPending;
            Cart = state.Seat >= 0 ? GolfCartNetwork.Carts[state.Cart] : null;
            if (state.Seat < 0 && exitCart == null) GolfCartNetwork.Carts.TryGetValue(state.Cart, out exitCart);
            TransitionPending = false;
            ServerRequestPending = false;
            if (Carry) Carry.Install(state.Role, state.Partner, state.Immunity);
            Input.ClearContext();
            if (Seated)
            {
                lookOffset = 0f;
                seatYaw = Cart.Controller.Heading + (SeatIndex >= 2 ? 180f : 0f);
                var anchor = Cart.GetSeat(SeatIndex).PhysicalRider;
                state.Position = anchor.position;
                state.Rotation = anchor.rotation;
            }
            else Input.SetWorldYaw(worldYaw);
            bool suspended = Seated || PlacementPending || Carry && Carry.IsCarried;
            if (state.ContextOnly && !Motor.Suspended) Motor.SetControlRevision(state.ControlRevision);
            else
            {
                Motor.ApplyPlacement(suspended, state.ControlRevision, state.Generation, state.Position, state.Rotation, state.Velocity,
                    state.Recovery > 0f ? state.Recovery : 0.2f);
                inventory.Hitbox.SetSuspended(suspended);
                presentation.SetSeated(suspended);
            }
            if (!suspended && !state.ContextOnly) Motor.SuppressExitLaunch(exitCart);
            inventory.ApplyControlPermissions();
            if (IsDriver || wasDriver) inventory.ApplySeatPermissions();
            networkState.ClearChargingForSeat();
            if (impulse && !Seated && !PlacementPending && state.Ejection != Vector3.zero && IsOwner)
                Motor.SubmitWorldImpact(state.Ejection, 0.2f);
        }

        internal void ResetToSpawn()
        {
            var state = CaptureCurrent();
            state.Role = CarryRole.Free;
            state.Partner = -1;
            state.ControlRevision++;
            state.Generation++;
            state.Position = Motor.SpawnPoint;
            state.Rotation = Quaternion.identity;
            state.Velocity = Vector3.zero;
            state.PlacementPending = !CapsuleClear(state.Position, null);
            state.CarryPlacement = true;
            Apply(state, false);
            ObserversPlacement(state);
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
            position = Motor.SpawnPoint;
            return forced && CapsuleClear(position, null);
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

        private void LateUpdate()
        {
            if (!IsServerInitialized && !IsClientInitialized) return;
            if (Seated)
            {
                var seat = Cart.GetSeat(SeatIndex);
                var physical = seat.PhysicalRider;
                Motor.Body.position = physical.position;
                Motor.Body.rotation = physical.rotation;
                var visual = seat.VisualRider;
                presentation.Graphics.SetPositionAndRotation(visual.position, visual.rotation);
            }
            if (PlacementPending && IsServerInitialized && Time.unscaledTime >= retryTime)
            {
                retryTime = Time.unscaledTime + 0.25f;
                if (current.CarryPlacement ? TryCarryPlacement(current.Position, current.Rotation.eulerAngles.y, null, true, out var position) :
                    TryExit(current.Position, current.Position, exitCart, true, null, out position))
                {
                    var state = current;
                    state.Revision++;
                    state.ControlRevision = Motor.ControlRevision + 1;
                    state.ContextOnly = false;
                    state.Generation = Motor.ImpactGeneration + 1;
                    state.Position = position;
                    state.PlacementPending = false;
                    state.Immunity = Carry ? Carry.RemainingImmunity : 0f;
                    Apply(state, true);
                    ObserversPlacement(state);
                }
            }
        }
        [ObserversRpc] private void ObserversPlacement(SeatTransition state) { if (!IsServerInitialized) Receive(state, true); }

        internal bool TryExit(Vector3 origin, Vector3 desired, GolfCartNetwork source, bool forced, List<Vector3> reserved, out Vector3 position)
        {
            position = desired;
            if (SupportedExit(origin, desired, source, reserved, out position)) return true;
            int rings = forced ? 5 : 4;
            for (int ring = 0; ring < rings; ring++)
            {
                float radius = ring == 0 ? 1f : Mathf.Pow(2f, ring);
                for (int step = 0; step < 16; step++)
                {
                    float angle = step * Mathf.PI / 8f;
                    Vector3 candidate = desired + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                    if (SupportedExit(origin, candidate, source, reserved, out position)) return true;
                }
            }
            position = Motor.SpawnPoint;
            return forced && CapsuleClear(position, reserved);
        }

        private bool SupportedExit(Vector3 origin, Vector3 candidate, GolfCartNetwork source, List<Vector3> reserved, out Vector3 position)
        {
            position = candidate;
            if (!Physics.Raycast(candidate + Vector3.up * 1.5f, Vector3.down, out var ground, 6f, groundMask, QueryTriggerInteraction.Ignore) ||
                Vector3.Angle(ground.normal, Vector3.up) > 45f || ground.point.y > candidate.y + 0.5f) return false;
            position.y = ground.point.y + capsule.height * 0.5f - capsule.center.y + 0.05f;
            if (!CapsuleClear(position, reserved)) return false;
            Vector3 travel = position - origin;
            CapsulePoints(origin, out var bottom, out var top);
            int count = Physics.CapsuleCastNonAlloc(bottom, top, capsule.radius, travel.normalized, pathHits,
                travel.magnitude, clearanceMask, QueryTriggerInteraction.Ignore);
            if (count == pathHits.Length) return false;
            for (int i = 0; i < count; i++)
                if (pathHits[i].collider != capsule && (source == null || pathHits[i].rigidbody != source.Controller.Body)) return false;
            return true;
        }

        internal bool CapsuleClear(Vector3 position, List<Vector3> reserved)
        {
            if (reserved != null)
                foreach (var other in reserved)
                    if ((position - other).sqrMagnitude < capsule.height * capsule.height) return false;
            CapsulePoints(position, out var bottom, out var top);
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, capsule.radius + 0.03f, query, clearanceMask, QueryTriggerInteraction.Ignore);
            if (count == query.Length) return false;
            for (int i = 0; i < count; i++) if (query[i] != capsule) return false;
            return true;
        }
        internal void CapsulePoints(Vector3 position, out Vector3 bottom, out Vector3 top)
        {
            Vector3 center = position + capsule.center;
            float half = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
            bottom = center - Vector3.up * half;
            top = center + Vector3.up * half;
        }

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
