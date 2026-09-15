using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
using UnityEngine;

namespace TwoBirds
{
    public enum MovementMode : byte { Walking, Airborne, External }

    public struct WorldImpactRequest
    {
        public uint Generation;
        public uint RequestId;
        public uint OwnerTick;
        public Vector3 VelocityChange;
        public uint RecoveryTicks;
    }

    public struct WorldImpact
    {
        public uint Generation;
        public uint Sequence;
        public uint RequestId;
        public uint OwnerTick;
        public uint ServerTick;
        public Vector3 VelocityChange;
        public uint RecoveryTicks;
    }

    public struct MoveInput : IReplicateData
    {
        public Vector2 Direction;
        public float Facing;
        public bool Jump;
        public uint SeatingRevision;
        private uint tick;
        public MoveInput(Vector2 direction, float facing, bool jump) { Direction = direction; Facing = facing; Jump = jump; SeatingRevision = 0; tick = 0; }
        public uint GetTick() => tick;
        public void SetTick(uint value) => tick = value;
        public void Dispose() { }
    }

    public struct MotorState : IReconcileData
    {
        public PredictionRigidbody Body;
        public Vector3 Position;
        public Vector3 PendingVelocityChange;
        public MovementMode Mode;
        public byte JumpCooldown;
        public uint ResetRevision;
        public uint ImpactGeneration;
        public uint ServerTick;
        public uint LastImpactSequence;
        public uint LastOwnerRequestId;
        public uint RecoveryTicks;
        public uint SeatingRevision;
        private uint tick;
        public MotorState(PredictionRigidbody body, Vector3 velocityChange, MovementMode mode, byte cooldown,
            uint reset, uint generation, uint serverTick, uint sequence, uint requestId, uint recoveryTicks, uint seatingRevision)
        {
            Body = body; Position = body.Rigidbody.position; PendingVelocityChange = velocityChange;
            Mode = mode; JumpCooldown = cooldown; ResetRevision = reset; ImpactGeneration = generation; ServerTick = serverTick;
            LastImpactSequence = sequence; LastOwnerRequestId = requestId; RecoveryTicks = recoveryTicks; tick = 0;
            SeatingRevision = seatingRevision;
        }
        public uint GetTick() => tick;
        public void SetTick(uint value) => tick = value;
        public void Dispose() { }
    }

    [RequireComponent(typeof(Rigidbody), typeof(PlayerInputReader))]
    public sealed class PlayerMotor : TickNetworkBehaviour
    {
        private sealed class ImpactEntry
        {
            public WorldImpact Impact;
            public uint AcknowledgedAt;
        }

        [SerializeField] private GameSettings settings;
        private readonly PredictionRigidbody predictedBody = new();
        private readonly List<ImpactEntry> impacts = new();
        private PlayerInputReader input;
        private Vector3 spawnPoint;
        private Vector3 pendingVelocityChange;
        private byte jumpCooldown;
        private uint resetRevision;
        private uint impactGeneration;
        private int generationOwner = -1;
        private uint nextRequestId;
        private uint receivedRequestId;
        private uint lastRequestedTick;
        private uint nextImpactSequence;
        private uint lastImpactSequence;
        private uint lastOwnerRequestId;
        private uint movementTick;
        private uint lastApplicationOwnerTick;
        private uint recoveryTicks;
        private uint historyTicks;
        private bool generationReady;
        private bool rejectReplay;
        private CapsuleCollider capsule;
        public bool Seated { get; private set; }
        public uint SeatingRevision { get; private set; }
        internal Vector3 SpawnPoint => spawnPoint;
        public Rigidbody Body { get; private set; }
        public MovementMode Mode { get; private set; } = MovementMode.Airborne;
        public bool Grounded => Mode == MovementMode.Walking;
        public uint ResetRevision => resetRevision;
        internal uint ImpactGeneration => impactGeneration;
        public event System.Action<uint, uint, Vector3> Reconciled;
        public event System.Action<uint, Vector3> Simulated;
        internal event System.Action BeforeOwnerMove;
        internal event System.Action<Vector3> PresentationCorrected;
        private PlayerPresentation presentation;
        private Vector3 graphicsBeforeReconcile;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private readonly Queue<string> impactTrace = new();
        private uint traceTicksRemaining;
        private float nextTraceDump;
        public event System.Action<string> ImpactTraced;
#endif

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            capsule = GetComponent<CapsuleCollider>();
            predictedBody.Initialize(Body);
            input = GetComponent<PlayerInputReader>();
            presentation = GetComponent<PlayerPresentation>();
            spawnPoint = transform.position;
        }

