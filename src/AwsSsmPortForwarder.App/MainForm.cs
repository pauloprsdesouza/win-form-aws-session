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
    private readonly IAppConfigStore _configStore;
    private readonly ILogger<MainForm> _logger;

    private readonly TextBox _folderBox = new() { Width = 420 };
    private readonly Button _browse = new() { Text = "Browse", AutoSize = true };
    private readonly Label _folderStatus = new() { AutoSize = true, Text = "—" };
    private readonly Label _prereqStatus = new() { AutoSize = true, Text = "—" };
    private readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Button _reload = new() { Text = "Reload", AutoSize = true };
    private readonly Label _authLabel = new() { AutoSize = true, Text = "Authentication: —" };
    private readonly Button _signIn = new() { Text = "Sign in", AutoSize = true, Enabled = false };
    private readonly Button _deviceCode = new() { Text = "Use device code", AutoSize = true, Enabled = false, Visible = false };
    private readonly TextBox _ports = new() { Width = 420, PlaceholderText = "6106, 5432, 6379:16379" };
    private readonly Label _portsHint = new()
    {
        AutoSize = true,
        Text = "Use remote or remote:local; local defaults to remote",
        ForeColor = SystemColors.GrayText
    };
    private readonly Label _targetLabel = new() { AutoSize = true, Text = "Target: —", MaximumSize = new Size(560, 0) };
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true, Enabled = false };
    private readonly Button _stopAll = new() { Text = "Stop all", AutoSize = true };
    private readonly Button _retryFailed = new() { Text = "Retry failed", AutoSize = true };
    private readonly ListView _sessions = new()
    {
        View = View.Details,
        FullRowSelect = true,
        Height = 140,
        Dock = DockStyle.Fill
    };
    private readonly Label _status = new() { AutoSize = true, Text = "Initializing", Dock = DockStyle.Bottom };

    private UserSettings _settings = new();
    private PrerequisiteStatus _prereqs = new();
    private AwsFolderContext? _folder;
    private AppReadyState _ready = AppReadyState.Initializing;
    private AwsIdentity? _identity;
    private bool _signInRequired;
    private bool _isSso;
    private string? _region;
    private AwsTarget? _target;
    private CancellationTokenSource _cts = new();
    private bool _exitConfirmed;

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
        _configStore = configStore;
        _logger = logger;

        Text = "AWS Port Forwarding";
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(640, 520);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 560);

        _sessions.Columns.AddRange([
            new ColumnHeader { Text = "Mapping", Width = 260 },
            new ColumnHeader { Text = "Status", Width = 120 },
            new ColumnHeader { Text = "Detail", Width = 220 }
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
            RowCount = 6,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var folderGroup = new GroupBox { Text = "AWS folder", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var folderFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        folderFlow.Controls.AddRange([_folderBox, _browse, _folderStatus, _prereqStatus]);
        folderGroup.Controls.Add(folderFlow);
        root.Controls.Add(folderGroup, 0, 0);

        var profileGroup = new GroupBox { Text = "Profile", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var profileFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        profileFlow.Controls.AddRange([_profiles, _reload, _authLabel, _signIn, _deviceCode]);
        profileGroup.Controls.Add(profileFlow);
        root.Controls.Add(profileGroup, 0, 1);

        var portsGroup = new GroupBox { Text = "Ports", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var portsFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        portsFlow.Controls.AddRange([_ports, _portsHint]);
        portsGroup.Controls.Add(portsFlow);
        root.Controls.Add(portsGroup, 0, 2);

        root.Controls.Add(_targetLabel, 0, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        actions.Controls.AddRange([_connect, _stopAll, _retryFailed]);
        root.Controls.Add(actions, 0, 4);

        var sessionPanel = new Panel { Dock = DockStyle.Fill };
        sessionPanel.Controls.Add(_sessions);
        sessionPanel.Controls.Add(_status);
        root.Controls.Add(sessionPanel, 0, 5);

        Controls.Add(root);
    }

    private void Wire()
    {
        _browse.Click += async (_, _) => await BrowseFolderAsync();
        _reload.Click += async (_, _) => await ReloadAsync();
        _profiles.SelectedIndexChanged += async (_, _) => await OnProfileChangedAsync();
        _signIn.Click += async (_, _) => await SignInAsync(SsoLoginMode.Browser);
        _deviceCode.Click += async (_, _) => await SignInAsync(SsoLoginMode.DeviceCode);
        _connect.Click += async (_, _) => await ConnectAsync();
        _stopAll.Click += async (_, _) => await RunBusy(() => _orchestrator.StopAllAsync(_cts.Token));
        _retryFailed.Click += async (_, _) => await RetryFailedAsync();
        _folderBox.Leave += async (_, _) => await ApplyFolderTextAsync();
        _orchestrator.SessionsChanged += (_, _) => BeginInvoke(RefreshSessions);
        _orchestrator.StatusChanged += (_, msg) => BeginInvoke(() => SetStatus(msg));
        FormClosing += OnClosing;
    }

    private async Task InitializeAsync()
    {
        try
        {
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
            if (_orchestrator.Sessions.Any(s => s.State is SessionState.Connected or SessionState.Connecting))
            {
                var confirm = MessageBox.Show(this, "Stop active sessions and change folder?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;
                await _orchestrator.StopAllAsync(_cts.Token);
            }

            _cts.Cancel();
            _cts = new CancellationTokenSource();
            ClearAuthTarget();
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
    }

    private async Task ReloadAsync()
    {
        if (_folder is null) { await ApplyFolderTextAsync(); return; }
        await ApplyFolderTextAsync();
    }

    private async Task OnProfileChangedAsync()
    {
        if (_folder is null || _profiles.SelectedItem is null) return;
        try
        {
            ClearAuthTarget();
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
            _signInRequired = signInRequired;
            _isSso = isSso;

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
    }

    private async Task SignInAsync(SsoLoginMode mode)
    {
        if (_folder is null || _profiles.SelectedItem is null) return;
        try
        {
            SetStatus("Sign-in…");
            var context = new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region);
            await _orchestrator.SsoLoginAsync(context, mode, _cts.Token);
            await OnProfileChangedAsync();
        }
        catch (AppException ex) when (ex.Kind == ErrorKind.SsoCancelled)
        {
            SetStatus("Sign-in was cancelled.");
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
    }

    private async Task ResolveTargetAsync(AwsContext context)
    {
        try
        {
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
            UpdateConnectEnabled();
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
            SetStatus("Connecting…");
            var context = new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region);
            // Revalidate lightly
            await _aws.GetCallerIdentityAsync(context, _cts.Token);
            await _orchestrator.ConnectAsync(context, _target.InstanceId, parsed.Mappings, _cts.Token);
            SetStatus("Ready");
        });
    }

    private async Task RetryFailedAsync()
    {
        if (_folder is null || _profiles.SelectedItem is null || _target is null || _region is null) return;
        var context = new AwsContext(_folder, _profiles.SelectedItem.ToString()!, _region);
        await RunBusy(() => _orchestrator.RetryFailedAsync(context, _target.InstanceId, _cts.Token));
    }

    private void RefreshSessions()
    {
        _sessions.BeginUpdate();
        _sessions.Items.Clear();
        foreach (var s in _orchestrator.Sessions)
        {
            var mapping = $"{s.Mapping.RemotePort} → localhost:{s.Mapping.LocalPort}";
            var item = new ListViewItem([mapping, s.State.ToString(), s.LastError ?? ""]) ;
            item.ForeColor = s.State switch
            {
                SessionState.Connected => Color.DarkGreen,
                SessionState.Failed => Color.Firebrick,
                SessionState.Connecting or SessionState.Stopping => Color.DarkGoldenrod,
                _ => SystemColors.WindowText
            };
            _sessions.Items.Add(item);
        }
        _sessions.EndUpdate();
    }

    private void ClearAuthTarget()
    {
        _identity = null;
        _signInRequired = false;
        _region = null;
        _target = null;
        _targetLabel.Text = "Target: —";
        _authLabel.Text = "Authentication: —";
        UpdateConnectEnabled();
    }

    private void UpdateConnectEnabled()
    {
        _connect.Enabled = _ready == AppReadyState.Ready &&
                           _prereqs.AwsCliReady &&
                           _target is not null &&
                           _identity is not null;
    }

    private void SetReady(AppReadyState state, string status)
    {
        _ready = state;
        SetStatus(status);
        UpdateConnectEnabled();
    }

    private void SetStatus(string text) => _status.Text = text;

    private async Task PersistAsync()
    {
        try { await _settingsStore.SaveAsync(_settings, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "settings save failed"); }
    }

    private async Task RunBusy(Func<Task> action)
    {
        UseWaitCursor = true;
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
        finally { UseWaitCursor = false; }
    }

    private void ShowError(Exception ex)
    {
        if (ex is AppException app)
        {
            SetStatus(app.UserMessage);
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
        var active = _orchestrator.Sessions.Count(s => s.State is SessionState.Connected or SessionState.Connecting);
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
