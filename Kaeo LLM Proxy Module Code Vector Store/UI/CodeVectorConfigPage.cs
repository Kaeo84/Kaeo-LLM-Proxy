using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class CodeVectorConfigPage : TabPage
{
    private readonly CodeVectorModule _module;
    private ComboBox _backendCombo = null!;
    private GroupBox _remoteGroup = null!;
    private TextBox _remoteUrlBox = null!;
    private Button _fetchModelsButton = null!;
    private ComboBox _remoteModelCombo = null!;
    private Button _showModelButton = null!;
    private ComboBox _credentialCombo = null!;
    private NumericUpDown _timeoutBox = null!;
    private NumericUpDown _parallelismBox = null!;
    private Label _fetchStatusLabel = null!;
    private Button _testConnectionButton = null!;
    private GroupBox _onnxGroup = null!;
    private TextBox _onnxBox = null!;
    private Button _onnxBrowseButton = null!;
    private NumericUpDown _onnxMaxSeqBox = null!;
    private NumericUpDown _onnxThreadsBox = null!;
    private GroupBox _generalGroup = null!;
    private NumericUpDown _chunkLinesBox = null!;
    private NumericUpDown _overlapBox = null!;
    private NumericUpDown _maxSizeBox = null!;
    private NumericUpDown _topKBox = null!;
    private NumericUpDown _syncBox = null!;
    private TextBox _vectorDatabasePathBox = null!;
    private ComboBox _logLevelCombo = null!;
    private CheckBox _chkSearch = null!;
    private CheckBox _chkIndex = null!;
    private CheckBox _chkSync = null!;
    private CheckBox _chkStatus = null!;
    private CheckBox _chkRemove = null!;
    private CheckBox _chkReindex = null!;
    private GroupBox _reposGroup = null!;
    private ListView _reposListView = null!;
    private GroupBox _statusGroup = null!;
    private Label _engineStatusLabel = null!;
    private Label _queueStatusLabel = null!;
    private Label _currentStatusLabel = null!;
    private Label _workersStatusLabel = null!;
    private Label _logSummaryLabel = null!;
    private ListView _queueListView = null!;
    private ListView _logListView = null!;
    private Button _startEngineButton = null!;
    private Button _stopEngineButton = null!;
    private Button _clearQueueButton = null!;
    private ToolTip _statusTooltip = null!;
    private System.Windows.Forms.Timer? _refreshTimer;
    private long _lastRefreshedLogged = -1;

    public CodeVectorConfigPage(CodeVectorModule module) : base("Code Vector Store")
    {
        _module = module;
        BuildUi();
        WireAutoSave();
        UpdateBackendVisibility();
        RefreshRepos();
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
        Disposed += (_, _) => { _refreshTimer?.Dispose(); _refreshTimer = null; };
        RefreshStatus();
    }

    private void BuildUi()
    {
        AutoScroll = true;
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoScroll = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(14, 8, 14, 8),
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < main.RowCount; row++)
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Vector database location
        var databasePanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
        databasePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        databasePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        databasePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        databasePanel.Controls.Add(new Label { Text = "Vector Database:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, 0);
        _vectorDatabasePathBox = new TextBox { Dock = DockStyle.Fill, Text = _module.Settings.VectorDatabasePath, Margin = new Padding(3) };
        var browseDatabaseButton = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(3) };
        browseDatabaseButton.Click += BrowseDatabaseButton_Click;
        databasePanel.Controls.Add(_vectorDatabasePathBox, 1, 0);
        databasePanel.Controls.Add(browseDatabaseButton, 2, 0);
        main.Controls.Add(databasePanel, 0, 0);

        // Backend selector
        var backendPanel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        backendPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        backendPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        backendPanel.Controls.Add(new Label { Text = "Backend:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 0);
        _backendCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        _backendCombo.Items.AddRange(["Remote", "Onnx"]);
        _backendCombo.SelectedItem = _module.Settings.BackendType.ToString();
        _backendCombo.SelectedIndexChanged += BackendCombo_SelectedIndexChanged;
        backendPanel.Controls.Add(_backendCombo, 1, 0);
        main.Controls.Add(backendPanel, 0, 1);

        // Remote group
        _remoteGroup = BuildRemoteGroup();
        main.Controls.Add(_remoteGroup, 0, 2);

        // ONNX group
        _onnxGroup = BuildOnnxGroup();
        main.Controls.Add(_onnxGroup, 0, 3);

        // General settings
        _generalGroup = BuildGeneralGroup();
        main.Controls.Add(_generalGroup, 0, 4);

        // Git Repos
        _reposGroup = BuildReposGroup();
        main.Controls.Add(_reposGroup, 0, 5);

        // Status
        _statusGroup = BuildStatusGroup();
        main.Controls.Add(_statusGroup, 0, 6);

        Controls.Add(main);
    }

    // (BuildUi replaces the old InitializeComponent)

    private GroupBox BuildRemoteGroup()
    {
        var group = new GroupBox { Text = "Remote Backend", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;

        layout.Controls.Add(new Label { Text = "URL:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _remoteUrlBox = new TextBox { Dock = DockStyle.Fill, Text = _module.Settings.RemoteUrl, Margin = new Padding(3) };
        _fetchModelsButton = new Button { Text = "Fetch", AutoSize = true, Margin = new Padding(3) };
        _fetchModelsButton.Click += FetchModelsButton_Click;
        layout.Controls.Add(_remoteUrlBox, 1, row);
        layout.Controls.Add(_fetchModelsButton, 2, row++);

        layout.Controls.Add(new Label { Text = "Model:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _remoteModelCombo = new ComboBox { Dock = DockStyle.Fill, Text = _module.Settings.RemoteModel, Margin = new Padding(3) };
        _showModelButton = new Button { Text = "Info", AutoSize = true, Margin = new Padding(3) };
        _showModelButton.Click += ShowModelButton_Click;
        layout.Controls.Add(_remoteModelCombo, 1, row);
        layout.Controls.Add(_showModelButton, 2, row++);

        layout.Controls.Add(new Label { Text = "Test:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _testConnectionButton = new Button { Text = "Test Connection", AutoSize = true, Margin = new Padding(3) };
        _testConnectionButton.Click += TestConnectionButton_Click;
        layout.Controls.Add(_testConnectionButton, 1, row++);

        layout.Controls.Add(new Label { Text = "Credential:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _credentialCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(3) };
        try { _credentialCombo.Items.AddRange(_module.Secrets.ListCredentialNames().ToArray()); } catch { }
        _credentialCombo.Text = _module.Settings.RemoteCredentialName;
        layout.Controls.Add(_credentialCombo, 1, row++);

        layout.Controls.Add(new Label { Text = "Timeout (s):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _timeoutBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 5, Maximum = 300, Value = _module.Settings.RemoteTimeoutSeconds, Margin = new Padding(3) };
        layout.Controls.Add(_timeoutBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Parallelism:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _parallelismBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 16, Value = _module.Settings.RemoteParallelism, Margin = new Padding(3) };
        layout.Controls.Add(_parallelismBox, 1, row++);

        _fetchStatusLabel = new Label { Text = "", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3) };
        layout.Controls.Add(_fetchStatusLabel, 1, row);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildOnnxGroup()
    {
        var group = new GroupBox { Text = "ONNX Backend", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;

        layout.Controls.Add(new Label { Text = "Model Folder:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _onnxBox = new TextBox { Dock = DockStyle.Fill, Text = _module.Settings.OnnxModelFolder, Margin = new Padding(3) };
        _onnxBrowseButton = new Button { Text = "Browseâ€¦", AutoSize = true, Margin = new Padding(3) };
        _onnxBrowseButton.Click += OnnxBrowseButton_Click;
        layout.Controls.Add(_onnxBox, 1, row);
        layout.Controls.Add(_onnxBrowseButton, 2, row++);

        layout.Controls.Add(new Label { Text = "Max Sequence:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _onnxMaxSeqBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 32, Maximum = 4096, Value = _module.Settings.OnnxMaxSequenceLength, Margin = new Padding(3) };
        layout.Controls.Add(_onnxMaxSeqBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Threads:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _onnxThreadsBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 32, Value = _module.Settings.OnnxMaxThreads, Margin = new Padding(3) };
        layout.Controls.Add(_onnxThreadsBox, 1, row);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildGeneralGroup()
    {
        var group = new GroupBox { Text = "General Settings", Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
        var layout = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;

        layout.Controls.Add(new Label { Text = "Chunk Lines:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _chunkLinesBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 10, Maximum = 1000, Value = _module.Settings.ChunkLines, Margin = new Padding(3) };
        layout.Controls.Add(_chunkLinesBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Overlap Lines:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _overlapBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = _module.Settings.ChunkOverlapLines, Margin = new Padding(3) };
        layout.Controls.Add(_overlapBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Max File (KB):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _maxSizeBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 10240, Value = _module.Settings.MaxFileSizeKb, Margin = new Padding(3) };
        layout.Controls.Add(_maxSizeBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Default Top K:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _topKBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 100, Value = _module.Settings.DefaultTopK, Margin = new Padding(3) };
        layout.Controls.Add(_topKBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Git Sync (min):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _syncBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1440, Value = _module.Settings.GitSyncIntervalMinutes, Margin = new Padding(3) };
        layout.Controls.Add(_syncBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Log Level:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _logLevelCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3) };
        _logLevelCombo.Items.AddRange(["None", "Connectivity", "Full"]);
        _logLevelCombo.SelectedItem = _module.Settings.McpLogLevel.ToString();
        layout.Controls.Add(_logLevelCombo, 1, row++);

        layout.Controls.Add(new Label { Text = "Tools:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        var toolsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, Margin = new Padding(3) };
        _chkSearch = new CheckBox { Text = "Search", AutoSize = true, Checked = _module.Settings.SearchEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkIndex = new CheckBox { Text = "Index", AutoSize = true, Checked = _module.Settings.IndexEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkSync = new CheckBox { Text = "Sync", AutoSize = true, Checked = _module.Settings.SyncRepoEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkStatus = new CheckBox { Text = "Status", AutoSize = true, Checked = _module.Settings.StatusEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkRemove = new CheckBox { Text = "Remove", AutoSize = true, Checked = _module.Settings.RemoveEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkReindex = new CheckBox { Text = "Reindex", AutoSize = true, Checked = _module.Settings.ReindexEnabled, Margin = new Padding(3, 6, 3, 3) };
        toolsPanel.Controls.AddRange([_chkSearch, _chkIndex, _chkSync, _chkStatus, _chkRemove, _chkReindex]);
        layout.Controls.Add(toolsPanel, 1, row);

        group.Controls.Add(layout);
        return group;
    }

               private GroupBox BuildReposGroup()
               {
                   var group = new GroupBox { Text = "Git Repos", Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
                   var layout = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
                   layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                   layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
                   layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                   _reposListView = new ListView
                   {
                       View = View.Details,
                       FullRowSelect = true,
                       GridLines = true,
                       MultiSelect = false,
                       Dock = DockStyle.Fill,
                       HeaderStyle = ColumnHeaderStyle.Nonclickable,
                   };
                   _reposListView.Columns.Add("Collection", 140);
                   _reposListView.Columns.Add("Remote URL", 260);
                    _reposListView.Columns.Add("Branch", 70);
                    _reposListView.Columns.Add("Mirror Path", 220);
                   _reposListView.Columns.Add("Path Prefix", 100);
                   _reposListView.Columns.Add("Last Sync", 140);
                   _reposListView.Columns.Add("Status", 100);
                   layout.Controls.Add(_reposListView, 0, 0);

                   var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
                   var btnAdd = new Button { Text = "Add", AutoSize = true, Margin = new Padding(3) };
                   btnAdd.Click += AddRepoButton_Click;
                   var btnEdit = new Button { Text = "Edit", AutoSize = true, Margin = new Padding(3) };
                   btnEdit.Click += EditRepoButton_Click;
                   var btnRemove = new Button { Text = "Remove", AutoSize = true, Margin = new Padding(3) };
                   btnRemove.Click += RemoveRepoButton_Click;
                   var btnIndex = new Button { Text = "Index", AutoSize = true, Margin = new Padding(3) };
                   btnIndex.Click += IndexRepoButton_Click;
                   var btnSync = new Button { Text = "Sync", AutoSize = true, Margin = new Padding(3) };
                   btnSync.Click += SyncRepoButton_Click;
                   var btnStatus = new Button { Text = "Status", AutoSize = true, Margin = new Padding(3) };
                   btnStatus.Click += RepoStatusButton_Click;
                   var btnReindex = new Button { Text = "Reindex", AutoSize = true, Margin = new Padding(3) };
                   btnReindex.Click += ReindexRepoButton_Click;
                   btnPanel.Controls.AddRange([btnAdd, btnEdit, btnRemove, btnIndex, btnSync, btnStatus, btnReindex]);
                   layout.Controls.Add(btnPanel, 0, 1);

                   group.Controls.Add(layout);
                   return group;
               }

               private GroupBox BuildStatusGroup()
               {
                   var group = new GroupBox { Text = "Status", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
                   var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 5 };
                   layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                   layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                   layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                   layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                   layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
                   layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));

                   var statusPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
                   _engineStatusLabel = new Label { Text = "Engine: —", AutoSize = true, Margin = new Padding(3, 6, 14, 3) };
                   _queueStatusLabel = new Label { Text = "Queue: 0", AutoSize = true, Margin = new Padding(3, 6, 14, 3) };
                   _currentStatusLabel = new Label { Text = "Current: —", AutoSize = true, Margin = new Padding(3, 6, 14, 3) };
                   _workersStatusLabel = new Label { Text = "Workers: 0", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
                   statusPanel.Controls.AddRange([_engineStatusLabel, _queueStatusLabel, _currentStatusLabel, _workersStatusLabel]);
                   layout.Controls.Add(statusPanel, 0, 0);

                   _statusTooltip = new ToolTip();
                   _statusTooltip.SetToolTip(_engineStatusLabel, "Engine is stopped");

                   var buttonPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
                   _startEngineButton = new Button { Text = "Start Engine", AutoSize = true, Margin = new Padding(3) };
                   _startEngineButton.Click += StartEngineButton_Click;
                   _stopEngineButton = new Button { Text = "Stop Engine", AutoSize = true, Margin = new Padding(3) };
                   _stopEngineButton.Click += StopEngineButton_Click;
                   _clearQueueButton = new Button { Text = "Clear Queue", AutoSize = true, Margin = new Padding(3) };
                   _clearQueueButton.Click += ClearQueueButton_Click;
                   buttonPanel.Controls.AddRange([_startEngineButton, _stopEngineButton, _clearQueueButton]);
                   layout.Controls.Add(buttonPanel, 0, 1);

                   _logSummaryLabel = new Label { Text = "Logged: 0 | Errors: 0", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4) };
                   layout.Controls.Add(_logSummaryLabel, 0, 2);

                   _queueListView = new ListView
                   {
                       View = View.Details,
                       FullRowSelect = true,
                       GridLines = true,
                       MultiSelect = false,
                       Dock = DockStyle.Fill,
                       HeaderStyle = ColumnHeaderStyle.Nonclickable,
                   };
                   _queueListView.Columns.Add("Operation", 90);
                   _queueListView.Columns.Add("Collection", 130);
                   _queueListView.Columns.Add("Path", 250);
                   _queueListView.Columns.Add("Source", 60);
                   layout.Controls.Add(_queueListView, 0, 3);

                   var logHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
                   logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                   logHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                   logHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                   var logHeaderBar = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
                   logHeaderBar.Controls.Add(new Label { Text = "Activity Log:", AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
                   var clearButton = new Button { Text = "Clear", AutoSize = true, Margin = new Padding(0) };
                   clearButton.Click += (_, _) => { _module.Activity.ClearBuffer(); RefreshStatus(); };
                   logHeaderBar.Controls.Add(clearButton);
                   logHeader.Controls.Add(logHeaderBar, 0, 0);
                   _logListView = new ListView
                   {
                       View = View.Details,
                       FullRowSelect = true,
                       GridLines = true,
                       MultiSelect = false,
                       Dock = DockStyle.Fill,
                       HeaderStyle = ColumnHeaderStyle.Nonclickable,
                   };
                   _logListView.Columns.Add("Time", 65);
                   _logListView.Columns.Add("Operation", 90);
                   _logListView.Columns.Add("Target", 160);
                   _logListView.Columns.Add("Detail", 230);
                   logHeader.Controls.Add(_logListView, 0, 1);
                   layout.Controls.Add(logHeader, 0, 4);

                   group.Controls.Add(layout);
                   return group;
               }

               private void RefreshStatus()
               {
                   try
                   {
                       var engine = _module.Indexer;
                       var running = engine.IsRunning;
                       var stopReason = _module.EngineStopReason;
                       _engineStatusLabel.Text = running ? "Engine: Running" : "Engine: Stopped";
                       _engineStatusLabel.ForeColor = running ? Color.Green : SystemColors.GrayText;
                       _statusTooltip.SetToolTip(_engineStatusLabel, running ? "Engine is running" : (stopReason is null ? "Engine is stopped" : $"Engine stopped: {stopReason}"));
                       _queueStatusLabel.Text = $"Queue: {engine.QueueDepth}";
                       _workersStatusLabel.Text = $"Workers: {engine.ActiveWorkerCount}";
                       var current = engine.CurrentJob;
                       _currentStatusLabel.Text = current is null ? "Current: —" : $"Current: {current.Path}";

                       _startEngineButton.Enabled = !running;
                       _stopEngineButton.Enabled = running;
                       _clearQueueButton.Enabled = engine.QueueDepth > 0;

                       var activity = _module.Activity;
                       _logSummaryLabel.Text = $"Logged: {activity.TotalLogged} | Errors: {activity.ErrorCount}";

                       _queueListView.BeginUpdate();
                       _queueListView.Items.Clear();
                       foreach (var item in engine.GetQueueSnapshot())
                       {
                           var lvi = new ListViewItem(item.Operation);
                           lvi.SubItems.Add(item.Collection);
                           lvi.SubItems.Add(item.Path);
                           lvi.SubItems.Add(item.Source);
                           _queueListView.Items.Add(lvi);
                       }
                       _queueListView.EndUpdate();

                       if (activity.TotalLogged != _lastRefreshedLogged)
                       {
                           _lastRefreshedLogged = activity.TotalLogged;
                           _logListView.BeginUpdate();
                           _logListView.Items.Clear();
                           foreach (var entry in activity.GetRecentEntries())
                           {
                               var lvi = new ListViewItem(entry.Timestamp.ToString("HH:mm:ss"));
                               lvi.SubItems.Add(entry.Operation);
                               lvi.SubItems.Add(entry.Target);
                               lvi.SubItems.Add(entry.Detail ?? "");
                               if (entry.Operation == "error") lvi.ForeColor = Color.Red;
                               _logListView.Items.Add(lvi);
                           }
                           _logListView.EndUpdate();
                       }
                   }
                   catch (Exception ex)
                   {
                       _engineStatusLabel.Text = $"Status error: {ex.Message}";
                       _engineStatusLabel.ForeColor = Color.OrangeRed;
                   }
               }

               private void StartEngineButton_Click(object? sender, EventArgs e)
               {
                   try
                   {
                       _module.StartEngine();
                       RefreshStatus();
                   }
                   catch (Exception ex) { MessageBox.Show(ex.Message, "Start Engine Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
               }

               private async void StopEngineButton_Click(object? sender, EventArgs e)
               {
                   try
                   {
                       await _module.StopEngineAsync("stopped from UI");
                       RefreshStatus();
                   }
                   catch (Exception ex) { MessageBox.Show(ex.Message, "Stop Engine Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
               }

               private void ClearQueueButton_Click(object? sender, EventArgs e)
               {
                   try
                   {
                       _module.Indexer.ClearQueue();
                       RefreshStatus();
                   }
                   catch (Exception ex) { MessageBox.Show(ex.Message, "Clear Queue Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
               }

               private void RefreshRepos()
               {
                   try
                   {
                       _reposListView.BeginUpdate();
                       _reposListView.Items.Clear();
                       foreach (var m in _module.Repository.LoadMirrors())
                       {
                           var lvi = new ListViewItem(m.CollectionName);
                           lvi.SubItems.Add(m.RemoteUrl);
                           lvi.SubItems.Add(m.Branch);
                            lvi.SubItems.Add(m.MirrorPath ?? "default");
                           lvi.SubItems.Add(m.PathPrefix ?? "");
                           lvi.SubItems.Add(m.LastSyncUtc ?? "never");
                           lvi.SubItems.Add(m.LastSyncStatus ?? "pending");
                           lvi.Tag = m;
                           _reposListView.Items.Add(lvi);
                       }
                       _reposListView.EndUpdate();
                   }
                   catch { }
               }

               private MirrorRegistration? GetSelectedRepo()
                   => _reposListView.SelectedItems.Count > 0 ? _reposListView.SelectedItems[0].Tag as MirrorRegistration : null;

               private void AddRepoButton_Click(object? sender, EventArgs e)
               {
                   using var dlg = new RepoDialog(null);
                   if (dlg.ShowDialog(this.FindForm()) == DialogResult.OK)
                   {
                       try
                       {
                            _ = _module.MirrorManager.RegisterMirrorAsync(dlg.CollectionName, dlg.RemoteUrl, dlg.Branch, dlg.CredentialName, CancellationToken.None, dlg.MirrorPath, dlg.PathPrefix);
                            RefreshRepos();
                        }
                        catch (Exception ex) { MessageBox.Show(ex.Message, "Add Repo Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                   }
               }

               private void EditRepoButton_Click(object? sender, EventArgs e)
               {
                   var m = GetSelectedRepo();
                   if (m is null) { MessageBox.Show("Select a repo first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                   using var dlg = new RepoDialog(m);
                   if (dlg.ShowDialog(this.FindForm()) == DialogResult.OK)
                   {
                       try
                       {
                           _module.Repository.DeleteMirror(m.CollectionName);
                                 _ = _module.MirrorManager.RegisterMirrorAsync(dlg.CollectionName, dlg.RemoteUrl, dlg.Branch, dlg.CredentialName, CancellationToken.None, dlg.MirrorPath, dlg.PathPrefix);
                                RefreshRepos();
                            }
                            catch (Exception ex)
                            {
                                _module.Activity.Log("error", m.CollectionName, $"Edit repo failed: {ex}");
                                MessageBox.Show(ex.Message, "Edit Repo Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                   }
               }

               private void RemoveRepoButton_Click(object? sender, EventArgs e)
               {
                   var m = GetSelectedRepo();
                   if (m is null) { MessageBox.Show("Select a repo first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                   if (MessageBox.Show($"Remove '{m.CollectionName}'?", "Confirm Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                   {
                       _module.Repository.DeleteMirror(m.CollectionName);
                       RefreshRepos();
                   }
               }

               private void RequireRepo(out MirrorRegistration m)
               {
                   m = GetSelectedRepo()!;
                   if (m is null) MessageBox.Show("Select a repo in the list first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
               }

               private async void IndexRepoButton_Click(object? sender, EventArgs e)
               {
                   if (GetSelectedRepo() is not { } m) { RequireRepo(out _); return; }
                   try
                   {
                           await _module.MirrorManager.IndexMirrorFilesAsync(m, CancellationToken.None);
                           _module.Activity.Log("ui_index", m.CollectionName, $"Re-indexed files for {m.CollectionName}");
                       }
                       catch (Exception ex)
                       {
                           _module.Activity.Log("error", m.CollectionName, $"Index failed: {ex}");
                           MessageBox.Show(ex.Message, "Index Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                       }
               }

               private async void SyncRepoButton_Click(object? sender, EventArgs e)
               {
                   if (GetSelectedRepo() is not { } m) { RequireRepo(out _); return; }
                   try
                   {
                           await _module.MirrorManager.SyncMirrorAsync(m, CancellationToken.None);
                           RefreshRepos();
                       }
                       catch (Exception ex)
                       {
                           _module.Activity.Log("error", m.CollectionName, $"Sync failed: {ex.Message}");
                           MessageBox.Show(ex.Message, "Sync Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                       }
               }

               private void RepoStatusButton_Click(object? sender, EventArgs e)
               {
                   if (GetSelectedRepo() is not { } m) { RequireRepo(out _); return; }
                   var collections = _module.VectorDb.ListCollections();
                   var col = collections.FirstOrDefault(c => c.Name == m.CollectionName);
                   var sb = new StringBuilder();
                   sb.AppendLine($"Collection: {m.CollectionName}");
                   sb.AppendLine($"Remote: {m.RemoteUrl} [{m.Branch}]");
                   sb.AppendLine($"Last Sync: {m.LastSyncUtc ?? "never"}");
                   sb.AppendLine($"Sync Status: {m.LastSyncStatus ?? "pending"}");
                   if (col is not null) sb.AppendLine($"Indexed: {col.FileCount} files, {col.ChunkCount} chunks");
                   sb.AppendLine();
                   sb.AppendLine($"Engine: {(_module.Indexer.IsRunning ? "Running" : "Stopped")} | Queue: {_module.Indexer.QueueDepth}");
                   MessageBox.Show(sb.ToString(), $"Status â€” {m.CollectionName}", MessageBoxButtons.OK, MessageBoxIcon.Information);
               }

               private async void ReindexRepoButton_Click(object? sender, EventArgs e)
               {
                   if (GetSelectedRepo() is not { } m) { RequireRepo(out _); return; }
                   try
                   {
                       // Reindex = clear the collection, then re-walk the mirror and re-enqueue all files.
                           _module.VectorDb.DeleteCollection(m.CollectionName);
                           await _module.MirrorManager.IndexMirrorFilesAsync(m, CancellationToken.None);
                           _module.Activity.Log("ui_reindex", m.CollectionName, "Reindex: collection cleared + files re-queued");
                       }
                       catch (Exception ex)
                       {
                           _module.Activity.Log("error", m.CollectionName, $"Reindex failed: {ex}");
                           MessageBox.Show(ex.Message, "Reindex Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                       }
               }

               private void BackendCombo_SelectedIndexChanged(object? sender, EventArgs e)
                   => UpdateBackendVisibility();

    private void BrowseDatabaseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Select Vector Database Location",
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            FileName = Path.GetFileName(string.IsNullOrWhiteSpace(_vectorDatabasePathBox.Text) ? "codevectordb" : _vectorDatabasePathBox.Text),
            OverwritePrompt = false,
        };

        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            _vectorDatabasePathBox.Text = dialog.FileName;
    }

    private void UpdateBackendVisibility()
    {
        bool isRemote = _backendCombo.SelectedItem?.ToString() == "Remote";
        _remoteGroup.Visible = isRemote;
        _onnxGroup.Visible = !isRemote;
    }

    private string DeriveBaseUrl()
    {
        string baseUrl = _remoteUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = $"http://{_module.Host.DisplayHost}:{_module.Host.ListenPort}/v1/embeddings";

        if (baseUrl.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
            return baseUrl[..^"/v1/embeddings".Length];

        return baseUrl.TrimEnd('/');
    }

    private HttpClient CreateAuthedClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds((double)_timeoutBox.Value) };
        string credentialName = _credentialCombo.Text.Trim();
        if (!string.IsNullOrWhiteSpace(credentialName))
        {
            string? secret = _module.Secrets.ResolveSecret(credentialName);
            if (!string.IsNullOrWhiteSpace(secret))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        return client;
    }

    private async void FetchModelsButton_Click(object? sender, EventArgs e)
    {
        string modelsUrl = DeriveBaseUrl() + "/v1/models";

        _fetchModelsButton.Enabled = false;
        _fetchStatusLabel.ForeColor = SystemColors.GrayText;
        _fetchStatusLabel.Text = "Fetching modelsâ€¦";

        try
        {
            using var client = CreateAuthedClient();
            using var response = await client.GetAsync(modelsUrl);
            string body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    string? currentSelection = _remoteModelCombo.Text;
                    _remoteModelCombo.Items.Clear();
                    var modelIds = new List<string>();

                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idProp) && idProp.GetString() is string id)
                            modelIds.Add(id);
                    }

                    _remoteModelCombo.Items.AddRange(modelIds.ToArray());

                    if (modelIds.Contains(currentSelection))
                        _remoteModelCombo.Text = currentSelection;
                    else if (modelIds.Count > 0)
                        _remoteModelCombo.SelectedIndex = 0;

                    _fetchStatusLabel.ForeColor = Color.Green;
                    _fetchStatusLabel.Text = $"Found {modelIds.Count} model(s)";
                }
                else
                {
                    _fetchStatusLabel.ForeColor = Color.OrangeRed;
                    _fetchStatusLabel.Text = $"OK but unexpected response format";
                }
            }
            else
            {
                _fetchStatusLabel.ForeColor = Color.Red;
                _fetchStatusLabel.Text = $"Failed: {(int)response.StatusCode} {response.ReasonPhrase}";
            }
        }
        catch (Exception ex)
        {
            _fetchStatusLabel.ForeColor = Color.Red;
            _fetchStatusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _fetchModelsButton.Enabled = true;
        }
    }

    private async void ShowModelButton_Click(object? sender, EventArgs e)
    {
        string modelId = _remoteModelCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            MessageBox.Show("Enter or select a model first.", "No Model Selected",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string baseUrl = DeriveBaseUrl();
        _showModelButton.Enabled = false;

        try
        {
            using var client = CreateAuthedClient();

            // Try the per-model endpoint first (OpenAI-compatible), fall back to the
            // list endpoint (Ollama and others that don't support GET /v1/models/{id}).
            string modelUrl = baseUrl + "/v1/models/" + Uri.EscapeDataString(modelId);
            using var response = await client.GetAsync(modelUrl);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                using var listResponse = await client.GetAsync(baseUrl + "/v1/models");
                string listBody = await listResponse.Content.ReadAsStringAsync();
                if (listResponse.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(listBody);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idProp)
                                && string.Equals(idProp.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
                            {
                                body = JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true });
                                response.StatusCode = (System.Net.HttpStatusCode)200;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    body = listBody;
                }
            }

            string displayText;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    displayText = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                }
                catch
                {
                    displayText = body;
                }
            }
            else
            {
                displayText = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n\n{body}";
            }

            var dialog = new ModelInfoDialog(modelId, displayText);
            dialog.Show(this.FindForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error fetching model info: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _showModelButton.Enabled = true;
        }
    }

    private void OnnxBrowseButton_Click(object? sender, EventArgs e)
     {
         using var dialog = new OpenFileDialog
         {
             Title = "Select ONNX Model File",
             Filter = "ONNX Model Files (*.onnx)|*.onnx|All Files (*.*)|*.*",
             FilterIndex = 1,
         };

         if (!string.IsNullOrWhiteSpace(_onnxBox.Text) && Directory.Exists(_onnxBox.Text))
             dialog.InitialDirectory = _onnxBox.Text;

         if (dialog.ShowDialog() == DialogResult.OK)
         {
             string? folder = Path.GetDirectoryName(dialog.FileName);
             if (!string.IsNullOrEmpty(folder))
                 _onnxBox.Text = folder;
         }
     }

     private async void TestConnectionButton_Click(object? sender, EventArgs e)
     {
         _testConnectionButton.Enabled = false;
         _fetchStatusLabel.ForeColor = SystemColors.GrayText;
         _fetchStatusLabel.Text = "Testing connectionâ€¦";

         string baseUrl = DeriveBaseUrl();
         string testUrl = baseUrl + "/v1/models";

         try
         {
             using var client = CreateAuthedClient();
             using var response = await client.GetAsync(testUrl, System.Threading.CancellationToken.None);

             if (response.IsSuccessStatusCode)
             {
                 string body = await response.Content.ReadAsStringAsync();
                 _fetchStatusLabel.ForeColor = Color.Green;
                 try
                 {
                     using var doc = JsonDocument.Parse(body);
                          _fetchStatusLabel.Text = $"OK â€” {doc.RootElement.GetProperty("description").GetString() ?? body}";
                 }
                 catch
                 {
                     _fetchStatusLabel.Text = $"OK â€” {body.Substring(0, Math.Min(100, body.Length))}";
                 }
             }
             else
             {
                 _fetchStatusLabel.ForeColor = Color.Red;
                 _fetchStatusLabel.Text = $"Failed: {(int)response.StatusCode} {response.ReasonPhrase}";
             }
         }
         catch (Exception ex)
         {
             _fetchStatusLabel.ForeColor = Color.Red;
             _fetchStatusLabel.Text = $"Error: {ex.Message}";
         }
         finally
         {
             _testConnectionButton.Enabled = true;
         }
     }

     private void WireAutoSave()
     {
         _vectorDatabasePathBox.Validated += (_, _) => SaveSettings();
         _backendCombo.SelectedIndexChanged += (_, _) => SaveSettings();
         _remoteUrlBox.Validated += (_, _) => SaveSettings();
         _remoteModelCombo.TextChanged += (_, _) => SaveSettings();
         _credentialCombo.TextChanged += (_, _) => SaveSettings();
         _timeoutBox.ValueChanged += (_, _) => SaveSettings();
         _parallelismBox.ValueChanged += (_, _) => SaveSettings();
         _onnxBox.Validated += (_, _) => SaveSettings();
         _onnxMaxSeqBox.ValueChanged += (_, _) => SaveSettings();
         _onnxThreadsBox.ValueChanged += (_, _) => SaveSettings();
         _chunkLinesBox.ValueChanged += (_, _) => SaveSettings();
         _overlapBox.ValueChanged += (_, _) => SaveSettings();
         _maxSizeBox.ValueChanged += (_, _) => SaveSettings();
         _topKBox.ValueChanged += (_, _) => SaveSettings();
         _syncBox.ValueChanged += (_, _) => SaveSettings();
         _logLevelCombo.SelectedIndexChanged += (_, _) => SaveSettings();
         _chkSearch.CheckedChanged += (_, _) => SaveSettings();
         _chkIndex.CheckedChanged += (_, _) => SaveSettings();
         _chkSync.CheckedChanged += (_, _) => SaveSettings();
         _chkStatus.CheckedChanged += (_, _) => SaveSettings();
         _chkRemove.CheckedChanged += (_, _) => SaveSettings();
         _chkReindex.CheckedChanged += (_, _) => SaveSettings();
     }

     private void SaveSettings()
     {
         bool isOnnx = _backendCombo.SelectedItem?.ToString() == "Onnx";

         var settings = _module.Settings;
         var oldBackendType = settings.BackendType;
         string oldDbPath = settings.VectorDatabasePath;
         string oldOnnxFolder = settings.OnnxModelFolder;
         int oldSyncInterval = settings.GitSyncIntervalMinutes;
         int oldParallelism = settings.RemoteParallelism;

        settings.BackendType = isOnnx ? BackendType.Onnx : BackendType.Remote;
        settings.RemoteUrl = _remoteUrlBox.Text.Trim();
        settings.RemoteModel = _remoteModelCombo.Text.Trim();
        settings.RemoteCredentialName = _credentialCombo.Text.Trim();
        settings.RemoteTimeoutSeconds = (int)_timeoutBox.Value;
        settings.RemoteParallelism = (int)_parallelismBox.Value;
        settings.OnnxModelFolder = _onnxBox.Text.Trim();
        settings.OnnxMaxSequenceLength = (int)_onnxMaxSeqBox.Value;
        settings.OnnxMaxThreads = (int)_onnxThreadsBox.Value;
        settings.ChunkLines = (int)_chunkLinesBox.Value;
        settings.ChunkOverlapLines = (int)_overlapBox.Value;
        settings.MaxFileSizeKb = (int)_maxSizeBox.Value;
        settings.DefaultTopK = (int)_topKBox.Value;
        settings.GitSyncIntervalMinutes = (int)_syncBox.Value;
        if (Enum.TryParse<CodeVectorMcpLogLevel>(_logLevelCombo.SelectedItem?.ToString(), out var logLevel))
            settings.McpLogLevel = logLevel;
        settings.SearchEnabled = _chkSearch.Checked;
        settings.IndexEnabled = _chkIndex.Checked;
        settings.SyncRepoEnabled = _chkSync.Checked;
        settings.StatusEnabled = _chkStatus.Checked;
        settings.RemoveEnabled = _chkRemove.Checked;
        settings.ReindexEnabled = _chkReindex.Checked;
        settings.VectorDatabasePath = _vectorDatabasePathBox.Text.Trim();

        var repo = _module.Repository;
        repo.SaveSetting("backend_type", settings.BackendType.ToString());
        repo.SaveSetting("remote_url", settings.RemoteUrl);
        repo.SaveSetting("remote_model", settings.RemoteModel);
        repo.SaveSetting("remote_credential", settings.RemoteCredentialName);
        repo.SaveSetting("remote_timeout", settings.RemoteTimeoutSeconds.ToString());
        repo.SaveSetting("remote_parallelism", settings.RemoteParallelism.ToString());
        repo.SaveSetting("onnx_folder", settings.OnnxModelFolder);
        repo.SaveSetting("onnx_max_seq", settings.OnnxMaxSequenceLength.ToString());
        repo.SaveSetting("onnx_threads", settings.OnnxMaxThreads.ToString());
        repo.SaveSetting("chunk_lines", settings.ChunkLines.ToString());
        repo.SaveSetting("chunk_overlap", settings.ChunkOverlapLines.ToString());
        repo.SaveSetting("max_file_kb", settings.MaxFileSizeKb.ToString());
        repo.SaveSetting("default_top_k", settings.DefaultTopK.ToString());
        repo.SaveSetting("sync_interval", settings.GitSyncIntervalMinutes.ToString());
        repo.SaveSetting("log_level", settings.McpLogLevel.ToString());
        repo.SaveSetting("search_enabled", settings.SearchEnabled ? "1" : "0");
        repo.SaveSetting("index_enabled", settings.IndexEnabled ? "1" : "0");
        repo.SaveSetting("sync_enabled", settings.SyncRepoEnabled ? "1" : "0");
        repo.SaveSetting("status_enabled", settings.StatusEnabled ? "1" : "0");
        repo.SaveSetting("remove_enabled", settings.RemoveEnabled ? "1" : "0");
        repo.SaveSetting("reindex_enabled", settings.ReindexEnabled ? "1" : "0");
        repo.SaveSetting("vector_database_path", settings.VectorDatabasePath);

        if (oldDbPath != settings.VectorDatabasePath)
            _module.InvalidateVectorDatabase();

        if (oldBackendType != settings.BackendType || oldOnnxFolder != settings.OnnxModelFolder)
            _module.InvalidateEmbeddingBackend();

        if (oldSyncInterval != settings.GitSyncIntervalMinutes)
            _module.InvalidateMirrorManager();

        if (oldParallelism != settings.RemoteParallelism)
            _module.InvalidateEmbeddingBackend();
    }
}
