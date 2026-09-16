namespace ExpeditionPathOptimizer.PathPlannerData
{
    public interface IExpeditionRelic
    {
        (double Multiplier, double Increase) GetScoreMultiplier(IExpeditionLoot loot);
    }
}