        public override void OnStartNetwork()
        {
            // FishNet retains up to five seconds of replicate history.
            historyTicks = (uint)TimeManager.TickRate * 5 + 1;
            PredictionManager.OnPreReplicateReplay += BeforeReplay;
            PredictionManager.OnPreReconcile += BeforeReconcile;
            PredictionManager.OnPostReplicateReplay += AfterReplay;
            PredictionManager.OnPostReconcile += AfterReconcile;
        }

        public override void OnStartServer() => BeginGeneration(impactGeneration + 1);
        public override void OnSpawnServer(NetworkConnection connection) => TargetGeneration(connection, impactGeneration, OwnerId);
        public override void OnOwnershipServer(NetworkConnection previousOwner)
        {
            movementTick = IsOwner ? TimeManager.LocalTick - 1 : 0;
            BeginGeneration(impactGeneration + 1);
            ObserversGeneration(impactGeneration, OwnerId);
        }

        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            if (IsServerInitialized) return;
            if (IsOwner) movementTick = TimeManager.LocalTick - 1;
            generationReady = generationOwner == OwnerId;
            if (!generationReady) ClearImpacts();
        }

        public override void OnStopNetwork()
        {
            PredictionManager.OnPreReplicateReplay -= BeforeReplay;
            PredictionManager.OnPreReconcile -= BeforeReconcile;
            PredictionManager.OnPostReplicateReplay -= AfterReplay;
            PredictionManager.OnPostReconcile -= AfterReconcile;
            AfterReplay(0, 0);
            rejectReplay = false;
            ClearImpacts();
            impactGeneration = resetRevision = movementTick = 0;
            generationOwner = -1;
            generationReady = false;
        }

        private void BeforeReplay(uint clientTick, uint serverTick)
        {
            if (rejectReplay || Seated) NetworkObject.RigidbodyPauser.Pause();
        }

        private void AfterReplay(uint clientTick, uint serverTick)
        {
            if (rejectReplay || Seated) NetworkObject.RigidbodyPauser.Unpause();
            if (Seated) Body.isKinematic = true;
            TraceImpactState("replay-state");
        }

        private void BeforeReconcile(uint clientTick, uint serverTick)
        {
            graphicsBeforeReconcile = presentation.Graphics.position;
        }

        private void AfterReconcile(uint clientTick, uint serverTick)
        {
            AfterReplay(clientTick, serverTick);
            PresentationCorrected?.Invoke(presentation.Graphics.position - graphicsBeforeReconcile);
            rejectReplay = false;
        }

        [TargetRpc]
        private void TargetGeneration(NetworkConnection connection, uint generation, int owner) => ReceiveGeneration(generation, owner);

        [ObserversRpc]
        private void ObserversGeneration(uint generation, int owner) => ReceiveGeneration(generation, owner);

        private void ReceiveGeneration(uint generation, int owner)
        {
            if (IsServerInitialized || generation < impactGeneration) return;
            if (generation != impactGeneration) BeginGeneration(generation);
            generationOwner = owner;
            generationReady = owner == OwnerId;
        }

        private void BeginGeneration(uint generation)
        {
            ClearImpacts();
            impactGeneration = generation;
            generationOwner = OwnerId;
            generationReady = true;
            TraceImpact($"generation={generation}");
        }

        private void ClearImpacts()
        {
            impacts.Clear();
            pendingVelocityChange = default;
            recoveryTicks = 0;
            nextRequestId = receivedRequestId = lastRequestedTick = 0;
            nextImpactSequence = lastImpactSequence = lastOwnerRequestId = 0;
            lastApplicationOwnerTick = 0;
        }

        internal void SetSpawnPoint(Vector3 point) => spawnPoint = point;
        internal void QueueImpulse(Vector3 impulse) => QueueWorldImpact(impulse / Body.mass, 0.2f);

        public void QueueWorldImpact(Vector3 velocityChange, float recoverySeconds)
        {
            if (Seated || !IsServerInitialized || PredictionManager.IsReconciling || !ValidImpact(velocityChange, recoverySeconds)) return;
            impacts.Add(new ImpactEntry { Impact = new WorldImpact
            {
                Generation = impactGeneration,
                ServerTick = TimeManager.Tick, VelocityChange = velocityChange,
                RecoveryTicks = RecoveryDuration(recoverySeconds)
            } });
        }

        public uint SubmitWorldImpact(Vector3 velocityChange, float recoverySeconds)
        {
            if (Seated || !IsOwner || !generationReady || PredictionManager.IsReconciling || !ValidImpact(velocityChange, recoverySeconds)) return 0;
            var request = new WorldImpactRequest
            {
                Generation = impactGeneration, RequestId = ++nextRequestId, OwnerTick = movementTick + 1,
                VelocityChange = velocityChange, RecoveryTicks = RecoveryDuration(recoverySeconds)
            };
            TraceImpact($"request id={request.RequestId} intended={request.OwnerTick} delta={velocityChange:F6}");
            if (IsServerInitialized) AcceptImpact(request);
            else
            {
                impacts.Add(new ImpactEntry { Impact = FromRequest(request) });
                ServerImpact(request);
            }
            return request.RequestId;
        }

        private uint RecoveryDuration(float seconds) => (uint)Mathf.CeilToInt(seconds / (float)TimeManager.TickDelta);
        private static bool ValidImpact(Vector3 change, float seconds) =>
            Finite(change.x) && Finite(change.y) && Finite(change.z) && change.sqrMagnitude > 0f && Finite(seconds) && seconds >= 0f;

        private static WorldImpact FromRequest(WorldImpactRequest request) => new()
        {
            Generation = request.Generation, RequestId = request.RequestId, OwnerTick = request.OwnerTick,
            VelocityChange = request.VelocityChange, RecoveryTicks = request.RecoveryTicks
        };

        [ServerRpc]
        private void ServerImpact(WorldImpactRequest request) => AcceptImpact(request);

        private void AcceptImpact(WorldImpactRequest request)
        {
            if (Seated || request.Generation != impactGeneration || request.RequestId <= receivedRequestId) return;
            if (!Finite(request.VelocityChange.x) || !Finite(request.VelocityChange.y) || !Finite(request.VelocityChange.z)) return;
            receivedRequestId = request.RequestId;
            var impact = FromRequest(request);
            impact.OwnerTick = System.Math.Max(request.OwnerTick, System.Math.Max(lastRequestedTick, movementTick + 1));
            lastRequestedTick = impact.OwnerTick;
            impact.ServerTick = TimeManager.Tick;
            impacts.Add(new ImpactEntry { Impact = impact });
            TraceImpact($"accept id={request.RequestId} intended={request.OwnerTick} scheduled={impact.OwnerTick} delta={impact.VelocityChange:F6}");
        }

        [ObserversRpc]
        private void ObserversImpact(WorldImpact impact)
        {
            if (Seated || IsServerInitialized || impact.Generation != impactGeneration) return;
            if (impact.Generation != impactGeneration) BeginGeneration(impact.Generation);
            TraceImpact($"confirm id={impact.RequestId} sequence={impact.Sequence} ownerTick={impact.OwnerTick} serverTick={impact.ServerTick} delta={impact.VelocityChange:F6}");
            foreach (var entry in impacts)
            {
                if (entry.Impact.Sequence == impact.Sequence ||
                    impact.RequestId != 0 && entry.Impact.RequestId == impact.RequestId)
                {
                    entry.Impact = impact;
                    impacts.Sort(CompareImpacts);
                    return;
                }
            }
            impacts.Add(new ImpactEntry { Impact = impact });
            impacts.Sort(CompareImpacts);
        }

        private static int CompareImpacts(ImpactEntry a, ImpactEntry b)
        {
            if (a.Impact.Sequence == 0 && b.Impact.Sequence == 0) return a.Impact.RequestId.CompareTo(b.Impact.RequestId);
            if (a.Impact.Sequence == 0) return 1;
            if (b.Impact.Sequence == 0) return -1;
            return a.Impact.Sequence.CompareTo(b.Impact.Sequence);
        }

        internal void ApplySeating(bool seated, uint revision, uint generation, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            Seated = seated;
            SeatingRevision = revision;
            ClearReplicateCache();
            predictedBody.ClearPendingForces();
            if (!Body.isKinematic) predictedBody.ClearVelocities();
            BeginGeneration(System.Math.Max(generation, impactGeneration));
            jumpCooldown = 0;
            rejectReplay = true;
            Body.isKinematic = seated;
            capsule.enabled = !seated;
            Body.position = position;
            Body.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            if (!seated)
            {
                Body.linearVelocity = velocity;
                Body.angularVelocity = Vector3.zero;
                recoveryTicks = RecoveryDuration(0.2f);
            }
            Mode = seated ? MovementMode.External : MovementMode.Airborne;
            movementTick = IsOwner ? TimeManager.LocalTick - 1 : 0;
        }

        internal void SetExternalControl(bool external)
        {
            if (IsServerInitialized) Mode = external ? MovementMode.External : MovementMode.Airborne;
        }

        protected override void TimeManager_OnTick()
        {
            if (IsOwner) BeforeOwnerMove?.Invoke();
            if (Seated) return;
            var data = IsOwner ? input.Consume() : default;
            data.SeatingRevision = SeatingRevision;
            ReplicateMove(data);
        }
        protected override void TimeManager_OnPostTick()
        {
            if (Seated) return;
            Simulated?.Invoke(TimeManager.LocalTick, Body.position);
            TraceImpactState("physics-state");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (traceTicksRemaining > 0) traceTicksRemaining--;
#endif
            CreateReconcile();
        }

        public override void CreateReconcile()
        {
            if (Seated) return;
            ReconcileState(new MotorState(predictedBody, pendingVelocityChange, Mode, jumpCooldown, resetRevision,
                impactGeneration, IsServerInitialized ? TimeManager.Tick : 0, lastImpactSequence, lastOwnerRequestId, recoveryTicks, SeatingRevision));
        }

        private void ApplyImpacts()
        {
            if (recoveryTicks > 0) recoveryTicks--;
            uint ownerBarrier = 0;
            foreach (var entry in impacts)
            {
                var impact = entry.Impact;
                if (impact.Generation != impactGeneration) continue;
                if (IsOwner && impact.RequestId != 0) ownerBarrier = System.Math.Max(ownerBarrier, impact.OwnerTick);
                if (IsServerInitialized)
                {
                    if (impact.Sequence != 0 || impact.ServerTick > TimeManager.Tick ||
                        impact.OwnerTick > movementTick || movementTick < lastApplicationOwnerTick) continue;
                    impact.Sequence = ++nextImpactSequence;
                    impact.OwnerTick = movementTick;
                    impact.ServerTick = TimeManager.Tick;
                    lastApplicationOwnerTick = movementTick;
                    entry.Impact = impact;
                    ObserversImpact(impact);
                }
                else
                {
                    uint applicationTick = IsOwner ? impact.OwnerTick : impact.ServerTick;
                    if (IsOwner && impact.Sequence == 0) applicationTick = System.Math.Max(applicationTick, ownerBarrier);
                    if (applicationTick > movementTick) continue;
                    if (impact.Sequence != 0 && impact.Sequence <= lastImpactSequence) continue;
                    if (impact.Sequence == 0 && (!IsOwner || impact.RequestId <= lastOwnerRequestId)) continue;
                }
                bool predictedAlready = impact.RequestId != 0 && impact.RequestId <= lastOwnerRequestId;
                TraceImpact($"apply id={impact.RequestId} sequence={impact.Sequence} ownerTick={impact.OwnerTick} serverTick={impact.ServerTick} alreadyInState={predictedAlready} delta={impact.VelocityChange:F6} pendingBefore={pendingVelocityChange:F6}");
                if (!predictedAlready)
                {
                    Vector3 combined = pendingVelocityChange + impact.VelocityChange;
                    if (Finite(combined.x) && Finite(combined.y) && Finite(combined.z))
                    {
                        pendingVelocityChange = combined;
                        recoveryTicks = System.Math.Max(recoveryTicks, impact.RecoveryTicks);
                    }
                    else
                    {
                        TraceImpact($"invalid-sum id={impact.RequestId} sequence={impact.Sequence}");
                        DumpImpactTrace();
                    }
                }
                if (impact.Sequence != 0) lastImpactSequence = impact.Sequence;
                if (impact.RequestId != 0) lastOwnerRequestId = System.Math.Max(lastOwnerRequestId, impact.RequestId);
            }
            if (IsServerInitialized) impacts.RemoveAll(entry => entry.Impact.Sequence != 0);
        }

        [Replicate]
        private void ReplicateMove(MoveInput data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
        {
            if (Seated || data.SeatingRevision != SeatingRevision) return;
            if (PredictionManager.IsReconciling && rejectReplay) return;
            movementTick = data.GetTick();
            ApplyImpacts();
            if (!Finite(data.Direction.x) || !Finite(data.Direction.y) || !Finite(data.Facing)) data = default;
            // Missing inputs apply no new intent; jump edges are never extrapolated.
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
                if (recoveryTicks > 0) acceleration = 0f;
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
            if (pendingVelocityChange.sqrMagnitude > 0f)
            {
                TraceImpact($"force delta={pendingVelocityChange:F6} expectedVelocity={Body.linearVelocity + pendingVelocityChange:F6}");
                predictedBody.AddForce(pendingVelocityChange, ForceMode.VelocityChange);
                pendingVelocityChange = default;
            }
            if (IsServerInitialized && Body.position.y < settings.FallBoundary)
            {
                predictedBody.ClearPendingForces();
                predictedBody.ClearVelocities();
                Body.position = spawnPoint;
                Body.rotation = Quaternion.identity;
                jumpCooldown = 0;
                Mode = MovementMode.Airborne;
                resetRevision++;
                BeginGeneration(impactGeneration + 1);
                ObserversGeneration(impactGeneration, OwnerId);
            }
            predictedBody.Simulate();
        }

        [Reconcile]
        private void ReconcileState(MotorState data, Channel channel = Channel.Unreliable)
        {
            rejectReplay = Seated || data.SeatingRevision != SeatingRevision || data.ImpactGeneration < impactGeneration || data.ResetRevision < resetRevision;
            TraceImpactState($"reconcile rejected={rejectReplay} stateGeneration={data.ImpactGeneration} stateTick={data.GetTick()} serverTick={data.ServerTick} sequence={data.LastImpactSequence} request={data.LastOwnerRequestId} position={data.Position:F6}");
            if (rejectReplay) return;
            if (data.ImpactGeneration != impactGeneration) BeginGeneration(data.ImpactGeneration);
            if (data.ServerTick != 0)
            {
                Reconciled?.Invoke(data.GetTick(), data.ServerTick, data.Position);
                PruneImpacts(data.LastImpactSequence, data.LastOwnerRequestId);
            }
            pendingVelocityChange = data.PendingVelocityChange;
            resetRevision = data.ResetRevision;
            Mode = data.Mode;
            jumpCooldown = data.JumpCooldown;
            lastImpactSequence = data.LastImpactSequence;
            lastOwnerRequestId = data.LastOwnerRequestId;
            recoveryTicks = data.RecoveryTicks;
            predictedBody.Reconcile(data.Body);
        }

        private void PruneImpacts(uint sequence, uint requestId)
        {
            uint now = TimeManager.LocalTick;
            for (int i = impacts.Count - 1; i >= 0; i--)
            {
                var entry = impacts[i];
                bool acknowledged = entry.Impact.Sequence != 0 ? entry.Impact.Sequence <= sequence : entry.Impact.RequestId <= requestId;
                if (!acknowledged) continue;
                if (entry.AcknowledgedAt == 0) entry.AcknowledgedAt = now;
                if (now - entry.AcknowledgedAt > historyTicks) impacts.RemoveAt(i);
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        internal void TraceImpact(string detail)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            traceTicksRemaining = TimeManager.TickRate;
            TraceImpactState(detail);
#endif
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void TraceImpactState(string detail)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (traceTicksRemaining == 0) return;
            string entry = $"time={Time.unscaledTime:F6} owner={OwnerId} local={IsOwner} server={IsServerInitialized} tick={TimeManager.LocalTick} movement={movementTick} replay={PredictionManager.IsReconciling} generation={impactGeneration} sequence={lastImpactSequence} request={lastOwnerRequestId} body={Body.position:F6} velocity={Body.linearVelocity:F6} graphics={presentation.Graphics.position:F6} {detail}";
            if (impactTrace.Count == 256) impactTrace.Dequeue();
            impactTrace.Enqueue(entry);
            ImpactTraced?.Invoke(entry);
            if (detail == "physics-state" && (!Finite(Body.linearVelocity.x) || !Finite(Body.linearVelocity.y) ||
                !Finite(Body.linearVelocity.z) || Body.linearVelocity.sqrMagnitude > 10000f)) DumpImpactTrace();
#endif
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        internal void DumpImpactTrace()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Time.unscaledTime < nextTraceDump) return;
            nextTraceDump = Time.unscaledTime + 1f;
            Debug.LogWarning(string.Join("\n", impactTrace), this);
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [ContextMenu("Dump Impact Trace")]
        private void DumpImpactTraceFromInspector() => Debug.Log(string.Join("\n", impactTrace), this);
#endif
    }
}
