using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AwsSsmPortForwarder.Core.Models;

namespace AwsSsmPortForwarder.Core.Services;

public static class PortForwardRuleValidator
{
    private static readonly Regex DnsName = new(
        @"^(?=.{1,253}$)(?!-)[A-Za-z0-9-]{1,63}(?<!-)(\.(?!-)[A-Za-z0-9-]{1,63}(?<!-))*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RuleValidationResult Validate(PortForwardRule rule)
    {
        var errors = new List<string>();

        if (rule.RemotePort is null)
            errors.Add("Remote port is required.");
        else if (rule.RemotePort is < 1 or > 65535)
            errors.Add("Remote port must be between 1 and 65535.");

        if (rule.LocalPort is null)
            errors.Add("Local port is required.");
        else if (rule.LocalPort is < 1 or > 65535)
            errors.Add("Local port must be between 1 and 65535.");

        if (ContainsControlChars(rule.RemoteHost) || ContainsControlChars(rule.Label))
            errors.Add("Fields must not contain control characters.");

        if (rule.Type == PortForwardType.RemoteHost)
        {
            var hostError = ValidateHost(rule.RemoteHost);
            if (hostError is not null)
                errors.Add(hostError);
        }
        else if (!string.IsNullOrWhiteSpace(rule.RemoteHost))
        {
            errors.Add("Managed node rows must not include a remote host.");
        }

        if (errors.Count > 0)
        {
            var incomplete = rule.RemotePort is null || rule.LocalPort is null ||
                             (rule.Type == PortForwardType.RemoteHost && string.IsNullOrWhiteSpace(rule.RemoteHost));
            return new RuleValidationResult(false, incomplete ? SessionState.Incomplete : SessionState.Failed, errors);
        }

        return new RuleValidationResult(true, SessionState.Ready, []);
    }

    public static RuleValidationResult ValidateEnabledSet(IReadOnlyList<PortForwardRule> rules)
    {
        var enabled = rules.Where(r => r.Enabled).ToList();
        var allErrors = new List<string>();
        foreach (var rule in enabled)
        {
            var result = Validate(rule);
            if (!result.IsValid)
                allErrors.AddRange(result.Errors.Select(e => $"{Display(rule)}: {e}"));
        }

        var dup = enabled
            .Where(r => r.LocalPort is not null)
            .GroupBy(r => r.LocalPort!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var port in dup)
            allErrors.Add($"Duplicate local port {port}.");

        if (allErrors.Count > 0)
            return new RuleValidationResult(false, SessionState.Failed, allErrors);

        return new RuleValidationResult(true, SessionState.Ready, []);
    }

    public static bool RequiresConfirmation(IEnumerable<PortForwardRule> rules) =>
        rules.Count(r => r.Enabled) > 20;

    public static string? ValidateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "Enter a hostname or IP without protocol, path, or port.";

        host = host.Trim();
        if (host.Length > 253)
            return "Hostname must be at most 253 characters.";

        if (ContainsControlChars(host))
            return "Enter a hostname or IP without protocol, path, or port.";

        if (host.Contains("://", StringComparison.Ordinal) ||
            host.Contains('/') || host.Contains('?') || host.Contains('#') ||
            host.Contains('@') || host.Contains('\\'))
            return "Enter a hostname or IP without protocol, path, or port.";

        // Reject host:port (but allow IPv6 literals in brackets)
        if (host.Contains(':') && !(host.StartsWith('[') && host.EndsWith(']')))
        {
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                return "Enter a hostname or IP without protocol, path, or port.";
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out _))
            return null;

        if (DnsName.IsMatch(host))
            return null;

        return "Enter a hostname or IP without protocol, path, or port.";
    }

    private static bool ContainsControlChars(string? value) =>
        !string.IsNullOrEmpty(value) && value.Any(ch => char.IsControl(ch));

    private static string Display(PortForwardRule rule) =>
        rule.Label ?? (rule.Type == PortForwardType.RemoteHost
            ? (rule.RemoteHost ?? "Remote host")
            : $"Managed :{rule.RemotePort?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
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
            using var listener = new TcpListener(IPAddress.Loopback, port);
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
                (ep.Address.Equals(IPAddress.Loopback) ||
                 ep.Address.Equals(IPAddress.Any) ||
                 ep.Address.Equals(IPAddress.IPv6Loopback) ||
                 ep.Address.Equals(IPAddress.IPv6Any))))
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
    public static bool IsSsoProfile(string? configContent, string profileName)
    {
        if (string.IsNullOrWhiteSpace(configContent)) return false;
        var sectionNames = new[]
            {
                $"[profile {profileName}]",
                profileName.Equals("default", StringComparison.OrdinalIgnoreCase) ? "[default]" : null
            }
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

public static class ConnectionSettingsMapper
{
    public static PortForwardRule ToRule(SavedConnection saved)
    {
        var type = Enum.TryParse<PortForwardType>(saved.Type, true, out var parsed)
            ? parsed
            : PortForwardType.ManagedNode;
        return new PortForwardRule(
            Guid.NewGuid(),
            saved.Enabled,
            type,
            type == PortForwardType.RemoteHost ? saved.RemoteHost : null,
            saved.RemotePort,
            saved.LocalPort,
            saved.Label);
    }

    public static SavedConnection ToSaved(PortForwardRule rule) => new()
    {
        Type = rule.Type.ToString(),
        RemoteHost = rule.Type == PortForwardType.RemoteHost ? rule.RemoteHost : null,
        RemotePort = rule.RemotePort,
        LocalPort = rule.LocalPort,
        Enabled = rule.Enabled,
        Label = rule.Label
    };
}
