using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;
using FishNet.Utility.Template;

namespace TwoBirds
{
    public enum CartRecovery : byte { None, Flipped, Stuck }
    public struct CartOccupant
    {
        public int Player;
        public uint Revision, Generation;
    }
    public struct SeatTransition
    {
        public int Player, Cart;
        public sbyte Seat;
        public uint Revision, Generation;
        public Vector3 Position, Velocity, Ejection;
        public Quaternion Rotation;
        public bool PlacementPending;
    }

    [RequireComponent(typeof(GolfCartController))]
    public sealed partial class GolfCartNetwork : TickNetworkBehaviour
    {
        private sealed class PendingChange
        {
            public PlayerSeating Player;
            public int Seat;
            public uint Request, PlayerRevision;
            public bool Eject, Recover;
            public CartRecovery Recovery;
            public Vector3[] RiderVelocities;
        }

        internal static readonly Dictionary<int, GolfCartNetwork> Carts = new();
        [SerializeField] private Color bodyColor = new(0.08f, 0.35f, 0.85f);
        private bool lightsOn;
        private float hornCooldown;
        private GolfCartController controller;
        private GolfCartPresentation presentation;
        private CartSeat[] seats;
        private CartOccupant[] occupants = EmptySeats();
        private PendingChange pending;
        private byte disconnectedSeats;
        private uint epoch;
        private readonly List<Vector3> reservedExits = new();
        public CartRecovery Recovery { get; private set; }
        public uint StateRevision { get; private set; }
        public bool Busy => pending != null;
        public bool SimulatesPhysics => baselineReady && !controller.Body.isKinematic &&
            NetworkObject.RigidbodyPauser?.Paused != true && !(PredictionManager.IsReconciling && rejectReplay);
        public bool ReportsWorldEffects => SimulatesPhysics && !PredictionManager.IsReconciling &&
            (inputOwner < 0 ? IsServerInitialized : IsOwner && OwnerId == inputOwner);
        public uint Epoch => epoch;
        public int DriverId => occupants[0].Player;
        public GolfCartController Controller => controller;
        internal Vector3 RecoveryOrigin { get; private set; }
        public CartMotion DisplayMotion
        {
            get
            {
                var frame = controller.Capture(epoch, TimeManager.LocalTick);
                frame.Position = presentation.Graphics.position;
                frame.Rotation = presentation.Graphics.rotation;
                return frame;
            }
        }
        internal Pose VisualPose(Pose local) => new(presentation.Graphics.TransformPoint(local.position),
            presentation.Graphics.rotation * local.rotation);
        public CartSeat GetSeat(int index) => seats[index];
        public bool IsOccupied(int index) => occupants[index].Player >= 0;

        private static CartOccupant[] EmptySeats() => new[]
        {
            new CartOccupant { Player = -1 }, new CartOccupant { Player = -1 },
            new CartOccupant { Player = -1 }, new CartOccupant { Player = -1 }
        };

        private void Awake()
        {
            controller = GetComponent<GolfCartController>();
            presentation = GetComponent<GolfCartPresentation>();
            seats = new CartSeat[4];
            foreach (var seat in GetComponentsInChildren<CartSeat>()) seats[seat.Index] = seat;
            SetTickCallbacks(TickCallback.Tick | TickCallback.PostTick);
        }

        public override void OnStartNetwork()
        {
            Carts[ObjectId] = this;
            controller.Body.isKinematic = true;
            Bodies[controller.Body] = this;
            PredictionManager.OnPreReplicateReplay += BeforeReplay;
            PredictionManager.OnPostReplicateReplay += AfterReplay;
            PredictionManager.OnPreReconcile += BeforeReconcile;
            PredictionManager.OnPostReconcile += AfterReconcile;
            TimeManager.OnPrePhysicsSimulation += BeforePhysics;
            TimeManager.OnPostPhysicsSimulation += AfterPhysics;
            PlayerSeating.ResolvePending();
        }

        public override void OnStartServer()
        {
            ServerManager.Objects.OnPreDestroyClientObjects += Disconnect;
            controller.Body.isKinematic = false;
            controller.ResetMotion(false, true);
            controller.Body.WakeUp();
            BaselineTick = TimeManager.Tick;
            epoch = 1;
            inputOwner = -1;
            baselineReady = true;
            StateRevision = 1;
            BirdRegistry.Instance?.RememberCart(this);
            presentation.SetColor(bodyColor);
        }

