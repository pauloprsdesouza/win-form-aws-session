using AwsSsmPortForwarder.Core.Models;
using AwsSsmPortForwarder.Core.Services;
using AwsSsmPortForwarder.Infrastructure.AwsCli;
using FluentAssertions;

namespace AwsSsmPortForwarder.UnitTests;

public class PortParserTests
{
    [Theory]
    [InlineData("6106", 6106, 6106)]
    [InlineData("6379:16379", 6379, 16379)]
    public void Parses_single_mapping(string input, int remote, int local)
    {
        var result = PortParser.Parse(input);
        result.Errors.Should().BeEmpty();
        result.Mappings.Should().ContainSingle().Which.Should().Be(new PortMapping(remote, local));
    }

    [Fact]
    public void Parses_multiple_and_deduplicates()
    {
        var result = PortParser.Parse("6106, 5432; 6106");
        result.Mappings.Should().HaveCount(2);
    }

    [Fact]
    public void Rejects_duplicate_local_ports()
    {
        var result = PortParser.Parse("80:8080, 443:8080");
        result.Errors.Should().Contain(e => e.Contains("Duplicate local port"));
    }

    [Fact]
    public void Empty_input_errors()
    {
        PortParser.Parse("").Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Requires_confirmation_above_20()
    {
        var text = string.Join(",", Enumerable.Range(1000, 21));
        PortParser.Parse(text).RequiresConfirmation.Should().BeTrue();
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

    [Fact]
    public void Invalid_folder_throws()
    {
        var act = () => AwsFolderFactory.FromFolder(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        act.Should().Throw<AppException>().Which.Kind.Should().Be(ErrorKind.InvalidFolder);
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

public class ArgumentFactoryTests
{
    [Fact]
    public void StartPortForward_uses_json_parameters_and_no_shell()
    {
        var request = new StartPortForwardRequest(
            new AwsContext(new AwsFolderContext("C:\\x", "C:\\x\\config", "C:\\x\\credentials"), "prof", "eu-west-1"),
            "i-abc",
            new PortMapping(6106, 6106));

        var args = AwsCliArgumentFactory.StartPortForward(request);
        args.Should().NotContain(a => a.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
        args.Should().Contain("AWS-StartPortForwardingSession");
        args.Should().Contain(a => a.Contains("\"portNumber\":[\"6106\"]"));
        args.Should().Contain(a => a.Contains("\"localPortNumber\":[\"6106\"]"));
        // each value is its own argument
        args.Should().ContainInOrder("--profile", "prof", "--region", "eu-west-1", "--target", "i-abc");
    }

    [Fact]
    public void DescribeInstances_uses_configured_tag_not_hardcoded_only_via_options()
    {
        var args = AwsCliArgumentFactory.DescribeInstances("p", "eu-west-1",
            new TargetOptions("Name", "my-bastion", true, true));
        args.Should().Contain("Name=tag:Name,Values=my-bastion");
        args.Should().NotContain(a => a.Contains("6106"));
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
    public void Maps_sso_and_denies()
    {
        AwsCliErrorMapper.Map("get-caller-identity", 255, "Token has expired and refresh failed", "")
            .Kind.Should().Be(ErrorKind.SsoExpired);
        AwsCliErrorMapper.Map("describe-instances", 254, "AccessDenied", "")
            .Kind.Should().Be(ErrorKind.Ec2Denied);
    }

    [Fact]
    public void Sanitizes_secrets()
    {
        AwsCliErrorMapper.Sanitize("aws_secret_access_key = ABCDEF").Should().Contain("***").And.NotContain("ABCDEF");
    }
}
