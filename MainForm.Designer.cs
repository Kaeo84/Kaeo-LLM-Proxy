namespace Kaeo.LlmProxy;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        _tabControl = new TabControl();
        _tabDashboard = new TabPage();
        _tabLogs = new TabPage();
        _tabSettings = new TabPage();
        _tabInstructions = new TabPage();
        _tabCredentials = new TabPage();
        _tabModules = new TabPage();
        _tabMcp = new TabPage();
        _mcpSubTabs = new TabControl();
        _mcpServerPage = new TabPage();
        _tlpMcp = new TableLayoutPanel();
        _chkMcpEnabled = new CheckBox();
        _chkMcpApiExplorer = new CheckBox();
        _chkMcpCollectRequest = new CheckBox();
        _chkMcpCollectResponse = new CheckBox();
        _lblMcpApiExplorerUrl = new Label();
        _lblMcpSpecUrl = new Label();
        _lblMcpListenAddress = new Label();
        _cboMcpListenAddress = new ComboBox();
        _lblMcpPort = new Label();
        _nudMcpPort = new NumericUpDown();
        _lblMcpStatusCaption = new Label();
        _lblMcpStatus = new Label();
        _flpMcpButtons = new FlowLayoutPanel();
        _btnMcpApply = new Button();
        _tabTest = new TabPage();
        _tabHeartbeats = new TabPage();
        _tabSysLogs = new TabPage();
        _tlpSysLogs = new TableLayoutPanel();
        _flpSysLogTop = new FlowLayoutPanel();
        _clbSysLogLevel = new MultiSelectDropDown();
        _lblSysLogLevel = new Label();
        _lblSysLogFilter = new Label();
        _txtSysLogFilter = new TextBox();
        _lstSysLogs = new ListView();
        _colSysLogTime = new ColumnHeader();
        _colSysLogLevel = new ColumnHeader();
        _colSysLogMsg = new ColumnHeader();
        _colSysLogSrc = new ColumnHeader();
        _lblSysLogStatus = new Label();
        _tabHelp = new TabPage();
        _helpTabs = new TabControl();

        // Dashboard controls
        _dashScrollPanel = new Panel();
        _tlpDashboard = new TableLayoutPanel();
        _grpStatus = new GroupBox();
        _tlpStatus = new TableLayoutPanel();
        _flpStatusButtons = new FlowLayoutPanel();
        _lblStatusValue = new Label();
        _lblStatus = new Label();
        _lblStatusAddressCaption = new Label();
        _lblStatusAddressValue = new Label();
        _lblStatusPortCaption = new Label();
        _lblStatusPortValue = new Label();
        _btnStart = new Button();
        _btnStop = new Button();
        _btnRestart = new Button();

        // Dashboard MCP status controls
        _grpDashMcp = new GroupBox();
        _tlpDashMcp = new TableLayoutPanel();
        _lblDashMcpStatusCaption = new Label();
        _lblDashMcpStatusValue = new Label();
        _lblDashMcpAddressCaption = new Label();
        _lblDashMcpAddressValue = new Label();
        _lblDashMcpPortCaption = new Label();
        _lblDashMcpPortValue = new Label();
        _flpDashMcpButtons = new FlowLayoutPanel();
        _btnDashMcpStart = new Button();
        _btnDashMcpStop = new Button();
        _btnDashMcpRestart = new Button();

        // Stats panel
        _tlpStats = new TableLayoutPanel();
        _lblTotalRequestsCaption = new Label();
        _lblTotalRequestsValue = new Label();
        _lblTotalErrorsCaption = new Label();
        _lblTotalErrorsValue = new Label();
        _lblPromptTokensCaption = new Label();
        _lblPromptTokensValue = new Label();
        _lblCompletionTokensCaption = new Label();
        _lblCompletionTokensValue = new Label();
        _lblRpsCaption = new Label();
        _lblRpsValue = new Label();
        _btnResetStats = new Button();

        // MCP stats panel
        _tlpMcpStats = new TableLayoutPanel();
        _lblMcpTotalRequestsCaption = new Label();
        _lblMcpTotalRequestsValue = new Label();
        _lblMcpTotalErrorsCaption = new Label();
        _lblMcpTotalErrorsValue = new Label();
        _lblMcpRpsCaption = new Label();
        _lblMcpRpsValue = new Label();
        _btnResetMcpStats = new Button();

        // Performance panel
        _grpPerf = new GroupBox();
        _tlpPerf = new TableLayoutPanel();
        _lblCpuCaption = new Label();
        _lblCpuValue = new Label();
        _lblRamCaption = new Label();
        _lblRamValue = new Label();

        // Logs controls
        _tlpLogs = new TableLayoutPanel();
        _flpLogsButtons = new FlowLayoutPanel();
        _lstLogs = new ListView();
        _colTime = new ColumnHeader();
        _colMethod = new ColumnHeader();
        _colPath = new ColumnHeader();
        _colModel = new ColumnHeader();
        _colStatus = new ColumnHeader();
        _colDuration = new ColumnHeader();
        _colPromptTokens = new ColumnHeader();
        _colCompletionTokens = new ColumnHeader();
        _colReasoningTokens = new ColumnHeader();
        _colCachedTokens = new ColumnHeader();
        _colDraftAccept = new ColumnHeader();
        _colBytes = new ColumnHeader();
        _logSubTabs = new TabControl();
        _logProxyPage = new TabPage();
        _tlpProxyLogs = new TableLayoutPanel();
        _flpProxyLogTop = new FlowLayoutPanel();
        _lblProxyLogFilter = new Label();
        _txtProxyLogFilter = new TextBox();
        _logMcpPage = new TabPage();
        _tlpMcpLogs = new TableLayoutPanel();
        _flpMcpLogTop = new FlowLayoutPanel();
        _lblMcpLogFilter = new Label();
        _txtMcpLogFilter = new TextBox();
        _logNonProxiedPage = new TabPage();
        _tlpNonProxiedLogs = new TableLayoutPanel();
        _flpNonProxiedLogTop = new FlowLayoutPanel();
        _lblNonProxiedLogFilter = new Label();
        _txtNonProxiedLogFilter = new TextBox();
        _lstNonProxiedLogs = new ListView();
        _colNpTime = new ColumnHeader();
        _colNpMethod = new ColumnHeader();
        _colNpPath = new ColumnHeader();
        _colNpStatus = new ColumnHeader();
        _colNpDuration = new ColumnHeader();
        _colNpBytes = new ColumnHeader();
        _lstMcpLogs = new ListView();
        _colMcpTime = new ColumnHeader();
        _colMcpMethod = new ColumnHeader();
        _colMcpPath = new ColumnHeader();
        _colMcpStatus = new ColumnHeader();
        _colMcpDuration = new ColumnHeader();
        _colMcpBytes = new ColumnHeader();
        _chkAutoRefresh = new CheckBox();
        _lblRefreshInterval = new Label();
        _cmbRefreshInterval = new ComboBox();
        _btnRefreshLogs = new Button();
        _btnClearLogs = new Button();
        _btnLogDetails = new Button();

        // Settings controls
        _tlpSettings = new TableLayoutPanel();
        _lblListenPort = new Label();
        _txtListenPort = new TextBox();
        _lblListenAddress = new Label();
        _cmbListenAddress = new ComboBox();
        _lblMaxLogs = new Label();
        _txtMaxLogs = new TextBox();
        _lblCompactionFallback = new Label();
        _txtCompactionFallback = new TextBox();
        _lblMappings = new Label();
        _dgvMappings = new DataGridView();
        _colMappingEnabled = new DataGridViewTextBoxColumn();
        _colProxyName = new DataGridViewTextBoxColumn();
        _colModelName = new DataGridViewTextBoxColumn();
        _colInstructionSet = new DataGridViewTextBoxColumn();
        _colReasoningEffort = new DataGridViewTextBoxColumn();
        _colVision = new DataGridViewTextBoxColumn();
        _colCompactionTarget = new DataGridViewTextBoxColumn();
        _colCompactionRedirect = new DataGridViewTextBoxColumn();
        _grpListener = new GroupBox();
        _tlpListener = new TableLayoutPanel();
        _btnSaveListener = new Button();
        _flpMappingButtons = new FlowLayoutPanel();
        _btnAddMapping = new Button();
        _btnRemoveMapping = new Button();
        _btnDuplicateMapping = new Button();
        _btnConfigureMapping = new Button();
        _btnToggleEnabled = new Button();
        _chkAutoStart = new CheckBox();
        _chkStartWithDashboard = new CheckBox();
        _chkRunAsAdmin = new CheckBox();
        _chkCollectDetails = new CheckBox();
        _chkCollectResponseDetails = new CheckBox();
        _chkDebugMode = new CheckBox();
        _chkIrTranslation = new CheckBox();
        _grpNonProxiedCapture = new GroupBox();
        _tlpNonProxiedCapture = new TableLayoutPanel();
        _lblNonProxiedCaptureIntro = new Label();
        _chkCollectHealthProbes = new CheckBox();
        _chkCollectVersionExplorer = new CheckBox();
        _chkCollectCorsPreflight = new CheckBox();
        _chkCollectLocalStubs = new CheckBox();
        _chkCollectRejectedRequests = new CheckBox();
        _lblCollectNonProxiedWarning = new Label();
        _chkPerformanceSampling = new CheckBox();
        _chkApiExplorer = new CheckBox();
        _lblApiExplorerUrl = new Label();
        _lblApiSpecUrl = new Label();

        _grpLogging = new GroupBox();
        _tlpLogging = new TableLayoutPanel();
        _lblLogDir = new Label();
        _txtLogDir = new TextBox();
        _lblMinLevel = new Label();
        _cmbMinLevel = new ComboBox();
        _lblAppLogSize = new Label();
        _txtAppLogSize = new TextBox();
        _lblAppLogRetain = new Label();
        _txtAppLogRetain = new TextBox();
        _lblReqLogSize = new Label();
        _txtReqLogSize = new TextBox();
        _lblRequestDbPath = new Label();
        _tlpRequestDbPath = new TableLayoutPanel();
        _txtRequestDbPath = new TextBox();
        _btnBrowseRequestDb = new Button();
        _lblLogRetention = new Label();
        _txtLogRetention = new TextBox();

        _refreshTimer = new System.Windows.Forms.Timer(components);

        // Instructions tab controls
        _tlpInstructions = new TableLayoutPanel();
        _lstInstructions = new ListView();
        _colInstrName = new ColumnHeader();
        _colInstrDescription = new ColumnHeader();
        _flpInstructionButtons = new FlowLayoutPanel();
        _btnAddInstruction = new Button();
        _btnEditInstruction = new Button();
        _btnRemoveInstruction = new Button();
        _txtInstructionPreview = new TextBox();
        _lblInstructionPreview = new Label();

        // Credentials tab controls
        _tlpCredentials = new TableLayoutPanel();
        _lstCredentials = new ListView();
        _colCredName = new ColumnHeader();
        _colCredDescription = new ColumnHeader();
        _flpCredentialButtons = new FlowLayoutPanel();
        _btnAddCredential = new Button();
        _btnEditCredential = new Button();
        _btnRemoveCredential = new Button();

        // Modules tab controls
        _tlpModules = new TableLayoutPanel();
        _lblModulesNote = new Label();
        _lstModules = new ListView();
        _colModuleName = new ColumnHeader();
        _colModuleVersion = new ColumnHeader();
        _colModuleState = new ColumnHeader();
        _colModulePath = new ColumnHeader();
        _lblModuleStatus = new Label();
        _flpModuleButtons = new FlowLayoutPanel();
        _btnImportModule = new Button();
        _btnToggleModule = new Button();
        _btnRemoveModule = new Button();

        // Test Console controls
        _tlpTestOuter = new TableLayoutPanel();
        _tlpTestTop = new TableLayoutPanel();
        _lblTestModel = new Label();
        _cmbTestModel = new ComboBox();
        _lblTestTemp = new Label();
        _nudTestTemp = new NumericUpDown();
        _lblTestRepeatPenalty = new Label();
        _nudTestRepeatPenalty = new NumericUpDown();
        _btnTestSend = new Button();
        _btnTestCancel = new Button();
        _btnTestClear = new Button();
        _txtTestPrompt = new TextBox();
        _txtTestResponse = new TextBox();
        _lblTestStatus = new Label();

        // Heartbeats tab controls
        _tlpHeartbeats = new TableLayoutPanel();
        _lblHeartbeatInterval = new Label();
        _txtHeartbeatInterval = new TextBox();
        _grpPings = new GroupBox();
        _lstPings = new ListView();
        _colPingModel = new ColumnHeader();
        _colPingEnabled = new ColumnHeader();
        _colPingStatus = new ColumnHeader();
        _colPingAttempts = new ColumnHeader();
        _colPingFailures = new ColumnHeader();
        _colPingLast = new ColumnHeader();
        _colPingError = new ColumnHeader();
        _grpKeepAlives = new GroupBox();
        _lstKeepAlives = new ListView();
        _colKaModel = new ColumnHeader();
        _colKaEnabled = new ColumnHeader();
        _colKaSent = new ColumnHeader();
        _colKaLastSent = new ColumnHeader();
        _flpHeartbeatButtons = new FlowLayoutPanel();
        _btnResetCounters = new Button();
        _btnSaveHeartbeats = new Button();

        // Streaming Keep-Alive group (Settings tab)
        _grpSseKeepAlive = new GroupBox();
        _tlpSseKeepAlive = new TableLayoutPanel();
        _chkSseKeepAlive = new CheckBox();
        _lblKeepAliveInterval = new Label();
        _txtKeepAliveInterval = new TextBox();

        _grpListener.SuspendLayout();
        _tlpListener.SuspendLayout();
        _grpLogging.SuspendLayout();
        _tlpLogging.SuspendLayout();
        _grpPerf.SuspendLayout();
        _tlpPerf.SuspendLayout();
        _tlpMcpStats.SuspendLayout();
        _dashScrollPanel.SuspendLayout();
        _tlpDashboard.SuspendLayout();
        _tabControl.SuspendLayout();
        _tabDashboard.SuspendLayout();
        _tabLogs.SuspendLayout();
        _logSubTabs.SuspendLayout();
        _logProxyPage.SuspendLayout();
        _tlpProxyLogs.SuspendLayout();
        _flpProxyLogTop.SuspendLayout();
        _logMcpPage.SuspendLayout();
        _tlpMcpLogs.SuspendLayout();
        _flpMcpLogTop.SuspendLayout();
        _logNonProxiedPage.SuspendLayout();
        _tlpNonProxiedLogs.SuspendLayout();
        _flpNonProxiedLogTop.SuspendLayout();
        _tlpLogs.SuspendLayout();
        _flpLogsButtons.SuspendLayout();
        _tabSettings.SuspendLayout();
        _tabCredentials.SuspendLayout();
        _tlpCredentials.SuspendLayout();
        _flpCredentialButtons.SuspendLayout();
        _tabModules.SuspendLayout();
        _tlpModules.SuspendLayout();
        _flpModuleButtons.SuspendLayout();
        _mcpSubTabs.SuspendLayout();
        _mcpServerPage.SuspendLayout();
        _tabMcp.SuspendLayout();
        _tlpMcp.SuspendLayout();
        _flpMcpButtons.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_nudMcpPort).BeginInit();
        _tabTest.SuspendLayout();
        _tlpTestOuter.SuspendLayout();
        _tlpTestTop.SuspendLayout();
        _tabHeartbeats.SuspendLayout();
        _grpPings.SuspendLayout();
        _grpKeepAlives.SuspendLayout();
        _grpSseKeepAlive.SuspendLayout();
        _tlpSseKeepAlive.SuspendLayout();
        _tabSysLogs.SuspendLayout();
        _tlpSysLogs.SuspendLayout();
        _flpSysLogTop.SuspendLayout();
        _grpNonProxiedCapture.SuspendLayout();
        _tlpNonProxiedCapture.SuspendLayout();
        _tabHelp.SuspendLayout();
        _helpTabs.SuspendLayout();
        _tlpHeartbeats.SuspendLayout();
        _flpHeartbeatButtons.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_nudTestTemp).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_nudTestRepeatPenalty).BeginInit();
        _grpStatus.SuspendLayout();
        _tlpStatus.SuspendLayout();
        _grpDashMcp.SuspendLayout();
        _tlpDashMcp.SuspendLayout();
        _flpDashMcpButtons.SuspendLayout();
        _flpStatusButtons.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_dgvMappings).BeginInit();
        SuspendLayout();

        // _tabControl
        _tabControl.Controls.Add(_tabDashboard);
        _tabControl.Controls.Add(_tabLogs);
        _tabControl.Controls.Add(_tabSettings);
        _tabControl.Controls.Add(_tabInstructions);
        _tabControl.Controls.Add(_tabCredentials);
        _tabControl.Controls.Add(_tabMcp);
        _tabControl.Controls.Add(_tabTest);
        _tabControl.Controls.Add(_tabHeartbeats);
        _tabControl.Controls.Add(_tabHelp);
        _tabControl.Dock = DockStyle.Fill;
        _tabControl.Name = "_tabControl";
        _tabControl.SelectedIndex = 0;

        // _tabDashboard
        _tabDashboard.Controls.Add(_dashScrollPanel);
        _tabDashboard.Dock = DockStyle.Fill;
        _tabDashboard.Name = "_tabDashboard";
        _tabDashboard.Padding = new Padding(8);
        _tabDashboard.Text = "Dashboard";

        // _dashScrollPanel — scrolls the dashboard groups when they exceed the visible area
        _dashScrollPanel.AutoScroll = true;
        _dashScrollPanel.Controls.Add(_tlpDashboard);
        _dashScrollPanel.Dock = DockStyle.Fill;
        _dashScrollPanel.Name = "_dashScrollPanel";

        // _tlpDashboard
        _tlpDashboard.AutoSize = true;
        _tlpDashboard.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpDashboard.ColumnCount = 1;
        _tlpDashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpDashboard.Controls.Add(_grpPerf, 0, 0);
        _tlpDashboard.Controls.Add(_grpStatus, 0, 1);
        _tlpDashboard.Controls.Add(_grpDashMcp, 0, 2);
        _tlpDashboard.Dock = DockStyle.Top;
        _tlpDashboard.Name = "_tlpDashboard";
        _tlpDashboard.RowCount = 4;
        _tlpDashboard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpDashboard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpDashboard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpDashboard.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // _grpStatus
        _grpStatus.AutoSize = true;
        _grpStatus.AutoSizeMode = AutoSizeMode.GrowOnly;
        _grpStatus.Controls.Add(_tlpStatus);
        _grpStatus.Dock = DockStyle.Fill;
        _grpStatus.Margin = new Padding(0, 0, 0, 8);
        _grpStatus.Name = "_grpStatus";
        _grpStatus.Padding = new Padding(6, 2, 6, 4);
        _grpStatus.Text = "Proxy Status";

        // _tlpStatus — 2 columns: caption AutoSize | value Percent; final row is the button strip
        _tlpStatus.AutoSize = true;
        _tlpStatus.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpStatus.ColumnCount = 2;
        _tlpStatus.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpStatus.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpStatus.RowCount = 5;
        _tlpStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        _tlpStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        _tlpStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        _tlpStatus.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpStatus.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpStatus.Controls.Add(_lblStatus, 0, 0);
        _tlpStatus.Controls.Add(_lblStatusValue, 1, 0);
        _tlpStatus.Controls.Add(_lblStatusAddressCaption, 0, 1);
        _tlpStatus.Controls.Add(_lblStatusAddressValue, 1, 1);
        _tlpStatus.Controls.Add(_lblStatusPortCaption, 0, 2);
        _tlpStatus.Controls.Add(_lblStatusPortValue, 1, 2);
        _tlpStatus.Controls.Add(_flpStatusButtons, 0, 3);
        _tlpStatus.Controls.Add(_tlpStats, 0, 4);
        _tlpStatus.Dock = DockStyle.Fill;
        _tlpStatus.Name = "_tlpStatus";
        _tlpStatus.SetColumnSpan(_flpStatusButtons, 2);
        _tlpStatus.SetColumnSpan(_tlpStats, 2);

        // _flpStatusButtons — auto-sized to the button content
        _flpStatusButtons.AutoSize = true;
        _flpStatusButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpStatusButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpStatusButtons.Margin = new Padding(4, 8, 4, 4);
        _flpStatusButtons.Name = "_flpStatusButtons";
        _flpStatusButtons.WrapContents = false;
        _flpStatusButtons.Controls.Add(_btnStart);
        _flpStatusButtons.Controls.Add(_btnStop);
        _flpStatusButtons.Controls.Add(_btnRestart);

        _lblStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblStatus.AutoSize = true;
        _lblStatus.Margin = new Padding(4, 6, 4, 4);
        _lblStatus.Name = "_lblStatus";
        _lblStatus.Text = "Status:";

        _lblStatusValue.AutoSize = true;
        _lblStatusValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblStatusValue.Margin = new Padding(4, 6, 4, 4);
        _lblStatusValue.Name = "_lblStatusValue";
        _lblStatusValue.Text = "Stopped";

        _lblStatusAddressCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblStatusAddressCaption.AutoSize = true;
        _lblStatusAddressCaption.Margin = new Padding(4, 6, 4, 4);
        _lblStatusAddressCaption.Name = "_lblStatusAddressCaption";
        _lblStatusAddressCaption.Text = "Listen IP:";

        _lblStatusAddressValue.AutoSize = true;
        _lblStatusAddressValue.Margin = new Padding(4, 6, 4, 4);
        _lblStatusAddressValue.Name = "_lblStatusAddressValue";
        _lblStatusAddressValue.Text = "-";

        _lblStatusPortCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblStatusPortCaption.AutoSize = true;
        _lblStatusPortCaption.Margin = new Padding(4, 6, 4, 4);
        _lblStatusPortCaption.Name = "_lblStatusPortCaption";
        _lblStatusPortCaption.Text = "Port:";

        _lblStatusPortValue.AutoSize = true;
        _lblStatusPortValue.Margin = new Padding(4, 6, 4, 4);
        _lblStatusPortValue.Name = "_lblStatusPortValue";
        _lblStatusPortValue.Text = "-";

        _btnStart.Margin = new Padding(2, 0, 2, 0);
        _btnStart.Name = "_btnStart";
        _btnStart.Size = new Size(80, 28);
        _btnStart.Text = "Start";
        _btnStart.Click += BtnStart_Click;

        _btnStop.Margin = new Padding(2, 0, 2, 0);
        _btnStop.Name = "_btnStop";
        _btnStop.Size = new Size(80, 28);
        _btnStop.Text = "Stop";
        _btnStop.Click += BtnStop_Click;

        _btnRestart.Margin = new Padding(2, 0, 2, 0);
        _btnRestart.Name = "_btnRestart";
        _btnRestart.Size = new Size(88, 28);
        _btnRestart.Text = "Restart";
        _btnRestart.Click += BtnRestart_Click;

        // _grpDashMcp — runtime status of the built-in MCP server
        _grpDashMcp.AutoSize = true;
        _grpDashMcp.AutoSizeMode = AutoSizeMode.GrowOnly;
        _grpDashMcp.Controls.Add(_tlpDashMcp);
        _grpDashMcp.Dock = DockStyle.Fill;
        _grpDashMcp.Margin = new Padding(0, 0, 0, 8);
        _grpDashMcp.Name = "_grpDashMcp";
        _grpDashMcp.Padding = new Padding(6, 2, 6, 4);
        _grpDashMcp.Text = "MCP Status";

        // _tlpDashMcp — 2 columns: caption AutoSize | value Percent; final row is the button strip
        _tlpDashMcp.AutoSize = true;
        _tlpDashMcp.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpDashMcp.ColumnCount = 2;
        _tlpDashMcp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpDashMcp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpDashMcp.Controls.Add(_lblDashMcpStatusCaption, 0, 0);
        _tlpDashMcp.Controls.Add(_lblDashMcpStatusValue, 1, 0);
        _tlpDashMcp.Controls.Add(_lblDashMcpAddressCaption, 0, 1);
        _tlpDashMcp.Controls.Add(_lblDashMcpAddressValue, 1, 1);
        _tlpDashMcp.Controls.Add(_lblDashMcpPortCaption, 0, 2);
        _tlpDashMcp.Controls.Add(_lblDashMcpPortValue, 1, 2);
        _tlpDashMcp.Controls.Add(_flpDashMcpButtons, 0, 3);
        _tlpDashMcp.Controls.Add(_tlpMcpStats, 0, 4);
        _tlpDashMcp.Dock = DockStyle.Fill;
        _tlpDashMcp.Name = "_tlpDashMcp";
        _tlpDashMcp.SetColumnSpan(_flpDashMcpButtons, 2);
        _tlpDashMcp.SetColumnSpan(_tlpMcpStats, 2);

        // _flpDashMcpButtons — auto-sized to the button content
        _flpDashMcpButtons.AutoSize = true;
        _flpDashMcpButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpDashMcpButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpDashMcpButtons.Margin = new Padding(4, 8, 4, 4);
        _flpDashMcpButtons.Name = "_flpDashMcpButtons";
        _flpDashMcpButtons.WrapContents = false;
        _flpDashMcpButtons.Controls.Add(_btnDashMcpStart);
        _flpDashMcpButtons.Controls.Add(_btnDashMcpStop);
        _flpDashMcpButtons.Controls.Add(_btnDashMcpRestart);

        _lblDashMcpStatusCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblDashMcpStatusCaption.AutoSize = true;
        _lblDashMcpStatusCaption.Margin = new Padding(4, 6, 4, 4);
        _lblDashMcpStatusCaption.Name = "_lblDashMcpStatusCaption";
        _lblDashMcpStatusCaption.Text = "Status:";

        _lblDashMcpStatusValue.AutoSize = true;
        _lblDashMcpStatusValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblDashMcpStatusValue.Margin = new Padding(4, 6, 4, 4);
        _lblDashMcpStatusValue.Name = "_lblDashMcpStatusValue";
        _lblDashMcpStatusValue.Text = "Stopped";

        _lblDashMcpAddressCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblDashMcpAddressCaption.AutoSize = true;
        _lblDashMcpAddressCaption.Margin = new Padding(4, 6, 4, 4);
        _lblDashMcpAddressCaption.Name = "_lblDashMcpAddressCaption";
        _lblDashMcpAddressCaption.Text = "Listen IP:";

        _lblDashMcpAddressValue.AutoSize = true;
        _lblDashMcpAddressValue.Margin = new Padding(4, 6, 4, 4);
        _lblDashMcpAddressValue.Name = "_lblDashMcpAddressValue";
        _lblDashMcpAddressValue.Text = "-";

        _lblDashMcpPortCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblDashMcpPortCaption.AutoSize = true;
        _lblDashMcpPortCaption.Margin = new Padding(4, 6, 4, 4);
        _lblDashMcpPortCaption.Name = "_lblDashMcpPortCaption";
        _lblDashMcpPortCaption.Text = "Port:";

        _lblDashMcpPortValue.AutoSize = true;
        _lblDashMcpPortValue.Margin = new Padding(4, 6, 4, 4);
        _lblDashMcpPortValue.Name = "_lblDashMcpPortValue";
        _lblDashMcpPortValue.Text = "-";

        _btnDashMcpStart.Margin = new Padding(2, 0, 2, 0);
        _btnDashMcpStart.Name = "_btnDashMcpStart";
        _btnDashMcpStart.Size = new Size(80, 28);
        _btnDashMcpStart.Text = "Start";
        _btnDashMcpStart.Click += BtnDashMcpStart_Click;

        _btnDashMcpStop.Margin = new Padding(2, 0, 2, 0);
        _btnDashMcpStop.Name = "_btnDashMcpStop";
        _btnDashMcpStop.Size = new Size(80, 28);
        _btnDashMcpStop.Text = "Stop";
        _btnDashMcpStop.Click += BtnDashMcpStop_Click;

        _btnDashMcpRestart.Margin = new Padding(2, 0, 2, 0);
        _btnDashMcpRestart.Name = "_btnDashMcpRestart";
        _btnDashMcpRestart.Size = new Size(88, 28);
        _btnDashMcpRestart.Text = "Restart";
        _btnDashMcpRestart.Click += BtnDashMcpRestart_Click;

        // _tlpStats
        _tlpStats.AutoSize = true;
        _tlpStats.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpStats.ColumnCount = 4;
        _tlpStats.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        _tlpStats.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        _tlpStats.Controls.Add(_lblTotalRequestsCaption, 0, 0);
        _tlpStats.Controls.Add(_lblTotalRequestsValue, 1, 0);
        _tlpStats.Controls.Add(_lblTotalErrorsCaption, 2, 0);
        _tlpStats.Controls.Add(_lblTotalErrorsValue, 3, 0);
        _tlpStats.Controls.Add(_lblPromptTokensCaption, 0, 1);
        _tlpStats.Controls.Add(_lblPromptTokensValue, 1, 1);
        _tlpStats.Controls.Add(_lblCompletionTokensCaption, 2, 1);
        _tlpStats.Controls.Add(_lblCompletionTokensValue, 3, 1);
        _tlpStats.Controls.Add(_lblRpsCaption, 0, 2);
        _tlpStats.Controls.Add(_lblRpsValue, 1, 2);
        _tlpStats.Controls.Add(_btnResetStats, 3, 2);
        _tlpStats.Dock = DockStyle.Fill;
        _tlpStats.Margin = new Padding(4, 8, 4, 4);
        _tlpStats.Name = "_tlpStats";
        _tlpStats.Padding = new Padding(4);

        _lblTotalRequestsCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblTotalRequestsCaption.AutoSize = true;
        _lblTotalRequestsCaption.Margin = new Padding(4, 6, 4, 4);
        _lblTotalRequestsCaption.Name = "_lblTotalRequestsCaption";
        _lblTotalRequestsCaption.Text = "Total Requests:";

        _lblTotalRequestsValue.AutoSize = true;
        _lblTotalRequestsValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblTotalRequestsValue.Margin = new Padding(4, 6, 4, 4);
        _lblTotalRequestsValue.Name = "_lblTotalRequestsValue";
        _lblTotalRequestsValue.Text = "0";

        _lblTotalErrorsCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblTotalErrorsCaption.AutoSize = true;
        _lblTotalErrorsCaption.Margin = new Padding(12, 6, 4, 4);
        _lblTotalErrorsCaption.Name = "_lblTotalErrorsCaption";
        _lblTotalErrorsCaption.Text = "Errors:";

        _lblTotalErrorsValue.AutoSize = true;
        _lblTotalErrorsValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblTotalErrorsValue.Margin = new Padding(4, 6, 4, 4);
        _lblTotalErrorsValue.Name = "_lblTotalErrorsValue";
        _lblTotalErrorsValue.Text = "0";

        _lblPromptTokensCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblPromptTokensCaption.AutoSize = true;
        _lblPromptTokensCaption.Margin = new Padding(4, 6, 4, 4);
        _lblPromptTokensCaption.Name = "_lblPromptTokensCaption";
        _lblPromptTokensCaption.Text = "Prompt Tokens:";

        _lblPromptTokensValue.AutoSize = true;
        _lblPromptTokensValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblPromptTokensValue.Margin = new Padding(4, 6, 4, 4);
        _lblPromptTokensValue.Name = "_lblPromptTokensValue";
        _lblPromptTokensValue.Text = "0";

        _lblCompletionTokensCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblCompletionTokensCaption.AutoSize = true;
        _lblCompletionTokensCaption.Margin = new Padding(12, 6, 4, 4);
        _lblCompletionTokensCaption.Name = "_lblCompletionTokensCaption";
        _lblCompletionTokensCaption.Text = "Completion Tokens:";

        _lblCompletionTokensValue.AutoSize = true;
        _lblCompletionTokensValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblCompletionTokensValue.Margin = new Padding(4, 6, 4, 4);
        _lblCompletionTokensValue.Name = "_lblCompletionTokensValue";
        _lblCompletionTokensValue.Text = "0";

        _btnResetStats.Anchor = AnchorStyles.Right;
        _btnResetStats.AutoSize = true;
        _btnResetStats.Margin = new Padding(4, 8, 4, 4);
        _btnResetStats.Name = "_btnResetStats";
        _btnResetStats.Text = "Reset Stats";
        _btnResetStats.Click += BtnResetStats_Click;

        _lblRpsCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblRpsCaption.AutoSize = true;
        _lblRpsCaption.Margin = new Padding(4, 6, 4, 4);
        _lblRpsCaption.Name = "_lblRpsCaption";
        _lblRpsCaption.Text = "Req/s (60s avg):";

        _lblRpsValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblRpsValue.AutoSize = true;
        _lblRpsValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblRpsValue.Margin = new Padding(4, 6, 4, 4);
        _lblRpsValue.Name = "_lblRpsValue";
        _lblRpsValue.Text = "0.00";

        // _tlpMcpStats — MCP request counters shown inside the MCP Status group
        _tlpMcpStats.AutoSize = true;
        _tlpMcpStats.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpMcpStats.ColumnCount = 4;
        _tlpMcpStats.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMcpStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        _tlpMcpStats.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMcpStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        _tlpMcpStats.Controls.Add(_lblMcpTotalRequestsCaption, 0, 0);
        _tlpMcpStats.Controls.Add(_lblMcpTotalRequestsValue, 1, 0);
        _tlpMcpStats.Controls.Add(_lblMcpTotalErrorsCaption, 2, 0);
        _tlpMcpStats.Controls.Add(_lblMcpTotalErrorsValue, 3, 0);
        _tlpMcpStats.Controls.Add(_lblMcpRpsCaption, 0, 1);
        _tlpMcpStats.Controls.Add(_lblMcpRpsValue, 1, 1);
        _tlpMcpStats.Controls.Add(_btnResetMcpStats, 3, 1);
        _tlpMcpStats.Dock = DockStyle.Fill;
        _tlpMcpStats.Margin = new Padding(4, 8, 4, 4);
        _tlpMcpStats.Name = "_tlpMcpStats";
        _tlpMcpStats.Padding = new Padding(4);

        _lblMcpTotalRequestsCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMcpTotalRequestsCaption.AutoSize = true;
        _lblMcpTotalRequestsCaption.Margin = new Padding(4, 6, 4, 4);
        _lblMcpTotalRequestsCaption.Name = "_lblMcpTotalRequestsCaption";
        _lblMcpTotalRequestsCaption.Text = "Total Requests:";

        _lblMcpTotalRequestsValue.AutoSize = true;
        _lblMcpTotalRequestsValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblMcpTotalRequestsValue.Margin = new Padding(4, 6, 4, 4);
        _lblMcpTotalRequestsValue.Name = "_lblMcpTotalRequestsValue";
        _lblMcpTotalRequestsValue.Text = "0";

        _lblMcpTotalErrorsCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMcpTotalErrorsCaption.AutoSize = true;
        _lblMcpTotalErrorsCaption.Margin = new Padding(12, 6, 4, 4);
        _lblMcpTotalErrorsCaption.Name = "_lblMcpTotalErrorsCaption";
        _lblMcpTotalErrorsCaption.Text = "Errors:";

        _lblMcpTotalErrorsValue.AutoSize = true;
        _lblMcpTotalErrorsValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblMcpTotalErrorsValue.Margin = new Padding(4, 6, 4, 4);
        _lblMcpTotalErrorsValue.Name = "_lblMcpTotalErrorsValue";
        _lblMcpTotalErrorsValue.Text = "0";

        _btnResetMcpStats.Anchor = AnchorStyles.Right;
        _btnResetMcpStats.AutoSize = true;
        _btnResetMcpStats.Margin = new Padding(4, 8, 4, 4);
        _btnResetMcpStats.Name = "_btnResetMcpStats";
        _btnResetMcpStats.Text = "Reset Stats";
        _btnResetMcpStats.Click += BtnResetMcpStats_Click;

        _lblMcpRpsCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMcpRpsCaption.AutoSize = true;
        _lblMcpRpsCaption.Margin = new Padding(4, 6, 4, 4);
        _lblMcpRpsCaption.Name = "_lblMcpRpsCaption";
        _lblMcpRpsCaption.Text = "Req/s (60s avg):";

        _lblMcpRpsValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMcpRpsValue.AutoSize = true;
        _lblMcpRpsValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblMcpRpsValue.Margin = new Padding(4, 6, 4, 4);
        _lblMcpRpsValue.Name = "_lblMcpRpsValue";
        _lblMcpRpsValue.Text = "0.00";

        // _grpPerf
        _grpPerf.AutoSize = true;
        _grpPerf.AutoSizeMode = AutoSizeMode.GrowOnly;
        _grpPerf.Controls.Add(_tlpPerf);
        _grpPerf.Dock = DockStyle.Fill;
        _grpPerf.Margin = new Padding(0, 0, 0, 12);
        _grpPerf.Name = "_grpPerf";
        _grpPerf.Text = "Process Performance";

        // _tlpPerf
        _tlpPerf.AutoSize = true;
        _tlpPerf.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpPerf.ColumnCount = 4;
        _tlpPerf.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpPerf.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        _tlpPerf.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpPerf.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        _tlpPerf.Controls.Add(_lblCpuCaption, 0, 0);
        _tlpPerf.Controls.Add(_lblCpuValue, 1, 0);
        _tlpPerf.Controls.Add(_lblRamCaption, 2, 0);
        _tlpPerf.Controls.Add(_lblRamValue, 3, 0);
        _tlpPerf.Dock = DockStyle.Fill;
        _tlpPerf.Margin = new Padding(4);
        _tlpPerf.Name = "_tlpPerf";

        _lblCpuCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblCpuCaption.AutoSize = true;
        _lblCpuCaption.Margin = new Padding(4, 8, 8, 8);
        _lblCpuCaption.Name = "_lblCpuCaption";
        _lblCpuCaption.Text = "CPU:";

        _lblCpuValue.AutoSize = true;
        _lblCpuValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblCpuValue.Margin = new Padding(4, 8, 4, 8);
        _lblCpuValue.Name = "_lblCpuValue";
        _lblCpuValue.Text = "0.0%";

        _lblRamCaption.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblRamCaption.AutoSize = true;
        _lblRamCaption.Margin = new Padding(12, 8, 8, 8);
        _lblRamCaption.Name = "_lblRamCaption";
        _lblRamCaption.Text = "RAM:";

        _lblRamValue.AutoSize = true;
        _lblRamValue.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblRamValue.Margin = new Padding(4, 8, 4, 8);
        _lblRamValue.Name = "_lblRamValue";
        _lblRamValue.Text = "0 MB";

        // _tabLogs
        _tabLogs.Controls.Add(_tlpLogs);
        _tabLogs.Dock = DockStyle.Fill;
        _tabLogs.Name = "_tabLogs";
        _tabLogs.Padding = new Padding(8);
        _tabLogs.Text = "Logs";

        // _tlpLogs
        _tlpLogs.ColumnCount = 1;
        _tlpLogs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpLogs.Controls.Add(_logSubTabs, 0, 0);
        _tlpLogs.Controls.Add(_flpLogsButtons, 0, 1);
        _tlpLogs.Dock = DockStyle.Fill;
        _tlpLogs.Name = "_tlpLogs";
        _tlpLogs.RowCount = 2;
        _tlpLogs.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tlpLogs.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // _logSubTabs — per-service request logs
        _logSubTabs.Controls.Add(_logProxyPage);
        _logSubTabs.Controls.Add(_logMcpPage);
        _logSubTabs.Controls.Add(_logNonProxiedPage);
        _logSubTabs.Controls.Add(_tabSysLogs);
        _logSubTabs.Dock = DockStyle.Fill;
        _logSubTabs.Name = "_logSubTabs";
        _logSubTabs.SelectedIndexChanged += LogSubTabs_SelectionChanged;

        // _logProxyPage
        _logProxyPage.Controls.Add(_tlpProxyLogs);
        _logProxyPage.Dock = DockStyle.Fill;
        _logProxyPage.Name = "_logProxyPage";
        _logProxyPage.Padding = new Padding(3);
        _logProxyPage.Text = "Proxy";

        // _tlpProxyLogs — 1 column: filter bar AutoSize | list fills
        _tlpProxyLogs.ColumnCount = 1;
        _tlpProxyLogs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpProxyLogs.Controls.Add(_flpProxyLogTop, 0, 0);
        _tlpProxyLogs.Controls.Add(_lstLogs, 0, 1);
        _tlpProxyLogs.Dock = DockStyle.Fill;
        _tlpProxyLogs.Name = "_tlpProxyLogs";
        _tlpProxyLogs.RowCount = 2;
        _tlpProxyLogs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpProxyLogs.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // _flpProxyLogTop
        _flpProxyLogTop.AutoSize = true;
        _flpProxyLogTop.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpProxyLogTop.Controls.Add(_lblProxyLogFilter);
        _flpProxyLogTop.Controls.Add(_txtProxyLogFilter);
        _flpProxyLogTop.Dock = DockStyle.Fill;
        _flpProxyLogTop.FlowDirection = FlowDirection.LeftToRight;
        _flpProxyLogTop.Margin = new Padding(0, 0, 0, 4);
        _flpProxyLogTop.Name = "_flpProxyLogTop";
        _flpProxyLogTop.WrapContents = false;

        _lblProxyLogFilter.Anchor = AnchorStyles.Left;
        _lblProxyLogFilter.AutoSize = true;
        _lblProxyLogFilter.Margin = new Padding(0, 5, 4, 0);
        _lblProxyLogFilter.Name = "_lblProxyLogFilter";
        _lblProxyLogFilter.Text = "Filter:";

        _txtProxyLogFilter.Anchor = AnchorStyles.Left;
        _txtProxyLogFilter.Margin = new Padding(0, 0, 0, 0);
        _txtProxyLogFilter.Name = "_txtProxyLogFilter";
        _txtProxyLogFilter.PlaceholderText = "Filter by method, path, model, or status";
        _txtProxyLogFilter.Size = new Size(320, 23);
        _txtProxyLogFilter.TextChanged += LogFilter_TextChanged;

        // _logMcpPage
        _logMcpPage.Controls.Add(_tlpMcpLogs);
        _logMcpPage.Dock = DockStyle.Fill;
        _logMcpPage.Name = "_logMcpPage";
        _logMcpPage.Padding = new Padding(3);
        _logMcpPage.Text = "MCP";

        // _tlpMcpLogs — 1 column: filter bar AutoSize | list fills
        _tlpMcpLogs.ColumnCount = 1;
        _tlpMcpLogs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMcpLogs.Controls.Add(_flpMcpLogTop, 0, 0);
        _tlpMcpLogs.Controls.Add(_lstMcpLogs, 0, 1);
        _tlpMcpLogs.Dock = DockStyle.Fill;
        _tlpMcpLogs.Name = "_tlpMcpLogs";
        _tlpMcpLogs.RowCount = 2;
        _tlpMcpLogs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcpLogs.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // _flpMcpLogTop
        _flpMcpLogTop.AutoSize = true;
        _flpMcpLogTop.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpMcpLogTop.Controls.Add(_lblMcpLogFilter);
        _flpMcpLogTop.Controls.Add(_txtMcpLogFilter);
        _flpMcpLogTop.Dock = DockStyle.Fill;
        _flpMcpLogTop.FlowDirection = FlowDirection.LeftToRight;
        _flpMcpLogTop.Margin = new Padding(0, 0, 0, 4);
        _flpMcpLogTop.Name = "_flpMcpLogTop";
        _flpMcpLogTop.WrapContents = false;

        _lblMcpLogFilter.Anchor = AnchorStyles.Left;
        _lblMcpLogFilter.AutoSize = true;
        _lblMcpLogFilter.Margin = new Padding(0, 5, 4, 0);
        _lblMcpLogFilter.Name = "_lblMcpLogFilter";
        _lblMcpLogFilter.Text = "Filter:";

        _txtMcpLogFilter.Anchor = AnchorStyles.Left;
        _txtMcpLogFilter.Margin = new Padding(0, 0, 0, 0);
        _txtMcpLogFilter.Name = "_txtMcpLogFilter";
        _txtMcpLogFilter.PlaceholderText = "Filter by method, path, or status";
        _txtMcpLogFilter.Size = new Size(320, 23);
        _txtMcpLogFilter.TextChanged += LogFilter_TextChanged;

        // _logNonProxiedPage
        _logNonProxiedPage.Controls.Add(_tlpNonProxiedLogs);
        _logNonProxiedPage.Dock = DockStyle.Fill;
        _logNonProxiedPage.Name = "_logNonProxiedPage";
        _logNonProxiedPage.Padding = new Padding(3);
        _logNonProxiedPage.Text = "Non-proxied";

        // _tlpNonProxiedLogs — 1 column: filter bar AutoSize | list fills
        _tlpNonProxiedLogs.ColumnCount = 1;
        _tlpNonProxiedLogs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpNonProxiedLogs.Controls.Add(_flpNonProxiedLogTop, 0, 0);
        _tlpNonProxiedLogs.Controls.Add(_lstNonProxiedLogs, 0, 1);
        _tlpNonProxiedLogs.Dock = DockStyle.Fill;
        _tlpNonProxiedLogs.Name = "_tlpNonProxiedLogs";
        _tlpNonProxiedLogs.RowCount = 2;
        _tlpNonProxiedLogs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpNonProxiedLogs.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // _flpNonProxiedLogTop
        _flpNonProxiedLogTop.AutoSize = true;
        _flpNonProxiedLogTop.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpNonProxiedLogTop.Controls.Add(_lblNonProxiedLogFilter);
        _flpNonProxiedLogTop.Controls.Add(_txtNonProxiedLogFilter);
        _flpNonProxiedLogTop.Dock = DockStyle.Fill;
        _flpNonProxiedLogTop.FlowDirection = FlowDirection.LeftToRight;
        _flpNonProxiedLogTop.Margin = new Padding(0, 0, 0, 4);
        _flpNonProxiedLogTop.Name = "_flpNonProxiedLogTop";
        _flpNonProxiedLogTop.WrapContents = false;

        _lblNonProxiedLogFilter.Anchor = AnchorStyles.Left;
        _lblNonProxiedLogFilter.AutoSize = true;
        _lblNonProxiedLogFilter.Margin = new Padding(0, 5, 4, 0);
        _lblNonProxiedLogFilter.Name = "_lblNonProxiedLogFilter";
        _lblNonProxiedLogFilter.Text = "Filter:";

        _txtNonProxiedLogFilter.Anchor = AnchorStyles.Left;
        _txtNonProxiedLogFilter.Margin = new Padding(0, 0, 0, 0);
        _txtNonProxiedLogFilter.Name = "_txtNonProxiedLogFilter";
        _txtNonProxiedLogFilter.PlaceholderText = "Filter by method, path, or status";
        _txtNonProxiedLogFilter.Size = new Size(320, 23);
        _txtNonProxiedLogFilter.TextChanged += LogFilter_TextChanged;

        // _lstNonProxiedLogs
        _lstNonProxiedLogs.Columns.Add(_colNpTime);
        _lstNonProxiedLogs.Columns.Add(_colNpMethod);
        _lstNonProxiedLogs.Columns.Add(_colNpPath);
        _lstNonProxiedLogs.Columns.Add(_colNpStatus);
        _lstNonProxiedLogs.Columns.Add(_colNpDuration);
        _lstNonProxiedLogs.Columns.Add(_colNpBytes);
        _lstNonProxiedLogs.FullRowSelect = true;
        _lstNonProxiedLogs.GridLines = true;
        _lstNonProxiedLogs.Dock = DockStyle.Fill;
        _lstNonProxiedLogs.Margin = new Padding(0);
        _lstNonProxiedLogs.Name = "_lstNonProxiedLogs";
        _lstNonProxiedLogs.View = View.Details;
        _lstNonProxiedLogs.DoubleClick += LstNonProxiedLogs_DoubleClick;

        _colNpTime.Text = "Time";
        _colNpTime.Width = 105;
        _colNpMethod.Text = "Method";
        _colNpMethod.Width = 55;
        _colNpPath.Text = "Path";
        _colNpPath.Width = 260;
        _colNpStatus.Text = "Status";
        _colNpStatus.Width = 60;
        _colNpDuration.Text = "ms";
        _colNpDuration.Width = 60;
        _colNpBytes.Text = "Bytes (req/resp)";
        _colNpBytes.Width = 110;

        // _flpLogsButtons
        _flpLogsButtons.AutoSize = true;
        _flpLogsButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpLogsButtons.Controls.Add(_chkAutoRefresh);
        _flpLogsButtons.Controls.Add(_lblRefreshInterval);
        _flpLogsButtons.Controls.Add(_cmbRefreshInterval);
        _flpLogsButtons.Controls.Add(_btnRefreshLogs);
        _flpLogsButtons.Controls.Add(_btnLogDetails);
        _flpLogsButtons.Controls.Add(_btnClearLogs);
        _flpLogsButtons.Dock = DockStyle.Fill;
        _flpLogsButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpLogsButtons.Margin = new Padding(0, 8, 0, 0);
        _flpLogsButtons.Name = "_flpLogsButtons";
        _flpLogsButtons.WrapContents = false;

        _chkAutoRefresh.Anchor = AnchorStyles.Left;
        _chkAutoRefresh.AutoSize = true;
        _chkAutoRefresh.Checked = true;
        _chkAutoRefresh.CheckState = CheckState.Checked;
        _chkAutoRefresh.Margin = new Padding(0, 6, 8, 0);
        _chkAutoRefresh.Name = "_chkAutoRefresh";
        _chkAutoRefresh.Text = "Auto-refresh";

        _lblRefreshInterval.Anchor = AnchorStyles.Left;
        _lblRefreshInterval.AutoSize = true;
        _lblRefreshInterval.Margin = new Padding(0, 6, 4, 0);
        _lblRefreshInterval.Name = "_lblRefreshInterval";
        _lblRefreshInterval.Text = "every";

        _cmbRefreshInterval.Anchor = AnchorStyles.Left;
        _cmbRefreshInterval.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbRefreshInterval.Items.AddRange(new object[] { "1 s", "2 s", "5 s", "10 s", "30 s" });
        _cmbRefreshInterval.Margin = new Padding(0, 2, 12, 0);
        _cmbRefreshInterval.Name = "_cmbRefreshInterval";
        _cmbRefreshInterval.Size = new Size(68, 23);
        _cmbRefreshInterval.SelectedIndexChanged += CmbRefreshInterval_SelectedIndexChanged;

        _btnRefreshLogs.Anchor = AnchorStyles.Left;
        _btnRefreshLogs.Margin = new Padding(0, 0, 6, 0);
        _btnRefreshLogs.Name = "_btnRefreshLogs";
        _btnRefreshLogs.Size = new Size(88, 28);
        _btnRefreshLogs.Text = "Refresh";
        _btnRefreshLogs.Click += BtnRefreshLogs_Click;

        _btnLogDetails.Anchor = AnchorStyles.Left;
        _btnLogDetails.Margin = new Padding(0, 0, 6, 0);
        _btnLogDetails.Name = "_btnLogDetails";
        _btnLogDetails.Size = new Size(88, 28);
        _btnLogDetails.Text = "Details\u2026";
        _btnLogDetails.Click += BtnLogDetails_Click;

        _btnClearLogs.Anchor = AnchorStyles.Left;
        _btnClearLogs.Margin = new Padding(0);
        _btnClearLogs.Name = "_btnClearLogs";
        _btnClearLogs.Size = new Size(88, 28);
        _btnClearLogs.Text = "Clear";
        _btnClearLogs.Click += BtnClearLogs_Click;

        _lstLogs.Columns.Add(_colTime);
        _lstLogs.Columns.Add(_colMethod);
        _lstLogs.Columns.Add(_colPath);
        _lstLogs.Columns.Add(_colModel);
        _lstLogs.Columns.Add(_colStatus);
        _lstLogs.Columns.Add(_colDuration);
        _lstLogs.Columns.Add(_colPromptTokens);
        _lstLogs.Columns.Add(_colCompletionTokens);
        _lstLogs.Columns.Add(_colReasoningTokens);
        _lstLogs.Columns.Add(_colCachedTokens);
        _lstLogs.Columns.Add(_colDraftAccept);
        _lstLogs.Columns.Add(_colBytes);
        _lstLogs.FullRowSelect = true;
        _lstLogs.GridLines = true;
        _lstLogs.Dock = DockStyle.Fill;
        _lstLogs.Margin = new Padding(0);
        _lstLogs.Name = "_lstLogs";
        _lstLogs.View = View.Details;
        _lstLogs.DoubleClick += LstLogs_DoubleClick;

        _colTime.Text = "Time";
        _colTime.Width = 105;
        _colMethod.Text = "Method";
        _colMethod.Width = 55;
        _colPath.Text = "Path";
        _colPath.Width = 160;
        _colModel.Text = "Model";
        _colModel.Width = 160;
        _colStatus.Text = "Status";
        _colStatus.Width = 60;
        _colDuration.Text = "ms";
        _colDuration.Width = 60;
        _colPromptTokens.Text = "Prompt";
        _colPromptTokens.Width = 70;
        _colCompletionTokens.Text = "Completion";
        _colCompletionTokens.Width = 80;
        _colReasoningTokens.Text = "Reasoning";
        _colReasoningTokens.Width = 80;
        _colCachedTokens.Text = "Cached";
        _colCachedTokens.Width = 70;
        _colDraftAccept.Text = "Draft%";
        _colDraftAccept.Width = 65;
        _colBytes.Text = "Bytes (req/resp)";
        _colBytes.Width = 110;

        _lstMcpLogs.Columns.Add(_colMcpTime);
        _lstMcpLogs.Columns.Add(_colMcpMethod);
        _lstMcpLogs.Columns.Add(_colMcpPath);
        _lstMcpLogs.Columns.Add(_colMcpStatus);
        _lstMcpLogs.Columns.Add(_colMcpDuration);
        _lstMcpLogs.Columns.Add(_colMcpBytes);
        _lstMcpLogs.FullRowSelect = true;
        _lstMcpLogs.GridLines = true;
        _lstMcpLogs.Dock = DockStyle.Fill;
        _lstMcpLogs.Margin = new Padding(0);
        _lstMcpLogs.Name = "_lstMcpLogs";
        _lstMcpLogs.View = View.Details;
        _lstMcpLogs.DoubleClick += LstMcpLogs_DoubleClick;

        _colMcpTime.Text = "Time";
        _colMcpTime.Width = 105;
        _colMcpMethod.Text = "Method";
        _colMcpMethod.Width = 55;
        _colMcpPath.Text = "Path";
        _colMcpPath.Width = 220;
        _colMcpStatus.Text = "Status";
        _colMcpStatus.Width = 60;
        _colMcpDuration.Text = "ms";
        _colMcpDuration.Width = 60;
        _colMcpBytes.Text = "Bytes (req/resp)";
        _colMcpBytes.Width = 110;

        // _tabSettings
        _tabSettings.AutoScroll = true;
        _tabSettings.Controls.Add(_tlpSettings);
        _tabSettings.Dock = DockStyle.Fill;
        _tabSettings.Name = "_tabSettings";
        _tabSettings.Padding = new Padding(8);
        _tabSettings.Text = "Settings";

        _tlpSettings.AutoSize = true;
        _tlpSettings.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpSettings.ColumnCount = 2;
        _tlpSettings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpSettings.Location = new Point(8, 8);
        _tlpSettings.Name = "_tlpSettings";
        _tlpSettings.RowCount = 20;
        _tlpSettings.Size = new Size(660, 460);

        _tlpSettings.SetColumnSpan(_grpListener, 2);
        _tlpSettings.Controls.Add(_grpListener, 0, 0);
        _tlpSettings.SetColumnSpan(_lblMappings, 2);
        _tlpSettings.Controls.Add(_lblMappings, 0, 1);
        _tlpSettings.SetColumnSpan(_dgvMappings, 2);
        _tlpSettings.Controls.Add(_dgvMappings, 0, 2);
        _tlpSettings.SetColumnSpan(_flpMappingButtons, 2);
        _tlpSettings.Controls.Add(_flpMappingButtons, 0, 3);
        _tlpSettings.Controls.Add(_lblMaxLogs, 0, 4);
        _tlpSettings.Controls.Add(_txtMaxLogs, 1, 4);
        _tlpSettings.Controls.Add(_lblCompactionFallback, 0, 5);
        _tlpSettings.Controls.Add(_txtCompactionFallback, 1, 5);
        _tlpSettings.SetColumnSpan(_chkAutoStart, 2);
        _tlpSettings.Controls.Add(_chkAutoStart, 0, 6);
        _tlpSettings.SetColumnSpan(_chkStartWithDashboard, 2);
        _tlpSettings.Controls.Add(_chkStartWithDashboard, 0, 7);
        _tlpSettings.SetColumnSpan(_chkRunAsAdmin, 2);
        _tlpSettings.Controls.Add(_chkRunAsAdmin, 0, 8);
        _tlpSettings.SetColumnSpan(_chkCollectDetails, 2);
        _tlpSettings.Controls.Add(_chkCollectDetails, 0, 9);
        _tlpSettings.SetColumnSpan(_chkCollectResponseDetails, 2);
        _tlpSettings.Controls.Add(_chkCollectResponseDetails, 0, 10);
        _tlpSettings.SetColumnSpan(_chkDebugMode, 2);
        _tlpSettings.Controls.Add(_chkDebugMode, 0, 11);
        _tlpSettings.SetColumnSpan(_chkIrTranslation, 2);
        _tlpSettings.Controls.Add(_chkIrTranslation, 0, 13);
        _tlpSettings.SetColumnSpan(_grpNonProxiedCapture, 2);
        _tlpSettings.Controls.Add(_grpNonProxiedCapture, 0, 12);
        _tlpSettings.SetColumnSpan(_chkPerformanceSampling, 2);
        _tlpSettings.Controls.Add(_chkPerformanceSampling, 0, 14);
        _tlpSettings.SetColumnSpan(_chkApiExplorer, 2);
        _tlpSettings.Controls.Add(_chkApiExplorer, 0, 15);
        _tlpSettings.SetColumnSpan(_lblApiExplorerUrl, 2);
        _tlpSettings.Controls.Add(_lblApiExplorerUrl, 0, 16);
        _tlpSettings.SetColumnSpan(_lblApiSpecUrl, 2);
        _tlpSettings.Controls.Add(_lblApiSpecUrl, 0, 17);
        _tlpSettings.SetColumnSpan(_grpSseKeepAlive, 2);
        _tlpSettings.Controls.Add(_grpSseKeepAlive, 0, 18);
        _tlpSettings.SetColumnSpan(_grpLogging, 2);
        _tlpSettings.Controls.Add(_grpLogging, 0, 19);

        // _grpListener
        _grpListener.AutoSize = true;
        _grpListener.AutoSizeMode = AutoSizeMode.GrowOnly;
        _grpListener.Controls.Add(_tlpListener);
        _grpListener.Dock = DockStyle.Fill;
        _grpListener.Margin = new Padding(4, 4, 4, 4);
        _grpListener.Name = "_grpListener";
        _grpListener.Text = "Listener";

        // _tlpListener
        _tlpListener.AutoSize = true;
        _tlpListener.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpListener.ColumnCount = 2;
        _tlpListener.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpListener.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpListener.Dock = DockStyle.Fill;
        _tlpListener.Margin = new Padding(4);
        _tlpListener.Name = "_tlpListener";
        _tlpListener.RowCount = 3;
        _tlpListener.Controls.Add(_lblListenPort, 0, 0);
        _tlpListener.Controls.Add(_txtListenPort, 1, 0);
        _tlpListener.Controls.Add(_lblListenAddress, 0, 1);
        _tlpListener.Controls.Add(_cmbListenAddress, 1, 1);
        _tlpListener.SetColumnSpan(_btnSaveListener, 2);
        _tlpListener.Controls.Add(_btnSaveListener, 0, 2);

        _lblListenPort.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblListenPort.AutoSize = true;
        _lblListenPort.Margin = new Padding(4, 8, 8, 4);
        _lblListenPort.Name = "_lblListenPort";
        _lblListenPort.Text = "Listen Port:";

        _txtListenPort.Dock = DockStyle.Fill;
        _txtListenPort.Margin = new Padding(4, 6, 4, 4);
        _txtListenPort.Name = "_txtListenPort";

        _lblListenAddress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblListenAddress.AutoSize = true;
        _lblListenAddress.Margin = new Padding(4, 8, 8, 4);
        _lblListenAddress.Name = "_lblListenAddress";
        _lblListenAddress.Text = "Listen Address:";

        _cmbListenAddress.Dock = DockStyle.Fill;
        _cmbListenAddress.DropDownStyle = ComboBoxStyle.DropDown;
        _cmbListenAddress.Margin = new Padding(4, 6, 4, 4);
        _cmbListenAddress.Name = "_cmbListenAddress";

        _lblMaxLogs.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMaxLogs.AutoSize = true;
        _lblMaxLogs.Margin = new Padding(4, 8, 8, 4);
        _lblMaxLogs.Name = "_lblMaxLogs";
        _lblMaxLogs.Text = "Max Log Entries:";

        _txtMaxLogs.Dock = DockStyle.Fill;
        _txtMaxLogs.Margin = new Padding(4, 6, 4, 4);

        _lblCompactionFallback.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblCompactionFallback.AutoSize = true;
        _lblCompactionFallback.Margin = new Padding(4, 8, 8, 4);
        _lblCompactionFallback.Name = "_lblCompactionFallback";
        _lblCompactionFallback.Text = "Compaction Fallback Context (tokens):";

        _txtCompactionFallback.Dock = DockStyle.Fill;
        _txtCompactionFallback.Margin = new Padding(4, 6, 4, 4);
        _txtCompactionFallback.Name = "_txtCompactionFallback";
        _txtMaxLogs.Name = "_txtMaxLogs";

        _chkAutoStart.AutoSize = true;
        _chkAutoStart.Margin = new Padding(4, 8, 4, 4);
        _chkAutoStart.Name = "_chkAutoStart";
        _chkAutoStart.Text = "Automatically start proxy on launch";

        _chkStartWithDashboard.AutoSize = true;
        _chkStartWithDashboard.Margin = new Padding(4, 4, 4, 8);
        _chkStartWithDashboard.Name = "_chkStartWithDashboard";
        _chkStartWithDashboard.Text = "Open dashboard window on startup";

        _chkRunAsAdmin.AutoSize = true;
        _chkRunAsAdmin.Margin = new Padding(4, 4, 4, 4);
        _chkRunAsAdmin.Name = "_chkRunAsAdmin";
        _chkRunAsAdmin.Text = "Run as administrator on launch (required to listen on addresses other than localhost)";

        _chkCollectDetails.AutoSize = true;
        _chkCollectDetails.Margin = new Padding(4, 4, 4, 4);
        _chkCollectDetails.Name = "_chkCollectDetails";
        _chkCollectDetails.Text = "Collect request details (captures raw request body into each log entry)";

        _chkCollectResponseDetails.AutoSize = true;
        _chkCollectResponseDetails.Margin = new Padding(4, 4, 4, 4);
        _chkCollectResponseDetails.Name = "_chkCollectResponseDetails";
        _chkCollectResponseDetails.Text = "Collect response details (captures LLM response text into each log entry)";

        _chkDebugMode.AutoSize = true;
        _chkDebugMode.Margin = new Padding(4, 4, 4, 4);
        _chkDebugMode.Name = "_chkDebugMode";
        _chkDebugMode.Text = "Debug mode (log before/after translation details and applied overrides)";

        _chkIrTranslation.AutoSize = true;
        _chkIrTranslation.Margin = new Padding(4, 4, 4, 4);
        _chkIrTranslation.Name = "_chkIrTranslation";
        _chkIrTranslation.Text = "Use IR translation pipeline (experimental: validate the Microsoft.Extensions.AI path against live traffic)";

        // _grpNonProxiedCapture — one toggle per category of request the proxy answers without
        // calling a model, replacing the old all-or-nothing switch. Rejected requests default on
        // so turning automated noise off can never hide an error.
        _grpNonProxiedCapture.AutoSize = true;
        _grpNonProxiedCapture.AutoSizeMode = AutoSizeMode.GrowOnly;
        _grpNonProxiedCapture.Controls.Add(_tlpNonProxiedCapture);
        _grpNonProxiedCapture.Dock = DockStyle.Fill;
        _grpNonProxiedCapture.Margin = new Padding(4, 4, 4, 4);
        _grpNonProxiedCapture.Name = "_grpNonProxiedCapture";
        _grpNonProxiedCapture.Padding = new Padding(6, 2, 6, 4);
        _grpNonProxiedCapture.Text = "Non-proxied request capture";

        _tlpNonProxiedCapture.AutoSize = true;
        _tlpNonProxiedCapture.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpNonProxiedCapture.ColumnCount = 1;
        _tlpNonProxiedCapture.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpNonProxiedCapture.Dock = DockStyle.Fill;
        _tlpNonProxiedCapture.Margin = new Padding(4);
        _tlpNonProxiedCapture.Name = "_tlpNonProxiedCapture";
        _tlpNonProxiedCapture.RowCount = 7;
        _tlpNonProxiedCapture.Controls.Add(_lblNonProxiedCaptureIntro, 0, 0);
        _tlpNonProxiedCapture.Controls.Add(_chkCollectLocalStubs, 0, 1);
        _tlpNonProxiedCapture.Controls.Add(_chkCollectRejectedRequests, 0, 2);
        _tlpNonProxiedCapture.Controls.Add(_chkCollectHealthProbes, 0, 3);
        _tlpNonProxiedCapture.Controls.Add(_chkCollectVersionExplorer, 0, 4);
        _tlpNonProxiedCapture.Controls.Add(_chkCollectCorsPreflight, 0, 5);
        _tlpNonProxiedCapture.Controls.Add(_lblCollectNonProxiedWarning, 0, 6);

        _lblNonProxiedCaptureIntro.AutoSize = true;
        _lblNonProxiedCaptureIntro.Margin = new Padding(4, 4, 4, 4);
        _lblNonProxiedCaptureIntro.Name = "_lblNonProxiedCaptureIntro";
        _lblNonProxiedCaptureIntro.Text = "Requests the proxy answers without calling a model are logged to the Non-proxied tab. Choose which categories to capture:";

        _chkCollectLocalStubs.AutoSize = true;
        _chkCollectLocalStubs.Margin = new Padding(4, 4, 4, 0);
        _chkCollectLocalStubs.Name = "_chkCollectLocalStubs";
        _chkCollectLocalStubs.Text = "Local model stubs (/api/ps, /api/show, /v1/models)";

        _chkCollectRejectedRequests.AutoSize = true;
        _chkCollectRejectedRequests.Margin = new Padding(4, 0, 4, 0);
        _chkCollectRejectedRequests.Name = "_chkCollectRejectedRequests";
        _chkCollectRejectedRequests.Text = "Rejected requests (404, 501, 400, 413, 500, 503)";

        _chkCollectHealthProbes.AutoSize = true;
        _chkCollectHealthProbes.Margin = new Padding(4, 0, 4, 0);
        _chkCollectHealthProbes.Name = "_chkCollectHealthProbes";
        _chkCollectHealthProbes.Text = "Health probes (GET /, HEAD /)";

        _chkCollectVersionExplorer.AutoSize = true;
        _chkCollectVersionExplorer.Margin = new Padding(4, 0, 4, 0);
        _chkCollectVersionExplorer.Name = "_chkCollectVersionExplorer";
        _chkCollectVersionExplorer.Text = "Version and API explorer (/api/version, /scalar, /openapi.json)";

        _chkCollectCorsPreflight.AutoSize = true;
        _chkCollectCorsPreflight.Margin = new Padding(4, 0, 4, 0);
        _chkCollectCorsPreflight.Name = "_chkCollectCorsPreflight";
        _chkCollectCorsPreflight.Text = "CORS preflight (OPTIONS)";

        _lblCollectNonProxiedWarning.AutoSize = true;
        _lblCollectNonProxiedWarning.Margin = new Padding(4, 4, 4, 4);
        _lblCollectNonProxiedWarning.Name = "_lblCollectNonProxiedWarning";
        _lblCollectNonProxiedWarning.ForeColor = Color.DarkOrange;
        _lblCollectNonProxiedWarning.Text = "⚠ Turning a category off stops that traffic from being logged at all. Health probes and version/explorer calls arrive every few seconds, so leaving them on can bury real traffic.";

        _chkPerformanceSampling.AutoSize = true;
        _chkPerformanceSampling.Margin = new Padding(4, 4, 4, 8);
        _chkPerformanceSampling.Name = "_chkPerformanceSampling";
        _chkPerformanceSampling.Text = "Enable performance sampling (CPU and memory monitoring on dashboard)";

        _chkApiExplorer.AutoSize = true;
        _chkApiExplorer.Margin = new Padding(4, 4, 4, 4);
        _chkApiExplorer.Name = "_chkApiExplorer";
        _chkApiExplorer.Text = "Enable API Explorer (Scalar at /scalar)";

        _lblApiExplorerUrl.AutoSize = true;
        _lblApiExplorerUrl.Cursor = Cursors.Hand;
        _lblApiExplorerUrl.Margin = new Padding(4, 0, 4, 8);
        _lblApiExplorerUrl.Name = "_lblApiExplorerUrl";
        _lblApiExplorerUrl.ForeColor = SystemColors.GrayText;
        _lblApiExplorerUrl.Text = "API Explorer URL: (enable to see URL)";

        _lblApiSpecUrl.AutoSize = true;
        _lblApiSpecUrl.Cursor = Cursors.Hand;
        _lblApiSpecUrl.Margin = new Padding(4, 0, 4, 8);
        _lblApiSpecUrl.Name = "_lblApiSpecUrl";
        _lblApiSpecUrl.ForeColor = SystemColors.GrayText;
        _lblApiSpecUrl.Text = "OpenAPI Spec URL: (enable to see URL)";

        _lblMappings.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMappings.AutoSize = true;
        _lblMappings.Margin = new Padding(4, 8, 4, 4);
        _lblMappings.Name = "_lblMappings";
        _lblMappings.Text = "Model Mappings (Proxy → Upstream Model):";

        _dgvMappings.AllowUserToAddRows = false;
        _dgvMappings.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _dgvMappings.ReadOnly = true;
        _dgvMappings.RowHeadersVisible = false;
        _dgvMappings.EditMode = DataGridViewEditMode.EditProgrammatically;
        _dgvMappings.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _dgvMappings.MultiSelect = false;
        _dgvMappings.Columns.Add(_colMappingEnabled);
        _dgvMappings.Columns.Add(_colProxyName);
        _dgvMappings.Columns.Add(_colModelName);
        _dgvMappings.Columns.Add(_colInstructionSet);
        _dgvMappings.Columns.Add(_colReasoningEffort);
        _dgvMappings.Columns.Add(_colVision);
        _dgvMappings.Columns.Add(_colCompactionTarget);
        _dgvMappings.Columns.Add(_colCompactionRedirect);
        _dgvMappings.Dock = DockStyle.Fill;
        _dgvMappings.Margin = new Padding(4, 4, 4, 4);
        _dgvMappings.MinimumSize = new Size(0, 120);
        _dgvMappings.Name = "_dgvMappings";
        _dgvMappings.CellDoubleClick += DgvMappings_CellDoubleClick;

        _colMappingEnabled.HeaderText = "Enabled";
        _colMappingEnabled.Name = "_colMappingEnabled";
        _colMappingEnabled.Width = 70;
        _colMappingEnabled.FillWeight = 45;

        _colProxyName.HeaderText = "Proxy Name";
        _colProxyName.Name = "_colProxyName";

        _colModelName.HeaderText = "Model Name";
        _colModelName.Name = "_colModelName";
        _colModelName.FillWeight = 120;

        _colInstructionSet.HeaderText = "Instruction Set";
        _colInstructionSet.Name = "_colInstructionSet";
        _colInstructionSet.FillWeight = 120;
        _colInstructionSet.DefaultCellStyle.NullValue = string.Empty;

        _colReasoningEffort.HeaderText = "Reasoning Effort";
        _colReasoningEffort.Name = "_colReasoningEffort";
        _colReasoningEffort.FillWeight = 90;
        _colReasoningEffort.DefaultCellStyle.NullValue = string.Empty;

        _colVision.HeaderText = "Vision";
        _colVision.Name = "_colVision";
        _colVision.FillWeight = 50;

        _colCompactionTarget.HeaderText = "Compaction Model";
        _colCompactionTarget.Name = "_colCompactionTarget";
        _colCompactionTarget.FillWeight = 110;
        _colCompactionTarget.DefaultCellStyle.NullValue = string.Empty;

        _colCompactionRedirect.HeaderText = "Redirect /compact";
        _colCompactionRedirect.Name = "_colCompactionRedirect";
        _colCompactionRedirect.FillWeight = 70;

        _flpMappingButtons.AutoSize = true;
        _flpMappingButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpMappingButtons.Controls.Add(_btnAddMapping);
        _flpMappingButtons.Controls.Add(_btnRemoveMapping);
        _flpMappingButtons.Controls.Add(_btnDuplicateMapping);
        _flpMappingButtons.Controls.Add(_btnConfigureMapping);
        _flpMappingButtons.Controls.Add(_btnToggleEnabled);
        _flpMappingButtons.Dock = DockStyle.Fill;
        _flpMappingButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpMappingButtons.Margin = new Padding(0, 4, 0, 4);
        _flpMappingButtons.Name = "_flpMappingButtons";
        _flpMappingButtons.WrapContents = false;

        _btnAddMapping.AutoSize = true;
        _btnAddMapping.Margin = new Padding(4, 4, 4, 4);
        _btnAddMapping.Name = "_btnAddMapping";
        _btnAddMapping.Text = "Add Mapping";
        _btnAddMapping.Click += BtnAddMapping_Click;

        _btnRemoveMapping.AutoSize = true;
        _btnRemoveMapping.Margin = new Padding(4, 4, 4, 4);
        _btnRemoveMapping.Name = "_btnRemoveMapping";
        _btnRemoveMapping.Text = "Remove Mapping";
        _btnRemoveMapping.Click += BtnRemoveMapping_Click;

        _btnDuplicateMapping.AutoSize = true;
        _btnDuplicateMapping.Margin = new Padding(4, 4, 4, 4);
        _btnDuplicateMapping.Name = "_btnDuplicateMapping";
        _btnDuplicateMapping.Text = "Duplicate Selected";
        _btnDuplicateMapping.Click += BtnDuplicateMapping_Click;

        _btnConfigureMapping.AutoSize = true;
        _btnConfigureMapping.Margin = new Padding(4, 4, 4, 4);
        _btnConfigureMapping.Name = "_btnConfigureMapping";
        _btnConfigureMapping.Text = "Configure Selected…";
        _btnConfigureMapping.Click += BtnConfigureMapping_Click;

        _btnToggleEnabled.AutoSize = true;
        _btnToggleEnabled.Margin = new Padding(4, 4, 4, 4);
        _btnToggleEnabled.Name = "_btnToggleEnabled";
        _btnToggleEnabled.Text = "Toggle Enabled";
        _btnToggleEnabled.Click += BtnToggleEnabled_Click;

        _btnSaveListener.Anchor = AnchorStyles.Right;
        _btnSaveListener.AutoSize = true;
        _btnSaveListener.Margin = new Padding(4, 8, 4, 4);
        _btnSaveListener.Name = "_btnSaveListener";
        _btnSaveListener.Text = "Save";
        _btnSaveListener.Click += BtnSaveListener_Click;

        // _grpLogging
        _grpLogging.AutoSize = true;
        _grpLogging.AutoSizeMode = AutoSizeMode.GrowOnly;
        _grpLogging.Controls.Add(_tlpLogging);
        _grpLogging.Dock = DockStyle.Fill;
        _grpLogging.Margin = new Padding(4, 8, 4, 4);
        _grpLogging.Name = "_grpLogging";
        _grpLogging.Text = "Logging";

        // _tlpLogging
        _tlpLogging.AutoSize = true;
        _tlpLogging.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpLogging.ColumnCount = 2;
        _tlpLogging.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpLogging.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpLogging.Dock = DockStyle.Fill;
        _tlpLogging.Margin = new Padding(4);
        _tlpLogging.Name = "_tlpLogging";
        _tlpLogging.RowCount = 7;
        _tlpLogging.Controls.Add(_lblLogDir, 0, 0);
        _tlpLogging.Controls.Add(_txtLogDir, 1, 0);
        _tlpLogging.Controls.Add(_lblMinLevel, 0, 1);
        _tlpLogging.Controls.Add(_cmbMinLevel, 1, 1);
        _tlpLogging.Controls.Add(_lblAppLogSize, 0, 2);
        _tlpLogging.Controls.Add(_txtAppLogSize, 1, 2);
        _tlpLogging.Controls.Add(_lblAppLogRetain, 0, 3);
        _tlpLogging.Controls.Add(_txtAppLogRetain, 1, 3);
        _tlpLogging.Controls.Add(_lblReqLogSize, 0, 4);
        _tlpLogging.Controls.Add(_txtReqLogSize, 1, 4);
        _tlpLogging.Controls.Add(_lblRequestDbPath, 0, 5);
        _tlpLogging.Controls.Add(_tlpRequestDbPath, 1, 5);
        _tlpLogging.Controls.Add(_lblLogRetention, 0, 6);
        _tlpLogging.Controls.Add(_txtLogRetention, 1, 6);

        _lblLogDir.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblLogDir.AutoSize = true;
        _lblLogDir.Margin = new Padding(4, 8, 8, 4);
        _lblLogDir.Name = "_lblLogDir";
        _lblLogDir.Text = "Log Directory:";

        _txtLogDir.Dock = DockStyle.Fill;
        _txtLogDir.Margin = new Padding(4, 6, 4, 4);
        _txtLogDir.Name = "_txtLogDir";

        _lblMinLevel.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMinLevel.AutoSize = true;
        _lblMinLevel.Margin = new Padding(4, 8, 8, 4);
        _lblMinLevel.Name = "_lblMinLevel";
        _lblMinLevel.Text = "Minimum Level:";

        _cmbMinLevel.Dock = DockStyle.Fill;
        _cmbMinLevel.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMinLevel.Items.AddRange(new object[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" });
        _cmbMinLevel.Margin = new Padding(4, 6, 4, 4);
        _cmbMinLevel.Name = "_cmbMinLevel";

        _lblAppLogSize.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblAppLogSize.AutoSize = true;
        _lblAppLogSize.Margin = new Padding(4, 8, 8, 4);
        _lblAppLogSize.Name = "_lblAppLogSize";
        _lblAppLogSize.Text = "App Log File Limit (MB):";

        _txtAppLogSize.Dock = DockStyle.Fill;
        _txtAppLogSize.Margin = new Padding(4, 6, 4, 4);
        _txtAppLogSize.Name = "_txtAppLogSize";

        _lblAppLogRetain.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblAppLogRetain.AutoSize = true;
        _lblAppLogRetain.Margin = new Padding(4, 8, 8, 4);
        _lblAppLogRetain.Name = "_lblAppLogRetain";
        _lblAppLogRetain.Text = "App Log Files to Keep:";

        _txtAppLogRetain.Dock = DockStyle.Fill;
        _txtAppLogRetain.Margin = new Padding(4, 6, 4, 4);
        _txtAppLogRetain.Name = "_txtAppLogRetain";

        _lblReqLogSize.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblReqLogSize.AutoSize = true;
        _lblReqLogSize.Margin = new Padding(4, 8, 8, 4);
        _lblReqLogSize.Name = "_lblReqLogSize";
        _lblReqLogSize.Text = "Database File Limit (MB):";

        _txtReqLogSize.Dock = DockStyle.Fill;
        _txtReqLogSize.Margin = new Padding(4, 6, 4, 4);
        _txtReqLogSize.Name = "_txtReqLogSize";

        _lblRequestDbPath.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblRequestDbPath.AutoSize = true;
        _lblRequestDbPath.Margin = new Padding(4, 8, 8, 4);
        _lblRequestDbPath.Name = "_lblRequestDbPath";
        _lblRequestDbPath.Text = "Application Database:";

        _tlpRequestDbPath.ColumnCount = 2;
        _tlpRequestDbPath.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpRequestDbPath.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpRequestDbPath.Controls.Add(_txtRequestDbPath, 0, 0);
        _tlpRequestDbPath.Controls.Add(_btnBrowseRequestDb, 1, 0);
        _tlpRequestDbPath.Dock = DockStyle.Fill;
        _tlpRequestDbPath.Margin = new Padding(0);
        _tlpRequestDbPath.Name = "_tlpRequestDbPath";
        _tlpRequestDbPath.RowCount = 1;

        _txtRequestDbPath.Dock = DockStyle.Fill;
        _txtRequestDbPath.Margin = new Padding(4, 6, 4, 4);
        _txtRequestDbPath.Name = "_txtRequestDbPath";

        _btnBrowseRequestDb.AutoSize = true;
        _btnBrowseRequestDb.Margin = new Padding(4, 4, 4, 4);
        _btnBrowseRequestDb.Name = "_btnBrowseRequestDb";
        _btnBrowseRequestDb.Text = "Browse…";
        _btnBrowseRequestDb.Click += BtnBrowseRequestDb_Click;

        _lblLogRetention.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblLogRetention.AutoSize = true;
        _lblLogRetention.Margin = new Padding(4, 8, 8, 4);
        _lblLogRetention.Name = "_lblLogRetention";
        _lblLogRetention.Text = "Log Retention (hours, 0=forever):";

        _txtLogRetention.Dock = DockStyle.Fill;
        _txtLogRetention.Margin = new Padding(4, 6, 4, 4);
        _txtLogRetention.Name = "_txtLogRetention";

        // _refreshTimer
        _refreshTimer.Interval = 1500;
        _refreshTimer.Tick += RefreshTimer_Tick;

        // ── Instructions tab ───────────────────────────────────────────────────

        // _tabInstructions
        _tabInstructions.Controls.Add(_tlpInstructions);
        _tabInstructions.Dock = DockStyle.Fill;
        _tabInstructions.Name = "_tabInstructions";
        _tabInstructions.Padding = new Padding(8);
        _tabInstructions.Text = "Instructions";

        // _tlpInstructions — 1 column, 4 rows: list | buttons | preview label | preview text
        _tlpInstructions.ColumnCount = 1;
        _tlpInstructions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpInstructions.Controls.Add(_lstInstructions, 0, 0);
        _tlpInstructions.Controls.Add(_flpInstructionButtons, 0, 1);
        _tlpInstructions.Controls.Add(_lblInstructionPreview, 0, 2);
        _tlpInstructions.Controls.Add(_txtInstructionPreview, 0, 3);
        _tlpInstructions.Dock = DockStyle.Fill;
        _tlpInstructions.Name = "_tlpInstructions";
        _tlpInstructions.RowCount = 4;
        _tlpInstructions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        _tlpInstructions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpInstructions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpInstructions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

        // _lstInstructions
        _lstInstructions.Columns.Add(_colInstrName);
        _lstInstructions.Columns.Add(_colInstrDescription);
        _lstInstructions.Dock = DockStyle.Fill;
        _lstInstructions.FullRowSelect = true;
        _lstInstructions.GridLines = true;
        _lstInstructions.Margin = new Padding(0, 0, 0, 8);
        _lstInstructions.MultiSelect = false;
        _lstInstructions.Name = "_lstInstructions";
        _lstInstructions.View = View.Details;
        _lstInstructions.SelectedIndexChanged += LstInstructions_SelectedIndexChanged;
        _lstInstructions.DoubleClick += LstInstructions_DoubleClick;

        _colInstrName.Text = "Name";
        _colInstrName.Width = 200;
        _colInstrDescription.Text = "Description";
        _colInstrDescription.Width = 400;

        // _flpInstructionButtons
        _flpInstructionButtons.AutoSize = true;
        _flpInstructionButtons.Controls.Add(_btnAddInstruction);
        _flpInstructionButtons.Controls.Add(_btnEditInstruction);
        _flpInstructionButtons.Controls.Add(_btnRemoveInstruction);
        _flpInstructionButtons.Dock = DockStyle.Fill;
        _flpInstructionButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpInstructionButtons.Margin = new Padding(0, 0, 0, 8);
        _flpInstructionButtons.Name = "_flpInstructionButtons";
        _flpInstructionButtons.WrapContents = false;

        _btnAddInstruction.AutoSize = true;
        _btnAddInstruction.Margin = new Padding(0, 0, 8, 0);
        _btnAddInstruction.Name = "_btnAddInstruction";
        _btnAddInstruction.Text = "Add New";
        _btnAddInstruction.Click += BtnAddInstruction_Click;

        _btnEditInstruction.AutoSize = true;
        _btnEditInstruction.Margin = new Padding(0, 0, 8, 0);
        _btnEditInstruction.Name = "_btnEditInstruction";
        _btnEditInstruction.Text = "Edit";
        _btnEditInstruction.Click += BtnEditInstruction_Click;

        _btnRemoveInstruction.AutoSize = true;
        _btnRemoveInstruction.Margin = new Padding(0, 0, 8, 0);
        _btnRemoveInstruction.Name = "_btnRemoveInstruction";
        _btnRemoveInstruction.Text = "Remove";
        _btnRemoveInstruction.Click += BtnRemoveInstruction_Click;

        // _lblInstructionPreview
        _lblInstructionPreview.AutoSize = true;
        _lblInstructionPreview.Dock = DockStyle.Fill;
        _lblInstructionPreview.Margin = new Padding(0, 0, 0, 4);
        _lblInstructionPreview.Name = "_lblInstructionPreview";
        _lblInstructionPreview.Text = "Preview:";

        // _txtInstructionPreview
        _txtInstructionPreview.Dock = DockStyle.Fill;
        _txtInstructionPreview.Margin = new Padding(0);
        _txtInstructionPreview.Multiline = true;
        _txtInstructionPreview.Name = "_txtInstructionPreview";
        _txtInstructionPreview.ReadOnly = true;
        _txtInstructionPreview.ScrollBars = ScrollBars.Vertical;

        // ── Credentials tab ─────────────────────────────────────────────────

        // _tabCredentials
        _tabCredentials.Controls.Add(_tlpCredentials);
        _tabCredentials.Dock = DockStyle.Fill;
        _tabCredentials.Name = "_tabCredentials";
        _tabCredentials.Padding = new Padding(8);
        _tabCredentials.Text = "Credentials";

        // _tlpCredentials — 1 column, 2 rows: list | buttons
        _tlpCredentials.ColumnCount = 1;
        _tlpCredentials.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpCredentials.Controls.Add(_lstCredentials, 0, 0);
        _tlpCredentials.Controls.Add(_flpCredentialButtons, 0, 1);
        _tlpCredentials.Dock = DockStyle.Fill;
        _tlpCredentials.Name = "_tlpCredentials";
        _tlpCredentials.RowCount = 2;
        _tlpCredentials.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tlpCredentials.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // _lstCredentials
        _lstCredentials.Columns.Add(_colCredName);
        _lstCredentials.Columns.Add(_colCredDescription);
        _lstCredentials.Dock = DockStyle.Fill;
        _lstCredentials.FullRowSelect = true;
        _lstCredentials.GridLines = true;
        _lstCredentials.Margin = new Padding(0, 0, 0, 8);
        _lstCredentials.MultiSelect = false;
        _lstCredentials.Name = "_lstCredentials";
        _lstCredentials.View = View.Details;
        _lstCredentials.DoubleClick += LstCredentials_DoubleClick;

        _colCredName.Text = "Name";
        _colCredName.Width = 200;
        _colCredDescription.Text = "Description";
        _colCredDescription.Width = 400;

        // _flpCredentialButtons
        _flpCredentialButtons.AutoSize = true;
        _flpCredentialButtons.Controls.Add(_btnAddCredential);
        _flpCredentialButtons.Controls.Add(_btnEditCredential);
        _flpCredentialButtons.Controls.Add(_btnRemoveCredential);
        _flpCredentialButtons.Dock = DockStyle.Fill;
        _flpCredentialButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpCredentialButtons.Margin = new Padding(0);
        _flpCredentialButtons.Name = "_flpCredentialButtons";
        _flpCredentialButtons.WrapContents = false;

        _btnAddCredential.AutoSize = true;
        _btnAddCredential.Margin = new Padding(0, 0, 8, 0);
        _btnAddCredential.Name = "_btnAddCredential";
        _btnAddCredential.Text = "Add New";
        _btnAddCredential.Click += BtnAddCredential_Click;

        _btnEditCredential.AutoSize = true;
        _btnEditCredential.Margin = new Padding(0, 0, 8, 0);
        _btnEditCredential.Name = "_btnEditCredential";
        _btnEditCredential.Text = "Edit";
        _btnEditCredential.Click += BtnEditCredential_Click;

        _btnRemoveCredential.AutoSize = true;
        _btnRemoveCredential.Margin = new Padding(0, 0, 8, 0);
        _btnRemoveCredential.Name = "_btnRemoveCredential";
        _btnRemoveCredential.Text = "Remove";
        _btnRemoveCredential.Click += BtnRemoveCredential_Click;

        // ── Modules tab ───────────────────────────────────────────────────

        // _tabModules
        _tabModules.Controls.Add(_tlpModules);
        _tabModules.Dock = DockStyle.Fill;
        _tabModules.Name = "_tabModules";
        _tabModules.Padding = new Padding(8);
        _tabModules.Text = "Modules";

        // _tlpModules — 1 column, 4 rows: note | list | status | buttons
        _tlpModules.ColumnCount = 1;
        _tlpModules.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpModules.Controls.Add(_lblModulesNote, 0, 0);
        _tlpModules.Controls.Add(_lstModules, 0, 1);
        _tlpModules.Controls.Add(_lblModuleStatus, 0, 2);
        _tlpModules.Controls.Add(_flpModuleButtons, 0, 3);
        _tlpModules.Dock = DockStyle.Fill;
        _tlpModules.Name = "_tlpModules";
        _tlpModules.RowCount = 4;
        _tlpModules.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpModules.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tlpModules.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpModules.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lblModulesNote.AutoSize = true;
        _lblModulesNote.Margin = new Padding(0, 0, 0, 8);
        _lblModulesNote.Name = "_lblModulesNote";
        _lblModulesNote.Text = "Extend the proxy with optional modules. Import a module assembly (.dll) built against the Kaeo LLM Proxy Modules contracts library.";

        // _lstModules
        _lstModules.Columns.Add(_colModuleName);
        _lstModules.Columns.Add(_colModuleVersion);
        _lstModules.Columns.Add(_colModuleState);
        _lstModules.Columns.Add(_colModulePath);
        _lstModules.Dock = DockStyle.Fill;
        _lstModules.FullRowSelect = true;
        _lstModules.GridLines = true;
        _lstModules.Margin = new Padding(0, 0, 0, 8);
        _lstModules.MultiSelect = false;
        _lstModules.Name = "_lstModules";
        _lstModules.View = View.Details;
        _lstModules.SelectedIndexChanged += LstModules_SelectedIndexChanged;

        _colModuleName.Text = "Name";
        _colModuleName.Width = 180;
        _colModuleVersion.Text = "Version";
        _colModuleVersion.Width = 80;
        _colModuleState.Text = "State";
        _colModuleState.Width = 80;
        _colModulePath.Text = "Path";
        _colModulePath.Width = 360;

        // _lblModuleStatus — shows the selected module's last error or path
        _lblModuleStatus.AutoSize = true;
        _lblModuleStatus.ForeColor = SystemColors.GrayText;
        _lblModuleStatus.Margin = new Padding(0, 0, 0, 8);
        _lblModuleStatus.Name = "_lblModuleStatus";

        // _flpModuleButtons
        _flpModuleButtons.AutoSize = true;
        _flpModuleButtons.Controls.Add(_btnImportModule);
        _flpModuleButtons.Controls.Add(_btnToggleModule);
        _flpModuleButtons.Controls.Add(_btnRemoveModule);
        _flpModuleButtons.Dock = DockStyle.Fill;
        _flpModuleButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpModuleButtons.Margin = new Padding(0);
        _flpModuleButtons.Name = "_flpModuleButtons";
        _flpModuleButtons.WrapContents = false;

        _btnImportModule.AutoSize = true;
        _btnImportModule.Margin = new Padding(0, 0, 8, 0);
        _btnImportModule.Name = "_btnImportModule";
        _btnImportModule.Text = "Import Module...";
        _btnImportModule.Click += BtnImportModule_Click;

        _btnToggleModule.AutoSize = true;
        _btnToggleModule.Enabled = false;
        _btnToggleModule.Margin = new Padding(0, 0, 8, 0);
        _btnToggleModule.Name = "_btnToggleModule";
        _btnToggleModule.Text = "Enable/Disable";
        _btnToggleModule.Click += BtnToggleModule_Click;

        _btnRemoveModule.AutoSize = true;
        _btnRemoveModule.Enabled = false;
        _btnRemoveModule.Margin = new Padding(0, 0, 8, 0);
        _btnRemoveModule.Name = "_btnRemoveModule";
        _btnRemoveModule.Text = "Remove";
        _btnRemoveModule.Click += BtnRemoveModule_Click;

        // ── MCP tab ─────────────────────────────────────────────────────────

        // _tabMcp — hosts the nested MCP sub-tabs (server settings, module registry, module pages)
        _tabMcp.Controls.Add(_mcpSubTabs);
        _tabMcp.Dock = DockStyle.Fill;
        _tabMcp.Name = "_tabMcp";
        _tabMcp.Text = "MCP";

        // _mcpSubTabs
        _mcpSubTabs.Controls.Add(_mcpServerPage);
        _mcpSubTabs.Controls.Add(_tabModules);
        _mcpSubTabs.Dock = DockStyle.Fill;
        _mcpSubTabs.Name = "_mcpSubTabs";

        // _mcpServerPage
        _mcpServerPage.Controls.Add(_tlpMcp);
        _mcpServerPage.Dock = DockStyle.Fill;
        _mcpServerPage.Name = "_mcpServerPage";
        _mcpServerPage.Padding = new Padding(8);
        _mcpServerPage.Text = "Server";

        // _tlpMcp — 2 columns: caption AutoSize | content Percent
        _tlpMcp.ColumnCount = 2;
        _tlpMcp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMcp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMcp.Controls.Add(_chkMcpEnabled, 0, 0);
        _tlpMcp.Controls.Add(_chkMcpApiExplorer, 0, 1);
        _tlpMcp.Controls.Add(_lblMcpApiExplorerUrl, 0, 2);
        _tlpMcp.Controls.Add(_lblMcpSpecUrl, 0, 3);
        _tlpMcp.Controls.Add(_lblMcpPort, 0, 4);
        _tlpMcp.Controls.Add(_nudMcpPort, 1, 4);
        _tlpMcp.Controls.Add(_lblMcpListenAddress, 0, 5);
        _tlpMcp.Controls.Add(_cboMcpListenAddress, 1, 5);
        _tlpMcp.Controls.Add(_chkMcpCollectRequest, 0, 6);
        _tlpMcp.Controls.Add(_chkMcpCollectResponse, 0, 7);
        _tlpMcp.Controls.Add(_lblMcpStatusCaption, 0, 8);
        _tlpMcp.Controls.Add(_lblMcpStatus, 1, 8);
        _tlpMcp.Controls.Add(_flpMcpButtons, 0, 10);
        _tlpMcp.Dock = DockStyle.Fill;
        _tlpMcp.Name = "_tlpMcp";
        _tlpMcp.RowCount = 11;
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tlpMcp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMcp.SetColumnSpan(_chkMcpEnabled, 2);
        _tlpMcp.SetColumnSpan(_chkMcpApiExplorer, 2);
        _tlpMcp.SetColumnSpan(_chkMcpCollectRequest, 2);
        _tlpMcp.SetColumnSpan(_chkMcpCollectResponse, 2);
        _tlpMcp.SetColumnSpan(_lblMcpApiExplorerUrl, 2);
        _tlpMcp.SetColumnSpan(_lblMcpSpecUrl, 2);
        _tlpMcp.SetColumnSpan(_flpMcpButtons, 2);

        _chkMcpEnabled.AccessibleName = "Enable MCP server";
        _chkMcpEnabled.AutoSize = true;
        _chkMcpEnabled.Margin = new Padding(0, 0, 0, 8);
        _chkMcpEnabled.Name = "_chkMcpEnabled";
        _chkMcpEnabled.Text = "Enable MCP server (Streamable HTTP at /mcp)";

        _chkMcpApiExplorer.AccessibleName = "Enable MCP API Explorer";
        _chkMcpApiExplorer.AutoSize = true;
        _chkMcpApiExplorer.Margin = new Padding(0, 0, 0, 4);
        _chkMcpApiExplorer.Name = "_chkMcpApiExplorer";
        _chkMcpApiExplorer.Text = "Enable API Explorer (Scalar at /scalar)";

        _lblMcpApiExplorerUrl.AutoSize = true;
        _lblMcpApiExplorerUrl.ForeColor = SystemColors.GrayText;
        _lblMcpApiExplorerUrl.Margin = new Padding(0, 0, 0, 8);
        _lblMcpApiExplorerUrl.Name = "_lblMcpApiExplorerUrl";
        _lblMcpApiExplorerUrl.Text = "API Explorer URL: (enable to see URL)";

        _lblMcpSpecUrl.AutoSize = true;
        _lblMcpSpecUrl.ForeColor = SystemColors.GrayText;
        _lblMcpSpecUrl.Margin = new Padding(0, 0, 0, 8);
        _lblMcpSpecUrl.Name = "_lblMcpSpecUrl";
        _lblMcpSpecUrl.Text = "OpenAPI Spec URL: (enable to see URL)";

        _lblMcpListenAddress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMcpListenAddress.AutoSize = true;
        _lblMcpListenAddress.Name = "_lblMcpListenAddress";
        _lblMcpListenAddress.Text = "Listen address";

        _cboMcpListenAddress.AccessibleName = "MCP listen address";
        _cboMcpListenAddress.Dock = DockStyle.Fill;
        _cboMcpListenAddress.DropDownStyle = ComboBoxStyle.DropDown;
        _cboMcpListenAddress.Margin = new Padding(0, 0, 0, 8);
        _cboMcpListenAddress.Name = "_cboMcpListenAddress";

        _chkMcpCollectRequest.AccessibleName = "Collect MCP request details";
        _chkMcpCollectRequest.AutoSize = true;
        _chkMcpCollectRequest.Margin = new Padding(0, 4, 0, 4);
        _chkMcpCollectRequest.Name = "_chkMcpCollectRequest";
        _chkMcpCollectRequest.Text = "Log request bodies";

        _chkMcpCollectResponse.AccessibleName = "Collect MCP response details";
        _chkMcpCollectResponse.AutoSize = true;
        _chkMcpCollectResponse.Margin = new Padding(0, 4, 0, 8);
        _chkMcpCollectResponse.Name = "_chkMcpCollectResponse";
        _chkMcpCollectResponse.Text = "Log response bodies";

        _lblMcpPort.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMcpPort.AutoSize = true;
        _lblMcpPort.Name = "_lblMcpPort";
        _lblMcpPort.Text = "Port";

        _nudMcpPort.AccessibleName = "MCP port";
        _nudMcpPort.Anchor = AnchorStyles.Left;
        _nudMcpPort.Margin = new Padding(0, 0, 0, 8);
        _nudMcpPort.Maximum = 65535;
        _nudMcpPort.Minimum = 1;
        _nudMcpPort.Name = "_nudMcpPort";

        _lblMcpStatusCaption.AutoSize = true;
        _lblMcpStatusCaption.Name = "_lblMcpStatusCaption";
        _lblMcpStatusCaption.Text = "Status";

        _lblMcpStatus.AutoSize = true;
        _lblMcpStatus.Name = "_lblMcpStatus";
        _lblMcpStatus.Text = "Stopped";

        _flpMcpButtons.AutoSize = true;
        _flpMcpButtons.Controls.Add(_btnMcpApply);
        _flpMcpButtons.Dock = DockStyle.Fill;
        _flpMcpButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpMcpButtons.Margin = new Padding(0);
        _flpMcpButtons.Name = "_flpMcpButtons";
        _flpMcpButtons.WrapContents = false;

        _btnMcpApply.AccessibleName = "Apply and restart MCP server";
        _btnMcpApply.AutoSize = true;
        _btnMcpApply.Name = "_btnMcpApply";
        _btnMcpApply.Text = "Apply && Restart";

        // ── Test Console tab ──────────────────────────────────────────────────

        // _tabTest
        _tabTest.Controls.Add(_tlpTestOuter);
        _tabTest.Dock = DockStyle.Fill;
        _tabTest.Name = "_tabTest";
        _tabTest.Padding = new Padding(8);
        _tabTest.Text = "Test Console";

        // _tlpTestOuter — 1 column, 4 rows: top bar | prompt label | prompt | response+status
        _tlpTestOuter.ColumnCount = 1;
        _tlpTestOuter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpTestOuter.Controls.Add(_tlpTestTop, 0, 0);
        _tlpTestOuter.Controls.Add(_txtTestPrompt, 0, 1);
        _tlpTestOuter.Controls.Add(_txtTestResponse, 0, 2);
        _tlpTestOuter.Controls.Add(_lblTestStatus, 0, 3);
        _tlpTestOuter.Dock = DockStyle.Fill;
        _tlpTestOuter.Name = "_tlpTestOuter";
        _tlpTestOuter.RowCount = 4;
        _tlpTestOuter.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpTestOuter.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpTestOuter.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tlpTestOuter.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // _tlpTestTop — model label | combo | temp label | nud | repeat penalty label | nud | Send | Cancel | Clear
        _tlpTestTop.AutoSize = true;
        _tlpTestTop.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpTestTop.ColumnCount = 9;
        _tlpTestTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpTestTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpTestTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpTestTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpTestTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpTestTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpTestTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpTestTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpTestTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpTestTop.Controls.Add(_lblTestModel, 0, 0);
        _tlpTestTop.Controls.Add(_cmbTestModel, 1, 0);
        _tlpTestTop.Controls.Add(_lblTestTemp, 2, 0);
        _tlpTestTop.Controls.Add(_nudTestTemp, 3, 0);
        _tlpTestTop.Controls.Add(_lblTestRepeatPenalty, 4, 0);
        _tlpTestTop.Controls.Add(_nudTestRepeatPenalty, 5, 0);
        _tlpTestTop.Controls.Add(_btnTestSend, 6, 0);
        _tlpTestTop.Controls.Add(_btnTestCancel, 7, 0);
        _tlpTestTop.Controls.Add(_btnTestClear, 8, 0);
        _tlpTestTop.Dock = DockStyle.Fill;
        _tlpTestTop.Margin = new Padding(0, 0, 0, 6);
        _tlpTestTop.Name = "_tlpTestTop";
        _tlpTestTop.RowCount = 1;

        _lblTestModel.Anchor = AnchorStyles.Left;
        _lblTestModel.AutoSize = true;
        _lblTestModel.Margin = new Padding(0, 0, 6, 0);
        _lblTestModel.Name = "_lblTestModel";
        _lblTestModel.Text = "Model:";

        _cmbTestModel.Dock = DockStyle.Fill;
        _cmbTestModel.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbTestModel.Margin = new Padding(0, 2, 8, 2);
        _cmbTestModel.Name = "_cmbTestModel";
        _cmbTestModel.SelectedIndexChanged += CmbTestModel_SelectedIndexChanged;

        _lblTestTemp.Anchor = AnchorStyles.Left;
        _lblTestTemp.AutoSize = true;
        _lblTestTemp.Margin = new Padding(0, 0, 4, 0);
        _lblTestTemp.Name = "_lblTestTemp";
        _lblTestTemp.Text = "Temp:";

        _nudTestTemp.DecimalPlaces = 2;
        _nudTestTemp.Increment = new decimal(new int[] { 5, 0, 0, 131072 });
        _nudTestTemp.Maximum = new decimal(new int[] { 2, 0, 0, 0 });
        _nudTestTemp.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
        _nudTestTemp.Margin = new Padding(0, 2, 8, 2);
        _nudTestTemp.Name = "_nudTestTemp";
        _nudTestTemp.Size = new Size(64, 25);
        _nudTestTemp.Value = new decimal(new int[] { 70, 0, 0, 131072 });

        _lblTestRepeatPenalty.Anchor = AnchorStyles.Left;
        _lblTestRepeatPenalty.AutoSize = true;
        _lblTestRepeatPenalty.Margin = new Padding(0, 0, 4, 0);
        _lblTestRepeatPenalty.Name = "_lblTestRepeatPenalty";
        _lblTestRepeatPenalty.Text = "Penalty:";

        _nudTestRepeatPenalty.DecimalPlaces = 2;
        _nudTestRepeatPenalty.Increment = new decimal(new int[] { 5, 0, 0, 131072 });
        _nudTestRepeatPenalty.Maximum = new decimal(new int[] { 2, 0, 0, 0 });
        _nudTestRepeatPenalty.Minimum = new decimal(new int[] { 5, 0, 0, 65536 });
        _nudTestRepeatPenalty.Margin = new Padding(0, 2, 8, 2);
        _nudTestRepeatPenalty.Name = "_nudTestRepeatPenalty";
        _nudTestRepeatPenalty.Size = new Size(64, 25);
        _nudTestRepeatPenalty.Value = new decimal(new int[] { 1, 0, 0, 0 });

        _btnTestSend.Margin = new Padding(0, 2, 4, 2);
        _btnTestSend.Name = "_btnTestSend";
        _btnTestSend.Size = new Size(80, 28);
        _btnTestSend.Text = "Send";
        _btnTestSend.Click += BtnTestSend_Click;

        _btnTestCancel.Margin = new Padding(0, 2, 4, 2);
        _btnTestCancel.Name = "_btnTestCancel";
        _btnTestCancel.Size = new Size(80, 28);
        _btnTestCancel.Text = "Cancel";
        _btnTestCancel.Enabled = false;
        _btnTestCancel.Click += BtnTestCancel_Click;

        _btnTestClear.Margin = new Padding(0, 2, 0, 2);
        _btnTestClear.Name = "_btnTestClear";
        _btnTestClear.Size = new Size(80, 28);
        _btnTestClear.Text = "Clear";
        _btnTestClear.Click += BtnTestClear_Click;

        _txtTestPrompt.Dock = DockStyle.Fill;
        _txtTestPrompt.Margin = new Padding(0, 0, 0, 4);
        _txtTestPrompt.Multiline = true;
        _txtTestPrompt.Name = "_txtTestPrompt";
        _txtTestPrompt.PlaceholderText = "Enter your prompt here…";
        _txtTestPrompt.ScrollBars = ScrollBars.Vertical;
        _txtTestPrompt.Size = new Size(100, 66);
        _txtTestPrompt.KeyDown += TxtTestPrompt_KeyDown;

        _txtTestResponse.BackColor = SystemColors.Window;
        _txtTestResponse.Dock = DockStyle.Fill;
        _txtTestResponse.Font = new Font("Consolas", 9F);
        _txtTestResponse.Margin = new Padding(0, 0, 0, 4);
        _txtTestResponse.Multiline = true;
        _txtTestResponse.Name = "_txtTestResponse";
        _txtTestResponse.ReadOnly = true;
        _txtTestResponse.ScrollBars = ScrollBars.Vertical;
        _txtTestResponse.WordWrap = true;

        _lblTestStatus.AutoSize = true;
        _lblTestStatus.Dock = DockStyle.Fill;
        _lblTestStatus.Margin = new Padding(0, 2, 0, 0);
        _lblTestStatus.Name = "_lblTestStatus";
        _lblTestStatus.Text = "Ready";

        // ── Heartbeats tab ────────────────────────────────────────────────────

        _tabHeartbeats.Controls.Add(_tlpHeartbeats);
        _tabHeartbeats.Dock = DockStyle.Fill;
        _tabHeartbeats.Name = "_tabHeartbeats";
        _tabHeartbeats.Padding = new Padding(8);
        _tabHeartbeats.Text = "Heartbeats";

        // _tabSysLogs
        _tabSysLogs.Controls.Add(_tlpSysLogs);
        _tabSysLogs.Dock = DockStyle.Fill;
        _tabSysLogs.Name = "_tabSysLogs";
        _tabSysLogs.Padding = new Padding(8);
        _tabSysLogs.Text = "System Logs";

        // _tlpSysLogs — 1 column: top bar AutoSize | list fills remaining
        _tlpSysLogs.ColumnCount = 1;
        _tlpSysLogs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpSysLogs.Controls.Add(_flpSysLogTop, 0, 0);
        _tlpSysLogs.Controls.Add(_lstSysLogs, 0, 1);
        _tlpSysLogs.Dock = DockStyle.Fill;
        _tlpSysLogs.Name = "_tlpSysLogs";
        _tlpSysLogs.RowCount = 2;
        _tlpSysLogs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpSysLogs.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // _flpSysLogTop
        _flpSysLogTop.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _flpSysLogTop.AutoSize = true;
        _flpSysLogTop.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpSysLogTop.FlowDirection = FlowDirection.LeftToRight;
        _flpSysLogTop.Margin = new Padding(0, 0, 0, 4);
        _flpSysLogTop.Name = "_flpSysLogTop";
        _flpSysLogTop.WrapContents = false;
        _flpSysLogTop.Controls.Add(_lblSysLogLevel);
        _flpSysLogTop.Controls.Add(_clbSysLogLevel);
        _flpSysLogTop.Controls.Add(_lblSysLogFilter);
        _flpSysLogTop.Controls.Add(_txtSysLogFilter);
        _flpSysLogTop.Controls.Add(_lblSysLogStatus);

        _lblSysLogLevel.Anchor = AnchorStyles.Left;
        _lblSysLogLevel.AutoSize = true;
        _lblSysLogLevel.Margin = new Padding(0, 5, 4, 0);
        _lblSysLogLevel.Name = "_lblSysLogLevel";
        _lblSysLogLevel.Text = "Levels:";

        // A multi-select drop-down: the closed control is one line, and clicking it lists every
        // level with a checkbox so they can all be seen and combined at once.
        _clbSysLogLevel.Anchor = AnchorStyles.Left;
        _clbSysLogLevel.Margin = new Padding(0, 0, 12, 0);
        _clbSysLogLevel.Name = "_clbSysLogLevel";
        _clbSysLogLevel.Size = new Size(150, 25);
        _clbSysLogLevel.Text = "All";
        _clbSysLogLevel.TextAlign = ContentAlignment.MiddleLeft;

        _lblSysLogFilter.Anchor = AnchorStyles.Left;
        _lblSysLogFilter.AutoSize = true;
        _lblSysLogFilter.Margin = new Padding(0, 5, 4, 0);
        _lblSysLogFilter.Name = "_lblSysLogFilter";
        _lblSysLogFilter.Text = "Filter:";

        _txtSysLogFilter.Anchor = AnchorStyles.Left;
        _txtSysLogFilter.Margin = new Padding(0, 0, 8, 0);
        _txtSysLogFilter.Name = "_txtSysLogFilter";
        _txtSysLogFilter.PlaceholderText = "Search message, exception, or source";
        _txtSysLogFilter.Size = new Size(280, 23);
        _txtSysLogFilter.TextChanged += SysLogFilter_TextChanged;

        _lblSysLogStatus.Anchor = AnchorStyles.Left;
        _lblSysLogStatus.AutoSize = true;
        _lblSysLogStatus.Margin = new Padding(0, 5, 0, 0);
        _lblSysLogStatus.Name = "_lblSysLogStatus";
        _lblSysLogStatus.Text = "";

        // _lstSysLogs
        _lstSysLogs.Dock = DockStyle.Fill;
        _lstSysLogs.FullRowSelect = true;
        _lstSysLogs.GridLines = true;
        _lstSysLogs.HideSelection = false;
        _lstSysLogs.MultiSelect = false;
        _lstSysLogs.Name = "_lstSysLogs";
        _lstSysLogs.View = View.Details;
        _lstSysLogs.DoubleClick += LstSysLogs_DoubleClick;
        _lstSysLogs.Columns.Add(_colSysLogTime);
        _lstSysLogs.Columns.Add(_colSysLogLevel);
        _lstSysLogs.Columns.Add(_colSysLogMsg);
        _lstSysLogs.Columns.Add(_colSysLogSrc);

        _colSysLogTime.Text = "Time";
        _colSysLogTime.Width = 140;

        _colSysLogLevel.Text = "Level";
        _colSysLogLevel.Width = 70;

        _colSysLogMsg.Text = "Message";
        _colSysLogMsg.Width = 500;

        _colSysLogSrc.Text = "Source";
        _colSysLogSrc.Width = 180;

        // _tabHelp — pages built in code (MainForm.BuildHelpContent)
        _tabHelp.Controls.Add(_helpTabs);
        _tabHelp.Dock = DockStyle.Fill;
        _tabHelp.Name = "_tabHelp";
        _tabHelp.Padding = new Padding(8);
        _tabHelp.Text = "Help";

        // _helpTabs
        _helpTabs.Dock = DockStyle.Fill;
        _helpTabs.Name = "_helpTabs";

        _tlpHeartbeats.ColumnCount = 2;
        _tlpHeartbeats.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpHeartbeats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpHeartbeats.Dock = DockStyle.Fill;
        _tlpHeartbeats.Name = "_tlpHeartbeats";
        _tlpHeartbeats.RowCount = 4;
        _tlpHeartbeats.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpHeartbeats.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        _tlpHeartbeats.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        _tlpHeartbeats.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpHeartbeats.Controls.Add(_lblHeartbeatInterval, 0, 0);
        _tlpHeartbeats.Controls.Add(_txtHeartbeatInterval, 1, 0);
        _tlpHeartbeats.SetColumnSpan(_grpPings, 2);
        _tlpHeartbeats.Controls.Add(_grpPings, 0, 1);
        _tlpHeartbeats.SetColumnSpan(_grpKeepAlives, 2);
        _tlpHeartbeats.Controls.Add(_grpKeepAlives, 0, 2);
        _tlpHeartbeats.SetColumnSpan(_flpHeartbeatButtons, 2);
        _tlpHeartbeats.Controls.Add(_flpHeartbeatButtons, 0, 3);

        _lblHeartbeatInterval.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblHeartbeatInterval.AutoSize = true;
        _lblHeartbeatInterval.Margin = new Padding(4, 8, 8, 4);
        _lblHeartbeatInterval.Name = "_lblHeartbeatInterval";
        _lblHeartbeatInterval.Text = "Heartbeat Interval (seconds):";

        _txtHeartbeatInterval.Dock = DockStyle.Fill;
        _txtHeartbeatInterval.Margin = new Padding(4, 6, 4, 8);
        _txtHeartbeatInterval.Name = "_txtHeartbeatInterval";

        _grpPings.Controls.Add(_lstPings);
        _grpPings.Dock = DockStyle.Fill;
        _grpPings.Margin = new Padding(4, 4, 4, 4);
        _grpPings.Name = "_grpPings";
        _grpPings.Padding = new Padding(8);
        _grpPings.Text = "Heartbeat Pings (is the model up?)";

        _lstPings.Columns.Add(_colPingModel);
        _lstPings.Columns.Add(_colPingEnabled);
        _lstPings.Columns.Add(_colPingStatus);
        _lstPings.Columns.Add(_colPingAttempts);
        _lstPings.Columns.Add(_colPingFailures);
        _lstPings.Columns.Add(_colPingLast);
        _lstPings.Columns.Add(_colPingError);
        _lstPings.Dock = DockStyle.Fill;
        _lstPings.FullRowSelect = true;
        _lstPings.GridLines = true;
        _lstPings.Margin = new Padding(4, 4, 4, 4);
        _lstPings.Name = "_lstPings";
        _lstPings.View = View.Details;

        _colPingModel.Text = "Model";
        _colPingModel.Width = 200;
        _colPingEnabled.Text = "Enabled";
        _colPingEnabled.Width = 70;
        _colPingStatus.Text = "Last Status";
        _colPingStatus.Width = 100;
        _colPingAttempts.Text = "Pings";
        _colPingAttempts.Width = 70;
        _colPingFailures.Text = "Failures";
        _colPingFailures.Width = 70;
        _colPingLast.Text = "Last Ping";
        _colPingLast.Width = 150;
        _colPingError.Text = "Last Error";
        _colPingError.Width = 240;

        _grpKeepAlives.Controls.Add(_lstKeepAlives);
        _grpKeepAlives.Dock = DockStyle.Fill;
        _grpKeepAlives.Margin = new Padding(4, 4, 4, 4);
        _grpKeepAlives.Name = "_grpKeepAlives";
        _grpKeepAlives.Padding = new Padding(8);
        _grpKeepAlives.Text = "SSE Keep-Alives (streaming sessions held open)";

        _lstKeepAlives.Columns.Add(_colKaModel);
        _lstKeepAlives.Columns.Add(_colKaEnabled);
        _lstKeepAlives.Columns.Add(_colKaSent);
        _lstKeepAlives.Columns.Add(_colKaLastSent);
        _lstKeepAlives.Dock = DockStyle.Fill;
        _lstKeepAlives.FullRowSelect = true;
        _lstKeepAlives.GridLines = true;
        _lstKeepAlives.Margin = new Padding(4, 4, 4, 4);
        _lstKeepAlives.Name = "_lstKeepAlives";
        _lstKeepAlives.View = View.Details;

        _colKaModel.Text = "Model";
        _colKaModel.Width = 200;
        _colKaEnabled.Text = "Enabled";
        _colKaEnabled.Width = 70;
        _colKaSent.Text = "Keep-Alives Sent";
        _colKaSent.Width = 130;
        _colKaLastSent.Text = "Last Sent";
        _colKaLastSent.Width = 150;

        _flpHeartbeatButtons.AutoSize = true;
        _flpHeartbeatButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpHeartbeatButtons.Controls.Add(_btnSaveHeartbeats);
        _flpHeartbeatButtons.Controls.Add(_btnResetCounters);
        _flpHeartbeatButtons.Dock = DockStyle.Fill;
        _flpHeartbeatButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpHeartbeatButtons.Margin = new Padding(0, 4, 0, 4);
        _flpHeartbeatButtons.Name = "_flpHeartbeatButtons";
        _flpHeartbeatButtons.WrapContents = false;

        _btnSaveHeartbeats.AutoSize = true;
        _btnSaveHeartbeats.Margin = new Padding(4, 4, 4, 4);
        _btnSaveHeartbeats.Name = "_btnSaveHeartbeats";
        _btnSaveHeartbeats.Text = "Save";
        _btnSaveHeartbeats.Click += BtnSaveHeartbeats_Click;

        _btnResetCounters.AutoSize = true;
        _btnResetCounters.Margin = new Padding(4, 4, 4, 4);
        _btnResetCounters.Name = "_btnResetCounters";
        _btnResetCounters.Text = "Reset Counters";
        _btnResetCounters.Click += BtnResetCounters_Click;

        // ── Streaming Keep-Alive group (Settings tab) ─────────────────────────

        _grpSseKeepAlive.AutoSize = true;
        _grpSseKeepAlive.AutoSizeMode = AutoSizeMode.GrowOnly;
        _grpSseKeepAlive.Controls.Add(_tlpSseKeepAlive);
        _grpSseKeepAlive.Dock = DockStyle.Fill;
        _grpSseKeepAlive.Margin = new Padding(4, 8, 4, 8);
        _grpSseKeepAlive.Name = "_grpSseKeepAlive";
        _grpSseKeepAlive.Padding = new Padding(8);
        _grpSseKeepAlive.Text = "Streaming Keep-Alive";

        _tlpSseKeepAlive.AutoSize = true;
        _tlpSseKeepAlive.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpSseKeepAlive.ColumnCount = 2;
        _tlpSseKeepAlive.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpSseKeepAlive.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpSseKeepAlive.Dock = DockStyle.Fill;
        _tlpSseKeepAlive.Name = "_tlpSseKeepAlive";
        _tlpSseKeepAlive.RowCount = 2;
        _tlpSseKeepAlive.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpSseKeepAlive.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpSseKeepAlive.SetColumnSpan(_chkSseKeepAlive, 2);
        _tlpSseKeepAlive.Controls.Add(_chkSseKeepAlive, 0, 0);
        _tlpSseKeepAlive.Controls.Add(_lblKeepAliveInterval, 0, 1);
        _tlpSseKeepAlive.Controls.Add(_txtKeepAliveInterval, 1, 1);

        _chkSseKeepAlive.AutoSize = true;
        _chkSseKeepAlive.Margin = new Padding(4, 4, 4, 4);
        _chkSseKeepAlive.Name = "_chkSseKeepAlive";
        _chkSseKeepAlive.Text = "Keep streaming chat sessions alive while the model works (prevents client timeouts)";

        _lblKeepAliveInterval.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblKeepAliveInterval.AutoSize = true;
        _lblKeepAliveInterval.Margin = new Padding(4, 8, 8, 4);
        _lblKeepAliveInterval.Name = "_lblKeepAliveInterval";
        _lblKeepAliveInterval.Text = "Keep-Alive Interval (seconds):";

        _txtKeepAliveInterval.Dock = DockStyle.Fill;
        _txtKeepAliveInterval.Margin = new Padding(4, 6, 4, 8);
        _txtKeepAliveInterval.Name = "_txtKeepAliveInterval";

        // MainForm
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(740, 560);
        Controls.Add(_tabControl);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimumSize = new Size(756, 599);
        MaximumSize = new Size(756, 599);
        Name = "MainForm";
        ShowInTaskbar = false;
        Text = "Kaeo LLM Proxy";

        _grpLogging.ResumeLayout(false);
        _grpLogging.PerformLayout();
        _tlpLogging.ResumeLayout(false);
        _tlpLogging.PerformLayout();
        _grpPerf.ResumeLayout(false);
        _grpPerf.PerformLayout();
        _tlpPerf.ResumeLayout(false);
        _tlpPerf.PerformLayout();
        _tlpMcpStats.ResumeLayout(false);
        _tlpMcpStats.PerformLayout();
        _tlpDashboard.ResumeLayout(false);
        _tlpDashboard.PerformLayout();
        _dashScrollPanel.ResumeLayout(false);
        _tabControl.ResumeLayout(false);
        _tabDashboard.ResumeLayout(false);
        _logProxyPage.ResumeLayout(false);
        _logMcpPage.ResumeLayout(false);
        _logSubTabs.ResumeLayout(false);
        _tabLogs.ResumeLayout(false);
        _tlpLogs.ResumeLayout(false);
        _tlpLogs.PerformLayout();
        _flpLogsButtons.ResumeLayout(false);
        _flpLogsButtons.PerformLayout();
        _tabSettings.ResumeLayout(false);
        _tabInstructions.ResumeLayout(false);
        _tlpInstructions.ResumeLayout(false);
        _tlpInstructions.PerformLayout();
        _flpInstructionButtons.ResumeLayout(false);
        _tabCredentials.ResumeLayout(false);
        _tlpCredentials.ResumeLayout(false);
        _tlpCredentials.PerformLayout();
        _flpCredentialButtons.ResumeLayout(false);
        _tabModules.ResumeLayout(false);
        _tlpModules.ResumeLayout(false);
        _tlpModules.PerformLayout();
        _flpModuleButtons.ResumeLayout(false);
        _mcpServerPage.ResumeLayout(false);
        _mcpSubTabs.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)_nudMcpPort).EndInit();
        _tabMcp.ResumeLayout(false);
        _tlpMcp.ResumeLayout(false);
        _tlpMcp.PerformLayout();
        _flpMcpButtons.ResumeLayout(false);
        _flpMcpButtons.PerformLayout();
        _flpStatusButtons.ResumeLayout(false);
        _flpStatusButtons.PerformLayout();
        _tlpStatus.ResumeLayout(false);
        _tlpStatus.PerformLayout();
        _grpStatus.ResumeLayout(false);
        _grpStatus.PerformLayout();
        _flpDashMcpButtons.ResumeLayout(false);
        _flpDashMcpButtons.PerformLayout();
        _tlpDashMcp.ResumeLayout(false);
        _tlpDashMcp.PerformLayout();
        _grpDashMcp.ResumeLayout(false);
        _grpDashMcp.PerformLayout();
        _tabTest.ResumeLayout(false);
        _tlpTestOuter.ResumeLayout(false);
        _tlpTestOuter.PerformLayout();
        _tlpTestTop.ResumeLayout(false);
        _tlpTestTop.PerformLayout();
        _tabHeartbeats.ResumeLayout(false);
        _tabSysLogs.ResumeLayout(false);
        _tabSysLogs.PerformLayout();
        _tlpSysLogs.ResumeLayout(false);
        _tlpSysLogs.PerformLayout();
        _flpSysLogTop.ResumeLayout(false);
        _flpSysLogTop.PerformLayout();
        // Every container suspended above must be resumed, or its children never receive the
        // layout pass that positions them: a Dock = Fill list inside an un-resumed panel keeps its
        // default bounds and the pane looks empty even though the filter row beside it renders.
        _tlpProxyLogs.ResumeLayout(false);
        _tlpProxyLogs.PerformLayout();
        _flpProxyLogTop.ResumeLayout(false);
        _flpProxyLogTop.PerformLayout();
        _tlpMcpLogs.ResumeLayout(false);
        _tlpMcpLogs.PerformLayout();
        _flpMcpLogTop.ResumeLayout(false);
        _flpMcpLogTop.PerformLayout();
        _tlpNonProxiedLogs.ResumeLayout(false);
        _tlpNonProxiedLogs.PerformLayout();
        _flpNonProxiedLogTop.ResumeLayout(false);
        _flpNonProxiedLogTop.PerformLayout();
        _logNonProxiedPage.ResumeLayout(false);
        _grpNonProxiedCapture.ResumeLayout(false);
        _grpNonProxiedCapture.PerformLayout();
        _tlpNonProxiedCapture.ResumeLayout(false);
        _tlpNonProxiedCapture.PerformLayout();
        _helpTabs.ResumeLayout(false);
        _tabHelp.ResumeLayout(false);
        _grpListener.ResumeLayout(false);
        _grpListener.PerformLayout();
        _tlpListener.ResumeLayout(false);
        _tlpListener.PerformLayout();
        _tlpHeartbeats.ResumeLayout(false);
        _tlpHeartbeats.PerformLayout();
        _grpPings.ResumeLayout(false);
        _grpKeepAlives.ResumeLayout(false);
        _flpHeartbeatButtons.ResumeLayout(false);
        _flpHeartbeatButtons.PerformLayout();
        _grpSseKeepAlive.ResumeLayout(false);
        _grpSseKeepAlive.PerformLayout();
        _tlpSseKeepAlive.ResumeLayout(false);
        _tlpSseKeepAlive.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)_nudTestTemp).EndInit();
        ((System.ComponentModel.ISupportInitialize)_nudTestRepeatPenalty).EndInit();
        ((System.ComponentModel.ISupportInitialize)_dgvMappings).EndInit();
        ResumeLayout(false);
    }

    private TabControl _tabControl;
    private TabPage _tabDashboard;
    private TabPage _tabLogs;
    private TabPage _tabSettings;
    private Panel _dashScrollPanel;
    private TableLayoutPanel _tlpDashboard;
    private GroupBox _grpStatus;
    private TableLayoutPanel _tlpStatus;
    private FlowLayoutPanel _flpStatusButtons;
    private Label _lblStatus;
    private Label _lblStatusValue;
    private Label _lblStatusAddressCaption;
    private Label _lblStatusAddressValue;
    private Label _lblStatusPortCaption;
    private Label _lblStatusPortValue;
    private Button _btnStart;
    private Button _btnStop;
    private Button _btnRestart;
    private GroupBox _grpDashMcp;
    private TableLayoutPanel _tlpDashMcp;
    private Label _lblDashMcpStatusCaption;
    private Label _lblDashMcpStatusValue;
    private Label _lblDashMcpAddressCaption;
    private Label _lblDashMcpAddressValue;
    private Label _lblDashMcpPortCaption;
    private Label _lblDashMcpPortValue;
    private FlowLayoutPanel _flpDashMcpButtons;
    private Button _btnDashMcpStart;
    private Button _btnDashMcpStop;
    private Button _btnDashMcpRestart;
    private TableLayoutPanel _tlpStats;
    private Label _lblTotalRequestsCaption;
    private Label _lblTotalRequestsValue;
    private Label _lblTotalErrorsCaption;
    private Label _lblTotalErrorsValue;
    private Label _lblPromptTokensCaption;
    private Label _lblPromptTokensValue;
    private Label _lblCompletionTokensCaption;
    private Label _lblCompletionTokensValue;
    private Label _lblRpsCaption;
    private Label _lblRpsValue;
    private Button _btnResetStats;
    private TableLayoutPanel _tlpMcpStats;
    private Label _lblMcpTotalRequestsCaption;
    private Label _lblMcpTotalRequestsValue;
    private Label _lblMcpTotalErrorsCaption;
    private Label _lblMcpTotalErrorsValue;
    private Label _lblMcpRpsCaption;
    private Label _lblMcpRpsValue;
    private Button _btnResetMcpStats;
    private GroupBox _grpPerf;
    private TableLayoutPanel _tlpPerf;
    private Label _lblCpuCaption;
    private Label _lblCpuValue;
    private Label _lblRamCaption;
    private Label _lblRamValue;
    private TableLayoutPanel _tlpLogs;
    private FlowLayoutPanel _flpLogsButtons;
    private ListView _lstLogs;
    private ColumnHeader _colTime;
    private ColumnHeader _colMethod;
    private ColumnHeader _colPath;
    private ColumnHeader _colModel;
    private ColumnHeader _colStatus;
    private ColumnHeader _colDuration;
    private ColumnHeader _colPromptTokens;
    private ColumnHeader _colCompletionTokens;
    private ColumnHeader _colReasoningTokens;
    private ColumnHeader _colCachedTokens;
    private ColumnHeader _colDraftAccept;
    private ColumnHeader _colBytes;
    private TabControl _logSubTabs;
    private TabPage _logProxyPage;
    private TableLayoutPanel _tlpProxyLogs;
    private FlowLayoutPanel _flpProxyLogTop;
    private Label _lblProxyLogFilter;
    private TextBox _txtProxyLogFilter;
    private TabPage _logMcpPage;
    private TableLayoutPanel _tlpMcpLogs;
    private FlowLayoutPanel _flpMcpLogTop;
    private Label _lblMcpLogFilter;
    private TextBox _txtMcpLogFilter;
    private TabPage _logNonProxiedPage;
    private TableLayoutPanel _tlpNonProxiedLogs;
    private FlowLayoutPanel _flpNonProxiedLogTop;
    private Label _lblNonProxiedLogFilter;
    private TextBox _txtNonProxiedLogFilter;
    private ListView _lstNonProxiedLogs;
    private ColumnHeader _colNpTime;
    private ColumnHeader _colNpMethod;
    private ColumnHeader _colNpPath;
    private ColumnHeader _colNpStatus;
    private ColumnHeader _colNpDuration;
    private ColumnHeader _colNpBytes;
    private ListView _lstMcpLogs;
    private ColumnHeader _colMcpTime;
    private ColumnHeader _colMcpMethod;
    private ColumnHeader _colMcpPath;
    private ColumnHeader _colMcpStatus;
    private ColumnHeader _colMcpDuration;
    private ColumnHeader _colMcpBytes;
    private CheckBox _chkAutoRefresh;
    private Label _lblRefreshInterval;
    private ComboBox _cmbRefreshInterval;
    private Button _btnClearLogs;
    private TableLayoutPanel _tlpSettings;
    private Label _lblListenPort;
    private TextBox _txtListenPort;
    private Label _lblListenAddress;
    private ComboBox _cmbListenAddress;
    private Label _lblMaxLogs;
    private TextBox _txtMaxLogs;
    private Label _lblCompactionFallback;
    private TextBox _txtCompactionFallback;
    private Label _lblMappings;
    private DataGridView _dgvMappings;
    private DataGridViewTextBoxColumn _colMappingEnabled;
    private DataGridViewTextBoxColumn _colProxyName;
    private DataGridViewTextBoxColumn _colModelName;
    private DataGridViewTextBoxColumn _colInstructionSet;
    private DataGridViewTextBoxColumn _colReasoningEffort;
    private DataGridViewTextBoxColumn _colVision;
    private DataGridViewTextBoxColumn _colCompactionTarget;
    private DataGridViewTextBoxColumn _colCompactionRedirect;
    private FlowLayoutPanel _flpMappingButtons;
    private Button _btnAddMapping;
    private Button _btnRemoveMapping;
    private Button _btnDuplicateMapping;
    private Button _btnConfigureMapping;
    private Button _btnToggleEnabled;
    private GroupBox _grpListener;
    private TableLayoutPanel _tlpListener;
    private Button _btnSaveListener;
    private CheckBox _chkAutoStart;
    private CheckBox _chkStartWithDashboard;
    private CheckBox _chkRunAsAdmin;
    private CheckBox _chkCollectDetails;
    private CheckBox _chkCollectResponseDetails;
    private CheckBox _chkDebugMode;
    private CheckBox _chkIrTranslation;
    private GroupBox _grpNonProxiedCapture;
    private TableLayoutPanel _tlpNonProxiedCapture;
    private Label _lblNonProxiedCaptureIntro;
    private CheckBox _chkCollectHealthProbes;
    private CheckBox _chkCollectVersionExplorer;
    private CheckBox _chkCollectCorsPreflight;
    private CheckBox _chkCollectLocalStubs;
    private CheckBox _chkCollectRejectedRequests;
    private Label _lblCollectNonProxiedWarning;
    private CheckBox _chkPerformanceSampling;
    private CheckBox _chkApiExplorer;
    private Label _lblApiExplorerUrl;
    private Label _lblApiSpecUrl;
    private CheckBox _chkSseKeepAlive;
    private Label _lblKeepAliveInterval;
    private TextBox _txtKeepAliveInterval;
    private System.Windows.Forms.Timer _refreshTimer;
    private Button _btnRefreshLogs;
    private Button _btnLogDetails;
    private GroupBox _grpLogging;
    private TableLayoutPanel _tlpLogging;
    private Label _lblLogDir;
    private TextBox _txtLogDir;
    private Label _lblMinLevel;
    private ComboBox _cmbMinLevel;
    private Label _lblAppLogSize;
    private TextBox _txtAppLogSize;
    private Label _lblAppLogRetain;
    private TextBox _txtAppLogRetain;
    private Label _lblReqLogSize;
    private TextBox _txtReqLogSize;
    private Label _lblRequestDbPath;
    private TableLayoutPanel _tlpRequestDbPath;
    private TextBox _txtRequestDbPath;
    private Button _btnBrowseRequestDb;
    private Label _lblLogRetention;
    private TextBox _txtLogRetention;

    // Instructions tab
    private TabPage _tabInstructions;
    private TableLayoutPanel _tlpInstructions;
    private ListView _lstInstructions;
    private ColumnHeader _colInstrName;
    private ColumnHeader _colInstrDescription;
    private FlowLayoutPanel _flpInstructionButtons;
    private Button _btnAddInstruction;
    private Button _btnEditInstruction;
    private Button _btnRemoveInstruction;
    private Label _lblInstructionPreview;
    private TextBox _txtInstructionPreview;

    // Credentials tab
    private TabPage _tabCredentials;
    private TableLayoutPanel _tlpCredentials;
    private ListView _lstCredentials;
    private ColumnHeader _colCredName;
    private ColumnHeader _colCredDescription;
    private FlowLayoutPanel _flpCredentialButtons;
    private Button _btnAddCredential;
    private Button _btnEditCredential;
    private Button _btnRemoveCredential;

    // Modules tab
    private TabPage _tabModules;
    private TableLayoutPanel _tlpModules;
    private Label _lblModulesNote;
    private ListView _lstModules;
    private ColumnHeader _colModuleName;
    private ColumnHeader _colModuleVersion;
    private ColumnHeader _colModuleState;
    private ColumnHeader _colModulePath;
    private Label _lblModuleStatus;
    private FlowLayoutPanel _flpModuleButtons;
    private Button _btnImportModule;
    private Button _btnToggleModule;
    private Button _btnRemoveModule;

    // MCP tab
    private TabPage _tabMcp;
    private TabControl _mcpSubTabs;
    private TabPage _mcpServerPage;
    private TableLayoutPanel _tlpMcp;
    private CheckBox _chkMcpEnabled;
    private CheckBox _chkMcpApiExplorer;
    private CheckBox _chkMcpCollectRequest;
    private CheckBox _chkMcpCollectResponse;
    private Label _lblMcpApiExplorerUrl;
    private Label _lblMcpSpecUrl;
    private Label _lblMcpListenAddress;
    private ComboBox _cboMcpListenAddress;
    private Label _lblMcpPort;
    private NumericUpDown _nudMcpPort;

    private Label _lblMcpStatusCaption;
    private Label _lblMcpStatus;
    private FlowLayoutPanel _flpMcpButtons;
    private Button _btnMcpApply;

    // Test Console
    private TabPage _tabTest;
    private TableLayoutPanel _tlpTestOuter;
    private TableLayoutPanel _tlpTestTop;
    private Label _lblTestModel;
    private ComboBox _cmbTestModel;
    private Label _lblTestTemp;
    private NumericUpDown _nudTestTemp;
    private Label _lblTestRepeatPenalty;
    private NumericUpDown _nudTestRepeatPenalty;
    private Button _btnTestSend;
    private Button _btnTestCancel;
    private Button _btnTestClear;
    private TextBox _txtTestPrompt;
    private TextBox _txtTestResponse;
    private Label _lblTestStatus;

    // System Logs tab
    private TabPage _tabSysLogs;
    private TableLayoutPanel _tlpSysLogs;
    private FlowLayoutPanel _flpSysLogTop;
    private MultiSelectDropDown _clbSysLogLevel;
    private Label _lblSysLogLevel;
    private Label _lblSysLogFilter;
    private TextBox _txtSysLogFilter;
    private ListView _lstSysLogs;
    private ColumnHeader _colSysLogTime;
    private ColumnHeader _colSysLogLevel;
    private ColumnHeader _colSysLogMsg;
    private ColumnHeader _colSysLogSrc;
    private Label _lblSysLogStatus;

    // Heartbeats tab
    private TabPage _tabHeartbeats;
    private TabPage _tabHelp;
    private TabControl _helpTabs;
    private TableLayoutPanel _tlpHeartbeats;
    private Label _lblHeartbeatInterval;
    private TextBox _txtHeartbeatInterval;
    private GroupBox _grpPings;
    private ListView _lstPings;
    private ColumnHeader _colPingModel;
    private ColumnHeader _colPingEnabled;
    private ColumnHeader _colPingStatus;
    private ColumnHeader _colPingAttempts;
    private ColumnHeader _colPingFailures;
    private ColumnHeader _colPingLast;
    private ColumnHeader _colPingError;
    private GroupBox _grpKeepAlives;
    private ListView _lstKeepAlives;
    private ColumnHeader _colKaModel;
    private ColumnHeader _colKaEnabled;
    private ColumnHeader _colKaSent;
    private ColumnHeader _colKaLastSent;
    private FlowLayoutPanel _flpHeartbeatButtons;
    private Button _btnResetCounters;
    private Button _btnSaveHeartbeats;

    // Streaming Keep-Alive group (Settings tab)
    private GroupBox _grpSseKeepAlive;
    private TableLayoutPanel _tlpSseKeepAlive;
}
