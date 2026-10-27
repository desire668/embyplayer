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

/// <summary>
/// 内容源下拉里的一项：一个已添加的 Emby 服务器，或内置的「TMDB 在线推荐」。
/// 下拉框要同时容纳两者，所以统一成这个外壳（DisplayName 直接给 ComboBox 用）。
/// </summary>
public sealed class ContentSourceOption
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsTmdb { get; init; }
    public ServerEntry? Server { get; init; }
}

public class AppSettings
{
    public List<ServerEntry> Servers { get; set; } = new();
    public string? CurrentServerId { get; set; }

    /// <summary>首页内容源：null / "server" = 当前 Emby 服务器，"tmdb" = TMDB 在线推荐。</summary>
    public string? SourceMode { get; set; }

    /// <summary>左侧导航栏展开时的宽度（用户拖动分隔条调整后记住）。缺省 / 越界时回落到 320。</summary>
    public int NavigationPaneWidth { get; set; } = 320;
    public string? MpvPath { get; set; }
    public bool AutoPlayNext { get; set; } = true;
    public string? DeviceId { get; set; }

    /// <summary>减弱动效：关闭卡片浮起 / 视差 / 转场，只保留状态切换。</summary>
    public bool ReduceMotion { get; set; }

    // 弹幕设置
    public bool DanmakuEnabled { get; set; } = false;
    public int DanmakuFontSize { get; set; } = 40;
    public double DanmakuOpacity { get; set; } = 0.85;
    public int DanmakuScrollDuration { get; set; } = 8;
    public double DanmakuDisplayArea { get; set; } = 0.5;
    /// <summary>弹幕服务器地址：LogVar 弹幕 API 基地址（如 https://host/token），或含 {title} 的旧版直链模板。</summary>
    public string? DanmakuServerUrl { get; set; }

    /// <summary>
    /// TMDB v3 API Key（用户在设置页填写，保存在本机 settings.json）。
    /// 刻意不放进源码：仓库是公开的，硬编码的 Key 会被他人拿去占用配额。
    /// </summary>
    public string? TmdbApiKey { get; set; }

    // 旧版单服务器字段（仅用于迁移）
    public string? ServerUrl { get; set; }
    public string? AccessToken { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
}

public sealed class SettingsService
{
    /// <summary>内容源标识：TMDB 在线推荐（不写入服务器列表）。</summary>
    public const string TmdbSourceId = "tmdb";

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

    /// <summary>首页内容源是否为 TMDB 在线推荐（已添加服务器的用户也可随时切过去）。</summary>
    public bool IsTmdbSource =>
        string.Equals(Current.SourceMode, TmdbSourceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>内容源下拉项：所有已添加的服务器 + 末尾的「TMDB 在线推荐」。</summary>
    public List<ContentSourceOption> SourceOptions
    {
        get
        {
            var options = Current.Servers
                .Select(s => new ContentSourceOption
                {
                    Id = s.Id,
                    DisplayName = s.DisplayName,
                    Server = s
                })
                .ToList();
            options.Add(new ContentSourceOption
            {
                Id = TmdbSourceId,
                DisplayName = "TMDB 在线推荐",
                IsTmdb = true
            });
            return options;
        }
    }

    /// <summary>当前内容源选项（用于下拉框初始选中）。</summary>
    public ContentSourceOption? CurrentSourceOption =>
        IsTmdbSource
            ? new ContentSourceOption { Id = TmdbSourceId, DisplayName = "TMDB 在线推荐", IsTmdb = true }
            : CurrentServer is { } server
                ? new ContentSourceOption { Id = server.Id, DisplayName = server.DisplayName, Server = server }
                : null;

    /// <summary>切换内容源。切到 TMDB 时保留当前服务器配置，播放时仍按片名回媒体库检索。</summary>
    public void SetSourceMode(bool tmdb)
    {
        var mode = tmdb ? TmdbSourceId : "server";
        if (string.Equals(Current.SourceMode, mode, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(Current.SourceMode))
        {
            return;
        }
        Current.SourceMode = mode;
        Save();
        ServersChanged?.Invoke();
    }

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
        // 刚登录的服务器立刻成为内容源（之前停在 TMDB 时也要切回来）
        Current.SourceMode = "server";
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
