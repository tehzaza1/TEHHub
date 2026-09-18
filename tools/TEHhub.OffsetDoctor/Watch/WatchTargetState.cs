namespace TEHhub.OffsetDoctor.Watch;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;

public sealed class WatchTargetState
{
    public WatchTargetInfo TargetInfo { get; }

    public string NodeId => TargetInfo.NodeId;
    public string DisplayName => TargetInfo.DisplayName;

    public ValidationStatus? FirstObservedStatus { get; private set; }
    public ValidationStatus? LatestObservedStatus { get; private set; }
    public ValidationStatus? BestObservedStatus { get; private set; }

    public TimeSpan? FirstChangeElapsed { get; private set; }
    public TimeSpan? LastChangeElapsed { get; private set; }

    public string? LastRawValue { get; private set; }
    public IntPtr LastResolvedAddress { get; private set; }
    public string? LastReason { get; private set; }
    public EvidenceRecord? StrongestEvidence { get; private set; }

    public bool PlayerActionObserved { get; private set; }
    public int TransitionCount { get; private set; }

    public string Recommendation
    {
        get
        {
            if (BestObservedStatus == ValidationStatus.VALID)
            {
                return "VALID evidence captured";
            }
            if (LatestObservedStatus == ValidationStatus.BROKEN)
            {
                return "BROKEN (failed while active)";
            }
            if (PlayerActionObserved)
            {
                return "remains UNVERIFIED (structural evidence verified during active state)";
            }
            return "still waiting/inactive";
        }
    }

    public WatchTargetState(WatchTargetInfo targetInfo)
    {
        TargetInfo = targetInfo;
    }

    public string? Update(ValidationResult result, TimeSpan elapsed)
    {
        var prevStatus = LatestObservedStatus;
        var prevAddress = LastResolvedAddress;
        var prevVal = LastRawValue;
        var prevReason = LastReason;

        var curStatus = result.Status;
        var curAddress = result.ResolvedAddress;
        var curVal = result.ExtractedValue?.ToString();
        var curReason = result.ErrorMessage;

        bool isFirst = !FirstObservedStatus.HasValue;

        if (isFirst)
        {
            FirstObservedStatus = curStatus;
            LatestObservedStatus = curStatus;
            BestObservedStatus = curStatus;
            LastResolvedAddress = curAddress;
            LastRawValue = curVal;
            LastReason = curReason;
            StrongestEvidence = result.Evidence.LastOrDefault(e => e.Passed) ?? result.Evidence.LastOrDefault();

            if (curStatus == ValidationStatus.VALID || IsActivePointerOrContent(result))
            {
                PlayerActionObserved = true;
            }

            return null;
        }

        // Check for meaningful change
        bool statusChanged = curStatus != prevStatus;
        bool addressChanged = curAddress != prevAddress;
        bool valChanged = curVal != prevVal;
        bool reasonChanged = curReason != prevReason;

        bool meaningfulTransition = statusChanged ||
                                     (addressChanged && (IsActiveAddress(curAddress) || IsActiveAddress(prevAddress))) ||
                                     (valChanged && !string.IsNullOrEmpty(curVal)) ||
                                     (reasonChanged && (curReason?.Contains("inactive") != prevReason?.Contains("inactive")));

        if (!meaningfulTransition)
        {
            return null;
        }

        TransitionCount++;
        FirstChangeElapsed ??= elapsed;
        LastChangeElapsed = elapsed;

        LatestObservedStatus = curStatus;
        LastResolvedAddress = curAddress;
        LastRawValue = curVal;
        LastReason = curReason;

        if (RankStatus(curStatus) > RankStatus(BestObservedStatus ?? ValidationStatus.BROKEN))
        {
            BestObservedStatus = curStatus;
        }

        var candidateEvidence = result.Evidence.LastOrDefault(e => e.Passed) ?? result.Evidence.LastOrDefault();
        if (candidateEvidence != null)
        {
            StrongestEvidence = candidateEvidence;
        }

        if (curStatus == ValidationStatus.VALID || IsActivePointerOrContent(result))
        {
            PlayerActionObserved = true;
        }

        // Format transition message
        string detail;
        if (curStatus == ValidationStatus.VALID)
        {
            detail = !string.IsNullOrEmpty(curVal) ? curVal : $"0x{curAddress.ToInt64():X}";
        }
        else if (IsActivePointerOrContent(result))
        {
            detail = !string.IsNullOrEmpty(curVal) ? curVal : $"active (0x{curAddress.ToInt64():X})";
        }
        else if (curReason != null && curReason.Contains("inactive or sentinel"))
        {
            detail = $"inactive (0x{curAddress.ToInt64():X})";
        }
        else
        {
            detail = curReason ?? (!string.IsNullOrEmpty(curVal) ? curVal : $"0x{curAddress.ToInt64():X}");
        }

        return $"[{elapsed:mm\\:ss\\.f}] {DisplayName} {prevStatus} -> {curStatus} {detail}";
    }

    private static int RankStatus(ValidationStatus status) => status switch
    {
        ValidationStatus.VALID => 4,
        ValidationStatus.UNVERIFIED => 3,
        ValidationStatus.BLOCKED => 2,
        ValidationStatus.BROKEN => 1,
        _ => 0
    };

    private static bool IsActiveAddress(IntPtr addr)
    {
        long val = addr.ToInt64();
        return val > 0x10000 && val < 0x7FFFFFFFFFFF && val != unchecked((long)0xC140000000000000);
    }

    private static bool IsActivePointerOrContent(ValidationResult result)
    {
        if (result.Status == ValidationStatus.VALID) return true;
        if (result.ErrorMessage != null && result.ErrorMessage.Contains("inactive")) return false;
        if (result.ErrorMessage != null && result.ErrorMessage.Contains("null")) return false;
        var extStr = result.ExtractedValue?.ToString();
        return IsActiveAddress(result.ResolvedAddress) || (!string.IsNullOrEmpty(extStr) && !extStr.Contains("Count=0"));
    }
}
