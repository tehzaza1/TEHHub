namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Buffers.Binary;
using System.Collections.Immutable;
using TEHhub.Offsets;
using TEHhub.Offsets.Objects;

/// <summary>Read-only OD-001 orchestration. The configured value reaches only current validation and post-decision comparison.</summary>
public static class Od001GameStatesRecovery
{
    public const string TargetId = "pattern_game_states";
    public const string StrategyId = "od-001-game-states-pattern";

    public static RecoveryResult Run(RecoverySession session, long? currentResolvedAddress = null,
        IEnumerable<long>? historicalResolvedAddresses = null, long maxScanBytes = 128L * 1024 * 1024)
    {
        var target = RecoveryTargetSpec.Create(TargetId,
            new RecoveryTargetScope("main-module", session.Identity.ProcessName), StrategyId,
            rootAddress: session.Identity.ModuleBase,
            approvedHypotheses: [StructuralHypothesis.StaticPattern]);
        return new RecoveryCoordinator().Run(session, target,
            new Od001PatternDiscovery(session.Identity.ModuleSize, maxScanBytes),
            new Od001GameStateValidator(), currentResolvedAddress, historicalResolvedAddresses);
    }
}

internal sealed class Od001PatternDiscovery(long moduleSize, long maxScanBytes) : IBlindDiscoveryStrategy
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxSections = 96;
    private static readonly Pattern Signature = StaticOffsetsPatterns.Patterns.Single(p => p.Name == "Game States");
    public string StrategyId => Od001GameStatesRecovery.StrategyId;

    public DiscoveryOutcome Discover(IRecoveryReadOnlyMemory memory, BlindDiscoveryRequest request)
    {
        var found = ImmutableArray.CreateBuilder<DiscoveredCandidate>();
        var regions = ImmutableArray.CreateBuilder<string>();
        long scanned = 0;
        int matches = 0;
        DiscoveryOutcome Finish(bool complete, string? error = null) =>
            new(found.ToImmutable(), complete, error,
                new DiscoveryScanEvidence(scanned, matches, regions.ToImmutable()));

        if (request.TargetId != Od001GameStatesRecovery.TargetId ||
            !request.ApprovedHypotheses.Contains(StructuralHypothesis.StaticPattern) ||
            request.AnchorAddress < 0x10000 || moduleSize <= 0 || maxScanBytes <= 0)
            return Finish(false, "OD-001 module identity, bounds, or structural hypothesis is invalid.");
        long moduleEnd;
        try { moduleEnd = checked(request.AnchorAddress + moduleSize); }
        catch (OverflowException) { return Finish(false, "Module bounds overflow."); }

        // Read only the PE headers and sections marked both readable and executable.
        byte[] header = new byte[0x1000];
        if (!memory.TryReadBytes(request.AnchorAddress, header))
            return Finish(false, "Main module PE header read failed.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 0x5A4D)
            return Finish(false, "Main module DOS signature is invalid.");
        int pe = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C));
        if (pe < 0 || pe + 24 > header.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pe)) != 0x00004550)
            return Finish(false, "Main module PE signature is invalid or outside the header.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 20));
        int sections = pe + 24 + optionalSize;
        if (count is <= 0 or > MaxSections || sections < 0 || sections + count * 40 > header.Length)
            return Finish(false, "Main module section table is outside bounded PE headers.");

        var scanRanges = new List<(long Start, long End)>();
        for (int i = 0; i < count; i++)
        {
            int entry = sections + i * 40;
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(entry + 8));
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(entry + 12));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(entry + 36));
            if ((flags & 0x60000000) != 0x60000000 || virtualSize == 0) continue;
            if ((long)rva + virtualSize > moduleSize)
                return Finish(false, "Executable/readable PE section exceeds module bounds.");
            long start = request.AnchorAddress + rva;
            long end = start + virtualSize;
            scanRanges.Add((start, end));
        }
        if (scanRanges.Count == 0) return Finish(false, "No executable/readable module section was found.");
        scanRanges.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < scanRanges.Count; i++)
            if (scanRanges[i].Start < scanRanges[i - 1].End)
                return Finish(false, "Executable/readable module sections overlap.");

        foreach (var (start, end) in scanRanges)
        {
            regions.Add($"0x{start:X}-0x{end:X}");
            long length = end - start;
            if (length > maxScanBytes - scanned)
                return Finish(false, "OD-001 scan budget exceeded before complete module scan.");
            // Overlap each chunk by signature length minus one; visit each match address once.
            long cursor = start;
            while (cursor < end)
            {
                int payload = (int)Math.Min(ChunkSize, end - cursor);
                int readLength = (int)Math.Min(end - cursor, payload + Signature.Data.Length - 1);
                byte[] bytes = new byte[readLength];
                if (!memory.TryReadBytes(cursor, bytes))
                    return Finish(false, $"Executable module read failed at 0x{cursor:X} ({readLength} bytes).");
                scanned += payload;
                int last = Math.Min(payload - 1, readLength - Signature.Data.Length);
                for (int position = 0; position <= last; position++)
                {
                    bool match = true;
                    for (int j = 0; j < Signature.Data.Length; j++)
                        if (Signature.Mask[j] && bytes[position + j] != Signature.Data[j])
                        { match = false; break; }
                    if (!match) continue;
                    matches++;
                    long matchAddress = cursor + position;
                    string id = $"match-rva-{matchAddress - request.AnchorAddress:X}";
                    int dispAt = position + Signature.BytesToSkip;
                    int displacement = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(dispAt, 4));
                    long resolved = 0;
                    bool valid;
                    try
                    {
                        resolved = checked(matchAddress + Signature.BytesToSkip + 4L + displacement);
                        valid = resolved >= request.AnchorAddress && resolved <= moduleEnd - IntPtr.Size &&
                            memory.IsValidAddress(resolved);
                    }
                    catch (OverflowException) { valid = false; }
                    found.Add(new DiscoveredCandidate(id, resolved,
                        $"signature at 0x{matchAddress:X} (RVA 0x{matchAddress - request.AnchorAddress:X}); resolved 0x{resolved:X} (RVA 0x{resolved - request.AnchorAddress:X})",
                        [new RecoveryEvidenceRecord("signature-match", RecoveryEvidenceResult.PASS,
                            $"OD-001 signature matched at 0x{matchAddress:X}.", true, false),
                         new RecoveryEvidenceRecord("rip-disp32-module-target",
                            valid ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                            valid ? $"RIP displacement {displacement} resolves within main module at 0x{resolved:X}." :
                                $"RIP displacement {displacement} resolves outside valid main-module target semantics (0x{resolved:X}).",
                            true, false)]));
                }
                cursor += payload;
            }
        }
        return Finish(true);
    }
}

