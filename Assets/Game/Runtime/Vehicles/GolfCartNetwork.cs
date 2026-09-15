using System;
using System.Collections.Generic;
using FishNet.Component.Prediction;
using FishNet.Component.Transforming;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;
using MotionFrame = FishNet.Component.Transforming.NetworkTransform.MotionFrame;

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

    [RequireComponent(typeof(GolfCartController), typeof(NetworkTransform), typeof(OfflineRigidbody))]
    public sealed class GolfCartNetwork : NetworkBehaviour
    {
        private sealed class PendingChange
        {
            public PlayerSeating Player;
            public int Seat;
            public uint Request, Token;
            public bool Eject;
            public CartRecovery Recovery;
            public Vector3[] RiderVelocities;
            public float Deadline;
        }

        internal static readonly Dictionary<int, GolfCartNetwork> Carts = new();
        [SerializeField] private Color bodyColor = new(0.08f, 0.35f, 0.85f);
        private GolfCartController controller;
        private GolfCartPresentation presentation;
        private NetworkTransform motion;
        private OfflineRigidbody offlineBody;
        private CartSeat[] seats;
        private CartOccupant[] occupants = EmptySeats();
        private PendingChange pending;
        private MotionFrame baseline, lastSent, stoppedMotion;
        private uint epoch, nextToken, stopToken, cancelledToken, eventSequence, lastIncident;
        private int simulator = -1;
        private bool startPending, sentRest, incidentPending;
        private readonly List<Vector3> reservedExits = new();
        public CartRecovery Recovery { get; private set; }
        public uint StateRevision { get; private set; }
        public bool Busy => pending != null;
        public bool Simulating { get; private set; }
        public uint Epoch => epoch;
        public GolfCartController Controller => controller;
        internal Vector3 RecoveryOrigin { get; private set; }
        public MotionFrame DisplayMotion
        {
            get
            {
                if (!Simulating) return motion.DisplayMotion;
                var frame = controller.Capture(epoch, TimeManager.LocalTick);
                frame.Position = transform.position;
                frame.Rotation = transform.rotation;
                return frame;
            }
        }
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
            motion = GetComponent<NetworkTransform>();
            offlineBody = GetComponent<OfflineRigidbody>();
            seats = new CartSeat[4];
            foreach (var seat in GetComponentsInChildren<CartSeat>()) seats[seat.Index] = seat;
            motion.EnableEpochMotion();
        }

        public override void OnStartNetwork()
        {
            Carts[ObjectId] = this;
            controller.Body.isKinematic = true;
            offlineBody.SetPredictionManager(PredictionManager);
            TimeManager.OnPrePhysicsSimulation += BeforePhysics;
            TimeManager.OnPostPhysicsSimulation += AfterPhysics;
            PlayerSeating.ResolvePending();
        }

        public override void OnStartServer()
        {
            ServerManager.Objects.OnPreDestroyClientObjects += Disconnect;
            baseline = controller.Capture(1, 0);
            baseline.Velocity = baseline.AngularVelocity = Vector3.zero;
            InstallBaseline(baseline, -1);
            StateRevision = 1;
            presentation.SetColor(bodyColor);
        }

        public override void OnSpawnServer(NetworkConnection connection) =>
            TargetCurrent(connection, StateRevision, occupants, Recovery, motion.LatestMotion, simulator, bodyColor);

        [TargetRpc]
        private void TargetCurrent(NetworkConnection connection, uint revision, CartOccupant[] current, CartRecovery recovery,
            MotionFrame frame, int owner, Color color)
        {
            if (IsServerInitialized) return;
            if (frame.Epoch >= epoch) InstallBaseline(frame, owner);
            ApplyState(revision, current, recovery, Array.Empty<SeatTransition>());
            bodyColor = color;
            presentation.SetColor(color);
        }

        internal void Request(PlayerSeating player, uint request, uint playerRevision, uint cartRevision, int destination)
        {
            if (!IsServerInitialized) return;
            if (Busy || player.Revision != playerRevision || cartRevision != StateRevision || destination < -1 || destination > 3 ||
                player.PlacementPending || player.Cart != null && player.Cart != this || destination < 0 && player.Cart != this)
            { player.CompleteRequest(request); return; }
            if (Recovery != CartRecovery.None)
            {
                if (destination >= 0 && Array.TrueForAll(occupants, entry => entry.Player < 0) &&
                    controller.TryRecovery(out var position, out var rotation))
                {
                    Freeze();
                    var frame = motion.LatestMotion;
                    frame.Epoch = epoch + 1;
                    frame.Tick = 0;
                    frame.Position = position;
                    frame.Rotation = rotation;
                    frame.Velocity = frame.AngularVelocity = Vector3.zero;
                    InstallBaseline(frame, -1);
                    Recovery = CartRecovery.None;
                    Broadcast(Array.Empty<SeatTransition>(), true);
                }
                player.CompleteRequest(request);
                return;
            }
            if (destination >= 0 && IsOccupied(destination)) { player.CompleteRequest(request); return; }
            var change = new PendingChange { Player = player, Seat = destination, Request = request };
            if (player.SeatIndex == 0 || destination == 0) BeginHandoff(change);
            else Commit(change, motion.LatestMotion, false);
        }

        private void BeginHandoff(PendingChange change)
        {
            if (pending != null) return;
            change.Token = ++nextToken;
            change.Deadline = Time.unscaledTime + 2f;
            pending = change;
            if (simulator < 0 || IsOwner) stopToken = change.Token;
            else TargetStop(Owner, change.Token, epoch);
        }

        [TargetRpc]
        private void TargetStop(NetworkConnection connection, uint token, uint expectedEpoch)
        {
            if (expectedEpoch == epoch && token > cancelledToken) stopToken = token;
        }

        [ServerRpc]
        private void ServerStopped(uint token, MotionFrame frame)
        {
            if (pending == null || pending.Token != token || frame.Epoch != epoch) return;
            Commit(pending, frame, true);
        }

        [TargetRpc]
        private void TargetResume(NetworkConnection connection, uint token, uint expectedEpoch)
        {
            cancelledToken = Math.Max(cancelledToken, token);
            if (expectedEpoch != epoch) return;
            stopToken = 0;
            if (!Simulating)
            {
                baseline = stoppedMotion;
                startPending = true;
            }
            incidentPending = false;
        }

        private void BeforePhysics(float delta)
        {
            if (PredictionManager.IsReconciling) return;
            if (startPending && (simulator < 0 ? IsServerInitialized : IsOwner && OwnerId == simulator))
            {
                startPending = false;
                controller.Body.position = baseline.Position;
                controller.Body.rotation = baseline.Rotation;
                controller.Body.isKinematic = false;
                controller.Body.interpolation = RigidbodyInterpolation.Interpolate;
                controller.Body.linearVelocity = baseline.Velocity;
                controller.Body.angularVelocity = baseline.AngularVelocity;
                Simulating = true;
            }
            if (!Simulating) return;
            var driver = PlayerSeating.Local;
            if (driver != null && driver.Cart == this && driver.IsDriver && !driver.TransitionPending)
                controller.SetInput(driver.Input.CartMove, driver.Input.Handbrake);
            else controller.ClearInput();
            controller.BeforePhysics(delta);
        }

        private void AfterPhysics(float delta)
        {
            if (PredictionManager.IsReconciling) return;
            if (IsServerInitialized && pending != null && Time.unscaledTime >= pending.Deadline)
            {
                var timedOut = pending;
                pending = null;
                if (simulator >= 0 && !IsOwner) TargetResume(Owner, timedOut.Token, epoch);
                else stopToken = 0;
                timedOut.Player?.CompleteRequest(timedOut.Request);
                incidentPending = false;
            }
            if (!Simulating) return;
            controller.AfterPhysics(delta);
            var frame = controller.Capture(epoch, TimeManager.LocalTick);
            bool resting = controller.Body.IsSleeping() || frame.Velocity.sqrMagnitude < 0.0001f && frame.AngularVelocity.sqrMagnitude < 0.0001f;
            bool changed = (frame.Position - lastSent.Position).sqrMagnitude > 0.000001f || Quaternion.Angle(frame.Rotation, lastSent.Rotation) > 0.05f ||
                (frame.Velocity - lastSent.Velocity).sqrMagnitude > 0.0001f || (frame.AngularVelocity - lastSent.AngularVelocity).sqrMagnitude > 0.0001f ||
                frame.Steering != lastSent.Steering || frame.Handbrake != lastSent.Handbrake ||
                frame.FrontLeft != lastSent.FrontLeft || frame.FrontRight != lastSent.FrontRight || frame.RearLeft != lastSent.RearLeft || frame.RearRight != lastSent.RearRight;
            if (resting && !sentRest || changed && TimeManager.LocalTick % 3 == 0)
            {
                if (resting) frame.Velocity = frame.AngularVelocity = Vector3.zero;
                motion.PublishMotion(frame, resting);
                lastSent = frame;
                sentRest = resting;
            }
            if (stopToken == 0) return;
            uint token = stopToken;
            stopToken = 0;
            stoppedMotion = frame;
            Freeze();
            if (IsServerInitialized && pending != null && pending.Token == token) Commit(pending, frame, true);
            else if (IsOwner) ServerStopped(token, frame);
        }

        private void Freeze()
        {
            Simulating = startPending = false;
            controller.ClearInput();
            controller.Body.isKinematic = true;
            controller.Body.interpolation = RigidbodyInterpolation.None;
        }

        private void InstallBaseline(MotionFrame frame, int owner)
        {
            Freeze();
            epoch = frame.Epoch;
            simulator = owner;
            baseline = frame;
            motion.InstallMotionBaseline(baseline);
            controller.ResetMotion();
            eventSequence = lastIncident = stopToken = cancelledToken = 0;
            incidentPending = sentRest = false;
            startPending = true;
        }

        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            if (IsOwner && simulator == OwnerId) startPending = true;
            else if (Simulating && simulator >= 0) Freeze();
        }

        private void Commit(PendingChange change, MotionFrame frame, bool handoff)
        {
            reservedExits.Clear();
            var transitions = new List<SeatTransition>(4);
            if (change.Eject)
            {
                for (int i = 0; i < 4; i++)
                    if (PlayerSeating.Players.TryGetValue(occupants[i].Player, out var rider))
                        transitions.Add(CreateExit(rider, frame, change, true));
                occupants = EmptySeats();
                Recovery = change.Recovery == CartRecovery.None ? Recovery : change.Recovery;
            }
            else
            {
                var player = change.Player;
                if (player == null) { pending = null; return; }
                SeatTransition transition;
                if (change.Seat < 0)
                {
                    transition = CreateExit(player, frame, change, false);
                    if (transition.PlacementPending)
                    {
                        pending = null;
                        if (handoff) ResumeStopped(change.Token, frame);
                        player.CompleteRequest(change.Request);
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
                frame.Epoch = epoch + 1;
                frame.Tick = 0;
                InstallBaseline(frame, owner);
            }
            Broadcast(transitions.ToArray(), handoff);
            change.Player?.CompleteRequest(change.Request);
        }

        private void ResumeStopped(uint token, MotionFrame frame)
        {
            if (simulator >= 0 && !IsOwner) TargetResume(Owner, token, epoch);
            else
            {
                stopToken = 0;
                if (!Simulating) { baseline = frame; startPending = true; }
            }
        }

        private SeatTransition CreateExit(PlayerSeating player, MotionFrame frame, PendingChange change, bool forced)
        {
            var seat = seats[player.SeatIndex];
            Vector3 riderPosition = frame.Position + frame.Rotation * transform.InverseTransformPoint(seat.Rider.position);
            Vector3 center = frame.Position + frame.Rotation * controller.Settings.CenterOfMass;
            Vector3 velocity = forced ? change.RiderVelocities[player.SeatIndex] :
                frame.Velocity + Vector3.Cross(frame.AngularVelocity, riderPosition - center);
            Vector3 desired = frame.Position + frame.Rotation * transform.InverseTransformPoint(seat.Exit.position);
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
            ObserversState(revision, occupants, Recovery, transitions, resetMotion, baseline, simulator);
        }

        [ObserversRpc]
        private void ObserversState(uint revision, CartOccupant[] current, CartRecovery recovery, SeatTransition[] transitions,
            bool resetMotion, MotionFrame frame, int owner)
        {
            if (IsServerInitialized || revision <= StateRevision) return;
            if (resetMotion && frame.Epoch > epoch) InstallBaseline(frame, owner);
            ApplyState(revision, current, recovery, transitions);
        }

        private void ApplyState(uint revision, CartOccupant[] current, CartRecovery recovery, SeatTransition[] transitions)
        {
            if (revision <= StateRevision) return;
            StateRevision = revision;
            occupants = (CartOccupant[])current.Clone();
            Recovery = recovery;
            if (recovery != CartRecovery.None) RecoveryOrigin = motion.LatestMotion.Position;
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
            if (!Simulating || incidentPending) return;
            incidentPending = true;
            var velocities = new Vector3[4];
            for (int i = 0; i < 4; i++) velocities[i] = controller.PreImpactVelocity(transform.InverseTransformPoint(seats[i].Rider.position));
            if (IsServerInitialized) AcceptIncident(epoch, ++eventSequence, StateRevision, recovery, velocities);
            else ServerIncident(epoch, ++eventSequence, StateRevision, recovery, velocities);
        }

        [ServerRpc]
        private void ServerIncident(uint motionEpoch, uint sequence, uint revision, CartRecovery recovery, Vector3[] velocities) =>
            AcceptIncident(motionEpoch, sequence, revision, recovery, velocities);

        private void AcceptIncident(uint motionEpoch, uint sequence, uint revision, CartRecovery recovery, Vector3[] velocities)
        {
            if (motionEpoch != epoch || sequence <= lastIncident || revision != StateRevision) return;
            lastIncident = sequence;
            if (pending != null)
            {
                pending.Player?.CompleteRequest(pending.Request);
                pending.Player = null;
                pending.Eject = true;
                pending.Recovery = recovery;
                pending.RiderVelocities = velocities;
                return;
            }
            BeginHandoff(new PendingChange { Eject = true, Recovery = recovery, RiderVelocities = velocities });
        }

        internal void ClearRecovery()
        {
            if (!IsServerInitialized || Recovery == CartRecovery.None || Busy) return;
            Recovery = CartRecovery.None;
            Broadcast(Array.Empty<SeatTransition>(), false);
        }

        private void Disconnect(NetworkConnection connection)
        {
            bool driverLeft = PlayerSeating.Players.TryGetValue(occupants[0].Player, out var driver) && driver.Owner == connection;
            if (pending != null && (driverLeft || pending.Player != null && pending.Player.Owner == connection))
            {
                if (!driverLeft) ResumeStopped(pending.Token, motion.LatestMotion);
                pending.Player?.CompleteRequest(pending.Request);
                pending = null;
            }
            bool changed = false;
            for (int i = 0; i < 4; i++)
                if (PlayerSeating.Players.TryGetValue(occupants[i].Player, out var player) && player.Owner == connection)
                { occupants[i] = new CartOccupant { Player = -1 }; changed = true; }
            if (!changed) return;
            if (driverLeft)
            {
                var frame = motion.LatestMotion;
                RemoveOwnership();
                frame.Epoch = epoch + 1;
                frame.Tick = 0;
                InstallBaseline(frame, -1);
            }
            Broadcast(Array.Empty<SeatTransition>(), driverLeft);
        }

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
            if (Application.isPlaying && presentation != null && IsServerInitialized) SetBodyColor(bodyColor);
        }
#endif

        public override void OnStopNetwork()
        {
            TimeManager.OnPrePhysicsSimulation -= BeforePhysics;
            TimeManager.OnPostPhysicsSimulation -= AfterPhysics;
            ServerManager.Objects.OnPreDestroyClientObjects -= Disconnect;
            Carts.Remove(ObjectId);
            PlayerSeating.ForgetCart(ObjectId);
            pending = null;
            Freeze();
        }
    }
}
