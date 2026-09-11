using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using AwsSessionLauncher.Domain;

namespace AwsSessionLauncher.Infrastructure;

public interface IFileSettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(AppSettings settings, CancellationToken ct);
}

public sealed class FileSettingsRepository : IFileSettingsRepository
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileSettingsRepository()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AwsSessionLauncher");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return new AppSettings();

        await using var stream = File.OpenRead(_path);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct)
            .ConfigureAwait(false);
        return settings ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct).ConfigureAwait(false);
        }
        File.Copy(temp, _path, overwrite: true);
        File.Delete(temp);
    }
}

public interface IPortInspector
{
    bool IsLocalPortAvailable(int port);
}

public sealed class WindowsPortInspector : IPortInspector
{
    public bool IsLocalPortAvailable(int port)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        return listeners.All(ep => ep.Port != port);
    }
}

public sealed class PrerequisiteStatus
{
    public bool AwsCliReady { get; init; }
    public string AwsCliVersion { get; init; } = "not found";
    public string? AwsCliPath { get; init; }
    public bool SessionManagerReady { get; init; }
    public string SessionManagerVersion { get; init; } = "not found";
    public string? SessionManagerPath { get; init; }
}

public interface IPrerequisiteDetector
{
    Task<PrerequisiteStatus> DetectAsync(CancellationToken ct);
    string? ResolveAwsCliPath();
}

public sealed class PrerequisiteDetector(IProcessRunner processRunner) : IPrerequisiteDetector
{
    public string? ResolveAwsCliPath()
    {
        var fromPath = FindOnPath("aws.exe");
        if (fromPath is not null) return fromPath;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon", "AWSCLIV2", "aws.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Amazon", "AWSCLIV2", "aws.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "Scripts", "aws.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<PrerequisiteStatus> DetectAsync(CancellationToken ct)
    {
        var awsPath = ResolveAwsCliPath();
        string awsVersion = "not found";
        var awsReady = false;
        if (awsPath is not null)
        {
            try
            {
                var result = await processRunner.RunAsync(awsPath, ["--version"], ct, TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);
                awsVersion = (result.StdOut + " " + result.StdErr).Trim();
                awsReady = result.ExitCode == 0 && awsVersion.Contains("aws-cli", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                awsReady = false;
            }
        }

        var pluginPath = ResolveSessionManagerPluginPath();
        string pluginVersion = "not found";
        var pluginReady = false;
        if (pluginPath is not null)
        {
            try
            {
                var result = await processRunner.RunAsync(pluginPath, ["--version"], ct, TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                pluginVersion = (result.StdOut + " " + result.StdErr).Trim();
                if (string.IsNullOrWhiteSpace(pluginVersion))
                    pluginVersion = "installed";
                pluginReady = true;
            }
            catch
            {
                pluginReady = File.Exists(pluginPath);
                pluginVersion = pluginReady ? "installed" : "not found";
            }
        }

        return new PrerequisiteStatus
        {
            AwsCliReady = awsReady,
            AwsCliVersion = awsVersion,
            AwsCliPath = awsPath,
            SessionManagerReady = pluginReady,
            SessionManagerVersion = pluginVersion,
            SessionManagerPath = pluginPath
        };
    }

    private static string? ResolveSessionManagerPluginPath()
    {
        var fromPath = FindOnPath("session-manager-plugin.exe");
        if (fromPath is not null) return fromPath;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon", "SessionManagerPlugin", "bin", "session-manager-plugin.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Amazon", "SessionManagerPlugin", "bin", "session-manager-plugin.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* ignore bad PATH entries */ }
        }
        return null;
    }
}
