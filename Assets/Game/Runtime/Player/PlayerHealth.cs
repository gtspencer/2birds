using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public enum PlayerLifeState : byte { Alive, Downed }
    public enum PlayerLifeTransition : byte { Spawn, Down, Revive, Respawn }
    public enum HealthChangeCause : byte { Damage, Healing }

    public sealed class PlayerHealth : MonoBehaviour
    {
        public const byte Maximum = 100;
        private readonly HashSet<object> fallProtection = new();
        private PlayerNetworkState network;
        private PlayerMotor motor;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private PlayerInventory inventory;
        private PlayerInputReader input;
        private PlayerPresentation presentation;
        private PlayerPotionEffects effects;
        private PlayerRagdoll ragdoll;
        private double healingRemainder;
        private bool landingProtected;
        public byte Current { get; private set; } = Maximum;
        public float Normalized => Current / (float)Maximum;
        public bool IsAlive => !IsDowned;
        public bool IsDowned { get; private set; }
        internal bool PreserveDownInput { get; private set; }
        public event Action HealthChanged;
        public event Action LifeChanged;
        public event Action DamagingHit;
        internal bool CanChange => network.LifeReady && network.IsOwner && network.IsClientInitialized &&
            !network.PredictionManager.IsReconciling && IsAlive;
        internal GameSettings Settings => motor.Settings;

        private void Awake()
        {
            network = GetComponent<PlayerNetworkState>();
            motor = GetComponent<PlayerMotor>();
            seating = GetComponent<PlayerSeating>();
            carry = GetComponent<PlayerCarry>();
            inventory = GetComponent<PlayerInventory>();
            input = GetComponent<PlayerInputReader>();
            presentation = GetComponent<PlayerPresentation>();
            effects = GetComponent<PlayerPotionEffects>();
            ragdoll = GetComponent<PlayerRagdoll>();
        }

        internal Vector3 CaptureVelocity => carry.ReleasePreview ? carry.PreviewVelocity :
            carry.IsCarried && carry.Partner ? carry.Partner.Seating.PointVelocity :
            seating.Seated ? seating.PointVelocity : motor.CaptureImpactVelocity;
        internal Vector3 LivingPosition => seating.Seated ? seating.Cart.GetSeat(seating.SeatIndex).PhysicalRider.position :
            carry.IsCarried || carry.ReleasePreview ? presentation.Graphics.position : motor.Body.position;

        public void ApplyDamage(int amount) => ApplyDamage(amount, Vector3.zero);

        internal PlayerRagdollSeed CaptureDownSeed(Vector3 velocity) => ragdoll.Capture(velocity);

        internal void ApplyDamage(int amount, Vector3 incomingChange, PlayerRagdollSeed? captured = null)
        {
            if (!CanChange || amount <= 0) return;
            var seed = captured ?? ragdoll.Capture(CaptureVelocity + incomingChange);
            SetHealth((byte)Mathf.Max(0, Current - amount));
            DamagingHit?.Invoke();
            if (Current == 0)
            {
                healingRemainder = 0d;
                landingProtected = false;
                IsDowned = true;
                ragdoll.Begin(seed);
                Suspend();
                LifeChanged?.Invoke();
                network.ReportDown(seed);
            }
            else network.ReportHealth(HealthChangeCause.Damage, false);
        }

        public void Heal(int amount)
        {
            if (!CanChange || amount <= 0 || Current == Maximum) return;
            SetHealth((byte)Mathf.Min(Maximum, Current + amount));
            if (Current == Maximum) healingRemainder = 0d;
            network.ReportHealth(HealthChangeCause.Healing, false);
        }

        public void HealContinuous(float amount)
        {
            if (!CanChange || amount <= 0f) return;
            if (Current == Maximum) { healingRemainder = 0d; FlushHealing(); return; }
            healingRemainder += amount;
            int whole = (int)Math.Min(Maximum, Math.Floor(healingRemainder));
            if (whole == 0) return;
            healingRemainder -= whole;
            SetHealth((byte)Mathf.Min(Maximum, Current + whole));
            if (Current == Maximum) healingRemainder = 0d;
            network.ReportHealth(HealthChangeCause.Healing, Current < Maximum);
        }

        public void FlushHealing() => network.FlushHealing();
        public void AcquireFallProtection(object source) => fallProtection.Add(source);
        public void ReleaseFallProtection(object source)
        {
            if (fallProtection.Remove(source) && fallProtection.Count == 0 && IsAlive && !motor.HasLandingSupport)
                landingProtected = true;
        }
        internal bool ConsumeLandingProtection()
        {
            bool result = landingProtected || fallProtection.Count > 0;
            landingProtected = false;
            return result;
        }

        internal void SetHealth(byte value)
        {
            value = (byte)Mathf.Min(Maximum, value);
            if (Current == value) return;
            Current = value;
            HealthChanged?.Invoke();
        }

        internal void PrepareLife(in PlayerLifeSnapshot life)
        {
            bool predicted = IsDowned;
            IsDowned = life.State == PlayerLifeState.Downed;
            PreserveDownInput = predicted && IsDowned && network.IsOwner;
            healingRemainder = 0d;
            landingProtected = false;
            if (IsDowned)
            {
                if (!predicted || !network.IsOwner) ragdoll.Begin(life.Seed);
            }
            else ragdoll.End();
            if (life.Kind == PlayerLifeTransition.Respawn || life.Kind == PlayerLifeTransition.Spawn)
            {
                landingProtected = false;
                motor.ResetStamina();
            }
            SetHealth(life.Health);
        }

        internal void CompleteLife()
        {
            if (!PreserveDownInput) input.ClearContext();
            effects.RefreshLife();
            inventory.ApplyControlPermissions();
            if (!PreserveDownInput) LifeChanged?.Invoke();
            PreserveDownInput = false;
        }

        private void Suspend()
        {
            inventory.Equipment.CancelUse();
            input.ClearContext();
            motor.ApplyPlacement(true, motor.ControlRevision, motor.ImpactGeneration,
                motor.Body.position, motor.Body.rotation, Vector3.zero);
            inventory.Hitbox.SetSuspended(true);
            presentation.SetSeated(true);
            inventory.ApplyControlPermissions();
            effects.RefreshLife();
        }
    }
}
