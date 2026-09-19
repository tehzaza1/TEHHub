namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;

public sealed class RecoveryV1TargetDescriptor
{
    public string TargetId { get; }
    public string ScopeKey { get; }
    public string StrategyFamily { get; }
    public ImmutableArray<string> RunnableRecoveryDependencies { get; }
    public ImmutableArray<string> RequiredTrustedAnchors { get; }
    public ImmutableArray<string> RequiredContextKeys { get; }
    public PriorEvidenceRecord PriorEvidence { get; }
    public Func<ImmutableArray<VersionedThreshold>> CanonicalThresholdsSupplier { get; }
    public Func<RecoverySession, TargetExecutionInput, RecoveryResult> ExecuteTarget { get; }

    public RecoveryV1TargetDescriptor(
        string targetId,
        string scopeKey,
        string strategyFamily,
        IEnumerable<string> runnableRecoveryDependencies,
        IEnumerable<string> requiredTrustedAnchors,
        IEnumerable<string> requiredContextKeys,
        PriorEvidenceRecord priorEvidence,
        Func<ImmutableArray<VersionedThreshold>> canonicalThresholdsSupplier,
        Func<RecoverySession, TargetExecutionInput, RecoveryResult> executeTarget)
    {
        TargetId = targetId;
        ScopeKey = scopeKey;
        StrategyFamily = strategyFamily;
        RunnableRecoveryDependencies = runnableRecoveryDependencies.ToImmutableArray();
        RequiredTrustedAnchors = requiredTrustedAnchors.ToImmutableArray();
        RequiredContextKeys = requiredContextKeys.ToImmutableArray();
        PriorEvidence = priorEvidence;
        CanonicalThresholdsSupplier = canonicalThresholdsSupplier;
        ExecuteTarget = executeTarget;
    }
}

public static class RecoveryV1Registry
{
    public const string Od001Id = "OD-001";
    public const string Od063Id = "OD-063";
    public const string Od114Id = "OD-114";
    public const string Od144Id = "OD-144";
    public const string Od145Id = "OD-145";

    private static readonly Dictionary<string, RecoveryV1TargetDescriptor> Descriptors =
        new(StringComparer.OrdinalIgnoreCase);

