using System.Text.RegularExpressions;
using AwsSsmPortForwarder.Core.Models;
using AwsSsmPortForwarder.Core.Services;
using AwsSsmPortForwarder.Infrastructure.Processes;
using Microsoft.Extensions.Logging;

namespace AwsSsmPortForwarder.Infrastructure.AwsCli;

public sealed class AwsCliClient(IProcessRunner runner, ILogger<AwsCliClient> logger) : IAwsCliClient
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Login = TimeSpan.FromMinutes(5);

    public async Task<PrerequisiteStatus> DetectPrerequisitesAsync(CancellationToken ct)
    {
        var awsPath = ResolveAws();
        var awsReady = false;
        var awsVersion = "not found";
        if (awsPath is not null)
        {
            try
            {
                var r = await runner.RunAsync(awsPath, ["--version"], null, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                awsVersion = (r.StdOut + " " + r.StdErr).Trim();
                awsReady = r.ExitCode == 0 && Regex.IsMatch(awsVersion, @"aws-cli/2\.");
            }
            catch { awsReady = false; }
        }

        var plugin = ResolvePlugin();
        var pluginReady = false;
        var pluginVersion = "not found";
        if (plugin is not null)
        {
            try
            {
                var r = await runner.RunAsync(plugin, ["--version"], null, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                pluginVersion = string.IsNullOrWhiteSpace((r.StdOut + r.StdErr).Trim()) ? "installed" : (r.StdOut + " " + r.StdErr).Trim();
                pluginReady = true;
            }
            catch { pluginReady = File.Exists(plugin); pluginVersion = pluginReady ? "installed" : "not found"; }
        }

        return new PrerequisiteStatus
        {
            AwsCliReady = awsReady,
            AwsCliVersion = awsVersion,
            AwsCliPath = awsPath,
            SessionManagerReady = pluginReady,
            SessionManagerVersion = pluginVersion
        };
    }

    public async Task<IReadOnlyList<string>> ListProfilesAsync(AwsFolderContext folder, CancellationToken ct)
    {
        var env = AwsFolderFactory.BuildChildEnvironment(folder);
        var result = await RunAws(AwsCliArgumentFactory.ListProfiles(), env, ct, Short).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw AwsCliErrorMapper.Map("list-profiles", result.ExitCode, result.StdErr, result.StdOut);

        return result.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AwsIdentity> GetCallerIdentityAsync(AwsContext context, CancellationToken ct)
    {
        var env = AwsFolderFactory.BuildChildEnvironment(context.Folder);
        var args = AwsCliArgumentFactory.GetCallerIdentity(context.ProfileName).ToList();
        if (!string.IsNullOrWhiteSpace(context.Region))
        {
            args.Add("--region");
            args.Add(context.Region);
        }
        var result = await RunAws(args, env, ct, Short).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw AwsCliErrorMapper.Map("get-caller-identity", result.ExitCode, result.StdErr, result.StdOut);
        return AwsJsonParsers.ParseIdentity(result.StdOut);
    }

    public async Task SsoLoginAsync(AwsContext context, SsoLoginMode mode, CancellationToken ct)
    {
        logger.LogInformation("SSO login mode={Mode} profile={Profile}", mode, context.ProfileName);
        var env = AwsFolderFactory.BuildChildEnvironment(context.Folder);
        var result = await RunAws(AwsCliArgumentFactory.SsoLogin(context.ProfileName, mode), env, ct, Login).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw AwsCliErrorMapper.Map("sso login", result.ExitCode, result.StdErr, result.StdOut);
    }

    public async Task<string?> GetRegionAsync(AwsContext context, CancellationToken ct)
    {
        var env = AwsFolderFactory.BuildChildEnvironment(context.Folder);
        var result = await RunAws(AwsCliArgumentFactory.GetRegion(context.ProfileName), env, ct, Short).ConfigureAwait(false);
        var region = result.StdOut.Trim();
        return string.IsNullOrWhiteSpace(region) ? null : region;
    }

    public async Task<IReadOnlyList<AwsTarget>> DescribeTargetsAsync(AwsContext context, TargetOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.Region))
            throw new AppException(ErrorKind.RegionMissing, "This profile does not define a Region.", "Select Region");

        var env = AwsFolderFactory.BuildChildEnvironment(context.Folder);
        var result = await RunAws(
            AwsCliArgumentFactory.DescribeInstances(context.ProfileName, context.Region, options),
            env, ct, Short).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw AwsCliErrorMapper.Map("describe-instances", result.ExitCode, result.StdErr, result.StdOut);
        return AwsJsonParsers.ParseInstances(result.StdOut);
    }

    public async Task<IReadOnlyDictionary<string, SsmNodeStatus>> DescribeSsmNodesAsync(
        AwsContext context, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return new Dictionary<string, SsmNodeStatus>();
        if (string.IsNullOrWhiteSpace(context.Region))
            throw new AppException(ErrorKind.RegionMissing, "This profile does not define a Region.", "Select Region");

        var env = AwsFolderFactory.BuildChildEnvironment(context.Folder);
        var result = await RunAws(
            AwsCliArgumentFactory.DescribeInstanceInformation(context.ProfileName, context.Region, ids),
            env, ct, Short).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw AwsCliErrorMapper.Map("describe-instance-information", result.ExitCode, result.StdErr, result.StdOut);
        return AwsJsonParsers.ParseSsm(result.StdOut, ids);
    }

    public Task<IManagedProcess> StartPortForwardAsync(StartPortForwardRequest request, CancellationToken ct)
    {
        var aws = RequireAws();
        var env = AwsFolderFactory.BuildChildEnvironment(request.Context.Folder);
        var args = AwsCliArgumentFactory.StartPortForward(request);
        logger.LogInformation("Starting port forward remote={Remote} local={Local}", request.Mapping.RemotePort, request.Mapping.LocalPort);
        return runner.StartLongRunningAsync(aws, args, env, ct);
    }

    public async Task TerminateSessionAsync(AwsContext context, string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.Region)) return;
        var env = AwsFolderFactory.BuildChildEnvironment(context.Folder);
        await RunAws(
            AwsCliArgumentFactory.TerminateSession(context.ProfileName, context.Region, sessionId),
            env, ct, Short).ConfigureAwait(false);
    }

    private async Task<ProcessResult> RunAws(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        CancellationToken ct,
        TimeSpan timeout)
    {
        var aws = RequireAws();
        return await runner.RunAsync(aws, args, env, ct, timeout).ConfigureAwait(false);
    }

    private string RequireAws() =>
        ResolveAws() ?? throw new AppException(ErrorKind.AwsCliMissing,
            "AWS CLI v2 was not found.",
            "Install AWS CLI v2 from the official AWS documentation.");

    private static string? ResolveAws()
    {
        var path = FindOnPath("aws.exe");
        if (path is not null) return path;
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon", "AWSCLIV2", "aws.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Amazon", "AWSCLIV2", "aws.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolvePlugin()
    {
        var path = FindOnPath("session-manager-plugin.exe");
        if (path is not null) return path;
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
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
