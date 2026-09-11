namespace AwsSessionLauncher.Domain;

public sealed class AppSettings
{
    public string? ConfigFilePath { get; set; }
    public string? CredentialsFilePath { get; set; }
    public string? LastProfile { get; set; }
    public string? LastRegion { get; set; }
    public string? LastInstanceId { get; set; }
    public TargetFilterSettings TargetFilter { get; set; } = new();
    public List<PortForwardRuleSettings> Rules { get; set; } = [];
    public int WindowWidth { get; set; } = 980;
    public int WindowHeight { get; set; } = 720;
}

public sealed class TargetFilterSettings
{
    public string TagName { get; set; } = "Name";
    public string TagValue { get; set; } = "bastion-host";
    public bool RequireRunning { get; set; } = true;
    public bool RequireSsmOnline { get; set; } = true;
}

public sealed class PortForwardRuleSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "RemoteHost";
    public string? RemoteHost { get; set; }
    public int RemotePort { get; set; } = 5432;
    public int LocalPort { get; set; } = 5432;
    public bool Enabled { get; set; } = true;
}
