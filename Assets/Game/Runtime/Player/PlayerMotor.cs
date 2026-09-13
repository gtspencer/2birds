using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
using UnityEngine;

namespace TwoBirds
{
    public enum MovementMode : byte { Walking, Airborne, External }

    public struct MoveInput : IReplicateData
    {
        public Vector2 Direction;
        public float Facing;
        public bool Jump;
        private uint tick;
        public MoveInput(Vector2 direction, float facing, bool jump) { Direction = direction; Facing = facing; Jump = jump; tick = 0; }
        public uint GetTick() => tick;
        public void SetTick(uint value) => tick = value;
        public void Dispose() { }
    }

    public struct MotorState : IReconcileData
    {
        public PredictionRigidbody Body;
        public Vector3 Position;
        public Vector3 PendingImpulse;
        public MovementMode Mode;
        public byte JumpCooldown;
        public uint ResetRevision;
        public uint ServerTick;
        private uint tick;
        public MotorState(PredictionRigidbody body, Vector3 impulse, MovementMode mode, byte cooldown, uint reset, uint serverTick)
        { Body = body; Position = body.Rigidbody.position; PendingImpulse = impulse; Mode = mode; JumpCooldown = cooldown; ResetRevision = reset; ServerTick = serverTick; tick = 0; }
        public uint GetTick() => tick;
        public void SetTick(uint value) => tick = value;
        public void Dispose() { }
    }

    [RequireComponent(typeof(Rigidbody), typeof(PlayerInputReader))]
    public sealed class PlayerMotor : TickNetworkBehaviour
    {
        [SerializeField] private GameSettings settings;
        private readonly PredictionRigidbody predictedBody = new();
        private PlayerInputReader input;
        private Vector3 spawnPoint;
        private Vector3 pendingImpulse;
        private byte jumpCooldown;
        private uint resetRevision;
        public Rigidbody Body { get; private set; }
        public MovementMode Mode { get; private set; } = MovementMode.Airborne;
        public bool Grounded => Mode == MovementMode.Walking;
        public uint ResetRevision => resetRevision;
        public event System.Action<uint, uint, Vector3> Reconciled;
        public event System.Action<uint, Vector3> Simulated;

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            predictedBody.Initialize(Body);
            input = GetComponent<PlayerInputReader>();
            spawnPoint = transform.position;
        }

        internal void SetSpawnPoint(Vector3 point) => spawnPoint = point;

        // Server-only effects are queued at a tick boundary. Their pending state and resulting body
        // are reconciled together, so replay cannot lose or duplicate an impulse.
        internal void QueueImpulse(Vector3 impulse)
        {
            if (IsServerInitialized && Finite(impulse.x) && Finite(impulse.y) && Finite(impulse.z)) pendingImpulse += impulse;
        }

        internal void SetExternalControl(bool external)
        {
            if (IsServerInitialized) Mode = external ? MovementMode.External : MovementMode.Airborne;
        }

        protected override void TimeManager_OnTick() => ReplicateMove(IsOwner ? input.Consume() : default);
        protected override void TimeManager_OnPostTick()
        {
            Simulated?.Invoke(TimeManager.LocalTick, Body.position);
            CreateReconcile();
        }

        public override void CreateReconcile() => ReconcileState(new MotorState(predictedBody, pendingImpulse, Mode, jumpCooldown, resetRevision, IsServerInitialized ? TimeManager.Tick : 0));

        [Replicate]
        private void ReplicateMove(MoveInput data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
        {
            if (!Finite(data.Direction.x) || !Finite(data.Direction.y) || !Finite(data.Facing)) data = default;
            // Missing inputs apply no new intent. Physics retains momentum; jump edges are never extrapolated.
            if (!state.ContainsCreated()) { data.Direction = default; data.Jump = false; }
            if (jumpCooldown > 0) jumpCooldown--;
            bool grounded = Physics.SphereCast(Body.position + Vector3.down * 0.45f, 0.45f, Vector3.down,
                out _, 0.17f, settings.GroundLayers, QueryTriggerInteraction.Ignore) && Body.linearVelocity.y <= 0.5f;
            if (Mode != MovementMode.External)
            {
                Mode = grounded ? MovementMode.Walking : MovementMode.Airborne;
                Vector2 direction = Vector2.ClampMagnitude(data.Direction, 1f);
                Vector3 target = new Vector3(direction.x, 0f, direction.y) * settings.WalkSpeed;
                Vector3 horizontal = new Vector3(Body.linearVelocity.x, 0f, Body.linearVelocity.z);
                float acceleration = grounded ? (direction.sqrMagnitude > 0f ? settings.GroundAcceleration : settings.Braking) : settings.AirAcceleration;
                Vector3 change = Vector3.ClampMagnitude(target - horizontal, acceleration * (float)TimeManager.TickDelta);
                predictedBody.AddForce(change, ForceMode.VelocityChange);
                if (state.ContainsCreated()) predictedBody.MoveRotation(Quaternion.Euler(0f, Mathf.Repeat(data.Facing, 360f), 0f));
                if (data.Jump && grounded && jumpCooldown == 0)
                {
                    predictedBody.AddForce(Vector3.up * settings.JumpSpeed, ForceMode.VelocityChange);
                    jumpCooldown = 12;
                    Mode = MovementMode.Airborne;
                }
            }
            if (pendingImpulse != Vector3.zero)
            {
                predictedBody.AddForce(pendingImpulse, ForceMode.Impulse);
                pendingImpulse = default;
            }
            if (IsServerInitialized && Body.position.y < settings.FallBoundary)
            {
                predictedBody.ClearPendingForces();
                predictedBody.ClearVelocities();
                Body.position = spawnPoint;
                Body.rotation = Quaternion.identity;
                jumpCooldown = 0;
                pendingImpulse = default;
                Mode = MovementMode.Airborne;
                resetRevision++;
            }
            predictedBody.Simulate();
        }

        [Reconcile]
        private void ReconcileState(MotorState data, Channel channel = Channel.Unreliable)
        {
            // Do not count FishNet's locally generated fallback states as authoritative measurements.
            if (data.ServerTick != 0) Reconciled?.Invoke(data.GetTick(), data.ServerTick, data.Position);
            pendingImpulse = data.PendingImpulse;
            Mode = data.Mode;
            jumpCooldown = data.JumpCooldown;
            resetRevision = data.ResetRevision;
            predictedBody.Reconcile(data.Body);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
