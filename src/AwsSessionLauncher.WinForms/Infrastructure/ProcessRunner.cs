using System.Diagnostics;

namespace AwsSessionLauncher.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public sealed class ManagedProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetimeCts = new();

    public ManagedProcess(Process process)
    {
        _process = process;
        _ = Task.Run(DrainStreamsAsync);
    }

    public int Id => _process.Id;
    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch { return true; }
        }
    }

    public int? ExitCode => HasExited ? _process.ExitCode : null;
    public event EventHandler? Exited;
    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;

    private async Task DrainStreamsAsync()
    {
        var stdout = ReadAsync(_process.StandardOutput, line => OutputReceived?.Invoke(this, line));
        var stderr = ReadAsync(_process.StandardError, line => ErrorReceived?.Invoke(this, line));
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        try { await _process.WaitForExitAsync(_lifetimeCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
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
            _process.CloseMainWindow();
            using var cts = new CancellationTokenSource(gracefulTimeout);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { /* best effort */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _process.Dispose();
    }
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null);

    Task<ManagedProcess> StartLongRunningAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var process = CreateProcess(executable, arguments);
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {executable}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
            linked.CancelAfter(timeout.Value);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Process timed out: {executable}");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    public Task<ManagedProcess> StartLongRunningAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = CreateProcess(executable, arguments);
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {executable}");
        return Task.FromResult(new ManagedProcess(process));
    }

    private static Process CreateProcess(string executable, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        return new Process { StartInfo = psi, EnableRaisingEvents = true };
    }
}
