namespace TEHhub.OffsetDoctor.Gui;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using ClickableTransparentOverlay;
using ImGuiNET;
using TEHhub.OffsetDoctor.Baseline;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;

public sealed class OffsetDoctorApp : Overlay
{
    private static readonly Vector4 ColorGreen = new(0.35f, 0.90f, 0.35f, 1.0f);
    private static readonly Vector4 ColorRed = new(1.00f, 0.35f, 0.35f, 1.0f);
    private static readonly Vector4 ColorYellow = new(1.00f, 0.85f, 0.30f, 1.0f);
    private static readonly Vector4 ColorBlue = new(0.40f, 0.75f, 1.00f, 1.0f);
    private static readonly Vector4 ColorMuted = new(0.65f, 0.65f, 0.65f, 1.0f);

    private readonly OffsetDoctorGuiModel _model = new();
    private readonly OffsetDoctorGuiController _controller = new();

    private string _selectedCategoryFilter = "All";
    private string _searchFilter = string.Empty;
    private bool _showWatchLog = true;

    public OffsetDoctorApp() : base("TEHhub OffsetDoctor GUI V1", false, 960, 780)
    {
    }

    protected override void Render()
    {
        ImGui.SetNextWindowPos(new Vector2(80, 80), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(1100, 750), ImGuiCond.FirstUseEver);

        var windowFlags = ImGuiWindowFlags.MenuBar;

        if (ImGui.Begin("TEHhub OffsetDoctor###OffsetDoctorMainWindow", windowFlags))
        {
            DrawMenuBar();
            DrawHeaderNotice();
            DrawGroundTruthSection();
            DrawWatchSection();
            DrawActionControls();
            DrawStatusSummary();
            DrawMessages();
            DrawResultsTable();

            ImGui.End();
        }
    }

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar())
        {
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
        ImGui.Text("TEHhub OffsetDoctor External GUI V1");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextColored(ColorGreen, "READ-ONLY");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextColored(ColorBlue, "PoE2 Diagnostics");

        ImGui.EndMenuBar();
    }

    private static void DrawHeaderNotice()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
        ImGui.TextWrapped("OffsetDoctor performs strict read-only diagnostic checks against PoE2 memory structures. Zero memory writes are performed. No recovery, Ghidra, or offset searching.");
        ImGui.PopStyleColor();
        ImGui.Separator();
    }

    private void DrawGroundTruthSection()
    {
        if (ImGui.CollapsingHeader("Ground-Truth Inputs (Optional)###ODGroundTruth", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
            ImGui.TextUnformatted("Leave inputs empty if not supplied. Only stable max values are used. No hardcoded default player values.");
            ImGui.PopStyleColor();

            ImGui.Columns(4, "ODGroundTruthCols", false);

            // Column 1: Gold
            ImGui.Text("Gold:");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ODGold", ref _model.GoldInput, 32);

            ImGui.NextColumn();

            // Column 2: Max HP
            ImGui.Text("Max HP:");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ODMaxHp", ref _model.HpTotalInput, 16);

            ImGui.NextColumn();

            // Column 3: Max Mana
            ImGui.Text("Max Mana:");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ODMaxMp", ref _model.MpTotalInput, 16);

            ImGui.NextColumn();

            // Column 4: Max ES
            ImGui.Text("Max ES:");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ODMaxEs", ref _model.EsTotalInput, 16);

            ImGui.Columns(1);
            ImGui.Spacing();
        }
    }

    private void DrawWatchSection()
    {
        if (ImGui.CollapsingHeader("Watch Mode Configuration (Optional)###ODWatchConfig"))
        {
            ImGui.Columns(3, "ODWatchCols", false);

            ImGui.Text("Duration (Seconds):");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ODWatchDur", ref _model.WatchDurationSecInput, 8);

            ImGui.NextColumn();

            ImGui.Text("Interval (ms):");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ODWatchInterval", ref _model.WatchIntervalMsInput, 8);

            ImGui.NextColumn();

            ImGui.Text("Target Filter (e.g. ui / all):");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ODWatchTarget", ref _model.WatchTargetInput, 32);

            ImGui.Columns(1);

            if (_model.WatchLog.Count > 0)
            {
                ImGui.Spacing();
                ImGui.Checkbox("Show Transition Log", ref _showWatchLog);
                if (_showWatchLog)
                {
                    ImGui.BeginChild("ODWatchLogBox", new Vector2(0, 100), ImGuiChildFlags.Borders);
                    lock (_model.WatchLog)
                    {
                        foreach (var line in _model.WatchLog)
                        {
                            ImGui.TextUnformatted(line);
                        }
                    }
                    ImGui.EndChild();
                }
            }

            ImGui.Spacing();
        }
    }

    private void DrawActionControls()
    {
        ImGui.Text("Baseline File:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);
        ImGui.InputText("##ODBaselinePath", ref _model.BaselinePathInput, 260);

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();

        bool isBusy = _model.IsBusy;
        if (isBusy)
        {
            ImGui.BeginDisabled();
        }

        // Button 1: Validate Now
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.45f, 0.20f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.58f, 0.26f, 1.0f));
        if (ImGui.Button("Validate Now##ODValidateBtn", new Vector2(100, 0)))
        {
            ExecuteAsync("Validating memory offsets...", reader => _controller.ValidateNow(reader, _model));
        }
        ImGui.PopStyleColor(2);

        ImGui.SameLine();

        // Button 2: Watch UI
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.32f, 0.55f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.42f, 0.70f, 1.0f));
        if (ImGui.Button("Watch UI##ODWatchBtn", new Vector2(90, 0)))
        {
            ExecuteAsync("Watching UI targets...", reader => _controller.WatchUi(reader, _model));
        }
        ImGui.PopStyleColor(2);

        ImGui.SameLine();

        // Button 3: Capture Baseline
        if (ImGui.Button("Capture Baseline##ODCaptureBtn", new Vector2(120, 0)))
        {
            ExecuteAsync("Capturing baseline snapshot...", reader => _controller.CaptureBaseline(reader, _model));
        }

        ImGui.SameLine();

        // Button 4: Compare Baseline
        if (ImGui.Button("Compare Baseline##ODCompareBtn", new Vector2(120, 0)))
        {
            ExecuteAsync("Comparing memory against baseline...", reader => _controller.CompareBaseline(reader, _model));
        }

        ImGui.SameLine();

        // Button 5: Clear Results
        if (ImGui.Button("Clear Results##ODClearBtn", new Vector2(95, 0)))
        {
            _controller.ClearResults(_model);
        }

        if (isBusy)
        {
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextColored(ColorYellow, $"[{_model.OperationStatus}]");
        }
        else if (_model.LastRunTimestampUtc != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Last run: {_model.LastRunTimestampUtc.Value:HH:mm:ss} UTC");
        }

        ImGui.Spacing();
        ImGui.Separator();
    }

    private void DrawStatusSummary()
    {
        ImGui.Columns(5, "ODSummaryCols", false);

        ImGui.Text("Summary:");
        ImGui.NextColumn();

        ImGui.TextColored(_model.ValidCount > 0 ? ColorGreen : ColorMuted, $"VALID: {_model.ValidCount}");
        ImGui.NextColumn();

        ImGui.TextColored(_model.BrokenCount > 0 ? ColorRed : ColorMuted, $"BROKEN: {_model.BrokenCount}");
        ImGui.NextColumn();

        ImGui.TextColored(_model.BlockedCount > 0 ? ColorYellow : ColorMuted, $"BLOCKED: {_model.BlockedCount}");
        ImGui.NextColumn();

        ImGui.TextColored(ColorBlue, $"UNVERIFIED: {_model.UnverifiedCount}");
        ImGui.Columns(1);

        ImGui.Separator();
    }

    private void DrawMessages()
    {
        if (!string.IsNullOrEmpty(_model.ErrorMessage))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorRed);
            ImGui.TextWrapped($"[ERROR] {_model.ErrorMessage}");
            ImGui.PopStyleColor();
        }

        if (!string.IsNullOrEmpty(_model.NoticeMessage))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorGreen);
            ImGui.TextWrapped($"[NOTICE] {_model.NoticeMessage}");
            ImGui.PopStyleColor();
        }

        if (_model.ComparisonResult != null)
        {
            var comp = _model.ComparisonResult;
            ImGui.TextColored(
                comp.HasCriticalRegressions ? ColorRed : (comp.WarningCount > 0 ? ColorYellow : ColorGreen),
                $"Baseline Deltas: {comp.CriticalCount} Critical, {comp.WarningCount} Warnings, {comp.InformationalCount} Informational");
        }

        if (!string.IsNullOrEmpty(_model.ErrorMessage) || !string.IsNullOrEmpty(_model.NoticeMessage) || _model.ComparisonResult != null)
        {
            ImGui.Separator();
        }
    }

    private void DrawResultsTable()
    {
        // Filter bar
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("Category##ODCategoryFilter", _selectedCategoryFilter))
        {
            var categories = new[] { "All", "Static Roots", "Game States", "Area / Server Data", "ServerData & Inventory", "Player & Components", "UI Elements", "Area Loading State" };
            foreach (var cat in categories)
            {
                bool isSelected = _selectedCategoryFilter == cat;
                if (ImGui.Selectable(cat, isSelected))
                {
                    _selectedCategoryFilter = cat;
                }
                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##ODSearchFilter", "Search node name...", ref _searchFilter, 64);

        ImGui.Spacing();

        if (!_model.HasResults)
        {
            ImGui.TextDisabled("No validation results yet. Click 'Validate Now', 'Watch UI', or 'Compare Baseline' to scan game memory.");
            return;
        }

        var availHeight = ImGui.GetContentRegionAvail().Y;
        var tableHeight = Math.Max(200, availHeight - 10);
        if (ImGui.BeginTable("ODResultsTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupColumn("Node Display Name", ImGuiTableColumnFlags.WidthStretch, 0.32f);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthStretch, 0.18f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Address / Val", ImGuiTableColumnFlags.WidthStretch, 0.18f);
            ImGui.TableSetupColumn("Evidence / Reason", ImGuiTableColumnFlags.WidthStretch, 0.32f);
            ImGui.TableHeadersRow();

            foreach (var row in _model.Rows)
            {
                if (_selectedCategoryFilter != "All" && row.Category != _selectedCategoryFilter)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(_searchFilter) &&
                    !row.DisplayName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) &&
                    !row.Category.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ImGui.TableNextRow();

                // Col 1: Display Name + markers
                ImGui.TableNextColumn();
                if (row.IsStaticRoot)
                {
                    ImGui.TextColored(ColorYellow, "[Root] ");
                    ImGui.SameLine();
                }
                if (row.IsOptionalStateDependent)
                {
                    ImGui.TextColored(ColorMuted, "[Opt] ");
                    ImGui.SameLine();
                }
                ImGui.TextUnformatted(row.DisplayName);

                // Col 2: Category
                ImGui.TableNextColumn();
                ImGui.TextDisabled(row.Category);

                // Col 3: Status
                ImGui.TableNextColumn();
                var (statusColor, statusText) = row.Status switch
                {
                    ValidationStatus.VALID => (ColorGreen, "VALID"),
                    ValidationStatus.BROKEN => (ColorRed, "BROKEN"),
                    ValidationStatus.BLOCKED => (ColorYellow, "BLOCKED"),
                    ValidationStatus.UNVERIFIED => (ColorBlue, "UNVERIFIED"),
                    _ => (ColorMuted, "UNKNOWN")
                };
                ImGui.TextColored(statusColor, statusText);

                // Col 4: Observed / Traversal Address
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.ObservedOrTraversalAddress ?? "-");

                // Col 5: Reason / Evidence
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.ShortReason ?? string.Empty);
                if (row.EvidenceSummary.Count > 0 && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"Evidence for {row.DisplayName}:");
                    ImGui.Separator();
                    foreach (var ev in row.EvidenceSummary)
                    {
                        ImGui.TextUnformatted(ev);
                    }
                    ImGui.EndTooltip();
                }
            }

            ImGui.EndTable();
        }
    }

    private void ExecuteAsync(string operationName, Action<IProcessMemoryReader> action)
    {
        // Synchronously acquire operation lock and set IsBusy on the UI thread before Task.Run starts
        if (!_controller.TryBeginOperation(_model, operationName))
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var discovery = ProcessDiscovery.DiscoverTargetProcess();
                if (discovery.Status != ProcessDiscoveryStatus.Success || discovery.SelectedProcess == null)
                {
                    _model.ErrorMessage = discovery.ErrorMessage ?? "Could not find running Path of Exile process.";
                    return;
                }

                using var reader = new WindowsProcessMemoryReader(discovery.SelectedProcess.Id);
                action(reader);
            }
            catch (Exception ex)
            {
                _model.ErrorMessage = $"Process memory access failed: {ex.Message}";
            }
            finally
            {
                _controller.EndOperation(_model);
            }
        });
    }
}
