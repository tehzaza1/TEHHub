namespace TEHhub.OffsetDoctor.Gui;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TEHhub.OffsetDoctor.Baseline;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Validation;
using TEHhub.OffsetDoctor.Watch;

public sealed class MainForm : Form
{
    private readonly OffsetDoctorGuiModel _model = new();
    private readonly OffsetDoctorGuiController _controller = new();

    // Top Controls
    private Label _lblHeaderTitle = null!;
    private Label _lblReadOnlyNotice = null!;
    private Panel _pnlBanner = null!;
    private Label _lblBanner = null!;

    // Ground Truth Inputs
    private TextBox _txtGold = null!;
    private TextBox _txtHpCur = null!;
    private TextBox _txtHpTot = null!;
    private TextBox _txtMpCur = null!;
    private TextBox _txtMpTot = null!;
    private TextBox _txtEsCur = null!;
    private TextBox _txtEsTot = null!;

    // Baseline & Watch Inputs
    private TextBox _txtBaselinePath = null!;
    private Button _btnBrowseBaseline = null!;
    private TextBox _txtWatchTarget = null!;
    private TextBox _txtWatchDuration = null!;
    private TextBox _txtWatchInterval = null!;

    // Action Buttons
    private Button _btnValidateNow = null!;
    private Button _btnWatchUi = null!;
    private Button _btnCaptureBaseline = null!;
    private Button _btnCompareBaseline = null!;
    private Button _btnClearResults = null!;

    // Status & Metrics
    private Label _lblOperationStatus = null!;
    private Label _lblTotalCount = null!;
    private Label _lblValidCount = null!;
    private Label _lblBrokenCount = null!;
    private Label _lblBlockedCount = null!;
    private Label _lblUnverifiedCount = null!;

    // Filters & Grid
    private ComboBox _cmbCategory = null!;
    private TextBox _txtSearch = null!;
    private DataGridView _gridResults = null!;
    private TextBox _txtEvidenceDetails = null!;
    private ListBox _lstWatchLog = null!;
    private TextBox _txtBaselineReport = null!;
    private TabControl _tabMain = null!;

    public MainForm()
    {
        InitializeComponent();
        SyncInputsToModel();
        RefreshUiFromModel();
    }

    private void InitializeComponent()
    {
        this.Text = "TEHhub Offset Doctor - Diagnostics & Health Scanner";
        this.Size = new Size(1150, 800);
        this.MinimumSize = new Size(950, 620);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        this.BackColor = Color.FromArgb(246, 248, 250);
        this.ShowInTaskbar = true;
        this.TopMost = false;
        this.FormBorderStyle = FormBorderStyle.Sizable;

        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12),
            BackColor = Color.Transparent
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Header & Banner
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Ground Truth & Config
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Actions & Summary
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Filters
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // Tab Content

