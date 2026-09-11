namespace AwsSessionLauncher.Domain.Enums;

public enum AwsProfileType
{
    Unknown,
    Sso,
    StaticCredentials,
    AssumeRole,
    CredentialProcess
}

public enum AuthStatus
{
    Unknown,
    Valid,
    LoginRequired,
    Invalid
}

public enum PortForwardMode
{
    ManagedNode,
    RemoteHost
}

public enum SessionState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

public enum SsmTargetStatus
{
    Online,
    ConnectionLost,
    Inactive,
    NotManaged,
    Unknown
}

public enum AppErrorCategory
{
    PrerequisiteMissing,
    ProfileNotFound,
    AuthenticationRequired,
    AuthenticationFailed,
    RegionMissing,
    AwsPermissionDenied,
    TargetNotFound,
    MultipleTargetsFound,
    TargetNotManaged,
    TargetOffline,
    LocalPortInUse,
    SessionStartFailed,
    SessionUnexpectedExit,
    ConfigurationParseFailed,
    Unknown
}
