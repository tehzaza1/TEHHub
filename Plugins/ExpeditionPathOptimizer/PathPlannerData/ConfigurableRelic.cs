namespace ExpeditionPathOptimizer.PathPlannerData
{
    public class ConfigurableRelic : IExpeditionRelic
    {
        private readonly double multiplier;
        private readonly double increase;
        private readonly bool isMonsterRelic;

        public ConfigurableRelic(double multiplier, double increase, bool isMonsterRelic)
        {
            this.multiplier = multiplier;
            this.increase = increase;
            this.isMonsterRelic = isMonsterRelic;
        }

        public (double Multiplier, double Increase) GetScoreMultiplier(IExpeditionLoot loot)
        {
            return (this.isMonsterRelic, loot) switch
            {
                (true, IMonster) or (false, IChest) => (this.multiplier, this.increase),
                _ => (1.0, 0.0),
            };
        }
    }
}
