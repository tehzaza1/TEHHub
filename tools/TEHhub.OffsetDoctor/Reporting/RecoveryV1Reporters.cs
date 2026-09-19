namespace TEHhub.OffsetDoctor.Reporting;

using System.Text.Json;
using System.Text.Json.Serialization;
using TEHhub.OffsetDoctor.RecoveryV1;

public static class RecoveryJsonReportExporter
{
    public static string Serialize(RecoveryReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
}

public static class RecoveryConsoleReportWriter
{
    public static void Write(TextWriter writer, RecoveryReport report)
    {
        writer.WriteLine($"Recovery {report.SchemaVersion} | {report.Identity.ProcessName} " +
            $"PID {report.Identity.ProcessId} | build {report.Identity.FileVersion} | " +
            $"module 0x{report.Identity.ModuleBase:X}+0x{report.Identity.ModuleSize:X}");
        foreach (RecoveryResult result in report.Results)
        {
            writer.WriteLine($"{result.Target.Id} [{result.Target.Scope.Name}/{result.Target.Scope.InstanceId}] " +
                $"{result.Decision.TerminalResult} | strategy {result.Target.StrategyId} | Applied=false");
            writer.WriteLine($"  dependencies: {string.Join(", ", result.Dependencies.Select(d =>
                $"{d.TargetId}:{d.Result}:validated={d.IndependentlyValidated}:" +
                $"anchor={(d.Anchor is long anchor ? $"0x{anchor:X}" : "none")}:" +
                $"candidate={d.CandidateId ?? "none"}:digest={d.EvidenceDigest}"))}");
            writer.WriteLine($"  context: {string.Join(", ", result.Context.Select(c => $"{c.Key}={c.Value}"))}");
            writer.WriteLine($"  current validation: {(result.CurrentValidation is null ? "none" :
                $"0x{result.CurrentValidation.Value:X} complete={result.CurrentValidation.Complete} " +
                    string.Join(",", result.CurrentValidation.Evidence.Select(e => $"{e.Predicate}:{e.Result} ({e.Detail})")))}");
            writer.WriteLine(result.Discovery.Scan is { } scan
                ? $"  scan: {scan.BytesScanned} bytes; {scan.RawMatchCount} raw matches; regions={string.Join(", ", scan.Regions)}"
                : "  scan: not run");
            foreach (CandidateEliminationStage stage in result.Discovery.EliminationStages)
                writer.WriteLine($"  {stage.Stage}: {stage.InputCount} in, {stage.SurvivingCount} survive, " +
                    $"{stage.RejectedCount} rejected ({string.Join(", ", stage.RejectionReasons.SelectMany(r =>
                        r.Value.Select(reason => $"{r.Key}:{reason.Predicate}:{reason.Reason}")))})");
            foreach (RecoveryCandidate candidate in result.Discovery.CandidateLedger)
                writer.WriteLine($"  {candidate.Id}: 0x{candidate.Value:X} {candidate.Disposition}; " +
                    $"origin={candidate.DiscoveryOrigin}; discovery={string.Join(",", candidate.Evidence.Select(e => $"{e.Predicate}:{e.Result}"))}; " +
                    $"validation={string.Join(",", candidate.ValidationEvidence.Select(e => $"{e.Predicate}:{e.Result} ({e.Detail})"))}; " +
                    $"rejections={string.Join(",", candidate.RejectionReasons.Select(r => $"{r.Predicate}:{r.Reason}"))}");
            writer.WriteLine($"  survivors: {string.Join(", ", result.Decision.SurvivorIds)}");
            writer.WriteLine($"  proposal: {(result.Decision.Proposal is long proposal ? $"0x{proposal:X}" : "none")}");
            if (result.Target.Id == Od144RuneshapePanelRecovery.TargetId)
            {
                if (result.CurrentValidation is { } currentPathValidation)
                    writer.WriteLine($"  configured child path: [{string.Join(",", currentPathValidation.ChildPath)}]");
                writer.WriteLine($"  derived child path: {(result.Decision.ChildPath.IsEmpty ? "none" : $"[{string.Join(",", result.Decision.ChildPath)}]")}");
                writer.WriteLine($"  path proposal: {(result.Decision.ProposedChildPath.IsEmpty ? "none" : $"[{string.Join(",", result.Decision.ProposedChildPath)}]")}");
                foreach (var candidate in result.Discovery.CandidateLedger.Where(c => c.Disposition == CandidateDisposition.Survivor))
                    writer.WriteLine($"  semantic survivor {candidate.Id}: [{string.Join(",", candidate.ChildPath)}]");
                if (result.PathComparison is { } pathComparison)
                    writer.WriteLine($"  post-result path comparison: current=[{string.Join(",", pathComparison.CurrentPath)}], " +
                        $"history={string.Join(";", pathComparison.HistoricalPaths.Select(p => $"[{string.Join(",", p)}]"))}");
            }
            if (result.Target.Id == Od145AtlasLayoutRecovery.TargetId)
            {
                if (result.CurrentValidation?.FieldPair is { } currentFieldPair)
                    writer.WriteLine($"  configured field pair: {currentFieldPair}");
                writer.WriteLine($"  proposed field pair: {(result.Decision.ProposedFieldPair is { } proposedPair ? proposedPair.ToString() : "none")}");
                foreach (var pair in result.Decision.SurvivingFieldPairs)
                    writer.WriteLine($"  surviving field pair: {pair}");
                if (result.FieldPairComparison is { } pairComparison)
                    writer.WriteLine($"  post-result field pair comparison: current={pairComparison.CurrentPair?.ToString() ?? "none"}, " +
                        $"history={string.Join(";", pairComparison.HistoricalPairs)}");
            }
            writer.WriteLine($"  post-result comparison: {(result.PostResultComparison is null ? "none" : $"current={result.PostResultComparison.CurrentValue}, history={string.Join(",", result.PostResultComparison.HistoricalValues)}")}");
            writer.WriteLine($"  digest: {result.Decision.EvidenceDigest}");
            if (result.Detail is not null) writer.WriteLine($"  detail: {result.Detail}");
        }
    }
}
