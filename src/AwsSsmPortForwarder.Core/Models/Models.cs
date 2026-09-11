namespace AwsSsmPortForwarder.Core.Models;

public sealed record AwsFolderContext(string FolderPath, string? ConfigFilePath, string? CredentialsFilePath)
{
    public bool HasConfig => !string.IsNullOrWhiteSpace(ConfigFilePath) && File.Exists(ConfigFilePath);
    public bool HasCredentials => !string.IsNullOrWhiteSpace(CredentialsFilePath) && File.Exists(CredentialsFilePath);
    public bool IsValid => HasConfig || HasCredentials;
}

public sealed record AwsContext(AwsFolderContext Folder, string ProfileName, string? Region);

public enum PortForwardType
{
    ManagedNode,
    RemoteHost
}

public sealed record PortForwardRule(
    Guid Id,
    bool Enabled,
    PortForwardType Type,
    string? RemoteHost,
    int? RemotePort,
    int? LocalPort,
    string? Label = null);

public sealed record AwsIdentity(string AccountId, string Arn, string UserId);

public sealed record AwsTarget(
    string InstanceId,
    string Name,
    string? PrivateIpAddress,
    string AvailabilityZone,
    string Ec2State,
    string SsmPingStatus);

public sealed record StartPortForwardRequest(AwsContext Context, string InstanceId, PortForwardRule Rule);

public sealed record TargetOptions(string TagKey, string TagValue, bool RequireRunning, bool RequireSsmOnline);

public enum SsoLoginMode
{
    Browser,
    DeviceCode
}

public enum SsmNodeStatus
{
    Online,
    ConnectionLost,
    Inactive,
    NotManaged,
    Unknown
}

public enum AppReadyState
{
    Initializing,
    FolderRequired,
    DiscoveringProfiles,
    ProfileRequired,
    ValidatingAuthentication,
    SignInRequired,
    ResolvingTarget,
    Ready,
    Error
}

public enum SessionState
{
    Incomplete,
    Ready,
    Connecting,
    Connected,
    Reconnecting,
    Stopping,
    Stopped,
    Failed
}

public enum ErrorKind
{
    AwsCliMissing,
    PluginMissing,
    InvalidFolder,
    NoProfiles,
    SsoExpired,
    SsoCancelled,
    CredentialsInvalid,
    RegionMissing,
    Ec2Denied,
    NoBastion,
    MultipleBastions,
    SsmDenied,
    RemoteHostInvalid,
    RemoteHostUnreachable,
    SsmAgentIncompatible,
    LocalPortOccupied,
    SessionExited,
    Unknown
}

public sealed class AppException : Exception
{
    public ErrorKind Kind { get; }
    public string UserMessage { get; }
    public string ActionHint { get; }
    public string? TechnicalDetails { get; }

    public AppException(ErrorKind kind, string userMessage, string actionHint, string? technicalDetails = null, Exception? inner = null)
        : base(userMessage, inner)
    {
        Kind = kind;
        UserMessage = userMessage;
        ActionHint = actionHint;
        TechnicalDetails = technicalDetails;
    }
}

public sealed class SavedConnection
{
    public string Type { get; set; } = nameof(PortForwardType.ManagedNode);
    public string? RemoteHost { get; set; }
    public int? RemotePort { get; set; }
    public int? LocalPort { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Label { get; set; }
}

public sealed class UserSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string? LastAwsFolder { get; set; }
    public string? LastProfile { get; set; }
    public bool RememberConnections { get; set; }
    public List<SavedConnection> Connections { get; set; } = [];
}

public sealed class AppTargetConfig
{
    public string TagKey { get; set; } = "Name";
    public string TagValue { get; set; } = "bastion-host";
    public bool RequireRunning { get; set; } = true;
    public bool RequireSsmOnline { get; set; } = true;

    public TargetOptions ToOptions() => new(TagKey, TagValue, RequireRunning, RequireSsmOnline);
}

public sealed class AppConfigRoot
{
    public AppTargetConfig AwsTarget { get; set; } = new();
}

public sealed class SessionView
{
    public required PortForwardRule Rule { get; init; }
    public SessionState State { get; set; } = SessionState.Incomplete;
    public string? SessionId { get; set; }
    public string? LastError { get; set; }
    public int? ProcessId { get; set; }
    public int RetryAttempt { get; set; }
    public int MaxRetries { get; set; } = 3;
}

public sealed class ProgressUpdate
{
    public required string Message { get; init; }
    public bool IsBusy { get; init; }
    public int? Percent { get; init; }
}

public sealed class PrerequisiteStatus
{
    public bool AwsCliReady { get; init; }
    public string AwsCliVersion { get; init; } = "not found";
    public string? AwsCliPath { get; init; }
    public bool SessionManagerReady { get; init; }
    public string SessionManagerVersion { get; init; } = "not found";
}

public sealed record RuleValidationResult(bool IsValid, SessionState Status, IReadOnlyList<string> Errors);
