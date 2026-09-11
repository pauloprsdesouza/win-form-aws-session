using System.Text.RegularExpressions;
using AwsSessionLauncher.Domain;
using AwsSessionLauncher.Domain.Enums;
using AwsSessionLauncher.Infrastructure;
using Microsoft.Extensions.Logging;

namespace AwsSessionLauncher.Application;

public interface IAwsEnvironmentService
{
    Task<PrerequisiteStatus> CheckPrerequisitesAsync(CancellationToken ct);
    string ResolveConfigPath();
    string ResolveCredentialsPath();
    void ApplySettings(AppSettings settings);
}

public sealed class AwsEnvironmentService(
    IPrerequisiteDetector prerequisites,
    IFileSettingsRepository settingsRepository) : IAwsEnvironmentService
{
    private AppSettings? _cached;

    public Task<PrerequisiteStatus> CheckPrerequisitesAsync(CancellationToken ct) =>
        prerequisites.DetectAsync(ct);

    public string ResolveConfigPath() =>
        AwsPathResolver.ResolveConfigPath(_cached?.ConfigFilePath);

    public string ResolveCredentialsPath() =>
        AwsPathResolver.ResolveCredentialsPath(_cached?.CredentialsFilePath);

    public async Task EnsureSettingsLoadedAsync(CancellationToken ct) =>
        _cached ??= await settingsRepository.LoadAsync(ct).ConfigureAwait(false);

    public void ApplySettings(AppSettings settings) => _cached = settings;
}

public interface IAwsProfileService
{
    Task<IReadOnlyList<AwsProfile>> LoadProfilesAsync(CancellationToken ct);
}

public sealed class AwsProfileService(
    IAwsEnvironmentService environment,
    AwsConfigParser parser,
    IFileSettingsRepository settingsRepository) : IAwsProfileService
{
    public async Task<IReadOnlyList<AwsProfile>> LoadProfilesAsync(CancellationToken ct)
    {
        var settings = await settingsRepository.LoadAsync(ct).ConfigureAwait(false);
        environment.ApplySettings(settings);

        var configPath = AwsPathResolver.ResolveConfigPath(settings.ConfigFilePath);
        var credPath = AwsPathResolver.ResolveCredentialsPath(settings.CredentialsFilePath);

        string? config = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false) : null;
        string? creds = File.Exists(credPath) ? await File.ReadAllTextAsync(credPath, ct).ConfigureAwait(false) : null;

        if (config is null && creds is null)
        {
            throw new AppErrorException(
                AppErrorCategory.ConfigurationParseFailed,
                "AWS configuration was not found.",
                "Neither the config nor credentials file exists at the resolved paths.",
                "Run 'aws configure sso' or browse to your AWS config/credentials files in Settings.",
                $"Config: {configPath}{Environment.NewLine}Credentials: {credPath}");
        }

        return parser.ParseProfiles(config, creds);
    }
}

public sealed record ProfileAuthState(
    AuthStatus Status,
    AwsIdentity? Identity,
    string? Message);

public interface IAwsAuthenticationService
{
    Task<ProfileAuthState> ValidateAsync(AwsProfile profile, string? region, CancellationToken ct);
    Task<ProfileAuthState> SignInAsync(AwsProfile profile, string? region, CancellationToken ct);
}

public sealed class AwsAuthenticationService(IAwsCli awsCli, ILogger<AwsAuthenticationService> logger)
    : IAwsAuthenticationService
{
    public async Task<ProfileAuthState> ValidateAsync(AwsProfile profile, string? region, CancellationToken ct)
    {
        try
        {
            var identity = await awsCli.GetCallerIdentityAsync(profile.Name, region, ct).ConfigureAwait(false);
            return new ProfileAuthState(AuthStatus.Valid, identity, null);
        }
        catch (AppErrorException ex) when (ex.Category == AppErrorCategory.AuthenticationRequired)
        {
            var status = profile.Type == AwsProfileType.Sso ? AuthStatus.LoginRequired : AuthStatus.Invalid;
            return new ProfileAuthState(status, null, ex.Summary);
        }
        catch (AppErrorException ex)
        {
            logger.LogWarning(ex, "Profile validation failed for {Profile}", profile.Name);
            return new ProfileAuthState(AuthStatus.Invalid, null, ex.Summary);
        }
    }

    public async Task<ProfileAuthState> SignInAsync(AwsProfile profile, string? region, CancellationToken ct)
    {
        if (profile.Type != AwsProfileType.Sso)
        {
            return new ProfileAuthState(
                AuthStatus.Invalid,
                null,
                "SSO login is only available for SSO profiles.");
        }

        await awsCli.SsoLoginAsync(profile.Name, ct).ConfigureAwait(false);
        return await ValidateAsync(profile, region, ct).ConfigureAwait(false);
    }
}

