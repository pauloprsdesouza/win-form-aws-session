using System.Globalization;
using System.Text.Json;
using AwsSsmPortForwarder.Core.Models;
using AwsSsmPortForwarder.Core.Services;

namespace AwsSsmPortForwarder.Infrastructure.AwsCli;

public static class AwsCliArgumentFactory
{
    public static IReadOnlyList<string> ListProfiles() =>
        ["configure", "list-profiles"];

    public static IReadOnlyList<string> GetCallerIdentity(string profile) =>
        ["sts", "get-caller-identity", "--profile", profile, "--output", "json", "--no-cli-pager"];

    public static IReadOnlyList<string> SsoLogin(string profile, SsoLoginMode mode) =>
        mode == SsoLoginMode.DeviceCode
            ? ["sso", "login", "--profile", profile, "--use-device-code", "--no-browser", "--no-cli-pager"]
            : ["sso", "login", "--profile", profile, "--no-cli-pager"];

    public static IReadOnlyList<string> GetRegion(string profile) =>
        ["configure", "get", "region", "--profile", profile];

    public static IReadOnlyList<string> DescribeInstances(string profile, string region, TargetOptions options)
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
        args.Add($"Name=tag:{options.TagKey},Values={options.TagValue}");
        if (options.RequireRunning)
            args.Add("Name=instance-state-name,Values=running");
        return args;
    }

    public static IReadOnlyList<string> DescribeInstanceInformation(string profile, string region, IReadOnlyCollection<string> ids) =>
    [
        "ssm", "describe-instance-information",
        "--profile", profile,
        "--region", region,
        "--output", "json",
        "--no-cli-pager",
        "--filters",
        $"Key=InstanceIds,Values={string.Join(',', ids)}"
    ];

    public static IReadOnlyList<string> StartPortForward(StartPortForwardRequest request)
    {
        var document = SsmDocumentNameResolver.Resolve(request.Rule.Type);
        var parameters = SsmParameterSerializer.Serialize(request.Rule);

        return
        [
            "ssm", "start-session",
            "--profile", request.Context.ProfileName,
            "--region", request.Context.Region!,
            "--target", request.InstanceId,
            "--document-name", document,
            "--parameters", parameters,
            "--no-cli-pager"
        ];
    }

    public static IReadOnlyList<string> TerminateSession(string profile, string region, string sessionId) =>
    [
        "ssm", "terminate-session",
        "--profile", profile,
        "--region", region,
        "--session-id", sessionId,
        "--no-cli-pager"
    ];
}

