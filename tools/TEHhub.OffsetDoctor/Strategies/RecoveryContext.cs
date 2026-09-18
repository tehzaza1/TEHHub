namespace TEHhub.OffsetDoctor.Strategies;

using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Validation;

public sealed class RecoveryContext
{
    public int? ExpectedGoldAmount { get; init; }
    public int? ExpectedHpCurrent { get; init; }
    public int? ExpectedHpTotal { get; init; }
    public int? ExpectedMpCurrent { get; init; }
    public int? ExpectedMpTotal { get; init; }
    public int? ExpectedEsCurrent { get; init; }
    public int? ExpectedEsTotal { get; init; }

    public Dictionary<string, int> ProvisionalOffsets { get; init; } = new();
    public List<OffsetNode> AllNodes { get; init; } = [];
    public int BranchDepth { get; init; } = 0;
    public int MaxBranchDepth { get; init; } = 5;
    public Func<ValueKind, IRecoveryStrategy?>? StrategyResolver { get; set; }

    public static RecoveryContext FromGroundTruth(ValidationGroundTruth? truth, List<OffsetNode>? allNodes = null)
    {
        return new RecoveryContext
        {
            ExpectedGoldAmount = truth?.ExpectedGold,
            ExpectedHpCurrent = truth?.ExpectedHpCurrent,
            ExpectedHpTotal = truth?.ExpectedHpTotal,
            ExpectedMpCurrent = truth?.ExpectedMpCurrent,
            ExpectedMpTotal = truth?.ExpectedMpTotal,
            ExpectedEsCurrent = truth?.ExpectedEsCurrent,
            ExpectedEsTotal = truth?.ExpectedEsTotal,
            AllNodes = allNodes ?? []
        };
    }

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
            ExpectedHpCurrent = ExpectedHpCurrent,
            ExpectedHpTotal = ExpectedHpTotal,
            ExpectedMpCurrent = ExpectedMpCurrent,
            ExpectedMpTotal = ExpectedMpTotal,
            ExpectedEsCurrent = ExpectedEsCurrent,
            ExpectedEsTotal = ExpectedEsTotal,
            ProvisionalOffsets = overrides,
            AllNodes = AllNodes,
            BranchDepth = BranchDepth + 1,
            MaxBranchDepth = MaxBranchDepth,
            StrategyResolver = StrategyResolver
        };
    }
}
