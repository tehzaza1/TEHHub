namespace TEHhub.OffsetDoctor.Gui;

using System.Globalization;
using TEHhub.OffsetDoctor.Baseline;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Recovery;
using TEHhub.OffsetDoctor.Validation;
using TEHhub.OffsetDoctor.Watch;

public sealed class OffsetDoctorGuiController
{
    private readonly OffsetRecoveryEngine _recoveryEngine = new();
    private readonly BaselineCaptureEngine _captureEngine = new();
    private readonly BaselineComparisonEngine _comparisonEngine = new();
    private readonly OffsetWatchEngine _watchEngine = new();
    private readonly object _operationLock = new();

    public bool TryBuildGroundTruth(OffsetDoctorGuiModel model, out ValidationGroundTruth? groundTruth, out string? errorMessage)
    {
        errorMessage = null;
        groundTruth = null;

        int? ParseOptionalInt(string input, string fieldName, out string? err)
        {
            err = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var clean = input.Trim().Replace(",", string.Empty).Replace("_", string.Empty);
            if (!int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val) || val < 0)
            {
                err = $"Invalid {fieldName}: '{input}'. Must be a non-negative whole integer.";
                return null;
            }

            return val;
        }

        var gold = ParseOptionalInt(model.GoldInput, "Gold", out var goldErr);
        if (goldErr != null) { errorMessage = goldErr; return false; }

        var hpCur = ParseOptionalInt(model.HpCurrentInput, "HP Current", out var hpCurErr);
        if (hpCurErr != null) { errorMessage = hpCurErr; return false; }

        var hpTot = ParseOptionalInt(model.HpTotalInput, "HP Total", out var hpTotErr);
        if (hpTotErr != null) { errorMessage = hpTotErr; return false; }

        var mpCur = ParseOptionalInt(model.MpCurrentInput, "Mana Current", out var mpCurErr);
        if (mpCurErr != null) { errorMessage = mpCurErr; return false; }

        var mpTot = ParseOptionalInt(model.MpTotalInput, "Mana Total", out var mpTotErr);
        if (mpTotErr != null) { errorMessage = mpTotErr; return false; }

        var esCur = ParseOptionalInt(model.EsCurrentInput, "ES Current", out var esCurErr);
        if (esCurErr != null) { errorMessage = esCurErr; return false; }

        var esTot = ParseOptionalInt(model.EsTotalInput, "ES Total", out var esTotErr);
        if (esTotErr != null) { errorMessage = esTotErr; return false; }

        // If any value was provided, construct the ground truth object
        if (gold != null || hpCur != null || hpTot != null || mpCur != null || mpTot != null || esCur != null || esTot != null)
        {
            groundTruth = new ValidationGroundTruth
            {
                ExpectedGold = gold,
                ExpectedHpCurrent = hpCur,
                ExpectedHpTotal = hpTot,
                ExpectedMpCurrent = mpCur,
                ExpectedMpTotal = mpTot,
                ExpectedEsCurrent = esCur,
                ExpectedEsTotal = esTot
            };
        }

