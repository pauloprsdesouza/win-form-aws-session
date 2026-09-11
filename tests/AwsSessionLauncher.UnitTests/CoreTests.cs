using AwsSessionLauncher.Application;
using AwsSessionLauncher.Domain;
using AwsSessionLauncher.Domain.Enums;
using AwsSessionLauncher.Infrastructure;
using FluentAssertions;

namespace AwsSessionLauncher.UnitTests;

public class AwsConfigParserTests
{
    private readonly AwsConfigParser _parser = new();

    [Fact]
    public void NormalizeProfileName_strips_profile_prefix()
    {
        AwsConfigParser.NormalizeProfileName("profile dev", isConfig: true).Should().Be("dev");
        AwsConfigParser.NormalizeProfileName("default", isConfig: true).Should().Be("default");
        AwsConfigParser.NormalizeProfileName("dev", isConfig: false).Should().Be("dev");
    }

    [Fact]
    public void ParseProfiles_merges_config_and_credentials_without_secrets()
    {
        var config = """
            [default]
            region = us-east-1

            [profile sso-dev]
            sso_session = company
            region = eu-west-1

            [profile assume]
            role_arn = arn:aws:iam::123:role/x
            source_profile = default

            [sso-session company]
            sso_start_url = https://example.awsapps.com/start
            """;

        var creds = """
            [default]
            aws_access_key_id = AKIATEST
            aws_secret_access_key = SUPERSECRET
            """;

        var profiles = _parser.ParseProfiles(config, creds);
        profiles.Should().Contain(p => p.Name == "default" && p.Type == AwsProfileType.StaticCredentials);
        profiles.Should().Contain(p => p.Name == "sso-dev" && p.Type == AwsProfileType.Sso && p.Region == "eu-west-1");
        profiles.Should().Contain(p => p.Name == "assume" && p.Type == AwsProfileType.AssumeRole);
        profiles.Should().NotContain(p => p.Name.Contains("company", StringComparison.OrdinalIgnoreCase) && p.Type == AwsProfileType.Unknown && p.Name != "sso-dev");
    }
}

public class PortValidationTests
{
    [Fact]
    public void Rejects_duplicate_local_ports()
    {
        var svc = new PortValidationService(new AlwaysFreePortInspector());
        var act = () => svc.ValidateRules([
            new PortForwardRule { Id = Guid.NewGuid(), Name = "a", Mode = PortForwardMode.ManagedNode, RemotePort = 80, LocalPort = 8080 },
            new PortForwardRule { Id = Guid.NewGuid(), Name = "b", Mode = PortForwardMode.ManagedNode, RemotePort = 81, LocalPort = 8080 }
        ]);
        act.Should().Throw<AppErrorException>().Which.Category.Should().Be(AppErrorCategory.LocalPortInUse);
    }

    [Fact]
    public void Requires_host_for_remote_mode()
    {
        var svc = new PortValidationService(new AlwaysFreePortInspector());
        var act = () => svc.ValidateRule(new PortForwardRule
        {
            Id = Guid.NewGuid(), Name = "db", Mode = PortForwardMode.RemoteHost, RemotePort = 5432, LocalPort = 5432
        });
        act.Should().Throw<AppErrorException>();
    }

    [Fact]
    public void Detects_occupied_local_port()
    {
        var svc = new PortValidationService(new AlwaysBusyPortInspector());
        var act = () => svc.EnsureLocalPortFree(5432);
        act.Should().Throw<AppErrorException>().Which.Category.Should().Be(AppErrorCategory.LocalPortInUse);
    }

    private sealed class AlwaysFreePortInspector : IPortInspector
    {
        public bool IsLocalPortAvailable(int port) => true;
    }

    private sealed class AlwaysBusyPortInspector : IPortInspector
    {
        public bool IsLocalPortAvailable(int port) => false;
    }
}

public class RegionResolverTests
{
    [Fact]
    public void Prefers_user_override()
    {
        RegionResolver.Resolve("us-west-2", "eu-west-1").Should().Be("us-west-2");
    }

