using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public enum SeatRequestResult : byte { Completed, Occupied, Busy, Blocked }

    [DefaultExecutionOrder(20)]
    public sealed class PlayerSeating : NetworkBehaviour
    {
        private sealed class Contact
        {
            public Vector3 CartPosition, PlayerPosition;
            public Quaternion CartRotation;
            public uint Epoch, Revision, Reset;
            public bool Touching;
        }
        internal static readonly Dictionary<int, PlayerSeating> Players = new();
        private static readonly Dictionary<int, (SeatTransition state, bool impulse)> unresolved = new();
        public static PlayerSeating Local { get; private set; }
        private readonly Collider[] query = new Collider[64];
        private readonly RaycastHit[] pathHits = new RaycastHit[64];
        private readonly Dictionary<int, Contact> contacts = new();
        private CapsuleCollider capsule;
        private PlayerInventory inventory;
        private PlayerPresentation presentation;
        private PlayerNetworkState networkState;
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
        public bool TransitionPending { get; private set; }
        public PlayerInputReader Input { get; private set; }
        public PlayerMotor Motor { get; private set; }
        public bool CanEquip => !IsDriver && !PlacementPending && !TransitionPending;
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
            capsule = GetComponent<CapsuleCollider>();
            clearanceMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("CartSeat", "ItemHeld", "PlayerItemHitbox");
            groundMask = clearanceMask & ~LayerMask.GetMask("Player", "GolfCart", "ItemWorld");
        }

        public override void OnStartNetwork()
        {
            Players[ObjectId] = this;
            Motor.PresentationCorrected += RebaseContacts;
            ResolvePending();
        }
        public override void OnStartClient() { if (IsOwner) Local = this; }
        public override void OnSpawnServer(NetworkConnection connection)
        {
            var state = current;
            state.Player = ObjectId;
            state.Seat = (sbyte)SeatIndex;
            state.Generation = Motor.ImpactGeneration;
            state.Position = Motor.Body.position;
            state.Rotation = Motor.Body.rotation;
            state.Velocity = Seated ? Vector3.zero : Motor.Body.linearVelocity;
            state.Ejection = Vector3.zero;
            TargetCurrent(connection, state);
        }
        [TargetRpc] private void TargetCurrent(NetworkConnection connection, SeatTransition state)
        {
            if (!IsServerInitialized) Receive(state, false);
        }

        internal static void Receive(SeatTransition state, bool impulse)
        {
            if (Players.TryGetValue(state.Player, out var player) && (state.Seat < 0 || GolfCartNetwork.Carts.ContainsKey(state.Cart)))
            {
                player.Apply(state, impulse);
                if (unresolved.TryGetValue(state.Player, out var queued) && queued.state.Revision <= state.Revision) unresolved.Remove(state.Player);
            }
            else if (!unresolved.TryGetValue(state.Player, out var old) || state.Revision > old.state.Revision)
                unresolved[state.Player] = (state, impulse);
        }

        internal static void ForgetCart(int id)
        {
            foreach (var player in Players.Values) player.contacts.Remove(id);
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
            if (!IsOwner || TransitionPending || PlacementPending || !Input.GameplayActive) return;
            TransitionPending = true;
            Input.ClearContext();
            if (IsServerInitialized) cart.Request(this, ++requestId, Revision, cart.StateRevision, cart.Epoch, destination);
            else ServerRequest(++requestId, Revision, cart.ObjectId, cart.StateRevision, cart.Epoch, destination);
        }

        public void RequestExit() { if (Seated) Request(Cart, -1); }
        [ServerRpc] private void ServerRequest(uint request, uint revision, int cartId, uint cartRevision, uint epoch, int destination)
        {
            if (GolfCartNetwork.Carts.TryGetValue(cartId, out var cart)) cart.Request(this, request, revision, cartRevision, epoch, destination);
            else CompleteRequest(request, SeatRequestResult.Busy);
        }
        internal void CompleteRequest(uint request, SeatRequestResult result = SeatRequestResult.Completed)
        {
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
                SeatRequestResult.Occupied => "That seat is occupied.",
                SeatRequestResult.Busy => "Cart is busy. Try again.",
                SeatRequestResult.Blocked => "No clear space to exit or recover the cart.",
                _ => ""
            };
            feedbackUntil = Time.unscaledTime + 3f;
        }

        private void Apply(SeatTransition state, bool impulse)
        {
            if (receivedState && state.Revision <= Revision) return;
            receivedState = true;
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
            Input.ClearContext();
            if (Seated)
            {
                lookOffset = 0f;
                seatYaw = Cart.Controller.Heading + (SeatIndex >= 2 ? 180f : 0f);
                var anchor = Cart.GetSeat(SeatIndex).Rider;
                state.Position = anchor.position;
                state.Rotation = anchor.rotation;
            }
            else Input.SetWorldYaw(worldYaw);
            Motor.ApplySeating(Seated || PlacementPending, state.Revision, state.Generation, state.Position, state.Rotation, state.Velocity);
            inventory.Hitbox.SetSuspended(Seated || PlacementPending);
            presentation.SetSeated(Seated || PlacementPending);
            if (IsDriver || wasDriver) inventory.ApplySeatPermissions();
            if (IsDriver) networkState.ClearChargingForSeat();
            if (impulse && !Seated && !PlacementPending && state.Ejection != Vector3.zero && IsOwner)
                Motor.SubmitWorldImpact(state.Ejection, 0.2f);
        }

        internal void AddLook(float yaw) => lookOffset = Mathf.Repeat(lookOffset + yaw + 180f, 360f) - 180f;
        private float UpdateSeatHeading()
        {
            Vector3 forward = Vector3.ProjectOnPlane(Cart.GetSeat(SeatIndex).Rider.forward, Vector3.up);
            if (forward.sqrMagnitude > 0.01f) seatYaw = Quaternion.LookRotation(forward).eulerAngles.y;
            return seatYaw;
        }
        internal Pose AimPose => new(Cart.GetSeat(SeatIndex).Eye.position, Quaternion.Euler(Input.Pitch, WorldYaw, 0f));
        public Vector3 PointVelocity
        {
            get
            {
                if (!Seated) return Motor.Body.linearVelocity;
                var frame = Cart.DisplayMotion;
                Vector3 center = frame.Position + frame.Rotation * Cart.Controller.Settings.CenterOfMass;
                return frame.Velocity + Vector3.Cross(frame.AngularVelocity, Cart.GetSeat(SeatIndex).Rider.position - center);
            }
        }

        private void LateUpdate()
        {
            if (!IsServerInitialized && !IsClientInitialized) return;
            if (Seated)
            {
                var anchor = Cart.GetSeat(SeatIndex).Rider;
                transform.SetPositionAndRotation(anchor.position, anchor.rotation);
                presentation.Graphics.SetPositionAndRotation(anchor.position, anchor.rotation);
            }
            if (PlacementPending && IsServerInitialized && Time.unscaledTime >= retryTime)
            {
                retryTime = Time.unscaledTime + 0.25f;
                if (TryExit(current.Position, current.Position, exitCart, true, null, out var position))
                {
                    var state = current;
                    state.Revision++;
                    state.Generation = Motor.ImpactGeneration + 1;
                    state.Position = position;
                    state.PlacementPending = false;
                    Apply(state, true);
                    ObserversPlacement(state);
                }
            }
            if (IsOwner && !Seated && !PlacementPending && !PredictionManager.IsReconciling) SampleCartContacts();
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

        private bool CapsuleClear(Vector3 position, List<Vector3> reserved)
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
        private void CapsulePoints(Vector3 position, out Vector3 bottom, out Vector3 top)
        {
            Vector3 center = position + capsule.center;
            float half = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
            bottom = center - Vector3.up * half;
            top = center + Vector3.up * half;
        }

        private void RebaseContacts(Vector3 correction)
        {
            foreach (var contact in contacts.Values) contact.PlayerPosition += correction;
        }

        private void SampleCartContacts()
        {
            Vector3 playerPosition = presentation.Graphics.position;
            foreach (var entry in GolfCartNetwork.Carts)
            {
                var cart = entry.Value;
                var frame = cart.DisplayMotion;
                bool fresh = !contacts.TryGetValue(entry.Key, out var contact);
                if (fresh) contacts[entry.Key] = contact = new Contact();
                fresh |= contact.Epoch != cart.Epoch || contact.Revision != Revision || contact.Reset != Motor.ResetRevision;
                bool touching = CartOverlap(cart, playerPosition, frame.Position, frame.Rotation, out var normal, 0.08f);
                bool ignored = exitCart == cart;
                if (!fresh && !contact.Touching && !ignored)
                {
                    float relativeDistance = ((frame.Position - contact.CartPosition) - (playerPosition - contact.PlayerPosition)).magnitude;
                    int steps = Mathf.Max(1, Mathf.CeilToInt(relativeDistance / (capsule.radius * 0.5f)),
                        Mathf.CeilToInt(Quaternion.Angle(frame.Rotation, contact.CartRotation) / 4f));
                    for (int i = 0; i <= steps; i++)
                    {
                        float t = (float)i / steps;
                        Vector3 point = Vector3.Lerp(contact.PlayerPosition, playerPosition, t);
                        if (!CartOverlap(cart, point, Vector3.Lerp(contact.CartPosition, frame.Position, t),
                            Quaternion.Slerp(contact.CartRotation, frame.Rotation, t), out normal, 0f)) continue;
                        Vector3 center = frame.Position + frame.Rotation * cart.Controller.Settings.CenterOfMass;
                        Vector3 velocity = frame.Velocity + Vector3.Cross(frame.AngularVelocity, point - center);
                        float closing = Mathf.Max(0f, Vector3.Dot(velocity - Motor.Body.linearVelocity, normal));
                        var settings = cart.Controller.Settings;
                        if (closing >= settings.MinimumHitSpeed)
                            Motor.SubmitWorldImpact((normal + Vector3.up * settings.HitLift) * closing * settings.CollisionMultiplier, 0.2f);
                        touching = true;
                        break;
                    }
                }
                if (ignored && !touching) exitCart = null;
                contact.CartPosition = frame.Position;
                contact.CartRotation = frame.Rotation;
                contact.PlayerPosition = playerPosition;
                contact.Epoch = cart.Epoch;
                contact.Revision = Revision;
                contact.Reset = Motor.ResetRevision;
                contact.Touching = touching;
            }
        }

        private bool CartOverlap(GolfCartNetwork cart, Vector3 playerPosition, Vector3 cartPosition, Quaternion cartRotation,
            out Vector3 normal, float margin)
        {
            normal = Vector3.zero;
            foreach (var box in cart.Controller.Chassis)
            {
                Vector3 local = cart.transform.InverseTransformPoint(box.transform.position);
                Quaternion rotation = cartRotation * Quaternion.Inverse(cart.transform.rotation) * box.transform.rotation;
                if (Physics.ComputePenetration(capsule, playerPosition, Quaternion.identity, box,
                    cartPosition + cartRotation * local, rotation, out normal, out _)) return true;
                if (margin <= 0f) continue;
                Vector3 toPlayer = (playerPosition - cartPosition).normalized * margin;
                if (Physics.ComputePenetration(capsule, playerPosition - toPlayer, Quaternion.identity, box,
                    cartPosition + cartRotation * local, rotation, out normal, out _)) return true;
            }
            return false;
        }

        public override void OnStopNetwork()
        {
            Players.Remove(ObjectId);
            Motor.PresentationCorrected -= RebaseContacts;
            unresolved.Remove(ObjectId);
            if (Local == this) Local = null;
            contacts.Clear();
        }
    }
}
