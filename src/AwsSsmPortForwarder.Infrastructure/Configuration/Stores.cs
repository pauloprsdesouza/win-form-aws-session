using System.Text.Json;
using AwsSsmPortForwarder.Core.Models;
using AwsSsmPortForwarder.Core.Services;

namespace AwsSsmPortForwarder.Infrastructure.Configuration;

public sealed class FileUserSettingsStore : IUserSettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FileUserSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AwsSsmPortForwarder");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public async Task<UserSettings> LoadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_path)) return new UserSettings();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<UserSettings>(stream, Json, ct).ConfigureAwait(false)
                   ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken ct)
    {
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, settings, Json, ct).ConfigureAwait(false);
        File.Copy(temp, _path, overwrite: true);
        File.Delete(temp);
    }
}

public sealed class FileAppConfigStore : IAppConfigStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public FileAppConfigStore(string? baseDirectory = null)
    {
        var dir = baseDirectory ?? AppContext.BaseDirectory;
        _path = Path.Combine(dir, "appsettings.json");
    }

    public AppConfigRoot Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var defaults = new AppConfigRoot();
                File.WriteAllText(_path, JsonSerializer.Serialize(defaults, Json));
                return defaults;
            }
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppConfigRoot>(json, Json) ?? new AppConfigRoot();
        }
        catch
        {
            return new AppConfigRoot();
        }
    }
}
