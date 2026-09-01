using System.Text.Json;

namespace EmbyPlayer.Services;

public class ServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string? AccessToken { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? ServerName { get; set; }

    public string DisplayName => ServerName ?? (string.IsNullOrEmpty(UserName) ? Url : UserName);
}

public class AppSettings
{
    public List<ServerEntry> Servers { get; set; } = new();
    public string? CurrentServerId { get; set; }
    public string? MpvPath { get; set; }
    public bool AutoPlayNext { get; set; } = true;
    public string? DeviceId { get; set; }

    // 旧版单服务器字段（仅用于迁移）
    public string? ServerUrl { get; set; }
    public string? AccessToken { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmbyPlayer", "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event Action? SettingsChanged;
    public event Action? ServersChanged;

    public SettingsService()
    {
        Load();
        MigrateLegacyServer();
        if (string.IsNullOrEmpty(Current.DeviceId))
        {
            Current.DeviceId = Guid.NewGuid().ToString("N");
            Save();
        }
    }

    public ServerEntry? CurrentServer =>
        Current.Servers.FirstOrDefault(s => s.Id == Current.CurrentServerId)
        ?? Current.Servers.FirstOrDefault();

    public bool IsLoggedIn =>
        CurrentServer is not null && !string.IsNullOrEmpty(CurrentServer.AccessToken);

    public string? ServerUrl => CurrentServer?.Url;
    public string? AccessToken => CurrentServer?.AccessToken;
    public string? UserId => CurrentServer?.UserId;
    public string? UserName => CurrentServer?.UserName;

    private void MigrateLegacyServer()
    {
        if (Current.Servers.Count == 0 && !string.IsNullOrEmpty(Current.ServerUrl))
        {
            var entry = new ServerEntry
            {
                Url = Current.ServerUrl,
                AccessToken = Current.AccessToken,
                UserId = Current.UserId,
                UserName = Current.UserName
            };
            Current.Servers.Add(entry);
            Current.CurrentServerId = entry.Id;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Current, JsonOptions));
        SettingsChanged?.Invoke();
    }

    /// <summary>登录成功后新增或更新服务器条目，并切换为当前服务器。</summary>
    public void AddOrUpdateServer(string url, string token, string userId, string userName, string? serverName = null)
    {
        var existing = Current.Servers.FirstOrDefault(s => s.Url == url);
        if (existing is null)
        {
            existing = new ServerEntry { Url = url };
            Current.Servers.Add(existing);
        }
        existing.AccessToken = token;
        existing.UserId = userId;
        existing.UserName = userName;
        existing.ServerName = string.IsNullOrEmpty(serverName) ? existing.ServerName : serverName;
        Current.CurrentServerId = existing.Id;
        Save();
        ServersChanged?.Invoke();
    }

    public void SwitchServer(string serverId)
    {
        if (Current.CurrentServerId == serverId)
        {
            return;
        }
        Current.CurrentServerId = serverId;
        Save();
        ServersChanged?.Invoke();
    }

    /// <summary>退出当前服务器登录：移除该条目；若还有其它服务器则切到第一个。</summary>
    public void LogoutCurrent()
    {
        var current = CurrentServer;
        if (current is not null)
        {
            Current.Servers.Remove(current);
        }
        Current.CurrentServerId = Current.Servers.FirstOrDefault()?.Id;
        Save();
        ServersChanged?.Invoke();
    }
}
