namespace TEHhub.OffsetDoctor.Baseline;

using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Recovery;
using TEHhub.OffsetDoctor.Validation;

public sealed class BaselineCaptureEngine
{
    private readonly OffsetRecoveryEngine _recoveryEngine = new();

    public BaselineSnapshot Capture(
        IProcessMemoryReader reader,
        ValidationGroundTruth? groundTruth,
        string? commitHash = null)
    {
        var report = _recoveryEngine.RunValidation(reader, groundTruth);
        var manifestNodes = OffsetManifest.CreateFullRepositoryManifest();
        var nodeMap = manifestNodes.ToDictionary(n => n.Id, n => n);

        var nodeSnapshots = new List<BaselineNodeSnapshot>();

        foreach (var result in report.Results)
        {
            nodeMap.TryGetValue(result.NodeId, out var manifestNode);

            var evidenceSummary = result.Evidence.Select(e =>
                $"[{ (e.Passed ? "PASS" : "FAIL") }] {e.RuleName}: {e.Description}").ToList();

            var nodeSnapshot = new BaselineNodeSnapshot
            {
                NodeId = result.NodeId,
                DisplayName = result.NodeDisplayName,
                Category = result.Category,
                ParentId = result.ParentId,
                ConfiguredOffset = result.ConfiguredOffset,
                PatternName = manifestNode?.StaticPatternName,
                Kind = manifestNode?.Kind ?? ValueKind.PointerField,
                Status = result.Status,
                ObservedAddress = result.ObservedAddress != IntPtr.Zero ? $"0x{result.ObservedAddress.ToInt64():X}" : null,
                ResolvedAddress = result.ResolvedAddress != IntPtr.Zero ? $"0x{result.ResolvedAddress.ToInt64():X}" : null,
                TraversalAddress = result.TraversalAddress != IntPtr.Zero ? $"0x{result.TraversalAddress.ToInt64():X}" : null,
                ExtractedValue = result.ExtractedValue?.ToString(),
                ErrorMessage = result.ErrorMessage,
                EvidenceSummary = evidenceSummary,
                IsOptionalStateDependent = manifestNode?.IsOptionalStateDependent ?? false,
                IsStaticRoot = manifestNode?.Kind == ValueKind.StaticPattern || manifestNode?.ParentId == null,
                StaticResolutionKind = manifestNode?.StaticPatternResolution
            };

            nodeSnapshots.Add(nodeSnapshot);
        }

        return new BaselineSnapshot
        {
            ToolVersion = "1.0.0",
            CommitHash = commitHash,
            TimestampUtc = report.TimestampUtc,
            ProcessName = reader.Metadata.ProcessName,
            ProcessId = reader.Metadata.ProcessId,
            ModuleBase = $"0x{reader.Metadata.ModuleBase.ToInt64():X}",
            ModuleSize = reader.Metadata.ModuleMemorySize,
            GroundTruth = groundTruth,
            Summary = new BaselineSummary
            {
                ValidCount = report.ValidCount,
                BrokenCount = report.BrokenCount,
                BlockedCount = report.BlockedCount,
                UnverifiedCount = report.UnverifiedCount,
                TotalNodesCount = report.TotalNodesCount
            },
            Nodes = nodeSnapshots
        };
    }
}
