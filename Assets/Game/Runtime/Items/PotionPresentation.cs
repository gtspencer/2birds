using UnityEngine;

namespace TwoBirds
{
    public sealed class PotionPresentation : MonoBehaviour
    {
        [System.Serializable]
        public struct ColorTarget
        {
            public Renderer Renderer;
            public int MaterialSlot;
            public string Property;
        }

        [SerializeField] private ColorTarget[] targets = System.Array.Empty<ColorTarget>();
        private int[] properties;
        private MaterialPropertyBlock block;

        internal void ApplyVisual(GameObject visual, ItemDefinition definition)
        {
            if (definition is not PotionDefinition potion) return;
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            var source = transform.Find("VisualRoot").GetComponentsInChildren<Renderer>(true);
            var properties = new MaterialPropertyBlock();
            foreach (var target in targets)
            {
                int index = System.Array.IndexOf(source, target.Renderer);
                if (index < 0 || index >= renderers.Length) continue;
                properties.Clear();
                renderers[index].GetPropertyBlock(properties, target.MaterialSlot);
                properties.SetColor(Shader.PropertyToID(target.Property), potion.Color);
                renderers[index].SetPropertyBlock(properties, target.MaterialSlot);
            }
        }

        public void ApplyDefinition(ItemDefinition definition)
        {
            if (definition is not PotionDefinition potion) return;
            if (properties == null)
            {
                properties = new int[targets.Length];
                for (int i = 0; i < targets.Length; i++) properties[i] = Shader.PropertyToID(targets[i].Property);
                block = new MaterialPropertyBlock();
            }
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                if (!target.Renderer) continue;
                block.Clear();
                target.Renderer.GetPropertyBlock(block, target.MaterialSlot);
                block.SetColor(properties[i], potion.Color);
                target.Renderer.SetPropertyBlock(block, target.MaterialSlot);
            }
        }
    }
}
