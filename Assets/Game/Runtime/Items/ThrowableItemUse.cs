using UnityEngine;

namespace TwoBirds
{
    [DisallowMultipleComponent, RequireComponent(typeof(WorldItem))]
    public sealed class ThrowableItemUse : ItemUseBehaviour
    {
        private WorldItem item;
        private PlayerEquipment user;
        private double pressTime;

        public override bool IsCharging => user != null;
        public override float Charge01 => IsCharging
            ? Mathf.Clamp01((float)((Time.timeAsDouble - pressTime) / item.Definition.ThrowChargeTime)) : 0f;

        private void Awake() => item = GetComponent<WorldItem>();

        public override void BeginUse(PlayerEquipment user)
        {
            if (IsCharging || item.Definition == null) return;
            this.user = user;
            pressTime = Time.timeAsDouble;
        }

        public override void EndUse()
        {
            if (!IsCharging) return;
            var equipment = user;
            uint id = item.Record.Motion.Id;
            float speed = Mathf.Lerp(item.Definition.MinThrowSpeed, item.Definition.MaxThrowSpeed, Charge01);
            CancelUse();
            if (equipment.TryReleaseItem(id, speed) && item && item.Record.Motion.Id == id) item.StartPickupCooldown();
        }

        public override void CancelUse()
        {
            user = null;
            pressTime = 0;
        }

        private void OnDisable()
        {
            item.InterruptUse();
            CancelUse();
        }
    }
}
