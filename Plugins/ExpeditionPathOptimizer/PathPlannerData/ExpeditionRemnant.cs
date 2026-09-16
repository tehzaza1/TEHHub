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
        public int RuneSlots { get; set; } = 0;
        public int AnchorIdx { get; set; } = -1;
        public int AnchorPos { get; set; }
        public string? AnchorRune { get; set; }
        public bool IsUnique { get; set; }
        public List<int> GoldenSlots { get; set; } = new();
        public IReadOnlyList<RuneshapeRecipeOffer> RecipeOffers { get; set; } = Array.Empty<RuneshapeRecipeOffer>();
        public RuneshapeRecipeOffer? BestPriceRecipe { get; set; }
        public RuneshapeRecipeOffer? BestRuneRecipe { get; set; }

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
            RuneshapeRecipeOffer? bestPriceRecipe,
            RuneshapeRecipeOffer? bestRuneRecipe)
        {
            this.EntityId = entityId;
            this.EntityAddress = entityAddress;
            this.WorldPos = worldPos;
            this.GridPos = gridPos;
            this.RuneSlots = runeSlots is >= 0 and <= 16 ? runeSlots : 0;
            this.AnchorIdx = anchorIdx;
            this.AnchorPos = anchorPos;
            this.AnchorRune = anchorRune;
            this.IsUnique = isUnique;
            this.GoldenSlots = goldenSlots ?? new List<int>();
            this.RecipeOffers = recipeOffers ?? Array.Empty<RuneshapeRecipeOffer>();
            this.BestPriceRecipe = bestPriceRecipe;
            this.BestRuneRecipe = bestRuneRecipe;
        }

        public double CalculateBaseRuneWeight(IReadOnlyDictionary<string, double> weights)
        {
            if (this.BestRuneRecipe?.PropagatedRunes == null || this.BestRuneRecipe.PropagatedRunes.Count == 0)
                return 0.0;

            double sum = 0.0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in this.BestRuneRecipe.PropagatedRunes)
            {
                if (seen.Add(r))
                {
                    sum += weights.GetValueOrDefault(r, 20.0);
                }
            }
            return sum;
        }
    }
}
