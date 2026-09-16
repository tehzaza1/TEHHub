namespace ExpeditionPathOptimizer.PathPlannerData
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    public class ExpeditionRemnant
    {
        public uint EntityId { get; set; }
        public IntPtr EntityAddress { get; set; }
        public Vector3 WorldPos { get; set; }
        public Vector2 GridPos { get; set; }
        public int RuneSlots { get; set; } = 4;
        public int AnchorIdx { get; set; } = -1;
        public int AnchorPos { get; set; }
        public string? AnchorRune { get; set; }
        public bool IsUnique { get; set; }
        public List<int> GoldenSlots { get; set; } = new();
        public IReadOnlyList<RuneshapeRecipeOffer> RecipeOffers { get; set; } = Array.Empty<RuneshapeRecipeOffer>();
        public RuneshapeRecipeOffer? BestRecipe { get; set; }

        public ExpeditionRemnant()
        {
        }

        public ExpeditionRemnant(
            uint entityId,
            IntPtr entityAddress,
            Vector3 worldPos,
            Vector2 gridPos,
            int runeSlots,
            int anchorIdx,
            int anchorPos,
            string? anchorRune,
            bool isUnique,
            List<int> goldenSlots,
            IReadOnlyList<RuneshapeRecipeOffer> recipeOffers,
            RuneshapeRecipeOffer? bestRecipe)
        {
            this.EntityId = entityId;
            this.EntityAddress = entityAddress;
            this.WorldPos = worldPos;
            this.GridPos = gridPos;
            this.RuneSlots = Math.Clamp(runeSlots, 1, 16);
            this.AnchorIdx = anchorIdx;
            this.AnchorPos = anchorPos;
            this.AnchorRune = anchorRune;
            this.IsUnique = isUnique;
            this.GoldenSlots = goldenSlots ?? new List<int>();
            this.RecipeOffers = recipeOffers ?? Array.Empty<RuneshapeRecipeOffer>();
            this.BestRecipe = bestRecipe;
        }

        public double CalculateBaseRuneWeight(Dictionary<string, double> weights)
        {
            if (this.BestRecipe?.PropagatedRunes == null || this.BestRecipe.PropagatedRunes.Count == 0)
                return 0.0;

            double sum = 0.0;
            foreach (var r in this.BestRecipe.PropagatedRunes)
            {
                sum += weights.GetValueOrDefault(r, 20.0);
            }
            return sum;
        }
    }
}