        public override void OnSpawnServer(NetworkConnection connection) =>
            TargetCurrent(connection, StateRevision, occupants, Recovery, CaptureBaseline(), bodyColor, lightsOn);

        [TargetRpc]
        private void TargetCurrent(NetworkConnection connection, uint revision, CartOccupant[] current, CartRecovery recovery,
            CartBaseline state, Color color, bool lights)
        {
            if (IsServerInitialized) return;
            ApplyState(revision, current, recovery, Array.Empty<SeatTransition>());
            InstallBaseline(state);
            bodyColor = color;
            presentation.SetColor(color);
            lightsOn = lights;
            presentation.SetLights(lights);
        }

        internal void Request(PlayerSeating player, uint request, uint playerRevision, uint cartRevision, uint motionEpoch, int destination)
        {
            if (!IsServerInitialized) return;
            bool changesDriver = player.SeatIndex == 0 || destination == 0;
            if (Busy || player.Revision != playerRevision || motionEpoch != epoch ||
                (changesDriver || Recovery != CartRecovery.None) && cartRevision != StateRevision || destination < -1 || destination > 3 ||
                player.PlacementPending || player.Cart && player.Cart != this || destination < 0 && player.Cart != this)
            { player.CompleteRequest(request, SeatRequestResult.Busy); return; }
            if (destination >= 0 && IsOccupied(destination)) { player.CompleteRequest(request, SeatRequestResult.Occupied); return; }
            pending = new PendingChange { Player = player, Seat = destination, Request = request, PlayerRevision = playerRevision,
                Recover = Recovery != CartRecovery.None };
        }

        private void BeforePhysics(float delta)
        {
            if (!SimulatesPhysics) return;
            controller.CaptureContactStep();
            if (ReportsWorldEffects) BirdRegistry.Instance?.CartBefore(this);
        }

        private void AfterPhysics(float delta)
        {
            if (!SimulatesPhysics) return;
            controller.FinishPhysics(delta);
            if (ReportsWorldEffects) BirdRegistry.Instance?.CartAfter(this);
        }

        private void CommitPending()
        {
            if (pending == null) return;
            var change = pending;
            if (!change.Eject && (!change.Player || change.Player.Revision != change.PlayerRevision ||
                change.Player.Cart && change.Player.Cart != this || change.Seat >= 0 && IsOccupied(change.Seat)))
            {
                pending = null;
                if (change.Player) change.Player.CompleteRequest(change.Request, SeatRequestResult.Busy);
                return;
            }
            if (change.Recover)
            {
                pending = null;
                Vector3 position = default;
                Quaternion rotation = default;
                bool clear = change.Seat >= 0 && Array.TrueForAll(occupants, entry => entry.Player < 0) &&
                    controller.TryRecovery(out position, out rotation);
                if (clear)
                {
                    controller.Body.position = position;
                    controller.Body.rotation = rotation;
                    controller.Body.linearVelocity = controller.Body.angularVelocity = Vector3.zero;
                    Recovery = CartRecovery.None;
                    BeginBaseline();
                    Broadcast(Array.Empty<SeatTransition>(), true);
                }
                change.Player?.CompleteRequest(change.Request, clear ? SeatRequestResult.Completed : SeatRequestResult.Blocked);
                return;
            }
            bool handoff = change.Eject || change.Seat == 0 || change.Player && change.Player.SeatIndex == 0;
            Commit(change, controller.Capture(epoch, TimeManager.Tick), handoff);
        }

