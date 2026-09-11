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
    public const int DefaultMaxRetries = 3;

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
    public event EventHandler<ProgressUpdate>? ProgressChanged;

    public IReadOnlyList<SessionView> Sessions
    {
        get
        {
            lock (_gate)
                return _sessions.Values.Select(s => CloneView(s.View)).ToList();
        }
    }

    public async Task<(AwsIdentity? Identity, bool SignInRequired, bool IsSso, string? Message)> ValidateAuthAsync(
        AwsContext context, string? configContent, CancellationToken ct)
    {
        RaiseProgress("Validating authentication…", true);
        var isSso = SsoProfileClassifier.IsSsoProfile(configContent, context.ProfileName);
        try
        {
            var identity = await _aws.GetCallerIdentityAsync(context, ct).ConfigureAwait(false);
            RaiseProgress("Authentication valid", false, 100);
            return (identity, false, isSso, null);
        }
        catch (AppException ex) when (ex.Kind is ErrorKind.SsoExpired or ErrorKind.CredentialsInvalid)
        {
            RaiseProgress(ex.UserMessage, false);
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

            RaiseProgress($"Starting {mappings.Count} tunnel(s)…", true, 0);
            using var limiter = new SemaphoreSlim(3);
            var completed = 0;
            var total = mappings.Count;
            var tasks = mappings.Select(async mapping =>
            {
                await limiter.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await StartOneAsync(context, instanceId, mapping, isRetry: false, ct).ConfigureAwait(false);
                }
                finally
                {
                    limiter.Release();
                    var done = Interlocked.Increment(ref completed);
                    RaiseProgress($"Connected {done}/{total} tunnel(s)…", true, (int)(done * 100.0 / total));
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
            RaiseProgress("All tunnels processed", false, 100);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task StartOneAsync(
        AwsContext context,
        string instanceId,
        PortMapping mapping,
        bool isRetry,
        CancellationToken ct)
    {
        LiveSession live;
        lock (_gate)
        {
            if (_sessions.TryGetValue(mapping.LocalPort, out var existing))
            {
                if (!isRetry && existing.View.State is SessionState.Connected or SessionState.Connecting or SessionState.Reconnecting)
                    return;
                live = existing;
                live.Context = context;
                live.InstanceId = instanceId;
                live.StopRequested = false;
                live.View.State = isRetry ? SessionState.Reconnecting : SessionState.Connecting;
                live.View.LastError = null;
                live.View.SessionId = null;
            }
            else
            {
                live = new LiveSession(new SessionView
                {
                    Mapping = mapping,
                    State = SessionState.Connecting,
                    MaxRetries = DefaultMaxRetries
                })
                {
                    Context = context,
                    InstanceId = instanceId
                };
                _sessions[mapping.LocalPort] = live;
            }
        }

        RaiseSessions();
        var attemptLabel = live.View.RetryAttempt > 0
            ? $" (retry {live.View.RetryAttempt}/{live.View.MaxRetries})"
            : "";
        RaiseStatus($"Connecting {mapping.RemotePort} → localhost:{mapping.LocalPort}{attemptLabel}");
        RaiseProgress($"Connecting localhost:{mapping.LocalPort}{attemptLabel}", true);

        // On retry the previous process should already be gone; port may still be freeing up.
        var portReady = await WaitForLocalPortFreeAsync(mapping.LocalPort, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (!portReady)
        {
            await HandleStartFailureAsync(live, $"Local port {mapping.LocalPort} is already in use.", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var process = await _aws.StartPortForwardAsync(
                new StartPortForwardRequest(context, instanceId, mapping), ct).ConfigureAwait(false);

            lock (_gate)
            {
                live.Process = process;
                live.View.ProcessId = process.Id;
                live.Buffer.Clear();
            }

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
                // Only auto-retry unexpected drops after the tunnel was established.
                if (live.View.State != SessionState.Connected) return;
                OnProcessExited(live);
            };

            var listening = await LocalPortChecker.WaitUntilListeningAsync(
                mapping.LocalPort, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

            if (live.StopRequested)
                return;

            if (process.HasExited || !listening)
            {
                await process.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await HandleStartFailureAsync(live,
                    process.HasExited
                        ? (live.Buffer.LastOrDefault() ?? "Session process exited during startup.")
                        : "Local listener did not become ready within 20 seconds.",
                    ct).ConfigureAwait(false);
                return;
            }

            lock (_gate)
            {
                live.View.State = SessionState.Connected;
                live.View.LastError = null;
                // Successful connect resets the consecutive failure budget for future drops.
                live.View.RetryAttempt = 0;
            }
            RaiseSessions();
            RaiseStatus($"Connected localhost:{mapping.LocalPort}");
            RaiseProgress($"Connected localhost:{mapping.LocalPort}", false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start mapping {Local}", mapping.LocalPort);
            await HandleStartFailureAsync(live, Sanitize(ex.Message), ct).ConfigureAwait(false);
        }
    }

    private void OnProcessExited(LiveSession live)
    {
        if (live.StopRequested || live.View.State is SessionState.Stopping or SessionState.Stopped)
            return;

        // Unexpected drop while connected (or mid-reconnect) → retry policy
        _ = Task.Run(async () =>
        {
            try
            {
                await ScheduleReconnectAsync(live).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnect scheduling failed for port {Port}", live.View.Mapping.LocalPort);
            }
        });
    }

    private async Task ScheduleReconnectAsync(LiveSession live)
    {
        if (live.StopRequested) return;
        if (live.Context is null || live.InstanceId is null) return;

        int attempt;
        lock (_gate)
        {
            if (live.StopRequested) return;
            if (live.View.State is SessionState.Stopping or SessionState.Stopped) return;

            live.View.RetryAttempt++;
            attempt = live.View.RetryAttempt;
            if (attempt > live.View.MaxRetries)
            {
                live.View.State = SessionState.Failed;
                live.View.LastError = $"Connection dropped after {live.View.MaxRetries} reconnect attempts.";
                RaiseSessions();
                RaiseStatus($"Failed localhost:{live.View.Mapping.LocalPort} after {live.View.MaxRetries} retries");
                RaiseProgress($"Failed localhost:{live.View.Mapping.LocalPort}", false);
                return;
            }

            live.View.State = SessionState.Reconnecting;
            live.View.LastError = $"Connection dropped. Reconnecting ({attempt}/{live.View.MaxRetries})…";
            live.Process = null;
            live.View.ProcessId = null;
        }

        RaiseSessions();
        RaiseStatus($"Reconnecting localhost:{live.View.Mapping.LocalPort} ({attempt}/{live.View.MaxRetries})");
        RaiseProgress($"Reconnecting localhost:{live.View.Mapping.LocalPort} ({attempt}/{live.View.MaxRetries})", true,
            (int)((attempt - 1) * 100.0 / live.View.MaxRetries));

        var delay = TimeSpan.FromSeconds(2 * attempt);
        try
        {
            await Task.Delay(delay, live.LifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (live.StopRequested) return;

        await StartOneAsync(live.Context!, live.InstanceId!, live.View.Mapping, isRetry: true, live.LifetimeToken)
            .ConfigureAwait(false);
    }

    private async Task HandleStartFailureAsync(LiveSession live, string error, CancellationToken ct)
    {
        if (live.StopRequested) return;

        // Treat start failure the same as a drop when we still have retries left.
        if (live.View.RetryAttempt < live.View.MaxRetries && live.Context is not null && live.InstanceId is not null)
        {
            lock (_gate)
            {
                live.View.LastError = error;
            }
            await ScheduleReconnectAsync(live).ConfigureAwait(false);
            return;
        }

        Fail(live, error);
    }

    public async Task StopAsync(int localPort, CancellationToken ct)
    {
        LiveSession? live;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(localPort, out live)) return;
            live.StopRequested = true;
            live.View.State = SessionState.Stopping;
            live.RetryCts.Cancel();
        }
        RaiseSessions();
        RaiseProgress($"Stopping localhost:{localPort}…", true);

        if (live.Process is not null)
            await live.Process.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        lock (_gate)
        {
            live.View.State = SessionState.Stopped;
            _sessions.Remove(localPort);
            live.RetryCts.Dispose();
        }
        RaiseSessions();
        RaiseProgress($"Stopped localhost:{localPort}", false);
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        int[] ports;
        lock (_gate) ports = _sessions.Keys.ToArray();
        RaiseProgress(ports.Length == 0 ? "No active tunnels" : $"Stopping {ports.Length} tunnel(s)…", ports.Length > 0);
        foreach (var p in ports)
            await StopAsync(p, ct).ConfigureAwait(false);
        RaiseProgress("All tunnels stopped", false, 100);
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
        lock (_gate)
        {
            live.View.State = SessionState.Failed;
            live.View.LastError = error;
        }
        RaiseSessions();
        RaiseStatus($"Failed localhost:{live.View.Mapping.LocalPort}");
        RaiseProgress($"Failed localhost:{live.View.Mapping.LocalPort}", false);
    }

    private static async Task<bool> WaitForLocalPortFreeAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var checker = new LocalPortChecker();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (checker.IsAvailable(port)) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return checker.IsAvailable(port);
    }

    private static SessionView CloneView(SessionView v) => new()
    {
        Mapping = v.Mapping,
        State = v.State,
        SessionId = v.SessionId,
        LastError = v.LastError,
        ProcessId = v.ProcessId,
        RetryAttempt = v.RetryAttempt,
        MaxRetries = v.MaxRetries
    };

    private static void CaptureSessionId(LiveSession live, string line)
    {
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

    private void RaiseProgress(string message, bool isBusy, int? percent = null)
    {
        ProgressChanged?.Invoke(this, new ProgressUpdate
        {
            Message = message,
            IsBusy = isBusy,
            Percent = percent
        });
        StatusChanged?.Invoke(this, message);
    }

    private sealed class LiveSession(SessionView view)
    {
        public SessionView View { get; } = view;
        public IManagedProcess? Process { get; set; }
        public List<string> Buffer { get; } = [];
        public AwsContext? Context { get; set; }
        public string? InstanceId { get; set; }
        public bool StopRequested { get; set; }
        public CancellationTokenSource RetryCts { get; } = new();
        public CancellationToken LifetimeToken => RetryCts.Token;
    }
}
