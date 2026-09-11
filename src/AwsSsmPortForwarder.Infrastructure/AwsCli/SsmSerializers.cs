using System.Globalization;
using System.Text.Json;
using AwsSsmPortForwarder.Core.Models;

namespace AwsSsmPortForwarder.Infrastructure.AwsCli;

public static class SsmDocumentNameResolver
{
    public static string Resolve(PortForwardType type) => type switch
    {
        PortForwardType.ManagedNode => "AWS-StartPortForwardingSession",
        PortForwardType.RemoteHost => "AWS-StartPortForwardingSessionToRemoteHost",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported forwarding type.")
    };
}

public static class SsmParameterSerializer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null
    };

    public static string Serialize(PortForwardRule rule)
    {
        if (rule.RemotePort is null || rule.LocalPort is null)
            throw new InvalidOperationException("Ports are required before serialization.");

        var remote = rule.RemotePort.Value.ToString(CultureInfo.InvariantCulture);
        var local = rule.LocalPort.Value.ToString(CultureInfo.InvariantCulture);

        return rule.Type switch
        {
            PortForwardType.ManagedNode => JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["portNumber"] = [remote],
                ["localPortNumber"] = [local]
            }, Json),
            PortForwardType.RemoteHost => JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["host"] = [rule.RemoteHost!.Trim()],
                ["portNumber"] = [remote],
                ["localPortNumber"] = [local]
            }, Json),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Type, "Unsupported forwarding type.")
        };
    }
}
