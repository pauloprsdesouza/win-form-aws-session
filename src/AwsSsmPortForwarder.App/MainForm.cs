using System.ComponentModel;
using AwsSsmPortForwarder.App.Dialogs;
using AwsSsmPortForwarder.Core.Models;
using AwsSsmPortForwarder.Core.Services;
using Microsoft.Extensions.Logging;

namespace AwsSsmPortForwarder.App;

public sealed class MainForm : Form
{
    private readonly IAwsCliClient _aws;
    private readonly ConnectionOrchestrator _orchestrator;
    private readonly IUserSettingsStore _settingsStore;
    private readonly ILogger<MainForm> _logger;

    private readonly TextBox _folderBox = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = "Browse", MinimumSize = new Size(100, 36), Height = 36, AutoSize = true };
    private readonly Label _folderStatus = new() { AutoSize = true, Text = "—", MaximumSize = new Size(280, 0) };
    private readonly Label _prereqStatus = new() { AutoSize = true, Text = "—", MaximumSize = new Size(220, 0) };
    private readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _reload = new() { Text = "Reload", MinimumSize = new Size(100, 36), Height = 36, AutoSize = true };
    private readonly Label _authLabel = new() { AutoSize = true, Text = "Authentication: —", MaximumSize = new Size(420, 0) };
    private readonly Button _signIn = new() { Text = "Sign in", MinimumSize = new Size(110, 36), Height = 36, AutoSize = true, Enabled = false, Visible = false };
    private readonly Button _deviceCode = new() { Text = "Device code", MinimumSize = new Size(120, 36), Height = 36, AutoSize = true, Enabled = false, Visible = false };
    private readonly Label _regionLabel = new() { AutoSize = true, Text = "Region: —", MaximumSize = new Size(280, 0) };
    private readonly Label _targetLabel = new() { AutoSize = true, Text = "Bastion: —", MaximumSize = new Size(520, 0) };
    private readonly CheckBox _remember = new() { Text = "Remember connections", AutoSize = true };
    private readonly Button _addConnection = new() { Text = "+ Add connection", MinimumSize = new Size(140, 36), Height = 36, AutoSize = true };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoGenerateColumns = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        EnableHeadersVisualStyles = false,
        EditMode = DataGridViewEditMode.EditOnEnter
    };
    private readonly Label _summary = new() { AutoSize = true, Text = "0 ready • 0 incomplete • 0 active", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _connect = new() { Text = "Start enabled", MinimumSize = new Size(140, 40), Height = 40, AutoSize = true, Enabled = false };
    private readonly Button _stopAll = new() { Text = "Stop all", MinimumSize = new Size(110, 40), Height = 40, AutoSize = true };
    private readonly Button _retryFailed = new() { Text = "Retry failed", MinimumSize = new Size(120, 40), Height = 40, AutoSize = true };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, Text = "Initializing", TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill,
        Style = ProgressBarStyle.Continuous,
        Minimum = 0,
        Maximum = 100,
        Value = 0
    };

    private readonly BindingList<ConnectionRow> _rows = [];
    private UserSettings _settings = new();
    private PrerequisiteStatus _prereqs = new();
    private AwsFolderContext? _folder;
    private AppReadyState _ready = AppReadyState.Initializing;
    private AwsIdentity? _identity;
    private string? _region;
    private AwsTarget? _target;
    private CancellationTokenSource _cts = new();
    private bool _exitConfirmed;
    private int _busyCount;
    private bool _suppressGridEvents;
    private bool _suppressProfileEvents;
    private bool _suppressSettingsEvents;
    private bool _startupComplete;
    private int _startColumnIndex = -1;
    private int _destinationColumnIndex = -1;

    public MainForm(
        IAwsCliClient aws,
        ConnectionOrchestrator orchestrator,
        IUserSettingsStore settingsStore,
        IAppConfigStore configStore,
        ILogger<MainForm> logger)
    {
        _aws = aws;
        _orchestrator = orchestrator;
        _settingsStore = settingsStore;
        _logger = logger;
        _ = configStore;

        Text = "AWS Port Forwarding";
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(960, 720);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 760);
        AutoScaleMode = AutoScaleMode.Dpi;

        ConfigureGrid();
        BuildLayout();
        Wire();
        Shown += async (_, _) => await InitializeAsync();
    }

    private void ConfigureGrid()
    {
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(245, 245, 245),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        _grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        _grid.ColumnHeadersHeight = 36;
        _grid.RowTemplate.Height = 38;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        var enabled = new DataGridViewCheckBoxColumn { DataPropertyName = nameof(ConnectionRow.Enabled), HeaderText = "", Width = 36 };
        var name = new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ConnectionRow.Name),
            HeaderText = "Name",
            Width = 140,
            MinimumWidth = 100
        };
        var type = new DataGridViewComboBoxColumn
        {
            DataPropertyName = nameof(ConnectionRow.TypeDisplay),
            HeaderText = "Type",
            Width = 130,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        };
        type.Items.AddRange("Managed node", "Remote host");

        var destination = new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ConnectionRow.Destination),
            HeaderText = "Destination",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 160
        };
        var remote = new DataGridViewTextBoxColumn { DataPropertyName = nameof(ConnectionRow.RemotePortText), HeaderText = "Remote", Width = 70 };
        var local = new DataGridViewTextBoxColumn { DataPropertyName = nameof(ConnectionRow.LocalPortText), HeaderText = "Local", Width = 70 };
        var status = new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ConnectionRow.StatusText),
            HeaderText = "Status",
            Width = 120,
            ReadOnly = true
        };
        var start = new DataGridViewButtonColumn
        {
            HeaderText = "",
            Text = "Start",
            Width = 80,
            UseColumnTextForButtonValue = false,
            FlatStyle = FlatStyle.Standard
        };
        var stop = new DataGridViewButtonColumn { HeaderText = "", Text = "Stop", Width = 80, UseColumnTextForButtonValue = true };
        var delete = new DataGridViewButtonColumn { HeaderText = "", Text = "Delete", Width = 88, UseColumnTextForButtonValue = true };

        _grid.Columns.AddRange(enabled, name, type, destination, remote, local, status, start, stop, delete);
        _destinationColumnIndex = destination.Index;
        _startColumnIndex = start.Index;
        _grid.DataSource = _rows;
        _grid.DataBindingComplete += (_, _) => RefreshStartButtons();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16, 12, 16, 12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        root.Controls.Add(BuildContextCard(), 0, 0);
        root.Controls.Add(BuildConnectionsCard(), 0, 1);
        root.Controls.Add(BuildActionsRow(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);
    }

    private GroupBox BuildContextCard()
    {
        var group = new GroupBox { Text = "AWS context", Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        grid.Controls.Add(Lbl("Folder"), 0, 0);
        grid.Controls.Add(_folderBox, 1, 0);
        grid.Controls.Add(_browse, 2, 0);

        var folderMeta = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        _folderStatus.Margin = new Padding(0, 2, 12, 0);
        _prereqStatus.Margin = new Padding(0, 2, 0, 0);
        folderMeta.Controls.AddRange([_folderStatus, _prereqStatus]);
        grid.Controls.Add(folderMeta, 1, 1);
        grid.SetColumnSpan(folderMeta, 2);

        grid.Controls.Add(Lbl("Profile"), 0, 2);
        grid.Controls.Add(_profiles, 1, 2);
        grid.Controls.Add(_reload, 2, 2);

        var authMeta = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        _authLabel.Margin = new Padding(0, 8, 12, 0);
        _regionLabel.Margin = new Padding(0, 8, 16, 0);
        _targetLabel.Margin = new Padding(0, 8, 12, 0);
        _remember.Margin = new Padding(0, 6, 0, 0);
        authMeta.Controls.AddRange([_authLabel, _regionLabel, _targetLabel, _remember]);
        grid.Controls.Add(authMeta, 1, 3);
        grid.SetColumnSpan(authMeta, 2);

        var authButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 4, 0, 0)
        };
        _signIn.Margin = new Padding(0, 0, 8, 0);
        _deviceCode.Margin = new Padding(0, 0, 0, 0);
        authButtons.Controls.AddRange([_signIn, _deviceCode]);
        grid.Controls.Add(authButtons, 1, 4);
        grid.SetColumnSpan(authButtons, 2);

        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildConnectionsCard()
    {
        var group = new GroupBox { Text = "Connections", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        header.Controls.Add(_addConnection);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildActionsRow()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0)
        };
        foreach (var b in new[] { _connect, _stopAll, _retryFailed })
            b.Margin = new Padding(0, 0, 8, 0);
        buttons.Controls.AddRange([_connect, _stopAll, _retryFailed]);
        panel.Controls.Add(buttons, 0, 0);

        _summary.Anchor = AnchorStyles.Right;
        _summary.Margin = new Padding(8, 12, 0, 0);
        panel.Controls.Add(_summary, 1, 0);
        return panel;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(4, 4, 4, 0) };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        footer.Controls.Add(_progress, 0, 0);
        footer.Controls.Add(_status, 0, 1);
        return footer;
    }

    private static Label Lbl(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private void Wire()
    {
        _browse.Click += async (_, _) => await BrowseFolderAsync();
        _reload.Click += async (_, _) => await ApplyFolderTextAsync(validateSelectedProfile: true);
        _profiles.SelectedIndexChanged += async (_, _) =>
        {
            if (_suppressProfileEvents) return;
            await OnProfileChangedAsync();
        };
        _signIn.Click += async (_, _) => await SignInAsync(SsoLoginMode.Browser);
        _deviceCode.Click += async (_, _) => await SignInAsync(SsoLoginMode.DeviceCode);
        _addConnection.Click += (_, _) => AddConnectionRow();
        _connect.Click += async (_, _) => await ConnectEnabledAsync();
        _stopAll.Click += async (_, _) => await RunBusy(() => _orchestrator.StopAllAsync(_cts.Token), "Stopping tunnels…");
        _retryFailed.Click += async (_, _) => await RetryFailedAsync();
        _remember.CheckedChanged += async (_, _) =>
        {
            if (_suppressSettingsEvents) return;
            _settings.RememberConnections = _remember.Checked;
            await PersistAsync();
        };
        _folderBox.Leave += async (_, _) =>
        {
            if (!_startupComplete) return;
            await ApplyFolderTextAsync(validateSelectedProfile: false);
        };
        _grid.CellValueChanged += GridOnCellValueChanged;
        _grid.CellEndEdit += GridOnCellEndEdit;
        _grid.EditingControlShowing += GridOnEditingControlShowing;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Commit only checkbox/combo immediately; text cells commit on EndEdit to avoid caret reset.
            if (!_grid.IsCurrentCellDirty || _grid.CurrentCell is null) return;
            if (_grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellBeginEdit += GridOnCellBeginEdit;
        _grid.CellContentClick += async (_, e) => await GridOnButtonClickAsync(e);
        _grid.DataError += (_, e) => e.ThrowException = false;
        _orchestrator.SessionsChanged += (_, _) => BeginInvoke(SyncSessionStatuses);
        _orchestrator.StatusChanged += (_, msg) => BeginInvoke(() => _status.Text = msg);
        _orchestrator.ProgressChanged += (_, update) => BeginInvoke(() => ApplyProgress(update));
        KeyPreview = true;
        KeyDown += MainForm_KeyDown;
        FormClosing += OnClosing;
        _rows.ListChanged += (_, _) => UpdateSummaryAndConnect();
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.N) { AddConnectionRow(); e.Handled = true; }
        if (e.Control && e.KeyCode == Keys.Enter) { _ = ConnectEnabledAsync(); e.Handled = true; }
    }

    private void GridOnCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        var row = _rows[e.RowIndex];
        var session = _orchestrator.GetSession(row.Id);
        if (session?.State is SessionState.Connected or SessionState.Connecting or SessionState.Reconnecting)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Stop the session before editing this connection.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void GridOnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressGridEvents || e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        var row = _rows[e.RowIndex];
        var col = _grid.Columns[e.ColumnIndex].DataPropertyName;

        if (col == nameof(ConnectionRow.TypeDisplay))
        {
            if (row.Type == PortForwardType.ManagedNode)
                row.SetDestinationWithoutNotify("Bastion (same target)");
            else if (row.Destination == "Bastion (same target)")
                row.SetDestinationWithoutNotify("");
            UpdateDestinationReadOnly(e.RowIndex);
            row.RefreshValidation();
            UpdateSummaryAndConnect();
            RefreshStartButtons();
            _ = PersistAsync();
            return;
        }

        if (col == nameof(ConnectionRow.Enabled))
        {
            row.RefreshValidation();
            UpdateSummaryAndConnect();
            RefreshStartButtons();
            _ = PersistAsync();
        }
    }

    private void GridOnCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressGridEvents || e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        var row = _rows[e.RowIndex];
        var col = _grid.Columns[e.ColumnIndex].DataPropertyName;

        if (col == nameof(ConnectionRow.RemotePortText) &&
            row.LocalPortLinked &&
            string.IsNullOrWhiteSpace(row.LocalPortText) &&
            !string.IsNullOrWhiteSpace(row.RemotePortText))
        {
            row.SetLocalPortWithoutNotify(row.RemotePortText);
        }

        row.RefreshValidation();
        PreserveActiveSessionStatus(row);
        UpdateSummaryAndConnect();
        RefreshStartButtons();
        _ = PersistAsync();
    }

    private void GridOnEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (e.Control is not TextBox textBox) return;
        textBox.AutoSize = false;
        // Place caret at end instead of selecting all when edit begins.
        BeginInvoke(() =>
        {
            if (!textBox.IsDisposed && textBox.Focused)
            {
                textBox.SelectionLength = 0;
                textBox.SelectionStart = textBox.TextLength;
            }
        });
    }

    private void UpdateDestinationReadOnly(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count || _destinationColumnIndex < 0) return;
        var cell = _grid.Rows[rowIndex].Cells[_destinationColumnIndex];
        var managed = _rows[rowIndex].Type == PortForwardType.ManagedNode;
        cell.ReadOnly = managed;
        cell.Style.BackColor = managed ? Color.FromArgb(245, 245, 245) : SystemColors.Window;
    }

    private async Task GridOnButtonClickAsync(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        var row = _rows[e.RowIndex];
        var column = _grid.Columns[e.ColumnIndex];
        var text = (column as DataGridViewButtonColumn)?.Text;
        var cellValue = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();

        if (e.ColumnIndex == _startColumnIndex || text == "Start" || cellValue == "Start")
        {
            if (!CanStartRow(row)) return;
            await StartSingleAsync(row);
        }
        else if (text == "Stop" || cellValue == "Stop")
            await RunBusy(() => _orchestrator.StopAsync(row.Id, _cts.Token), "Stopping…");
        else if (text == "Delete" || cellValue == "Delete")
            await DeleteRowAsync(row);
    }

    private void AddConnectionRow()
    {
        var row = ConnectionRow.CreateNew();
        _rows.Add(row);
        UpdateDestinationReadOnly(_rows.Count - 1);
        UpdateSummaryAndConnect();
        RefreshStartButtons();
        _ = PersistAsync();
    }

    private async Task DeleteRowAsync(ConnectionRow row)
    {
        var session = _orchestrator.GetSession(row.Id);
        if (session?.State is SessionState.Connected or SessionState.Connecting or SessionState.Reconnecting)
        {
            var confirm = MessageBox.Show(this, "This connection is active. Stop and delete it?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
            await _orchestrator.StopAsync(row.Id, _cts.Token);
        }
        _rows.Remove(row);
        UpdateSummaryAndConnect();
        RefreshStartButtons();
        await PersistAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            SetBusy(true, "Checking prerequisites…");
            _settings = await _settingsStore.LoadAsync(_cts.Token);
            // Load rows before binding the Remember checkbox — CheckedChanged would Persist empty rows and wipe settings.
            LoadSavedConnections();
            _suppressSettingsEvents = true;
            try { _remember.Checked = _settings.RememberConnections; }
            finally { _suppressSettingsEvents = false; }

            _prereqs = await _aws.DetectPrerequisitesAsync(_cts.Token);
            _prereqStatus.Text = $"AWS CLI {(_prereqs.AwsCliReady ? "✓" : "✗")}   SSM {(_prereqs.SessionManagerReady ? "✓" : "✗")}";
            _prereqStatus.ForeColor = _prereqs.AwsCliReady && _prereqs.SessionManagerReady ? Color.DarkGreen : Color.Firebrick;

            if (!_prereqs.AwsCliReady)
            {
                SetReady(AppReadyState.Error, "AWS CLI v2 was not found.");
                return;
            }

            var folder = _settings.LastAwsFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                folder = AwsFolderFactory.SuggestDefaultFolder();

            if (string.IsNullOrWhiteSpace(folder))
            {
                SetReady(AppReadyState.FolderRequired, "Select an AWS folder.");
                await BrowseFolderAsync();
                return;
            }

            _folderBox.Text = folder;
            // Load folder/profiles only — do not validate or start tunnels on startup.
            await ApplyFolderTextAsync(validateSelectedProfile: false);
            SetReady(AppReadyState.ProfileRequired,
                "Select a profile (or click Reload) to authenticate. Connections start only when you click Start.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _startupComplete = true;
            SetBusy(false, "Ready — click Reload or select a profile to authenticate.");
            ResetProgressIdle();
        }
    }

    private void LoadSavedConnections()
    {
        _suppressGridEvents = true;
        _rows.Clear();
        if (_settings.RememberConnections && _settings.Connections.Count > 0)
        {
            foreach (var saved in _settings.Connections)
                _rows.Add(ConnectionRow.FromRule(ConnectionSettingsMapper.ToRule(saved)));
        }
        if (_rows.Count == 0)
            _rows.Add(ConnectionRow.CreateNew());
        _suppressGridEvents = false;
        for (var i = 0; i < _rows.Count; i++)
            UpdateDestinationReadOnly(i);
        UpdateSummaryAndConnect();
        RefreshStartButtons();
    }

    private async Task BrowseFolderAsync()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select the folder that contains AWS config and/or credentials files.",
            UseDescriptionForTitle = true
        };
        var suggested = AwsFolderFactory.SuggestDefaultFolder();
        if (suggested is not null) dlg.InitialDirectory = suggested;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _folderBox.Text = dlg.SelectedPath;
        await ApplyFolderTextAsync(validateSelectedProfile: true);
    }

    private async Task ApplyFolderTextAsync(bool validateSelectedProfile)
    {
        try
        {
            if (_orchestrator.Sessions.Any(s => s.State is SessionState.Connected or SessionState.Connecting or SessionState.Reconnecting))
            {
                var confirm = MessageBox.Show(this, "Stop active sessions and change folder?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;
                await _orchestrator.StopAllAsync(_cts.Token);
            }

            _cts.Cancel();
            _cts = new CancellationTokenSource();
            ClearAuthTarget();
            HideSignInActions();
            SetBusy(true, "Discovering profiles…");
            SetReady(AppReadyState.DiscoveringProfiles, "Discovering profiles…");

            _folder = AwsFolderFactory.FromFolder(_folderBox.Text);
            _folderStatus.Text = $"{(_folder.HasConfig ? "✓ config" : "○ config")}   {(_folder.HasCredentials ? "✓ credentials" : "○ credentials")}";
            _settings.LastAwsFolder = _folder.FolderPath;
            await PersistAsync();

            var profiles = await _aws.ListProfilesAsync(_folder, _cts.Token);
            _suppressProfileEvents = true;
            try
            {
                _profiles.Items.Clear();
                foreach (var p in profiles) _profiles.Items.Add(p);

                if (profiles.Count == 0)
                {
                    SetReady(AppReadyState.ProfileRequired, "No profiles were found.");
                    return;
                }

                var idx = profiles.ToList().FindIndex(p => p.Equals(_settings.LastProfile, StringComparison.OrdinalIgnoreCase));
                _profiles.SelectedIndex = idx >= 0 ? idx : 0;
            }
            finally
            {
                _suppressProfileEvents = false;
            }

            if (validateSelectedProfile)
                await OnProfileChangedAsync();
            else
                SetReady(AppReadyState.ProfileRequired,
                    "Profiles loaded. Select a profile or click Reload to authenticate — Start opens tunnels.");
        }
        catch (Exception ex)
        {
            ShowError(ex);
            SetReady(AppReadyState.FolderRequired, "Select an AWS folder.");
        }
        finally
        {
            SetBusy(false);
            ResetProgressIdle();
        }
    }

    private async Task OnProfileChangedAsync()
    {
        if (_folder is null || _profiles.SelectedItem is null) return;
        try
        {
            ClearAuthTarget();
            HideSignInActions();
            SetBusy(true, "Validating authentication…");
            SetReady(AppReadyState.ValidatingAuthentication, "Checking…");
            var profile = _profiles.SelectedItem.ToString()!;
            _settings.LastProfile = profile;
            await PersistAsync();

            var context = new AwsContext(_folder, profile, null);
            string? configContent = _folder.HasConfig
                ? await File.ReadAllTextAsync(_folder.ConfigFilePath!, _cts.Token)
                : null;

            var (identity, signInRequired, isSso, message) =
                await _orchestrator.ValidateAuthAsync(context, configContent, _cts.Token);
            _identity = identity;

            if (signInRequired)
            {
                ShowSignInRequired(isSso);
                return;
            }

            if (identity is null)
            {
                _authLabel.Text = $"Authentication: {message ?? "invalid"}";
                HideSignInActions();
                SetReady(AppReadyState.Error, message ?? "Credentials are missing, invalid, or expired. Update AWS files and click Reload.");
                return;
            }

            _authLabel.Text = isSso ? "Authentication: ✓ SSO Connected" : "Authentication: ✓ Connected";
            HideSignInActions();
            await ResolveTargetAsync(context);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            SetBusy(false);
            if (_ready != AppReadyState.SignInRequired)
                ResetProgressIdle();
        }
    }

    private void ShowSignInRequired(bool isSso)
    {
        _authLabel.Text = isSso
            ? "Authentication: Sign-in required"
            : "Authentication: Sign-in required (SSO)";
        _authLabel.ForeColor = Color.DarkOrange;
        _signIn.Visible = true;
        _signIn.Enabled = true;
        _deviceCode.Visible = true;
        _deviceCode.Enabled = true;
        SetReady(AppReadyState.SignInRequired, "Sign-in approval is required. Click Sign in to continue in the browser.");
        ResetProgressIdle();
        BeginInvoke(() => _signIn.Focus());
    }

    private void HideSignInActions()
    {
        _authLabel.ForeColor = SystemColors.ControlText;
        _signIn.Visible = false;
        _signIn.Enabled = false;
        _deviceCode.Visible = false;
        _deviceCode.Enabled = false;
    }

    private async Task SignInAsync(SsoLoginMode mode)
    {
        if (_folder is null || _profiles.SelectedItem is null) return;
        try
        {
            _signIn.Enabled = false;
            _deviceCode.Enabled = false;
            SetBusy(true, mode == SsoLoginMode.Browser
                ? "Waiting for browser sign-in… Complete approval in your browser."
                : "Waiting for device code sign-in…");
            await _orchestrator.SsoLoginAsync(new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region), mode, _cts.Token);
            await OnProfileChangedAsync();
        }
        catch (AppException ex) when (ex.Kind == ErrorKind.SsoCancelled)
        {
            ShowSignInRequired(true);
            ShowError(ex);
        }
        catch (Exception ex)
        {
            ShowSignInRequired(true);
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
            if (_ready == AppReadyState.SignInRequired)
            {
                _signIn.Enabled = true;
                _deviceCode.Enabled = true;
            }
            ResetProgressIdle();
        }
    }

    private async Task ResolveTargetAsync(AwsContext context)
    {
        try
        {
            SetBusy(true, "Resolving bastion target…");
            SetReady(AppReadyState.ResolvingTarget, "Resolving target…");
            var (region, target, candidates) = await _orchestrator.ResolveTargetAsync(context, _region, _cts.Token);
            _region = region;
            _regionLabel.Text = $"Region: {_region}";

            if (target is null && candidates.Count > 1)
            {
                using var dlg = new TargetSelectionDialog(candidates);
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected is null)
                {
                    SetReady(AppReadyState.Error, "Multiple eligible bastions were found.");
                    return;
                }
                target = dlg.Selected;
            }

            _target = target;
            _targetLabel.Text = $"Bastion: ✓ {target!.Name} / SSM {target.SsmPingStatus}";
            SetReady(AppReadyState.Ready, "Ready");
        }
        catch (AppException ex) when (ex.Kind == ErrorKind.RegionMissing)
        {
            using var dlg = new RegionSelectionDialog(_region);
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                SetReady(AppReadyState.Error, ex.UserMessage);
                return;
            }
            _region = dlg.SelectedRegion;
            await ResolveTargetAsync(context with { Region = _region });
        }
        catch (Exception ex)
        {
            _target = null;
            _targetLabel.Text = "Bastion: —";
            ShowError(ex);
            SetReady(AppReadyState.Error, ex is AppException ae ? ae.UserMessage : ex.Message);
        }
        finally
        {
            SetBusy(false);
            ResetProgressIdle();
        }
    }

    private async Task ConnectEnabledAsync()
    {
        if (_ready != AppReadyState.Ready)
        {
            MessageBox.Show(this,
                _ready == AppReadyState.SignInRequired
                    ? "Sign in is required before starting tunnels."
                    : "Authenticate and resolve a bastion before starting tunnels. Click Reload or select a profile.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        CommitGrid();
        var rules = _rows.Select(r => r.ToRule()).Where(r => r.Enabled).ToList();
        await StartRulesAsync(rules);
    }

    private async Task StartSingleAsync(ConnectionRow row)
    {
        if (_ready != AppReadyState.Ready)
        {
            MessageBox.Show(this,
                _ready == AppReadyState.SignInRequired
                    ? "Sign in is required before starting tunnels."
                    : "Authenticate and resolve a bastion before starting tunnels. Click Reload or select a profile.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        CommitGrid();
        row.Enabled = true;
        await StartRulesAsync([row.ToRule()]);
    }

    private async Task StartRulesAsync(IReadOnlyList<PortForwardRule> rules)
    {
        if (_folder is null || _profiles.SelectedItem is null || _target is null || _region is null) return;
        if (!_prereqs.SessionManagerReady)
        {
            MessageBox.Show(this, "Session Manager plugin was not found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var validation = PortForwardRuleValidator.ValidateEnabledSet(rules);
        if (!validation.IsValid)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, validation.Errors), Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (PortForwardRuleValidator.RequiresConfirmation(rules))
        {
            var ok = MessageBox.Show(this, $"Start {rules.Count(r => r.Enabled)} connections?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ok != DialogResult.Yes) return;
        }

        await PersistAsync();
        await RunBusy(async () =>
        {
            var context = new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region);
            await _aws.GetCallerIdentityAsync(context, _cts.Token);
            await _orchestrator.ConnectAsync(context, _target.InstanceId, rules, _cts.Token);
        }, "Connecting tunnels…");
    }

    private async Task RetryFailedAsync()
    {
        if (_folder is null || _profiles.SelectedItem is null || _target is null || _region is null) return;
        var context = new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region);
        await RunBusy(() => _orchestrator.RetryFailedAsync(context, _target.InstanceId, _cts.Token), "Retrying failed tunnels…");
    }

    private void CommitGrid()
    {
        _grid.EndEdit();
        if (_grid.IsCurrentCellInEditMode)
            _grid.EndEdit();
    }

    private void SyncSessionStatuses()
    {
        foreach (var row in _rows)
        {
            var session = _orchestrator.GetSession(row.Id);
            if (session is null)
            {
                row.RefreshValidation();
                continue;
            }
            row.StatusText = session.State switch
            {
                SessionState.Reconnecting => $"Reconnecting {session.RetryAttempt}/{session.MaxRetries}",
                _ => session.State.ToString()
            };
            if (!string.IsNullOrWhiteSpace(session.LastError) &&
                session.State is SessionState.Failed or SessionState.Reconnecting)
                row.StatusText = $"{session.State}: {session.LastError}";
        }
        _grid.Refresh();
        UpdateSummaryAndConnect();
        RefreshStartButtons();
    }

    private void UpdateSummaryAndConnect()
    {
        var rules = _rows.Select(r => r.ToRule()).ToList();
        var enabled = rules.Where(r => r.Enabled).ToList();
        var ready = enabled.Count(r => PortForwardRuleValidator.Validate(r).IsValid);
        var incomplete = enabled.Count - ready;
        var active = _orchestrator.Sessions.Count(s =>
            s.State is SessionState.Connected or SessionState.Connecting or SessionState.Reconnecting);
        _summary.Text = $"{ready} ready • {incomplete} incomplete • {active} active";

        var allValid = enabled.Count > 0 && incomplete == 0;
        _connect.Enabled = _busyCount == 0 && CanConnect() && allValid;
        RefreshStartButtons();
    }

    private void PreserveActiveSessionStatus(ConnectionRow row)
    {
        var session = _orchestrator.GetSession(row.Id);
        if (session is null) return;
        row.StatusText = session.State switch
        {
            SessionState.Reconnecting => $"Reconnecting {session.RetryAttempt}/{session.MaxRetries}",
            _ => session.State.ToString()
        };
        if (!string.IsNullOrWhiteSpace(session.LastError) &&
            session.State is SessionState.Failed or SessionState.Reconnecting)
            row.StatusText = $"{session.State}: {session.LastError}";
    }

    private bool IsSessionActive(ConnectionRow row)
    {
        var session = _orchestrator.GetSession(row.Id);
        return session?.State is SessionState.Connected or SessionState.Connecting
            or SessionState.Reconnecting or SessionState.Stopping;
    }

    private bool CanStartRow(ConnectionRow row)
    {
        if (_busyCount > 0 || !CanConnect()) return false;
        if (IsSessionActive(row)) return false;
        return PortForwardRuleValidator.Validate(row.ToRule()).IsValid;
    }

    private void RefreshStartButtons()
    {
        if (_startColumnIndex < 0 || _grid.Rows.Count == 0) return;
        for (var i = 0; i < _rows.Count && i < _grid.Rows.Count; i++)
        {
            var canStart = CanStartRow(_rows[i]);
            var cell = _grid.Rows[i].Cells[_startColumnIndex];
            // Empty value greys out / blocks the button look; clicks are also guarded in CanStartRow.
            cell.Value = canStart ? "Start" : "";
        }
    }

    private void ClearAuthTarget()
    {
        _identity = null;
        _region = null;
        _target = null;
        _regionLabel.Text = "Region: —";
        _targetLabel.Text = "Bastion: —";
        _authLabel.Text = "Authentication: —";
        UpdateSummaryAndConnect();
    }

    private bool CanConnect() =>
        _ready == AppReadyState.Ready && _prereqs.AwsCliReady && _target is not null && _identity is not null;

    private void SetReady(AppReadyState state, string status)
    {
        _ready = state;
        _status.Text = status;
        UpdateSummaryAndConnect();
    }

    private void ApplyProgress(ProgressUpdate update)
    {
        _status.Text = update.Message;
        // Never use Marquee — it runs forever if IsBusy is left true.
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.MarqueeAnimationSpeed = 0;
        if (update.Percent is int p)
            _progress.Value = Math.Clamp(p, 0, 100);
        else if (update.IsBusy)
            _progress.Value = Math.Max(_progress.Value, 8);
        else if (_busyCount == 0)
            _progress.Value = 0;
    }

    private void ResetProgressIdle()
    {
        if (_busyCount > 0) return;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.MarqueeAnimationSpeed = 0;
        _progress.Value = 0;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        if (busy) _busyCount++;
        else _busyCount = Math.Max(0, _busyCount - 1);
        var isBusy = _busyCount > 0;
        UseWaitCursor = isBusy;
        _browse.Enabled = !isBusy;
        _reload.Enabled = !isBusy;
        _profiles.Enabled = !isBusy;
        _addConnection.Enabled = !isBusy;
        if (message is not null)
            ApplyProgress(new ProgressUpdate { Message = message, IsBusy = isBusy, Percent = isBusy ? 12 : 0 });
        if (!isBusy)
            ResetProgressIdle();
        UpdateSummaryAndConnect();
    }

    private async Task PersistAsync()
    {
        _settings.RememberConnections = _remember.Checked;
        _settings.Connections = _remember.Checked
            ? _rows.Select(r => ConnectionSettingsMapper.ToSaved(r.ToRule())).ToList()
            : [];
        try { await _settingsStore.SaveAsync(_settings, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "settings save failed"); }
    }

    private async Task RunBusy(Func<Task> action, string busyMessage)
    {
        SetBusy(true, busyMessage);
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            SetBusy(false);
            ResetProgressIdle();
        }
    }

    private void ShowError(Exception ex)
    {
        if (ex is AppException app)
        {
            ApplyProgress(new ProgressUpdate { Message = app.UserMessage, IsBusy = false, Percent = 0 });
            MessageBox.Show(this,
                $"{app.UserMessage}{Environment.NewLine}{Environment.NewLine}{app.ActionHint}" +
                (string.IsNullOrWhiteSpace(app.TechnicalDetails) ? "" : $"{Environment.NewLine}{Environment.NewLine}{app.TechnicalDetails}"),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            _logger.LogError(ex, "UI error");
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitConfirmed) return;
        var active = _orchestrator.Sessions.Count(s =>
            s.State is SessionState.Connected or SessionState.Connecting or SessionState.Reconnecting);
        if (active > 0)
        {
            var result = MessageBox.Show(this,
                $"There are {active} active AWS tunnels.{Environment.NewLine}Stop all tunnels and exit?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes) { e.Cancel = true; return; }
        }
        e.Cancel = true;
        _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        try
        {
            ApplyProgress(new ProgressUpdate { Message = "Stopping tunnels and exiting…", IsBusy = true, Percent = 20 });
            await _orchestrator.StopAllAsync(CancellationToken.None);
            await PersistAsync();
        }
        finally
        {
            _cts.Cancel();
            _exitConfirmed = true;
            Close();
        }
    }
}

public sealed class ConnectionRow : INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _name = "";
    private string _typeDisplay = "Managed node";
    private string _destination = "Bastion (same target)";
    private string _remotePortText = "";
    private string _localPortText = "";
    private string _statusText = "Incomplete";

    public Guid Id { get; init; } = Guid.NewGuid();
    public bool LocalPortLinked { get; set; } = true;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnChanged(nameof(Enabled)); RefreshValidation(); }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnChanged(nameof(Name));
        }
    }

    public string TypeDisplay
    {
        get => _typeDisplay;
        set
        {
            _typeDisplay = value;
            OnChanged(nameof(TypeDisplay));
            OnChanged(nameof(Type));
            RefreshValidation();
        }
    }

    public PortForwardType Type =>
        _typeDisplay.StartsWith("Remote", StringComparison.OrdinalIgnoreCase)
            ? PortForwardType.RemoteHost
            : PortForwardType.ManagedNode;

    public string Destination
    {
        get => _destination;
        set
        {
            if (_destination == value) return;
            _destination = value;
            OnChanged(nameof(Destination));
            // Do not RefreshValidation here — StatusText change resets grid caret/selection while typing.
        }
    }

    public string RemotePortText
    {
        get => _remotePortText;
        set
        {
            if (_remotePortText == value) return;
            _remotePortText = value;
            OnChanged(nameof(RemotePortText));
        }
    }

    public string LocalPortText
    {
        get => _localPortText;
        set
        {
            if (_localPortText == value) return;
            _localPortText = value;
            LocalPortLinked = false;
            OnChanged(nameof(LocalPortText));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnChanged(nameof(StatusText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ConnectionRow CreateNew() => new();

    public static ConnectionRow FromRule(PortForwardRule rule)
    {
        var row = new ConnectionRow
        {
            Id = rule.Id,
            LocalPortLinked = false
        };
        row._enabled = rule.Enabled;
        row._name = rule.Label ?? "";
        row._typeDisplay = rule.Type == PortForwardType.RemoteHost ? "Remote host" : "Managed node";
        row._destination = rule.Type == PortForwardType.RemoteHost
            ? (rule.RemoteHost ?? "")
            : "Bastion (same target)";
        row._remotePortText = rule.RemotePort?.ToString() ?? "";
        row._localPortText = rule.LocalPort?.ToString() ?? "";
        row.RefreshValidation();
        return row;
    }

    public PortForwardRule ToRule()
    {
        int? remote = int.TryParse(RemotePortText, out var r) ? r : null;
        int? local = int.TryParse(LocalPortText, out var l) ? l : null;
        var label = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
        return new PortForwardRule(
            Id,
            Enabled,
            Type,
            Type == PortForwardType.RemoteHost ? Destination.Trim() : null,
            remote,
            local,
            label);
    }

    public void SetDestinationWithoutNotify(string value) => _destination = value;

    public void SetLocalPortWithoutNotify(string value) => _localPortText = value;

    public void RefreshValidation()
    {
        var result = PortForwardRuleValidator.Validate(ToRule());
        if (result.IsValid)
            StatusText = "Ready";
        else
            StatusText = result.Status == SessionState.Incomplete
                ? "Incomplete"
                : $"Invalid: {result.Errors.FirstOrDefault()}";
    }

    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