public interface IAwsInstanceDiscoveryService
{
    Task<IReadOnlyList<Ec2Target>> DiscoverAsync(
        string profile, string region, TargetFilter filter, CancellationToken ct);
}

public sealed class AwsInstanceDiscoveryService(IAwsCli awsCli) : IAwsInstanceDiscoveryService
{
    public async Task<IReadOnlyList<Ec2Target>> DiscoverAsync(
        string profile, string region, TargetFilter filter, CancellationToken ct)
    {
        var targets = await awsCli.DescribeInstancesAsync(profile, region, filter, ct).ConfigureAwait(false);
        var ids = targets.Select(t => t.InstanceId).ToList();
        var statuses = await awsCli.GetSsmStatusesAsync(profile, region, ids, ct).ConfigureAwait(false);

        return targets.Select(t => t with
        {
            SsmStatus = statuses.TryGetValue(t.InstanceId, out var s) ? s : SsmTargetStatus.NotManaged
        }).ToList();
    }
}

public interface IPortValidationService
{
    void ValidateRules(IReadOnlyList<PortForwardRule> rules);
    void ValidateRule(PortForwardRule rule);
    void EnsureLocalPortFree(int port);
}

public sealed class PortValidationService(IPortInspector portInspector) : IPortValidationService
{
    private static readonly Regex HostRegex = new(
        @"^[A-Za-z0-9]([A-Za-z0-9\-.]{0,253}[A-Za-z0-9])?$",
        RegexOptions.Compiled);

    public void ValidateRules(IReadOnlyList<PortForwardRule> rules)
    {
        var enabled = rules.Where(r => r.Enabled).ToList();
        foreach (var rule in enabled)
            ValidateRule(rule);

        var dup = enabled.GroupBy(r => r.LocalPort).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            throw new AppErrorException(
                AppErrorCategory.LocalPortInUse,
                $"Duplicate local port {dup.Key}.",
                "More than one enabled rule uses the same local port.",
                "Change one of the local ports so each enabled rule is unique.");
        }
    }

    public void ValidateRule(PortForwardRule rule)
    {
        if (rule.RemotePort is < 1 or > 65535 || rule.LocalPort is < 1 or > 65535)
        {
            throw new AppErrorException(
                AppErrorCategory.Unknown,
                "Invalid port number.",
                "Ports must be between 1 and 65535.",
                "Correct the remote/local port values.");
        }

        if (rule.Mode == PortForwardMode.RemoteHost)
        {
            if (string.IsNullOrWhiteSpace(rule.RemoteHost))
            {
                throw new AppErrorException(
                    AppErrorCategory.Unknown,
                    "Remote host is required.",
                    "RemoteHost mode needs a destination hostname.",
                    "Enter a hostname such as db.internal.");
            }

            if (rule.RemoteHost.Contains('\r') || rule.RemoteHost.Contains('\n') ||
                !HostRegex.IsMatch(rule.RemoteHost))
            {
                throw new AppErrorException(
                    AppErrorCategory.Unknown,
                    "Invalid remote host.",
                    "The hostname contains invalid characters.",
                    "Use a DNS name or IP without spaces or control characters.");
            }
        }
    }

    public void EnsureLocalPortFree(int port)
    {
        if (!portInspector.IsLocalPortAvailable(port))
        {
            throw new AppErrorException(
                AppErrorCategory.LocalPortInUse,
                $"Local port {port} is already in use.",
                "Another process is listening on that TCP port.",
                $"Choose another local port or stop the process currently using {port}.");
        }
    }
}

public static class RegionResolver
{
    public static string Resolve(string? userOverride, string? profileRegion)
    {
        if (!string.IsNullOrWhiteSpace(userOverride))
            return userOverride.Trim();
        if (!string.IsNullOrWhiteSpace(profileRegion))
            return profileRegion.Trim();
        throw new AppErrorException(
            AppErrorCategory.RegionMissing,
            "AWS region is not set.",
            "No region override or profile region is available.",
            "Select a region in the UI or set region in the AWS profile.");
    }
}
