namespace TEHhub.OffsetDoctor.Strategies;

using TEHhub.OffsetDoctor.Manifest;

public sealed class RecoveryContext
{
    public int? ExpectedGoldAmount { get; init; }
    public Dictionary<string, int> ProvisionalOffsets { get; init; } = new();
    public List<OffsetNode> AllNodes { get; init; } = [];

    public OffsetNode? FindNode(string id) =>
        AllNodes.FirstOrDefault(n => n.Id == id);

    public OffsetNode? FindChild(string parentId) =>
        AllNodes.FirstOrDefault(n => n.ParentId == parentId);
}
