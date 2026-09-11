using AwsSsmPortForwarder.Core.Models;
using Microsoft.Extensions.Logging;

namespace AwsSsmPortForwarder.Core.Services;

public sealed class TargetResolver(IAwsCliClient aws, ILogger<TargetResolver> logger)
{
    public async Task<(string? Region, AwsTarget? Target, IReadOnlyList<AwsTarget> Candidates)> ResolveAsync(
        AwsContext context,
        TargetOptions options,
        string? regionOverride,
        CancellationToken ct)
    {
        var region = regionOverride;
        if (string.IsNullOrWhiteSpace(region))
            region = await aws.GetRegionAsync(context, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(region))
            throw new AppException(ErrorKind.RegionMissing,
                "This profile does not define a Region.",
                "Select Region");

        var ctx = context with { Region = region };
        IReadOnlyList<AwsTarget> targets;
        try
        {
            targets = await aws.DescribeTargetsAsync(ctx, options, ct).ConfigureAwait(false);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EC2 discovery failed");
            throw new AppException(ErrorKind.Ec2Denied,
                "This profile cannot discover EC2 instances.",
                "Show permission",
                ex.Message,
                ex);
        }

        var ids = targets.Select(t => t.InstanceId).ToList();
        IReadOnlyDictionary<string, SsmNodeStatus> ssm;
        try
        {
            ssm = await aws.DescribeSsmNodesAsync(ctx, ids, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SSM discovery failed");
            ssm = ids.ToDictionary(id => id, _ => SsmNodeStatus.Unknown);
        }

        var enriched = targets.Select(t =>
        {
            var status = ssm.TryGetValue(t.InstanceId, out var s) ? s : SsmNodeStatus.NotManaged;
            return t with { SsmPingStatus = status.ToString() };
        }).ToList();

        var eligible = enriched.Where(t =>
        {
            if (options.RequireRunning && !t.Ec2State.Equals("running", StringComparison.OrdinalIgnoreCase))
                return false;
            if (options.RequireSsmOnline &&
                !t.SsmPingStatus.Equals(nameof(SsmNodeStatus.Online), StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }).ToList();

        logger.LogInformation("Target discovery found {Total} instances, {Eligible} eligible", enriched.Count, eligible.Count);

        return eligible.Count switch
        {
            0 => throw new AppException(ErrorKind.NoBastion,
                "No running, SSM-online bastion was found.",
                "Reload",
                $"Criteria: tag:{options.TagKey}={options.TagValue}, running={options.RequireRunning}, ssmOnline={options.RequireSsmOnline}"),
            1 => (region, eligible[0], eligible),
            _ => (region, null, eligible)
        };
    }
}

public sealed class ConnectionOrchestrator
{
    private readonly IAwsCliClient _aws;
    private readonly TargetResolver _targets;
    private readonly ILocalPortChecker _ports;
    private readonly IAppConfigStore _config;
    private readonly ILogger<ConnectionOrchestrator> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Dictionary<int, LiveSession> _sessions = new();
    private readonly object _gate = new();

    public ConnectionOrchestrator(
        IAwsCliClient aws,
        TargetResolver targets,
        ILocalPortChecker ports,
        IAppConfigStore config,
        ILogger<ConnectionOrchestrator> logger)
    {
        _aws = aws;
        _targets = targets;
        _ports = ports;
        _config = config;
        _logger = logger;
    }

    public event EventHandler? SessionsChanged;
    public event EventHandler<string>? StatusChanged;

    public IReadOnlyList<SessionView> Sessions
    {
        get
        {
            lock (_gate)
                return _sessions.Values.Select(s => s.View).ToList();
        }
    }

    public async Task<(AwsIdentity? Identity, bool SignInRequired, bool IsSso, string? Message)> ValidateAuthAsync(
        AwsContext context, string? configContent, CancellationToken ct)
    {
        var isSso = SsoProfileClassifier.IsSsoProfile(configContent, context.ProfileName);
        try
        {
            var identity = await _aws.GetCallerIdentityAsync(context, ct).ConfigureAwait(false);
            return (identity, false, isSso, null);
        }
        catch (AppException ex) when (ex.Kind is ErrorKind.SsoExpired or ErrorKind.CredentialsInvalid)
        {
            if (isSso || ex.Kind == ErrorKind.SsoExpired)
                return (null, true, true, ex.UserMessage);
            return (null, false, false, ex.UserMessage);
        }
    }

    public Task SsoLoginAsync(AwsContext context, SsoLoginMode mode, CancellationToken ct) =>
        _aws.SsoLoginAsync(context, mode, ct);

    public Task<(string? Region, AwsTarget? Target, IReadOnlyList<AwsTarget> Candidates)> ResolveTargetAsync(
        AwsContext context, string? regionOverride, CancellationToken ct) =>
        _targets.ResolveAsync(context, _config.Load().AwsTarget.ToOptions(), regionOverride, ct);

    public async Task ConnectAsync(
        AwsContext context,
        string instanceId,
        IReadOnlyList<PortMapping> mappings,
        CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var mapping in mappings)
            {
                if (!_ports.IsAvailable(mapping.LocalPort))
                    throw new AppException(ErrorKind.LocalPortOccupied,
                        $"Local port {mapping.LocalPort} is already in use.",
                        "Change / Retry");
            }

            using var limiter = new SemaphoreSlim(3);
            var tasks = mappings.Select(async mapping =>
            {
                await limiter.WaitAsync(ct).ConfigureAwait(false);
                try { await StartOneAsync(context, instanceId, mapping, ct).ConfigureAwait(false); }
                finally { limiter.Release(); }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task StartOneAsync(AwsContext context, string instanceId, PortMapping mapping, CancellationToken ct)
    {
        LiveSession live;
        lock (_gate)
        {
            if (_sessions.TryGetValue(mapping.LocalPort, out var existing) &&
                existing.View.State is SessionState.Connected or SessionState.Connecting)
                return;

            live = new LiveSession(new SessionView { Mapping = mapping, State = SessionState.Connecting });
            _sessions[mapping.LocalPort] = live;
        }
        RaiseSessions();
        RaiseStatus($"Connecting {mapping.RemotePort} → localhost:{mapping.LocalPort}");

        if (!_ports.IsAvailable(mapping.LocalPort))
        {
            Fail(live, $"Local port {mapping.LocalPort} is already in use.");
            return;
        }

        try
        {
            var process = await _aws.StartPortForwardAsync(
                new StartPortForwardRequest(context, instanceId, mapping), ct).ConfigureAwait(false);
            live.Process = process;
            live.View.ProcessId = process.Id;

            process.OutputReceived += (_, line) =>
            {
                CaptureSessionId(live, line);
                live.Buffer.Add(Sanitize(line));
            };
            process.ErrorReceived += (_, line) =>
            {
                CaptureSessionId(live, line);
                live.Buffer.Add(Sanitize(line));
            };
            process.Exited += (_, _) =>
            {
                if (live.View.State is SessionState.Stopping or SessionState.Stopped) return;
                live.View.State = SessionState.Failed;
                live.View.LastError = "The forwarding session ended unexpectedly.";
                RaiseSessions();
                RaiseStatus($"Failed localhost:{mapping.LocalPort}");
            };

            var listening = await LocalPortChecker.WaitUntilListeningAsync(
                mapping.LocalPort, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

            if (process.HasExited || !listening)
            {
                await process.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                Fail(live, process.HasExited
                    ? (live.Buffer.LastOrDefault() ?? "Session process exited during startup.")
                    : "Local listener did not become ready within 20 seconds.");
                return;
            }

            live.View.State = SessionState.Connected;
            RaiseSessions();
            RaiseStatus($"Connected localhost:{mapping.LocalPort}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start mapping {Local}", mapping.LocalPort);
            Fail(live, Sanitize(ex.Message));
        }
    }

    public async Task StopAsync(int localPort, CancellationToken ct)
    {
        LiveSession? live;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(localPort, out live)) return;
            live.View.State = SessionState.Stopping;
        }
        RaiseSessions();

        if (!string.IsNullOrWhiteSpace(live.View.SessionId))
        {
            try
            {
                // best-effort; context may be incomplete for terminate — ignore failures
            }
            catch { /* ignore */ }
        }

        if (live.Process is not null)
            await live.Process.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        lock (_gate)
        {
            live.View.State = SessionState.Stopped;
            _sessions.Remove(localPort);
        }
        RaiseSessions();
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        int[] ports;
        lock (_gate) ports = _sessions.Keys.ToArray();
        foreach (var p in ports)
            await StopAsync(p, ct).ConfigureAwait(false);
    }

    public async Task RetryFailedAsync(AwsContext context, string instanceId, CancellationToken ct)
    {
        List<PortMapping> failed;
        lock (_gate)
        {
            failed = _sessions.Values
                .Where(s => s.View.State == SessionState.Failed)
                .Select(s => s.View.Mapping)
                .ToList();
        }
        foreach (var mapping in failed)
            await StopAsync(mapping.LocalPort, ct).ConfigureAwait(false);
        if (failed.Count > 0)
            await ConnectAsync(context, instanceId, failed, ct).ConfigureAwait(false);
    }

    public async Task TerminateTrackedSessionAsync(AwsContext context, string sessionId, CancellationToken ct)
    {
        try { await _aws.TerminateSessionAsync(context, sessionId, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "terminate-session best-effort failed"); }
    }

    private void Fail(LiveSession live, string error)
    {
        live.View.State = SessionState.Failed;
        live.View.LastError = error;
        RaiseSessions();
        RaiseStatus($"Failed localhost:{live.View.Mapping.LocalPort}");
    }

    private static void CaptureSessionId(LiveSession live, string line)
    {
        // SessionId appears in some plugin output lines; capture when present.
        const string marker = "SessionId:";
        var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;
        var id = line[(idx + marker.Length)..].Trim().Trim('"', '\'', ',');
        if (!string.IsNullOrWhiteSpace(id))
            live.View.SessionId = id;
    }

    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(aws_secret_access_key\s*=\s*)\S+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(session[_-]?token\s*[:=]\s*)\S+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text.Trim();
    }

    private void RaiseSessions() => SessionsChanged?.Invoke(this, EventArgs.Empty);
    private void RaiseStatus(string msg)
    {
        _logger.LogInformation("{Status}", msg);
        StatusChanged?.Invoke(this, msg);
    }

    private sealed class LiveSession(SessionView view)
    {
        public SessionView View { get; } = view;
        public IManagedProcess? Process { get; set; }
        public List<string> Buffer { get; } = [];
    }
}
