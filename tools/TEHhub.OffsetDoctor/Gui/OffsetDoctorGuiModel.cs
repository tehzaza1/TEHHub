namespace TEHhub.OffsetDoctor.Gui;

using TEHhub.OffsetDoctor.Baseline;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Validation;
using TEHhub.OffsetDoctor.Watch;

public sealed class OffsetDoctorGuiModel
{
    // Ground-truth inputs (All optional, empty string = not supplied; Max-only stable values)
    public string GoldInput = string.Empty;
    public string HpTotalInput = string.Empty;
    public string MpTotalInput = string.Empty;
    public string EsTotalInput = string.Empty;

    // Baseline configuration (Defaults safely to %LOCALAPPDATA%/TEHhub/OffsetDoctor/offsetdoctor-baseline.json)
    public string BaselinePathInput = BaselineSnapshot.GetDefaultBaselinePath();

    // Watch mode configuration
    public string WatchDurationSecInput = "10";
    public string WatchIntervalMsInput = "250";
    public string WatchTargetInput = "ui";
    public List<string> WatchLog { get; set; } = [];
    public WatchReport? LastWatchReport { get; set; }

    // Operation status & concurrency synchronization
    public bool IsBusy { get; set; }
    public string OperationStatus { get; set; } = "Ready";
    public string? ErrorMessage { get; set; }
    public string? NoticeMessage { get; set; }

    // Summary metrics
    public bool HasResults => Rows.Count > 0;
    public int ValidCount { get; set; }
    public int BrokenCount { get; set; }
    public int BlockedCount { get; set; }
    public int UnverifiedCount { get; set; }
    public int TotalCount => Rows.Count;

    // View rows (Sorted: BROKEN at top)
    public List<OffsetDoctorGuiRow> Rows { get; set; } = [];

    // Optional baseline comparison results
    public BaselineComparisonResult? ComparisonResult { get; set; }

    // Timestamp of last execution
    public DateTime? LastRunTimestampUtc { get; set; }
}

public sealed class OffsetDoctorGuiRow
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public ValidationStatus Status { get; init; }
    public bool IsStaticRoot { get; init; }
    public bool IsOptionalStateDependent { get; init; }
    public string? ObservedOrTraversalAddress { get; init; }
    public string? ExtractedValue { get; init; }
    public string? ShortReason { get; init; }
    public List<string> EvidenceSummary { get; init; } = [];
}
