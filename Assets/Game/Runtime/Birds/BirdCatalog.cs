using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Birds/Catalog")]
    public sealed class BirdCatalog : ScriptableObject
    {
        [Tooltip("Increment when static scenery or model hit geometry changes.")]
        [Min(1)] public int ContentVersion = 1;
        public BirdSpecies[] Species = System.Array.Empty<BirdSpecies>();
    }
}
