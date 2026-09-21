using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Crafting Recipe Book")]
    public sealed class CraftingRecipeBook : ScriptableObject
    {
        [Serializable] public struct Requirement { public ItemDefinition Item; [Min(1)] public int Quantity; }
        [Serializable] public struct Recipe
        {
            public Requirement[] Ingredients;
            public ItemDefinition Output;
            [Min(1)] public int MinimumTotal;
        }
        public Recipe[] Overrides = Array.Empty<Recipe>();
        public Recipe[] Exact = Array.Empty<Recipe>();

        public ItemDefinition Resolve(IReadOnlyList<IngredientRecord> ingredients)
        {
            if (ingredients.Count == 0) return null;
            foreach (var recipe in Overrides) if (Matches(recipe, ingredients, false)) return recipe.Output;
            foreach (var recipe in Exact) if (Matches(recipe, ingredients, true)) return recipe.Output;
            return null;
        }

        private static bool Matches(Recipe recipe, IReadOnlyList<IngredientRecord> ingredients, bool exact)
        {
            if (!recipe.Output || recipe.Ingredients == null || ingredients.Count < recipe.MinimumTotal) return false;
            var required = new Dictionary<byte, int>();
            int total = 0;
            foreach (var requirement in recipe.Ingredients)
            {
                if (!requirement.Item || requirement.Quantity < 1) return false;
                required.TryGetValue(requirement.Item.ItemId, out int count);
                required[requirement.Item.ItemId] = count + requirement.Quantity;
                total += requirement.Quantity;
            }
            if (exact && total != ingredients.Count) return false;
            foreach (var pair in required)
            {
                int found = 0;
                foreach (var ingredient in ingredients) if (ingredient.Definition == pair.Key) found++;
                if (found < pair.Value) return false;
            }
            return true;
        }
    }
}
