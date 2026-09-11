using AwsSsmPortForwarder.Core.Models;
using AwsSsmPortForwarder.Core.Services;
using AwsSsmPortForwarder.Infrastructure.AwsCli;
using AwsSsmPortForwarder.Infrastructure.Processes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwsSsmPortForwarder.IntegrationTests;

public class FakeAwsCliIntegrationTests
{
    [Fact]
    public async Task Lists_profiles_from_selected_folder_env()
    {
        var runner = new ScriptedRunner();
        Dictionary<string, string>? seenEnv = null;
        runner.OnRun = (_, _, env) =>
        {
            seenEnv = env?.ToDictionary(kv => kv.Key, kv => kv.Value);
            return new ProcessResult(0, "default\ndev\n", "");
        };

        var testCli = new TestableAwsCliClient(runner);
        var folder = AwsFolderFactory.FromFolder(CreateTempAwsFolder(true, false));
        try
        {
            var profiles = await testCli.ListProfilesAsync(folder, CancellationToken.None);
            profiles.Should().BeEquivalentTo(["default", "dev"]);
            seenEnv.Should().ContainKey("AWS_CONFIG_FILE");
            seenEnv.Should().ContainKey("AWS_SHARED_CREDENTIALS_FILE");
        }
        finally
        {
            Directory.Delete(folder.FolderPath, true);
        }
    }

    [Fact]
    public async Task Valid_identity_skips_sso()
    {
        var runner = new ScriptedRunner();
        runner.OnRun = (_, args, _) =>
        {
            if (args.Contains("get-caller-identity"))
                return new ProcessResult(0, "{\"Account\":\"111\",\"Arn\":\"arn:aws:sts::111:assumed-role/x\",\"UserId\":\"A\"}", "");
            return new ProcessResult(1, "", "unexpected");
        };
        var cli = new TestableAwsCliClient(runner);
        var identity = await cli.GetCallerIdentityAsync(
            new AwsContext(DummyFolder(), "dev", "eu-west-1"), CancellationToken.None);
        identity.AccountId.Should().Be("111");
    }

    [Fact]
    public async Task Expired_sso_maps_correctly()
    {
        var runner = new ScriptedRunner();
        runner.OnRun = (_, _, _) => new ProcessResult(255, "", "Error when retrieving token from sso: Token has expired");
        var cli = new TestableAwsCliClient(runner);
        var act = async () => await cli.GetCallerIdentityAsync(new AwsContext(DummyFolder(), "dev", null), CancellationToken.None);
        (await act.Should().ThrowAsync<AppException>()).Which.Kind.Should().Be(ErrorKind.SsoExpired);
    }

    [Fact]
    public async Task Zero_bastions_fail_clearly()
    {
        var runner = new ScriptedRunner();
        runner.OnRun = (_, args, _) =>
        {
            if (args.Contains("configure") && args.Contains("get"))
                return new ProcessResult(0, "eu-west-1\n", "");
            if (args.Contains("describe-instances"))
                return new ProcessResult(0, "{\"Reservations\":[]}", "");
            if (args.Contains("describe-instance-information"))
                return new ProcessResult(0, "{\"InstanceInformationList\":[]}", "");
            return new ProcessResult(1, "", "nope");
        };
        var cli = new TestableAwsCliClient(runner);
        var resolver = new TargetResolver(cli, NullLogger<TargetResolver>.Instance);
        var act = async () => await resolver.ResolveAsync(
            new AwsContext(DummyFolder(), "dev", null),
            new TargetOptions("Name", "bastion-host", true, true),
            null,
            CancellationToken.None);
        (await act.Should().ThrowAsync<AppException>()).Which.Kind.Should().Be(ErrorKind.NoBastion);
    }

    [Fact]
    public async Task Multiple_bastions_require_selection()
    {
        const string instancesJson =
            "{\"Reservations\":[{\"Instances\":[" +
            "{\"InstanceId\":\"i-1\",\"State\":{\"Name\":\"running\"},\"Placement\":{\"AvailabilityZone\":\"a\"},\"Tags\":[{\"Key\":\"Name\",\"Value\":\"bastion-host\"}]}," +
            "{\"InstanceId\":\"i-2\",\"State\":{\"Name\":\"running\"},\"Placement\":{\"AvailabilityZone\":\"b\"},\"Tags\":[{\"Key\":\"Name\",\"Value\":\"bastion-host\"}]}" +
            "]}]}";
        const string ssmJson =
            "{\"InstanceInformationList\":[" +
            "{\"InstanceId\":\"i-1\",\"PingStatus\":\"Online\"}," +
            "{\"InstanceId\":\"i-2\",\"PingStatus\":\"Online\"}" +
            "]}";

        var runner = new ScriptedRunner();
        runner.OnRun = (_, args, _) =>
        {
            if (args.Contains("configure") && args.Contains("get"))
                return new ProcessResult(0, "eu-west-1\n", "");
            if (args.Contains("describe-instances"))
                return new ProcessResult(0, instancesJson, "");
            if (args.Contains("describe-instance-information"))
                return new ProcessResult(0, ssmJson, "");
            return new ProcessResult(1, "", "nope");
        };
        var cli = new TestableAwsCliClient(runner);
        var resolver = new TargetResolver(cli, NullLogger<TargetResolver>.Instance);
        var (region, target, candidates) = await resolver.ResolveAsync(
            new AwsContext(DummyFolder(), "dev", null),
            new TargetOptions("Name", "bastion-host", true, true),
            null,
            CancellationToken.None);
        region.Should().Be("eu-west-1");
        target.Should().BeNull();
        candidates.Should().HaveCount(2);
    }