    static RecoveryV1Registry()
    {
        // 1. OD-001: Game States Pattern
        Register(new RecoveryV1TargetDescriptor(
            Od001Id,
            "main-module",
            "Pattern / Signature Discovery",
            runnableRecoveryDependencies: [],
            requiredTrustedAnchors: [],
            requiredContextKeys: [],
            priorEvidence: new PriorEvidenceRecord(
                "prior-proof-od001-states",
                Od001Id,
                LiveEvidenceStatus.LIVE_RECOVERY_PROVEN,
                "BlindDiscovery",
                "PoE2Client.exe",
                "commit 0ee3545",
                "Live PE module scan discovered Game States signature and resolved InGameState RIP offset."),
            canonicalThresholdsSupplier: () => [
                new("MaxScanBytes", 128L * 1024 * 1024, "Od001GameStatesRecovery.maxScanBytes", ThresholdClassification.SAFETY_BUDGET, "Cap for executable PE module memory scanned"),
                new("ChunkSize", 64 * 1024, "Od001PatternDiscovery.ChunkSize", ThresholdClassification.SAFETY_BUDGET, "Buffered memory scan chunk size"),
                new("MaxSections", 96, "Od001PatternDiscovery.MaxSections", ThresholdClassification.SAFETY_BUDGET, "PE section table traversal limit"),
                new("SectionFlags", 0x60000000u, "Od001PatternDiscovery (IMAGE_SCN_MEM_EXECUTE | IMAGE_SCN_MEM_READ)", ThresholdClassification.STRUCTURAL_ABI_INVARIANT, "PE executable and readable section requirement")
            ],
            executeTarget: (session, input) => Od001GameStatesRecovery.Run(
                session,
                input.CurrentValidation?.CurrentNumericValue,
                input.PostComparison?.HistoricalNumericValues)
        ));

        // 2. OD-063: Life Health Field
        Register(new RecoveryV1TargetDescriptor(
            Od063Id,
            "local-player-life-component",
            "Runtime Semantic Field Discovery",
            runnableRecoveryDependencies: [],
            requiredTrustedAnchors: ["comp_life"],
            requiredContextKeys: ["local-player-present", "life-component-readable", "player-health-meaningful"],
            priorEvidence: new PriorEvidenceRecord(
                "prior-proof-od063-health",
                Od063Id,
                LiveEvidenceStatus.LIVE_RECOVERY_PROVEN,
                "BlindDiscovery",
                "PoE2Client.exe",
                "commit 984802b",
                "Live Life component bounded field scan discovered VitalStruct at +0x1B0 with player HP correlation."),
            canonicalThresholdsSupplier: () => [
                new("SearchBytes", Od063LifeHealthRecovery.SearchBytes, "Od063LifeHealthRecovery.SearchBytes", ThresholdClassification.DISCOVERY_BOUND, "Bounded range in Life component"),
                new("ComponentHeaderFirstOffset", 0x10, "Od063VitalDiscovery.first", ThresholdClassification.STRUCTURAL_ABI_INVARIANT, "Offset after ComponentHeader"),
                new("VitalStructWidth", 0x38, "Od063VitalDiscovery.width", ThresholdClassification.STRUCTURAL_ABI_INVARIANT, "Size of VitalStruct layout"),
                new("Alignment", 8, "Od063VitalDiscovery.stride", ThresholdClassification.STRUCTURAL_ABI_INVARIANT, "Alignment step for VitalStruct"),
                new("MaxPlausibleHp", 50000, "Od063VitalValidator", ThresholdClassification.VALIDATION_THRESHOLD, "Plausible player HP upper bound")
            ],
            executeTarget: (session, input) => Od063LifeHealthRecovery.Run(
                session,
                input.CurrentValidation?.CurrentNumericValue,
                input.PostComparison?.HistoricalNumericValues)
        ));

        // 3. OD-114: Rune Station Bounded Owner
        Register(new RecoveryV1TargetDescriptor(
            Od114Id,
            "expedition-rune-station-state-machine",
            "Bounded Owner Discovery",
            runnableRecoveryDependencies: [],
            requiredTrustedAnchors: ["comp_statemachine", "expedition_rune_station_entity"],
            requiredContextKeys: ["expedition-rune-station-present", "state-machine-present"],
            priorEvidence: new PriorEvidenceRecord(
                "prior-proof-od114-runestation",
                Od114Id,
                LiveEvidenceStatus.LIVE_RECOVERY_PROVEN,
                "BlindDiscovery",
                "PoE2Client.exe",
                "commit cc5deb2",
                "Live bounded owner discovery verified Rune Station invariants and delta offset."),
            canonicalThresholdsSupplier: () => [
                new("Radius", Od114RuneStationOwnerRecovery.Radius, "Od114RuneStationOwnerRecovery.Radius", ThresholdClassification.DISCOVERY_BOUND, "Search radius around listener pointer"),
                new("Alignment", Od114RuneStationOwnerRecovery.Alignment, "Od114RuneStationOwnerRecovery.Alignment", ThresholdClassification.STRUCTURAL_ABI_INVARIANT, "8-byte struct pointer alignment"),
                new("CandidatesPerListener", Od114RuneStationOwnerRecovery.CandidatesPerListener, "Od114RuneStationOwnerRecovery.CandidatesPerListener", ThresholdClassification.DISCOVERY_BOUND, "Exact candidates per listener (129)"),
                new("SocketCountRange", "1..16", "Od114RuneStationOwnerRecovery.Inspect", ThresholdClassification.VERSIONED_SEMANTIC_INVARIANT, "Plausible Expedition socket count bounds")
            ],
            executeTarget: (session, input) => Od114RuneStationOwnerRecovery.Run(
                session,
                input.CurrentValidation?.CurrentNumericValue,
                input.PostComparison?.HistoricalNumericValues)
        ));

        // 4. OD-144: Runeshape Combinations Panel
        Register(new RecoveryV1TargetDescriptor(
            Od144Id,
            Od144RuneshapePanelRecovery.ScopeKey,
            "Semantic UI Discovery",
            runnableRecoveryDependencies: [Od001Id],
            requiredTrustedAnchors: ["OD-007", "OD-010", "OD-014", "OD-020"],
            requiredContextKeys: ["runeshape-ui-context"],
            priorEvidence: new PriorEvidenceRecord(
                "prior-proof-od144-runeshape",
                Od144Id,
                LiveEvidenceStatus.LIVE_RECOVERY_PROVEN,
                "BlindDiscovery",
                "PoE2Client.exe",
                "commit 528418e",
                "Live hideout Runesmithing panel semantic discovery verified 321 recipe invariant and child path."),
            canonicalThresholdsSupplier: () => [
                new("RawFingerprintV1", Od144RuneshapePanelRecovery.RawFingerprintV1, "Od144RuneshapePanelRecovery.RawFingerprintV1", ThresholdClassification.VERSIONED_SEMANTIC_INVARIANT, "Raw UI Element Flags including visible bit"),
                new("VisibleMask", Od144RuneshapePanelRecovery.VisibleMask, "Od144RuneshapePanelRecovery.VisibleMask", ThresholdClassification.STRUCTURAL_ABI_INVARIANT, "Mask for visibility bit in flags"),
                new("MaskedFingerprint", Od144RuneshapePanelRecovery.MaskedFingerprint, "Od144RuneshapePanelRecovery.MaskedFingerprint", ThresholdClassification.VERSIONED_SEMANTIC_INVARIANT, "Masked UI Element structural fingerprint"),
                new("RecipeCountV1", Od144RuneshapePanelRecovery.RecipeCountV1, "Od144RuneshapePanelRecovery.RecipeCountV1", ThresholdClassification.VERSIONED_SEMANTIC_INVARIANT, "Exact Runesmithing combinations count (321)"),
                new("MaxNodes", Od144RuneshapePanelRecovery.MaxNodes, "Od144RuneshapePanelRecovery.MaxNodes", ThresholdClassification.SAFETY_BUDGET, "Traversal node safety budget"),
                new("MaxDepth", Od144RuneshapePanelRecovery.MaxDepth, "Od144RuneshapePanelRecovery.MaxDepth", ThresholdClassification.SAFETY_BUDGET, "UI hierarchy depth budget"),
                new("MaxChildren", Od144RuneshapePanelRecovery.MaxChildren, "Od144RuneshapePanelRecovery.MaxChildren", ThresholdClassification.SAFETY_BUDGET, "Maximum child vector capacity budget"),
                new("MaxReads", Od144RuneshapePanelRecovery.MaxReads, "Od144RuneshapePanelRecovery.MaxReads", ThresholdClassification.SAFETY_BUDGET, "Read operation safety budget")
            ],
            executeTarget: (session, input) => Od144RuneshapePanelRecovery.Run(
                session,
                input.CurrentValidation?.CurrentPath,
                input.PostComparison?.HistoricalPaths)
        ));

        // 5. OD-145: Atlas Layout Constants
        Register(new RecoveryV1TargetDescriptor(
            Od145Id,
            Od145AtlasLayoutRecovery.ScopeKey,
            "Multi-Instance Structural Field Discovery",
            runnableRecoveryDependencies: [Od001Id],
            requiredTrustedAnchors: ["OD-007", "OD-010", "OD-014", "OD-020", "OD-134"],
            requiredContextKeys: ["atlas-ui-context"],
            priorEvidence: new PriorEvidenceRecord(
                "prior-proof-od145-atlas",
                Od145Id,
                LiveEvidenceStatus.LIVE_RECOVERY_PROVEN,
                "BlindDiscovery",
                "PoE2Client.exe",
                "commit 86fcf87",
                "Live multi-instance structural field discovery across 64 nodes recovered GridPosition +0x310 and Connections +0x590."),
            canonicalThresholdsSupplier: () => [
                new("GridStartOffset", Od145AtlasLayoutRecovery.GridStartOffset, "Od145AtlasLayoutRecovery.GridStartOffset", ThresholdClassification.DISCOVERY_BOUND, "Grid scan start offset"),
                new("GridEndOffset", Od145AtlasLayoutRecovery.GridEndOffset, "Od145AtlasLayoutRecovery.GridEndOffset", ThresholdClassification.DISCOVERY_BOUND, "Grid scan end offset"),
                new("GridStep", Od145AtlasLayoutRecovery.GridStep, "Od145AtlasLayoutRecovery.GridStep", ThresholdClassification.DISCOVERY_BOUND, "Grid scan step (4 bytes)"),
                new("GridExpectedCandidates", Od145AtlasLayoutRecovery.GridExpectedCandidates, "Od145AtlasLayoutRecovery.GridExpectedCandidates", ThresholdClassification.DISCOVERY_BOUND, "Expected Grid candidates (383)"),
                new("ConnStartOffset", Od145AtlasLayoutRecovery.ConnStartOffset, "Od145AtlasLayoutRecovery.ConnStartOffset", ThresholdClassification.DISCOVERY_BOUND, "Connections scan start offset"),
                new("ConnEndOffset", Od145AtlasLayoutRecovery.ConnEndOffset, "Od145AtlasLayoutRecovery.ConnEndOffset", ThresholdClassification.DISCOVERY_BOUND, "Connections scan end offset"),
                new("ConnStep", Od145AtlasLayoutRecovery.ConnStep, "Od145AtlasLayoutRecovery.ConnStep", ThresholdClassification.DISCOVERY_BOUND, "Connections scan step (8 bytes)"),
                new("ConnExpectedCandidates", Od145AtlasLayoutRecovery.ConnExpectedCandidates, "Od145AtlasLayoutRecovery.ConnExpectedCandidates", ThresholdClassification.DISCOVERY_BOUND, "Expected Connections candidates (190)"),
                new("StdVectorHeaderSize", Od145AtlasLayoutRecovery.StdVectorHeaderSize, "Od145AtlasLayoutRecovery.StdVectorHeaderSize", ThresholdClassification.STRUCTURAL_ABI_INVARIANT, "MSVC std::vector header size (24 bytes)"),
                new("EdgeElementStride", Od145AtlasLayoutRecovery.EdgeElementStride, "Od145AtlasLayoutRecovery.EdgeElementStride", ThresholdClassification.STRUCTURAL_ABI_INVARIANT, "AtlasConnectionEdge element stride (20 bytes Pack=1)"),
                new("MinAtlasNodes", Od145AtlasLayoutRecovery.MinAtlasNodes, "Od145AtlasLayoutRecovery.MinAtlasNodes", ThresholdClassification.VALIDATION_THRESHOLD, "Minimum required Atlas node population (16)"),
                new("MinEdges", Od145AtlasLayoutRecovery.MinEdges, "Od145AtlasLayoutRecovery.MinEdges", ThresholdClassification.VALIDATION_THRESHOLD, "Minimum required graph edges (4)"),
                new("MaxNodes", Od145AtlasLayoutRecovery.MaxNodes, "Od145AtlasLayoutRecovery.MaxNodes", ThresholdClassification.SAFETY_BUDGET, "Max canvas child nodes budget (512)"),
                new("MaxEdges", Od145AtlasLayoutRecovery.MaxEdges, "Od145AtlasLayoutRecovery.MaxEdges", ThresholdClassification.SAFETY_BUDGET, "Max canvas edges budget (2048)"),
                new("MaxReads", Od145AtlasLayoutRecovery.MaxReads, "Od145AtlasLayoutRecovery.MaxReads", ThresholdClassification.SAFETY_BUDGET, "Max memory reads budget (65536)"),
                new("MaxMilliseconds", Od145AtlasLayoutRecovery.MaxMilliseconds, "Od145AtlasLayoutRecovery.MaxMilliseconds", ThresholdClassification.SAFETY_BUDGET, "Max execution duration budget (30000ms)")
            ],
            executeTarget: (session, input) => Od145AtlasLayoutRecovery.Run(
                session,
                input.CurrentValidation?.CurrentFieldPair,
                input.PostComparison?.HistoricalFieldPairs)
        ));
    }

