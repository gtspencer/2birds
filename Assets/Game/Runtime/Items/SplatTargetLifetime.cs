using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed class SplatTargetLifetime : MonoBehaviour
    {
        private readonly HashSet<SplatPresentation> splats = new();
        private AvatarPresentation avatar;
        private PlayerHealth health;
        private PlayerPotionEffects effects;
        private uint lifetime, reset;
        internal event System.Action<SplatTargetLifetime> Destroyed;

        internal static SplatTargetLifetime Get(Transform root)
        {
            return root.TryGetComponent<SplatTargetLifetime>(out var owner) ? owner : root.gameObject.AddComponent<SplatTargetLifetime>();
        }

        internal void BindPlayer(PlayerInventory player)
        {
            if (avatar)
            {
                LifeChanged();
                return;
            }
            avatar = player.GetComponent<PlayerAvatarPresentation>().Presentation;
            health = player.GetComponent<PlayerHealth>();
            effects = player.Effects;
            lifetime = effects.Lifetime; reset = effects.Reset;
            avatar.WillUnbind += Unbind;
            avatar.DidBind += Bind;
            health.LifeChanged += LifeChanged;
        }

        internal void Add(SplatPresentation splat) => splats.Add(splat);
        internal void Remove(SplatPresentation splat) => splats.Remove(splat);
        private void Unbind(AvatarBinding binding) => Clear();
        private void Bind(AvatarBinding binding)
        {
            foreach (var splat in new List<SplatPresentation>(splats))
            {
                var bone = binding.GetBone(splat.Event.Target.Bone);
                if (bone) splat.Attach(bone);
                else splat.Dispose();
            }
        }

        private void LifeChanged()
        {
            if (effects.Lifetime == lifetime && effects.Reset == reset) return;
            Clear();
            lifetime = effects.Lifetime; reset = effects.Reset;
        }

        internal void Clear()
        {
            foreach (var splat in new List<SplatPresentation>(splats)) if (splat) splat.Dispose();
            splats.Clear();
        }

        internal static void Invalidate(Transform root)
        {
            if (root && root.TryGetComponent<SplatTargetLifetime>(out var owner)) owner.Clear();
        }

        private void OnDisable() => Clear();
        private void OnDestroy()
        {
            Clear();
            if (avatar) { avatar.WillUnbind -= Unbind; avatar.DidBind -= Bind; }
            if (health) health.LifeChanged -= LifeChanged;
            Destroyed?.Invoke(this);
        }
    }
}
