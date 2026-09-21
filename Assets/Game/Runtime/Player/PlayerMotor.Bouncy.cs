using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public struct BounceState
    {
        public uint Revision, Expiry, LastDose;
        public byte Definition, SequenceDefinition;
        public float Launch, Retention, Minimum;
        public bool Sequence, TookOff;
    }

    public sealed partial class PlayerMotor
    {
        private readonly List<PotionDose> bounceDoses = new();
        private BounceState bounce;
        private PlayerPotionEffects potionEffects;
        internal uint NextPotionTick => Suspended ? TimeManager.LocalTick + 1 : movementTick + 1;
        internal void ScheduleAcceptedDose(ref PotionDose dose)
        {
            dose.OwnerTick = System.Math.Max(dose.OwnerTick, movementTick + 1);
            dose.SimulationTick = TimeManager.Tick;
        }
        internal Vector3 PhysicalFeet
        {
            get
            {
                potionEffects.RefreshPhysicalPose();
                return Body.position + Body.rotation * (capsule.center + Vector3.down * capsule.height * 0.5f);
            }
        }
        internal void ScheduleDose(PotionDose dose)
        {
            int index = bounceDoses.FindIndex(value => value.Revision == dose.Revision);
            if (index >= 0) bounceDoses[index] = dose;
            else bounceDoses.Add(dose);
            bounceDoses.Sort((a, b) => a.Revision.CompareTo(b.Revision));
            if (Suspended) SynchronizeBouncy();
        }
        internal void ClearBouncy() { bounce = default; bounceDoses.Clear(); }
        private void SynchronizeBouncy()
        {
            foreach (var dose in bounceDoses)
                if (dose.Revision > bounce.LastDose && dose.Expiry > TimeManager.Tick) SetDose(dose);
            bounce.Sequence = bounce.TookOff = false;
        }
        private void SetDose(PotionDose dose)
        {
            bounce.Definition = dose.Definition;
            bounce.Revision = bounce.LastDose = dose.Revision;
            bounce.Expiry = dose.Expiry;
        }
        private bool StepBouncy(bool grounded, bool jump)
        {
            uint simulationTick = PredictionManager.IsReconciling ? PredictionManager.ServerReplayTick : TimeManager.Tick;
            foreach (var dose in bounceDoses)
            {
                if (dose.Revision <= bounce.LastDose || dose.Reset != potionEffects.Reset) continue;
                uint scheduled = IsOwner || IsServerInitialized ? dose.OwnerTick : dose.SimulationTick;
                if (movementTick >= scheduled && (!IsServerInitialized || simulationTick >= dose.SimulationTick)) SetDose(dose);
            }
            if (!PredictionManager.IsReconciling)
                bounceDoses.RemoveAll(dose => dose.Revision < bounce.LastDose && (long)simulationTick - dose.Expiry > historyTicks);
            if (bounce.Definition == 0 || simulationTick >= bounce.Expiry)
            {
                bounce.Sequence = bounce.TookOff = false;
                return false;
            }
            if (bounce.Sequence)
            {
                if (!grounded) bounce.TookOff = true;
                else if (bounce.TookOff)
                {
                    float next = bounce.Launch * bounce.Retention;
                    if (next >= settings.JumpSpeed * bounce.Minimum) BounceLaunch(next);
                    else bounce.Sequence = bounce.TookOff = false;
                }
                return true;
            }
            if (!jump || !grounded || jumpCooldown != 0) return true;
            var definition = (PotionDefinition)WorldItemRegistry.Instance.GetDefinition(bounce.Definition);
            bounce.SequenceDefinition = bounce.Definition;
            bounce.Retention = definition.ReboundRetention;
            bounce.Minimum = definition.MinimumMultiplier;
            BounceLaunch(settings.JumpSpeed * definition.InitialMultiplier);
            jumpCooldown = 12;
            return true;
        }
        private void BounceLaunch(float velocity)
        {
            bounce.Sequence = true; bounce.TookOff = false; bounce.Launch = velocity;
            predictedBody.AddForce(Vector3.up * (velocity - Body.linearVelocity.y), ForceMode.VelocityChange);
            Mode = MovementMode.Airborne;
        }
    }
}
