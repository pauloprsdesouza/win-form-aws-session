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
    private readonly Label _folderStatus = new() { AutoSize = true, Text = "—", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _prereqStatus = new() { AutoSize = true, Text = "—", TextAlign = ContentAlignment.MiddleLeft };
    private readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _reload = new() { Text = "Reload", Width = 90, Height = 28 };
    private readonly Label _authLabel = new() { AutoSize = true, Text = "Authentication: —", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _signIn = new() { Text = "Sign in", Width = 90, Height = 28, Enabled = false };
    private readonly Button _deviceCode = new() { Text = "Device code", Width = 100, Height = 28, Enabled = false, Visible = false };
    private readonly TextBox _ports = new() { Dock = DockStyle.Fill, PlaceholderText = "6106, 5432, 6379:16379" };
    private readonly Label _portsHint = new()
    {
        AutoSize = true,
        Text = "Use remote or remote:local — local defaults to remote",
        ForeColor = SystemColors.GrayText,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Label _targetLabel = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        Text = "Target: —",
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Button _connect = new() { Text = "Connect", Width = 110, Height = 32, Enabled = false };
    private readonly Button _stopAll = new() { Text = "Stop all", Width = 110, Height = 32 };
    private readonly Button _retryFailed = new() { Text = "Retry failed", Width = 110, Height = 32 };
    private readonly ListView _sessions = new()
    {
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Dock = DockStyle.Fill
    };
    private readonly Label _status = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        Text = "Initializing",
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill,
        Style = ProgressBarStyle.Continuous,
        Minimum = 0,
        Maximum = 100,
        Value = 0
    };

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
        MinimumSize = new Size(720, 620);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 640);
        Padding = new Padding(0);

        _sessions.Columns.AddRange([
            new ColumnHeader { Text = "Mapping", Width = 220 },
            new ColumnHeader { Text = "Status", Width = 130 },
            new ColumnHeader { Text = "Retry", Width = 80 },
            new ColumnHeader { Text = "Detail", Width = 280 }
        ]);

        BuildLayout();
        Wire();
        Shown += async (_, _) => await InitializeAsync();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(16, 12, 16, 12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));   // folder
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));  // profile
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));   // ports
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));   // target
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));   // actions
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // sessions
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));   // progress/status

        root.Controls.Add(BuildFolderGroup(), 0, 0);
        root.Controls.Add(BuildProfileGroup(), 0, 1);
        root.Controls.Add(BuildPortsGroup(), 0, 2);

        var targetRow = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 4, 4, 0) };
        targetRow.Controls.Add(_targetLabel);
        root.Controls.Add(targetRow, 0, 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
            AutoSize = false
        };
        // Center the button group horizontally
        actions.Resize += (_, _) => CenterFlowChildren(actions);
        actions.Controls.AddRange([_connect, _stopAll, _retryFailed]);
        foreach (Control c in actions.Controls)
            c.Margin = new Padding(8, 0, 8, 0);
        root.Controls.Add(actions, 0, 4);

        var sessionGroup = new GroupBox
        {
            Text = "Sessions",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 10)
        };
        sessionGroup.Controls.Add(_sessions);
        root.Controls.Add(sessionGroup, 0, 5);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(4, 6, 4, 0)
        };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        footer.Controls.Add(_progress, 0, 0);
        footer.Controls.Add(_status, 0, 1);
        root.Controls.Add(footer, 0, 6);

        Controls.Add(root);
        Shown += (_, _) => CenterFlowChildren(actions);
    }

    private static void CenterFlowChildren(FlowLayoutPanel panel)
    {
        if (panel.Controls.Count == 0) return;
        var totalWidth = panel.Controls.Cast<Control>().Sum(c => c.Width + c.Margin.Horizontal);
        var left = Math.Max(0, (panel.ClientSize.Width - totalWidth) / 2);
        panel.Padding = new Padding(left, panel.Padding.Top, 0, panel.Padding.Bottom);
    }

    private GroupBox BuildFolderGroup()
    {
        var group = new GroupBox { Text = "AWS folder", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        grid.Controls.Add(new Label { Text = "Path", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        grid.Controls.Add(_folderBox, 1, 0);
        grid.Controls.Add(_browse, 2, 0);

        var statusRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 0)
        };
        _folderStatus.Margin = new Padding(0, 0, 16, 0);
        statusRow.Controls.Add(_folderStatus);
        statusRow.Controls.Add(_prereqStatus);
        grid.Controls.Add(statusRow, 1, 1);
        grid.SetColumnSpan(statusRow, 2);

        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildProfileGroup()
    {
        var group = new GroupBox { Text = "Profile", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        grid.Controls.Add(new Label { Text = "Profile", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        grid.Controls.Add(_profiles, 1, 0);
        grid.Controls.Add(_reload, 2, 0);

        var authRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 2, 0, 0)
        };
        _authLabel.Margin = new Padding(0, 4, 12, 0);
        _signIn.Margin = new Padding(0, 0, 8, 0);
        authRow.Controls.Add(_authLabel);
        authRow.Controls.Add(_signIn);
        authRow.Controls.Add(_deviceCode);
        grid.Controls.Add(authRow, 1, 1);
        grid.SetColumnSpan(authRow, 2);

        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildPortsGroup()
    {
        var group = new GroupBox { Text = "Ports", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        grid.Controls.Add(new Label { Text = "Ports", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        grid.Controls.Add(_ports, 1, 0);
        grid.Controls.Add(_portsHint, 1, 1);

        group.Controls.Add(grid);
        return group;
    }

    private void Wire()
    {
        _browse.Click += async (_, _) => await BrowseFolderAsync();
        _reload.Click += async (_, _) => await ReloadAsync();
        _profiles.SelectedIndexChanged += async (_, _) => await OnProfileChangedAsync();
        _signIn.Click += async (_, _) => await SignInAsync(SsoLoginMode.Browser);
        _deviceCode.Click += async (_, _) => await SignInAsync(SsoLoginMode.DeviceCode);
        _connect.Click += async (_, _) => await ConnectAsync();
        _stopAll.Click += async (_, _) => await RunBusy(() => _orchestrator.StopAllAsync(_cts.Token), "Stopping tunnels…");
        _retryFailed.Click += async (_, _) => await RetryFailedAsync();
        _folderBox.Leave += async (_, _) => await ApplyFolderTextAsync();
        _orchestrator.SessionsChanged += (_, _) => BeginInvoke(RefreshSessions);
        _orchestrator.StatusChanged += (_, msg) => BeginInvoke(() => _status.Text = msg);
        _orchestrator.ProgressChanged += (_, update) => BeginInvoke(() => ApplyProgress(update));
        FormClosing += OnClosing;
        Resize += (_, _) => ResizeSessionColumns();
    }

    private void ResizeSessionColumns()
    {
        if (_sessions.Columns.Count < 4) return;
        var usable = Math.Max(200, _sessions.ClientSize.Width - 24);
        _sessions.Columns[0].Width = (int)(usable * 0.32);
        _sessions.Columns[1].Width = (int)(usable * 0.18);
        _sessions.Columns[2].Width = (int)(usable * 0.12);
        _sessions.Columns[3].Width = usable - _sessions.Columns[0].Width - _sessions.Columns[1].Width - _sessions.Columns[2].Width;
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
        _connect.Enabled = !isBusy && CanConnect();
        _browse.Enabled = !isBusy;
        _reload.Enabled = !isBusy;
        _profiles.Enabled = !isBusy;

        if (message is not null)
            ApplyProgress(new ProgressUpdate { Message = message, IsBusy = isBusy, Percent = isBusy ? null : 0 });
        else if (!isBusy)
            ApplyProgress(new ProgressUpdate { Message = _status.Text, IsBusy = false, Percent = _progress.Value });
    }

    private async Task InitializeAsync()
    {
        try
        {
            SetBusy(true, "Checking prerequisites…");
            _settings = await _settingsStore.LoadAsync(_cts.Token);
            _ports.Text = _settings.LastPortsText ?? "";
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
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
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
            _folderStatus.Text = $"{(_folder.HasConfig ? "✓ config found" : "○ config missing")}   {(_folder.HasCredentials ? "✓ credentials found" : "○ credentials missing")}";
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
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadAsync() => await ApplyFolderTextAsync();

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
            string? configContent = null;
            if (_folder.HasConfig)
                configContent = await File.ReadAllTextAsync(_folder.ConfigFilePath!, _cts.Token);

            var (identity, signInRequired, isSso, message) =
                await _orchestrator.ValidateAuthAsync(context, configContent, _cts.Token);
            _identity = identity;

            if (signInRequired)
            {
                _authLabel.Text = "Authentication: SSO • Sign-in required";
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

            _authLabel.Text = isSso ? "Authentication: SSO • Valid" : "Authentication: Valid";
            _signIn.Enabled = false;
            _deviceCode.Visible = false;
            await ResolveTargetAsync(context);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SignInAsync(SsoLoginMode mode)
    {
        if (_folder is null || _profiles.SelectedItem is null) return;
        try
        {
            SetBusy(true, mode == SsoLoginMode.Browser ? "Waiting for browser sign-in…" : "Waiting for device code…");
            var context = new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region);
            await _orchestrator.SsoLoginAsync(context, mode, _cts.Token);
            await OnProfileChangedAsync();
        }
        catch (AppException ex) when (ex.Kind == ErrorKind.SsoCancelled)
        {
            _deviceCode.Visible = true;
            _deviceCode.Enabled = true;
            ShowError(ex);
        }
        catch (Exception ex)
        {
            _deviceCode.Visible = true;
            _deviceCode.Enabled = true;
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
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
            _targetLabel.Text = $"Target: {target!.Name} • {target.InstanceId} • SSM {target.SsmPingStatus}";
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
            _targetLabel.Text = "Target: —";
            ShowError(ex);
            SetReady(AppReadyState.Error, ex is AppException ae ? ae.UserMessage : ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ConnectAsync()
    {
        if (_folder is null || _profiles.SelectedItem is null || _target is null || _region is null) return;
        if (!_prereqs.SessionManagerReady)
        {
            MessageBox.Show(this, "Session Manager plugin was not found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var parsed = PortParser.Parse(_ports.Text);
        if (parsed.Errors.Count > 0 && parsed.Mappings.Count == 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, parsed.Errors), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (parsed.Errors.Count > 0)
            MessageBox.Show(this, string.Join(Environment.NewLine, parsed.Errors), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (parsed.RequiresConfirmation)
        {
            var ok = MessageBox.Show(this, $"Start {parsed.Mappings.Count} port mappings?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ok != DialogResult.Yes) return;
        }

        _settings.LastPortsText = _ports.Text.Trim();
        await PersistAsync();

        await RunBusy(async () =>
        {
            var context = new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region);
            await _aws.GetCallerIdentityAsync(context, _cts.Token);
            await _orchestrator.ConnectAsync(context, _target.InstanceId, parsed.Mappings, _cts.Token);
        }, "Connecting tunnels…");
    }

    private async Task RetryFailedAsync()
    {
        if (_folder is null || _profiles.SelectedItem is null || _target is null || _region is null) return;
        var context = new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region);
        await RunBusy(() => _orchestrator.RetryFailedAsync(context, _target.InstanceId, _cts.Token), "Retrying failed tunnels…");
    }

    private void RefreshSessions()
    {
        _sessions.BeginUpdate();
        _sessions.Items.Clear();
        foreach (var s in _orchestrator.Sessions)
        {
            var mapping = $"{s.Mapping.RemotePort} → localhost:{s.Mapping.LocalPort}";
            var retry = s.State == SessionState.Reconnecting || s.RetryAttempt > 0
                ? $"{s.RetryAttempt}/{s.MaxRetries}"
                : "—";
            var item = new ListViewItem([mapping, s.State.ToString(), retry, s.LastError ?? ""]);
            item.ForeColor = s.State switch
            {
                SessionState.Connected => Color.DarkGreen,
                SessionState.Failed => Color.Firebrick,
                SessionState.Connecting or SessionState.Reconnecting or SessionState.Stopping => Color.DarkGoldenrod,
                _ => SystemColors.WindowText
            };
            _sessions.Items.Add(item);
        }
        _sessions.EndUpdate();
        ResizeSessionColumns();

        // Keep progress visible while any reconnect is in flight
        if (_orchestrator.Sessions.Any(s => s.State is SessionState.Reconnecting or SessionState.Connecting))
        {
            if (_progress.Style != ProgressBarStyle.Marquee && _busyCount == 0)
            {
                _progress.Style = ProgressBarStyle.Marquee;
                _progress.MarqueeAnimationSpeed = 30;
            }
        }
        else if (_busyCount == 0 && _progress.Style == ProgressBarStyle.Marquee)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Value = 0;
        }
    }

    private void ClearAuthTarget()
    {
        _identity = null;
        _region = null;
        _target = null;
        _targetLabel.Text = "Target: —";
        _authLabel.Text = "Authentication: —";
        UpdateConnectEnabled();
    }

    private bool CanConnect() =>
        _ready == AppReadyState.Ready &&
        _prereqs.AwsCliReady &&
        _target is not null &&
        _identity is not null;

    private void UpdateConnectEnabled() => _connect.Enabled = _busyCount == 0 && CanConnect();

    private void SetReady(AppReadyState state, string status)
    {
        _ready = state;
        _status.Text = status;
        UpdateConnectEnabled();
    }

    private async Task PersistAsync()
    {
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
            _settings.LastPortsText = _ports.Text.Trim();
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