    [Fact]
    public void Falls_back_to_profile_region()
    {
        RegionResolver.Resolve(null, "eu-west-1").Should().Be("eu-west-1");
    }

    [Fact]
    public void Throws_when_missing()
    {
        var act = () => RegionResolver.Resolve(null, null);
        act.Should().Throw<AppErrorException>().Which.Category.Should().Be(AppErrorCategory.RegionMissing);
    }
}

public class AwsCliParsingTests
{
    [Fact]
    public void ParseDescribeInstances_reads_targets()
    {
        var json = """
            {
              "Reservations": [
                {
                  "Instances": [
                    {
                      "InstanceId": "i-abc",
                      "PrivateIpAddress": "10.0.0.1",
                      "State": { "Name": "running" },
                      "Placement": { "AvailabilityZone": "eu-west-1a" },
                      "Tags": [{ "Key": "Name", "Value": "bastion-host" }]
                    }
                  ]
                }
              ]
            }
            """;

        var targets = AwsCli.ParseDescribeInstances(json);
        targets.Should().ContainSingle();
        targets[0].InstanceId.Should().Be("i-abc");
        targets[0].Name.Should().Be("bastion-host");
        targets[0].PrivateIpAddress.Should().Be("10.0.0.1");
    }

    [Fact]
    public void BuildStartSessionArguments_uses_argument_list_safely()
    {
        var runner = new RecordingRunner();
        var prereq = new FixedPrereq();
        var cli = new AwsCli(runner, prereq, Microsoft.Extensions.Logging.Abstractions.NullLogger<AwsCli>.Instance);
        var args = cli.BuildStartSessionArguments(new StartPortForwardRequest
        {
            Profile = "my profile",
            Region = "eu-west-1",
            InstanceId = "i-123",
            Rule = new PortForwardRule
            {
                Id = Guid.NewGuid(),
                Name = "db",
                Mode = PortForwardMode.RemoteHost,
                RemoteHost = "db.internal; rm -rf /",
                RemotePort = 5432,
                LocalPort = 5432
            }
        });

        args.Should().ContainInOrder("ssm", "start-session", "--profile", "my profile", "--target", "i-123");
        args.Should().Contain("AWS-StartPortForwardingSessionToRemoteHost");
        string.Join(' ', args).Should().NotContain("cmd.exe");
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
            => Task.FromResult(new ProcessResult(0, "", ""));

        public Task<ManagedProcess> StartLongRunningAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FixedPrereq : IPrerequisiteDetector
    {
        public Task<PrerequisiteStatus> DetectAsync(CancellationToken ct) =>
            Task.FromResult(new PrerequisiteStatus { AwsCliReady = true, AwsCliPath = "aws.exe" });
        public string? ResolveAwsCliPath() => "aws.exe";
    }
}

public class SettingsSerializationTests
{
    [Fact]
    public async Task Round_trips_settings_without_secrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asl-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var repo = new FileSettingsRepositoryForTests(path);
            var settings = new AppSettings
            {
                LastProfile = "dev",
                Rules =
                [
                    new PortForwardRuleSettings
                    {
                        Name = "Postgres",
                        Mode = "RemoteHost",
                        RemoteHost = "db.internal",
                        RemotePort = 5432,
                        LocalPort = 5432
                    }
                ]
            };
            await repo.SaveAsync(settings, CancellationToken.None);
            var loaded = await repo.LoadAsync(CancellationToken.None);
            loaded.LastProfile.Should().Be("dev");
            loaded.Rules.Should().ContainSingle(r => r.Name == "Postgres");
            (await File.ReadAllTextAsync(path)).Should().NotContain("aws_secret");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

file sealed class FileSettingsRepositoryForTests : IFileSettingsRepository
{
    private readonly string _path;
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    public FileSettingsRepositoryForTests(string path) => _path = path;

    public async Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(_path);
        return (await System.Text.Json.JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, ct))!;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, settings, Options, ct);
        File.Copy(temp, _path, true);
        File.Delete(temp);
    }
}
