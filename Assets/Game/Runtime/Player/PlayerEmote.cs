using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerEmote : MonoBehaviour
    {
        internal const byte StopId = 0xFF;
        private PlayerAvatarPresentation owner;
        private AvatarPresentation avatar;
        private PlayerInputReader input;
        private PlayerMotor motor;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private PlayerHealth health;
        private PlayerNetworkState network;
        private EmoteCatalog catalog;
        private float elapsed;
        private int disruption;
        internal EmoteDefinition Current { get; private set; }
        internal bool Active => Current;
        internal float ViewWeight { get; private set; }
        internal bool Presenting => Active || ViewWeight > 0f;
        internal float FacingYaw { get; private set; }
        internal event System.Action Changed;
        internal bool CanStart => network.CanGameplayActions && motor.Grounded && !seating.Seated &&
            !seating.TransitionPending && !carry.IsCarried && !carry.IsCarrying;

        internal void Initialize(PlayerAvatarPresentation value)
        {
            owner = value; avatar = value.Presentation;
            input = GetComponent<PlayerInputReader>(); motor = GetComponent<PlayerMotor>();
            seating = GetComponent<PlayerSeating>(); carry = GetComponent<PlayerCarry>();
            health = GetComponent<PlayerHealth>(); network = GetComponent<PlayerNetworkState>();
            catalog = SessionController.Instance.Emotes;
            motor.Disturbed += Disturbed;
            enabled = false;
        }

        internal void Play(byte index)
        {
            var definition = catalog ? catalog.Get(index) : null;
            if (!owner.IsOwner || !definition || !CanStart) return;
            Begin(definition);
            owner.SendEmote(index);
        }

        internal void Stop() { if (owner.IsOwner) End(true); }

        internal void Receive(byte id)
        {
            if (id == StopId) { End(false); return; }
            var definition = catalog ? catalog.Get(id) : null;
            if (definition) Begin(definition);
        }

        internal void ResetLocal()
        {
            bool presenting = Presenting;
            Current = null;
            avatar.State.ClearEmote();
            ViewWeight = 0f;
            enabled = false;
            if (presenting) Changed?.Invoke();
        }

        private void Begin(EmoteDefinition definition)
        {
            if (!Current) FacingYaw = input.Yaw;
            Current = definition; elapsed = 0f; disruption = Disruption();
            avatar.State.PlayEmote(definition);
            enabled = true;
            Changed?.Invoke();
        }

        private void End(bool send)
        {
            if (!Current) return;
            Current = null;
            avatar.State.StopEmote();
            if (send) owner.SendEmote(StopId);
            if (health.IsDowned) ViewWeight = 0f;
            enabled = Presenting;
            Changed?.Invoke();
        }

        private void Update()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (Current)
            {
                elapsed += dt;
                int flags = Disruption();
                bool disrupted = (flags & ~disruption) != 0;
                disruption = flags;
                if (disrupted) End(owner.IsOwner);
                else if (!Current.Loop && elapsed * Current.Speed >= Current.Clip.length - EmoteDefinition.BlendDuration * Current.Speed) End(false);
            }
            if (owner.IsOwner)
            {
                bool presenting = Presenting;
                ViewWeight = health.IsDowned ? 0f : Mathf.MoveTowards(ViewWeight, Current ? 1f : 0f, dt / EmoteDefinition.BlendDuration);
                if (presenting != Presenting) Changed?.Invoke();
            }
            enabled = Presenting;
        }

        private int Disruption() =>
            (motor.Grounded ? 0 : 1) | (carry.IsCarried ? 2 : 0) | (seating.Seated ? 4 : 0) | (health.IsDowned ? 8 : 0);

        private void Disturbed() { if (owner.IsOwner) Stop(); }

        private void OnDestroy() { if (motor) motor.Disturbed -= Disturbed; }
    }
}
