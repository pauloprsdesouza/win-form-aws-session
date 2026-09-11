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
    private readonly Button _browse = new() { Text = "Browse", Width = 90, Height = 28 };
    private readonly Label _folderStatus = new() { AutoSize = true, Text = "—" };
    private readonly Label _prereqStatus = new() { AutoSize = true, Text = "—" };
    private readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _reload = new() { Text = "Reload", Width = 90, Height = 28 };
    private readonly Label _authLabel = new() { AutoSize = true, Text = "Authentication: —" };
    private readonly Button _signIn = new() { Text = "Sign in", Width = 90, Height = 28, Enabled = false };
    private readonly Button _deviceCode = new() { Text = "Device code", Width = 100, Height = 28, Enabled = false, Visible = false };
    private readonly Label _regionLabel = new() { AutoSize = true, Text = "Region: —" };
    private readonly Label _targetLabel = new() { AutoSize = true, Text = "Bastion: —", MaximumSize = new Size(420, 0) };
    private readonly CheckBox _remember = new() { Text = "Remember connections", AutoSize = true };
    private readonly Button _addConnection = new() { Text = "+ Add connection", AutoSize = true };
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
        EnableHeadersVisualStyles = false
    };
    private readonly Label _summary = new() { AutoSize = true, Text = "0 ready • 0 incomplete • 0 active", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _connect = new() { Text = "Start enabled", Width = 120, Height = 32, Enabled = false };
    private readonly Button _stopAll = new() { Text = "Stop all", Width = 100, Height = 32 };
    private readonly Button _retryFailed = new() { Text = "Retry failed", Width = 110, Height = 32 };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, Text = "Initializing", TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };

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
        MinimumSize = new Size(860, 640);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 700);

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
        _grid.DefaultCellStyle.Padding = new Padding(4);
        _grid.RowTemplate.Height = 28;

        var enabled = new DataGridViewCheckBoxColumn { DataPropertyName = nameof(ConnectionRow.Enabled), HeaderText = "", Width = 36 };
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
            MinimumWidth = 180
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
        var start = new DataGridViewButtonColumn { HeaderText = "", Text = "Start", Width = 60, UseColumnTextForButtonValue = true };
        var stop = new DataGridViewButtonColumn { HeaderText = "", Text = "Stop", Width = 60, UseColumnTextForButtonValue = true };
        var delete = new DataGridViewButtonColumn { HeaderText = "", Text = "Delete", Width = 70, UseColumnTextForButtonValue = true };

        _grid.Columns.AddRange(enabled, type, destination, remote, local, status, start, stop, delete);
        _grid.DataSource = _rows;
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        root.Controls.Add(BuildContextCard(), 0, 0);
        root.Controls.Add(BuildConnectionsCard(), 0, 1);
        root.Controls.Add(BuildActionsRow(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);
    }

    private GroupBox BuildContextCard()
    {
        var group = new GroupBox { Text = "AWS context", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        grid.Controls.Add(Lbl("Folder"), 0, 0);
        grid.Controls.Add(_folderBox, 1, 0);
        grid.Controls.Add(_browse, 2, 0);

        var folderMeta = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _folderStatus.Margin = new Padding(0, 6, 12, 0);
        _prereqStatus.Margin = new Padding(0, 6, 0, 0);
        folderMeta.Controls.AddRange([_folderStatus, _prereqStatus]);
        grid.Controls.Add(folderMeta, 3, 0);

        grid.Controls.Add(Lbl("Profile"), 0, 1);
        grid.Controls.Add(_profiles, 1, 1);
        grid.Controls.Add(_reload, 2, 1);
        var authFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _authLabel.Margin = new Padding(0, 6, 8, 0);
        authFlow.Controls.AddRange([_authLabel, _signIn, _deviceCode]);
        grid.Controls.Add(authFlow, 3, 1);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _regionLabel.Margin = new Padding(0, 6, 24, 0);
        _targetLabel.Margin = new Padding(0, 6, 16, 0);
        _remember.Margin = new Padding(0, 4, 0, 0);
        bottom.Controls.AddRange([_regionLabel, _targetLabel, _remember]);
        grid.Controls.Add(bottom, 1, 2);
        grid.SetColumnSpan(bottom, 3);

        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildConnectionsCard()
    {
        var group = new GroupBox { Text = "Connections", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
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
        _reload.Click += async (_, _) => await ApplyFolderTextAsync();
        _profiles.SelectedIndexChanged += async (_, _) => await OnProfileChangedAsync();
        _signIn.Click += async (_, _) => await SignInAsync(SsoLoginMode.Browser);
        _deviceCode.Click += async (_, _) => await SignInAsync(SsoLoginMode.DeviceCode);
        _addConnection.Click += (_, _) => AddConnectionRow();
        _connect.Click += async (_, _) => await ConnectEnabledAsync();
        _stopAll.Click += async (_, _) => await RunBusy(() => _orchestrator.StopAllAsync(_cts.Token), "Stopping tunnels…");
        _retryFailed.Click += async (_, _) => await RetryFailedAsync();
        _remember.CheckedChanged += async (_, _) => { _settings.RememberConnections = _remember.Checked; await PersistAsync(); };
        _folderBox.Leave += async (_, _) => await ApplyFolderTextAsync();
        _grid.CellValueChanged += GridOnCellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
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
                row.Destination = "Bastion (same target)";
            else if (row.Destination == "Bastion (same target)")
                row.Destination = "";
        }

        if (col == nameof(ConnectionRow.RemotePortText) &&
            string.IsNullOrWhiteSpace(row.LocalPortText) &&
            !string.IsNullOrWhiteSpace(row.RemotePortText))
        {
            row.LocalPortText = row.RemotePortText;
            row.LocalPortLinked = false;
        }

        row.RefreshValidation();
        UpdateDestinationReadOnly(e.RowIndex);
        UpdateSummaryAndConnect();
        _ = PersistAsync();
    }

    private void UpdateDestinationReadOnly(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;
        var cell = _grid.Rows[rowIndex].Cells[2];
        var managed = _rows[rowIndex].Type == PortForwardType.ManagedNode;
        cell.ReadOnly = managed;
        cell.Style.BackColor = managed ? Color.FromArgb(245, 245, 245) : SystemColors.Window;
    }

    private async Task GridOnButtonClickAsync(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        var row = _rows[e.RowIndex];
        var header = _grid.Columns[e.ColumnIndex].HeaderText;
        var text = (_grid.Columns[e.ColumnIndex] as DataGridViewButtonColumn)?.Text;

        if (text == "Start")
            await StartSingleAsync(row);
        else if (text == "Stop")
            await RunBusy(() => _orchestrator.StopAsync(row.Id, _cts.Token), "Stopping…");
        else if (text == "Delete")
            await DeleteRowAsync(row);
    }

    private void AddConnectionRow()
    {
        var row = ConnectionRow.CreateNew();
        _rows.Add(row);
        UpdateDestinationReadOnly(_rows.Count - 1);
        UpdateSummaryAndConnect();
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
        await PersistAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            SetBusy(true, "Checking prerequisites…");
            _settings = await _settingsStore.LoadAsync(_cts.Token);
            _remember.Checked = _settings.RememberConnections;
            LoadSavedConnections();

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
            await ApplyFolderTextAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
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
        await ApplyFolderTextAsync();
    }

    private async Task ApplyFolderTextAsync()
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
            SetBusy(true, "Discovering profiles…");
            SetReady(AppReadyState.DiscoveringProfiles, "Discovering profiles…");

            _folder = AwsFolderFactory.FromFolder(_folderBox.Text);
            _folderStatus.Text = $"{(_folder.HasConfig ? "✓ config" : "○ config")}   {(_folder.HasCredentials ? "✓ credentials" : "○ credentials")}";
            _settings.LastAwsFolder = _folder.FolderPath;
            await PersistAsync();

            var profiles = await _aws.ListProfilesAsync(_folder, _cts.Token);
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
        catch (Exception ex)
        {
            ShowError(ex);
            SetReady(AppReadyState.FolderRequired, "Select an AWS folder.");
        }
        finally { SetBusy(false); }
    }

    private async Task OnProfileChangedAsync()
    {
        if (_folder is null || _profiles.SelectedItem is null) return;
        try
        {
            ClearAuthTarget();
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
                _authLabel.Text = "Authentication: Sign-in required";
                _signIn.Enabled = true;
                _deviceCode.Visible = true;
                _deviceCode.Enabled = true;
                SetReady(AppReadyState.SignInRequired, "Sign-in approval is required.");
                return;
            }

            if (identity is null)
            {
                _authLabel.Text = $"Authentication: {message ?? "invalid"}";
                _signIn.Enabled = false;
                SetReady(AppReadyState.Error, message ?? "Credentials are missing, invalid, or expired.");
                return;
            }

            _authLabel.Text = isSso ? "Authentication: ✓ SSO Connected" : "Authentication: ✓ Connected";
            _signIn.Enabled = false;
            _deviceCode.Visible = false;
            await ResolveTargetAsync(context);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async Task SignInAsync(SsoLoginMode mode)
    {
        if (_folder is null || _profiles.SelectedItem is null) return;
        try
        {
            SetBusy(true, mode == SsoLoginMode.Browser ? "Waiting for browser sign-in…" : "Waiting for device code…");
            await _orchestrator.SsoLoginAsync(new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region), mode, _cts.Token);
            await OnProfileChangedAsync();
        }
        catch (Exception ex)
        {
            _deviceCode.Visible = true;
            _deviceCode.Enabled = true;
            ShowError(ex);
        }
        finally { SetBusy(false); }
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
        finally { SetBusy(false); }
    }

    private async Task ConnectEnabledAsync()
    {
        CommitGrid();
        var rules = _rows.Select(r => r.ToRule()).Where(r => r.Enabled).ToList();
        await StartRulesAsync(rules);
    }

    private async Task StartSingleAsync(ConnectionRow row)
    {
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
        if (update.IsBusy)
        {
            if (update.Percent is int p)
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = Math.Clamp(p, 0, 100);
            }
            else
            {
                _progress.Style = ProgressBarStyle.Marquee;
                _progress.MarqueeAnimationSpeed = 30;
            }
        }
        else
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Value = update.Percent ?? 0;
        }
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
            ApplyProgress(new ProgressUpdate { Message = message, IsBusy = isBusy, Percent = isBusy ? null : 0 });
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
        finally { SetBusy(false); }
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
            ApplyProgress(new ProgressUpdate { Message = "Stopping tunnels and exiting…", IsBusy = true });
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
        set { _destination = value; OnChanged(nameof(Destination)); RefreshValidation(); }
    }

    public string RemotePortText
    {
        get => _remotePortText;
        set { _remotePortText = value; OnChanged(nameof(RemotePortText)); RefreshValidation(); }
    }

    public string LocalPortText
    {
        get => _localPortText;
        set { _localPortText = value; LocalPortLinked = false; OnChanged(nameof(LocalPortText)); RefreshValidation(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnChanged(nameof(StatusText)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ConnectionRow CreateNew() => new();

    public static ConnectionRow FromRule(PortForwardRule rule) => new()
    {
        Id = rule.Id,
        Enabled = rule.Enabled,
        TypeDisplay = rule.Type == PortForwardType.RemoteHost ? "Remote host" : "Managed node",
        Destination = rule.Type == PortForwardType.RemoteHost
            ? (rule.RemoteHost ?? "")
            : "Bastion (same target)",
        RemotePortText = rule.RemotePort?.ToString() ?? "",
        LocalPortText = rule.LocalPort?.ToString() ?? "",
        LocalPortLinked = false
    };

    public PortForwardRule ToRule()
    {
        int? remote = int.TryParse(RemotePortText, out var r) ? r : null;
        int? local = int.TryParse(LocalPortText, out var l) ? l : null;
        return new PortForwardRule(
            Id,
            Enabled,
            Type,
            Type == PortForwardType.RemoteHost ? Destination.Trim() : null,
            remote,
            local);
    }

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
