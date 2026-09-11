using System.Globalization;
using System.Text.RegularExpressions;
using AwsSsmPortForwarder.Core.Models;

namespace AwsSsmPortForwarder.Core.Services;

public sealed record PortParseResult(IReadOnlyList<PortMapping> Mappings, IReadOnlyList<string> Errors, bool RequiresConfirmation);

public static class PortParser
{
    private static readonly Regex TokenSplit = new(@"[\s,;]+", RegexOptions.Compiled);

    public static PortParseResult Parse(string? text)
    {
        var errors = new List<string>();
        var mappings = new List<PortMapping>();
        if (string.IsNullOrWhiteSpace(text))
            return new PortParseResult([], ["Enter at least one port."], false);

        var tokens = TokenSplit.Split(text.Trim()).Where(t => t.Length > 0);
        foreach (var token in tokens)
        {
            var parts = token.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var remote) ||
                remote is < 1 or > 65535)
            {
                errors.Add($"Invalid port entry '{token}'.");
                continue;
            }

            int local = remote;
            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out local) ||
                    local is < 1 or > 65535)
                {
                    errors.Add($"Invalid local port in '{token}'.");
                    continue;
                }
            }

            mappings.Add(new PortMapping(remote, local));
        }

        // Deduplicate identical mappings
        mappings = mappings.Distinct().ToList();

        var dupLocal = mappings.GroupBy(m => m.LocalPort).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var port in dupLocal)
            errors.Add($"Duplicate local port {port}.");

        if (dupLocal.Count > 0)
            mappings = mappings.Where(m => !dupLocal.Contains(m.LocalPort)).ToList();

        var needsConfirm = mappings.Count > 20;
        if (errors.Count > 0 && mappings.Count == 0)
            return new PortParseResult([], errors, false);

        return new PortParseResult(mappings, errors, needsConfirm);
    }
}

public interface ILocalPortChecker
{
    bool IsAvailable(int port);
}

public sealed class LocalPortChecker : ILocalPortChecker
{
    public bool IsAvailable(int port)
    {
        var listeners = System.Net.NetworkInformation.IPGlobalProperties
            .GetIPGlobalProperties().GetActiveTcpListeners();
        if (listeners.Any(ep => ep.Port == port))
            return false;

        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> WaitUntilListeningAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var listeners = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties().GetActiveTcpListeners();
            if (listeners.Any(ep => ep.Port == port &&
                (ep.Address.Equals(System.Net.IPAddress.Loopback) ||
                 ep.Address.Equals(System.Net.IPAddress.Any) ||
                 ep.Address.Equals(System.Net.IPAddress.IPv6Loopback) ||
                 ep.Address.Equals(System.Net.IPAddress.IPv6Any))))
            {
                return true;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }
}

public static class AwsFolderFactory
{
    public static AwsFolderContext FromFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new AppException(ErrorKind.InvalidFolder,
                "Select a folder containing config or credentials.",
                "Browse to an AWS configuration folder.");

        var full = Path.GetFullPath(folderPath.Trim());
        if (!Directory.Exists(full))
            throw new AppException(ErrorKind.InvalidFolder,
                "Select a folder containing config or credentials.",
                "Browse to an existing folder.");

        var config = Path.Combine(full, "config");
        var creds = Path.Combine(full, "credentials");
        var ctx = new AwsFolderContext(full, config, creds);
        if (!ctx.IsValid)
            throw new AppException(ErrorKind.InvalidFolder,
                "Select a folder containing config or credentials.",
                "Browse");
        return ctx;
    }

    public static string? SuggestDefaultFolder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws");
        if (!Directory.Exists(path)) return null;
        if (File.Exists(Path.Combine(path, "config")) || File.Exists(Path.Combine(path, "credentials")))
            return path;
        return null;
    }

    public static Dictionary<string, string> BuildChildEnvironment(AwsFolderContext folder) => new()
    {
        ["AWS_CONFIG_FILE"] = folder.ConfigFilePath ?? Path.Combine(folder.FolderPath, "config"),
        ["AWS_SHARED_CREDENTIALS_FILE"] = folder.CredentialsFilePath ?? Path.Combine(folder.FolderPath, "credentials")
    };
}

public static class SsoProfileClassifier
{
    /// <summary>Classifies SSO without loading secret values.</summary>
    public static bool IsSsoProfile(string? configContent, string profileName)
    {
        if (string.IsNullOrWhiteSpace(configContent)) return false;
        var sectionNames = new[] { $"[profile {profileName}]", profileName.Equals("default", StringComparison.OrdinalIgnoreCase) ? "[default]" : null }
            .Where(s => s is not null)
            .Cast<string>()
            .ToArray();

        using var reader = new StringReader(configContent);
        string? line;
        string? current = null;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line;
                continue;
            }
            if (current is null) continue;
            if (!sectionNames.Any(s => s.Equals(current, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (line.StartsWith("sso_session", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("sso_start_url", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
