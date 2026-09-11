using AwsSessionLauncher.Application;
using AwsSessionLauncher.Domain;
using AwsSessionLauncher.Domain.Enums;
using AwsSessionLauncher.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwsSessionLauncher.IntegrationTests;

public class FakeAwsProcessIntegrationTests
{
    [Fact]
    public async Task Authentication_flow_marks_valid_identity()
    {
        var runner = new ScriptedProcessRunner();
        runner.Script(args => args.Contains("get-caller-identity"),
            new ProcessResult(0, """{"Account":"111122223333","Arn":"arn:aws:sts::111122223333:assumed-role/x","UserId":"AIDAI"}""", ""));

        var cli = new AwsCli(runner, new FakePrereq(), NullLogger<AwsCli>.Instance);
        var auth = new AwsAuthenticationService(cli, NullLogger<AwsAuthenticationService>.Instance);
        var profile = new AwsProfile { Name = "dev", Type = AwsProfileType.Sso, Region = "eu-west-1" };

        var state = await auth.ValidateAsync(profile, "eu-west-1", CancellationToken.None);
        state.Status.Should().Be(AuthStatus.Valid);
        state.Identity!.AccountId.Should().Be("111122223333");
    }

    [Fact]
    public async Task Expired_sso_requires_login()
    {
        var runner = new ScriptedProcessRunner();
        runner.Script(args => args.Contains("get-caller-identity"),
            new ProcessResult(255, "", "Error: Token has expired and refresh failed"));

        var cli = new AwsCli(runner, new FakePrereq(), NullLogger<AwsCli>.Instance);
        var auth = new AwsAuthenticationService(cli, NullLogger<AwsAuthenticationService>.Instance);
        var profile = new AwsProfile { Name = "dev", Type = AwsProfileType.Sso };

        var state = await auth.ValidateAsync(profile, null, CancellationToken.None);
        state.Status.Should().Be(AuthStatus.LoginRequired);
    }

    [Fact]
    public async Task Discovery_attaches_ssm_status()
    {
        var runner = new ScriptedProcessRunner();
        runner.Script(args => args.Contains("describe-instances"),
            new ProcessResult(0, """
            {"Reservations":[{"Instances":[{"InstanceId":"i-1","State":{"Name":"running"},"Tags":[{"Key":"Name","Value":"bastion-host"}]}]}]}
            """, ""));
        runner.Script(args => args.Contains("describe-instance-information"),
            new ProcessResult(0, """
            {"InstanceInformationList":[{"InstanceId":"i-1","PingStatus":"Online"}]}
            """, ""));

        var cli = new AwsCli(runner, new FakePrereq(), NullLogger<AwsCli>.Instance);
        var discovery = new AwsInstanceDiscoveryService(cli);
        var targets = await discovery.DiscoverAsync("dev", "eu-west-1", new TargetFilter(), CancellationToken.None);
        targets.Should().ContainSingle();
        targets[0].SsmStatus.Should().Be(SsmTargetStatus.Online);
    }

    [Fact]
    public async Task Access_denied_maps_to_permission_error()
    {
        var runner = new ScriptedProcessRunner();
        runner.Script(_ => true, new ProcessResult(254, "", "An error occurred (AccessDenied) when calling the operation"));
        var cli = new AwsCli(runner, new FakePrereq(), NullLogger<AwsCli>.Instance);
        var act = async () => await cli.GetCallerIdentityAsync("dev", null, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AppErrorException>();
        ex.Which.Category.Should().Be(AppErrorCategory.AwsPermissionDenied);
    }

    private sealed class FakePrereq : IPrerequisiteDetector
    {
        public Task<PrerequisiteStatus> DetectAsync(CancellationToken ct) =>
            Task.FromResult(new PrerequisiteStatus { AwsCliReady = true, AwsCliPath = "aws.exe", SessionManagerReady = true });
        public string? ResolveAwsCliPath() => "aws.exe";
    }

    private sealed class ScriptedProcessRunner : IProcessRunner
    {
        private readonly List<(Func<IReadOnlyList<string>, bool> Match, ProcessResult Result)> _scripts = [];

        public void Script(Func<IReadOnlyList<string>, bool> match, ProcessResult result) =>
            _scripts.Add((match, result));

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            var script = _scripts.FirstOrDefault(s => s.Match(arguments));
            if (script.Match is null)
                return Task.FromResult(new ProcessResult(1, "", "no script"));
            return Task.FromResult(script.Result);
        }

        public Task<ManagedProcess> StartLongRunningAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
