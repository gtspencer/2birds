using System.Collections.Generic;
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
        public uint LastHitId;
        public byte KnockbackTicks;
        private uint tick;
        public MotorState(PredictionRigidbody body, Vector3 impulse, MovementMode mode, byte cooldown, uint reset, uint serverTick, uint lastHitId, byte knockbackTicks)
        { Body = body; Position = body.Rigidbody.position; PendingImpulse = impulse; Mode = mode; JumpCooldown = cooldown; ResetRevision = reset; ServerTick = serverTick; LastHitId = lastHitId; KnockbackTicks = knockbackTicks; tick = 0; }
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
        private readonly List<ItemHit> hits = new();
        private uint nextHitId;
        private uint lastHitId;
        private uint movementTick;
        private byte knockbackTicks;
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

        internal void QueueImpulse(Vector3 impulse)
        {
            QueueItemHit(0, impulse);
        }

        internal void QueueItemHit(uint source, Vector3 impulse)
        {
            if (!IsServerInitialized || PredictionManager.IsReconciling || !WorldItemRegistry.Finite(impulse)) return;
            var hit = new ItemHit { Id = ++nextHitId, Source = source, ServerTick = TimeManager.Tick,
                PlayerTick = movementTick + 1, Impulse = impulse };
            hits.Add(hit);
            ObserversHit(hit);
        }

        [ObserversRpc]
        private void ObserversHit(ItemHit hit)
        {
            if (IsServerInitialized || hit.Id <= lastHitId) return;
            foreach (var existing in hits)
                if (existing.Id == hit.Id) return;
            hits.Add(hit);
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

        public override void CreateReconcile() => ReconcileState(new MotorState(predictedBody, pendingImpulse, Mode, jumpCooldown, resetRevision, IsServerInitialized ? TimeManager.Tick : 0, lastHitId, knockbackTicks));

        [Replicate]
        private void ReplicateMove(MoveInput data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
        {
            movementTick = data.GetTick();
            if (knockbackTicks > 0) knockbackTicks--;
            foreach (var hit in hits)
            {
                if (hit.Id <= lastHitId || hit.PlayerTick > movementTick) continue;
                pendingImpulse += hit.Impulse;
                lastHitId = hit.Id;
                knockbackTicks = 12;
            }
            if (IsServerInitialized) RemoveAppliedHits();
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
                if (knockbackTicks > 0) acceleration = 0f;
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
                knockbackTicks = 0;
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
            lastHitId = data.LastHitId;
            knockbackTicks = data.KnockbackTicks;
            if (data.ServerTick != 0) RemoveAppliedHits();
            predictedBody.Reconcile(data.Body);
        }

        private void RemoveAppliedHits()
        {
            for (int i = hits.Count - 1; i >= 0; i--)
                if (hits[i].Id <= lastHitId) hits.RemoveAt(i);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
