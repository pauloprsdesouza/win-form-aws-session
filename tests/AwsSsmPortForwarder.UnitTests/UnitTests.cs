using AwsSsmPortForwarder.Core.Models;
using AwsSsmPortForwarder.Core.Services;
using AwsSsmPortForwarder.Infrastructure.AwsCli;
using FluentAssertions;

namespace AwsSsmPortForwarder.UnitTests;

public class PortForwardRuleValidatorTests
{
    [Fact]
    public void Managed_node_ready_without_host()
    {
        var rule = new PortForwardRule(Guid.NewGuid(), true, PortForwardType.ManagedNode, null, 6106, 6106);
        var result = PortForwardRuleValidator.Validate(rule);
        result.IsValid.Should().BeTrue();
        result.Status.Should().Be(SessionState.Ready);
    }

    [Fact]
    public void Remote_host_requires_valid_host()
    {
        var missing = new PortForwardRule(Guid.NewGuid(), true, PortForwardType.RemoteHost, null, 443, 9100);
        PortForwardRuleValidator.Validate(missing).IsValid.Should().BeFalse();

        var bad = missing with { RemoteHost = "https://evil.example/path" };
        PortForwardRuleValidator.Validate(bad).Errors.Should()
            .Contain(e => e.Contains("hostname or IP", StringComparison.OrdinalIgnoreCase));

        var good = missing with { RemoteHost = "api.internal.example" };
        PortForwardRuleValidator.Validate(good).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Duplicate_local_ports_rejected_in_set()
    {
        var rules = new[]
        {
            new PortForwardRule(Guid.NewGuid(), true, PortForwardType.ManagedNode, null, 80, 8080),
            new PortForwardRule(Guid.NewGuid(), true, PortForwardType.RemoteHost, "db.internal", 5432, 8080)
        };
        PortForwardRuleValidator.ValidateEnabledSet(rules).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("host.example", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("https://host", false)]
    [InlineData("host:443", false)]
    [InlineData("user@host", false)]
    public void Host_validation_cases(string host, bool ok)
    {
        var error = PortForwardRuleValidator.ValidateHost(host);
        if (ok) error.Should().BeNull();
        else error.Should().NotBeNull();
    }
}

public class SsmSerializerTests
{
    [Fact]
    public void Managed_node_parameters_omit_host()
    {
        var rule = new PortForwardRule(Guid.NewGuid(), true, PortForwardType.ManagedNode, null, 6106, 6106);
        var json = SsmParameterSerializer.Serialize(rule);
        json.Should().Contain("\"portNumber\":[\"6106\"]");
        json.Should().Contain("\"localPortNumber\":[\"6106\"]");
        json.Should().NotContain("host");
        SsmDocumentNameResolver.Resolve(PortForwardType.ManagedNode)
            .Should().Be("AWS-StartPortForwardingSession");
    }

    [Fact]
    public void Remote_host_parameters_include_single_host()
    {
        var rule = new PortForwardRule(Guid.NewGuid(), true, PortForwardType.RemoteHost, "api.internal", 443, 9100);
        var json = SsmParameterSerializer.Serialize(rule);
        json.Should().Contain("\"host\":[\"api.internal\"]");
        json.Should().Contain("\"portNumber\":[\"443\"]");
        json.Should().Contain("\"localPortNumber\":[\"9100\"]");
        SsmDocumentNameResolver.Resolve(PortForwardType.RemoteHost)
            .Should().Be("AWS-StartPortForwardingSessionToRemoteHost");
    }

    [Fact]
    public void Argument_factory_uses_typed_document_and_no_shell()
    {
        var request = new StartPortForwardRequest(
            new AwsContext(new AwsFolderContext("C:\\x", "C:\\x\\config", "C:\\x\\credentials"), "prof", "eu-west-1"),
            "i-abc",
            new PortForwardRule(Guid.NewGuid(), true, PortForwardType.RemoteHost, "api.internal", 443, 9100));
        var args = AwsCliArgumentFactory.StartPortForward(request);
        args.Should().Contain("AWS-StartPortForwardingSessionToRemoteHost");
        args.Should().NotContain(a => a.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
        args.Should().NotContain(a => a.Contains("winpty", StringComparison.OrdinalIgnoreCase));
        args.Should().ContainInOrder("--profile", "prof", "--target", "i-abc");
    }
}

public class AwsFolderFactoryTests
{
    [Fact]
    public void BuildChildEnvironment_sets_both_overrides_inside_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aws-folder-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config"), "[default]\nregion=us-east-1\n");
        try
        {
            var folder = AwsFolderFactory.FromFolder(dir);
            var env = AwsFolderFactory.BuildChildEnvironment(folder);
            env["AWS_CONFIG_FILE"].Should().Be(Path.Combine(dir, "config"));
            env["AWS_SHARED_CREDENTIALS_FILE"].Should().Be(Path.Combine(dir, "credentials"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class SsoClassifierTests
{
    [Fact]
    public void Detects_sso_without_reading_secrets()
    {
        var config = """
            [profile demo]
            sso_session = company
            region = eu-west-1
            """;
        SsoProfileClassifier.IsSsoProfile(config, "demo").Should().BeTrue();
        SsoProfileClassifier.IsSsoProfile(config, "other").Should().BeFalse();
    }
}

public class JsonParserTests
{
    [Fact]
    public void Parses_instances_and_ssm()
    {
        var json = """
            {"Reservations":[{"Instances":[{"InstanceId":"i-1","State":{"Name":"running"},"Placement":{"AvailabilityZone":"a"},"Tags":[{"Key":"Name","Value":"bastion-host"}]}]}]}
            """;
        var targets = AwsJsonParsers.ParseInstances(json);
        targets.Should().ContainSingle(t => t.InstanceId == "i-1");

        var ssm = AwsJsonParsers.ParseSsm(
            """{"InstanceInformationList":[{"InstanceId":"i-1","PingStatus":"Online"}]}""",
            ["i-1"]);
        ssm["i-1"].Should().Be(SsmNodeStatus.Online);
    }
}

public class ErrorMapperTests
{
    [Fact]
    public void Maps_sso_and_remote_host_errors()
    {
        AwsCliErrorMapper.Map("get-caller-identity", 255, "Token has expired and refresh failed", "")
            .Kind.Should().Be(ErrorKind.SsoExpired);
        AwsCliErrorMapper.Map("start-session", 255, "Could not resolve host", "")
            .Kind.Should().Be(ErrorKind.RemoteHostUnreachable);
    }

    [Fact]
    public void Sanitizes_secrets()
    {
        AwsCliErrorMapper.Sanitize("aws_secret_access_key = ABCDEF").Should().Contain("***").And.NotContain("ABCDEF");
    }
}

public class RetryPolicyTests
{
    [Fact]
    public void Default_max_retries_is_three()
    {
        ConnectionOrchestrator.DefaultMaxRetries.Should().Be(3);
        new SessionView
        {
            Rule = new PortForwardRule(Guid.NewGuid(), true, PortForwardType.ManagedNode, null, 1, 1)
        }.MaxRetries.Should().Be(3);
    }
}
