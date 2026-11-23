using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog;

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
    private LinkLabel _helpLink = null!;
    private ToolTip _helpTooltip = null!;
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
    private string? _lastQueueSignature;
    private System.Windows.Forms.Timer? _saveDebounceTimer;
    private bool _suppressAutoSave;
    private Label _saveStatusLabel = null!;
    private readonly Dictionary<string, string> _lastSavedSettings = [];
    private FlowLayoutPanel _repoButtonPanel = null!;
    private readonly CancellationTokenSource _repoCts = new();
    private HttpClient? _probeClient;
    private TimeSpan _probeClientTimeout;
    private Font? _errorRowFont;

    public CodeVectorConfigPage(CodeVectorModule module) : base("Code Vector Store")
    {
        _module = module;
        _lastSavedSettings = SettingsToValues(module.Settings);
        BuildUi();
        WireAutoSave();
        WireHelpTooltips();
        UpdateBackendVisibility();
        RefreshRepos();
        _saveDebounceTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _saveDebounceTimer.Tick += (_, _) => { _saveDebounceTimer!.Stop(); SaveSettings(); };
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
        Disposed += (_, _) =>
        {
            _refreshTimer?.Dispose(); _refreshTimer = null;
            _saveDebounceTimer?.Dispose(); _saveDebounceTimer = null;
            _repoCts.Cancel(); _repoCts.Dispose();
            // L4: ToolTips hold native window handles; the probe client holds sockets.
            _statusTooltip?.Dispose();
            _helpTooltip?.Dispose();
            _probeClient?.Dispose(); _probeClient = null;
            _errorRowFont?.Dispose(); _errorRowFont = null;
        };
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
            RowCount = 8,
            Padding = new Padding(14, 8, 14, 8),
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < main.RowCount; row++)
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Header with Help link
        var headerPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerPanel.Controls.Add(new Label { Text = "Code Vector Store", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold), Margin = new Padding(3, 6, 6, 3) }, 0, 0);
        _helpLink = new LinkLabel { Text = "Help", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(6, 6, 3, 3) };
        _helpLink.LinkClicked += HelpLink_Click;
        headerPanel.Controls.Add(_helpLink, 1, 0);
        main.Controls.Add(headerPanel, 0, 0);

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
        main.Controls.Add(databasePanel, 0, 1);

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
        main.Controls.Add(backendPanel, 0, 2);

        // Remote group
        _remoteGroup = BuildRemoteGroup();
        main.Controls.Add(_remoteGroup, 0, 3);

        // ONNX group
        _onnxGroup = BuildOnnxGroup();
        main.Controls.Add(_onnxGroup, 0, 4);

        // General settings
        _generalGroup = BuildGeneralGroup();
        main.Controls.Add(_generalGroup, 0, 5);

        // Git Repos
        _reposGroup = BuildReposGroup();
        main.Controls.Add(_reposGroup, 0, 6);

        // Status
        _statusGroup = BuildStatusGroup();
        main.Controls.Add(_statusGroup, 0, 7);

        Controls.Add(main);
        _saveStatusLabel = new Label
        {
            Text = "",
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 22,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(14, 2, 14, 0),
        };
        Controls.Add(_saveStatusLabel);
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
        try { _credentialCombo.Items.AddRange([.. _module.Secrets.ListCredentialNames()]); }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            // M15: shared-DB contention must degrade gracefully but never silently (project rule).
            Log.Warning(ex, "CodeVector credential list unavailable; combo left empty");
        }
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
        _onnxBrowseButton = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(3) };
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
                   _reposListView.Columns.Add("Source", 260);
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
                   _repoButtonPanel = btnPanel;
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
                       // M13: status polling must not lazily create/start the engine or open the
                       // vector database; show "not started" until something else creates it.
                       var engine = _module.IndexerOrNull;
                       var running = engine?.IsRunning ?? false;
                       var stopReason = _module.EngineStopReason;
                       _engineStatusLabel.Text = running ? "Engine: Running" : (engine is null ? "Engine: Not started" : "Engine: Stopped");
                       _engineStatusLabel.ForeColor = running ? SystemColors.Highlight : SystemColors.GrayText;
                       _statusTooltip.SetToolTip(_engineStatusLabel, running ? "Engine is running" : (stopReason is null ? "Engine is stopped" : $"Engine stopped: {stopReason}"));
                       int queueDepth = engine?.QueueDepth ?? 0;
                       _queueStatusLabel.Text = $"Queue: {queueDepth}";
                       _workersStatusLabel.Text = $"Workers: {engine?.ActiveWorkerCount ?? 0}";
                       var current = engine?.CurrentJob;
                       _currentStatusLabel.Text = current is null ? "Current: —" : $"Current: {current.Path}";

                       _startEngineButton.Enabled = !running;
                       _stopEngineButton.Enabled = running;
                       _clearQueueButton.Enabled = queueDepth > 0;

                       var activity = _module.Activity;
                       _logSummaryLabel.Text = $"Logged: {activity.TotalLogged} | Errors: {activity.ErrorCount}";

                       // M13: rebuild queue rows only when the snapshot actually changed.
                       var snapshot = engine?.GetQueueSnapshot() ?? [];
                       string signature = string.Join('\n', snapshot.Select(i => $"{i.Operation}|{i.Collection}|{i.Path}|{i.Source}"));
                       if (signature != _lastQueueSignature)
                       {
                           _lastQueueSignature = signature;
                           _queueListView.BeginUpdate();
                           _queueListView.Items.Clear();
                           foreach (var item in snapshot)
                           {
                               var lvi = new ListViewItem(item.Operation);
                               lvi.SubItems.Add(item.Collection);
                               lvi.SubItems.Add(item.Path);
                               lvi.SubItems.Add(item.Source);
                               _queueListView.Items.Add(lvi);
                           }
                           _queueListView.EndUpdate();
                       }

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
                               if (entry.Operation == "error")
                               {
                                   // L3: theme-safe emphasis (bold) instead of a fixed red that
                                   // can be illegible on dark/high-contrast backgrounds.
                                   _errorRowFont ??= new Font(_logListView.Font, FontStyle.Bold);
                                   lvi.Font = _errorRowFont;
                               }
                               _logListView.Items.Add(lvi);
                           }
                           _logListView.EndUpdate();
                       }
                   }
                   catch (Exception ex)
                   {
                       _engineStatusLabel.Text = $"Status error: {ex.Message}";
                       _engineStatusLabel.ForeColor = SystemColors.ControlText;
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
                       _module.IndexerOrNull?.ClearQueue();
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
                           lvi.SubItems.Add(m.IsLocalDirectory ? (m.SourcePath ?? "") : UrlRedactor.Redact(m.RemoteUrl));
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
                   catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
                   {
                       // M15: another instance may hold the shared DB — warn and show a hint
                       // instead of silently leaving the list empty.
                       Log.Warning(ex, "CodeVector repo list unavailable (shared database busy?)");
                       _reposListView.Items.Clear();
                       _reposListView.Items.Add(new ListViewItem("(unavailable — database busy; see log)"));
                       _reposListView.EndUpdate();
                   }
               }

               private MirrorRegistration? GetSelectedRepo()
                   => _reposListView.SelectedItems.Count > 0 ? _reposListView.SelectedItems[0].Tag as MirrorRegistration : null;

               /// <summary>Resolves the effective source (git URL or local directory path) and kind for a mirror dialog.</summary>
               private static (string Kind, string Source) ResolveMirrorSource(RepoDialog dlg)
                   => dlg.IsLocalDirectory
                       ? (MirrorRegistration.SourceKindDir, dlg.DirectoryPath!)
                       : (MirrorRegistration.SourceKindGit, dlg.RemoteUrl);

               private async void AddRepoButton_Click(object? sender, EventArgs e)
               {
                   using var dlg = new RepoDialog(null);
                   if (dlg.ShowDialog(this.FindForm()) != DialogResult.OK) return;
                   if (!_repoButtonPanel.Enabled) return;
                   _repoButtonPanel.Enabled = false;
                   try
                   {
                       var (kind, source) = ResolveMirrorSource(dlg);
                       // H3: await the full registration (including initial clone/sync) so failures
                       // are observed here instead of becoming unobserved task exceptions.
                       await _module.MirrorManager.RegisterMirrorAsync(dlg.CollectionName, source, dlg.Branch, dlg.CredentialName, _repoCts.Token, dlg.MirrorPath, dlg.PathPrefix, kind, dlg.IsLocalDirectory ? dlg.DirectoryPath : null);
                       if (!IsDisposed) RefreshRepos();
                   }
                   catch (OperationCanceledException) { }
                   catch (Exception ex)
                   {
                       _module.Activity.Log("error", dlg.CollectionName, $"Add repo failed: {ex}");
                       MessageBox.Show(ex.Message, "Add Repo Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                       if (!IsDisposed) RefreshRepos();
                   }
                   finally { if (!IsDisposed) _repoButtonPanel.Enabled = true; }
               }

               private async void EditRepoButton_Click(object? sender, EventArgs e)
               {
                   var m = GetSelectedRepo();
                   if (m is null) { MessageBox.Show("Select a repo first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                   using var dlg = new RepoDialog(m);
                   if (dlg.ShowDialog(this.FindForm()) != DialogResult.OK) return;
                   if (!_repoButtonPanel.Enabled) return;
                   _repoButtonPanel.Enabled = false;
                   try
                   {
                       var (kind, source) = ResolveMirrorSource(dlg);
                       await _module.MirrorManager.RegisterMirrorAsync(dlg.CollectionName, source, dlg.Branch, dlg.CredentialName, _repoCts.Token, dlg.MirrorPath, dlg.PathPrefix, kind, dlg.IsLocalDirectory ? dlg.DirectoryPath : null);
                       // Upsert overwrites a same-name row; only delete the old row on rename, and
                       // only after the new registration succeeded so a failure cannot lose the mirror.
                       if (!string.Equals(m.CollectionName, dlg.CollectionName, StringComparison.Ordinal))
                       {
                           _module.Repository.DeleteMirror(m.CollectionName);
                           OfferOrphanCleanup(m);
                       }
                       if (!IsDisposed) RefreshRepos();
                   }
                   catch (OperationCanceledException) { }
                   catch (Exception ex)
                   {
                       _module.Activity.Log("error", m.CollectionName, $"Edit repo failed: {ex}");
                       MessageBox.Show(ex.Message, "Edit Repo Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                       if (!IsDisposed) RefreshRepos();
                   }
                   finally { if (!IsDisposed) _repoButtonPanel.Enabled = true; }
               }

               private void RemoveRepoButton_Click(object? sender, EventArgs e)
               {
                   var m = GetSelectedRepo();
                   if (m is null) { MessageBox.Show("Select a repo first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                   if (MessageBox.Show($"Remove '{m.CollectionName}'?", "Confirm Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                   {
                       try
                       {
                           _module.Repository.DeleteMirror(m.CollectionName);
                           OfferOrphanCleanup(m);
                           RefreshRepos();
                       }
                       catch (Exception ex)
                       {
                           // M15: a shared-DB failure must not crash the UI.
                           Log.Warning(ex, "CodeVector remove mirror failed ({Collection})", m.CollectionName);
                           MessageBox.Show(ex.Message, "Remove Repo Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                       }
                   }
               }

               /// <summary>
               /// L10: after a rename/remove, offer to delete the orphaned indexed collection and
               /// (for git mirrors) the on-disk clone so stale data doesn't accumulate invisibly.
               /// Never touches a local-directory source — that is the user's own data.
               /// </summary>
               private void OfferOrphanCleanup(MirrorRegistration mirror)
               {
                   try
                   {
                       if (MessageBox.Show($"Also delete the indexed collection '{mirror.CollectionName}' (all files, chunks and embeddings)?",
                               "Delete Indexed Data", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                           _module.VectorDb.DeleteCollection(mirror.CollectionName);
                       if (!mirror.IsLocalDirectory)
                       {
                           string dir = _module.MirrorManager.GetMirrorDirectory(mirror);
                           if (Directory.Exists(dir)
                               && MessageBox.Show($"Also delete the mirror clone folder?\n{dir}", "Delete Mirror Folder", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                               Directory.Delete(dir, recursive: true);
                       }
                   }
                   catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
                   {
                       Log.Warning(ex, "CodeVector orphan cleanup failed ({Collection})", mirror.CollectionName);
                       MessageBox.Show(ex.Message, "Cleanup Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                           await _module.MirrorManager.IndexMirrorFilesAsync(m, _repoCts.Token);
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
                           await _module.MirrorManager.SyncMirrorAsync(m, _repoCts.Token);
                           if (!IsDisposed) RefreshRepos();
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
                   sb.AppendLine($"Source: {m.DescribeSource}");
                   sb.AppendLine($"Last Sync: {m.LastSyncUtc ?? "never"}");
                   sb.AppendLine($"Sync Status: {m.LastSyncStatus ?? "pending"}");
                   if (col is not null) sb.AppendLine($"Indexed: {col.FileCount} files, {col.ChunkCount} chunks");
                   sb.AppendLine();
                   var engine = _module.IndexerOrNull;
                   sb.AppendLine($"Engine: {(engine?.IsRunning == true ? "Running" : "Stopped")} | Queue: {engine?.QueueDepth ?? 0}");
                   MessageBox.Show(sb.ToString(), $"Status — {m.CollectionName}", MessageBoxButtons.OK, MessageBoxIcon.Information);
               }

               private async void ReindexRepoButton_Click(object? sender, EventArgs e)
               {
                   if (GetSelectedRepo() is not { } m) { RequireRepo(out _); return; }
                   try
                   {
                       // Reindex = clear the collection, then re-walk the mirror and re-enqueue all files.
                           _module.VectorDb.DeleteCollection(m.CollectionName);
                           await _module.MirrorManager.IndexMirrorFilesAsync(m, _repoCts.Token);
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

        string trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"/v1/embeddings".Length];
        // L2: a base URL already ending in /v1 must not gain a second /v1 segment.
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"/v1".Length];

        return trimmed;
    }

    private HttpClient CreateAuthedClient()
    {
        // L4: reuse one client for the rare probe clicks instead of churning sockets per
        // click. Recreated only when the timeout changes (Timeout is immutable after use).
        var timeout = TimeSpan.FromSeconds((double)_timeoutBox.Value);
        if (_probeClient is null || _probeClientTimeout != timeout)
        {
            _probeClient?.Dispose();
            _probeClient = new HttpClient { Timeout = timeout };
            _probeClientTimeout = timeout;
        }
        _probeClient.DefaultRequestHeaders.Authorization = null;
        string credentialName = _credentialCombo.Text.Trim();
        if (!string.IsNullOrWhiteSpace(credentialName))
        {
            string? secret = _module.Secrets.ResolveSecret(credentialName);
            if (!string.IsNullOrWhiteSpace(secret))
                _probeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        return _probeClient;
    }

    private async void FetchModelsButton_Click(object? sender, EventArgs e)
    {
        string modelsUrl = DeriveBaseUrl() + "/v1/models";

        _fetchModelsButton.Enabled = false;
        _fetchStatusLabel.ForeColor = SystemColors.GrayText;
        _fetchStatusLabel.Text = "Fetching models…";

        try
        {
            var client = CreateAuthedClient();
            using var response = await client.GetAsync(modelsUrl);
            string body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    string? currentSelection = _remoteModelCombo.Text;
                    var modelIds = new List<string>();

                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idProp) && idProp.GetString() is string id)
                            modelIds.Add(id);
                    }

                    // H7: populating the combo fires TextChanged per item; suppress the autosave
                    // storm and schedule a single save afterwards.
                    _suppressAutoSave = true;
                    try
                    {
                        _remoteModelCombo.Items.Clear();
                        _remoteModelCombo.Items.AddRange([.. modelIds]);

                        if (modelIds.Contains(currentSelection))
                            _remoteModelCombo.Text = currentSelection;
                        else if (modelIds.Count > 0)
                            _remoteModelCombo.SelectedIndex = 0;
                    }
                    finally { _suppressAutoSave = false; }
                    ScheduleSave();

                    _fetchStatusLabel.ForeColor = SystemColors.Highlight;
                    _fetchStatusLabel.Text = $"Found {modelIds.Count} model(s)";
                }
                else
                {
                    _fetchStatusLabel.ForeColor = SystemColors.ControlText;
                    _fetchStatusLabel.Text = $"OK but unexpected response format";
                }
            }
            else
            {
                _fetchStatusLabel.ForeColor = SystemColors.ControlText;
                _fetchStatusLabel.Text = $"Failed: {(int)response.StatusCode} {response.ReasonPhrase}";
            }
        }
        catch (Exception ex)
        {
            _fetchStatusLabel.ForeColor = SystemColors.ControlText;
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
            var client = CreateAuthedClient();

            // Try the per-model endpoint first (OpenAI-compatible), fall back to the
            // list endpoint (Ollama and others that don't support GET /v1/models/{id}).
            string modelUrl = baseUrl + "/v1/models/" + Uri.EscapeDataString(modelId);
            using var response = await client.GetAsync(modelUrl);
            string body = await response.Content.ReadAsStringAsync();
            // L9: track success locally instead of mutating the response's StatusCode.
            bool ok = response.IsSuccessStatusCode;

            if (!ok)
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
                                ok = true;
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
            if (ok)
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
         _fetchStatusLabel.Text = "Testing connection…";

         string baseUrl = DeriveBaseUrl();
         string testUrl = baseUrl + "/v1/models";

         try
         {
             var client = CreateAuthedClient();
             using var response = await client.GetAsync(testUrl, System.Threading.CancellationToken.None);

             if (response.IsSuccessStatusCode)
             {
                 string body = await response.Content.ReadAsStringAsync();
                 _fetchStatusLabel.ForeColor = SystemColors.Highlight;
                 try
                 {
                     using var doc = JsonDocument.Parse(body);
                          _fetchStatusLabel.Text = $"OK — {doc.RootElement.GetProperty("description").GetString() ?? body}";
                 }
                 catch
                 {
                     _fetchStatusLabel.Text = $"OK — {body[..Math.Min(100, body.Length)]}";
                 }
             }
             else
             {
                 _fetchStatusLabel.ForeColor = SystemColors.ControlText;
                 _fetchStatusLabel.Text = $"Failed: {(int)response.StatusCode} {response.ReasonPhrase}";
             }
         }
         catch (Exception ex)
         {
             _fetchStatusLabel.ForeColor = SystemColors.ControlText;
             _fetchStatusLabel.Text = $"Error: {ex.Message}";
         }
         finally
         {
             _testConnectionButton.Enabled = true;
         }
     }

     private void WireHelpTooltips()
     {
         _helpTooltip = new ToolTip();
         SetHelpTip(_vectorDatabasePathBox, "Vector Database");
         SetHelpTip(_backendCombo, "Backend");
         SetHelpTip(_remoteUrlBox, "Remote URL");
         SetHelpTip(_fetchModelsButton, "Fetch Models");
         SetHelpTip(_remoteModelCombo, "Remote Model");
         SetHelpTip(_showModelButton, "Model Info");
         SetHelpTip(_testConnectionButton, "Test Connection");
         SetHelpTip(_credentialCombo, "Credential");
         SetHelpTip(_timeoutBox, "Timeout (s)");
         SetHelpTip(_parallelismBox, "Parallelism");
         SetHelpTip(_onnxBox, "ONNX Model Folder");
         SetHelpTip(_onnxMaxSeqBox, "ONNX Max Sequence");
         SetHelpTip(_onnxThreadsBox, "ONNX Threads");
         SetHelpTip(_chunkLinesBox, "Chunk Lines");
         SetHelpTip(_overlapBox, "Overlap Lines");
         SetHelpTip(_maxSizeBox, "Max File (KB)");
         SetHelpTip(_topKBox, "Default Top K");
         SetHelpTip(_syncBox, "Git Sync (min)");
         SetHelpTip(_logLevelCombo, "Log Level");
         SetHelpTip(_chkSearch, "Tools");
         SetHelpTip(_chkIndex, "Tools");
         SetHelpTip(_chkSync, "Tools");
         SetHelpTip(_chkStatus, "Tools");
         SetHelpTip(_chkRemove, "Tools");
         SetHelpTip(_chkReindex, "Tools");
     }

     private void SetHelpTip(Control control, string setting)
     {
         var description = CodeVectorSettingsHelp.Describe(setting);
         if (description is not null)
             _helpTooltip.SetToolTip(control, description);
     }

     private void HelpLink_Click(object? sender, LinkLabelLinkClickedEventArgs e)
     {
         using var dlg = new CodeVectorSettingsHelpDialog();
         dlg.ShowDialog(FindForm());
     }

     private void WireAutoSave()
     {
         _vectorDatabasePathBox.Validated += (_, _) => ScheduleSave();
         _backendCombo.SelectedIndexChanged += (_, _) => ScheduleSave();
         _remoteUrlBox.Validated += (_, _) => ScheduleSave();
         _remoteModelCombo.TextChanged += (_, _) => ScheduleSave();
         _credentialCombo.TextChanged += (_, _) => ScheduleSave();
         _timeoutBox.ValueChanged += (_, _) => ScheduleSave();
         _parallelismBox.ValueChanged += (_, _) => ScheduleSave();
         _onnxBox.Validated += (_, _) => ScheduleSave();
         _onnxMaxSeqBox.ValueChanged += (_, _) => ScheduleSave();
         _onnxThreadsBox.ValueChanged += (_, _) => ScheduleSave();
         _chunkLinesBox.ValueChanged += (_, _) => ScheduleSave();
         _overlapBox.ValueChanged += (_, _) => ScheduleSave();
         _maxSizeBox.ValueChanged += (_, _) => ScheduleSave();
         _topKBox.ValueChanged += (_, _) => ScheduleSave();
         _syncBox.ValueChanged += (_, _) => ScheduleSave();
         _logLevelCombo.SelectedIndexChanged += (_, _) => ScheduleSave();
         _chkSearch.CheckedChanged += (_, _) => ScheduleSave();
         _chkIndex.CheckedChanged += (_, _) => ScheduleSave();
         _chkSync.CheckedChanged += (_, _) => ScheduleSave();
         _chkStatus.CheckedChanged += (_, _) => ScheduleSave();
         _chkRemove.CheckedChanged += (_, _) => ScheduleSave();
         _chkReindex.CheckedChanged += (_, _) => ScheduleSave();
     }

     private void ScheduleSave()
     {
         if (_suppressAutoSave) return;
         // H7: debounce so a burst of keystrokes / spinner clicks produces a single save.
         _saveDebounceTimer?.Stop();
         _saveDebounceTimer?.Start();
     }

     private static Dictionary<string, string> SettingsToValues(CodeVectorSettings s) => new()
     {
         ["backend_type"] = s.BackendType.ToString(),
         ["remote_url"] = s.RemoteUrl,
         ["remote_model"] = s.RemoteModel,
         ["remote_credential"] = s.RemoteCredentialName,
         ["remote_timeout"] = s.RemoteTimeoutSeconds.ToString(),
         ["remote_parallelism"] = s.RemoteParallelism.ToString(),
         ["onnx_folder"] = s.OnnxModelFolder,
         ["onnx_max_seq"] = s.OnnxMaxSequenceLength.ToString(),
         ["onnx_threads"] = s.OnnxMaxThreads.ToString(),
         ["chunk_lines"] = s.ChunkLines.ToString(),
         ["chunk_overlap"] = s.ChunkOverlapLines.ToString(),
         ["max_file_kb"] = s.MaxFileSizeKb.ToString(),
         ["default_top_k"] = s.DefaultTopK.ToString(),
         ["sync_interval"] = s.GitSyncIntervalMinutes.ToString(),
         ["log_level"] = s.McpLogLevel.ToString(),
         ["search_enabled"] = s.SearchEnabled ? "1" : "0",
         ["index_enabled"] = s.IndexEnabled ? "1" : "0",
         ["sync_enabled"] = s.SyncRepoEnabled ? "1" : "0",
         ["status_enabled"] = s.StatusEnabled ? "1" : "0",
         ["remove_enabled"] = s.RemoveEnabled ? "1" : "0",
         ["reindex_enabled"] = s.ReindexEnabled ? "1" : "0",
         ["vector_database_path"] = s.VectorDatabasePath,
     };

     private void ShowSaveError(string message)
     {
         if (IsDisposed) return;
         _saveStatusLabel.ForeColor = SystemColors.ControlText;
         _saveStatusLabel.Text = message;
     }

     private async void SaveSettings()
     {
         try
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

        // H7: write only the keys whose value actually changed since the last successful save.
        var repo = _module.Repository;
        int savedCount = 0;
        foreach (var (key, value) in SettingsToValues(settings))
        {
            if (_lastSavedSettings.TryGetValue(key, out var previous) && previous == value) continue;
            repo.SaveSetting(key, value);
            _lastSavedSettings[key] = value;
            savedCount++;
        }

        if (oldDbPath != settings.VectorDatabasePath)
            await _module.InvalidateVectorDatabaseAsync();

        if (oldBackendType != settings.BackendType || oldOnnxFolder != settings.OnnxModelFolder)
            await _module.InvalidateEmbeddingBackendAsync();

        if (oldSyncInterval != settings.GitSyncIntervalMinutes)
            await _module.InvalidateMirrorManagerAsync();

        if (oldParallelism != settings.RemoteParallelism)
            await _module.RestartEngineAsync("parallelism changed");

        if (savedCount > 0 && !IsDisposed)
        {
            _saveStatusLabel.ForeColor = SystemColors.GrayText;
            _saveStatusLabel.Text = "Settings saved.";
        }
         }
         catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
         {
             Log.Warning(ex, "CodeVector settings save failed (database busy); change not persisted.");
             ShowSaveError("Save failed: database busy. Change not persisted.");
         }
         catch (Exception ex)
         {
             Log.Warning(ex, "CodeVector settings save failed.");
             ShowSaveError($"Save failed: {ex.Message}");
         }
    }
}
