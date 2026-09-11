using System.Collections.Concurrent;
using AwsSessionLauncher.Domain;
using AwsSessionLauncher.Domain.Enums;
using AwsSessionLauncher.Infrastructure;
using Microsoft.Extensions.Logging;

namespace AwsSessionLauncher.Application;

public interface ISsmSessionService
{
    IReadOnlyCollection<SsmSession> Sessions { get; }
    event EventHandler? SessionsChanged;
    event EventHandler<string>? Activity;

    Task StartAsync(string profile, string region, string instanceId, PortForwardRule rule, CancellationToken ct);
    Task StopAsync(Guid ruleId, CancellationToken ct);
    Task StartAllAsync(string profile, string region, string instanceId, IEnumerable<PortForwardRule> rules, CancellationToken ct);
    Task StopAllAsync(CancellationToken ct);
    Task RetryAsync(Guid ruleId, string profile, string region, string instanceId, CancellationToken ct);
}

public sealed class SsmSessionService(
    IAwsCli awsCli,
    IPortValidationService portValidation,
    ILogger<SsmSessionService> logger) : ISsmSessionService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, LiveSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public IReadOnlyCollection<SsmSession> Sessions =>
        _sessions.Values.Select(s => s.Session).ToList();

    public event EventHandler? SessionsChanged;
    public event EventHandler<string>? Activity;

    public async Task StartAsync(
        string profile, string region, string instanceId, PortForwardRule rule, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(rule.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(rule.Id, out var existing) &&
                existing.Session.State is SessionState.Running or SessionState.Starting)
            {
                return;
            }

            portValidation.ValidateRule(rule);
            portValidation.EnsureLocalPortFree(rule.LocalPort);

            var session = new SsmSession
            {
                Id = Guid.NewGuid(),
                Rule = rule,
                InstanceId = instanceId,
                State = SessionState.Starting,
                StartedAt = DateTimeOffset.Now
            };

            var live = new LiveSession(session);
            _sessions[rule.Id] = live;
            RaiseChanged();
            RaiseActivity($"Starting '{rule.Name}' on localhost:{rule.LocalPort}");

            portValidation.EnsureLocalPortFree(rule.LocalPort);

            var process = await awsCli.StartPortForwardAsync(new StartPortForwardRequest
            {
                Profile = profile,
                Region = region,
                InstanceId = instanceId,
                Rule = rule
            }, ct).ConfigureAwait(false);

            live.Process = process;
            session.ProcessId = process.Id;

            var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputReceived += (_, line) =>
            {
                live.RecentOutput.Enqueue(line);
                while (live.RecentOutput.Count > 50) live.RecentOutput.TryDequeue(out string? _);
                if (line.Contains("Waiting for connections", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Port", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("opened", StringComparison.OrdinalIgnoreCase))
                {
                    readyTcs.TrySetResult();
                }
                RaiseActivity($"[{rule.Name}] {line}");
            };
            process.ErrorReceived += (_, line) =>
            {
                live.RecentErrors.Enqueue(line);
                while (live.RecentErrors.Count > 50) live.RecentErrors.TryDequeue(out string? _);
                RaiseActivity($"[{rule.Name}] {line}");
            };
            process.Exited += (_, _) =>
            {
                if (session.State is SessionState.Stopping or SessionState.Stopped)
                {
                    session.State = SessionState.Stopped;
                }
                else
                {
                    session.State = SessionState.Failed;
                    session.LastError = live.RecentErrors.LastOrDefault() ?? $"Process exited ({process.ExitCode})";
                    RaiseActivity($"Tunnel '{rule.Name}' exited unexpectedly.");
                }
                RaiseChanged();
            };

            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyCts.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await readyTcs.Task.WaitAsync(readyCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // fall through — process still alive counts as started after timeout
            }

            if (process.HasExited)
            {
                session.State = SessionState.Failed;
                session.LastError = string.Join(Environment.NewLine, live.RecentErrors.TakeLast(5));
                RaiseChanged();
                throw new AppErrorException(
                    AppErrorCategory.SessionStartFailed,
                    $"Could not start '{rule.Name}' tunnel.",
                    "The AWS start-session process exited during startup.",
                    "Verify the target is SSM Online and the ports/host are correct.",
                    session.LastError);
            }

            session.State = SessionState.Running;
            RaiseActivity($"'{rule.Name}' tunnel listening on localhost:{rule.LocalPort}");
            RaiseChanged();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(Guid ruleId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(ruleId, out var live))
            return;

        live.Session.State = SessionState.Stopping;
        RaiseChanged();
        RaiseActivity($"Stopping '{live.Session.Rule.Name}'");

        if (live.Process is not null)
            await live.Process.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        live.Session.State = SessionState.Stopped;
        _sessions.TryRemove(ruleId, out _);
        RaiseChanged();
    }

    public async Task StartAllAsync(
        string profile, string region, string instanceId, IEnumerable<PortForwardRule> rules, CancellationToken ct)
    {
        portValidation.ValidateRules(rules.ToList());
        using var gate = new SemaphoreSlim(3);
        var tasks = rules.Where(r => r.Enabled).Select(async rule =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await StartAsync(profile, region, instanceId, rule, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        var ids = _sessions.Keys.ToList();
        foreach (var id in ids)
            await StopAsync(id, ct).ConfigureAwait(false);
    }

    public async Task RetryAsync(Guid ruleId, string profile, string region, string instanceId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(ruleId, out var live))
            return;
        var rule = live.Session.Rule;
        await StopAsync(ruleId, ct).ConfigureAwait(false);
        await StartAsync(profile, region, instanceId, rule, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void RaiseChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);
    private void RaiseActivity(string message)
    {
        logger.LogInformation("{Message}", message);
        Activity?.Invoke(this, message);
    }

    private sealed class LiveSession(SsmSession session)
    {
        public SsmSession Session { get; } = session;
        public ManagedProcess? Process { get; set; }
        public ConcurrentQueue<string> RecentOutput { get; } = new();
        public ConcurrentQueue<string> RecentErrors { get; } = new();
    }
}

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(AppSettings settings, CancellationToken ct);
    IReadOnlyList<PortForwardRule> ToRules(AppSettings settings);
    void FromRules(AppSettings settings, IEnumerable<PortForwardRule> rules);
}

public sealed class SettingsService(IFileSettingsRepository repository) : ISettingsService
{
    public Task<AppSettings> LoadAsync(CancellationToken ct) => repository.LoadAsync(ct);
    public Task SaveAsync(AppSettings settings, CancellationToken ct) => repository.SaveAsync(settings, ct);

    public IReadOnlyList<PortForwardRule> ToRules(AppSettings settings) =>
        settings.Rules.Select(r => new PortForwardRule
        {
            Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
            Name = r.Name,
            Mode = Enum.TryParse<PortForwardMode>(r.Mode, true, out var mode) ? mode : PortForwardMode.RemoteHost,
            RemoteHost = r.RemoteHost,
            RemotePort = r.RemotePort,
            LocalPort = r.LocalPort,
            Enabled = r.Enabled
        }).ToList();

    public void FromRules(AppSettings settings, IEnumerable<PortForwardRule> rules)
    {
        settings.Rules = rules.Select(r => new PortForwardRuleSettings
        {
            Id = r.Id,
            Name = r.Name,
            Mode = r.Mode.ToString(),
            RemoteHost = r.RemoteHost,
            RemotePort = r.RemotePort,
            LocalPort = r.LocalPort,
            Enabled = r.Enabled
        }).ToList();
    }
}

public static class DiagnosticsBuilder
{
    public static string Build(
        PrerequisiteStatus prereqs,
        string? profile,
        string? region,
        string? instanceId,
        IEnumerable<PortForwardRule> rules,
        IEnumerable<string> recentActivity)
    {
        var lines = new List<string>
        {
            "AWS Session Launcher diagnostics",
            $"UTC: {DateTimeOffset.UtcNow:O}",
            $"AWS CLI: {prereqs.AwsCliVersion} ({(prereqs.AwsCliReady ? "ready" : "missing")})",
            $"Session Manager: {prereqs.SessionManagerVersion} ({(prereqs.SessionManagerReady ? "ready" : "missing")})",
            $"Profile: {profile ?? "(none)"}",
            $"Region: {region ?? "(none)"}",
            $"Instance: {instanceId ?? "(none)"}",
            "Rules:"
        };
        foreach (var rule in rules)
        {
            lines.Add($"  - {rule.Name}: {rule.Mode} local={rule.LocalPort} remote={rule.RemotePort} host={rule.RemoteHost} enabled={rule.Enabled}");
        }
        lines.Add("Recent activity:");
        foreach (var a in recentActivity.TakeLast(40))
            lines.Add($"  {a}");
        return string.Join(Environment.NewLine, lines);
    }
}
