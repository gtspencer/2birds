using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Held Item Settings")]
    public sealed class HeldItemSettings : ScriptableObject
    {
        public event System.Action ContentChanged;
        public void NotifyContentChanged() => ContentChanged?.Invoke();
        private void OnValidate() => NotifyContentChanged();
        public SlingshotChargePoseSettings SlingshotChargePose = SlingshotChargePoseSettings.Default;
        public HeldItemSpatialSettings FirstPersonPose = HeldItemSpatialSettings.FirstPersonDefault;
        public HeldItemPoseSettings HoldSettings = HeldItemPoseSettings.Default;
        public HeldItemPoseSettings HeavyHoldSettings = HeldItemPoseSettings.HeavyDefault;

        public HeldItemPoseSettings ResolveHold(ItemDefinition item) => item.OverrideHoldSettings ? item.HandPose :
            item.HoldMode == ItemHoldMode.Heavy ? HeavyHoldSettings : HoldSettings;
        public HeldItemSpatialSettings ResolveSpatial(ItemDefinition item, bool firstPerson) =>
            !firstPerson ? HeldItemSpatialSettings.From(ResolveHold(item)) : item.OverrideFirstPersonPose ? item.FirstPersonPose :
            item.HoldMode == ItemHoldMode.Heavy ? HeldItemSpatialSettings.From(ResolveHold(item)) : FirstPersonPose;
        public SlingshotChargePoseSettings ResolveCharge(SlingshotDefinition item, bool firstPerson) => firstPerson
            ? item.OverrideFirstPersonChargePose ? item.FirstPersonChargePose : SlingshotChargePose
            : item.OverrideRemoteChargePose ? item.RemoteChargePose : SlingshotChargePose;

        public void SetOverride(ItemDefinition item, string group, bool enabled)
        {
            switch (group)
            {
                case nameof(ItemDefinition.OverrideHoldSettings):
                    if (enabled && !item.OverrideHoldSettings) item.HandPose = ResolveHold(item);
                    item.OverrideHoldSettings = enabled; break;
                case nameof(ItemDefinition.OverrideFirstPersonPose):
                    if (enabled && !item.OverrideFirstPersonPose) item.FirstPersonPose = ResolveSpatial(item, true);
                    item.OverrideFirstPersonPose = enabled; break;
                case nameof(SlingshotDefinition.OverrideFirstPersonChargePose) when item is SlingshotDefinition sling:
                    if (enabled && !sling.OverrideFirstPersonChargePose) sling.FirstPersonChargePose = ResolveCharge(sling, true);
                    sling.OverrideFirstPersonChargePose = enabled; break;
                case nameof(SlingshotDefinition.OverrideRemoteChargePose) when item is SlingshotDefinition sling:
                    if (enabled && !sling.OverrideRemoteChargePose) sling.RemoteChargePose = ResolveCharge(sling, false);
                    sling.OverrideRemoteChargePose = enabled; break;
            }
            item.NotifyContentChanged();
        }
    }
}