    private static void Register(RecoveryV1TargetDescriptor descriptor)
    {
        Descriptors[descriptor.TargetId] = descriptor;
    }

    public static IReadOnlyList<RecoveryV1TargetDescriptor> AllDescriptors =>
        Descriptors.Values.OrderBy(d => d.TargetId, StringComparer.Ordinal).ToList();

    public static ImmutableArray<string> AllTargetIds =>
        [Od001Id, Od063Id, Od114Id, Od144Id, Od145Id];

    public static ImmutableArray<string> AllRunnableTargetIds => AllTargetIds;

    public static bool TryGet(string id, out RecoveryV1TargetDescriptor? descriptor)
    {
        string normalized = NormalizeId(id);
        return Descriptors.TryGetValue(normalized, out descriptor);
    }

    public static RecoveryV1TargetDescriptor Get(string id)
    {
        if (!TryGet(id, out var descriptor) || descriptor is null)
            throw new KeyNotFoundException($"Target '{id}' is not a registered runnable Recovery V1 strategy.");
        return descriptor;
    }

    public static bool IsRunnableTarget(string id) =>
        Descriptors.ContainsKey(NormalizeId(id));

    public static readonly ImmutableHashSet<string> KnownAnchorIds =
        ["OD-007", "OD-010", "OD-014", "OD-020", "OD-134", "COMP_LIFE", "COMP_STATEMACHINE", "EXPEDITION_RUNE_STATION_ENTITY"];

