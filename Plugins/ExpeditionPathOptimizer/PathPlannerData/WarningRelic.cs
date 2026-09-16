namespace ExpeditionPathOptimizer.PathPlannerData
{
    public class WarningRelic : IExpeditionRelic
    {
        public string WarningName { get; }

        public WarningRelic(string warningName)
        {
            this.WarningName = warningName;
        }

        public (double Multiplier, double Increase) GetScoreMultiplier(IExpeditionLoot loot)
        {
            return (1.0, 0.0);
        }
    }
}