        private void Commit(PendingChange change, CartMotion frame, bool handoff)
        {
            reservedExits.Clear();
            var transitions = new List<SeatTransition>(4);
            if (change.Eject)
            {
                for (int i = 0; i < 4; i++)
                    if (PlayerSeating.Players.TryGetValue(occupants[i].Player, out var rider))
                        transitions.Add(CreateExit(rider, frame, change, true));
                occupants = EmptySeats();
                if (change.Recovery != CartRecovery.None && change.Recovery != Recovery) RecoveryOrigin = controller.Body.position;
                Recovery = change.Recovery == CartRecovery.None ? Recovery : change.Recovery;
            }
            else
            {
                var player = change.Player;
                if (!player) { pending = null; return; }
                SeatTransition transition;
                if (change.Seat < 0)
                {
                    transition = CreateExit(player, frame, change, false);
                    if (transition.PlacementPending)
                    {
                        pending = null;
                        player.CompleteRequest(change.Request, SeatRequestResult.Blocked);
                        return;
                    }
                }
                else transition = new SeatTransition { Player = player.ObjectId, Cart = ObjectId, Seat = (sbyte)change.Seat,
                    Revision = player.Revision + 1, Generation = player.Motor.ImpactGeneration + 1 };
                if (player.Cart == this) occupants[player.SeatIndex] = new CartOccupant { Player = -1 };
                if (change.Seat >= 0) occupants[change.Seat] = new CartOccupant { Player = player.ObjectId,
                    Revision = transition.Revision, Generation = transition.Generation };
                transitions.Add(transition);
            }
            pending = null;
            if (handoff)
            {
                int owner = PlayerSeating.Players.TryGetValue(occupants[0].Player, out var driver) ? driver.OwnerId : -1;
                if (owner < 0) RemoveOwnership(); else GiveOwnership(driver.Owner);
                BeginBaseline();
            }
            Broadcast(transitions.ToArray(), handoff);
            change.Player?.CompleteRequest(change.Request);
        }

        private SeatTransition CreateExit(PlayerSeating player, CartMotion frame, PendingChange change, bool forced)
        {
            var seat = seats[player.SeatIndex];
            Vector3 riderPosition = frame.Position + frame.Rotation * seat.RiderLocal.position;
            Vector3 center = frame.Position + frame.Rotation * controller.Settings.CenterOfMass;
            Vector3 velocity = forced ? change.RiderVelocities[player.SeatIndex] :
                frame.Velocity + Vector3.Cross(frame.AngularVelocity, riderPosition - center);
            Vector3 desired = frame.Position + frame.Rotation * seat.ExitLocal.position;
            bool clear = player.TryExit(riderPosition, desired, this, forced, reservedExits, out var position);
            if (clear) reservedExits.Add(position);
            Vector3 outward = Vector3.ProjectOnPlane(riderPosition - center, Vector3.up).normalized;
            return new SeatTransition { Player = player.ObjectId, Cart = ObjectId, Seat = -1,
                Revision = player.Revision + 1, Generation = player.Motor.ImpactGeneration + 1,
                Position = position, Rotation = Quaternion.Euler(0f, controller.Heading, 0f), Velocity = velocity,
                Ejection = forced ? outward * controller.Settings.EjectionSpeed + Vector3.up * controller.Settings.EjectionLift : Vector3.zero,
                PlacementPending = !clear };
        }

        private void Broadcast(SeatTransition[] transitions, bool resetMotion)
        {
            uint revision = StateRevision + 1;
            ApplyState(revision, occupants, Recovery, transitions);
            ObserversState(revision, occupants, Recovery, transitions, resetMotion, CaptureBaseline());
        }

        [ObserversRpc]
        private void ObserversState(uint revision, CartOccupant[] current, CartRecovery recovery, SeatTransition[] transitions,
            bool resetMotion, CartBaseline baseline)
        {
            if (IsServerInitialized || revision <= StateRevision) return;
            ApplyState(revision, current, recovery, transitions);
            if (resetMotion) InstallBaseline(baseline);
        }

        private void ApplyState(uint revision, CartOccupant[] current, CartRecovery recovery, SeatTransition[] transitions)
        {
            if (revision <= StateRevision) return;
            StateRevision = revision;
            occupants = (CartOccupant[])current.Clone();
            BirdRegistry.Instance?.RememberCart(this);
            if (recovery != CartRecovery.None && recovery != Recovery) RecoveryOrigin = controller.Body.position;
            Recovery = recovery;
            foreach (var transition in transitions) PlayerSeating.Receive(transition, true);
            for (int i = 0; i < 4; i++)
                if (occupants[i].Player >= 0) PlayerSeating.Receive(new SeatTransition
                {
                    Player = occupants[i].Player, Cart = ObjectId, Seat = (sbyte)i,
                    Revision = occupants[i].Revision, Generation = occupants[i].Generation
                }, false);
        }

        internal void ReportIncident(CartRecovery recovery)
        {
            if (!IsServerInitialized || PredictionManager.IsReconciling) return;
            if ((recovery == CartRecovery.None || recovery == Recovery) &&
                Array.TrueForAll(occupants, entry => entry.Player < 0)) return;
            var velocities = new Vector3[4];
            for (int i = 0; i < 4; i++) velocities[i] = controller.PreImpactVelocity(seats[i].RiderLocal.position);
            pending?.Player?.CompleteRequest(pending.Request, SeatRequestResult.Busy);
            pending = new PendingChange { Eject = true, Recovery = recovery, RiderVelocities = velocities };
        }

