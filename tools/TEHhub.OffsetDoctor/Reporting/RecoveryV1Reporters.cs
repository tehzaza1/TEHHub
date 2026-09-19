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

    public static string SerializeAggregate(RecoveryAggregateReport report) =>
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

    public static void WriteAggregate(TextWriter writer, RecoveryAggregateReport report)
    {
        writer.WriteLine("================================================================================");
        writer.WriteLine($"Recovery V1 Aggregate Report | Status: {report.AggregateStatus} | Applied={report.Applied}");
        writer.WriteLine($"Process: {report.Identity.ProcessName} (PID {report.Identity.ProcessId}) | FileVersion: {report.Identity.FileVersion}");
        writer.WriteLine($"Module: 0x{report.Identity.ModuleBase:X}+0x{report.Identity.ModuleSize:X} | Time: {report.TimestampUtc:u} | Elapsed: {report.TotalElapsedMilliseconds}ms");
        writer.WriteLine("--------------------------------------------------------------------------------");
        writer.WriteLine($"Requested targets: {string.Join(", ", report.RequestedTargets)}");
        if (!report.AutoAddedRunnableDependencies.IsEmpty)
        {
            writer.WriteLine($"Auto-added runnable prerequisites: {string.Join(", ", report.AutoAddedRunnableDependencies)}");
        }
        writer.WriteLine($"Execution order: {string.Join(" -> ", report.ExecutionOrder)}");
        writer.WriteLine("--------------------------------------------------------------------------------");
        writer.WriteLine("Target Execution Summary:");
        foreach (var metric in report.TargetMetrics)
        {
            var res = report.Results.FirstOrDefault(r => r.Target.Id == metric.TargetId);
            var termResult = res?.Decision.TerminalResult.ToString() ?? "NOT_RUN";
            var readsText = metric.ReadCount.HasValue ? $"{metric.ReadCount} reads" : "reads N/A";
            var bytesText = metric.BytesScanned.HasValue ? $"{metric.BytesScanned} bytes" : "bytes N/A";
            var candidatesText = metric.CandidateCount.HasValue ? $"{metric.CandidateCount} candidates" : "candidates N/A";
            writer.WriteLine($"  [{metric.TargetId,-6}] {termResult,-18} | {metric.ElapsedMilliseconds,4}ms | {readsText}, {bytesText}, {candidatesText} | Applied=false");
        }
        writer.WriteLine("--------------------------------------------------------------------------------");
        if (!report.Proposals.IsEmpty)
        {
            writer.WriteLine("Proposals (UNAPPLIED - Review Required):");
            foreach (var (targetId, proposal) in report.Proposals)
            {
                writer.WriteLine($"  * {targetId}: {proposal} (Applied=false)");
            }
            writer.WriteLine("--------------------------------------------------------------------------------");
        }
        if (!report.AmbiguousTargets.IsEmpty)
        {
            writer.WriteLine("Ambiguous Targets (No Preferred Candidate):");
            foreach (var targetId in report.AmbiguousTargets)
            {
                var res = report.Results.FirstOrDefault(r => r.Target.Id == targetId);
                var survivors = res != null ? string.Join(", ", res.Decision.SurvivorIds.Select(s => $"0x{s:X}")) : "none";
                writer.WriteLine($"  * {targetId}: Surviving candidates: [{survivors}]");
            }
            writer.WriteLine("--------------------------------------------------------------------------------");
        }
        if (!report.BlockedDependencyTargets.IsEmpty)
        {
            writer.WriteLine("Blocked Dependency Targets:");
            foreach (var targetId in report.BlockedDependencyTargets)
            {
                var res = report.Results.FirstOrDefault(r => r.Target.Id == targetId);
                writer.WriteLine($"  * {targetId}: {res?.Detail ?? "Missing or failed dependency"}");
            }
            writer.WriteLine("--------------------------------------------------------------------------------");
        }
        if (!report.BlockedContextTargets.IsEmpty)
        {
            writer.WriteLine("Blocked Context Targets (Zone/UI Not Present):");
            foreach (var targetId in report.BlockedContextTargets)
            {
                var res = report.Results.FirstOrDefault(r => r.Target.Id == targetId);
                writer.WriteLine($"  * {targetId}: {res?.Detail ?? "Required game context not present"}");
            }
            writer.WriteLine("--------------------------------------------------------------------------------");
        }
        if (!report.ErrorTargets.IsEmpty || !report.Errors.IsEmpty)
        {
            writer.WriteLine("Errors:");
            foreach (var err in report.Errors)
            {
                writer.WriteLine($"  ! {err}");
            }
            writer.WriteLine("--------------------------------------------------------------------------------");
        }
        writer.WriteLine("Per-Target Details:");
        foreach (var result in report.Results)
        {
            writer.WriteLine($"--- {result.Target.Id} [{result.Target.Scope.Name}/{result.Target.Scope.InstanceId}] : {result.Decision.TerminalResult} ---");
            writer.WriteLine($"  Strategy: {result.Target.StrategyId} | Applied=false");
            if (result.CurrentValidation is { } cv)
                writer.WriteLine($"  Current Validation: Complete={cv.Complete}, 0x{cv.Value:X}");
            if (result.Discovery.Scan is { } scan)
                writer.WriteLine($"  Scan: {scan.BytesScanned} bytes, {scan.RawMatchCount} raw matches");
            if (result.Decision.Proposal is long prop)
                writer.WriteLine($"  Proposal: 0x{prop:X}");
            if (result.Decision.ProposedChildPath.Length > 0)
                writer.WriteLine($"  Proposed Child Path: [{string.Join(",", result.Decision.ProposedChildPath)}]");
            if (result.Decision.ProposedFieldPair is { } pfp)
                writer.WriteLine($"  Proposed Field Pair: {pfp}");
            writer.WriteLine($"  Digest: {result.Decision.EvidenceDigest}");
            if (result.Detail is not null)
                writer.WriteLine($"  Detail: {result.Detail}");
        }
        writer.WriteLine("--------------------------------------------------------------------------------");
        writer.WriteLine($"Total Elapsed: {report.TotalElapsedMilliseconds}ms | Total Scanned: {(report.TotalBytesScanned.HasValue ? $"{report.TotalBytesScanned} bytes" : "N/A")}");
        writer.WriteLine("================================================================================");
    }
}
