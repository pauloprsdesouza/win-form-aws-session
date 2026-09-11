using AwsSessionLauncher.Domain.Enums;

namespace AwsSessionLauncher.Domain;

public sealed record AwsProfile
{
    public required string Name { get; init; }
    public string? Region { get; init; }
    public AwsProfileType Type { get; init; }
    public string? SsoSessionName { get; init; }
}

public sealed record AwsIdentity
{
    public required string AccountId { get; init; }
    public required string Arn { get; init; }
    public required string UserId { get; init; }
}

public sealed record Ec2Target
{
    public required string InstanceId { get; init; }
    public string? Name { get; init; }
    public string? PrivateIpAddress { get; init; }
    public string? AvailabilityZone { get; init; }
    public required string State { get; init; }
    public SsmTargetStatus SsmStatus { get; init; }
}

public sealed record PortForwardRule
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required PortForwardMode Mode { get; init; }
    public string? RemoteHost { get; init; }
    public required int RemotePort { get; init; }
    public required int LocalPort { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed class SsmSession
{
    public required Guid Id { get; init; }
    public required PortForwardRule Rule { get; init; }
    public required string InstanceId { get; init; }
    public SessionState State { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed record Ec2TagFilter(string Name, IReadOnlyList<string> Values);

public sealed record TargetFilter
{
    public string TagName { get; init; } = "Name";
    public string TagValue { get; init; } = "bastion-host";
    public bool RequireRunning { get; init; } = true;
    public bool RequireSsmOnline { get; init; } = true;
}

public sealed record AwsCliVersion(string Raw, int Major);

public sealed record StartPortForwardRequest
{
    public required string Profile { get; init; }
    public required string Region { get; init; }
    public required string InstanceId { get; init; }
    public required PortForwardRule Rule { get; init; }
}

public sealed class AppErrorException : Exception
{
    public AppErrorCategory Category { get; }
    public string Summary { get; }
    public string LikelyCause { get; }
    public string SuggestedAction { get; }
    public string? TechnicalDetails { get; }

    public AppErrorException(
        AppErrorCategory category,
        string summary,
        string likelyCause,
        string suggestedAction,
        string? technicalDetails = null,
        Exception? inner = null)
        : base(summary, inner)
    {
        Category = category;
        Summary = summary;
        LikelyCause = likelyCause;
        SuggestedAction = suggestedAction;
        TechnicalDetails = technicalDetails;
    }

    public string ToUserMessage() =>
        $"{Summary}{Environment.NewLine}{Environment.NewLine}{LikelyCause}{Environment.NewLine}{Environment.NewLine}{SuggestedAction}";
}
