namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using TEHhub.Offsets.Objects.Components;

/// <summary>OD-063 uses only a session-validated Life parent. Offsets enter the coordinator's
/// current-value and post-decision paths, never the blind strategy.</summary>
public static class Od063LifeHealthRecovery
{
    public const string TargetId = "comp_life_health";
    public const string StrategyId = "od-063-runtime-semantic-field";
    public const string ParentId = "comp_life";
    public const int SearchBytes = 0x400;

    public static RecoveryResult Run(RecoverySession session, long? currentConfiguredOffset = null,
        IEnumerable<long>? historicalOffsets = null)
    {
        var target = RecoveryTargetSpec.Create(TargetId,
            new RecoveryTargetScope("local-player-life-component", session.Identity.ProcessName), StrategyId,
            ["local-player-present", "life-component-readable", "player-health-meaningful"],
            parentTargetId: ParentId, approvedHypotheses: [StructuralHypothesis.ScalarField]);
        long configured = currentConfiguredOffset ??
            Marshal.OffsetOf<LifeOffset>(nameof(LifeOffset.Health)).ToInt64();
        return new RecoveryCoordinator().Run(session, target,
            new Od063VitalDiscovery(), new Od063VitalValidator(), configured,
            historicalOffsets);
    }
}

internal sealed class Od063VitalDiscovery : IBlindDiscoveryStrategy
{
    public string StrategyId => Od063LifeHealthRecovery.StrategyId;

    public DiscoveryOutcome Discover(IRecoveryReadOnlyMemory memory, BlindDiscoveryRequest request)
    {
        var candidates = ImmutableArray.CreateBuilder<DiscoveredCandidate>();
        var regions = ImmutableArray.CreateBuilder<string>();
        long anchor = request.AnchorAddress;
        if (request.TargetId != Od063LifeHealthRecovery.TargetId ||
            !request.ApprovedHypotheses.Contains(StructuralHypothesis.ScalarField) ||
            anchor < 0x10000 || anchor > long.MaxValue - Od063LifeHealthRecovery.SearchBytes)
            return new([], false, "OD-063 Life anchor or structural hypothesis is invalid.");

        const int first = 0x10; // skip the ComponentHeader, not a historical Health location
        const int width = 0x38; // VitalStruct's last declared int ends at +0x34
        regions.Add($"Life 0x{anchor + first:X}-0x{anchor + Od063LifeHealthRecovery.SearchBytes:X}; stride=8; width=0x{width:X}");
        int examined = 0;
        for (int offset = first; offset + width <= Od063LifeHealthRecovery.SearchBytes; offset += 8)
        {
            examined++;
            long address = anchor + offset;
            if (!memory.TryRead(address, out VitalStruct vital))
            {
                candidates.Add(new DiscoveredCandidate($"life+{offset:X}", offset,
                    $"Life+0x{offset:X} at 0x{address:X}",
                    [new("vital-readable", RecoveryEvidenceResult.FAIL,
                        $"VitalStruct read failed at 0x{address:X}.", true, false)]));
                return new(candidates.ToImmutable(), false,
                    $"Incomplete bounded Life scan at +0x{offset:X}.",
                    new DiscoveryScanEvidence(examined * 8L, examined, regions.ToImmutable()));
            }

            bool owner = vital.PtrToLifeComponent.ToInt64() == anchor;
            bool shape = vital.VtablePtr.ToInt64() >= 0x10000 &&
                memory.IsValidAddress(vital.VtablePtr.ToInt64());
            candidates.Add(new DiscoveredCandidate($"life+{offset:X}", offset,
                $"Life+0x{offset:X} at 0x{address:X}; owner=0x{vital.PtrToLifeComponent.ToInt64():X}; " +
                $"total={vital.Total}, current={vital.Current}, reservedFlat={vital.ReservedFlat}, reservedPercent={vital.ReservedPercent}",
                [new("vital-readable", RecoveryEvidenceResult.PASS,
                    $"Read VitalStruct at 0x{address:X}.", true, false),
                 new("vital-vtable-shape", shape ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                    $"Vtable pointer 0x{vital.VtablePtr.ToInt64():X} is {(shape ? "addressable" : "invalid")}.", true, false),
                 new("life-back-pointer", owner ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                    $"Vital owner 0x{vital.PtrToLifeComponent.ToInt64():X}; trusted Life 0x{anchor:X}.", true, false)]));
        }
        return new(candidates.ToImmutable(), true, null,
            new DiscoveryScanEvidence(examined * 8L, examined, regions.ToImmutable()));
    }
}

