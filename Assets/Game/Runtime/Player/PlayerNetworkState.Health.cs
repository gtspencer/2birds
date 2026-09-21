using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Timing;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public struct PlayerHealthReport
    {
        public uint Lifetime, Revision, Sequence;
        public byte Health;
        public HealthChangeCause Cause;
    }

    public struct PlayerDownReport
    {
        public PlayerRagdollSeed Seed;
        public uint Tick;
        public byte Fraction;
    }

    public struct PlayerLifeSnapshot
    {
        public uint Lifetime, Revision, DownTick;
        public byte DownFraction, Health;
        public PlayerLifeState State;
        public PlayerLifeTransition Kind;
        public PlayerControlTransition Placement;
        public PlayerRagdollSeed Seed;
    }

    public struct PlayerReviveClaim
    {
        public uint LifeRevision, Sequence, Attempt, RescuerLifetime, RescuerRevision, StartTick;
        public byte StartFraction;
        public int Rescuer;
        public float Duration;
        public bool Active;
    }

    public sealed partial class PlayerNetworkState
    {
        internal static readonly Dictionary<int, PlayerNetworkState> Players = new();
        private PlayerHealth health;
        private PlayerRagdoll ragdoll;
        private PlayerRevival revival;
        private PlayerPotionEffects effects;
        private PlayerPlacement placement;
        private PlayerLifeSnapshot life;
        private PlayerHealthReport retainedHealth, pendingHealth;
        private PlayerReviveClaim claim, pendingClaim;
        private PlayerNetworkState rescueTarget;
        private uint localHealthSequence, claimSequence;
        private byte lastReportedHealth = PlayerHealth.Maximum;
        private float nextHealingReport;
        private bool healingPending, baselineReady, downPending, boundaryPending, hasPendingHealth, hasPendingClaim;
        public PlayerHealth Health => health;
        public PlayerLifeSnapshot Life => life;
        public PlayerReviveClaim Claim => claim;
        public uint Lifetime => life.Lifetime;
        public uint LifeRevision => life.Revision;
        public bool CanGameplayActions => health.IsAlive && !seating.PlacementPending && !seating.AwaitingReference && !revival.Busy && !rescueTarget;
        internal bool DownConfirmed => baselineReady && !downPending && life.State == PlayerLifeState.Downed;
        internal bool LifeReady => baselineReady;
        public event Action ClaimChanged;

        private void AwakeHealth()
        {
            health = GetComponent<PlayerHealth>();
            ragdoll = GetComponent<PlayerRagdoll>();
            revival = GetComponent<PlayerRevival>();
            effects = GetComponent<PlayerPotionEffects>();
            placement = GetComponent<PlayerPlacement>();
        }

        internal double Elapsed(uint tick, byte fraction)
        {
            var now = TimeManager.GetPreciseTick(TickType.Tick);
            return ((int)(now.Tick - tick) + now.PercentAsDouble -
                new PreciseTick(tick, fraction).PercentAsDouble) * TimeManager.TickDelta;
        }
        public float RescueRemaining => health.IsDowned ? (float)Math.Max(0d, 30d - Elapsed(life.DownTick, life.DownFraction)) : 0f;
        public float ReviveRemaining => claim.Active ? (float)Math.Max(0d, claim.Duration - Elapsed(claim.StartTick, claim.StartFraction)) : 0f;

        private void StartHealthNetwork()
        {
            Players[ObjectId] = this;
            TimeManager.OnTick += HealthTick;
            ragdoll.StartNetwork();
        }

        private void StartHealthServer()
        {
            effects.EnsureLifetime();
            life = new PlayerLifeSnapshot { Lifetime = effects.Lifetime, Revision = 1,
                State = PlayerLifeState.Alive, Kind = PlayerLifeTransition.Spawn, Health = PlayerHealth.Maximum,
                Placement = seating.CaptureCurrent() };
            retainedHealth = new PlayerHealthReport { Lifetime = life.Lifetime, Revision = life.Revision, Health = life.Health };
            baselineReady = true;
            ServerManager.Objects.OnPreDestroyClientObjects += HealthDisconnect;
        }

        private void HealthTick()
        {
            if (PredictionManager.IsReconciling || !baselineReady) return;
            if (IsOwner)
            {
                if (healingPending && Time.unscaledTime >= nextHealingReport) FlushHealing();
                Vector3 position = health.IsDowned ? ragdoll.RootPosition : health.LivingPosition;
                if (!boundaryPending && position.y < motor.Settings.FallBoundary)
                {
                    boundaryPending = true;
                    if (IsServerInitialized) AcceptBoundary(Lifetime, LifeRevision);
                    else ServerBoundary(Lifetime, LifeRevision);
                }
            }
            if (!IsServerInitialized) return;
            if (life.State == PlayerLifeState.Downed)
            {
                if (RescueRemaining <= 0f || retainedRoot.Position.y < motor.Settings.FallBoundary)
                    CommitLife(PlayerLifeTransition.Respawn, Lifetime, LifeRevision);
            }
            else if (health.LivingPosition.y < motor.Settings.FallBoundary)
                CommitLife(PlayerLifeTransition.Respawn, Lifetime, LifeRevision);
            if (claim.Active && (!Players.TryGetValue(claim.Rescuer, out var rescuer) ||
                !rescuer.Owner.IsActive || rescuer.Lifetime != claim.RescuerLifetime ||
                rescuer.LifeRevision != claim.RescuerRevision || rescuer.life.State != PlayerLifeState.Alive)) ClearClaim();
        }

        internal void ReportHealth(HealthChangeCause cause, bool continuous)
        {
            if (!IsOwner || !baselineReady || PredictionManager.IsReconciling || downPending) return;
            if (continuous && Time.unscaledTime < nextHealingReport) { healingPending = true; return; }
            healingPending = false;
            if (health.Current == lastReportedHealth) return;
            var report = MakeReport(cause);
            nextHealingReport = Time.unscaledTime + 0.25f;
            if (IsServerInitialized) AcceptHealth(report, null);
            else ServerHealth(report, null);
        }

        private PlayerHealthReport MakeReport(HealthChangeCause cause)
        {
            lastReportedHealth = health.Current;
            return new PlayerHealthReport { Lifetime = Lifetime, Revision = LifeRevision,
                Sequence = ++localHealthSequence, Health = health.Current, Cause = cause };
        }

        internal void FlushHealing()
        {
            if (!healingPending) return;
            ReportHealth(HealthChangeCause.Healing, false);
        }

        internal void ReportDown(PlayerRagdollSeed seed)
        {
            if (!baselineReady || downPending) return;
            var time = TimeManager.GetPreciseTick(TickType.Tick);
            healingPending = false;
            var report = MakeReport(HealthChangeCause.Damage);
            downPending = true;
            life.DownTick = time.Tick;
            life.DownFraction = time.PercentAsByte;
            var down = new PlayerDownReport { Seed = seed, Tick = time.Tick, Fraction = time.PercentAsByte };
            if (IsServerInitialized) AcceptHealth(report, down);
            else ServerHealth(report, down);
        }

        [ServerRpc]
        private void ServerHealth(PlayerHealthReport report, PlayerDownReport? down) => AcceptHealth(report, down);

        private void AcceptHealth(PlayerHealthReport report, PlayerDownReport? down)
        {
            if (report.Lifetime != Lifetime || report.Revision != LifeRevision ||
                life.State != PlayerLifeState.Alive || report.Sequence <= retainedHealth.Sequence) return;
            if (report.Cause == HealthChangeCause.Damage) ClearRescueClaims();
            retainedHealth = report;
            if (report.Health == 0 && down.HasValue)
            {
                CommitLife(PlayerLifeTransition.Down, Lifetime, LifeRevision, down.Value.Seed, down.Value.Tick, down.Value.Fraction);
                if (RescueRemaining <= 0f) CommitLife(PlayerLifeTransition.Respawn, Lifetime, LifeRevision);
                return;
            }
            if (!IsOwner) health.SetHealth(report.Health);
            ObserversHealth(report);
        }

        [ObserversRpc]
        private void ObserversHealth(PlayerHealthReport report)
        {
            if (!IsServerInitialized) ReceiveHealth(report);
        }

        private void ReceiveHealth(PlayerHealthReport report)
        {
            if (!baselineReady || report.Lifetime == Lifetime && report.Revision > LifeRevision)
            {
                if (!hasPendingHealth || report.Revision > pendingHealth.Revision ||
                    report.Revision == pendingHealth.Revision && report.Sequence > pendingHealth.Sequence)
                { pendingHealth = report; hasPendingHealth = true; }
                return;
            }
            if (report.Lifetime != Lifetime || report.Revision != LifeRevision || report.Sequence < retainedHealth.Sequence) return;
            retainedHealth = report;
            if (IsOwner && (downPending || report.Sequence < localHealthSequence || healingPending)) return;
            health.SetHealth(report.Health);
        }

        private void CommitLife(PlayerLifeTransition kind, uint lifetime, uint revision,
            PlayerRagdollSeed seed = default, uint downTick = 0, byte downFraction = 0)
        {
            if (!IsServerInitialized || lifetime != Lifetime || revision != LifeRevision ||
                kind == PlayerLifeTransition.Down && life.State != PlayerLifeState.Alive ||
                kind == PlayerLifeTransition.Revive && (life.State != PlayerLifeState.Downed || RescueRemaining <= 0f)) return;
            ClearRescueClaims();
            if (seating.Seated) seating.Cart.ReleaseForLife(seating);
            carry.ReleaseForLife();
            var control = seating.CaptureCurrent();
            control.Revision++;
            control.ControlRevision++;
            control.Generation++;
            control.Cart = -1;
            control.Seat = -1;
            control.Role = CarryRole.Free;
            control.Partner = -1;
            control.ContextOnly = control.PlacementPending = control.CrashDamage = false;
            control.LifePlacement = true;
            control.CarryPlacement = false;
            control.ItemAction = default;
            control.Velocity = control.Ejection = Vector3.zero;
            control.Rotation = Quaternion.Euler(0f, kind == PlayerLifeTransition.Down ? seed.Rotation.eulerAngles.y : motor.Body.rotation.eulerAngles.y, 0f);
            if (kind == PlayerLifeTransition.Down) control.Position = seed.Position;
            else
            {
                bool clear = kind == PlayerLifeTransition.Revive
                    ? placement.TryRevive(retainedRoot.Position, motor.SpawnPoint, out control.Position)
                    : placement.TrySpawn(motor.SpawnPoint, out control.Position);
                control.PlacementPending = !clear;
                if (kind == PlayerLifeTransition.Respawn) control.EffectReset++;
            }
            var next = new PlayerLifeSnapshot { Lifetime = lifetime, Revision = revision + 1,
                State = kind == PlayerLifeTransition.Down ? PlayerLifeState.Downed : PlayerLifeState.Alive,
                Kind = kind, Health = kind == PlayerLifeTransition.Down ? (byte)0 : kind == PlayerLifeTransition.Revive ? (byte)50 : PlayerHealth.Maximum,
                DownTick = downTick, DownFraction = downFraction, Placement = control, Seed = seed };
            ApplyLife(next);
            ObserversLife(next);
        }

        [ObserversRpc]
        private void ObserversLife(PlayerLifeSnapshot snapshot)
        {
            if (!IsServerInitialized) ApplyLife(snapshot);
        }

        private void ApplyLife(PlayerLifeSnapshot snapshot, bool baseline = false)
        {
            if (baselineReady && (snapshot.Lifetime != Lifetime || snapshot.Revision < LifeRevision ||
                snapshot.Revision == LifeRevision && !baseline)) return;
            bool sameLife = baselineReady && snapshot.Revision == LifeRevision;
            life = snapshot;
            baselineReady = true;
            if (!sameLife)
            {
                healingPending = downPending = boundaryPending = false;
                localHealthSequence = 0;
                lastReportedHealth = snapshot.Health;
                retainedHealth = new PlayerHealthReport { Lifetime = Lifetime, Revision = LifeRevision, Health = snapshot.Health };
                claim = default;
                ResetRoot(snapshot);
                health.PrepareLife(snapshot);
            }
            PlayerSeating.Receive(snapshot.Placement, false);
            PlayerSeating.ResolvePending();
            if (!sameLife) health.CompleteLife();
            if (hasPendingHealth && pendingHealth.Revision <= LifeRevision)
            { var pending = pendingHealth; hasPendingHealth = false; ReceiveHealth(pending); }
            ApplyPendingRoot();
            if (hasPendingClaim && pendingClaim.LifeRevision <= LifeRevision)
            { var pending = pendingClaim; hasPendingClaim = false; ReceiveClaim(pending); }
            ClaimChanged?.Invoke();
        }

        private void SendHealthBaseline(NetworkConnection connection)
        {
            var snapshot = life;
            snapshot.Placement = seating.CaptureCurrent();
            TargetHealthBaseline(connection, state.Value.SpawnSlot, motor.SpawnPoint, snapshot, retainedHealth, claim, retainedRoot);
        }

        [TargetRpc]
        private void TargetHealthBaseline(NetworkConnection connection, byte slot, Vector3 spawn, PlayerLifeSnapshot snapshot,
            PlayerHealthReport currentHealth, PlayerReviveClaim currentClaim, PlayerRagdollRoot root)
        {
            if (IsServerInitialized) return;
            motor.SetSpawnPoint(spawn);
            effects.InstallLifetime(snapshot.Lifetime);
            if (snapshot.State == PlayerLifeState.Downed && root.Revision == snapshot.Revision)
                snapshot.Seed.Position = root.Position;
            ApplyLife(snapshot, true);
            ReceiveHealth(currentHealth);
            ReceiveRoot(root);
            ReceiveClaim(currentClaim);
        }

        [ServerRpc] private void ServerBoundary(uint lifetime, uint revision) => AcceptBoundary(lifetime, revision);
        private void AcceptBoundary(uint lifetime, uint revision) => CommitLife(PlayerLifeTransition.Respawn, lifetime, revision);
        internal void GiveUp()
        {
            if (!IsOwner || !DownConfirmed) return;
            if (IsServerInitialized) AcceptGiveUp(Lifetime, LifeRevision);
            else ServerGiveUp(Lifetime, LifeRevision);
        }
        [ServerRpc] private void ServerGiveUp(uint lifetime, uint revision) => AcceptGiveUp(lifetime, revision);
        private void AcceptGiveUp(uint lifetime, uint revision)
        {
            if (life.State == PlayerLifeState.Downed) CommitLife(PlayerLifeTransition.Respawn, lifetime, revision);
        }

        internal void RequestRevive(PlayerNetworkState target, uint attempt)
        {
            bool geometry = revival.Geometry(target.revival);
            if (IsServerInitialized) AcceptRevive(target.ObjectId, target.Lifetime, target.LifeRevision, Lifetime, LifeRevision, attempt, geometry);
            else ServerRevive(target.ObjectId, target.Lifetime, target.LifeRevision, Lifetime, LifeRevision, attempt, geometry);
        }
        [ServerRpc]
        private void ServerRevive(int targetId, uint targetLifetime, uint targetRevision, uint rescuerLifetime, uint rescuerRevision, uint attempt, bool geometry) =>
            AcceptRevive(targetId, targetLifetime, targetRevision, rescuerLifetime, rescuerRevision, attempt, geometry);
        private void AcceptRevive(int targetId, uint targetLifetime, uint targetRevision, uint rescuerLifetime, uint rescuerRevision, uint attempt, bool geometry)
        {
            if (!geometry || !Players.TryGetValue(targetId, out var target) || !target.Owner.IsActive || target == this || life.State != PlayerLifeState.Alive ||
                Lifetime != rescuerLifetime || LifeRevision != rescuerRevision || rescueTarget ||
                target.Lifetime != targetLifetime || target.LifeRevision != targetRevision ||
                target.life.State != PlayerLifeState.Downed || target.RescueRemaining <= 0f || target.claim.Active)
            {
                if (IsOwner) revival.Rejected(attempt);
                else TargetReviveRejected(Owner, attempt);
                return;
            }
            var start = TimeManager.GetPreciseTick(TickType.Tick);
            rescueTarget = target;
            target.claim = new PlayerReviveClaim { Active = true, LifeRevision = targetRevision,
                Sequence = ++target.claimSequence, Rescuer = ObjectId, RescuerLifetime = Lifetime, RescuerRevision = LifeRevision,
                Attempt = attempt, StartTick = start.Tick, StartFraction = start.PercentAsByte, Duration = revival.Duration };
            GetComponent<PlayerInventory>().ApplyControlPermissions();
            target.PublishClaim();
        }
        [TargetRpc] private void TargetReviveRejected(NetworkConnection connection, uint attempt) => revival.Rejected(attempt);

        internal void EndRevive(PlayerNetworkState target, uint attempt, uint token, bool complete)
        {
            bool geometry = !complete || revival.Geometry(target.revival);
            if (IsServerInitialized) AcceptReviveEnd(target.ObjectId, target.Lifetime, target.LifeRevision, attempt, token, complete, geometry);
            else ServerReviveEnd(target.ObjectId, target.Lifetime, target.LifeRevision, attempt, token, complete, geometry);
        }
        [ServerRpc]
        private void ServerReviveEnd(int targetId, uint lifetime, uint revision, uint attempt, uint token, bool complete, bool geometry) =>
            AcceptReviveEnd(targetId, lifetime, revision, attempt, token, complete, geometry);
        private void AcceptReviveEnd(int targetId, uint lifetime, uint revision, uint attempt, uint token, bool complete, bool geometry)
        {
            if (!Players.TryGetValue(targetId, out var target) || target.Lifetime != lifetime || target.LifeRevision != revision ||
                !target.claim.Active || target.claim.Rescuer != ObjectId || target.claim.RescuerLifetime != Lifetime ||
                target.claim.Attempt != attempt || token != 0 && target.claim.Sequence != token) return;
            if (!complete) { target.ClearClaim(); return; }
            if (!geometry || life.State != PlayerLifeState.Alive || LifeRevision != target.claim.RescuerRevision || !Owner.IsActive ||
                target.Elapsed(target.claim.StartTick, target.claim.StartFraction) < target.claim.Duration) return;
            if (target.RescueRemaining <= 0f) target.CommitLife(PlayerLifeTransition.Respawn, lifetime, revision);
            else target.CommitLife(PlayerLifeTransition.Revive, lifetime, revision);
        }

        private void PublishClaim()
        {
            ClaimChanged?.Invoke();
            ObserversClaim(Lifetime, claim);
        }
        [ObserversRpc]
        private void ObserversClaim(uint lifetime, PlayerReviveClaim value)
        {
            if (!IsServerInitialized && (!baselineReady || lifetime == Lifetime)) ReceiveClaim(value);
        }
        private void ReceiveClaim(PlayerReviveClaim value)
        {
            if (!baselineReady || value.LifeRevision > LifeRevision)
            { pendingClaim = value; hasPendingClaim = true; return; }
            if (value.LifeRevision != LifeRevision || value.Sequence < claim.Sequence) return;
            claim = value;
            ClaimChanged?.Invoke();
        }
        private void ClearClaim()
        {
            if (!claim.Active) return;
            if (Players.TryGetValue(claim.Rescuer, out var rescuer) && rescuer.rescueTarget == this)
            {
                rescuer.rescueTarget = null;
                rescuer.GetComponent<PlayerInventory>().ApplyControlPermissions();
            }
            claim.Active = false;
            claim.Sequence = ++claimSequence;
            PublishClaim();
        }
        private void ClearRescueClaims()
        {
            if (!IsServerInitialized) return;
            if (rescueTarget) rescueTarget.ClearClaim();
            ClearClaim();
        }
        private void HealthDisconnect(NetworkConnection connection)
        {
            if (Owner == connection) ClearRescueClaims();
        }
        private void LoseHealthOwnership()
        {
            healingPending = downPending = boundaryPending = false;
            hasPendingHealth = hasPendingClaim = hasPendingRoot = false;
            localHealthSequence = retainedHealth.Sequence;
            lastReportedHealth = health.Current;
            rootSequence = retainedRoot.Sequence;
            sentRoot = retainedRoot.Position;
            nextRootSend = 0f;
            revival.Cancel();
            ragdoll.OwnershipChanged();
        }
        private void StopHealthNetwork()
        {
            ClearRescueClaims();
            if (ServerManager) ServerManager.Objects.OnPreDestroyClientObjects -= HealthDisconnect;
            TimeManager.OnTick -= HealthTick;
            ragdoll.StopNetwork();
            revival.Cancel();
            Players.Remove(ObjectId);
            baselineReady = healingPending = downPending = boundaryPending = hasPendingHealth = hasPendingClaim = false;
            hasPendingRoot = false;
            retainedRoot = pendingRoot = default;
            life = default;
            claim = default;
            rescueTarget = null;
        }
    }
}
