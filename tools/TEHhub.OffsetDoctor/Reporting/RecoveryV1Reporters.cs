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
            $"PID {report.Identity.ProcessId} | build {report.Identity.FileVersion}");
        foreach (RecoveryResult result in report.Results)
        {
            writer.WriteLine($"{result.Target.Id} [{result.Target.Scope.Name}/{result.Target.Scope.InstanceId}] " +
                $"{result.Decision.TerminalResult} | strategy {result.Target.StrategyId} | Applied=false");
            writer.WriteLine($"  dependencies: {string.Join(", ", result.Dependencies.Select(d => $"{d.TargetId}:{d.Result}"))}");
            writer.WriteLine($"  context: {string.Join(", ", result.Context.Select(c => $"{c.Key}={c.Value}"))}");
            writer.WriteLine($"  current validation: {(result.CurrentValidation is null ? "none" :
                $"0x{result.CurrentValidation.Value:X} complete={result.CurrentValidation.Complete} " +
                string.Join(",", result.CurrentValidation.Evidence.Select(e => $"{e.Predicate}:{e.Result}")))}");
            foreach (CandidateEliminationStage stage in result.Discovery.EliminationStages)
                writer.WriteLine($"  {stage.Stage}: {stage.InputCount} in, {stage.SurvivingCount} survive, " +
                    $"{stage.RejectedCount} rejected ({string.Join(", ", stage.RejectionReasons.SelectMany(r =>
                        r.Value.Select(reason => $"{r.Key}:{reason.Predicate}:{reason.Reason}")))})");
            foreach (RecoveryCandidate candidate in result.Discovery.CandidateLedger)
                writer.WriteLine($"  {candidate.Id}: 0x{candidate.Value:X} {candidate.Disposition}; " +
                    $"origin={candidate.DiscoveryOrigin}; discovery={string.Join(",", candidate.Evidence.Select(e => $"{e.Predicate}:{e.Result}"))}; " +
                    $"validation={string.Join(",", candidate.ValidationEvidence.Select(e => $"{e.Predicate}:{e.Result}"))}; " +
                    $"rejections={string.Join(",", candidate.RejectionReasons.Select(r => $"{r.Predicate}:{r.Reason}"))}");
            writer.WriteLine($"  survivors: {string.Join(", ", result.Decision.SurvivorIds)}");
            writer.WriteLine($"  proposal: {(result.Decision.Proposal is long proposal ? $"0x{proposal:X}" : "none")}");
            writer.WriteLine($"  post-result comparison: {(result.PostResultComparison is null ? "none" : $"current={result.PostResultComparison.CurrentValue}, history={string.Join(",", result.PostResultComparison.HistoricalValues)}")}");
            writer.WriteLine($"  digest: {result.Decision.EvidenceDigest}");
        }
    }
}