        internal void ClearRecovery()
        {
            if (!IsServerInitialized || Recovery == CartRecovery.None || Busy) return;
            Recovery = CartRecovery.None;
            Broadcast(Array.Empty<SeatTransition>(), false);
        }

        private void Disconnect(NetworkConnection connection)
        {
            for (int i = 0; i < 4; i++)
                if (PlayerSeating.Players.TryGetValue(occupants[i].Player, out var player) && player.Owner == connection)
                    disconnectedSeats |= (byte)(1 << i);
            if (pending != null && pending.Player && pending.Player.Owner == connection) pending = null;
        }

        private void CommitDisconnects()
        {
            if (disconnectedSeats == 0) return;
            bool driverLeft = (disconnectedSeats & 1) != 0;
            for (int i = 0; i < 4; i++)
                if ((disconnectedSeats & (1 << i)) != 0) occupants[i] = new CartOccupant { Player = -1 };
            disconnectedSeats = 0;
            if (driverLeft)
            {
                RemoveOwnership();
                BeginBaseline();
            }
            Broadcast(Array.Empty<SeatTransition>(), driverLeft);
        }

        public bool LightsOn => lightsOn;

        public void ToggleLights()
        {
            ApplyLightToggle();
            if (!IsServerInitialized) ServerToggleLights();
        }

        private void ApplyLightToggle(NetworkConnection sender = null)
        {
            lightsOn = !lightsOn;
            presentation.SetLights(lightsOn);
            if (!IsServerInitialized) return;
            foreach (var connection in Observers)
                if (connection != sender && !connection.IsLocalClient) TargetToggleLights(connection);
        }

        [ServerRpc(RequireOwnership = false)]
        private void ServerToggleLights(NetworkConnection sender = null) => ApplyLightToggle(sender);
        [TargetRpc] private void TargetToggleLights(NetworkConnection connection) => ApplyLightToggle();

        public void Honk()
        {
            if (hornCooldown > Time.unscaledTime) return;
            hornCooldown = Time.unscaledTime + 0.3f;
            BirdRegistry.Instance?.Honk(this);
            PlayHorn();
            if (!IsServerInitialized) ServerHonk();
        }

        private void PlayHorn(NetworkConnection sender = null)
        {
            presentation.PlayHorn();
            if (!IsServerInitialized) return;
            foreach (var connection in Observers)
                if (connection != sender && !connection.IsLocalClient) TargetHonk(connection);
        }

        [ServerRpc(RequireOwnership = false)] private void ServerHonk(NetworkConnection sender = null) => PlayHorn(sender);
        [TargetRpc] private void TargetHonk(NetworkConnection connection) => presentation.PlayHorn();

        public void SetBodyColor(Color color)
        {
            if (!IsServerInitialized) { ServerColor(color); return; }
            bodyColor = color;
            presentation.SetColor(color);
            ObserversColor(color);
        }
        [ServerRpc(RequireOwnership = false)] private void ServerColor(Color color) => SetBodyColor(color);
        [ObserversRpc] private void ObserversColor(Color color) { bodyColor = color; presentation.SetColor(color); }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            if (Application.isPlaying && presentation && IsServerInitialized) SetBodyColor(bodyColor);
        }
#endif

        public override void OnStopNetwork()
        {
            BirdRegistry.Instance?.ForgetCart(this);
            TimeManager.OnPrePhysicsSimulation -= BeforePhysics;
            TimeManager.OnPostPhysicsSimulation -= AfterPhysics;
            ServerManager.Objects.OnPreDestroyClientObjects -= Disconnect;
            Carts.Remove(ObjectId);
            PlayerSeating.ForgetCart(ObjectId);
            pending = null;
            PredictionManager.OnPreReplicateReplay -= BeforeReplay;
            PredictionManager.OnPostReplicateReplay -= AfterReplay;
            PredictionManager.OnPreReconcile -= BeforeReconcile;
            PredictionManager.OnPostReconcile -= AfterReconcile;
            AfterReplay(0, 0);
            Bodies.Remove(controller.Body);
            baselineReady = false;
            controller.Body.isKinematic = true;
        }
    }
}
