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

    public bool TryGetResult(string targetId, out RecoveryResult? result)
    {
        if (_results.TryGetValue(targetId, out result)) return true;
        string norm = RecoveryV1Registry.NormalizeId(targetId);
        foreach (var (k, v) in _results)
        {
            if (RecoveryV1Registry.NormalizeId(k) == norm)
            {
                result = v;
                return true;
            }
        }
        result = null;
        return false;
    }

    public bool TryGetProvisional(string targetId, out ProvisionalValue? value)
    {
        if (_provisional.TryGetValue(targetId, out value)) return true;
        string norm = RecoveryV1Registry.NormalizeId(targetId);
        foreach (var (k, v) in _provisional)
        {
            if (RecoveryV1Registry.NormalizeId(k) == norm)
            {
                value = v;
                return true;
            }
        }
        value = null;
        return false;
    }

    public void TrustAnchor(string id, string name, long address, string provenance = "trusted-anchor")
    {
        if (TryGetResult(id, out _)) return;
        var target = RecoveryTargetSpec.Create(id, new RecoveryTargetScope("trusted-anchor", name),
            "trusted-anchor-registration", rootAddress: address);
        var evidence = ImmutableArray.Create(new RecoveryEvidenceRecord(
            "anchor-registration", RecoveryEvidenceResult.PASS,
            $"Anchor registered with provenance '{provenance}'.", true, true));
        var candidate = new DiscoveredCandidate($"anchor-{id}", address, provenance, evidence);
        var decision = new FrozenDecision(RecoveryTerminalResult.PASS_CURRENT, [candidate.Id], address,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{id}:{address}:{provenance}"))));
        var discovery = new FrozenDiscoveryResult([new RecoveryCandidate(candidate.Id, address, provenance,
            evidence, evidence, [], CandidateDisposition.Survivor)], [], [candidate.Id]);
        var result = new RecoveryResult(target, decision, discovery, null, [],
            ImmutableDictionary<string, string>.Empty.SetItem("provenance", provenance), null, null);
        Record(result);
    }

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
                (string.IsNullOrWhiteSpace(initialValue) ||
                 initialValue.Equals("false", StringComparison.OrdinalIgnoreCase)))
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
        if (target.ParentTargetId is null && target.AdditionalDependencyIds.IsEmpty)
        {
            dependencies = [];
            anchor = target.RootAddress ?? 0;
            error = target.RootAddress is null ? "Root address is missing." : null;
            return error is null;
        }

        var ids = (target.ParentTargetId is null ? Enumerable.Empty<string>() : [target.ParentTargetId])
            .Concat(target.AdditionalDependencyIds).Distinct(StringComparer.Ordinal).ToArray();
        var states = ImmutableArray.CreateBuilder<RecoveryDependencyState>();
        anchor = target.RootAddress ?? 0;
        error = target.ParentTargetId is null && target.RootAddress is null
            ? "Root address is missing." : null;
        foreach (string id in ids)
        {
            if (!session.TryGetResult(id, out RecoveryResult? parent) || parent is null)
            {
                states.Add(new RecoveryDependencyState(id,
                    RecoveryTerminalResult.BLOCKED_DEPENDENCY, false, string.Empty));
                error ??= $"Dependency '{id}' has no result.";
                continue;
            }

            bool isProvisional = session.TryGetProvisional(id, out ProvisionalValue? provisional) &&
                provisional is not null && provisional.Identity == session.Identity &&
                parent.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED;

            bool isPassCurrent = parent.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT;

            long? anchorValue = isProvisional ? provisional!.Value :
                (parent.Decision.Proposal ?? parent.CurrentValidation?.Value ?? parent.Target.RootAddress);

            bool valid = (isProvisional || isPassCurrent) && anchorValue.HasValue && anchorValue.Value > 0;

            states.Add(new RecoveryDependencyState(id, parent.Decision.TerminalResult, valid,
                parent.Decision.EvidenceDigest, valid ? anchorValue : null,
                isProvisional ? provisional!.CandidateId : null));

            if (id == target.ParentTargetId && valid) anchor = anchorValue!.Value;
            if (!valid) error ??= $"Dependency '{id}' lacks a unique validated session value.";
        }
        dependencies = states.ToImmutable();
        if (error is not null) anchor = 0;
        return error is null;
    }
}