        return true;
    }

    public bool ValidateNow(IProcessMemoryReader reader, OffsetDoctorGuiModel model)
    {
        lock (_operationLock)
        {
            if (model.IsBusy)
            {
                model.ErrorMessage = "An operation is already in progress. Please wait.";
                return false;
            }
            model.IsBusy = true;
        }

        model.ErrorMessage = null;
        model.NoticeMessage = null;

        if (!TryBuildGroundTruth(model, out var gt, out var gtErr))
        {
            model.ErrorMessage = gtErr;
            lock (_operationLock) { model.IsBusy = false; }
            return false;
        }

        try
        {
            model.OperationStatus = "Validating memory offsets...";

            var report = _recoveryEngine.RunValidation(reader, gt);
            PopulateRowsFromReport(report, model);

            model.LastRunTimestampUtc = report.TimestampUtc;
            model.ComparisonResult = null;
            model.NoticeMessage = $"Validation completed: {model.ValidCount} VALID, {model.BrokenCount} BROKEN, {model.BlockedCount} BLOCKED, {model.UnverifiedCount} UNVERIFIED.";
            return true;
        }
        catch (Exception ex)
        {
            model.ErrorMessage = $"Validation failed: {ex.Message}";
            return false;
        }
        finally
        {
            lock (_operationLock)
            {
                model.IsBusy = false;
                model.OperationStatus = "Ready";
            }
        }
    }

    public bool WatchUi(
        IProcessMemoryReader reader,
        OffsetDoctorGuiModel model,
        int? durationSec = null,
        int? intervalMs = null,
        string? targetFilter = null,
        CancellationToken cancellationToken = default)
    {
        lock (_operationLock)
        {
            if (model.IsBusy)
            {
                model.ErrorMessage = "An operation is already in progress. Please wait.";
                return false;
            }
            model.IsBusy = true;
        }

        model.ErrorMessage = null;
        model.NoticeMessage = null;

        if (!TryBuildGroundTruth(model, out var gt, out var gtErr))
        {
            model.ErrorMessage = gtErr;
            lock (_operationLock) { model.IsBusy = false; }
            return false;
        }

        int dur = durationSec ?? (int.TryParse(model.WatchDurationSecInput, out var d) && d > 0 ? d : 10);
        int interval = intervalMs ?? (int.TryParse(model.WatchIntervalMsInput, out var iv) && iv > 0 ? iv : 250);
        string target = !string.IsNullOrWhiteSpace(targetFilter) ? targetFilter.Trim() :
                        !string.IsNullOrWhiteSpace(model.WatchTargetInput) ? model.WatchTargetInput.Trim() : "ui";

        try
        {
            model.OperationStatus = $"Watching '{target}' for {dur}s ({interval}ms interval)...";
            model.WatchLog.Clear();

            var watchReport = _watchEngine.RunWatch(
                reader,
                gt,
                TimeSpan.FromMilliseconds(interval),
                TimeSpan.FromSeconds(dur),
                target,
                onTransition: msg =>
                {
                    lock (model.WatchLog)
                    {
                        model.WatchLog.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {msg}");
                    }
                },
                cancellationToken: cancellationToken);

            model.LastWatchReport = watchReport;

            // Also update the UI with current validation report
            var validationReport = _recoveryEngine.RunValidation(reader, gt);
            PopulateRowsFromReport(validationReport, model);

            model.LastRunTimestampUtc = watchReport.EndTimeUtc;
            model.NoticeMessage = $"Watch completed: {watchReport.TargetSummaries.Count} targets watched, {watchReport.TransitionLog.Count} transitions recorded.";
            return true;
        }
        catch (Exception ex)
        {
            model.ErrorMessage = $"Watch UI failed: {ex.Message}";
            return false;
        }
        finally
        {
            lock (_operationLock)
            {
                model.IsBusy = false;
                model.OperationStatus = "Ready";
            }
        }
    }

    public bool CaptureBaseline(IProcessMemoryReader reader, OffsetDoctorGuiModel model, string? outputPath = null)
    {
        lock (_operationLock)
        {
            if (model.IsBusy)
            {
                model.ErrorMessage = "An operation is already in progress. Please wait.";
                return false;
            }
            model.IsBusy = true;
        }

        model.ErrorMessage = null;
        model.NoticeMessage = null;

        if (!TryBuildGroundTruth(model, out var gt, out var gtErr))
        {
            model.ErrorMessage = gtErr;
            lock (_operationLock) { model.IsBusy = false; }
            return false;
        }

        var targetPath = !string.IsNullOrWhiteSpace(outputPath) ? outputPath.Trim() :
                         !string.IsNullOrWhiteSpace(model.BaselinePathInput) ? model.BaselinePathInput.Trim() :
                         "offsetdoctor-baseline.json";

        try
        {
            model.OperationStatus = "Capturing baseline snapshot...";

            var snapshot = _captureEngine.Capture(reader, gt);
            BaselineSnapshot.SaveToFile(snapshot, targetPath);

            // Also update the UI with current validation rows
            var report = _recoveryEngine.RunValidation(reader, gt);
            PopulateRowsFromReport(report, model);

            model.LastRunTimestampUtc = snapshot.TimestampUtc;
            model.ComparisonResult = null;
            model.NoticeMessage = $"Baseline captured & saved to '{targetPath}' ({snapshot.Summary.ValidCount} VALID, {snapshot.Summary.BrokenCount} BROKEN, {snapshot.Summary.UnverifiedCount} UNVERIFIED).";
            return true;
        }
        catch (Exception ex)
        {
            model.ErrorMessage = $"Baseline capture failed: {ex.Message}";
            return false;
        }
        finally
        {
            lock (_operationLock)
            {
                model.IsBusy = false;
                model.OperationStatus = "Ready";
            }
        }
    }

    public bool CompareBaseline(IProcessMemoryReader reader, OffsetDoctorGuiModel model, string? inputPath = null)
    {
        lock (_operationLock)
        {
            if (model.IsBusy)
            {
                model.ErrorMessage = "An operation is already in progress. Please wait.";
                return false;
            }
            model.IsBusy = true;
        }

        model.ErrorMessage = null;
        model.NoticeMessage = null;

        if (!TryBuildGroundTruth(model, out var gt, out var gtErr))
        {
            model.ErrorMessage = gtErr;
            lock (_operationLock) { model.IsBusy = false; }
            return false;
        }

        var sourcePath = !string.IsNullOrWhiteSpace(inputPath) ? inputPath.Trim() :
                         !string.IsNullOrWhiteSpace(model.BaselinePathInput) ? model.BaselinePathInput.Trim() :
                         "offsetdoctor-baseline.json";

        try
        {
            model.OperationStatus = "Comparing memory against baseline...";

            if (!File.Exists(sourcePath))
            {
                model.ErrorMessage = $"Baseline file '{sourcePath}' does not exist.";
                return false;
            }

            var baseline = BaselineSnapshot.LoadFromFile(sourcePath);
            var report = _recoveryEngine.RunValidation(reader, gt);
            var compResult = _comparisonEngine.Compare(baseline, report);

            PopulateRowsFromReport(report, model);

            model.ComparisonResult = compResult;
            model.LastRunTimestampUtc = report.TimestampUtc;

            if (compResult.HasCriticalRegressions)
            {
                model.ErrorMessage = $"CRITICAL REGRESSIONS DETECTED: {compResult.CriticalCount} critical, {compResult.WarningCount} warnings.";
            }
            else if (compResult.WarningCount > 0)
            {
                model.NoticeMessage = $"Baseline compared: 0 critical regressions, {compResult.WarningCount} warnings, {compResult.InformationalCount} informational.";
            }
            else
            {
                model.NoticeMessage = $"Baseline compared: No critical baseline regressions detected ({compResult.InformationalCount} informational).";
            }

            return true;
        }
        catch (Exception ex)
        {
            model.ErrorMessage = $"Baseline comparison failed: {ex.Message}";
            return false;
        }
        finally
        {
            lock (_operationLock)
            {
                model.IsBusy = false;
                model.OperationStatus = "Ready";
            }
        }
    }

    public void ClearResults(OffsetDoctorGuiModel model)
    {
        lock (_operationLock)
        {
            if (model.IsBusy)
            {
                return;
            }
        }

        model.Rows.Clear();
        model.WatchLog.Clear();
        model.LastWatchReport = null;
        model.ValidCount = 0;
        model.BrokenCount = 0;
        model.BlockedCount = 0;
        model.UnverifiedCount = 0;
        model.ComparisonResult = null;
        model.ErrorMessage = null;
        model.NoticeMessage = null;
        model.LastRunTimestampUtc = null;
    }

    private static void PopulateRowsFromReport(OffsetDoctorReport report, OffsetDoctorGuiModel model)
    {
        var manifestNodes = OffsetManifest.CreateFullRepositoryManifest();
        var manifestMap = manifestNodes.ToDictionary(n => n.Id, n => n);

        var rows = new List<OffsetDoctorGuiRow>();

        foreach (var result in report.Results)
        {
            manifestMap.TryGetValue(result.NodeId, out var mNode);

            var addressOrVal = result.TraversalAddress != IntPtr.Zero ? $"0x{result.TraversalAddress.ToInt64():X}" :
                               result.ObservedAddress != IntPtr.Zero ? $"0x{result.ObservedAddress.ToInt64():X}" :
                               result.ExtractedValue?.ToString();

            var shortReason = result.ErrorMessage;
            if (string.IsNullOrEmpty(shortReason) && result.Status == ValidationStatus.VALID)
            {
                shortReason = result.ExtractedValue?.ToString() ?? "Structurally verified";
            }

            rows.Add(new OffsetDoctorGuiRow
            {
                NodeId = result.NodeId,
                DisplayName = result.NodeDisplayName,
                Category = result.Category,
                Status = result.Status,
                IsStaticRoot = mNode?.Kind == ValueKind.StaticPattern || mNode?.ParentId == null,
                IsOptionalStateDependent = mNode?.IsOptionalStateDependent ?? false,
                ObservedOrTraversalAddress = addressOrVal,
                ExtractedValue = result.ExtractedValue?.ToString(),
                ShortReason = shortReason,
                EvidenceSummary = result.Evidence.Select(e => $"[{(e.Passed ? "PASS" : "FAIL")}] {e.RuleName}: {e.Description}").ToList()
            });
        }

        // Sort: BROKEN at the very top, then BLOCKED, then UNVERIFIED, then VALID. Within each status group, static roots first.
        rows.Sort((a, b) =>
        {
            int GetStatusRank(ValidationStatus s) => s switch
            {
                ValidationStatus.BROKEN => 0,
                ValidationStatus.BLOCKED => 1,
                ValidationStatus.UNVERIFIED => 2,
                ValidationStatus.VALID => 3,
                _ => 4
            };

            var rankA = GetStatusRank(a.Status);
            var rankB = GetStatusRank(b.Status);
            if (rankA != rankB) return rankA.CompareTo(rankB);

            // Static roots prioritized within the same status
            if (a.IsStaticRoot != b.IsStaticRoot) return b.IsStaticRoot.CompareTo(a.IsStaticRoot);

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        model.Rows = rows;
        model.ValidCount = report.ValidCount;
        model.BrokenCount = report.BrokenCount;
        model.BlockedCount = report.BlockedCount;
        model.UnverifiedCount = report.UnverifiedCount;
    }
}