    [Fact]
    public void Production_defaults_do_not_hardcode_example_port_or_host()
    {
        var settings = new UserSettings();
        settings.RememberConnections.Should().BeFalse();
        settings.Connections.Should().BeEmpty();
        var config = new AppConfigRoot();
        config.AwsTarget.TagValue.Should().Be("bastion-host");
    }

    private static AwsFolderContext DummyFolder() =>
        AwsFolderFactory.FromFolder(CreateTempAwsFolder(true, true));

    private static string CreateTempAwsFolder(bool withConfig, bool withCreds)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aws-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (withConfig) File.WriteAllText(Path.Combine(dir, "config"), "[default]\nregion=eu-west-1\n");
        if (withCreds) File.WriteAllText(Path.Combine(dir, "credentials"), "[default]\naws_access_key_id=AKIATEST\naws_secret_access_key=secret\n");
        return dir;
    }

    private sealed class ScriptedRunner : IProcessRunner
    {
        public Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?, ProcessResult>? OnRun { get; set; }

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(OnRun?.Invoke(executable, arguments, environment) ?? new ProcessResult(1, "", "unscripted"));

        public Task<IManagedProcess> StartLongRunningAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class TestableAwsCliClient(IProcessRunner runner) : IAwsCliClient
    {
        public Task<PrerequisiteStatus> DetectPrerequisitesAsync(CancellationToken ct) =>
            Task.FromResult(new PrerequisiteStatus
            {
                AwsCliReady = true,
                SessionManagerReady = true,
                AwsCliPath = "aws.exe",
                AwsCliVersion = "aws-cli/2.0.0"
            });

        public async Task<IReadOnlyList<string>> ListProfilesAsync(AwsFolderContext folder, CancellationToken ct)
        {
            var env = AwsFolderFactory.BuildChildEnvironment(folder);
            var result = await runner.RunAsync("aws.exe", AwsCliArgumentFactory.ListProfiles(), env, ct).ConfigureAwait(false);
            return result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<AwsIdentity> GetCallerIdentityAsync(AwsContext context, CancellationToken ct)
        {
            var env = AwsFolderFactory.BuildChildEnvironment(context.Folder);
            var result = await runner.RunAsync("aws.exe", AwsCliArgumentFactory.GetCallerIdentity(context.ProfileName), env, ct);
            if (result.ExitCode != 0) throw AwsCliErrorMapper.Map("get-caller-identity", result.ExitCode, result.StdErr, result.StdOut);
            return AwsJsonParsers.ParseIdentity(result.StdOut);
        }

        public Task SsoLoginAsync(AwsContext context, SsoLoginMode mode, CancellationToken ct) => Task.CompletedTask;

        public async Task<string?> GetRegionAsync(AwsContext context, CancellationToken ct)
        {
            var result = await runner.RunAsync("aws.exe", AwsCliArgumentFactory.GetRegion(context.ProfileName),
                AwsFolderFactory.BuildChildEnvironment(context.Folder), ct);
            var r = result.StdOut.Trim();
            return string.IsNullOrWhiteSpace(r) ? null : r;
        }

        public async Task<IReadOnlyList<AwsTarget>> DescribeTargetsAsync(AwsContext context, TargetOptions options, CancellationToken ct)
        {
            var result = await runner.RunAsync("aws.exe",
                AwsCliArgumentFactory.DescribeInstances(context.ProfileName, context.Region!, options),
                AwsFolderFactory.BuildChildEnvironment(context.Folder), ct);
            if (result.ExitCode != 0) throw AwsCliErrorMapper.Map("describe-instances", result.ExitCode, result.StdErr, result.StdOut);
            return AwsJsonParsers.ParseInstances(result.StdOut);
        }

        public async Task<IReadOnlyDictionary<string, SsmNodeStatus>> DescribeSsmNodesAsync(AwsContext context, IReadOnlyCollection<string> ids, CancellationToken ct)
        {
            var result = await runner.RunAsync("aws.exe",
                AwsCliArgumentFactory.DescribeInstanceInformation(context.ProfileName, context.Region!, ids),
                AwsFolderFactory.BuildChildEnvironment(context.Folder), ct);
            return AwsJsonParsers.ParseSsm(result.StdOut, ids);
        }

        public Task<IManagedProcess> StartPortForwardAsync(StartPortForwardRequest request, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task TerminateSessionAsync(AwsContext context, string sessionId, CancellationToken ct) => Task.CompletedTask;
    }
}
