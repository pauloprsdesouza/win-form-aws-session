using System.Text.Json;
using System.Text.RegularExpressions;
using AwsSessionLauncher.Domain;
using AwsSessionLauncher.Domain.Enums;
using AwsSessionLauncher.Infrastructure;
using Microsoft.Extensions.Logging;

namespace AwsSessionLauncher.Application;

public interface IAwsCli
{
    Task<AwsCliVersion> GetVersionAsync(CancellationToken ct);

    Task<AwsIdentity> GetCallerIdentityAsync(string profile, string? region, CancellationToken ct);

    Task SsoLoginAsync(string profile, CancellationToken ct);

    Task<IReadOnlyList<Ec2Target>> DescribeInstancesAsync(
        string profile, string region, TargetFilter filter, CancellationToken ct);

    Task<IReadOnlyDictionary<string, SsmTargetStatus>> GetSsmStatusesAsync(
        string profile, string region, IReadOnlyCollection<string> instanceIds, CancellationToken ct);

    Task<ManagedProcess> StartPortForwardAsync(StartPortForwardRequest request, CancellationToken ct);

    IReadOnlyList<string> BuildStartSessionArguments(StartPortForwardRequest request);
}

public sealed class AwsCli(
    IProcessRunner processRunner,
    IPrerequisiteDetector prerequisites,
    ILogger<AwsCli> logger) : IAwsCli
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    private string AwsPath =>
        prerequisites.ResolveAwsCliPath()
        ?? throw new AppErrorException(
            AppErrorCategory.PrerequisiteMissing,
            "AWS CLI was not found.",
            "aws.exe is not on PATH and was not found in standard install locations.",
            "Install AWS CLI v2, then restart the application.");

    public async Task<AwsCliVersion> GetVersionAsync(CancellationToken ct)
    {
        var result = await processRunner.RunAsync(AwsPath, ["--version"], ct, ShortTimeout).ConfigureAwait(false);
        var raw = (result.StdOut + " " + result.StdErr).Trim();
        var major = 0;
        var m = Regex.Match(raw, @"aws-cli/(\d+)");
        if (m.Success) int.TryParse(m.Groups[1].Value, out major);
        return new AwsCliVersion(raw, major);
    }

    public async Task<AwsIdentity> GetCallerIdentityAsync(string profile, string? region, CancellationToken ct)
    {
        var args = new List<string> { "sts", "get-caller-identity", "--profile", profile, "--output", "json", "--no-cli-pager" };
        if (!string.IsNullOrWhiteSpace(region))
        {
            args.Add("--region");
            args.Add(region);
        }

        var result = await processRunner.RunAsync(AwsPath, args, ct, ShortTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw MapCliFailure("get-caller-identity", result, profile);

        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement;
        return new AwsIdentity
        {
            AccountId = root.GetProperty("Account").GetString() ?? "",
            Arn = root.GetProperty("Arn").GetString() ?? "",
            UserId = root.GetProperty("UserId").GetString() ?? ""
        };
    }

    public async Task SsoLoginAsync(string profile, CancellationToken ct)
    {
        logger.LogInformation("Starting SSO login for profile {Profile}", profile);
        var result = await processRunner.RunAsync(
            AwsPath,
            ["sso", "login", "--profile", profile, "--no-cli-pager"],
            ct,
            LoginTimeout).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new AppErrorException(
                AppErrorCategory.AuthenticationFailed,
                $"SSO login failed for profile '{profile}'.",
                "The browser login did not complete successfully.",
                "Retry Sign in and complete the browser authorization flow.",
                Sanitize(result.StdErr));
        }
    }

    public async Task<IReadOnlyList<Ec2Target>> DescribeInstancesAsync(
        string profile, string region, TargetFilter filter, CancellationToken ct)
    {
        var args = new List<string>
        {
            "ec2", "describe-instances",
            "--profile", profile,
            "--region", region,
            "--output", "json",
            "--no-cli-pager",
            "--filters"
        };

        if (filter.RequireRunning)
            args.Add("Name=instance-state-name,Values=running");

        if (!string.IsNullOrWhiteSpace(filter.TagName) && !string.IsNullOrWhiteSpace(filter.TagValue))
            args.Add($"Name=tag:{filter.TagName},Values={filter.TagValue}");

        var result = await processRunner.RunAsync(AwsPath, args, ct, ShortTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw MapCliFailure("describe-instances", result, profile);

        return ParseDescribeInstances(result.StdOut);
    }

    public async Task<IReadOnlyDictionary<string, SsmTargetStatus>> GetSsmStatusesAsync(
        string profile, string region, IReadOnlyCollection<string> instanceIds, CancellationToken ct)
    {
        var map = instanceIds.ToDictionary(id => id, _ => SsmTargetStatus.NotManaged, StringComparer.OrdinalIgnoreCase);
        if (instanceIds.Count == 0) return map;

        var args = new List<string>
        {
            "ssm", "describe-instance-information",
            "--profile", profile,
            "--region", region,
            "--output", "json",
            "--no-cli-pager",
            "--filters",
            $"Key=InstanceIds,Values={string.Join(',', instanceIds)}"
        };

        var result = await processRunner.RunAsync(AwsPath, args, ct, ShortTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw MapCliFailure("describe-instance-information", result, profile);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(result.StdOut) ? "{}" : result.StdOut);
        if (!doc.RootElement.TryGetProperty("InstanceInformationList", out var list))
            return map;

        foreach (var item in list.EnumerateArray())
        {
            var id = item.TryGetProperty("InstanceId", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var ping = item.TryGetProperty("PingStatus", out var pingEl) ? pingEl.GetString() : null;
            map[id] = ping switch
            {
                "Online" => SsmTargetStatus.Online,
                "ConnectionLost" => SsmTargetStatus.ConnectionLost,
                "Inactive" => SsmTargetStatus.Inactive,
                _ => SsmTargetStatus.Unknown
            };
        }

        return map;
    }

    public IReadOnlyList<string> BuildStartSessionArguments(StartPortForwardRequest request)
    {
        var rule = request.Rule;
        var args = new List<string>
        {
            "ssm", "start-session",
            "--profile", request.Profile,
            "--region", request.Region,
            "--target", request.InstanceId,
            "--no-cli-pager"
        };

        if (rule.Mode == PortForwardMode.RemoteHost)
        {
            args.Add("--document-name");
            args.Add("AWS-StartPortForwardingSessionToRemoteHost");
            args.Add("--parameters");
            args.Add($"host={rule.RemoteHost},portNumber={rule.RemotePort},localPortNumber={rule.LocalPort}");
        }
        else
        {
            args.Add("--document-name");
            args.Add("AWS-StartPortForwardingSession");
            args.Add("--parameters");
            args.Add($"portNumber={rule.RemotePort},localPortNumber={rule.LocalPort}");
        }

        return args;
    }

    public Task<ManagedProcess> StartPortForwardAsync(StartPortForwardRequest request, CancellationToken ct)
    {
        var args = BuildStartSessionArguments(request);
        logger.LogInformation(
            "Starting port forward {Rule} local={Local} remote={Remote} target={Target}",
            request.Rule.Name, request.Rule.LocalPort, request.Rule.RemotePort, request.InstanceId);
        return processRunner.StartLongRunningAsync(AwsPath, args, ct);
    }

    public static List<Ec2Target> ParseDescribeInstances(string json)
    {
        var targets = new List<Ec2Target>();
        if (string.IsNullOrWhiteSpace(json)) return targets;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Reservations", out var reservations))
            return targets;

        foreach (var reservation in reservations.EnumerateArray())
        {
            if (!reservation.TryGetProperty("Instances", out var instances)) continue;
            foreach (var instance in instances.EnumerateArray())
            {
                var id = instance.GetProperty("InstanceId").GetString() ?? "";
                string? name = null;
                if (instance.TryGetProperty("Tags", out var tags))
                {
                    foreach (var tag in tags.EnumerateArray())
                    {
                        if (tag.TryGetProperty("Key", out var key) &&
                            key.GetString() == "Name" &&
                            tag.TryGetProperty("Value", out var val))
                        {
                            name = val.GetString();
                            break;
                        }
                    }
                }

                var state = instance.TryGetProperty("State", out var stateObj) &&
                            stateObj.TryGetProperty("Name", out var stateName)
                    ? stateName.GetString() ?? "unknown"
                    : "unknown";

                targets.Add(new Ec2Target
                {
                    InstanceId = id,
                    Name = name,
                    PrivateIpAddress = instance.TryGetProperty("PrivateIpAddress", out var ip) ? ip.GetString() : null,
                    AvailabilityZone = instance.TryGetProperty("Placement", out var placement) &&
                                       placement.TryGetProperty("AvailabilityZone", out var az)
                        ? az.GetString()
                        : null,
                    State = state,
                    SsmStatus = SsmTargetStatus.Unknown
                });
            }
        }

        return targets;
    }

    private static AppErrorException MapCliFailure(string operation, ProcessResult result, string profile)
    {
        var err = Sanitize(result.StdErr + " " + result.StdOut);
        var lower = err.ToLowerInvariant();

        if (lower.Contains("expiredtoken") || lower.Contains("token has expired") ||
            lower.Contains("sso") && (lower.Contains("login") || lower.Contains("refresh")))
        {
            return new AppErrorException(
                AppErrorCategory.AuthenticationRequired,
                $"Authentication required for profile '{profile}'.",
                "The AWS session/token is missing or expired.",
                "Click Sign in for SSO profiles, or refresh credentials for other profile types.",
                err);
        }

        if (lower.Contains("accessdenied") || lower.Contains("unauthorized") || lower.Contains("not authorized"))
        {
            return new AppErrorException(
                AppErrorCategory.AwsPermissionDenied,
                $"AWS denied permission for {operation}.",
                "The selected identity lacks the required IAM permission.",
                "Ask an administrator to grant the needed IAM permissions.",
                err);
        }

        return new AppErrorException(
            AppErrorCategory.Unknown,
            $"AWS CLI {operation} failed.",
            "The AWS CLI returned a non-zero exit code.",
            "Review the technical details and retry.",
            err);
    }

    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Regex.Replace(text, @"(aws_secret_access_key\s*=\s*)\S+", "$1***", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(session[_-]?token\s*[:=]\s*)\S+", "$1***", RegexOptions.IgnoreCase);
        return text.Trim();
    }
}
