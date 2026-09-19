namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TEHhub.Offsets.Natives;

/// <summary>Read-only bounded owner discovery. The device back-pointer is a
/// structural invariant, never a listener-owner displacement hint.</summary>
public static class Od114RuneStationOwnerRecovery
{
    public const string TargetId = "comp_statemachine_rune_station_owner";
    public const string StrategyId = "od-114-bounded-owner-discovery";
    public const string StateMachineId = "comp_statemachine";
    public const string RuneStationEntityId = "expedition_rune_station_entity";
    public const int Radius = 0x200;
    public const int Alignment = 8;
    public const int CandidatesPerListener = Radius * 2 / Alignment + 1;
    private const int MaxListeners = 24;

    public static RecoveryResult Run(RecoverySession session, long? currentConfiguredDelta = null,
        IEnumerable<long>? historicalDeltas = null)
    {
        var target = RecoveryTargetSpec.Create(TargetId,
            new RecoveryTargetScope("expedition-rune-station-state-machine", session.Identity.ProcessName),
            StrategyId, ["expedition-rune-station-present", "state-machine-present"],
            parentTargetId: StateMachineId, approvedHypotheses: [StructuralHypothesis.PointerField],
            additionalDependencyIds: [RuneStationEntityId]);
        var memory = session.Memory;
        var context = session.Context;
        var ledger = new List<OwnerObservation>();
        var stages = new List<CandidateEliminationStage>();
        var regions = ImmutableArray.CreateBuilder<string>();
        ImmutableArray<RecoveryDependencyState> dependencies = [];
        RecoveryCurrentValidation? currentValidation = null;
        RecoveryTerminalResult? forced = null;
        string? detail = null;
        long sm = 0, entity = 0;
        ImmutableArray<long> listeners = [];
        bool scanned = false;
        string? Guard() => !session.IdentityIsCurrent ? "Process/build identity became stale." :
            ContextRequirementEvaluator.CheckRequired(target, context, session.Context);
        try
        {
            detail = Guard();
            if (detail is not null) forced = detail.StartsWith("Required context", StringComparison.Ordinal)
                ? RecoveryTerminalResult.BLOCKED_CONTEXT : RecoveryTerminalResult.ERROR;
            if (forced is null && !DependencyEvaluator.TryResolve(session, target,
                    out sm, out dependencies, out detail))
                forced = RecoveryTerminalResult.BLOCKED_DEPENDENCY;
            if (forced is null)
            {
                entity = dependencies.Single(d => d.TargetId == RuneStationEntityId).Anchor!.Value;
                if (sm < 0x10000 || entity < 0x10000 ||
                    !memory.TryRead(sm + 8, out long owner) || owner != entity)
                {
                    forced = RecoveryTerminalResult.BLOCKED_DEPENDENCY;
                    detail = "StateMachine ownership does not match the trusted Rune Station entity.";
                }
            }
            if (forced is null && !TryListeners(memory, sm, out listeners, out detail))
                forced = RecoveryTerminalResult.BLOCKED_CONTEXT;
            if (forced is null && currentConfiguredDelta is long current)
            {
                var evidence = ImmutableArray.CreateBuilder<RecoveryEvidenceRecord>();
                bool valid = false;
                foreach (long listener in listeners)
                {
                    if (current < -Radius || current > Radius || current % Alignment != 0) break;
                    var check = Inspect(memory, listener - current, entity, sm, true);
                    evidence.AddRange(check.Evidence);
                    if (check.Incomplete)
                    {
                        forced = RecoveryTerminalResult.ERROR;
                        detail = check.Error;
                        break;
                    }
                    valid |= check.Valid;
                }
                currentValidation = new(current, evidence.ToImmutable(), forced is null);
                if (forced is null && valid) forced = RecoveryTerminalResult.PASS_CURRENT;
            }
            if (forced is null)
            {
                scanned = true;
                for (int i = 0; i < listeners.Length; i++)
                {
                    long listener = listeners[i];
                    regions.Add($"listener[{i}]=0x{listener:X}; owner bases 0x{listener - Radius:X}..0x{listener + Radius:X}; alignment={Alignment}");
                    for (int offset = -Radius; offset <= Radius; offset += Alignment)
                        ledger.Add(new OwnerObservation($"listener-{i:D2}-base-{offset + Radius:D3}",
                            listener, listener + offset));
                }
                Filter(stages, EliminationStage.Od114AlignedBases, ledger, _ => (true, "aligned-base"),
                    "Every aligned base in the declared listener radius.");
                Filter(stages, EliminationStage.Od114ReadableShape, ledger, c =>
                {
                    c.Shape = Inspect(memory, c.Owner, entity, sm, false);
                    return (c.Shape.Readable, "required-memory-shape");
                });
                if (ledger.Any(c => c.Shape?.Incomplete == true))
                {
                    forced = RecoveryTerminalResult.ERROR;
                    detail = ledger.First(c => c.Shape?.Incomplete == true).Shape!.Error;
                }
                if (forced is null) Filter(stages, EliminationStage.Od114OwnerBackPointer, ledger,
                    c => (c.Shape!.BackPointer, "trusted-owner-back-pointer"));
                if (forced is null) Filter(stages, EliminationStage.Od114AnchorStructure, ledger,
                    c => (c.Shape!.Anchor, "anchor-holder-device-structure"));
                if (forced is null) Filter(stages, EliminationStage.Od114SocketCount, ledger,
                    c => (c.Shape!.Sockets, "socket-count-range"));
                if (forced is null) Filter(stages, EliminationStage.Od114GoldenSlots, ledger,
                    c => (c.Shape!.Golden, "golden-slots-vector"));
                if (forced is null) Filter(stages, EliminationStage.Od114AnchorPosition, ledger,
                    c => (c.Shape!.Position, "anchor-position-relationship"));
                if (forced is null)
                {
                    var input = ledger.Where(c => !c.Rejected).ToArray();
                    foreach (var c in input)
                    {
                        var repeat = Inspect(memory, c.Owner, entity, sm, true);
                        c.Validation = repeat.Evidence;
                        if (repeat.Incomplete) { forced = RecoveryTerminalResult.ERROR; detail = repeat.Error; break; }
                        if (!repeat.Valid) c.Reject(EliminationStage.IndependentValidation,
                            "independent-repeat", "Rune Station failed repeated validation.");
                    }
                    if (forced is null) RecordStage(stages, EliminationStage.IndependentValidation, input);
                }
                if (forced is null)
                {
                    var input = ledger.Where(c => !c.Rejected).ToArray();
                    var seen = new Dictionary<(long Owner, long Delta), string>();
                    foreach (var c in input)
                    {
                        var key = (c.Owner, c.Listener - c.Owner);
                        if (seen.TryGetValue(key, out string? first)) c.EquivalentTo = first;
                        else seen.Add(key, c.Id);
                    }
                    RecordStage(stages, EliminationStage.Equivalence, input,
                        "Same validated owner address and listener-minus-owner delta.");
                }
            }
            if (forced is null && Guard() is string guard)
            {
                detail = guard;
                forced = guard.StartsWith("Required context", StringComparison.Ordinal)
                    ? RecoveryTerminalResult.BLOCKED_CONTEXT : RecoveryTerminalResult.ERROR;
            }
        }
        catch (Exception ex) { forced = RecoveryTerminalResult.ERROR; detail = ex.Message; }

        var survivors = forced is null ? ledger.Where(c => !c.Rejected && c.EquivalentTo is null).ToArray() : [];
        var terminal = forced ?? (survivors.Length switch
        {
            0 => RecoveryTerminalResult.NOT_FOUND,
            1 => RecoveryTerminalResult.PROPOSED,
            _ => RecoveryTerminalResult.AMBIGUOUS
        });
        // The relationship is derived only after a unique owner passed independent validation.
        long? proposal = terminal == RecoveryTerminalResult.PROPOSED
            ? survivors[0].Listener - survivors[0].Owner : null;
        var frozenLedger = ledger.Select(c => c.Freeze(survivors.Contains(c))).ToImmutableArray();
        var ids = survivors.Select(c => c.Id).ToImmutableArray();
        var discovery = new FrozenDiscoveryResult(frozenLedger, stages.ToImmutableArray(), ids,
            scanned ? new DiscoveryScanEvidence(ledger.Count * Alignment, ledger.Count, regions.ToImmutable()) : null);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { target.Id, terminal, proposal, ids, frozenLedger, stages }))));
        var decision = new FrozenDecision(terminal, ids, proposal, digest);
        // Historical values are materialized only after the decision and digest are frozen.
        var history = new long[] { -0x98, -0xA0 }
            .Concat(historicalDeltas ?? []).Distinct().ToImmutableArray();
        var comparison = new HistoricalComparison(currentConfiguredDelta, history,
                proposal is null || currentConfiguredDelta is null ? null : proposal == currentConfiguredDelta,
                proposal is null || history.IsEmpty ? null : history.Contains(proposal.Value));
        var reportFacts = context.Facts
            .SetItem("state-machine-address", sm == 0 ? "unavailable" : $"0x{sm:X}")
            .SetItem("owner-entity-address", entity == 0 ? "unavailable" : $"0x{entity:X}")
            .SetItem("listener-count", listeners.Length.ToString())
            .SetItem("listener-addresses", string.Join(",", listeners.Select(a => $"0x{a:X}")))
            .SetItem("owner-search-bounds", $"listener ± 0x{Radius:X}")
            .SetItem("owner-search-alignment", Alignment.ToString())
            .SetItem("owner-candidate-count", ledger.Count.ToString());
        var result = new RecoveryResult(target, decision, discovery, currentValidation,
            dependencies, reportFacts, comparison, detail);
        session.Record(result);
        return result;
    }

    private static bool TryListeners(IRecoveryReadOnlyMemory memory, long sm,
        out ImmutableArray<long> listeners, out string detail)
    {
        listeners = [];
        if (sm < 0x10000 || sm > long.MaxValue - 0x38 ||
            !memory.TryRead(sm + 0x20, out StdVector vector))
        { detail = "StateMachine listener vector is unreadable."; return false; }
        long first = vector.First.ToInt64(), last = vector.Last.ToInt64(), end = vector.End.ToInt64();
        if (first < 0x10000 || first % 8 != 0 || last < first || end < last ||
            (last - first) % 8 != 0 || (last - first) / 8 is < 1 or > MaxListeners)
        { detail = "StateMachine listener vector has no bounded usable entries."; return false; }
        var found = ImmutableArray.CreateBuilder<long>();
        for (long p = first; p < last; p += 8)
        {
            if (!memory.TryRead(p, out long node) || node < 0x10000 ||
                !memory.TryRead(node, out long listener) || listener < 0x10200 ||
                listener > long.MaxValue - Radius - 0x58 || listener % Alignment != 0)
                continue;
            found.Add(listener);
        }
        listeners = found.ToImmutable();
        detail = listeners.IsEmpty ? "StateMachine listener vector contains no usable observations." : "";
        return !listeners.IsEmpty;
    }

    private static OwnerShape Inspect(IRecoveryReadOnlyMemory memory, long station,
        long entity, long sm, bool independent)
    {
        var evidence = ImmutableArray.CreateBuilder<RecoveryEvidenceRecord>();
        void Add(string name, bool pass, string message) => evidence.Add(new(name,
            pass ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL, message, true, independent));
        OwnerShape Done(bool readable, bool back, bool anchor, bool sockets, bool golden,
            bool position, bool incomplete = false, string? error = null) =>
            new(readable, back, anchor, sockets, golden, position, incomplete, error, evidence.ToImmutable());
        if (station < 0x10000 || station > long.MaxValue - 0x58 || !memory.IsValidAddress(station))
        {
            Add("required-memory-shape", false, $"Owner base 0x{station:X} is not addressable.");
            return Done(false, false, false, false, false, false);
        }
        if (!memory.TryRead(station + 0x10, out long backPtr) ||
            !memory.TryRead(station + 0x28, out long row) ||
            !memory.TryRead(station + 0x30, out long holder) ||
            !memory.TryRead(station + 0x38, out int count) ||
            !memory.TryRead(station + 0x3C, out int anchorPos) ||
            !memory.TryRead(station + 0x40, out StdVector vector))
        {
            Add("required-memory-shape", false, $"Partial owner read at 0x{station:X}.");
            return Done(false, false, false, false, false, false, true,
                $"Incomplete bounded Rune Station read at 0x{station:X}.");
        }
        Add("required-memory-shape", true, $"Owner base 0x{station:X} has readable fields.");
        bool back = backPtr == entity;
        Add("trusted-owner-back-pointer", back,
            $"StationDeviceBackPtr=0x{backPtr:X}; trusted OwnerEntity=0x{entity:X}.");
        bool anchor = row == 0 ? holder == 0 : row >= 0x10000 &&
            memory.IsValidAddress(row) && holder >= 0x10000 &&
            memory.IsValidAddress(holder) && memory.TryRead(holder + 0x28, out long p1) &&
            p1 >= 0x10000 && memory.IsValidAddress(p1);
        Add("anchor-holder-device-structure", anchor,
            $"AnchorRef=0x{row:X}; holder=0x{holder:X}; StateMachine=0x{sm:X}.");
        bool sockets = count is > 0 and <= 16;
        Add("socket-count-range", sockets, $"Socket count={count}; required 1..16.");
        long first = vector.First.ToInt64(), last = vector.Last.ToInt64(), end = vector.End.ToInt64();
        bool golden = first == 0 && last == 0 && end == 0 ||
            first >= 0x10000 && first % 4 == 0 && last >= first && end >= last &&
            last - first is >= 0 and <= 64 && (last - first) % 4 == 0 &&
            end - first <= 64 && (end - first) % 4 == 0;
        var slots = new HashSet<int>();
        if (golden)
            for (long p = first; p < last; p += 4)
                if (!memory.TryRead(p, out int slot) || slot < 0 || slot >= count || !slots.Add(slot))
                { golden = false; break; }
        Add("golden-slots-vector", golden,
            $"GoldenSlots=[0x{first:X},0x{last:X},0x{end:X}], entries={slots.Count}.");
        bool position = sockets && anchorPos >= 0 && anchorPos < count;
        if (row != 0 && anchor)
        {
            memory.TryRead(holder + 0x28, out long tableHolder);
            position &= memory.TryRead(tableHolder, out long table) && table >= 0x10000 &&
                row >= table && ((row - table) % 0x68 == 0 && (row - table) / 0x68 < 33 ||
                                 (row - table) % 0x6C == 0 && (row - table) / 0x6C < 33);
        }
        Add("anchor-position-relationship", position,
            $"Anchor position={anchorPos}; sockets={count}; anchor ref=0x{row:X}.");
        if (independent)
        {
            bool stable = memory.TryRead(station + 0x10, out long repeatBack) &&
                memory.TryRead(station + 0x38, out int repeatCount) &&
                repeatBack == backPtr && repeatCount == count &&
                memory.TryRead(sm + 8, out long repeatOwner) && repeatOwner == entity;
            Add("stable-repeat-and-state-machine-owner", stable,
                $"Repeated station and StateMachine ownership at 0x{sm:X}.");
            position &= stable;
        }
        return Done(true, back, anchor, sockets, golden, position);
    }

    private static void Filter(List<CandidateEliminationStage> stages, EliminationStage stage,
        List<OwnerObservation> ledger, Func<OwnerObservation, (bool Pass, string Reason)> predicate,
        string? rule = null)
    {
        var input = ledger.Where(c => !c.Rejected).ToArray();
        foreach (var c in input)
        {
            var (pass, reason) = predicate(c);
            if (!pass) c.Reject(stage, reason, $"Owner base 0x{c.Owner:X} failed {reason}.");
        }
        RecordStage(stages, stage, input, rule);
    }

    private static void RecordStage(List<CandidateEliminationStage> stages, EliminationStage stage,
        OwnerObservation[] input, string? rule = null)
    {
        stages.Add(new CandidateEliminationStage(stage,
            input.Select(c => c.Id).ToImmutableArray(),
            input.Where(c => !c.Rejected && c.EquivalentTo is null).Select(c => c.Id).ToImmutableArray(),
            input.Where(c => c.Rejected || c.EquivalentTo is not null).Select(c => c.Id).ToImmutableArray(),
            input.Where(c => c.Rejected || c.EquivalentTo is not null).ToImmutableDictionary(c => c.Id,
                c => c.Rejections.Where(r => r.Stage == stage).ToImmutableArray(), StringComparer.Ordinal), rule));
    }

    private sealed class OwnerObservation(string id, long listener, long owner)
    {
        public string Id { get; } = id;
        public long Listener { get; } = listener;
        public long Owner { get; } = owner;
        public OwnerShape? Shape { get; set; }
        public ImmutableArray<RecoveryEvidenceRecord> Validation { get; set; } = [];
        public List<CandidateRejection> Rejections { get; } = [];
        public bool Rejected { get; private set; }
        private string? _equivalentTo;
        public string? EquivalentTo
        {
            get => _equivalentTo;
            set
            {
                _equivalentTo = value;
                if (value is not null) Rejections.Add(new(EliminationStage.Equivalence,
                    "same-owner-and-delta", $"Equivalent to {value}."));
            }
        }
        public void Reject(EliminationStage stage, string predicate, string reason)
        { Rejected = true; Rejections.Add(new(stage, predicate, reason)); }
        public RecoveryCandidate Freeze(bool survived) => new(Id, Owner,
            $"listener=0x{Listener:X}; owner=0x{Owner:X}; listener-minus-owner={Listener - Owner}",
            Shape?.Evidence ?? [], Validation, Rejections.ToImmutableArray(),
            Rejected ? CandidateDisposition.Rejected : EquivalentTo is not null ? CandidateDisposition.Equivalent :
            survived ? CandidateDisposition.Survivor : CandidateDisposition.Unresolved);
    }

    private sealed record OwnerShape(bool Readable, bool BackPointer, bool Anchor, bool Sockets,
        bool Golden, bool Position, bool Incomplete, string? Error,
        ImmutableArray<RecoveryEvidenceRecord> Evidence)
    {
        public bool Valid => Readable && BackPointer && Anchor && Sockets && Golden && Position &&
            !Incomplete && Evidence.All(e => !e.Required || e.Result == RecoveryEvidenceResult.PASS);
    }
}
