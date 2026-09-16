namespace ExpeditionPathOptimizer.PathPlannerData
{
    public enum ExpeditionChestType
    {
        Generic,
        Currency,
        Artifact,
        Equipment,
        Gem,
        Map,
        Unique,
        Fragment
    }

    public class Chest : IChest
    {
        public ExpeditionChestType Type { get; set; }

        public Chest(ExpeditionChestType type)
        {
            this.Type = type;
        }
    }
}
