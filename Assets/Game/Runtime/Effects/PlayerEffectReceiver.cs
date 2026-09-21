using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(Collider))]
    public sealed class PlayerEffectReceiver : MonoBehaviour
    {
        public PlayerPotionEffects Effects { get; private set; }
        public Collider Collider { get; private set; }
        private bool eligible;
        public bool Eligible => isActiveAndEnabled && Collider && Collider.enabled && Effects && Effects.IsSpawned;
        private void Awake()
        {
            Collider = GetComponent<Collider>();
            Effects = GetComponentInParent<PlayerPotionEffects>();
        }
        private void OnEnable() => eligible = false;
        private void OnDisable()
        {
            if (eligible && Effects) Effects.SettleHealing(true);
            if (WorldItemRegistry.Instance) WorldItemRegistry.Instance.RemoveReceiver(this);
            eligible = false;
        }
        internal void RefreshEligibility()
        {
            bool next = Eligible;
            if (next == eligible) return;
            eligible = next;
            if (next) WorldItemRegistry.Instance.ReseedReceiver(this);
            else WorldItemRegistry.Instance.RemoveReceiver(this);
        }
    }
}