    public static bool IsKnownAnchor(string id)
    {
        string norm = NormalizeId(id);
        return KnownAnchorIds.Contains(norm) || KnownAnchorIds.Contains(id.Trim().ToUpperInvariant());
    }

    public static string NormalizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        string clean = id.Trim().ToUpperInvariant();
        return clean switch
        {
            "OD001" or "001" or "OD-001" or "PATTERN_GAME_STATES" => Od001Id,
            "OD063" or "063" or "OD-063" or "COMP_LIFE_HEALTH" => Od063Id,
            "OD114" or "114" or "OD-114" or "COMP_STATEMACHINE_RUNE_STATION_OWNER" => Od114Id,
            "OD144" or "144" or "OD-144" => Od144Id,
            "OD145" or "145" or "OD-145" => Od145Id,
            _ => clean
        };
    }

    public static ImmutableArray<string> ResolveExecutionOrder(
        IEnumerable<string> requestedTargetIds,
        out ImmutableArray<string> autoAddedRunnableDependencies)
    {
        var requestedList = requestedTargetIds.Select(NormalizeId).Distinct(StringComparer.Ordinal).ToList();
        var autoAdded = new List<string>();
        var effective = new HashSet<string>(requestedList, StringComparer.Ordinal);

        foreach (string id in requestedList)
        {
            if (TryGet(id, out var desc) && desc is not null)
            {
                foreach (string runnableDep in desc.RunnableRecoveryDependencies)
                {
                    if (effective.Add(runnableDep))
                    {
                        autoAdded.Add(runnableDep);
                    }
                }
            }
        }

        autoAddedRunnableDependencies = autoAdded.ToImmutableArray();

        // Maintain canonical V1 ordering: OD-001, OD-063, OD-114, OD-144, OD-145.
        var ordered = AllTargetIds.Where(effective.Contains).ToImmutableArray();
        return ordered;
    }
}

