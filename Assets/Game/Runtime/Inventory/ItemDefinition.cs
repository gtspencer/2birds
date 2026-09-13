using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Item Definition")]
    public sealed class ItemDefinition : ScriptableObject
    {
        public string DefinitionId;
        public string DisplayName;
        public Sprite Icon;
        public int MaximumStack = 1;
    }
}
