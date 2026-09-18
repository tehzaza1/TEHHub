namespace TEHhub.OffsetDoctor.Baseline;

using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Recovery;

public sealed class BaselineComparisonEngine
{
    public BaselineComparisonResult Compare(BaselineSnapshot baseline, OffsetDoctorReport currentReport)
    {
        var deltas = new List<BaselineDelta>();
        var baselineMap = baseline.Nodes.ToDictionary(n => n.NodeId, n => n);
        var currentMap = currentReport.Results.ToDictionary(r => r.NodeId, r => r);

        // 1. Check for missing / new nodes
        foreach (var (nodeId, bNode) in baselineMap)
        {
            if (!currentMap.ContainsKey(nodeId))
            {
                deltas.Add(new BaselineDelta
                {
                    Severity = bNode.IsOptionalStateDependent ? DeltaSeverity.Warning : DeltaSeverity.Critical,
                    NodeId = nodeId,
                    DisplayName = bNode.DisplayName,
                    Category = bNode.Category,
                    BaselineStatus = bNode.Status,
                    CurrentStatus = null,
                    Description = $"Node present in baseline is missing from current manifest/scan."
                });
            }
        }

        foreach (var (nodeId, cResult) in currentMap)
        {
            if (!baselineMap.ContainsKey(nodeId))
            {
                deltas.Add(new BaselineDelta
                {
                    Severity = DeltaSeverity.Informational,
                    NodeId = nodeId,
                    DisplayName = cResult.NodeDisplayName,
                    Category = cResult.Category,
                    BaselineStatus = null,
                    CurrentStatus = cResult.Status,
                    CurrentValue = cResult.ExtractedValue?.ToString(),
                    Description = "New node added to manifest since baseline was captured."
                });
            }
        }

        // 2. Compare matched nodes
        foreach (var (nodeId, bNode) in baselineMap)
        {
            if (!currentMap.TryGetValue(nodeId, out var cResult))
            {
                continue;
            }

            var bStatus = bNode.Status;
            var cStatus = cResult.Status;
            var bVal = bNode.ExtractedValue ?? bNode.ResolvedAddress ?? bNode.ObservedAddress;
            var cVal = cResult.ExtractedValue?.ToString() ??
                       (cResult.ResolvedAddress != IntPtr.Zero ? $"0x{cResult.ResolvedAddress.ToInt64():X}" : null) ??
                       (cResult.ObservedAddress != IntPtr.Zero ? $"0x{cResult.ObservedAddress.ToInt64():X}" : null);

            // A. Status Regressions
            if (bStatus == ValidationStatus.VALID && cStatus == ValidationStatus.BROKEN)
            {
                deltas.Add(new BaselineDelta
                {
                    Severity = DeltaSeverity.Critical,
                    NodeId = nodeId,
                    DisplayName = bNode.DisplayName,
                    Category = bNode.Category,
                    BaselineStatus = bStatus,
                    CurrentStatus = cStatus,
                    BaselineValue = bVal,
                    CurrentValue = cVal,
                    Description = $"CRITICAL REGRESSION: VALID node became BROKEN. Error: {cResult.ErrorMessage}"
                });
                continue;
            }

            if (bStatus == ValidationStatus.UNVERIFIED && cStatus == ValidationStatus.BROKEN)
            {
                deltas.Add(new BaselineDelta
                {
                    Severity = DeltaSeverity.Critical,
                    NodeId = nodeId,
                    DisplayName = bNode.DisplayName,
                    Category = bNode.Category,
                    BaselineStatus = bStatus,
                    CurrentStatus = cStatus,
                    BaselineValue = bVal,
                    CurrentValue = cVal,
                    Description = $"CRITICAL REGRESSION: UNVERIFIED node became BROKEN. Error: {cResult.ErrorMessage}"
                });
                continue;
            }

            if (bStatus == ValidationStatus.VALID && cStatus == ValidationStatus.UNVERIFIED)
            {
                bool isGroundTruthDependent = nodeId == "psd_gold_field" || nodeId.StartsWith("comp_life_");
                deltas.Add(new BaselineDelta
                {
                    Severity = isGroundTruthDependent ? DeltaSeverity.Informational : DeltaSeverity.Warning,
                    NodeId = nodeId,
                    DisplayName = bNode.DisplayName,
                    Category = bNode.Category,
                    BaselineStatus = bStatus,
                    CurrentStatus = cStatus,
                    BaselineValue = bVal,
                    CurrentValue = cVal,
                    Description = $"Status changed from VALID to UNVERIFIED (Ground truth may not have been supplied or dynamic state changed)."
                });
                continue;
            }

            if (bStatus == ValidationStatus.UNVERIFIED && cStatus == ValidationStatus.VALID)
            {
                deltas.Add(new BaselineDelta
                {
                    Severity = DeltaSeverity.Informational,
                    NodeId = nodeId,
                    DisplayName = bNode.DisplayName,
                    Category = bNode.Category,
                    BaselineStatus = bStatus,
                    CurrentStatus = cStatus,
                    BaselineValue = bVal,
                    CurrentValue = cVal,
                    Description = $"Status improved from UNVERIFIED to VALID."
                });
                continue;
            }

            if (cStatus == ValidationStatus.BLOCKED && bStatus != ValidationStatus.BLOCKED)
            {
                deltas.Add(new BaselineDelta
                {
                    Severity = DeltaSeverity.Warning,
                    NodeId = nodeId,
                    DisplayName = bNode.DisplayName,
                    Category = bNode.Category,
                    BaselineStatus = bStatus,
                    CurrentStatus = cStatus,
                    BaselineValue = bVal,
                    CurrentValue = cVal,
                    Description = $"Node became BLOCKED due to upstream parent failure."
                });
                continue;
            }

            // B. Static Root / Pattern checks
            if (bNode.IsStaticRoot && bNode.PatternName != null)
            {
                if (bStatus == ValidationStatus.VALID && cStatus != ValidationStatus.VALID)
                {
                    deltas.Add(new BaselineDelta
                    {
                        Severity = DeltaSeverity.Critical,
                        NodeId = nodeId,
                        DisplayName = bNode.DisplayName,
                        Category = bNode.Category,
                        BaselineStatus = bStatus,
                        CurrentStatus = cStatus,
                        BaselineValue = bVal,
                        CurrentValue = cVal,
                        Description = $"Static root pattern '{bNode.PatternName}' failed to resolve."
                    });
                }
            }

            // C. Dynamic / State-Dependent value changes
            if (IsDynamicNode(nodeId, bNode))
            {
                if (bVal != cVal && !string.IsNullOrEmpty(bVal) && !string.IsNullOrEmpty(cVal))
                {
                    deltas.Add(new BaselineDelta
                    {
                        Severity = DeltaSeverity.Informational,
                        NodeId = nodeId,
                        DisplayName = bNode.DisplayName,
                        Category = bNode.Category,
                        BaselineStatus = bStatus,
                        CurrentStatus = cStatus,
                        BaselineValue = bVal,
                        CurrentValue = cVal,
                        Description = $"Dynamic runtime value updated ({bVal} -> {cVal})."
                    });
                }
            }
            else
            {
                // Structural value / address change
                if (bVal != cVal && !string.IsNullOrEmpty(bVal) && !string.IsNullOrEmpty(cVal))
                {
                    deltas.Add(new BaselineDelta
                    {
                        Severity = DeltaSeverity.Informational,
                        NodeId = nodeId,
                        DisplayName = bNode.DisplayName,
                        Category = bNode.Category,
                        BaselineStatus = bStatus,
                        CurrentStatus = cStatus,
                        BaselineValue = bVal,
                        CurrentValue = cVal,
                        Description = $"Structural pointer address changed ({bVal} -> {cVal})."
                    });
                }
            }
        }

        return new BaselineComparisonResult
        {
            Baseline = baseline,
            CurrentReport = currentReport,
            Deltas = deltas
        };
    }

    private static bool IsDynamicNode(string nodeId, BaselineNodeSnapshot node)
    {
        if (node.IsOptionalStateDependent) return true;
        if (nodeId.StartsWith("comp_life_")) return true;
        if (nodeId.StartsWith("comp_buffs_")) return true;
        if (nodeId.StartsWith("ui_")) return true;
        if (nodeId.StartsWith("loading_state_")) return true;
        if (nodeId == "psd_gold_field") return true;
        return false;
    }
}
