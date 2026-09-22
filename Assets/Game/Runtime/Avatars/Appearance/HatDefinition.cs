using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Cosmetics/Hat")]
    public sealed class HatDefinition : ScriptableObject
    {
        public HatId Id;
        public string DisplayName;
        public Sprite Icon;
        public GameObject Source, Visual;
        public bool UnlockedByDefault;
        public CosmeticFit Fit = CosmeticFit.Identity;
    }
}
