using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace TwoBirds
{
    public struct HealthSnapshot { public int Current, Maximum; }
    public sealed class PlayerHealth : NetworkBehaviour
    {
        [SerializeField, Min(1)] private int defaultMaximum = 100;
        private readonly SyncVar<HealthSnapshot> state = new(new SyncTypeSettings(ReadPermission.OwnerOnly));
        public HealthSnapshot Snapshot => state.Value;
        public event Action Changed;
        private void Awake() => state.OnChange += OnChanged;
        private void OnChanged(HealthSnapshot previous, HealthSnapshot next, bool asServer)
        { if (IsOwner && IsClientInitialized) Changed?.Invoke(); }
        public override void OnStartServer() => ServerSet(Math.Max(1, defaultMaximum), Math.Max(1, defaultMaximum));
        public override void OnStartClient() { if (IsOwner) Changed?.Invoke(); }
        public bool ServerSet(int current, int maximum)
        {
            if (!IsServerInitialized || maximum < 1) return false;
            state.Value = new HealthSnapshot { Current = Math.Clamp(current, 0, maximum), Maximum = maximum };
            return true;
        }
    }
}
