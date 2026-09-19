namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;
using TEHhub.OffsetDoctor.Process;

public interface IRecoveryReadOnlyMemory
{
    bool TryRead<T>(long address, out T value) where T : unmanaged;
    bool TryReadBytes(long address, Span<byte> destination);
    bool IsValidAddress(long address);
}

internal sealed class RecoveryMemoryView(IProcessMemoryReader source) : IRecoveryReadOnlyMemory
{
    public bool TryRead<T>(long address, out T value) where T : unmanaged =>
        source.TryRead(new IntPtr(address), out value);
    public bool TryReadBytes(long address, Span<byte> destination) =>
        source.TryReadBytes(new IntPtr(address), destination);
    public bool IsValidAddress(long address) => source.IsValidAddress(new IntPtr(address));
}

public sealed class RecoverySession
{
    private readonly IProcessMemoryReader _reader;
    private readonly Dictionary<string, RecoveryResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProvisionalValue> _provisional = new(StringComparer.Ordinal);
    private RecoveryContextSnapshot _context;

    public RecoverySession(IProcessMemoryReader reader, RecoveryContextSnapshot context)
    {
        _reader = reader;
        Memory = new RecoveryMemoryView(reader);
        Identity = CaptureIdentity(reader);
        _context = context;
    }

    public RecoveryIdentity Identity { get; }
    public IRecoveryReadOnlyMemory Memory { get; }
    public RecoveryContextSnapshot Context => _context;
    public IReadOnlyCollection<RecoveryResult> Results => _results.Values;
    public IReadOnlyCollection<RecoveryDependencyState> DependencyStates => _results.Values
        .Select(r => new RecoveryDependencyState(r.Target.Id, r.Decision.TerminalResult,
            _provisional.ContainsKey(r.Target.Id), r.Decision.EvidenceDigest))
        .ToArray();
    public IReadOnlyDictionary<string, ProvisionalValue> ProvisionalValues => _provisional;
    public bool IdentityIsCurrent => Identity == CaptureIdentity(_reader);

    public void ReplaceContext(RecoveryContextSnapshot context) => _context = context;

    public bool TryGetResult(string targetId, out RecoveryResult? result) =>
        _results.TryGetValue(targetId, out result);

    public bool TryGetProvisional(string targetId, out ProvisionalValue? value) =>
        _provisional.TryGetValue(targetId, out value);

    internal void Record(RecoveryResult result)
    {
        if (!_results.TryAdd(result.Target.Id, result))
            throw new InvalidOperationException($"Target {result.Target.Id} already has a result in this session.");

        if (result.Decision.TerminalResult != RecoveryTerminalResult.PROPOSED ||
            result.Decision.Proposal is not long proposal ||
            result.Decision.SurvivorIds.Length != 1)
            return;

        string id = result.Decision.SurvivorIds[0];
        RecoveryCandidate candidate = result.Discovery.CandidateLedger.Single(c => c.Id == id);
        if (candidate.Disposition != CandidateDisposition.Survivor ||
            candidate.ValidationEvidence.All(e => !e.Independent || e.Result != RecoveryEvidenceResult.PASS) ||
            candidate.ValidationEvidence.Any(e => e.Required && e.Result != RecoveryEvidenceResult.PASS))
            throw new InvalidOperationException("A provisional value requires a unique independently validated candidate.");

        _provisional.Add(result.Target.Id,
            new ProvisionalValue(proposal, result.Target.Id, id, result.Decision.EvidenceDigest, Identity));
    }

    private static RecoveryIdentity CaptureIdentity(IProcessMemoryReader reader)
    {
        ProcessMetadata metadata = reader.Metadata;
        return new RecoveryIdentity(metadata.ProcessId, metadata.ProcessName, metadata.ProcessPath, metadata.FileVersion,
            reader.MainModuleBase.ToInt64(), reader.MainModuleSize, metadata.AttachedTimeUtc);
    }
}

public static class ContextRequirementEvaluator
{
    public static string? CheckRequired(RecoveryTargetSpec target, RecoveryContextSnapshot initial,
        RecoveryContextSnapshot current)
    {
        foreach (string key in target.RequiredContextKeys)
        {
            if (!initial.Facts.TryGetValue(key, out string? initialValue) ||
                string.IsNullOrWhiteSpace(initialValue))
                return $"Required context '{key}' is missing.";
            if (!current.Facts.TryGetValue(key, out string? currentValue) ||
                !StringComparer.Ordinal.Equals(initialValue, currentValue))
                return $"Required context '{key}' changed during recovery.";
        }
        return null;
    }
}

public static class DependencyEvaluator
{
    public static bool TryResolve(RecoverySession session, RecoveryTargetSpec target,
        out long anchor, out ImmutableArray<RecoveryDependencyState> dependencies, out string? error)
    {
        if (target.ParentTargetId is null)
        {
            dependencies = [];
            anchor = target.RootAddress ?? 0;
            error = target.RootAddress is null ? "Root address is missing." : null;
            return error is null;
        }

        if (!session.TryGetResult(target.ParentTargetId, out RecoveryResult? parent) || parent is null)
        {
            dependencies = [new RecoveryDependencyState(target.ParentTargetId,
                RecoveryTerminalResult.BLOCKED_DEPENDENCY, false, string.Empty)];
            anchor = 0;
            error = $"Parent '{target.ParentTargetId}' has no result.";
            return false;
        }

        bool valid = session.TryGetProvisional(target.ParentTargetId, out ProvisionalValue? provisional) &&
            provisional is not null && provisional.Identity == session.Identity &&
            parent.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED;
        dependencies = [new RecoveryDependencyState(target.ParentTargetId,
            parent.Decision.TerminalResult, valid, parent.Decision.EvidenceDigest)];
        anchor = valid ? provisional!.Value : 0;
        error = valid ? null : $"Parent '{target.ParentTargetId}' lacks a unique validated session value.";
        return valid;
    }
}