        // 1. Header & Banner Section
        var pnlHeader = new Panel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        _lblHeaderTitle = new Label
        {
            Text = "TEHhub Offset Doctor - PoE2 Read-Only Offset Health Scanner",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(30, 41, 59),
            AutoSize = true,
            Location = new Point(0, 0)
        };
        _lblReadOnlyNotice = new Label
        {
            Text = "OffsetDoctor operates strictly in READ-ONLY mode. Zero memory writes performed.",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(0, 26)
        };
        _pnlBanner = new Panel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            Margin = new Padding(0, 8, 0, 0),
            Padding = new Padding(8),
            Visible = false,
            BackColor = Color.FromArgb(254, 226, 226)
        };
        _lblBanner = new Label
        {
            Text = string.Empty,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(185, 28, 28),
            AutoSize = true,
            Dock = DockStyle.Fill
        };
        _pnlBanner.Controls.Add(_lblBanner);
        pnlHeader.Controls.Add(_lblHeaderTitle);
        pnlHeader.Controls.Add(_lblReadOnlyNotice);
        pnlHeader.Controls.Add(_pnlBanner);
        mainLayout.Controls.Add(pnlHeader, 0, 0);

        // 2. Ground-Truth & Config GroupBox
        var grpGroundTruth = new GroupBox
        {
            Text = " Ground-Truth Inputs (Optional - Leave empty if not supplied) ",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 8),
            ForeColor = Color.FromArgb(51, 65, 85)
        };

        var gtLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 7,
            RowCount = 2
        };
        gtLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14f)); // Gold
        gtLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18f)); // HP
        gtLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18f)); // Mana
        gtLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18f)); // ES
        gtLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12f)); // Watch Target
        gtLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10f)); // Duration
        gtLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10f)); // Interval

        // Labels row
        gtLayout.Controls.Add(new Label { Text = "Gold Amount:", AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105) }, 0, 0);
        gtLayout.Controls.Add(new Label { Text = "HP Current / Max:", AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105) }, 1, 0);
        gtLayout.Controls.Add(new Label { Text = "Mana Current / Max:", AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105) }, 2, 0);
        gtLayout.Controls.Add(new Label { Text = "ES Current / Max:", AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105) }, 3, 0);
        gtLayout.Controls.Add(new Label { Text = "Watch Target:", AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105) }, 4, 0);
        gtLayout.Controls.Add(new Label { Text = "Duration (s):", AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105) }, 5, 0);
        gtLayout.Controls.Add(new Label { Text = "Interval (ms):", AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105) }, 6, 0);

        // Inputs row
        _txtGold = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2, 2, 8, 4) };

        var pnlHp = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _txtHpCur = new TextBox { Width = 60, Margin = new Padding(2, 2, 2, 4) };
        _txtHpTot = new TextBox { Width = 60, Margin = new Padding(2, 2, 8, 4) };
        pnlHp.Controls.Add(_txtHpCur);
        pnlHp.Controls.Add(new Label { Text = "/", AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
        pnlHp.Controls.Add(_txtHpTot);

        var pnlMp = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _txtMpCur = new TextBox { Width = 60, Margin = new Padding(2, 2, 2, 4) };
        _txtMpTot = new TextBox { Width = 60, Margin = new Padding(2, 2, 8, 4) };
        pnlMp.Controls.Add(_txtMpCur);
        pnlMp.Controls.Add(new Label { Text = "/", AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
        pnlMp.Controls.Add(_txtMpTot);

        var pnlEs = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _txtEsCur = new TextBox { Width = 60, Margin = new Padding(2, 2, 2, 4) };
        _txtEsTot = new TextBox { Width = 60, Margin = new Padding(2, 2, 8, 4) };
        pnlEs.Controls.Add(_txtEsCur);
        pnlEs.Controls.Add(new Label { Text = "/", AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
        pnlEs.Controls.Add(_txtEsTot);

        _txtWatchTarget = new TextBox { Text = "ui", Dock = DockStyle.Fill, Margin = new Padding(2, 2, 8, 4) };
        _txtWatchDuration = new TextBox { Text = "10", Dock = DockStyle.Fill, Margin = new Padding(2, 2, 8, 4) };
        _txtWatchInterval = new TextBox { Text = "250", Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 4) };

        gtLayout.Controls.Add(_txtGold, 0, 1);
        gtLayout.Controls.Add(pnlHp, 1, 1);
        gtLayout.Controls.Add(pnlMp, 2, 1);
        gtLayout.Controls.Add(pnlEs, 3, 1);
        gtLayout.Controls.Add(_txtWatchTarget, 4, 1);
        gtLayout.Controls.Add(_txtWatchDuration, 5, 1);
        gtLayout.Controls.Add(_txtWatchInterval, 6, 1);

        var pnlBaselineRow = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        var lblBaseline = new Label { Text = "Baseline File Path:", AutoSize = true, Location = new Point(0, 6), ForeColor = Color.FromArgb(71, 85, 105) };
        _txtBaselinePath = new TextBox { Text = _model.BaselinePathInput, Location = new Point(115, 3), Width = 550, Anchor = AnchorStyles.Left | AnchorStyles.Top };
        _btnBrowseBaseline = new Button { Text = "Browse...", Location = new Point(672, 2), Width = 80, Height = 25 };
        _btnBrowseBaseline.Click += BtnBrowseBaseline_Click;
        pnlBaselineRow.Controls.Add(lblBaseline);
        pnlBaselineRow.Controls.Add(_txtBaselinePath);
        pnlBaselineRow.Controls.Add(_btnBrowseBaseline);

        grpGroundTruth.Controls.Add(gtLayout);
        grpGroundTruth.Controls.Add(pnlBaselineRow);
        mainLayout.Controls.Add(grpGroundTruth, 0, 1);

        // 3. Action Buttons & Summary Bar
        var pnlActionsAndSummary = new Panel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };

        var flpButtons = new FlowLayoutPanel { AutoSize = true, Location = new Point(0, 0), WrapContents = false };
        _btnValidateNow = new Button
        {
            Text = "Validate Now",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Height = 32,
            Width = 115,
            Margin = new Padding(0, 0, 6, 0)
        };
        _btnValidateNow.FlatAppearance.BorderSize = 0;
        _btnValidateNow.Click += (s, e) => ExecuteValidateNow();

        _btnWatchUi = new Button
        {
            Text = "Watch UI",
            Height = 32,
            Width = 95,
            Margin = new Padding(0, 0, 6, 0)
        };
        _btnWatchUi.Click += (s, e) => ExecuteWatchUi();

        _btnCaptureBaseline = new Button
        {
            Text = "Capture Baseline",
            Height = 32,
            Width = 125,
            Margin = new Padding(0, 0, 6, 0)
        };
        _btnCaptureBaseline.Click += (s, e) => ExecuteCaptureBaseline();

        _btnCompareBaseline = new Button
        {
            Text = "Compare Baseline",
            Height = 32,
            Width = 135,
            Margin = new Padding(0, 0, 6, 0)
        };
        _btnCompareBaseline.Click += (s, e) => ExecuteCompareBaseline();

        _btnClearResults = new Button
        {
            Text = "Clear Results",
            Height = 32,
            Width = 105,
            Margin = new Padding(0, 0, 12, 0)
        };
        _btnClearResults.Click += (s, e) => ExecuteClearResults();

        _lblOperationStatus = new Label
        {
            Text = "Status: Ready",
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Italic),
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 8, 0, 0)
        };

        flpButtons.Controls.Add(_btnValidateNow);
        flpButtons.Controls.Add(_btnWatchUi);
        flpButtons.Controls.Add(_btnCaptureBaseline);
        flpButtons.Controls.Add(_btnCompareBaseline);
        flpButtons.Controls.Add(_btnClearResults);
        flpButtons.Controls.Add(_lblOperationStatus);

        var flpSummary = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, WrapContents = false };
        _lblTotalCount = new Label { Text = "Total: 0", AutoSize = true, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(6, 6, 6, 0) };
        _lblValidCount = new Label { Text = "VALID: 0", AutoSize = true, ForeColor = Color.FromArgb(22, 101, 52), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(6, 6, 6, 0) };
        _lblBrokenCount = new Label { Text = "BROKEN: 0", AutoSize = true, ForeColor = Color.FromArgb(185, 28, 28), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(6, 6, 6, 0) };
        _lblBlockedCount = new Label { Text = "BLOCKED: 0", AutoSize = true, ForeColor = Color.FromArgb(180, 83, 9), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(6, 6, 6, 0) };
        _lblUnverifiedCount = new Label { Text = "UNVERIFIED: 0", AutoSize = true, ForeColor = Color.FromArgb(30, 64, 175), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(6, 6, 6, 0) };

        flpSummary.Controls.Add(_lblTotalCount);
        flpSummary.Controls.Add(_lblValidCount);
        flpSummary.Controls.Add(_lblBrokenCount);
        flpSummary.Controls.Add(_lblBlockedCount);
        flpSummary.Controls.Add(_lblUnverifiedCount);

        pnlActionsAndSummary.Controls.Add(flpButtons);
        pnlActionsAndSummary.Controls.Add(flpSummary);
        mainLayout.Controls.Add(pnlActionsAndSummary, 0, 2);

        // 4. Filters Bar
        var pnlFilters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 6), WrapContents = false };
        pnlFilters.Controls.Add(new Label { Text = "Filter Category:", AutoSize = true, Margin = new Padding(0, 4, 4, 0) });
        _cmbCategory = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Margin = new Padding(0, 0, 16, 0) };
        _cmbCategory.Items.AddRange(new object[] { "All", "Static Roots", "Game States", "Area / Server Data", "ServerData & Inventory", "Player & Components", "UI Elements", "Area Loading State" });
        _cmbCategory.SelectedIndex = 0;
        _cmbCategory.SelectedIndexChanged += (s, e) => PopulateGridFromModel();

        pnlFilters.Controls.Add(_cmbCategory);
        pnlFilters.Controls.Add(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(0, 4, 4, 0) });
        _txtSearch = new TextBox { Width = 220, Margin = new Padding(0, 0, 0, 0) };
        _txtSearch.TextChanged += (s, e) => PopulateGridFromModel();
        pnlFilters.Controls.Add(_txtSearch);
        mainLayout.Controls.Add(pnlFilters, 0, 3);

        // 5. Main Tab Content
        _tabMain = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0) };

        // Tab 1: Results DataGridView & Details
        var tabResults = new TabPage { Text = "Offset Health Results" };
        var splitResults = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 320
        };

        _gridResults = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.Fixed3D,
            RowHeadersVisible = false,
            AutoGenerateColumns = false
        };

        _gridResults.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", Width = 95 });
        _gridResults.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = "Category", Width = 150 });
        _gridResults.Columns.Add(new DataGridViewTextBoxColumn { Name = "DisplayName", HeaderText = "Node Name", Width = 210 });
        _gridResults.Columns.Add(new DataGridViewTextBoxColumn { Name = "AddressOrVal", HeaderText = "Observed / Traversal", Width = 160 });
        _gridResults.Columns.Add(new DataGridViewTextBoxColumn { Name = "ShortReason", HeaderText = "Reason / Value / Error", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _gridResults.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Optional", HeaderText = "Optional", Width = 65 });

        _gridResults.SelectionChanged += GridResults_SelectionChanged;

        var pnlDetails = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var lblDetailsHeader = new Label { Text = "Evidence & Validation Rules Summary:", Dock = DockStyle.Top, AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        _txtEvidenceDetails = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(248, 250, 252),
            Font = new Font("Consolas", 9F)
        };
        pnlDetails.Controls.Add(_txtEvidenceDetails);
        pnlDetails.Controls.Add(lblDetailsHeader);

        splitResults.Panel1.Controls.Add(_gridResults);
        splitResults.Panel2.Controls.Add(pnlDetails);
        tabResults.Controls.Add(splitResults);

        // Tab 2: Watch UI Log
        var tabWatchLog = new TabPage { Text = "Watch Log / Transitions" };
        _lstWatchLog = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5F),
            BackColor = Color.FromArgb(248, 250, 252)
        };
        tabWatchLog.Controls.Add(_lstWatchLog);

        // Tab 3: Baseline Comparison Report
        var tabBaseline = new TabPage { Text = "Baseline Comparison Report" };
        _txtBaselineReport = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.5F),
            BackColor = Color.FromArgb(248, 250, 252)
        };
        tabBaseline.Controls.Add(_txtBaselineReport);

        _tabMain.TabPages.Add(tabResults);
        _tabMain.TabPages.Add(tabWatchLog);
        _tabMain.TabPages.Add(tabBaseline);

        mainLayout.Controls.Add(_tabMain, 0, 4);
        this.Controls.Add(mainLayout);
    }

    private void SyncInputsToModel()
    {
        _model.GoldInput = _txtGold.Text.Trim();
        _model.HpCurrentInput = _txtHpCur.Text.Trim();
        _model.HpTotalInput = _txtHpTot.Text.Trim();
        _model.MpCurrentInput = _txtMpCur.Text.Trim();
        _model.MpTotalInput = _txtMpTot.Text.Trim();
        _model.EsCurrentInput = _txtEsCur.Text.Trim();
        _model.EsTotalInput = _txtEsTot.Text.Trim();
        _model.BaselinePathInput = _txtBaselinePath.Text.Trim();
        _model.WatchTargetInput = _txtWatchTarget.Text.Trim();
        _model.WatchDurationSecInput = _txtWatchDuration.Text.Trim();
        _model.WatchIntervalMsInput = _txtWatchInterval.Text.Trim();
    }

    private void RefreshUiFromModel()
    {
        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action(RefreshUiFromModel));
            return;
        }

        _lblTotalCount.Text = $"Total: {_model.TotalCount}";
        _lblValidCount.Text = $"VALID: {_model.ValidCount}";
        _lblBrokenCount.Text = $"BROKEN: {_model.BrokenCount}";
        _lblBlockedCount.Text = $"BLOCKED: {_model.BlockedCount}";
        _lblUnverifiedCount.Text = $"UNVERIFIED: {_model.UnverifiedCount}";

        _lblOperationStatus.Text = _model.IsBusy ? $"Status: {_model.OperationStatus}" :
                                   _model.LastRunTimestampUtc != null ? $"Last run: {_model.LastRunTimestampUtc.Value:HH:mm:ss} UTC" : "Status: Ready";

        // Banner display
        if (!string.IsNullOrEmpty(_model.ErrorMessage))
        {
            _pnlBanner.Visible = true;
            _pnlBanner.BackColor = Color.FromArgb(254, 226, 226);
            _lblBanner.ForeColor = Color.FromArgb(185, 28, 28);
            _lblBanner.Text = $"[ERROR] {_model.ErrorMessage}";
        }
        else if (_model.BrokenCount > 0)
        {
            _pnlBanner.Visible = true;
            _pnlBanner.BackColor = Color.FromArgb(254, 226, 226);
            _lblBanner.ForeColor = Color.FromArgb(185, 28, 28);
            _lblBanner.Text = $"[CRITICAL] {_model.BrokenCount} broken offset(s) detected! Inspect highlighted rows below.";
        }
        else if (!string.IsNullOrEmpty(_model.NoticeMessage))
        {
            _pnlBanner.Visible = true;
            _pnlBanner.BackColor = Color.FromArgb(240, 253, 244);
            _lblBanner.ForeColor = Color.FromArgb(22, 101, 52);
            _lblBanner.Text = $"[NOTICE] {_model.NoticeMessage}";
        }
        else
        {
            _pnlBanner.Visible = false;
        }

        // Watch log tab update
        _lstWatchLog.Items.Clear();
        lock (_model.WatchLog)
        {
            foreach (var log in _model.WatchLog)
            {
                _lstWatchLog.Items.Add(log);
            }
        }
        if (_model.LastWatchReport != null)
        {
            _lstWatchLog.Items.Add("==========================================================");
            _lstWatchLog.Items.Add($"Watch Session Finished at {_model.LastWatchReport.EndTimeUtc:HH:mm:ss} UTC");
            _lstWatchLog.Items.Add($"Total Targets: {_model.LastWatchReport.TargetSummaries.Count}, Transitions: {_model.LastWatchReport.TransitionLog.Count}");
            foreach (var summary in _model.LastWatchReport.TargetSummaries)
            {
                _lstWatchLog.Items.Add($"* {summary.NodeId,-30} Best: {summary.BestStatus,-10} Latest: {summary.LatestStatus,-10} Action Observed: {summary.PlayerActionObserved}");
            }
        }

        // Baseline comparison report update
        if (_model.ComparisonResult != null)
        {
            var comp = _model.ComparisonResult;
            var sw = new StringWriter();
            sw.WriteLine("================================================================================");
            sw.WriteLine("  TEHHub Offset Doctor - Baseline Comparison Report");
            sw.WriteLine("================================================================================");
            sw.WriteLine($"Baseline Process: {comp.Baseline.ProcessName} (Base: {comp.Baseline.ModuleBase})");
            sw.WriteLine($"Current Runtime:  {comp.CurrentReport.ProcessMetadata.ProcessName} (Base: 0x{comp.CurrentReport.ProcessMetadata.ModuleBase.ToInt64():X})");
            sw.WriteLine($"Deltas Summary:   {comp.CriticalCount} Critical, {comp.WarningCount} Warnings, {comp.InformationalCount} Informational");
            sw.WriteLine("--------------------------------------------------------------------------------");
            foreach (var delta in comp.Deltas)
            {
                sw.WriteLine($"[{delta.Severity}] {delta.DisplayName} ({delta.Category}): {delta.Description}");
            }
            if (!comp.HasCriticalRegressions)
            {
                sw.WriteLine("--------------------------------------------------------------------------------");
                sw.WriteLine("No critical baseline regressions detected.");
            }
            _txtBaselineReport.Text = sw.ToString();
        }
        else
        {
            _txtBaselineReport.Text = "No baseline comparison has been performed yet.";
        }

        PopulateGridFromModel();
    }

    private void PopulateGridFromModel()
    {
        _gridResults.Rows.Clear();
        var selectedCat = _cmbCategory.SelectedItem?.ToString() ?? "All";
        var search = _txtSearch.Text.Trim();

        var filtered = _model.Rows.AsEnumerable();

        if (selectedCat == "Static Roots")
        {
            filtered = filtered.Where(r => r.IsStaticRoot);
        }
        else if (selectedCat != "All")
        {
            filtered = filtered.Where(r => r.Category.Equals(selectedCat, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(r => r.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                           r.NodeId.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var row in filtered)
        {
            int index = _gridResults.Rows.Add(
                row.Status.ToString(),
                row.Category,
                row.DisplayName,
                row.ObservedOrTraversalAddress ?? "-",
                row.ShortReason ?? string.Empty,
                row.IsOptionalStateDependent);

            var gridRow = _gridResults.Rows[index];
            gridRow.Tag = row;

            // Highlight status cell
            var statusCell = gridRow.Cells["Status"];
            switch (row.Status)
            {
                case ValidationStatus.VALID:
                    statusCell.Style.ForeColor = Color.FromArgb(22, 101, 52);
                    statusCell.Style.Font = new Font(_gridResults.Font, FontStyle.Bold);
                    break;
                case ValidationStatus.BROKEN:
                    statusCell.Style.ForeColor = Color.FromArgb(185, 28, 28);
                    statusCell.Style.BackColor = Color.FromArgb(254, 226, 226);
                    statusCell.Style.Font = new Font(_gridResults.Font, FontStyle.Bold);
                    break;
                case ValidationStatus.BLOCKED:
                    statusCell.Style.ForeColor = Color.FromArgb(180, 83, 9);
                    statusCell.Style.BackColor = Color.FromArgb(254, 243, 199);
                    statusCell.Style.Font = new Font(_gridResults.Font, FontStyle.Bold);
                    break;
                case ValidationStatus.UNVERIFIED:
                    statusCell.Style.ForeColor = Color.FromArgb(30, 64, 175);
                    break;
            }
        }

        if (_gridResults.Rows.Count > 0)
        {
            _gridResults.Rows[0].Selected = true;
            UpdateEvidenceDetails(_gridResults.Rows[0].Tag as OffsetDoctorGuiRow);
        }
        else
        {
            _txtEvidenceDetails.Text = string.Empty;
        }
    }

    private void GridResults_SelectionChanged(object? sender, EventArgs e)
    {
        if (_gridResults.SelectedRows.Count > 0)
        {
            UpdateEvidenceDetails(_gridResults.SelectedRows[0].Tag as OffsetDoctorGuiRow);
        }
    }

    private void UpdateEvidenceDetails(OffsetDoctorGuiRow? row)
    {
        if (row == null)
        {
            _txtEvidenceDetails.Text = string.Empty;
            return;
        }

        var sw = new StringWriter();
        sw.WriteLine($"Node ID:       {row.NodeId}");
        sw.WriteLine($"Display Name:  {row.DisplayName}");
        sw.WriteLine($"Category:      {row.Category}");
        sw.WriteLine($"Status:        {row.Status}");
        sw.WriteLine($"Address/Value: {row.ObservedOrTraversalAddress ?? "-"}");
        sw.WriteLine($"Reason:        {row.ShortReason ?? "-"}");
        sw.WriteLine("--------------------------------------------------------------------------------");
        sw.WriteLine("Evidence Checklist:");
        if (row.EvidenceSummary.Count == 0)
        {
            sw.WriteLine("  (No direct rule evidence recorded for this node)");
        }
        else
        {
            foreach (var ev in row.EvidenceSummary)
            {
                sw.WriteLine($"  {ev}");
            }
        }
        _txtEvidenceDetails.Text = sw.ToString();
    }

    private void SetButtonsEnabled(bool enabled)
    {
        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action(() => SetButtonsEnabled(enabled)));
            return;
        }

        _btnValidateNow.Enabled = enabled;
        _btnWatchUi.Enabled = enabled;
        _btnCaptureBaseline.Enabled = enabled;
        _btnCompareBaseline.Enabled = enabled;
        _btnClearResults.Enabled = enabled;
    }

    private void ExecuteAsync(string operationName, Action<IProcessMemoryReader> action)
    {
        SyncInputsToModel();

        // 1. Verify ground-truth inputs before acquiring background gate
        if (!_controller.TryBuildGroundTruth(_model, out _, out var gtErr))
        {
            _model.ErrorMessage = gtErr;
            RefreshUiFromModel();
            return;
        }

        // 2. Synchronously acquire gate on UI thread
        if (!_controller.TryBeginOperation(_model, operationName))
        {
            RefreshUiFromModel();
            return;
        }

        SetButtonsEnabled(false);
        RefreshUiFromModel();

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
                _model.ErrorMessage = $"Operation failed: {ex.Message}";
            }
            finally
            {
                _controller.EndOperation(_model);
                this.BeginInvoke(new Action(() =>
                {
                    SetButtonsEnabled(true);
                    RefreshUiFromModel();
                }));
            }
        });
    }

    private void ExecuteValidateNow()
    {
        _tabMain.SelectedIndex = 0; // Show results tab
        ExecuteAsync("Validating memory offsets...", reader =>
        {
            _controller.ValidateNow(reader, _model);
        });
    }

    private void ExecuteWatchUi()
    {
        _tabMain.SelectedIndex = 1; // Show watch log tab
        ExecuteAsync("Watching UI targets...", reader =>
        {
            int dur = int.TryParse(_model.WatchDurationSecInput, out var d) && d > 0 ? d : 10;
            int interval = int.TryParse(_model.WatchIntervalMsInput, out var iv) && iv > 0 ? iv : 250;
            string target = !string.IsNullOrWhiteSpace(_model.WatchTargetInput) ? _model.WatchTargetInput.Trim() : "ui";

            _controller.WatchUi(reader, _model, durationSec: dur, intervalMs: interval, targetFilter: target);
        });
    }

    private void ExecuteCaptureBaseline()
    {
        ExecuteAsync("Capturing baseline snapshot...", reader =>
        {
            _controller.CaptureBaseline(reader, _model, _model.BaselinePathInput);
        });
    }

    private void ExecuteCompareBaseline()
    {
        _tabMain.SelectedIndex = 2; // Show baseline comparison tab
        ExecuteAsync("Comparing memory against baseline...", reader =>
        {
            _controller.CompareBaseline(reader, _model, _model.BaselinePathInput);
        });
    }

    private void ExecuteClearResults()
    {
        if (_model.IsBusy) return;
        _controller.ClearResults(_model);
        RefreshUiFromModel();
    }

    private void BtnBrowseBaseline_Click(object? sender, EventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Select OffsetDoctor Baseline File",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            CheckFileExists = false
        };

        if (ofd.ShowDialog(this) == DialogResult.OK)
        {
            _txtBaselinePath.Text = ofd.FileName;
            _model.BaselinePathInput = ofd.FileName;
        }
    }
}