public static class AwsCliErrorMapper
{
    public static AppException Map(string operation, int exitCode, string stderr, string stdout)
    {
        var text = Sanitize((stderr + " " + stdout).Trim());
        var lower = text.ToLowerInvariant();

        if (lower.Contains("expired") || lower.Contains("sso") && (lower.Contains("login") || lower.Contains("token")))
            return new AppException(ErrorKind.SsoExpired, "Sign-in approval is required.", "Sign in", text);

        if (lower.Contains("cancelled") || lower.Contains("canceled") || lower.Contains("access denied by user"))
            return new AppException(ErrorKind.SsoCancelled, "Sign-in was cancelled.", "Retry", text);

        if (operation.Contains("get-caller-identity", StringComparison.OrdinalIgnoreCase) ||
            lower.Contains("unable to locate credentials") ||
            lower.Contains("invalidclienttokenid") ||
            lower.Contains("signaturedoesnotmatch"))
            return new AppException(ErrorKind.CredentialsInvalid,
                "Credentials are missing, invalid, or expired.",
                "Update AWS files / Reload", text);

        if (lower.Contains("accessdenied") || lower.Contains("unauthorizedoperation") || lower.Contains("not authorized"))
        {
            if (operation.Contains("describe-instances", StringComparison.OrdinalIgnoreCase))
                return new AppException(ErrorKind.Ec2Denied, "This profile cannot discover EC2 instances.", "Show permission", text);
            if (operation.Contains("start-session", StringComparison.OrdinalIgnoreCase))
                return new AppException(ErrorKind.SsmDenied, "This profile cannot start an SSM session.", "Show permission", text);
        }

        if (lower.Contains("address already in use") || lower.Contains("port is already") || lower.Contains("bind"))
            return new AppException(ErrorKind.LocalPortOccupied, "Local port is already in use.", "Change / Retry", text);

        if (lower.Contains("targetnotconnected") || lower.Contains("unsupported document") ||
            lower.Contains("document does not exist") || lower.Contains("ssm agent"))
            return new AppException(ErrorKind.SsmAgentIncompatible,
                "The bastion's SSM Agent does not support this forwarding type.",
                "Ask the AWS administrator to update the agent", text);

        if (lower.Contains("could not resolve") || lower.Contains("no such host") ||
            lower.Contains("connection timed out") || lower.Contains("connection refused") ||
            lower.Contains("name or service not known"))
            return new AppException(ErrorKind.RemoteHostUnreachable,
                "The bastion could not resolve or reach the remote host and port.",
                "Verify DNS, routes, security groups, NACLs, and service availability", text);

        return new AppException(ErrorKind.Unknown,
            $"AWS CLI {operation} failed (exit {exitCode}).",
            "Retry",
            text);
    }

    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(aws_secret_access_key\s*=\s*)\S+", "$1***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(aws_session_token\s*=\s*)\S+", "$1***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(authorization[=:]\s*)\S+", "$1***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text.Trim();
    }
}

public static class AwsJsonParsers
{
    public static List<AwsTarget> ParseInstances(string json)
    {
        var list = new List<AwsTarget>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Reservations", out var reservations)) return list;

        foreach (var reservation in reservations.EnumerateArray())
        {
            if (!reservation.TryGetProperty("Instances", out var instances)) continue;
            foreach (var instance in instances.EnumerateArray())
            {
                var id = instance.GetProperty("InstanceId").GetString() ?? "";
                var name = "";
                if (instance.TryGetProperty("Tags", out var tags))
                {
                    foreach (var tag in tags.EnumerateArray())
                    {
                        if (tag.TryGetProperty("Key", out var key) && key.GetString() == "Name" &&
                            tag.TryGetProperty("Value", out var val))
                        {
                            name = val.GetString() ?? "";
                            break;
                        }
                    }
                }

                var state = instance.TryGetProperty("State", out var st) && st.TryGetProperty("Name", out var sn)
                    ? sn.GetString() ?? "unknown" : "unknown";
                var az = instance.TryGetProperty("Placement", out var pl) && pl.TryGetProperty("AvailabilityZone", out var azEl)
                    ? azEl.GetString() ?? "" : "";
                var ip = instance.TryGetProperty("PrivateIpAddress", out var ipEl) ? ipEl.GetString() : null;

                list.Add(new AwsTarget(id, name, ip, az, state, nameof(SsmNodeStatus.Unknown)));
            }
        }
        return list;
    }

    public static Dictionary<string, SsmNodeStatus> ParseSsm(string json, IReadOnlyCollection<string> ids)
    {
        var map = ids.ToDictionary(id => id, _ => SsmNodeStatus.NotManaged, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return map;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("InstanceInformationList", out var list)) return map;
        foreach (var item in list.EnumerateArray())
        {
            var id = item.TryGetProperty("InstanceId", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var ping = item.TryGetProperty("PingStatus", out var p) ? p.GetString() : null;
            map[id] = ping switch
            {
                "Online" => SsmNodeStatus.Online,
                "ConnectionLost" => SsmNodeStatus.ConnectionLost,
                "Inactive" => SsmNodeStatus.Inactive,
                _ => SsmNodeStatus.Unknown
            };
        }
        return map;
    }

    public static AwsIdentity ParseIdentity(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new AwsIdentity(
            root.GetProperty("Account").GetString() ?? "",
            root.GetProperty("Arn").GetString() ?? "",
            root.GetProperty("UserId").GetString() ?? "");
    }
}
