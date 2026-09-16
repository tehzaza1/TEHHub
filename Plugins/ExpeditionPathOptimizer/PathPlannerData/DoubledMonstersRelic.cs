namespace ExpeditionPathOptimizer.PathPlannerData
{
    public class DoubledMonstersRelic : IExpeditionRelic
    {
        public (double Multiplier, double Increase) GetScoreMultiplier(IExpeditionLoot loot)
        {
            if (loot is RunicMonster)
            {
                return (2.0, 0.0);
            }

            return (1.0, 0.0);
        }
    }
}
