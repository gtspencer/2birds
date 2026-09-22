using UnityEngine;

namespace TwoBirds
{
    [DisallowMultipleComponent, RequireComponent(typeof(WorldItem), typeof(SlingshotPresentation))]
    public sealed class SlingshotItemUse : ItemUseBehaviour
    {
        private WorldItem item;
        private PlayerEquipment user;
        public override bool IsCharging => user;
        public override float Charge01 => user ? user.ItemCharge01(item.Definition) : 0f;
        private void Awake() => item = GetComponent<WorldItem>();
        public override void BeginUse(PlayerEquipment equipment)
        {
            if (!user && item.Definition is SlingshotDefinition) user = equipment;
        }
        public override void EndUse()
        {
            if (!user) return;
            user.TryFireSlingshot(item);
            CancelUse();
        }
        public override void CancelUse() => user = null;
        private void OnDisable() { item.InterruptUse(); CancelUse(); }
    }
}
