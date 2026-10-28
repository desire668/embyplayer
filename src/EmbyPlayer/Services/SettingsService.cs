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

    /// <summary>在线服务（TMDB 等，不需要 Emby 服务器）。</summary>
    public bool IsOnline { get; init; }

    /// <summary>兼容旧代码：TMDB 在线推荐。</summary>
    public bool IsTmdb => IsOnline;

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

    /// <summary>首页大图自动轮播间隔（秒）；0 表示不自动切换。</summary>
    public int HeroAutoPlaySeconds { get; set; } = 12;

    // ---------- mpv 可视化配置：启动 mpv 时转成命令行参数 ----------

    /// <summary>硬件解码方式：auto-safe（默认）/ auto / no / d3d11va / nvdec / vulkan。</summary>
    public string MpvHwdec { get; set; } = "auto-safe";

    /// <summary>预读缓存秒数（--cache-secs）；0 表示不启用缓存。</summary>
    public int MpvCacheSeconds { get; set; } = 30;

    /// <summary>起始音量 0~100（--volume）。</summary>
    public int MpvVolume { get; set; } = 100;

    /// <summary>首选音轨语言（--alang），空表示交给 mpv 自动选择。</summary>
    public string MpvAudioLang { get; set; } = "";

    /// <summary>mpv 原生字幕字号（--sub-font-size，默认 55）。</summary>
    public double MpvSubFontSize { get; set; } = 55;

    /// <summary>mpv 原生字幕描边粗细（--sub-border-size，默认 3）。</summary>
    public double MpvSubBorderSize { get; set; } = 3;

    /// <summary>mpv 原生字幕垂直位置（--sub-pos，100 为默认贴近底部）。</summary>
    public double MpvSubPos { get; set; } = 100;

    /// <summary>额外 mpv 参数（高级）：空格分隔，原样追加，可覆盖以上任何一项。</summary>
    public string? MpvExtraArgs { get; set; }

    /// <summary>
    /// 自定义快捷键：功能 id → 按键（多个用 ';' 分隔，空串表示主动取消绑定）。
    /// 没写进来（或整段缺失）的功能用默认键，见 <see cref="MpvShortcuts"/>。
    /// </summary>
    public Dictionary<string, string> MpvKeyBindings { get; set; } = new();

    /// <summary>
    /// 搜索历史：最近搜过的词，最新的排最前（最多 <see cref="SettingsService.MaxSearchHistory"/> 条）。
    /// 只在用户主动提交搜索时记录，防抖过程中的中间输入不算。
    /// </summary>
    public List<string> SearchHistory { get; set; } = new();

    /// <summary>减弱动效：关闭卡片浮起 / 视差 / 转场，只保留状态切换。</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>颜色模式：system / light / dark。MainWindow 在启动和设置变更时应用。</summary>
    public string ThemeMode { get; set; } = "system";

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

    /// <summary>
    /// OMDb API Key（omdbapi.com，免费注册），用于详情页显示 IMDb 评分。
    /// 留空则详情页不显示 IMDb 评分，不会影响其它功能。
    /// </summary>
    public string? OmdbApiKey { get; set; }

    /// <summary>
    /// Trakt API 的 Client ID（trakt.tv/oauth/applications 建应用即得），用于详情页显示 Trakt 评分。
    /// 留空则不显示 Trakt 评分。
    /// </summary>
    public string? TraktClientId { get; set; }

    // ---------- 外部服务「自定义接口地址」（留空则用官方默认） ----------

    /// <summary>TMDB API 根地址，形如 https://api.themoviedb.org/3；自建反代 / 镜像填这里。</summary>
    public string? TmdbBaseUrl { get; set; }

    /// <summary>TMDB 图片 CDN 前缀，形如 https://image.tmdb.org/t/p。</summary>
    public string? TmdbImageBaseUrl { get; set; }

    /// <summary>OMDb 接口根地址，形如 https://www.omdbapi.com/。</summary>
    public string? OmdbBaseUrl { get; set; }

    /// <summary>图片代理前缀：图片域名被拦时用它兜底，形如 https://wsrv.nl/?url= 。</summary>
    public string? ImageProxyBaseUrl { get; set; }

    // ---------- 缓存（设置页「缓存管理」分区） ----------

    /// <summary>元数据缓存有效期（分钟）；0 = 不缓存，每次都向服务器请求。</summary>
    public int MetadataCacheMinutes { get; set; } = 10;

    /// <summary>元数据缓存上限（MB）；0 = 不限制。</summary>
    public int MetadataCacheLimitMb { get; set; } = 512;

    /// <summary>图片与网络缓存上限（MB）；0 = 不限制。</summary>
    public int ImageCacheLimitMb { get; set; } = 1024;

    /// <summary>弹幕缓存上限（MB）；0 = 不限制。</summary>
    public int DanmakuCacheLimitMb { get; set; } = 128;

    /// <summary>播放缓存（外挂字幕等）上限（MB）；0 = 不限制。</summary>
    public int PlaybackCacheLimitMb { get; set; } = 256;

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

    /// <summary>
    /// OMDb 官方接口地址（调用形式同 ?i=tt3896198&amp;apikey=xxx）。
    /// 默认走 https：http 明文在部分网络下会被中间设备直接拦成 403。
    /// </summary>
    public const string OmdbOfficialBaseUrl = "https://www.omdbapi.com/";

    /// <summary>
    /// 内置的 OMDb 公共 Key：用户没填自己的 Key 时用它取 IMDb 评分。
    /// 换成自己的 Key（omdbapi.com 免费申请，1000 次/天）不受此额度影响。
    /// </summary>
    public const string DefaultOmdbApiKey = "3b168f2e";

    /// <summary>
    /// 把用户填的 OMDb 地址整理成「根地址 + Key」。
    /// 容错点：很多人会直接把整条调用链接粘进来
    /// （<c>http://www.omdbapi.com/?i=tt3896198&amp;apikey=xxx</c>），
    /// 这时原样当 base 拼会得到 <c>?i=..&amp;apikey=/?apikey=xxx</c>，服务器只能看到空 key 并返回 401。
    /// 所以这里把 query 里的 apikey 抽出来、只保留 scheme://host/ 作为根地址。
    /// </summary>
    public static (string BaseUrl, string? ApiKey) NormalizeOmdbEndpoint(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return (OmdbOfficialBaseUrl, null);
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return (value.TrimEnd('/') + "/", null);
        }

        string? key = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("apikey", StringComparison.OrdinalIgnoreCase))
            {
                // Key 里混进零宽字符 / 空格时 OMDb 一律当无效
                key = new string(Uri.UnescapeDataString(kv[1]).Where(char.IsLetterOrDigit).ToArray());
            }
        }

        return ($"{uri.Scheme}://{uri.Authority}/", string.IsNullOrEmpty(key) ? null : key);
    }

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
        MigrateOmdbEndpoint();
        if (string.IsNullOrEmpty(Current.DeviceId))
        {
            Current.DeviceId = Guid.NewGuid().ToString("N");
            Save();
        }
    }

    /// <summary>
    /// 旧配置把整条 OMDb 调用链接填进了「接口地址」：拆成干净的根地址 + Key，
    /// 否则每次请求都会带着 <c>?i=..&amp;apikey=</c> 前缀，服务器返回 401 Invalid API key。
    /// </summary>
    private void MigrateOmdbEndpoint()
    {
        var raw = Current.OmdbBaseUrl;
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains('?', StringComparison.Ordinal))
        {
            return;
        }

        var (baseUrl, key) = NormalizeOmdbEndpoint(raw);
        Current.OmdbBaseUrl = baseUrl.Equals(OmdbOfficialBaseUrl, StringComparison.OrdinalIgnoreCase)
            ? null
            : baseUrl;
        if (!string.IsNullOrEmpty(key) && string.IsNullOrWhiteSpace(Current.OmdbApiKey))
        {
            Current.OmdbApiKey = key;
        }

        Save();
    }

    public ServerEntry? CurrentServer =>
        Current.Servers.FirstOrDefault(s => s.Id == Current.CurrentServerId)
        ?? Current.Servers.FirstOrDefault();

    public bool IsLoggedIn =>
        CurrentServer is not null && !string.IsNullOrEmpty(CurrentServer.AccessToken);

    /// <summary>首页内容源是否为 TMDB 在线推荐（已添加服务器的用户也可随时切过去）。</summary>
    public bool IsTmdbSource =>
        string.Equals(Current.SourceMode, TmdbSourceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 当前内容源的唯一标识（服务器 ID，或 "tmdb"）。
    /// 首页用它判断「用户是不是换过内容源」，换过就重新拉一次首屏。
    /// </summary>
    public string CurrentSourceKey => IsTmdbSource ? TmdbSourceId : CurrentServer?.Id ?? "none";

    /// <summary>内容源下拉项：「在线服务」在前，Emby 媒体服务器在后。</summary>
    public List<ContentSourceOption> SourceOptions
    {
        get
        {
            // 在线服务（TMDB，未来可继续加其它 API）排在最上面：
            // 没有服务器的人先看到它，有服务器的人也能一眼切到在线推荐
            var options = new List<ContentSourceOption>
            {
                new() { Id = TmdbSourceId, DisplayName = "TMDB 在线推荐", IsOnline = true }
            };
            options.AddRange(Current.Servers.Select(s => new ContentSourceOption
            {
                Id = s.Id,
                DisplayName = s.DisplayName,
                Server = s
            }));
            return options;
        }
    }

    /// <summary>当前内容源选项（用于下拉框初始选中）。</summary>
    public ContentSourceOption? CurrentSourceOption =>
        IsTmdbSource
            ? new ContentSourceOption { Id = TmdbSourceId, DisplayName = "TMDB 在线推荐", IsOnline = true }
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

    /// <summary>改服务器别名（列表里显示的名字），不影响登录凭据。</summary>
    public void RenameServer(string serverId, string? name)
    {
        var entry = Current.Servers.FirstOrDefault(s => s.Id == serverId);
        if (entry is null)
        {
            return;
        }

        entry.ServerName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Save();
        ServersChanged?.Invoke();
    }

    /// <summary>
    /// 删除一个服务器：连同它保存的令牌一起从本机删除。
    /// 删的是当前服务器时自动切到下一个；一个都不剩就退回 TMDB 在线推荐。
    /// </summary>
    public void RemoveServer(string serverId)
    {
        var entry = Current.Servers.FirstOrDefault(s => s.Id == serverId);
        if (entry is null)
        {
            return;
        }

        var wasCurrent = Current.CurrentServerId == serverId;
        Current.Servers.Remove(entry);

        if (wasCurrent)
        {
            Current.CurrentServerId = Current.Servers.FirstOrDefault()?.Id;
        }

        if (Current.Servers.Count == 0)
        {
            Current.SourceMode = TmdbSourceId;
        }

        Save();
        ServersChanged?.Invoke();
    }

    /// <summary>搜索历史最多保留多少条。</summary>
    public const int MaxSearchHistory = 12;

    /// <summary>
    /// 记一个搜索词：去重后放到最前，超出上限的从末尾丢掉。
    /// 空白词直接忽略，免得历史里塞一堆空记录。
    /// </summary>
    public void RememberSearchTerm(string? term)
    {
        var value = term?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var history = Current.SearchHistory;
        history.RemoveAll(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, value);
        if (history.Count > MaxSearchHistory)
        {
            history.RemoveRange(MaxSearchHistory, history.Count - MaxSearchHistory);
        }

        Save();
    }

    /// <summary>删掉一条搜索历史（弹层里每条后面的叉）。</summary>
    public void RemoveSearchTerm(string? term)
    {
        var value = term?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (Current.SearchHistory.RemoveAll(
                t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
        }
    }

    /// <summary>清空搜索历史。</summary>
    public void ClearSearchHistory()
    {
        if (Current.SearchHistory.Count == 0)
        {
            return;
        }

        Current.SearchHistory.Clear();
        Save();
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
