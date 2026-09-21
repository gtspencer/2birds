using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerPotionEffects : NetworkBehaviour
    {
        private readonly SyncVar<uint> lifetime = new();
        private readonly Dictionary<uint, (float Strength, uint Expiry)> healing = new();
        private readonly HashSet<uint> receivedEffects = new();
        private PlayerSeating seating;
        private PlayerCarry carry;
        private PlayerEffectReceiver receiver;
        private WorldItemRegistry registry;
        private PotionDose current;
        private uint revision;
        private double healingTime;
        private bool showingBuff;
        private PlayerHealth health;
        private uint receivedLifetime;
        private bool bouncyProtected;
        public PlayerMotor Motor { get; private set; }
        public uint Lifetime => lifetime.Value != 0 ? lifetime.Value : receivedLifetime;
        public uint Reset { get; private set; }
        public PotionDefinition Buff => current.Definition == 0 || !registry ? null : registry.GetDefinition(current.Definition) as PotionDefinition;
        public float Remaining => registry ? Mathf.Max(0f, ((long)current.Expiry - registry.ServerTick) * (float)registry.TickDelta) : 0f;
        public event System.Action BuffChanged;

        private void Awake()
        {
            Motor = GetComponent<PlayerMotor>(); seating = GetComponent<PlayerSeating>(); carry = GetComponent<PlayerCarry>();
            receiver = GetComponentInChildren<PlayerEffectReceiver>(true);
            health = GetComponent<PlayerHealth>();
            lifetime.OnChange += LifetimeChanged;
        }
        public override void OnStartNetwork() => registry = WorldItemRegistry.Instance;
        public override void OnStartServer() => EnsureLifetime();
        internal void EnsureLifetime() { if (lifetime.Value == 0) lifetime.Value = (uint)Random.Range(1, int.MaxValue); }
        internal void InstallLifetime(uint value) => receivedLifetime = value;
        private void LifetimeChanged(uint previous, uint next, bool server)
        {
            if (registry) registry.BindEffects(this);
        }
        internal void RefreshPhysicalPose()
        {
            seating.RefreshPhysicalAttachment();
            carry.RefreshPhysicalAttachment();
        }
        internal void Step()
        {
            if (registry.Replaying) return;
            receiver.RefreshEligibility();
            RefreshProtection();
            if (showingBuff && Remaining <= 0f) { showingBuff = false; BuffChanged?.Invoke(); }
        }
        internal void Apply(PotionActivation activation, PotionDefinition definition)
        {
            if (!IsOwner || !receiver.Eligible || registry.Replaying || !receivedEffects.Add(activation.Id)) return;
            if (definition.Effect == PotionEffect.Health) { ApplyHealing(definition.HealthStrength); return; }
            var dose = new PotionDose { Player = ObjectId, Lifetime = Lifetime, Reset = Reset, Revision = ++revision,
                Effect = activation.Id, Definition = definition.ItemId, StartTick = registry.ServerTick,
                Expiry = registry.ServerTick + registry.DurationTicks(definition.BuffDuration), OwnerTick = Motor.NextPotionTick,
                SimulationTick = registry.ServerTick + 1 };
            AcceptDose(dose);
            registry.ReportDose(dose);
        }
        internal void AcceptDose(PotionDose dose)
        {
            if (dose.Lifetime != Lifetime || dose.Reset != Reset || dose.Revision < current.Revision) return;
            revision = System.Math.Max(revision, dose.Revision);
            Motor.ScheduleDose(dose);
            if (dose.Revision == current.Revision) return;
            current = dose;
            showingBuff = Remaining > 0f;
            RefreshProtection();
            BuffChanged?.Invoke();
        }
        internal void RefreshLife()
        {
            healingTime = registry ? registry.ServerTick * registry.TickDelta : 0d;
            receiver.RefreshEligibility();
            RefreshProtection();
            Motor.ResumeBouncy();
        }
        private void RefreshProtection()
        {
            bool next = health.IsAlive && Remaining > 0f && Buff && Buff.Effect == PotionEffect.Bouncy;
            if (next == bouncyProtected) return;
            bouncyProtected = next;
            if (next) health.AcquireFallProtection(this);
            else health.ReleaseFallProtection(this);
        }
        internal void ResetEffects(uint reset)
        {
            if (reset <= Reset) return;
            Reset = reset;
            current = default;
            healing.Clear();
            health.FlushHealing();
            receivedEffects.Clear();
            showingBuff = false;
            Motor.ClearBouncy();
            RefreshProtection();
            if (registry) registry.ClearPlayerDose(ObjectId);
            BuffChanged?.Invoke();
        }
        internal void SetHealing(uint id, float strength, uint expiry)
        {
            SettleHealing();
            healing[id] = (strength, expiry);
        }
        internal void RemoveHealing(uint id)
        {
            SettleHealing();
            healing.Remove(id);
            if (healing.Count == 0) health.FlushHealing();
        }
        internal void SettleHealing(bool endingEligibility = false)
        {
            if (!registry || registry.Replaying) return;
            double now = registry.ServerTick * registry.TickDelta;
            if (IsOwner && (receiver.Eligible || endingEligibility))
            {
                while (healingTime < now && healing.Count > 0)
                {
                    double end = now;
                    float rate = 0f;
                    foreach (var membership in healing.Values)
                    {
                        double expiry = membership.Expiry * registry.TickDelta;
                        if (expiry <= healingTime) continue;
                        rate = Mathf.Max(rate, membership.Strength);
                        end = System.Math.Min(end, expiry);
                    }
                    if (rate > 0f) health.HealContinuous((float)(end - healingTime) * rate);
                    healingTime = end;
                }
            }
            healingTime = now;
            bool active = false;
            foreach (var membership in healing.Values) active |= membership.Expiry * registry.TickDelta > now;
            if (!active || endingEligibility) health.FlushHealing();
        }
        internal void ApplyHealing(float amount)
        {
            health.Heal(Mathf.Max(0, Mathf.RoundToInt(amount)));
        }
        internal void ApplyDamage(float amount, Vector3 impact = default)
        {
            health.ApplyDamage(Mathf.Max(0, Mathf.RoundToInt(amount)), impact);
        }
        public override void OnStopNetwork()
        {
            if (registry) registry.RemoveReceiver(receiver);
            healing.Clear(); receivedEffects.Clear(); current = default; showingBuff = false;
            health.ReleaseFallProtection(this);
            bouncyProtected = false;
            Motor.ClearBouncy();
            BuffChanged?.Invoke();
        }
    }
}