public static class RecoveryV1CliParser
{
    public static bool TryParse(string arg, out ImmutableArray<string> targetIds, out string? error)
    {
        targetIds = [];
        error = null;

        if (string.IsNullOrWhiteSpace(arg))
        {
            error = "Recovery target argument cannot be empty.";
            return false;
        }

        string trimmed = arg.Trim();
        if (trimmed.Equals("v1", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targetIds = RecoveryV1Registry.AllTargetIds;
            return true;
        }

        var tokens = trimmed.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resolved = ImmutableArray.CreateBuilder<string>();

        foreach (string token in tokens)
        {
            string normalized = RecoveryV1Registry.NormalizeId(token);
            if (!RecoveryV1Registry.IsRunnableTarget(normalized))
            {
                if (RecoveryV1Registry.IsKnownAnchor(token))
                {
                    error = $"Target '{normalized}' is a trusted anchor or not runnable in Recovery V1. Runnable targets: od-001, od-063, od-114, od-144, od-145, v1, all.";
                }
                else
                {
                    error = $"Unknown recovery target '{token}'. Supported Recovery V1 targets: od-001, od-063, od-114, od-144, od-145, v1, all.";
                }
                return false;
            }
            if (!resolved.Contains(normalized))
            {
                resolved.Add(normalized);
            }
        }

        if (resolved.Count == 0)
        {
            error = "No valid recovery targets specified.";
            return false;
        }

        targetIds = resolved.ToImmutable();
        return true;
    }
}
