using System.Text;
using AwsSessionLauncher.Domain;
using AwsSessionLauncher.Domain.Enums;

namespace AwsSessionLauncher.Infrastructure;

public sealed class AwsConfigParser
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "aws_secret_access_key",
        "aws_session_token",
        "aws_security_token"
    };

    public IReadOnlyList<AwsProfile> ParseProfiles(string? configContent, string? credentialsContent)
    {
        var map = new Dictionary<string, ProfileBuilder>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configContent))
            ParseIni(configContent, map, isConfig: true);

        if (!string.IsNullOrWhiteSpace(credentialsContent))
            ParseIni(credentialsContent, map, isConfig: false);

        return map.Values
            .Select(b => b.Build())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ParseIni(string content, Dictionary<string, ProfileBuilder> map, bool isConfig)
    {
        string? section = null;
        bool isSsoSession = false;

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var raw = line[1..^1].Trim();
                isSsoSession = raw.StartsWith("sso-session ", StringComparison.OrdinalIgnoreCase);
                if (isSsoSession)
                {
                    section = null;
                    continue;
                }

                section = NormalizeProfileName(raw, isConfig);
                if (!map.ContainsKey(section))
                    map[section] = new ProfileBuilder(section);
                continue;
            }

            if (section is null || isSsoSession)
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            if (SecretKeys.Contains(key))
                continue;

            var value = line[(eq + 1)..].Trim();
            map[section].Apply(key, value);
        }
    }

    public static string NormalizeProfileName(string section, bool isConfig)
    {
        if (section.Equals("default", StringComparison.OrdinalIgnoreCase))
            return "default";

        if (isConfig && section.StartsWith("profile ", StringComparison.OrdinalIgnoreCase))
            return section["profile ".Length..].Trim();

        return section.Trim();
    }

    private sealed class ProfileBuilder(string name)
    {
        private string? _region;
        private string? _ssoSession;
        private bool _hasSsoStartUrl;
        private bool _hasSsoAccount;
        private bool _hasRoleArn;
        private bool _hasCredentialProcess;
        private bool _hasAccessKey;

        public void Apply(string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "region":
                    _region = value;
                    break;
                case "sso_session":
                    _ssoSession = value;
                    break;
                case "sso_start_url":
                    _hasSsoStartUrl = true;
                    break;
                case "sso_account_id":
                    _hasSsoAccount = true;
                    break;
                case "role_arn":
                    _hasRoleArn = true;
                    break;
                case "credential_process":
                    _hasCredentialProcess = true;
                    break;
                case "aws_access_key_id":
                    _hasAccessKey = true;
                    break;
            }
        }

        public AwsProfile Build()
        {
            var type = AwsProfileType.Unknown;
            if (!string.IsNullOrWhiteSpace(_ssoSession) || _hasSsoStartUrl || _hasSsoAccount)
                type = AwsProfileType.Sso;
            else if (_hasCredentialProcess)
                type = AwsProfileType.CredentialProcess;
            else if (_hasRoleArn)
                type = AwsProfileType.AssumeRole;
            else if (_hasAccessKey)
                type = AwsProfileType.StaticCredentials;

            return new AwsProfile
            {
                Name = name,
                Region = _region,
                Type = type,
                SsoSessionName = _ssoSession
            };
        }
    }
}

public static class AwsPathResolver
{
    public static string ResolveConfigPath(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;
        var env = Environment.GetEnvironmentVariable("AWS_CONFIG_FILE");
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "config");
    }

    public static string ResolveCredentialsPath(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;
        var env = Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE");
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "credentials");
    }
}
