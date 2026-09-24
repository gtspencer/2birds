using UnityEngine;

namespace TwoBirds
{
    internal struct HeldItemPresentationInput
    {
        internal ItemDefinition SelectedDefinition, ActionDefinition;
        internal uint SelectedId;
        internal ItemActionSnapshot Action;
        internal double ActionAge;
        internal bool FirstPerson, CanEquip, CanCharge, HasAction;
        internal AvatarPresentationInput Placement;
        internal Pose Aim, Projectile;
        internal bool ProjectileAvailable, ProjectileUnavailable;
        internal int EnvironmentMask;
    }
}
