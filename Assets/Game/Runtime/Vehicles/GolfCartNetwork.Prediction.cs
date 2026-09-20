using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public struct CartMotion
    {
        public uint Epoch, Tick;
        public Vector3 Position, Velocity, AngularVelocity;
        public Quaternion Rotation;
        public sbyte Steering;
        public byte FrontLeft, FrontRight, RearLeft, RearRight;
        public bool Handbrake, ParkingBrake;
    }

    public struct CartInput : IReplicateData
    {
        public sbyte Steering, Throttle;
        public bool Handbrake;
        public uint Generation, DriverRevision;
        private uint tick;
        public uint GetTick() => tick;
        public void SetTick(uint value) => tick = value;
        public void Dispose() { }
    }

    public struct CartState : IReconcileData
    {
        public PredictionRigidbody Body;
        public CartPhysicsState Physics;
        public CartInput Held;
        public byte InputAge;
        public uint Generation, ServerTick;
        private uint tick;
        public uint GetTick() => tick;
        public void SetTick(uint value) => tick = value;
        public void Dispose() { }
    }

    public struct CartBaseline
    {
        public CartMotion Motion;
        public CartPhysicsState Physics;
        public uint ServerTick;
        public int Owner;
        public Vector3 RecoveryOrigin;
    }

    public sealed partial class GolfCartNetwork
    {
        private const byte ControlHoldTicks = 3;
        internal static readonly Dictionary<Rigidbody, GolfCartNetwork> Bodies = new();
        private CartInput heldInput;
        private byte inputAge;
        private int inputOwner = -1;
        private uint lastStateTick;
        internal uint BaselineTick { get; private set; }
        private bool baselineReady, rejectReplay, pausedReplay;
        private Vector3 graphicsBeforeReconcile;

        protected override void TimeManager_OnTick()
        {
            CartInput data = default;
            if (baselineReady && (IsOwner && inputOwner == OwnerId || IsServerInitialized && inputOwner < 0))
            {
                data.Generation = epoch;
                data.DriverRevision = occupants[0].Revision;
                var driver = PlayerSeating.Local;
                if (IsOwner && driver && driver.ObjectId == DriverId && driver.Cart == this &&
                    driver.IsDriver && !driver.TransitionPending)
                {
                    data.Steering = PackControl(driver.Input.CartMove.x);
                    data.Throttle = PackControl(driver.Input.CartMove.y);
                    data.Handbrake = driver.Input.Handbrake;
                }
            }
            ReplicateDrive(data);
        }

        private static sbyte PackControl(float value) => (sbyte)Mathf.RoundToInt(Mathf.Clamp(value, -1f, 1f) * 127f);

        [Replicate]
        private void ReplicateDrive(CartInput data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
        {
            if (!SimulatesPhysics) return;
            if (state.ContainsCreated() && data.Generation == epoch && data.DriverRevision == occupants[0].Revision)
            {
                heldInput = data;
                inputAge = 0;
            }
            else
            {
                if (inputAge < ControlHoldTicks) inputAge++;
                else heldInput = default;
                if (state.ContainsCreated()) heldInput = default;
            }
            if (inputOwner < 0) heldInput = default;
            controller.SetInput(new Vector2(heldInput.Steering / 127f, heldInput.Throttle / 127f), heldInput.Handbrake);
            controller.Simulate((float)TimeManager.TickDelta);
        }

        protected override void TimeManager_OnPostTick()
        {
            if (!baselineReady) return;
            if (IsServerInitialized)
            {
                CommitDisconnects();
                controller.AfterPhysics((float)TimeManager.TickDelta);
                CommitPending();
            }
            CreateReconcile();
        }

        public override void CreateReconcile()
        {
            if (!baselineReady) return;
            ReconcileDrive(new CartState
            {
                Body = controller.PredictedBody, Physics = controller.CapturePhysics(), Held = heldInput,
                InputAge = inputAge, Generation = epoch, ServerTick = TimeManager.Tick
            });
        }

        [Reconcile]
        private void ReconcileDrive(CartState data, Channel channel = Channel.Unreliable)
        {
            rejectReplay = !baselineReady || data.Generation != epoch;
            if (rejectReplay) return;
            lastStateTick = System.Math.Max(lastStateTick, data.ServerTick);
            controller.PredictedBody.Reconcile(data.Body);
            controller.RestorePhysics(data.Physics);
            heldInput = data.Held;
            inputAge = data.InputAge;
        }

        private void BeforeReplay(uint clientTick, uint serverTick)
        {
            var pauser = NetworkObject.RigidbodyPauser;
            if ((!baselineReady || rejectReplay) && pauser != null && !pauser.Paused)
            {
                pauser.Pause();
                pausedReplay = true;
            }
        }

        private void AfterReplay(uint clientTick, uint serverTick)
        {
            if (!pausedReplay) return;
            NetworkObject.RigidbodyPauser?.Unpause();
            pausedReplay = false;
        }

        private void BeforeReconcile(uint clientTick, uint serverTick) => graphicsBeforeReconcile = presentation.Graphics.position;

        private void AfterReconcile(uint clientTick, uint serverTick)
        {
            AfterReplay(clientTick, serverTick);
            rejectReplay = false;
            presentation.RebaseTravel(presentation.Graphics.position - graphicsBeforeReconcile);
        }

        private CartBaseline CaptureBaseline() => new()
        {
            Motion = controller.Capture(epoch, TimeManager.Tick), Physics = controller.CapturePhysics(),
            ServerTick = TimeManager.Tick, Owner = inputOwner, RecoveryOrigin = RecoveryOrigin
        };

        private void BeginBaseline()
        {
            epoch++;
            inputOwner = Owner.IsValid ? OwnerId : -1;
            controller.ResetMotion(inputOwner >= 0, inputOwner < 0);
            InstallBaseline(CaptureBaseline());
        }

        private void InstallBaseline(CartBaseline state)
        {
            if (state.Motion.Epoch < epoch || baselineReady && state.Motion.Epoch == epoch && state.ServerTick < lastStateTick) return;
            ClearReplicateCache();
            controller.PredictedBody.ClearPendingForces();
            epoch = state.Motion.Epoch;
            inputOwner = state.Owner;
            lastStateTick = state.ServerTick;
            BaselineTick = state.ServerTick;
            RecoveryOrigin = state.RecoveryOrigin;
            heldInput = default;
            inputAge = ControlHoldTicks;
            controller.ClearInput();
            var body = controller.Body;
            body.isKinematic = false;
            body.position = state.Motion.Position;
            body.rotation = state.Motion.Rotation;
            body.linearVelocity = state.Motion.Velocity;
            body.angularVelocity = state.Motion.AngularVelocity;
            controller.RestorePhysics(state.Physics);
            controller.RestoreDisplay(state.Motion);
            baselineReady = true;
            rejectReplay = true;
            presentation.ResetPose();
            Physics.SyncTransforms();
            foreach (var player in PlayerSeating.Players.Values) player.Motor.CartGenerationChanged(this);
        }

        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            heldInput = default;
            inputAge = ControlHoldTicks;
        }
    }
}
