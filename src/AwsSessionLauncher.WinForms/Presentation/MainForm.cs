using System.Collections.Concurrent;
using AwsSessionLauncher.Application;
using AwsSessionLauncher.Domain;
using AwsSessionLauncher.Domain.Enums;
using AwsSessionLauncher.Infrastructure;
using AwsSessionLauncher.Presentation.Dialogs;
using Microsoft.Extensions.Logging;

namespace AwsSessionLauncher.Presentation;

public sealed class MainForm : Form
{
    private readonly IAwsProfileService _profiles;
    private readonly IAwsAuthenticationService _auth;
    private readonly IAwsInstanceDiscoveryService _discovery;
    private readonly ISsmSessionService _sessions;
    private readonly ISettingsService _settingsService;
    private readonly IAwsEnvironmentService _environment;
    private readonly IPortValidationService _portValidation;
    private readonly ILogger<MainForm> _logger;

    private readonly Label _awsStatus = new() { AutoSize = true };
    private readonly Label _ssmStatus = new() { AutoSize = true };
    private readonly ComboBox _profileCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Button _signInButton = new() { Text = "Sign in", AutoSize = true, Enabled = false };
    private readonly Button _validateButton = new() { Text = "Validate", AutoSize = true };
    private readonly Label _accountLabel = new() { AutoSize = true, Text = "Account: —" };
    private readonly Label _identityLabel = new() { AutoSize = true, Text = "Identity: —", MaximumSize = new Size(700, 0) };
    private readonly TextBox _regionBox = new() { Width = 120 };
    private readonly TextBox _tagNameBox = new() { Text = "Name", Width = 80 };
    private readonly TextBox _tagValueBox = new() { Text = "bastion-host", Width = 180 };
    private readonly Button _refreshTargets = new() { Text = "Refresh", AutoSize = true };
    private readonly ListView _targets = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        Height = 120,
        Dock = DockStyle.Fill
    };
    private readonly ListView _rules = new()
    {
        View = View.Details,
        FullRowSelect = true,
        CheckBoxes = true,
        Height = 160,
        Dock = DockStyle.Fill
    };
    private readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f)
    };

    private readonly Button _addRule = new() { Text = "+ Add", AutoSize = true };
    private readonly Button _editRule = new() { Text = "Edit", AutoSize = true };
    private readonly Button _removeRule = new() { Text = "Remove", AutoSize = true };
    private readonly Button _duplicateRule = new() { Text = "Duplicate", AutoSize = true };
    private readonly Button _startSelected = new() { Text = "Start", AutoSize = true };
    private readonly Button _stopSelected = new() { Text = "Stop", AutoSize = true };
    private readonly Button _startAll = new() { Text = "Start All", AutoSize = true };
    private readonly Button _stopAll = new() { Text = "Stop All", AutoSize = true };
    private readonly Button _retry = new() { Text = "Retry", AutoSize = true };
    private readonly Button _copyDiagnostics = new() { Text = "Copy diagnostics", AutoSize = true };
    private readonly Button _setupPaths = new() { Text = "AWS paths…", AutoSize = true };

    private AppSettings _settings = new();
    private List<AwsProfile> _profileList = [];
    private List<PortForwardRule> _ruleList = [];
    private PrerequisiteStatus _prereqs = new();
    private ProfileAuthState? _authState;
    private readonly ConcurrentQueue<string> _activity = new();
    private CancellationTokenSource _cts = new();

    public MainForm(
        IAwsProfileService profiles,
        IAwsAuthenticationService auth,
        IAwsInstanceDiscoveryService discovery,
        ISsmSessionService sessions,
        ISettingsService settingsService,
        IAwsEnvironmentService environment,
        IPortValidationService portValidation,
        ILogger<MainForm> logger)
    {
        _profiles = profiles;
        _auth = auth;
        _discovery = discovery;
        _sessions = sessions;
        _settingsService = settingsService;
        _environment = environment;
        _portValidation = portValidation;
        _logger = logger;

        Text = "AWS Session Launcher";
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(900, 650);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        WireEvents();
        Shown += async (_, _) => await InitializeAsync();
    }

    private void BuildLayout()
    {
        _targets.Columns.AddRange([
            new ColumnHeader { Text = "Instance ID", Width = 160 },
            new ColumnHeader { Text = "Name", Width = 160 },
            new ColumnHeader { Text = "Private IP", Width = 110 },
            new ColumnHeader { Text = "AZ", Width = 100 },
            new ColumnHeader { Text = "State", Width = 80 },
            new ColumnHeader { Text = "SSM", Width = 110 }
        ]);

        _rules.Columns.AddRange([
            new ColumnHeader { Text = "Name", Width = 140 },
            new ColumnHeader { Text = "Mode", Width = 100 },
            new ColumnHeader { Text = "Host", Width = 160 },
            new ColumnHeader { Text = "Remote", Width = 70 },
            new ColumnHeader { Text = "Local", Width = 70 },
            new ColumnHeader { Text = "Status", Width = 100 }
        ]);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        header.Controls.Add(new Label { Text = "AWS Session Launcher", AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Margin = new Padding(0, 6, 20, 0) });
        header.Controls.Add(_awsStatus);
        header.Controls.Add(_ssmStatus);
        header.Controls.Add(_setupPaths);
        header.Controls.Add(_copyDiagnostics);
        root.Controls.Add(header, 0, 0);

        var profilePanel = new GroupBox { Text = "Profile", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var profileFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        profileFlow.Controls.AddRange([
            new Label { Text = "Profile", AutoSize = true, Margin = new Padding(0, 8, 4, 0) },
            _profileCombo,
            _signInButton,
            _validateButton,
            new Label { Text = "Region", AutoSize = true, Margin = new Padding(12, 8, 4, 0) },
            _regionBox,
            _accountLabel,
            _identityLabel
        ]);
        profilePanel.Controls.Add(profileFlow);
        root.Controls.Add(profilePanel, 0, 1);

        var targetPanel = new GroupBox { Text = "Target / Bastion", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var targetLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        targetLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        targetLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var filterFlow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        filterFlow.Controls.AddRange([
            new Label { Text = "Tag", AutoSize = true, Margin = new Padding(0, 8, 4, 0) },
            _tagNameBox,
            new Label { Text = "=", AutoSize = true, Margin = new Padding(4, 8, 4, 0) },
            _tagValueBox,
            _refreshTargets
        ]);
        targetLayout.Controls.Add(filterFlow, 0, 0);
        targetLayout.Controls.Add(_targets, 0, 1);
        targetPanel.Controls.Add(targetLayout);
        root.Controls.Add(targetPanel, 0, 2);

        var rulesPanel = new GroupBox { Text = "Port Forwarding", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var rulesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        rulesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rulesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        rulesLayout.Controls.Add(_rules, 0, 0);
        var ruleButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        ruleButtons.Controls.AddRange([
            _addRule, _editRule, _removeRule, _duplicateRule,
            _startSelected, _stopSelected, _retry, _startAll, _stopAll
        ]);
        rulesLayout.Controls.Add(ruleButtons, 0, 1);
        rulesPanel.Controls.Add(rulesLayout);
        root.Controls.Add(rulesPanel, 0, 3);

        var logPanel = new GroupBox { Text = "Activity / Logs", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logPanel.Controls.Add(_logBox);
        root.Controls.Add(logPanel, 0, 4);

        Controls.Add(root);
    }

    private void WireEvents()
    {
        _sessions.Activity += (_, msg) => BeginInvoke(() => AppendLog(msg));
        _sessions.SessionsChanged += (_, _) => BeginInvoke(RefreshRuleStatuses);

        _profileCombo.SelectedIndexChanged += async (_, _) => await OnProfileChangedAsync();
        _signInButton.Click += async (_, _) => await RunBusyAsync(SignInAsync);
        _validateButton.Click += async (_, _) => await RunBusyAsync(ValidateSelectedAsync);
        _refreshTargets.Click += async (_, _) => await RunBusyAsync(RefreshTargetsAsync);
        _addRule.Click += (_, _) => AddRule();
        _editRule.Click += (_, _) => EditRule();
        _removeRule.Click += (_, _) => RemoveRule();
        _duplicateRule.Click += (_, _) => DuplicateRule();
        _startSelected.Click += async (_, _) => await RunBusyAsync(() => StartRulesAsync(selectedOnly: true));
        _stopSelected.Click += async (_, _) => await RunBusyAsync(() => StopRulesAsync(selectedOnly: true));
        _startAll.Click += async (_, _) => await RunBusyAsync(() => StartRulesAsync(selectedOnly: false));
        _stopAll.Click += async (_, _) => await RunBusyAsync(() => _sessions.StopAllAsync(_cts.Token));
        _retry.Click += async (_, _) => await RunBusyAsync(RetrySelectedAsync);
        _copyDiagnostics.Click += (_, _) => CopyDiagnostics();
        _setupPaths.Click += async (_, _) => await SetupPathsAsync();
        FormClosing += OnFormClosing;
    }

    private bool _exitConfirmed;

    private async Task InitializeAsync()
    {
        try
        {
            _settings = await _settingsService.LoadAsync(_cts.Token);
            Size = new Size(Math.Max(900, _settings.WindowWidth), Math.Max(650, _settings.WindowHeight));
            _tagNameBox.Text = _settings.TargetFilter.TagName;
            _tagValueBox.Text = _settings.TargetFilter.TagValue;
            _regionBox.Text = _settings.LastRegion ?? "";
            _ruleList = _settingsService.ToRules(_settings).ToList();
            if (_ruleList.Count == 0)
            {
                _ruleList.Add(new PortForwardRule
                {
                    Id = Guid.NewGuid(),
                    Name = "PostgreSQL",
                    Mode = PortForwardMode.RemoteHost,
                    RemoteHost = "db.internal",
                    RemotePort = 5432,
                    LocalPort = 5432,
                    Enabled = true
                });
            }
            BindRules();

            _prereqs = await _environment.CheckPrerequisitesAsync(_cts.Token);
            _awsStatus.Text = _prereqs.AwsCliReady ? $"AWS CLI ✓ {_prereqs.AwsCliVersion}" : "AWS CLI ✗ missing";
            _awsStatus.ForeColor = _prereqs.AwsCliReady ? Color.DarkGreen : Color.Firebrick;
            _ssmStatus.Text = _prereqs.SessionManagerReady ? "SSM Plugin ✓ Ready" : "SSM Plugin ✗ missing — install Session Manager plugin";
            _ssmStatus.ForeColor = _prereqs.SessionManagerReady ? Color.DarkGreen : Color.DarkOrange;

            await LoadProfilesAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            _profileList = (await _profiles.LoadProfilesAsync(_cts.Token)).ToList();
            _profileCombo.Items.Clear();
            foreach (var p in _profileList)
                _profileCombo.Items.Add($"{p.Name}  [{p.Type}]{(p.Region is null ? "" : $" ({p.Region})")}");

            if (_profileList.Count == 0)
            {
                AppendLog("No profiles found. Configure AWS or set paths.");
                await SetupPathsAsync();
                return;
            }

            var idx = _profileList.FindIndex(p => p.Name.Equals(_settings.LastProfile, StringComparison.OrdinalIgnoreCase));
            _profileCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        catch (AppErrorException ex)
        {
            AppendLog(ex.Summary);
            await SetupPathsAsync();
        }
    }

    private async Task OnProfileChangedAsync()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        if (string.IsNullOrWhiteSpace(_regionBox.Text) && !string.IsNullOrWhiteSpace(profile.Region))
            _regionBox.Text = profile.Region;
        await ValidateSelectedAsync();
    }

    private async Task ValidateSelectedAsync()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        AppendLog($"Validating profile {profile.Name}…");
        _authState = await _auth.ValidateAsync(profile, RegionOrNull(), _cts.Token);
        ApplyAuthState(profile);
        if (_authState.Status == AuthStatus.Valid)
            await RefreshTargetsAsync();
    }

    private async Task SignInAsync()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        AppendLog($"Signing in to {profile.Name}…");
        _authState = await _auth.SignInAsync(profile, RegionOrNull(), _cts.Token);
        ApplyAuthState(profile);
        if (_authState.Status == AuthStatus.Valid)
            await RefreshTargetsAsync();
    }

    private void ApplyAuthState(AwsProfile profile)
    {
        _signInButton.Enabled = profile.Type == AwsProfileType.Sso &&
                                _authState?.Status is AuthStatus.LoginRequired or AuthStatus.Invalid or AuthStatus.Unknown;
        _accountLabel.Text = $"Account: {_authState?.Identity?.AccountId ?? "—"}";
        _identityLabel.Text = $"Identity: {_authState?.Identity?.Arn ?? _authState?.Message ?? "—"}";
        AppendLog($"Auth status for {profile.Name}: {_authState?.Status}");
        _settings.LastProfile = profile.Name;
        _settings.LastRegion = RegionOrNull();
        _ = PersistSettingsAsync();
    }

    private async Task RefreshTargetsAsync()
    {
        var profile = SelectedProfile();
        if (profile is null || _authState?.Status != AuthStatus.Valid) return;

        string region;
        try { region = RegionResolver.Resolve(RegionOrNull(), profile.Region); }
        catch (AppErrorException ex) { ShowError(ex); return; }

        _regionBox.Text = region;
        var filter = new TargetFilter
        {
            TagName = _tagNameBox.Text.Trim(),
            TagValue = _tagValueBox.Text.Trim(),
            RequireRunning = true,
            RequireSsmOnline = _settings.TargetFilter.RequireSsmOnline
        };

        AppendLog($"Discovering targets tag:{filter.TagName}={filter.TagValue}…");
        var targets = await _discovery.DiscoverAsync(profile.Name, region, filter, _cts.Token);
        _targets.BeginUpdate();
        _targets.Items.Clear();
        foreach (var t in targets)
        {
            var item = new ListViewItem([
                t.InstanceId,
                t.Name ?? "",
                t.PrivateIpAddress ?? "",
                t.AvailabilityZone ?? "",
                t.State,
                t.SsmStatus.ToString()
            ]) { Tag = t };
            item.ForeColor = t.SsmStatus switch
            {
                SsmTargetStatus.Online => Color.DarkGreen,
                SsmTargetStatus.ConnectionLost => Color.DarkOrange,
                SsmTargetStatus.NotManaged => Color.Gray,
                _ => Color.Firebrick
            };
            _targets.Items.Add(item);
        }
        _targets.EndUpdate();

        if (targets.Count == 0)
            AppendLog("No matching instance.");
        else if (targets.Count == 1)
            _targets.Items[0].Selected = true;
        else
        {
            var last = targets.FirstOrDefault(t => t.InstanceId == _settings.LastInstanceId);
            if (last is not null)
            {
                var item = _targets.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text == last.InstanceId);
                if (item is not null) item.Selected = true;
            }
            AppendLog($"{targets.Count} matching instances — select one.");
        }
    }

    private async Task StartRulesAsync(bool selectedOnly)
    {
        EnsureReadyToTunnel();
        var profile = SelectedProfile()!;
        var region = RegionResolver.Resolve(RegionOrNull(), profile.Region);
        var target = SelectedTarget() ?? throw new AppErrorException(
            AppErrorCategory.TargetNotFound, "No target selected.", "Select an EC2/SSM instance.", "Refresh targets and select one.");

        if (target.SsmStatus is SsmTargetStatus.NotManaged or SsmTargetStatus.Inactive)
            throw new AppErrorException(AppErrorCategory.TargetNotManaged, "Target is not usable via SSM.",
                $"SSM status is {target.SsmStatus}.", "Choose an Online managed instance.");

        SyncRulesFromListView();
        var rules = selectedOnly
            ? GetSelectedRules().Where(r => r.Enabled).ToList()
            : _ruleList.Where(r => r.Enabled).ToList();

        if (rules.Count == 0)
        {
            MessageBox.Show(this, "No enabled rules to start.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _portValidation.ValidateRules(rules);
        _settings.LastInstanceId = target.InstanceId;
        await PersistSettingsAsync();

        if (selectedOnly)
        {
            foreach (var rule in rules)
                await _sessions.StartAsync(profile.Name, region, target.InstanceId, rule, _cts.Token);
        }
        else
        {
            await _sessions.StartAllAsync(profile.Name, region, target.InstanceId, rules, _cts.Token);
        }
    }

    private async Task StopRulesAsync(bool selectedOnly)
    {
        var rules = selectedOnly ? GetSelectedRules() : _ruleList;
        foreach (var rule in rules)
            await _sessions.StopAsync(rule.Id, _cts.Token);
    }

    private async Task RetrySelectedAsync()
    {
        EnsureReadyToTunnel();
        var profile = SelectedProfile()!;
        var region = RegionResolver.Resolve(RegionOrNull(), profile.Region);
        var target = SelectedTarget() ?? throw new AppErrorException(
            AppErrorCategory.TargetNotFound, "No target selected.", "Select an instance.", "Refresh and select a target.");
        foreach (var rule in GetSelectedRules())
            await _sessions.RetryAsync(rule.Id, profile.Name, region, target.InstanceId, _cts.Token);
    }

    private void EnsureReadyToTunnel()
    {
        if (!_prereqs.AwsCliReady)
            throw new AppErrorException(AppErrorCategory.PrerequisiteMissing, "AWS CLI is missing.",
                "aws.exe was not detected.", "Install AWS CLI v2 and restart.");
        if (!_prereqs.SessionManagerReady)
            throw new AppErrorException(AppErrorCategory.PrerequisiteMissing, "Session Manager plugin is missing.",
                "session-manager-plugin.exe was not detected.",
                "Install the AWS Session Manager plugin, then restart.");
        if (_authState?.Status != AuthStatus.Valid)
            throw new AppErrorException(AppErrorCategory.AuthenticationRequired, "Profile is not authenticated.",
                "Caller identity validation has not succeeded.", "Validate or Sign in first.");
    }

    private void AddRule()
    {
        using var dlg = new PortForwardRuleDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _ruleList.Add(dlg.Result);
        BindRules();
        _ = PersistSettingsAsync();
    }

    private void EditRule()
    {
        var rule = GetSelectedRules().FirstOrDefault();
        if (rule is null) return;
        using var dlg = new PortForwardRuleDialog(rule);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var idx = _ruleList.FindIndex(r => r.Id == rule.Id);
        _ruleList[idx] = dlg.Result;
        BindRules();
        _ = PersistSettingsAsync();
    }

    private void RemoveRule()
    {
        var selected = GetSelectedRules().Select(r => r.Id).ToHashSet();
        _ruleList = _ruleList.Where(r => !selected.Contains(r.Id)).ToList();
        BindRules();
        _ = PersistSettingsAsync();
    }

    private void DuplicateRule()
    {
        var rule = GetSelectedRules().FirstOrDefault();
        if (rule is null) return;
        _ruleList.Add(rule with { Id = Guid.NewGuid(), Name = rule.Name + " copy" });
        BindRules();
        _ = PersistSettingsAsync();
    }

    private void BindRules()
    {
        _rules.BeginUpdate();
        _rules.Items.Clear();
        foreach (var rule in _ruleList)
        {
            var state = _sessions.Sessions.FirstOrDefault(s => s.Rule.Id == rule.Id)?.State.ToString() ?? "Stopped";
            var item = new ListViewItem([
                rule.Name,
                rule.Mode.ToString(),
                rule.RemoteHost ?? "",
                rule.RemotePort.ToString(),
                rule.LocalPort.ToString(),
                state
            ]) { Tag = rule, Checked = rule.Enabled };
            _rules.Items.Add(item);
        }
        _rules.EndUpdate();
    }

    private void RefreshRuleStatuses()
    {
        foreach (ListViewItem item in _rules.Items)
        {
            if (item.Tag is not PortForwardRule rule) continue;
            var session = _sessions.Sessions.FirstOrDefault(s => s.Rule.Id == rule.Id);
            item.SubItems[5].Text = session?.State.ToString() ?? "Stopped";
            item.ForeColor = session?.State switch
            {
                SessionState.Running => Color.DarkGreen,
                SessionState.Starting or SessionState.Stopping => Color.DarkGoldenrod,
                SessionState.Failed => Color.Firebrick,
                _ => SystemColors.WindowText
            };
        }
    }

    private void SyncRulesFromListView()
    {
        for (var i = 0; i < _rules.Items.Count; i++)
        {
            if (_rules.Items[i].Tag is PortForwardRule rule)
                _ruleList[i] = rule with { Enabled = _rules.Items[i].Checked };
        }
    }

    private List<PortForwardRule> GetSelectedRules()
    {
        SyncRulesFromListView();
        if (_rules.SelectedItems.Count == 0)
            return _ruleList.Where((_, i) => _rules.Items[i].Checked).ToList();
        return _rules.SelectedItems.Cast<ListViewItem>()
            .Select(i => (PortForwardRule)i.Tag!)
            .ToList();
    }

    private AwsProfile? SelectedProfile()
    {
        if (_profileCombo.SelectedIndex < 0 || _profileCombo.SelectedIndex >= _profileList.Count)
            return null;
        return _profileList[_profileCombo.SelectedIndex];
    }

    private Ec2Target? SelectedTarget() =>
        _targets.SelectedItems.Count == 0 ? null : _targets.SelectedItems[0].Tag as Ec2Target;

    private string? RegionOrNull() =>
        string.IsNullOrWhiteSpace(_regionBox.Text) ? null : _regionBox.Text.Trim();

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        _activity.Enqueue(line);
        while (_activity.Count > 500) _activity.TryDequeue(out _);
        _logBox.AppendText(line + Environment.NewLine);
    }

    private void CopyDiagnostics()
    {
        SyncRulesFromListView();
        var text = DiagnosticsBuilder.Build(
            _prereqs,
            SelectedProfile()?.Name,
            RegionOrNull(),
            SelectedTarget()?.InstanceId,
            _ruleList,
            _activity);
        Clipboard.SetText(text);
        AppendLog("Diagnostics copied to clipboard.");
    }

    private async Task SetupPathsAsync()
    {
        using var dlg = new SetupPathsDialog(_settings.ConfigFilePath, _settings.CredentialsFilePath);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.ConfigFilePath = dlg.ConfigPath;
        _settings.CredentialsFilePath = dlg.CredentialsPath;
        _environment.ApplySettings(_settings);
        await PersistSettingsAsync();
        await LoadProfilesAsync();
    }

    private async Task PersistSettingsAsync()
    {
        SyncRulesFromListView();
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.TargetFilter.TagName = _tagNameBox.Text.Trim();
        _settings.TargetFilter.TagValue = _tagValueBox.Text.Trim();
        _settings.LastRegion = RegionOrNull();
        _settingsService.FromRules(_settings, _ruleList);
        try { await _settingsService.SaveAsync(_settings, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to save settings"); }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        UseWaitCursor = true;
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
        finally { UseWaitCursor = false; }
    }

    private void ShowError(Exception ex)
    {
        if (ex is AppErrorException app)
        {
            AppendLog(app.Summary);
            MessageBox.Show(this, app.ToUserMessage() +
                (string.IsNullOrWhiteSpace(app.TechnicalDetails) ? "" : $"{Environment.NewLine}{Environment.NewLine}Details:{Environment.NewLine}{app.TechnicalDetails}"),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            _logger.LogError(ex, "Unhandled UI error");
            AppendLog(ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitConfirmed) return;

        var active = _sessions.Sessions.Count(s => s.State is SessionState.Running or SessionState.Starting);
        if (active > 0)
        {
            var result = MessageBox.Show(this,
                $"There are {active} active AWS tunnels.{Environment.NewLine}Stop all tunnels and exit?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (result != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        e.Cancel = true;
        _ = ExitCleanupAsync();
    }

    private async Task ExitCleanupAsync()
    {
        try
        {
            await _sessions.StopAllAsync(CancellationToken.None);
            await PersistSettingsAsync();
        }
        finally
        {
            _cts.Cancel();
            _exitConfirmed = true;
            Close();
        }
    }
}