internal sealed class Od063VitalValidator : IIndependentCandidateValidator
{
    public IndependentValidationOutcome Validate(IRecoveryReadOnlyMemory memory,
        IndependentValidationRequest request)
    {
        var evidence = ImmutableArray.CreateBuilder<RecoveryEvidenceRecord>();
        void Add(string name, bool pass, string detail, bool required = true) =>
            evidence.Add(new(name, pass ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                detail, required, true));
        IndependentValidationOutcome Done() => new(evidence.ToImmutable(), true);

        if (request.TargetId != Od063LifeHealthRecovery.TargetId ||
            request.CandidateValue < 0x10 || request.CandidateValue % 8 != 0 ||
            request.CandidateValue + 0x38 > Od063LifeHealthRecovery.SearchBytes ||
            request.AnchorAddress < 0x10000 ||
            request.AnchorAddress > long.MaxValue - Od063LifeHealthRecovery.SearchBytes)
        {
            Add("bounded-aligned-field", false, $"Field +0x{request.CandidateValue:X} is outside the bounded VitalStruct layout.");
            return Done();
        }
        long address = request.AnchorAddress + request.CandidateValue;
        if (!memory.TryRead(address, out VitalStruct vital))
            return new(evidence.ToImmutable(), false, $"VitalStruct read failed at 0x{address:X}.");
        Add("life-back-pointer", vital.PtrToLifeComponent.ToInt64() == request.AnchorAddress,
            $"Owner=0x{vital.PtrToLifeComponent.ToInt64():X}; Life=0x{request.AnchorAddress:X}.");
        Add("vital-vtable", vital.VtablePtr.ToInt64() >= 0x10000 &&
            memory.IsValidAddress(vital.VtablePtr.ToInt64()),
            $"Vtable=0x{vital.VtablePtr.ToInt64():X}.");
        long reserved = (long)Math.Ceiling(vital.ReservedPercent / 10000d * vital.Total) + vital.ReservedFlat;
        bool relationships = vital.Total > 0 && vital.Total <= 500_000 &&
            vital.Current > 0 && vital.Current <= vital.Total &&
            vital.ReservedFlat >= 0 && vital.ReservedFlat <= vital.Total &&
            vital.ReservedPercent is >= 0 and <= 10000 &&
            reserved >= 0 && reserved < vital.Total && vital.Current <= vital.Total - reserved &&
            float.IsFinite(vital.Regeneration);
        Add("health-vital-relationships", relationships,
            $"Total={vital.Total}, current={vital.Current}, reserved={reserved}, regeneration={vital.Regeneration}.");

        // These facts must come from an independently observed player-health source.
        // They are deliberately absent from the blind request's required context keys.
        if (request.ContextFacts.TryGetValue("observed-player-hp-current", out string? currentText))
        {
            if (!int.TryParse(currentText, out int observed))
                return new(evidence.ToImmutable(), false, "Observed player HP current is malformed.");
            Add("player-hp-current-correlation", vital.Current == observed,
                $"Vital current={vital.Current}; independently observed player HP={observed}.");
        }
        if (request.ContextFacts.TryGetValue("observed-player-hp-total", out string? totalText))
        {
            if (!int.TryParse(totalText, out int observed))
                return new(evidence.ToImmutable(), false, "Observed player HP total is malformed.");
            Add("player-hp-total-correlation", vital.Total == observed,
                $"Vital total={vital.Total}; independently observed player HP total={observed}.");
        }
        if (!memory.TryRead(address, out VitalStruct repeat))
            return new(evidence.ToImmutable(), false, $"Repeated VitalStruct read failed at 0x{address:X}.");
        Add("stable-repeat-observation",
            vital.PtrToLifeComponent == repeat.PtrToLifeComponent &&
            vital.Total == repeat.Total && vital.Current == repeat.Current &&
            vital.ReservedFlat == repeat.ReservedFlat && vital.ReservedPercent == repeat.ReservedPercent,
            $"Repeated owner, total, current, and reservations at Life+0x{request.CandidateValue:X}.");
        return Done();
    }
}
