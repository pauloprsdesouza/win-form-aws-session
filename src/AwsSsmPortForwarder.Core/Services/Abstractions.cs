using AwsSsmPortForwarder.Core.Models;

namespace AwsSsmPortForwarder.Core.Services;

public interface IManagedProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    event EventHandler? Exited;
    event EventHandler<string>? OutputReceived;
    event EventHandler<string>? ErrorReceived;
    Task StopAsync(TimeSpan gracefulTimeout);
}

public interface IAwsCliClient
{
    Task<PrerequisiteStatus> DetectPrerequisitesAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListProfilesAsync(AwsFolderContext folder, CancellationToken ct);
    Task<AwsIdentity> GetCallerIdentityAsync(AwsContext context, CancellationToken ct);
    Task SsoLoginAsync(AwsContext context, SsoLoginMode mode, CancellationToken ct);
    Task<string?> GetRegionAsync(AwsContext context, CancellationToken ct);
    Task<IReadOnlyList<AwsTarget>> DescribeTargetsAsync(AwsContext context, TargetOptions options, CancellationToken ct);
    Task<IReadOnlyDictionary<string, SsmNodeStatus>> DescribeSsmNodesAsync(AwsContext context, IReadOnlyCollection<string> ids, CancellationToken ct);
    Task<IManagedProcess> StartPortForwardAsync(StartPortForwardRequest request, CancellationToken ct);
    Task TerminateSessionAsync(AwsContext context, string sessionId, CancellationToken ct);
}

public interface IUserSettingsStore
{
    Task<UserSettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(UserSettings settings, CancellationToken ct);
}

public interface IAppConfigStore
{
    AppConfigRoot Load();
}
