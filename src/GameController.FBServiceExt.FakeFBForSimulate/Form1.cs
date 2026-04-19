using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace GameController.FBServiceExt.FakeFBForSimulate;

internal sealed class Form1 : Form
{
        private static readonly IReadOnlyDictionary<string, string> MetricHints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Running"] = "Whether the simulator is currently running.",
        ["StartedAtUtc"] = "UTC time when the current run started.",
        ["OutboundTransportMode"] = "How outbound worker messages reach the simulator.",
        ["ConfiguredUsers"] = "Number of fake users configured for the run.",
        ["ConfiguredWorkers"] = "Number of managed worker processes requested for the run.",
        ["ManagedWorkersRunning"] = "Managed worker processes that are still running.",
        ["DetectedWorkerInstances"] = "Worker instances reported by backend metrics.",
        ["UnmanagedWorkerInstances"] = "Worker instances that are running but are not managed by this simulator run.",
        ["ActiveUsers"] = "Fake users currently inside a voting cycle.",
        ["CyclesStarted"] = "Voting cycles started by fake users.",
        ["CyclesCompleted"] = "Voting cycles that reached an accepted vote response.",
        ["CyclesFailed"] = "Voting cycles that ended in timeout or non-success outcome.",
        ["WebhookAttempts"] = "Inbound webhook requests sent to the FBServiceExt API.",
        ["WebhookSuccesses"] = "Webhook requests accepted successfully by the API.",
        ["WebhookFailures"] = "Webhook requests that failed at transport or HTTP level.",
        ["OutboundMessagesReceived"] = "Outbound payloads received back from workers.",
        ["CarouselsReceived"] = "Candidate carousel payloads recognized by the simulator.",
        ["ConfirmationsReceived"] = "Confirmation challenge payloads recognized by the simulator.",
        ["AcceptedTextsReceived"] = "Accepted vote texts received at the expected final stage.",
        ["CooldownTextsReceived"] = "Cooldown texts received from the worker flow.",
        ["RejectedTextsReceived"] = "Rejected confirmation texts received from the worker flow.",
        ["ExpiredTextsReceived"] = "Expired confirmation texts received from the worker flow.",
        ["InactiveVotingTextsReceived"] = "Inactive voting texts received from the worker flow.",
        ["OtherTextsReceived"] = "Text messages that did not match any known simulator classification.",
        ["CarouselTimeouts"] = "Cycles that timed out while waiting for the initial candidate carousel.",
        ["ConfirmationTimeouts"] = "Cycles that timed out while waiting for the confirmation challenge.",
        ["FinalTextTimeouts"] = "Cycles that timed out while waiting for the final text response.",
        ["CarouselShapeFailures"] = "Carousel payloads that arrived but did not contain a usable vote button.",
        ["ConfirmationShapeFailures"] = "Confirmation payloads that arrived but did not contain a usable accept button.",
        ["StageUnexpectedTexts"] = "Unexpected text messages received while waiting for a non-text stage payload.",
        ["LateAcceptedTexts"] = "Accepted texts that arrived at the wrong stage and were treated as late/out-of-order.",
        ["UnexpectedOutboundShapes"] = "Legacy aggregate retained for compatibility. Prefer CarouselTimeouts, ConfirmationTimeouts, FinalTextTimeouts, and LateAcceptedTexts for diagnosis.",
        ["AverageCompletedCycleMs"] = "Average duration of completed voting cycles in milliseconds."
    };

    private static readonly IReadOnlyDictionary<int, string> WorkerColumnHints = new Dictionary<int, string>
    {
        [0] = "???????? ?? row ????????? simulator-?? ???? ???????? worker-? ???????? ?? ???? unmanaged ???????.",
        [1] = "worker ???????? PID.",
        [2] = "worker instance-?? ???????? ??????????????.",
        [3] = "??????? candidate carousel/option ???????? ?? worker-??.",
        [4] = "??????? vote ???????? accepted ???????.",
        [5] = "??????? event ???????? business ????? ????.",
        [6] = "??????? raw envelope ????? worker-??.",
        [7] = "??????? normalized event ???? worker-??.",
        [8] = "?? worker-?? normalized processing latency-?? p95.",
        [9] = "?? worker-?? outbound HTTP latency-?? p95.",
        [10] = "????? ???????? ????? ?? snapshot."
    };

    private readonly SimulatorDefaults _defaults;
    private readonly FakeFacebookSimulatorEngine _engine;
    private readonly SimulatorStateResetService _resetService;
    private readonly ManagedWorkerProcessManager _managedWorkerManager;
    private readonly DevMetricsSnapshotClient _backendMetricsClient;
    private readonly SimulatorApiPreflightClient _preflightClient;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly Dictionary<string, ListViewItem> _metricItems = new(StringComparer.Ordinal);
    private readonly ToolTip _toolTip = new();
    private readonly ToolTip _headerToolTip = new();
    private readonly Font _toolTipFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly ConcurrentQueue<string> _pendingLogLines = new();

    private const int LogFlushBatchSize = 250;
    private const int MaxVisibleLogLines = 2000;
    private const int MaxPendingLogLines = 5000;

    private int _pendingLogLineCount;
    private int _suppressedUiLogLines;

    private TableLayoutPanel _settingsTable = null!;
    private TextBox _webhookUrlTextBox = null!;
    private TextBox _listenerUrlTextBox = null!;
    private TextBox _pageIdTextBox = null!;
    private TextBox _appSecretTextBox = null!;
    private TextBox _startTokenTextBox = null!;
    private TextBox _activeShowIdTextBox = null!;
    private NumericUpDown _userCountInput = null!;
    private NumericUpDown _durationSecondsInput = null!;
    private NumericUpDown _cooldownSecondsInput = null!;
    private NumericUpDown _startupJitterSecondsInput = null!;
    private NumericUpDown _minThinkMillisecondsInput = null!;
    private NumericUpDown _maxThinkMillisecondsInput = null!;
    private NumericUpDown _outboundWaitSecondsInput = null!;
    private NumericUpDown _managedWorkerCountInput = null!;
    private Button _resetButton = null!;
    private Button _stopOldWorkersButton = null!;
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private Button _saveSummaryButton = null!;
    private Label _statusLabel = null!;
    private ListView _metricsListView = null!;
    private ListView _workerMetricsListView = null!;
    private TextBox _logTextBox = null!;
    private SplitContainer _mainSplit = null!;

    private IReadOnlyList<WorkerInstanceLoadSnapshot> _latestWorkerSnapshots = Array.Empty<WorkerInstanceLoadSnapshot>();
    private IReadOnlyList<ManagedWorkerProcessSnapshot> _latestManagedWorkers = Array.Empty<ManagedWorkerProcessSnapshot>();
    private DateTimeOffset _nextBackendRefreshUtc = DateTimeOffset.MinValue;
    private bool _backendRefreshInProgress;
    private ListViewHeaderToolTipController? _workerHeaderToolTipController;

    public Form1(SimulatorDefaults defaults)
    {
        _defaults = defaults;
        _engine = new FakeFacebookSimulatorEngine(defaults);
        _resetService = new SimulatorStateResetService(defaults);
        _backendMetricsClient = new DevMetricsSnapshotClient();
        _preflightClient = new SimulatorApiPreflightClient();
        _managedWorkerManager = new ManagedWorkerProcessManager(defaults, OnLogProduced);
        _engine.LogProduced += OnLogProduced;

        InitializeUi();
        ApplyDefaults(defaults);
        RefreshSnapshot();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += async (_, _) => await RefreshUiAsync().ConfigureAwait(true);
        _refreshTimer.Start();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        try
        {
            await _engine.EnsureListenerAsync(_listenerUrlTextBox.Text.Trim()).ConfigureAwait(true);
            AppendLog("Headless fake-meta subscription ready via Redis store.");
            AdjustSplitDistance();
            EnsureWorkerHeaderToolTips();
            await RefreshBackendMetricsAsync(force: true).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppendLog($"Listener start failed: {exception.Message}");
            MessageBox.Show(exception.ToString(), "Failed to start headless fake-meta subscription", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        _engine.LogProduced -= OnLogProduced;
        _workerHeaderToolTipController?.Dispose();
        _headerToolTip.RemoveAll();
        _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _managedWorkerManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _backendMetricsClient.Dispose();
        _preflightClient.Dispose();
        base.OnFormClosing(e);
    }

    private void InitializeUi()
    {
        Text = "Fake FB For Simulate";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1600, 920);
        Size = new Size(1800, 1040);
        WindowState = FormWindowState.Maximized;

        _toolTip.AutoPopDelay = 30000;
        _toolTip.InitialDelay = 250;
        _toolTip.ReshowDelay = 100;
        _toolTip.ShowAlways = true;
        _toolTip.OwnerDraw = true;
        _toolTip.Popup += ToolTipOnPopup;
        _toolTip.Draw += ToolTipOnDraw;

        _headerToolTip.AutoPopDelay = 30000;
        _headerToolTip.InitialDelay = 250;
        _headerToolTip.ReshowDelay = 100;
        _headerToolTip.ShowAlways = true;
        _headerToolTip.OwnerDraw = true;
        _headerToolTip.Popup += ToolTipOnPopup;
        _headerToolTip.Draw += ToolTipOnDraw;

        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var settingsGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "Simulation Settings",
            Padding = new Padding(8)
        };

        _settingsTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 7
        };
        for (var columnIndex = 0; columnIndex < 4; columnIndex++)
        {
            _settingsTable.ColumnStyles.Add(new ColumnStyle(columnIndex % 2 == 0 ? SizeType.AutoSize : SizeType.Percent, columnIndex % 2 == 0 ? 0 : 50));
        }

        _webhookUrlTextBox = CreateTextBox();
        _listenerUrlTextBox = CreateTextBox();
        _pageIdTextBox = CreateTextBox();
        _appSecretTextBox = CreateTextBox(usePassword: true);
        _startTokenTextBox = CreateTextBox();
        _activeShowIdTextBox = CreateTextBox();
        _userCountInput = CreateNumericInput(1, 100000, 200);
        _durationSecondsInput = CreateNumericInput(10, 86400, 300);
        _cooldownSecondsInput = CreateNumericInput(0, 3600, 60);
        _startupJitterSecondsInput = CreateNumericInput(0, 3600, 30);
        _minThinkMillisecondsInput = CreateNumericInput(0, 60000, 250);
        _maxThinkMillisecondsInput = CreateNumericInput(0, 60000, 1000);
        _outboundWaitSecondsInput = CreateNumericInput(1, 600, 15);
        _managedWorkerCountInput = CreateNumericInput(0, 16, 0);

        AddSettingRow(0, "Webhook URL", _webhookUrlTextBox, "Fake Meta URL", _listenerUrlTextBox);
        AddSettingRow(1, "Page ID", _pageIdTextBox, "App Secret", _appSecretTextBox);
        AddSettingRow(2, "Start Token", _startTokenTextBox, "Active Show ID", _activeShowIdTextBox);
        AddSettingRow(3, "Duration (sec)", _durationSecondsInput, "Fake Users", _userCountInput);
        AddSettingRow(4, "Cooldown (sec)", _cooldownSecondsInput, "Startup Jitter (sec)", _startupJitterSecondsInput);
        AddSettingRow(5, "Min Think (ms)", _minThinkMillisecondsInput, "Max Think (ms)", _maxThinkMillisecondsInput);
        AddSettingRow(6, "Outbound Wait (sec)", _outboundWaitSecondsInput, "Managed Workers", _managedWorkerCountInput);

        settingsGroup.Controls.Add(_settingsTable);
        root.Controls.Add(settingsGroup, 0, 0);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 4)
        };

        _resetButton = new Button
        {
            AutoSize = true,
            Text = "Reset DB/Redis",
            Padding = new Padding(14, 5, 14, 5)
        };
        _resetButton.Click += ResetButtonOnClick;

        _stopOldWorkersButton = new Button
        {
            AutoSize = true,
            Text = "Stop Old Workers",
            Padding = new Padding(14, 5, 14, 5)
        };
        _stopOldWorkersButton.Click += StopOldWorkersButtonOnClick;

        _startButton = new Button
        {
            AutoSize = true,
            Text = "Start Simulation",
            Padding = new Padding(14, 5, 14, 5)
        };
        _startButton.Click += StartButtonOnClick;

        _stopButton = new Button
        {
            AutoSize = true,
            Text = "Stop",
            Padding = new Padding(14, 5, 14, 5),
            Enabled = false
        };
        _stopButton.Click += StopButtonOnClick;

        _saveSummaryButton = new Button
        {
            AutoSize = true,
            Text = "Save Summary",
            Padding = new Padding(14, 5, 14, 5)
        };
        _saveSummaryButton.Click += SaveSummaryButtonOnClick;

        _statusLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(16, 10, 0, 0),
            Text = "Status: Idle"
        };

        toolbar.Controls.Add(_resetButton);
        toolbar.Controls.Add(_stopOldWorkersButton);
        toolbar.Controls.Add(_startButton);
        toolbar.Controls.Add(_stopButton);
        toolbar.Controls.Add(_saveSummaryButton);
        toolbar.Controls.Add(_statusLabel);
        root.Controls.Add(toolbar, 0, 1);

        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical
        };
        root.Controls.Add(_mainSplit, 0, 2);
        Resize += (_, _) => AdjustSplitDistance();

        var leftPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        _mainSplit.Panel1.Controls.Add(leftPanel);

        var metricsGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Live Metrics",
            Padding = new Padding(8)
        };

        _metricsListView = new ListView
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            View = View.Details,
            ShowItemToolTips = true,
            Font = new Font("Segoe UI", 9f)
        };
        _metricsListView.Columns.Add("Metric", 270);
        _metricsListView.Columns.Add("Value", 170);
        PopulateMetricRows();
        metricsGroup.Controls.Add(_metricsListView);
        leftPanel.Controls.Add(metricsGroup, 0, 0);

        var workersGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Worker Instances",
            Padding = new Padding(8)
        };

        _workerMetricsListView = new ListView
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            View = View.Details,
            ShowItemToolTips = true,
            Font = new Font("Segoe UI", 9f)
        };
        _workerMetricsListView.Columns.Add("Managed", 76);
        _workerMetricsListView.Columns.Add("PID", 64);
        _workerMetricsListView.Columns.Add("Instance", 170);
        _workerMetricsListView.Columns.Add("Options", 66);
        _workerMetricsListView.Columns.Add("Accepted", 72);
        _workerMetricsListView.Columns.Add("Ignored", 64);
        _workerMetricsListView.Columns.Add("Raw", 66);
        _workerMetricsListView.Columns.Add("Seen", 66);
        _workerMetricsListView.Columns.Add("Cycle p95", 76);
        _workerMetricsListView.Columns.Add("HTTP p95", 70);
        _workerMetricsListView.Columns.Add("Updated", 76);
        workersGroup.Controls.Add(_workerMetricsListView);
        leftPanel.Controls.Add(workersGroup, 0, 1);

        var logGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Live Log",
            Padding = new Padding(8)
        };
        _logTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font("Consolas", 9f)
        };
        logGroup.Controls.Add(_logTextBox);
        _mainSplit.Panel2.Controls.Add(logGroup);

        ApplyToolTips(settingsGroup, metricsGroup, workersGroup, logGroup);
    }

    private void ApplyDefaults(SimulatorDefaults defaults)
    {
        _webhookUrlTextBox.Text = defaults.WebhookUrl;
        _listenerUrlTextBox.Text = defaults.ListenerUrl;
        _pageIdTextBox.Text = defaults.PageId;
        _appSecretTextBox.Text = defaults.AppSecret;
        _startTokenTextBox.Text = defaults.StartToken;
        _activeShowIdTextBox.Text = defaults.ActiveShowId;
        _userCountInput.Value = defaults.DefaultUserCount;
        _durationSecondsInput.Value = defaults.DefaultDurationSeconds;
        _cooldownSecondsInput.Value = defaults.DefaultCooldownSeconds;
        _startupJitterSecondsInput.Value = defaults.DefaultStartupJitterSeconds;
        _minThinkMillisecondsInput.Value = defaults.DefaultMinThinkMilliseconds;
        _maxThinkMillisecondsInput.Value = defaults.DefaultMaxThinkMilliseconds;
        _outboundWaitSecondsInput.Value = defaults.DefaultOutboundWaitSeconds;
        _managedWorkerCountInput.Value = defaults.DefaultManagedWorkerCount;
    }

    private void PopulateMetricRows()
    {
        AddMetricRow("Running");
        AddMetricRow("StartedAtUtc");
        AddMetricRow("OutboundTransportMode");
        AddMetricRow("ConfiguredUsers");
        AddMetricRow("ConfiguredWorkers");
        AddMetricRow("ManagedWorkersRunning");
        AddMetricRow("DetectedWorkerInstances");
        AddMetricRow("UnmanagedWorkerInstances");
        AddMetricRow("ActiveUsers");
        AddMetricRow("CyclesStarted");
        AddMetricRow("CyclesCompleted");
        AddMetricRow("CyclesFailed");
        AddMetricRow("WebhookAttempts");
        AddMetricRow("WebhookSuccesses");
        AddMetricRow("WebhookFailures");
        AddMetricRow("OutboundMessagesReceived");
        AddMetricRow("CarouselsReceived");
        AddMetricRow("ConfirmationsReceived");
        AddMetricRow("AcceptedTextsReceived");
        AddMetricRow("CooldownTextsReceived");
        AddMetricRow("RejectedTextsReceived");
        AddMetricRow("ExpiredTextsReceived");
        AddMetricRow("InactiveVotingTextsReceived");
        AddMetricRow("OtherTextsReceived");
        AddMetricRow("CarouselTimeouts");
        AddMetricRow("ConfirmationTimeouts");
        AddMetricRow("FinalTextTimeouts");
        AddMetricRow("CarouselShapeFailures");
        AddMetricRow("ConfirmationShapeFailures");
        AddMetricRow("StageUnexpectedTexts");
        AddMetricRow("LateAcceptedTexts");
        AddMetricRow("UnexpectedOutboundShapes");
        AddMetricRow("AverageCompletedCycleMs");
    }

    private void AddMetricRow(string name)
    {
        var item = new ListViewItem(name);
        item.SubItems.Add("-");
        item.ToolTipText = BuildMetricTooltip(name, "-");
        _metricItems[name] = item;
        _metricsListView.Items.Add(item);
    }

    private async Task RefreshUiAsync()
    {
        FlushPendingLogs();
        RefreshSnapshot();
        await RefreshBackendMetricsAsync().ConfigureAwait(true);
    }

    private void RefreshSnapshot()
    {
        _metricsListView.BeginUpdate();
        try
        {
            _latestManagedWorkers = _managedWorkerManager.GetManagedProcesses();
            var snapshot = _engine.GetSnapshot();

            SetMetric("Running", snapshot.IsRunning ? "Yes" : "No");
            SetMetric("StartedAtUtc", snapshot.StartedAtUtc?.ToString("u") ?? "-");
            SetMetric("OutboundTransportMode", snapshot.OutboundTransportMode);
            SetMetric("ConfiguredUsers", snapshot.ConfiguredUsers.ToString("N0"));
            SetMetric("ConfiguredWorkers", _managedWorkerCountInput.Value.ToString("N0"));
            SetMetric("ManagedWorkersRunning", _latestManagedWorkers.Count(static worker => worker.IsRunning).ToString("N0"));
            SetMetric("DetectedWorkerInstances", _latestWorkerSnapshots.Count.ToString("N0"));
            SetMetric("UnmanagedWorkerInstances", _latestWorkerSnapshots.Count(worker => !worker.IsManaged).ToString("N0"));
            SetMetric("ActiveUsers", snapshot.ActiveUsers.ToString("N0"));
            SetMetric("CyclesStarted", snapshot.CyclesStarted.ToString("N0"));
            SetMetric("CyclesCompleted", snapshot.CyclesCompleted.ToString("N0"));
            SetMetric("CyclesFailed", snapshot.CyclesFailed.ToString("N0"));
            SetMetric("WebhookAttempts", snapshot.WebhookAttempts.ToString("N0"));
            SetMetric("WebhookSuccesses", snapshot.WebhookSuccesses.ToString("N0"));
            SetMetric("WebhookFailures", snapshot.WebhookFailures.ToString("N0"));
            SetMetric("OutboundMessagesReceived", snapshot.OutboundMessagesReceived.ToString("N0"));
            SetMetric("CarouselsReceived", snapshot.CarouselsReceived.ToString("N0"));
            SetMetric("ConfirmationsReceived", snapshot.ConfirmationsReceived.ToString("N0"));
            SetMetric("AcceptedTextsReceived", snapshot.AcceptedTextsReceived.ToString("N0"));
            SetMetric("CooldownTextsReceived", snapshot.CooldownTextsReceived.ToString("N0"));
            SetMetric("RejectedTextsReceived", snapshot.RejectedTextsReceived.ToString("N0"));
            SetMetric("ExpiredTextsReceived", snapshot.ExpiredTextsReceived.ToString("N0"));
            SetMetric("InactiveVotingTextsReceived", snapshot.InactiveVotingTextsReceived.ToString("N0"));
            SetMetric("OtherTextsReceived", snapshot.OtherTextsReceived.ToString("N0"));
            SetMetric("CarouselTimeouts", snapshot.CarouselTimeouts.ToString("N0"));
            SetMetric("ConfirmationTimeouts", snapshot.ConfirmationTimeouts.ToString("N0"));
            SetMetric("FinalTextTimeouts", snapshot.FinalTextTimeouts.ToString("N0"));
            SetMetric("CarouselShapeFailures", snapshot.CarouselShapeFailures.ToString("N0"));
            SetMetric("ConfirmationShapeFailures", snapshot.ConfirmationShapeFailures.ToString("N0"));
            SetMetric("StageUnexpectedTexts", snapshot.StageUnexpectedTexts.ToString("N0"));
            SetMetric("LateAcceptedTexts", snapshot.LateAcceptedTexts.ToString("N0"));
            SetMetric("UnexpectedOutboundShapes", snapshot.UnexpectedOutboundShapes.ToString("N0"));
            SetMetric("AverageCompletedCycleMs", snapshot.AverageCompletedCycleMilliseconds.ToString("N2"));

            var unmanagedCount = _latestWorkerSnapshots.Count(worker => !worker.IsManaged);
            _statusLabel.Text = snapshot.IsRunning
                ? $"Status: Running ({snapshot.ActiveUsers:N0} active fake users, {unmanagedCount:N0} unmanaged worker detected)"
                : $"Status: Idle ({unmanagedCount:N0} unmanaged worker detected)";

            SetRunningState(snapshot.IsRunning);
        }
        finally
        {
            _metricsListView.EndUpdate();
        }
    }

    private async Task RefreshBackendMetricsAsync(bool force = false)
    {
        if (_backendRefreshInProgress)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && now < _nextBackendRefreshUtc)
        {
            return;
        }

        _backendRefreshInProgress = true;
        try
        {
            _latestManagedWorkers = _managedWorkerManager.GetManagedProcesses();
            var managedPids = _latestManagedWorkers.Where(static worker => worker.IsRunning).Select(static worker => worker.ProcessId).ToHashSet();
            _latestWorkerSnapshots = await _backendMetricsClient.GetWorkerSnapshotsAsync(_webhookUrlTextBox.Text.Trim(), managedPids).ConfigureAwait(true);
            PopulateWorkerMetrics();
        }
        catch (Exception exception)
        {
            AppendLog($"Worker metrics refresh warning: {exception.Message}");
        }
        finally
        {
            _nextBackendRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(2);
            _backendRefreshInProgress = false;
            RefreshSnapshot();
        }
    }

    private void PopulateWorkerMetrics()
    {
        _workerMetricsListView.BeginUpdate();
        _workerMetricsListView.Items.Clear();

        foreach (var worker in _latestWorkerSnapshots)
        {
            var item = new ListViewItem(worker.IsManaged ? "Yes" : "No");
            item.SubItems.Add(worker.ProcessId.ToString());
            item.SubItems.Add(worker.InstanceId);
            item.SubItems.Add(worker.OptionsSent.ToString("N0"));
            item.SubItems.Add(worker.VotesAccepted.ToString("N0"));
            item.SubItems.Add(worker.Ignored.ToString("N0"));
            item.SubItems.Add(worker.RawEnvelopesReceived.ToString("N0"));
            item.SubItems.Add(worker.EventsSeen.ToString("N0"));
            item.SubItems.Add(worker.NormalizedCycleP95Milliseconds.ToString("N1"));
            item.SubItems.Add(worker.OutboundHttpP95Milliseconds.ToString("N1"));
            item.SubItems.Add(worker.UpdatedAtUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "-");
            item.ToolTipText = BuildWorkerTooltip(worker);
            _workerMetricsListView.Items.Add(item);
        }

        foreach (var managedWorker in _latestManagedWorkers.Where(managed => _latestWorkerSnapshots.All(snapshot => snapshot.ProcessId != managed.ProcessId)))
        {
            var item = new ListViewItem(managedWorker.IsRunning ? "Yes" : "No");
            item.SubItems.Add(managedWorker.ProcessId.ToString());
            item.SubItems.Add($"managed-slot-{managedWorker.Slot}");
            item.SubItems.Add("-");
            item.SubItems.Add("-");
            item.SubItems.Add("-");
            item.SubItems.Add("-");
            item.SubItems.Add("-");
            item.SubItems.Add("-");
            item.SubItems.Add("-");
            item.SubItems.Add(managedWorker.StartedAtUtc.ToLocalTime().ToString("HH:mm:ss"));
            item.ToolTipText = $"?? managed worker slot {managedWorker.Slot}-??????. PID: {managedWorker.ProcessId}. Metrics ??? ?? ?????? ?? worker ??????? ??????.";
            _workerMetricsListView.Items.Add(item);
        }

        _workerMetricsListView.EndUpdate();
    }

    private void SetMetric(string key, string value)
    {
        if (_metricItems.TryGetValue(key, out var item))
        {
            item.SubItems[1].Text = value;
            item.ToolTipText = BuildMetricTooltip(key, value);
        }
    }


    private static string BuildMetricTooltip(string key, string value)
    {
        var hint = MetricHints.TryGetValue(key, out var description)
            ? description
            : "???????? ?????????? ?? ???? ????????.";

        return $"{hint}{Environment.NewLine}????????? ???????????: {value}";
    }

    private void EnsureWorkerHeaderToolTips()
    {
        if (_workerHeaderToolTipController is not null || !_workerMetricsListView.IsHandleCreated)
        {
            return;
        }

        _workerHeaderToolTipController = ListViewHeaderToolTipController.TryCreate(_workerMetricsListView, _headerToolTip, WorkerColumnHints);
    }
    private async void StartButtonOnClick(object? sender, EventArgs e)
    {
        try
        {
            SetRunningState(true, isStarting: true);
            var settings = BuildSettings();
            AppendLog($"Preflight check: {settings.WebhookUrl}");
            var preflight = await _preflightClient.CheckAsync(settings.WebhookUrl).ConfigureAwait(true);
            if (!preflight.HealthLiveOk)
            {
                throw new InvalidOperationException($"API preflight failed at /health/live. {preflight.HealthLiveDetail}");
            }

            if (!preflight.DevAdminVotingOk)
            {
                throw new InvalidOperationException($"API preflight failed at /dev/admin/api/voting. {preflight.DevAdminVotingDetail}");
            }

            var desiredManagedWorkerCount = (int)_managedWorkerCountInput.Value;
            if (desiredManagedWorkerCount > 0)
            {
                var managedWorkerContract = RequireManagedWorkerContract();
                AppendLog($"Managed worker contract: Environment={managedWorkerContract.EnvironmentName}, Mode={managedWorkerContract.ResolvedOutboundMode}, Executable={managedWorkerContract.ExecutablePath}");
            }

            AppendLog("Applying managed worker count...");
            await _managedWorkerManager.EnsureWorkerCountAsync(desiredManagedWorkerCount).ConfigureAwait(true);
            await RefreshBackendMetricsAsync(force: true).ConfigureAwait(true);

            AppendLog($"Starting simulation with managed workers={desiredManagedWorkerCount}...");
            await _engine.StartAsync(settings).ConfigureAwait(true);
            RefreshSnapshot();
        }
        catch (Exception exception)
        {
            SetRunningState(false);
            AppendLog($"Start failed: {exception.Message}");
            MessageBox.Show(exception.ToString(), "Failed to start simulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void ResetButtonOnClick(object? sender, EventArgs e)
    {
        try
        {
            _settingsTable.Enabled = false;
            _resetButton.Enabled = false;
            _stopOldWorkersButton.Enabled = false;
            _startButton.Enabled = false;
            _stopButton.Enabled = false;
            _saveSummaryButton.Enabled = false;

            AppendLog("Resetting SQL and Redis state...");
            var result = await _resetService.ResetAsync().ConfigureAwait(true);
            AppendLog($"Reset complete. AcceptedVotes deleted={result.AcceptedVotesDeleted}, NormalizedEvents deleted={result.NormalizedEventsDeleted}, Redis keys deleted={result.RedisKeysDeleted}");
            await RefreshBackendMetricsAsync(force: true).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppendLog($"Reset failed: {exception.Message}");
            MessageBox.Show(exception.ToString(), "Failed to reset simulator state", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RefreshSnapshot();
        }
    }

    private async void StopOldWorkersButtonOnClick(object? sender, EventArgs e)
    {
        try
        {
            _settingsTable.Enabled = false;
            _resetButton.Enabled = false;
            _stopOldWorkersButton.Enabled = false;
            _startButton.Enabled = false;
            _stopButton.Enabled = false;
            _saveSummaryButton.Enabled = false;

            AppendLog("Stopping unmanaged worker processes...");
            var stoppedCount = await StopUnmanagedWorkersAsync().ConfigureAwait(true);
            AppendLog(stoppedCount == 0
                ? "No unmanaged worker processes were found."
                : $"Stopped {stoppedCount} unmanaged worker process(es)."
            );
            await RefreshBackendMetricsAsync(force: true).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppendLog($"Stop old workers failed: {exception.Message}");
            MessageBox.Show(exception.ToString(), "Failed to stop old workers", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RefreshSnapshot();
        }
    }

    private async Task<int> StopUnmanagedWorkersAsync(CancellationToken cancellationToken = default)
    {
        var managedPids = _managedWorkerManager.GetManagedProcesses()
            .Where(static process => process.IsRunning)
            .Select(static process => process.ProcessId)
            .ToHashSet();

        var stoppedCount = 0;
        foreach (var process in Process.GetProcessesByName("GameController.FBServiceExt.Worker")
                     .Where(process => !managedPids.Contains(process.Id))
                     .OrderBy(process => process.Id))
        {
            try
            {
                AppendLog($"Stopping unmanaged worker PID={process.Id}...");
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                stoppedCount++;
            }
            catch (Exception exception)
            {
                AppendLog($"Unmanaged worker stop warning. PID={process.Id}, {exception.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return stoppedCount;
    }

    private async void StopButtonOnClick(object? sender, EventArgs e)
    {
        try
        {
            SetRunningState(true, isStopping: true);
            AppendLog("Stopping simulation...");
            await _engine.StopAsync().ConfigureAwait(true);
            RefreshSnapshot();
        }
        catch (Exception exception)
        {
            AppendLog($"Stop failed: {exception.Message}");
            MessageBox.Show(exception.ToString(), "Failed to stop simulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetRunningState(false);
        }
    }

    private ManagedWorkerContractInfo? TryGetManagedWorkerContract()
    {
        try
        {
            return SimulatorManagedWorkerContract.Probe(_defaults);
        }
        catch
        {
            return null;
        }
    }

    private ManagedWorkerContractInfo RequireManagedWorkerContract()
    {
        SimulatorManagedWorkerContract.EnsureFakeMetaCompatible(_defaults, FakeFacebookSimulatorEngine.FakeMetaTransportMode);
        return SimulatorManagedWorkerContract.Probe(_defaults);
    }
    private SimulatorRunSettings BuildSettings()
    {
        var minThink = (int)_minThinkMillisecondsInput.Value;
        var maxThink = (int)_maxThinkMillisecondsInput.Value;
        if (maxThink < minThink)
        {
            throw new InvalidOperationException("Max Think (ms) must be greater than or equal to Min Think (ms).");
        }

        var managedWorkerContract = TryGetManagedWorkerContract();

        return new SimulatorRunSettings(
            _webhookUrlTextBox.Text.Trim(),
            _listenerUrlTextBox.Text.Trim(),
            _pageIdTextBox.Text.Trim(),
            _appSecretTextBox.Text,
            _startTokenTextBox.Text.Trim(),
            (int)_userCountInput.Value,
            (int)_durationSecondsInput.Value,
            (int)_cooldownSecondsInput.Value,
            (int)_startupJitterSecondsInput.Value,
            minThink,
            maxThink,
            (int)_outboundWaitSecondsInput.Value,
            _defaults.DefaultFailureBackoffMinSeconds,
            _defaults.DefaultFailureBackoffMaxSeconds,
            _activeShowIdTextBox.Text.Trim(),
            _defaults.ConfigureVotingGateOnStart,
            new SimulatorTextPatterns(
                _defaults.CooldownTextFragments,
                _defaults.RejectedTextFragments,
                _defaults.ExpiredTextFragments,
                _defaults.InactiveVotingTextFragments),
            managedWorkerContract?.EnvironmentName ?? SimulatorManagedWorkerContract.ResolveEffectiveEnvironmentName(_defaults.ManagedWorkerEnvironmentName),
            managedWorkerContract?.ResolvedOutboundMode ?? "Unknown",
            managedWorkerContract?.ExecutablePath ?? string.Empty);
    }

    private void SaveSummaryButtonOnClick(object? sender, EventArgs e)
    {
        try
        {
            RefreshSnapshot();
            var summaryDirectory = CreateSummaryDirectory();
            var jsonPath = Path.Combine(summaryDirectory, "summary.json");
            var textPath = Path.Combine(summaryDirectory, "summary.txt");

            var snapshot = _engine.GetSnapshot();
            var summary = new SimulatorRunSummary(
                DateTimeOffset.UtcNow,
                BuildSettings(),
                snapshot,
                _latestManagedWorkers,
                _latestWorkerSnapshots,
                _metricItems.ToDictionary(static pair => pair.Key, static pair => pair.Value.SubItems[1].Text, StringComparer.Ordinal),
                _logTextBox.Lines.Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray());

            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json, Encoding.UTF8);
            File.WriteAllText(textPath, BuildSummaryText(summary, jsonPath), Encoding.UTF8);

            AppendLog($"Summary saved: {summaryDirectory}");
            MessageBox.Show($"Summary saved to:{Environment.NewLine}{summaryDirectory}", "Summary saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            AppendLog($"Save summary failed: {exception.Message}");
            MessageBox.Show(exception.ToString(), "Failed to save summary", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string CreateSummaryDirectory()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var artifactsRoot = ResolveArtifactsRoot();
        var directory = Path.Combine(artifactsRoot, "simulator-ui", $"run-{timestamp}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string ResolveArtifactsRoot()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var solutionDirectory = FindAncestor(baseDirectory, "GameController.FBServiceExtSolution");
        return solutionDirectory is null
            ? Path.Combine(AppContext.BaseDirectory, "artifacts")
            : Path.Combine(solutionDirectory.FullName, "artifacts");
    }

    private static DirectoryInfo? FindAncestor(DirectoryInfo? start, string directoryName)
    {
        var current = start;
        while (current is not null)
        {
            if (string.Equals(current.Name, directoryName, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

        private static string BuildSummaryText(SimulatorRunSummary summary, string jsonPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("FakeFB Simulator Summary");
        builder.AppendLine($"SavedAtUtc: {summary.SavedAtUtc:u}");
        builder.AppendLine($"JsonPath: {jsonPath}");
        builder.AppendLine();
        builder.AppendLine("Settings");
        builder.AppendLine($"  WebhookUrl: {summary.Settings.WebhookUrl}");
        builder.AppendLine($"  ListenerUrl: {summary.Settings.ListenerUrl}");
        builder.AppendLine($"  PageId: {summary.Settings.PageId}");
        builder.AppendLine($"  StartToken: {summary.Settings.StartToken}");
        builder.AppendLine($"  FakeUsers: {summary.Settings.UserCount}");
        builder.AppendLine($"  DurationSeconds: {summary.Settings.DurationSeconds}");
        builder.AppendLine($"  CooldownSeconds: {summary.Settings.CooldownSeconds}");
        builder.AppendLine($"  StartupJitterSeconds: {summary.Settings.StartupJitterSeconds}");
        builder.AppendLine($"  MinThinkMilliseconds: {summary.Settings.MinThinkMilliseconds}");
        builder.AppendLine($"  MaxThinkMilliseconds: {summary.Settings.MaxThinkMilliseconds}");
        builder.AppendLine($"  OutboundWaitSeconds: {summary.Settings.OutboundWaitSeconds}");
        builder.AppendLine($"  FailureBackoffMinSeconds: {summary.Settings.FailureBackoffMinSeconds}");
        builder.AppendLine($"  FailureBackoffMaxSeconds: {summary.Settings.FailureBackoffMaxSeconds}");
        builder.AppendLine($"  ManagedWorkerEnvironmentName: {summary.Settings.ManagedWorkerEnvironmentName}");
        builder.AppendLine($"  ManagedWorkerOutboundMode: {summary.Settings.ManagedWorkerOutboundMode}");
        builder.AppendLine($"  ManagedWorkerExecutablePath: {summary.Settings.ManagedWorkerExecutablePath}");
        builder.AppendLine();
        builder.AppendLine("Snapshot");
        builder.AppendLine($"  Running: {summary.Snapshot.IsRunning}");
        builder.AppendLine($"  OutboundTransportMode: {summary.Snapshot.OutboundTransportMode}");
        builder.AppendLine($"  StartedAtUtc: {(summary.Snapshot.StartedAtUtc?.ToString("u") ?? "-")}");
        builder.AppendLine($"  CyclesStarted: {summary.Snapshot.CyclesStarted}");
        builder.AppendLine($"  CyclesCompleted: {summary.Snapshot.CyclesCompleted}");
        builder.AppendLine($"  CyclesFailed: {summary.Snapshot.CyclesFailed}");
        builder.AppendLine($"  WebhookAttempts: {summary.Snapshot.WebhookAttempts}");
        builder.AppendLine($"  WebhookSuccesses: {summary.Snapshot.WebhookSuccesses}");
        builder.AppendLine($"  WebhookFailures: {summary.Snapshot.WebhookFailures}");
        builder.AppendLine($"  AcceptedTextsReceived: {summary.Snapshot.AcceptedTextsReceived}");
        builder.AppendLine($"  LateAcceptedTexts: {summary.Snapshot.LateAcceptedTexts}");
        builder.AppendLine($"  StageUnexpectedTexts: {summary.Snapshot.StageUnexpectedTexts}");
        builder.AppendLine($"  CarouselTimeouts: {summary.Snapshot.CarouselTimeouts}");
        builder.AppendLine($"  ConfirmationTimeouts: {summary.Snapshot.ConfirmationTimeouts}");
        builder.AppendLine($"  FinalTextTimeouts: {summary.Snapshot.FinalTextTimeouts}");
        builder.AppendLine($"  CarouselShapeFailures: {summary.Snapshot.CarouselShapeFailures}");
        builder.AppendLine($"  ConfirmationShapeFailures: {summary.Snapshot.ConfirmationShapeFailures}");
        builder.AppendLine($"  UnexpectedOutboundShapes: {summary.Snapshot.UnexpectedOutboundShapes}");
        builder.AppendLine($"  AverageCompletedCycleMs: {summary.Snapshot.AverageCompletedCycleMilliseconds:N2}");
        builder.AppendLine();
        builder.AppendLine("WorkerInstances");
        foreach (var worker in summary.WorkerSnapshots)
        {
            builder.AppendLine($"  PID={worker.ProcessId}, Managed={worker.IsManaged}, Environment={worker.EnvironmentName}, Options={worker.OptionsSent}, Accepted={worker.VotesAccepted}, Raw={worker.RawEnvelopesReceived}, Seen={worker.EventsSeen}, CycleP95={worker.NormalizedCycleP95Milliseconds:N1}, HttpP95={worker.OutboundHttpP95Milliseconds:N1}");
        }

        return builder.ToString();
    }
    private void SetRunningState(bool isRunning, bool isStarting = false, bool isStopping = false)
    {
        _settingsTable.Enabled = !isRunning || isStopping;
        _resetButton.Enabled = !isRunning && !isStarting;
        _stopOldWorkersButton.Enabled = !isRunning && !isStarting;
        _startButton.Enabled = !isRunning && !isStarting;
        _stopButton.Enabled = isRunning && !isStarting;
        _saveSummaryButton.Enabled = !isStarting && !isStopping;
    }

    private void OnLogProduced(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        var pendingCount = Interlocked.Increment(ref _pendingLogLineCount);
        if (pendingCount > MaxPendingLogLines)
        {
            Interlocked.Decrement(ref _pendingLogLineCount);
            Interlocked.Increment(ref _suppressedUiLogLines);
            return;
        }

        _pendingLogLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void AppendLog(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        AppendLogLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void FlushPendingLogs()
    {
        if (IsDisposed)
        {
            return;
        }

        var builder = new StringBuilder();
        var suppressed = Interlocked.Exchange(ref _suppressedUiLogLines, 0);
        if (suppressed > 0)
        {
            builder.Append('[')
                .Append(DateTime.Now.ToString("HH:mm:ss"))
                .Append("] UI log queue suppressed ")
                .Append(suppressed.ToString("N0"))
                .AppendLine(" lines during burst.");
        }

        var count = 0;
        while (count < LogFlushBatchSize && _pendingLogLines.TryDequeue(out var line))
        {
            builder.AppendLine(line);
            Interlocked.Decrement(ref _pendingLogLineCount);
            count++;
        }

        if (builder.Length == 0)
        {
            return;
        }

        _logTextBox.AppendText(builder.ToString());
        TrimVisibleLogLinesIfNeeded();
    }

    private void AppendLogLine(string line)
    {
        _logTextBox.AppendText(line + Environment.NewLine);
        TrimVisibleLogLinesIfNeeded();
    }

    private void TrimVisibleLogLinesIfNeeded()
    {
        var lines = _logTextBox.Lines;
        if (lines.Length <= MaxVisibleLogLines)
        {
            return;
        }

        _logTextBox.Lines = lines.Skip(lines.Length - MaxVisibleLogLines).ToArray();
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
    }

    private void AddSettingRow(int rowIndex, string leftLabel, Control leftControl, string rightLabel, Control rightControl)
    {
        _settingsTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _settingsTable.Controls.Add(CreateLabel(leftLabel), 0, rowIndex);
        _settingsTable.Controls.Add(leftControl, 1, rowIndex);
        _settingsTable.Controls.Add(CreateLabel(rightLabel), 2, rowIndex);
        _settingsTable.Controls.Add(rightControl, 3, rowIndex);
    }

    private void AdjustSplitDistance()
    {
        if (!_mainSplit.IsHandleCreated)
        {
            return;
        }

        var desiredPanel2MinSize = Math.Min(360, Math.Max(0, _mainSplit.Width - _mainSplit.SplitterWidth - 100));
        if (_mainSplit.Panel2MinSize != desiredPanel2MinSize)
        {
            _mainSplit.Panel2MinSize = desiredPanel2MinSize;
        }

        var maxDistance = _mainSplit.Width - desiredPanel2MinSize - _mainSplit.SplitterWidth;
        if (maxDistance <= 0)
        {
            return;
        }

        var minimumDistance = Math.Min(960, maxDistance);
        var preferredDistance = Math.Max(1180, _mainSplit.Width - 420);
        _mainSplit.SplitterDistance = Math.Clamp(preferredDistance, minimumDistance, maxDistance);
    }

    private void ToolTipOnPopup(object? sender, PopupEventArgs e)
    {
        if (e.AssociatedControl is ListView)
        {
            return;
        }

        var text = e.AssociatedControl is not null ? _toolTip.GetToolTip(e.AssociatedControl) : string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        const int maxWidth = 520;
        var measured = TextRenderer.MeasureText(
            text,
            _toolTipFont,
            new Size(maxWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.LeftAndRightPadding);

        e.ToolTipSize = new Size(Math.Min(maxWidth + 16, measured.Width + 12), measured.Height + 10);
    }

    private void ToolTipOnDraw(object? sender, DrawToolTipEventArgs e)
    {
        e.Graphics.FillRectangle(SystemBrushes.Info, e.Bounds);
        e.DrawBorder();
        var textBounds = Rectangle.Inflate(e.Bounds, -6, -4);
        TextRenderer.DrawText(
            e.Graphics,
            e.ToolTipText,
            _toolTipFont,
            textBounds,
            SystemColors.InfoText,
            TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.LeftAndRightPadding);
    }

    private void ApplyToolTips(GroupBox settingsGroup, GroupBox metricsGroup, GroupBox workersGroup, GroupBox logGroup)
    {
        _toolTip.SetToolTip(settingsGroup, "?? ?????? ?????????? ??????????? ?? managed worker-???? ??????.");
        _toolTip.SetToolTip(_webhookUrlTextBox, "GameController.FBServiceExt-?? webhook endpoint, ????? simulator ???????? inbound request-???.");
        _toolTip.SetToolTip(_listenerUrlTextBox, "Headless ??????? outbound callback-?? ????? ?????????. ????????? ?????? Redis fake-meta store-? ???????; ?? ???? compatibility-?????? ?????.");
        _toolTip.SetToolTip(_pageIdTextBox, "???????????? Facebook Page ID.");
        _toolTip.SetToolTip(_appSecretTextBox, "Webhook request signature validation-?? ????????. ??????? ?????, ???? API ?????? signature check ????????.");
        _toolTip.SetToolTip(_startTokenTextBox, "??????, ???????? fake user ?????? voting flow-?, ????????? GET_STARTED.");
        _toolTip.SetToolTip(_userCountInput, "??????? fake user ???????? ?? run-??.");
        _toolTip.SetToolTip(_durationSecondsInput, "?????????? ???????????? ???????. ?? ????? ???????????? run ??????? ???????.");
        _toolTip.SetToolTip(_cooldownSecondsInput, "??????? ???? ????????? fake user ??????? ???????????.");
        _toolTip.SetToolTip(_startupJitterSecondsInput, "?????? ????? ????????????? ?????????? ?????????????? ??????, ??? ??????? ??????? burst ?????????.");
        _toolTip.SetToolTip(_minThinkMillisecondsInput, "fake user-?? ?????????? ?????????? ??? ???????????.");
        _toolTip.SetToolTip(_maxThinkMillisecondsInput, "fake user-?? ??????????? ?????????? ??? ???????????.");
        _toolTip.SetToolTip(_outboundWaitSecondsInput, "?????? ???? ????????? simulator outbound ??????, ????? timeout-?? ???????.");
        _toolTip.SetToolTip(_managedWorkerCountInput, "?????? worker process-? ??????? simulator ??????. unmanaged worker-??? ?? ??????? ?? ?????.");
        _toolTip.SetToolTip(_resetButton, "?????????? SQL ?? Redis state-? ????? run-?? ??????????.");
        _toolTip.SetToolTip(_stopOldWorkersButton, "????????? ???? unmanaged worker ????????? ?? ???????? ?????? ?? simulator run-?? managed worker-???.");
        _toolTip.SetToolTip(_startButton, "???????? ???? simulation run-? ????????? ????????????.");
        _toolTip.SetToolTip(_stopButton, "????????? ????????? simulation run-?.");
        _toolTip.SetToolTip(_saveSummaryButton, "????????? ????????? summary-?, worker breakdown-? ?? live log-? ???????? artifacts ????????.");
        _toolTip.SetToolTip(_statusLabel, "????????? run-?? ????? ???????.");
        _toolTip.SetToolTip(metricsGroup, "?? ???? run-?? ?????? ???????? ?? simulator-?? business counters.");
        _toolTip.SetToolTip(workersGroup, "?? ???? ???? worker instance-?? ?????????. ?????? row managed-??, ?????????? unmanaged.");
        _toolTip.SetToolTip(logGroup, "?? ???? simulator-?? ???????? live log. ?? ???? ????????? scroll-heavy ????.");
        _toolTip.SetToolTip(_metricsListView, "????????? ????????? metric row-?, ??? ???????? ???? ??? ???????.");
        _toolTip.SetToolTip(_workerMetricsListView, "????????? worker row-?, ??? ???? ?? instance-?? ???????? ??????? ??????????.");
    }
    private static string BuildWorkerTooltip(WorkerInstanceLoadSnapshot worker)
    {
        var managementText = worker.IsManaged
            ? "?? worker ?????? ?? simulator run-??."
            : "?? worker unmanaged/????? ???????? ?? ????????? simulator run-? ?? ???????.";

        return string.Join(
            Environment.NewLine,
            managementText,
            $"PID: {worker.ProcessId}",
            "Options: ??????? candidate carousel/option ????????.",
            "Accepted: ??????? vote ???????? accepted ???????.",
            "Ignored: ??????? event ???????? business ????? ????.",
            "Raw: ??????? raw envelope ?????.",
            "Seen: ??????? normalized event ????.",
            "Cycle p95: ?? worker-?? normalized processing p95 latency.",
            "HTTP p95: ?? worker-?? outbound HTTP p95 latency.");
    }
    private static Label CreateLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 8, 10, 0),
            Text = text
        };
    }

    private static TextBox CreateTextBox(bool usePassword = false)
    {
        return new TextBox
        {
            Dock = DockStyle.Top,
            Width = 420,
            UseSystemPasswordChar = usePassword
        };
    }

    private static NumericUpDown CreateNumericInput(int minimum, int maximum, int defaultValue)
    {
        return new NumericUpDown
        {
            Dock = DockStyle.Top,
            Minimum = minimum,
            Maximum = maximum,
            Value = defaultValue,
            ThousandsSeparator = true,
            Width = 160
        };
    }
}












internal sealed class ListViewHeaderToolTipController : NativeWindow, IDisposable
{
    private const int LvmFirst = 0x1000;
    private const int LvmGetHeader = LvmFirst + 31;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseLeave = 0x02A3;
    private const uint TmeLeave = 0x00000002;

    private readonly ListView _owner;
    private readonly ToolTip _toolTip;
    private readonly IReadOnlyDictionary<int, string> _columnHints;
    private int _currentColumn = -1;
    private bool _trackingMouseLeave;

    private ListViewHeaderToolTipController(ListView owner, ToolTip toolTip, IReadOnlyDictionary<int, string> columnHints)
    {
        _owner = owner;
        _toolTip = toolTip;
        _columnHints = columnHints;
    }

    public static ListViewHeaderToolTipController? TryCreate(ListView owner, ToolTip toolTip, IReadOnlyDictionary<int, string> columnHints)
    {
        if (!owner.IsHandleCreated)
        {
            return null;
        }

        var headerHandle = SendMessage(owner.Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
        if (headerHandle == IntPtr.Zero)
        {
            return null;
        }

        var controller = new ListViewHeaderToolTipController(owner, toolTip, columnHints);
        controller.AssignHandle(headerHandle);
        return controller;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmMouseMove:
                HandleMouseMove(m.LParam);
                break;
            case WmMouseLeave:
                HideCurrentToolTip();
                _trackingMouseLeave = false;
                break;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        HideCurrentToolTip();
        if (Handle != IntPtr.Zero)
        {
            ReleaseHandle();
        }
    }

    private void HandleMouseMove(IntPtr lParam)
    {
        TrackMouseLeave();

        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var columnIndex = ResolveColumnIndex(x);
        if (columnIndex == _currentColumn)
        {
            return;
        }

        HideCurrentToolTip();
        _currentColumn = columnIndex;
        if (columnIndex < 0 || !_columnHints.TryGetValue(columnIndex, out var text))
        {
            return;
        }

        var tooltipX = Math.Min(Math.Max(12, x + 12), Math.Max(12, _owner.Width - 24));
        _toolTip.Show(text, _owner, tooltipX, 6, 8000);
    }

    private int ResolveColumnIndex(int x)
    {
        var currentLeft = 0;
        for (var index = 0; index < _owner.Columns.Count; index++)
        {
            currentLeft += _owner.Columns[index].Width;
            if (x < currentLeft)
            {
                return index;
            }
        }

        return -1;
    }

    private void HideCurrentToolTip()
    {
        _currentColumn = -1;
        _toolTip.Hide(_owner);
    }

    private void TrackMouseLeave()
    {
        if (_trackingMouseLeave)
        {
            return;
        }

        var trackMouseEvent = new TrackMouseEvent
        {
            cbSize = Marshal.SizeOf<TrackMouseEvent>(),
            dwFlags = TmeLeave,
            hwndTrack = Handle,
            dwHoverTime = 0
        };

        _trackingMouseLeave = TrackMouseEventNative(ref trackMouseEvent);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "TrackMouseEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEventNative(ref TrackMouseEvent lpEventTrack);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEvent
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }
}
internal sealed record SimulatorRunSummary(
    DateTimeOffset SavedAtUtc,
    SimulatorRunSettings Settings,
    SimulationSnapshot Snapshot,
    IReadOnlyList<ManagedWorkerProcessSnapshot> ManagedWorkers,
    IReadOnlyList<WorkerInstanceLoadSnapshot> WorkerSnapshots,
    IReadOnlyDictionary<string, string> Metrics,
    IReadOnlyList<string> LogLines);













