internal sealed class Od001GameStateValidator : IIndependentCandidateValidator
{
    public IndependentValidationOutcome Validate(IRecoveryReadOnlyMemory memory, IndependentValidationRequest request)
    {
        var evidence = ImmutableArray.CreateBuilder<RecoveryEvidenceRecord>();
        void Add(string predicate, bool pass, string detail) => evidence.Add(new RecoveryEvidenceRecord(
            predicate, pass ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL, detail, true, true));
        IndependentValidationOutcome Done() => new(evidence.ToImmutable(), true);
        if (request.TargetId != Od001GameStatesRecovery.TargetId || request.CandidateValue < 0x10000 ||
            !memory.IsValidAddress(request.CandidateValue))
        {
            Add("resolved-address", false, "Resolved static address is outside readable target semantics.");
            return Done();
        }
        Add("resolved-address", true, $"Static address 0x{request.CandidateValue:X} is addressable.");
        if (!memory.TryRead(request.CandidateValue, out GameStateStaticOffset staticRoot))
            return new(evidence.ToImmutable(), false, "GameState static root read failed.");
        long root = staticRoot.GameState.ToInt64();
        bool rootValid = root >= 0x10000 && memory.IsValidAddress(root);
        Add("game-state-root", rootValid, $"Static root points to 0x{root:X}.");
        if (!rootValid) return Done();
        if (!memory.TryRead(root + 0x10, out TEHhub.Offsets.Natives.StdVector current) ||
            !memory.TryRead(root + 0x90, out long inGame))
            return new(evidence.ToImmutable(), false, "GameState current vector or InGameState slot read failed.");
        long first = current.First.ToInt64(), last = current.Last.ToInt64(), end = current.End.ToInt64();
        bool vectorShape = first >= 0x10000 && first <= last && last <= end &&
            (last - first) % IntPtr.Size == 0 && (end - first) % IntPtr.Size == 0 &&
            last - first >= 2 * IntPtr.Size && last - first <= GameStateHelper.TOTAL_STATES * IntPtr.Size &&
            memory.IsValidAddress(first);
        Add("current-state-vector", vectorShape,
            $"CurrentStatePtr=[0x{first:X},0x{last:X},0x{end:X}].");
        if (!vectorShape) return Done();
        // GameStates.UpdateData uses the second-to-last vector entry as current state.
        if (!memory.TryRead(last - 2 * IntPtr.Size, out long currentState))
            return new(evidence.ToImmutable(), false, "CurrentStatePtr active element read failed.");
        bool currentValid = currentState >= 0x10000 && memory.IsValidAddress(currentState);
        Add("current-state-object", currentValid, $"Current state object is 0x{currentState:X}.");
        bool inGameValid = inGame >= 0x10000 && memory.IsValidAddress(inGame);
        Add("in-game-state-slot", inGameValid, $"States[4].X is 0x{inGame:X}.");
        if (!currentValid || !inGameValid) return Done();
        bool currentInTable = false;
        for (int index = 0; index < GameStateHelper.TOTAL_STATES; index++)
        {
            if (!memory.TryRead(root + 0x50 + index * 2 * IntPtr.Size, out long stateEntry))
                return new(evidence.ToImmutable(), false, $"GameState States[{index}].X read failed.");
            if (stateEntry == currentState) currentInTable = true;
        }
        Add("current-state-table-relationship", currentInTable,
            $"Current state 0x{currentState:X} is {(currentInTable ? "present" : "absent")} in the 13-state GameState table.");
        if (!memory.TryRead(inGame + 0x290, out long area) ||
            !memory.TryRead(inGame + 0x368, out long world))
            return new(evidence.ToImmutable(), false, "InGameState downstream fields read failed.");
        bool downstream = (area == 0 || memory.IsValidAddress(area)) &&
            (world == 0 || memory.IsValidAddress(world));
        Add("in-game-state-downstream", downstream,
            $"InGameState area=0x{area:X}, world=0x{world:X}; zero is allowed while inactive.");
        if (!memory.TryRead(request.CandidateValue, out GameStateStaticOffset repeatStatic) ||
            !memory.TryRead(root + 0x90, out long repeatInGame))
            return new(evidence.ToImmutable(), false, "GameState repeated read failed.");
        Add("stable-repeat-read", repeatStatic.GameState.ToInt64() == root && repeatInGame == inGame,
            "Static root and States[4].X were read twice in the same context.");
        return Done();
    }
}
