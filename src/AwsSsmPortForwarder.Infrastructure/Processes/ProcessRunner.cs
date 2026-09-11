using System.Diagnostics;
using System.Text;
using AwsSsmPortForwarder.Core.Services;

namespace AwsSsmPortForwarder.Infrastructure.Processes;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct,
        TimeSpan? timeout = null);

    Task<IManagedProcess> StartLongRunningAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        using var process = Create(executable, arguments, environment);
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {executable}");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) linked.CancelAfter(timeout.Value);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Timed out: {executable}");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public Task<IManagedProcess> StartLongRunningAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var process = Create(executable, arguments, environment);
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {executable}");
        return Task.FromResult<IManagedProcess>(new ManagedProcess(process));
    }

    internal static Process Create(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;
        }
        return new Process { StartInfo = psi, EnableRaisingEvents = true };
    }
}

public sealed class ManagedProcess : IManagedProcess
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();

    public ManagedProcess(Process process)
    {
        _process = process;
        _ = Task.Run(DrainAsync);
    }

    public int Id => _process.Id;
    public bool HasExited
    {
        get { try { return _process.HasExited; } catch { return true; } }
    }
    public int? ExitCode => HasExited ? _process.ExitCode : null;

    public event EventHandler? Exited;
    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;

    private async Task DrainAsync()
    {
        var outTask = ReadAsync(_process.StandardOutput, line => OutputReceived?.Invoke(this, line));
        var errTask = ReadAsync(_process.StandardError, line => ErrorReceived?.Invoke(this, line));
        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        try { await _process.WaitForExitAsync(_cts.Token).ConfigureAwait(false); } catch { }
        Exited?.Invoke(this, EventArgs.Empty);
    }

    private static async Task ReadAsync(StreamReader reader, Action<string> onLine)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;
            onLine(line);
        }
    }

    public async Task StopAsync(TimeSpan gracefulTimeout)
    {
        if (HasExited) return;
        try
        {
            _process.Kill(entireProcessTree: true);
            using var cts = new CancellationTokenSource(gracefulTimeout);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _cts.Cancel();
        _cts.Dispose();
        _process.Dispose();
    }
}
