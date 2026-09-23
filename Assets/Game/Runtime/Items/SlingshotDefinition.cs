using UnityEngine;

namespace TwoBirds
{
    [System.Serializable]
    public struct SlingshotChargePoseSettings
    {
        [Tooltip("Offset from automatic charged placement in metres: right, up, forward. Uses camera axes in first person and aim axes remotely.")]
        public Vector3 ChargedPositionOffset;
        [Tooltip("Holding palm rotation at full charge, in degrees relative to the pose frame.")]
        public Vector3 ChargedPalmEuler;
        [Tooltip("Offset from DrawCenter at full charge in slingshot-local axes, scaled with the item. Zero keeps the authored draw.")]
        public Vector3 PullingHandDrawOffset;

        public static SlingshotChargePoseSettings Default => new() { ChargedPalmEuler = new(0f, 0f, 90f) };
    }

    [CreateAssetMenu(menuName = "Two Birds/Slingshot Definition")]
    public sealed class SlingshotDefinition : ItemDefinition
    {
        public override bool CanBeIngredient => false;
        [Header("Slingshot Charge Poses")]
        public ItemPalmContact PullingPalmContact;
        public bool OverrideFirstPersonChargePose, OverrideRemoteChargePose;
        public SlingshotChargePoseSettings FirstPersonChargePose = SlingshotChargePoseSettings.Default;
        public SlingshotChargePoseSettings RemoteChargePose = SlingshotChargePoseSettings.Default;

        [Header("Pebble Settings")]
        [Min(0)] public int PebbleDamage = 10;
        [Min(0f)] public float RecoverySeconds = 0.5f;
        [Range(0f, 1f)] public float ReboundRetention = 0.75f;
        public PebbleProjectile PebblePrefab;
        public ParticleSystem DirtPrefab;

        public override void NotifyContentChanged()
        {
            Stackable = false;
            MaxStack = 1;
            base.NotifyContentChanged();
        }
    }
}
