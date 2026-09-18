namespace TEHhub.OffsetDoctor.Strategies;

using TEHhub.OffsetDoctor.Manifest;

public sealed class RecoveryContext
{
    public int? ExpectedGoldAmount { get; init; }
    public Dictionary<string, int> ProvisionalOffsets { get; init; } = new();
    public List<OffsetNode> AllNodes { get; init; } = [];
    public int BranchDepth { get; init; } = 0;
    public int MaxBranchDepth { get; init; } = 5;
    public Func<ValueKind, IRecoveryStrategy?>? StrategyResolver { get; set; }

    public OffsetNode? FindNode(string id) =>
        AllNodes.FirstOrDefault(n => n.Id == id);

    public OffsetNode? FindChild(string parentId) =>
        AllNodes.FirstOrDefault(n => n.ParentId == parentId);

    public RecoveryContext CreateChildContext(Dictionary<string, int>? branchOverrides = null)
    {
        var overrides = new Dictionary<string, int>(ProvisionalOffsets);
        if (branchOverrides != null)
        {
            foreach (var (k, v) in branchOverrides) overrides[k] = v;
        }

        return new RecoveryContext
        {
            ExpectedGoldAmount = ExpectedGoldAmount,
            ProvisionalOffsets = overrides,
            AllNodes = AllNodes,
            BranchDepth = BranchDepth + 1,
            MaxBranchDepth = MaxBranchDepth,
            StrategyResolver = StrategyResolver
        };
    }
}
