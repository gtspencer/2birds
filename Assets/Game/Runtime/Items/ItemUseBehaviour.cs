using UnityEngine;

namespace TwoBirds
{
    public abstract class ItemUseBehaviour : MonoBehaviour
    {
        public virtual bool IsCharging => false;
        public virtual float Charge01 => 0f;
        public abstract void BeginUse(PlayerEquipment user);
        public abstract void EndUse();
        public abstract void CancelUse();
    }
}
