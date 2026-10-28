using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Animations;
using EmbyPlayer.Services;
using EmbyPlayer.Views;
using Windows.System;

namespace EmbyPlayer.ViewModels;

/// <summary>
/// 设置页：左侧分类 + 右侧表单。所有可调项都直接落到 settings.json，
/// 其中 mpv 相关项在下次开始播放时转成命令行参数生效。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly NavigationService _navigation;
    private readonly EmbyApiClient _api;
    private readonly UpdateService _update;

    /// <summary>轮播间隔下拉的取值：0 表示不自动切换。</summary>
    private static readonly int[] HeroIntervals = { 8, 12, 20, 30, 0 };

    /// <summary>硬件解码下拉对应的 mpv hwdec 值。</summary>
    private static readonly string[] HwdecModes = { "auto-safe", "no", "d3d11va", "nvdec", "auto" };

    /// <summary>缓存下拉对应的秒数：0 表示关闭预读缓存。</summary>
    private static readonly int[] CacheSecondsOptions = { 0, 10, 30, 60, 120 };

    /// <summary>音轨语言下拉对应的 mpv alang 值；空串表示交给 mpv 自动选择。</summary>
    private static readonly string[] AudioLangOptions = { "", "chi,zh,zho", "yue", "jpn", "eng" };

    // ---------------- 通用 ----------------

    [ObservableProperty]
    private bool _autoPlayNext;

    [ObservableProperty]
    private bool _reduceMotion;

    [ObservableProperty]
    private int _heroIntervalIndex;

    /// <summary>颜色模式下拉：0 跟随系统 / 1 浅色 / 2 深色。</summary>
    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private ContentSourceOption? _selectedSource;

    // ---------------- mpv 播放器 ----------------

    [ObservableProperty]
    private string _mpvPath = "";

    [ObservableProperty]
    private string _mpvStatus = "";

    [ObservableProperty]
    private int _hwdecIndex;

    [ObservableProperty]
    private int _cacheIndex;

    [ObservableProperty]
    private double _volume = 100;

    [ObservableProperty]
    private int _audioLangIndex;

    [ObservableProperty]
    private double _subFontSize = 55;

    [ObservableProperty]
    private double _subBorderSize = 3;

    [ObservableProperty]
    private double _subPos = 100;

    [ObservableProperty]
    private string _mpvExtraArgs = "";

    // ---------------- 快捷键 ----------------

    /// <summary>播放器快捷键列表，顺序与 <see cref="MpvShortcuts.All"/> 一致。</summary>
    public ObservableCollection<ShortcutRow> Shortcuts { get; } = new();

    [ObservableProperty]
    private string _shortcutStatus = "";

    // ---------------- 字幕与弹幕 ----------------

    [ObservableProperty]
    private bool _danmakuEnabled;

    [ObservableProperty]
    private int _danmakuFontSize;

    [ObservableProperty]
    private double _danmakuOpacity;

    [ObservableProperty]
    private int _danmakuScrollDuration;

    [ObservableProperty]
    private double _danmakuDisplayArea;

    [ObservableProperty]
    private string _danmakuServerUrl = "";

    // ---------------- 在线内容 ----------------

    /// <summary>TMDB v3 API Key / v4 令牌：内容源切到 TMDB 时用它拉在线推荐，保存在本机，不进源码。</summary>
    [ObservableProperty]
    private string _tmdbApiKey = "";

    /// <summary>OMDb Key：详情页 IMDb 评分用；留空则不显示 IMDb 评分。</summary>
    [ObservableProperty]
    private string _omdbApiKey = "";

    // ---------------- 存储与缓存 ----------------

    [ObservableProperty]
    private string _cacheStatus = "";

    [ObservableProperty]
    private string _metadataCacheSize = "";

    [ObservableProperty]
    private string _imageCacheSize = "";

    [ObservableProperty]
    private string _danmakuCacheSize = "";

    [ObservableProperty]
    private string _playbackCacheSize = "";

    [ObservableProperty]
    private string _totalCacheSize = "";

    /// <summary>元数据有效期下拉的取值（分钟）：0 表示不缓存。</summary>
    private static readonly int[] MetadataTtlOptions = { 0, 5, 10, 30, 60 };

    [ObservableProperty]
    private int _metadataTtlIndex;

    [ObservableProperty]
    private double _metadataCacheLimitMb = 512;

    [ObservableProperty]
    private double _imageCacheLimitMb = 1024;

    [ObservableProperty]
    private double _danmakuCacheLimitMb = 128;

    [ObservableProperty]
    private double _playbackCacheLimitMb = 256;

    private bool _initialized;

    public SettingsViewModel(SettingsService settings, NavigationService navigation, EmbyApiClient api, UpdateService update)
    {
        _settings = settings;
        _navigation = navigation;
        _api = api;
        _update = update;

        Sources = settings.SourceOptions;

        _autoPlayNext = settings.Current.AutoPlayNext;
        _reduceMotion = settings.Current.ReduceMotion;
        _heroIntervalIndex = NearestIndex(HeroIntervals, settings.Current.HeroAutoPlaySeconds);
        _themeIndex = settings.Current.ThemeMode?.ToLowerInvariant() switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        _selectedSource = Sources.FirstOrDefault(o =>
                             o.Id == (settings.IsTmdbSource ? SettingsService.TmdbSourceId : settings.CurrentServer?.Id))
                         ?? Sources.LastOrDefault();

        _mpvPath = settings.Current.MpvPath ?? "";
        _hwdecIndex = Math.Max(0, Array.FindIndex(HwdecModes,
            m => string.Equals(m, settings.Current.MpvHwdec, StringComparison.OrdinalIgnoreCase)));
        _cacheIndex = NearestIndex(CacheSecondsOptions, settings.Current.MpvCacheSeconds);
        _volume = settings.Current.MpvVolume;
        _audioLangIndex = Math.Max(0, Array.FindIndex(AudioLangOptions,
            l => string.Equals(l, settings.Current.MpvAudioLang ?? "", StringComparison.OrdinalIgnoreCase)));
        _subFontSize = settings.Current.MpvSubFontSize;
        _subBorderSize = settings.Current.MpvSubBorderSize;
        _subPos = settings.Current.MpvSubPos;
        _mpvExtraArgs = settings.Current.MpvExtraArgs ?? "";

        _danmakuEnabled = settings.Current.DanmakuEnabled;
        _danmakuFontSize = settings.Current.DanmakuFontSize;
        _danmakuOpacity = settings.Current.DanmakuOpacity;
        _danmakuScrollDuration = settings.Current.DanmakuScrollDuration;
        _danmakuDisplayArea = settings.Current.DanmakuDisplayArea;
        _danmakuServerUrl = settings.Current.DanmakuServerUrl ?? "";

        _tmdbApiKey = settings.Current.TmdbApiKey ?? "";
        _omdbApiKey = settings.Current.OmdbApiKey ?? "";

        _tmdbBaseUrl = settings.Current.TmdbBaseUrl ?? "";
        _tmdbImageBaseUrl = settings.Current.TmdbImageBaseUrl ?? "";
        _omdbBaseUrl = settings.Current.OmdbBaseUrl ?? "";
        _imageProxyBaseUrl = settings.Current.ImageProxyBaseUrl ?? "";
        BuildExternalLinks();

        _metadataTtlIndex = NearestIndex(MetadataTtlOptions, settings.Current.MetadataCacheMinutes);
        _metadataCacheLimitMb = settings.Current.MetadataCacheLimitMb;
        _imageCacheLimitMb = settings.Current.ImageCacheLimitMb;
        _danmakuCacheLimitMb = settings.Current.DanmakuCacheLimitMb;
        _playbackCacheLimitMb = settings.Current.PlaybackCacheLimitMb;

        _reduceMotion = settings.Current.ReduceMotion;
        FluidMotion.ReduceMotion = _reduceMotion;

        BuildShortcuts();

        _initialized = true;
        RefreshMpvStatus();
        RefreshCacheStatus();
    }

    /// <summary>内容源下拉：已添加的 Emby 服务器 + 「TMDB 在线推荐」。</summary>
    public List<ContentSourceOption> Sources { get; }

    /// <summary>当前应用版本号（如 "v1.0.9"），与 csproj AssemblyVersion 自动同步。</summary>
    public string AppVersion => $"v{UpdateService.CurrentVersionString}";

    /// <summary>GitHub 仓库主页 URL（用于设置页「关于」展示）。</summary>
    public string RepositoryUrl => UpdateService.RepositoryUrl;

    // ---------------- 保存逻辑 ----------------

    partial void OnAutoPlayNextChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.AutoPlayNext = value;
        _settings.Save();
    }

    partial void OnReduceMotionChanged(bool value)
    {
        FluidMotion.ReduceMotion = value;
        if (!_initialized)
        {
            return;
        }
        _settings.Current.ReduceMotion = value;
        _settings.Save();
    }

    partial void OnHeroIntervalIndexChanged(int value)
    {
        if (!_initialized || value < 0 || value >= HeroIntervals.Length)
        {
            return;
        }
        _settings.Current.HeroAutoPlaySeconds = HeroIntervals[value];
        _settings.Save();
    }

    /// <summary>切换颜色模式：写入设置后由 MainWindow 统一应用（它也监听 SettingsChanged）。</summary>
    partial void OnThemeIndexChanged(int value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.ThemeMode = value switch
        {
            1 => "light",
            2 => "dark",
            _ => "system"
        };
        _settings.Save();
    }

    /// <summary>切换内容源：选服务器就同时切服务器与凭据，选 TMDB 就只切在线推荐。</summary>
    partial void OnSelectedSourceChanged(ContentSourceOption? value)
    {
        if (!_initialized || value is null)
        {
            return;
        }

        if (value.IsTmdb)
        {
            _settings.SetSourceMode(true);
            return;
        }

        if (value.Server is { } server)
        {
            _settings.SetSourceMode(false);
            _settings.SwitchServer(server.Id);
            if (!string.IsNullOrEmpty(server.AccessToken))
            {
                _api.Configure(server.Url, server.AccessToken);
            }
        }
    }

    partial void OnMpvPathChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.MpvPath = string.IsNullOrWhiteSpace(value) ? null : value;
        _settings.Save();
        RefreshMpvStatus();
    }

    partial void OnHwdecIndexChanged(int value) => SaveMpv();
    partial void OnCacheIndexChanged(int value) => SaveMpv();

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        SaveMpv();
    }

    /// <summary>音量滑块右侧的数值（0~100）。</summary>
    public string VolumeText => $"{Math.Round(Volume):0}";

    partial void OnAudioLangIndexChanged(int value) => SaveMpv();
    partial void OnSubFontSizeChanged(double value) => SaveMpv();
    partial void OnSubBorderSizeChanged(double value) => SaveMpv();
    partial void OnSubPosChanged(double value) => SaveMpv();
    partial void OnMpvExtraArgsChanged(string value) => SaveMpv();

    private void SaveMpv()
    {
        if (!_initialized)
        {
            return;
        }

        var s = _settings.Current;
        s.MpvHwdec = HwdecModes[Math.Clamp(HwdecIndex, 0, HwdecModes.Length - 1)];
        s.MpvCacheSeconds = CacheSecondsOptions[Math.Clamp(CacheIndex, 0, CacheSecondsOptions.Length - 1)];
        s.MpvVolume = (int)Math.Clamp(Math.Round(Volume), 0, 100);
        s.MpvAudioLang = AudioLangOptions[Math.Clamp(AudioLangIndex, 0, AudioLangOptions.Length - 1)];
        s.MpvSubFontSize = Math.Clamp(SubFontSize, 20, 120);
        s.MpvSubBorderSize = Math.Clamp(SubBorderSize, 0, 10);
        s.MpvSubPos = Math.Clamp(SubPos, 0, 150);
        s.MpvExtraArgs = string.IsNullOrWhiteSpace(MpvExtraArgs) ? null : MpvExtraArgs.Trim();
        _settings.Save();
    }

    partial void OnDanmakuEnabledChanged(bool value) => SaveDanmaku();
    partial void OnDanmakuFontSizeChanged(int value) => SaveDanmaku();
    partial void OnDanmakuOpacityChanged(double value) => SaveDanmaku();
    partial void OnDanmakuScrollDurationChanged(int value) => SaveDanmaku();
    partial void OnDanmakuDisplayAreaChanged(double value) => SaveDanmaku();
    partial void OnDanmakuServerUrlChanged(string value) => SaveDanmaku();

    partial void OnTmdbApiKeyChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.TmdbApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _settings.Save();
        SyncExternalLink(ExternalServiceId.Tmdb, value);
    }

    partial void OnOmdbApiKeyChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.OmdbApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _settings.Save();
        SyncExternalLink(ExternalServiceId.Omdb, value);
    }

    // ---------------- 外部服务（连接账号 / 填自己的 API） ----------------

    /// <summary>「外部服务」分区的卡片集合：TMDB、OMDb（IMDb 评分）。</summary>
    public List<ExternalLinkItem> ExternalLinks { get; } = new();

    /// <summary>TMDB 接口根地址；留空用官方 https://api.themoviedb.org/3。</summary>
    [ObservableProperty]
    private string _tmdbBaseUrl = "";

    /// <summary>TMDB 图片前缀；留空用官方 https://image.tmdb.org/t/p。</summary>
    [ObservableProperty]
    private string _tmdbImageBaseUrl = "";

    /// <summary>OMDb 接口根地址；留空用官方 https://www.omdbapi.com/。</summary>
    [ObservableProperty]
    private string _omdbBaseUrl = "";

    /// <summary>图片代理前缀；留空用 https://wsrv.nl/?url= 。</summary>
    [ObservableProperty]
    private string _imageProxyBaseUrl = "";

    partial void OnTmdbBaseUrlChanged(string value) => SaveApiUrl(nameof(TmdbBaseUrl), value);
    partial void OnTmdbImageBaseUrlChanged(string value) => SaveApiUrl(nameof(TmdbImageBaseUrl), value);
    partial void OnOmdbBaseUrlChanged(string value) => SaveApiUrl(nameof(OmdbBaseUrl), value);
    partial void OnImageProxyBaseUrlChanged(string value) => SaveApiUrl(nameof(ImageProxyBaseUrl), value);

    private void SaveApiUrl(string field, string value)
    {
        if (!_initialized)
        {
            return;
        }

        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        switch (field)
        {
            case nameof(TmdbBaseUrl):
                _settings.Current.TmdbBaseUrl = trimmed;
                break;
            case nameof(TmdbImageBaseUrl):
                _settings.Current.TmdbImageBaseUrl = trimmed;
                break;
            case nameof(OmdbBaseUrl):
                _settings.Current.OmdbBaseUrl = trimmed;
                break;
            case nameof(ImageProxyBaseUrl):
                _settings.Current.ImageProxyBaseUrl = trimmed;
                break;
        }

        _settings.Save();
    }

    /// <summary>四个接口地址全部恢复成官方默认值。</summary>
    [RelayCommand]
    private void RestoreDefaultApiUrls()
    {
        TmdbBaseUrl = "";
        TmdbImageBaseUrl = "";
        OmdbBaseUrl = "";
        ImageProxyBaseUrl = "";
    }

    private void BuildExternalLinks()
    {
        ExternalLinks.Clear();
        ExternalLinks.Add(new ExternalLinkItem(
            ExternalServiceId.Tmdb,
            "TMDB",
            "首页热门趋势 / 受欢迎的影片、详情页评分与海报",
            "API Key 或令牌",
            "32 位 v3 API Key，或 eyJ 开头的 v4 令牌",
            "https://www.themoviedb.org/settings/api",
            "去 TMDB 申请",
            _settings));
        ExternalLinks.Add(new ExternalLinkItem(
            ExternalServiceId.Omdb,
            "OMDb（IMDb 评分）",
            "详情页显示 IMDb 评分。留空则用内置公共 Key，填自己的 Key 不占公共额度",
            "OMDb Key",
            $"留空用内置 Key（{SettingsService.DefaultOmdbApiKey}），或填自己的 8 位 Key",
            "https://www.omdbapi.com/apikey.aspx",
            "去 OMDb 申请",
            _settings,
            SettingsService.DefaultOmdbApiKey));
    }

    /// <summary>在「在线内容」里改 Key 时同步「外部服务」卡片的状态。</summary>
    private void SyncExternalLink(ExternalServiceId id, string value)
    {
        var item = ExternalLinks.FirstOrDefault(l => l.Id == id);
        if (item is null)
        {
            return;
        }

        if (!string.Equals(item.Credential, value, StringComparison.Ordinal))
        {
            item.Credential = value ?? "";
        }
        else
        {
            item.Refresh();
        }
    }

    private void SaveDanmaku()
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.DanmakuEnabled = DanmakuEnabled;
        _settings.Current.DanmakuFontSize = DanmakuFontSize;
        _settings.Current.DanmakuOpacity = DanmakuOpacity;
        _settings.Current.DanmakuScrollDuration = DanmakuScrollDuration;
        _settings.Current.DanmakuDisplayArea = DanmakuDisplayArea;
        _settings.Current.DanmakuServerUrl = string.IsNullOrWhiteSpace(DanmakuServerUrl) ? null : DanmakuServerUrl;
        _settings.Save();
    }

    // ---------------- mpv ----------------

    [RelayCommand]
    private void DetectMpv()
    {
        var found = MpvLocator.Find(_settings);
        if (found is null)
        {
            MpvStatus = "未找到 mpv，请手动输入完整路径。";
        }
        else
        {
            MpvPath = found;
            RefreshMpvStatus();
        }
    }

    /// <summary>把 mpv 相关项恢复成默认值（相当于清掉所有自定义参数）。</summary>
    [RelayCommand]
    private void ResetMpv()
    {
        HwdecIndex = 0;
        CacheIndex = NearestIndex(CacheSecondsOptions, 30);
        Volume = 100;
        AudioLangIndex = 0;
        SubFontSize = 55;
        SubBorderSize = 3;
        SubPos = 100;
        MpvExtraArgs = "";
        MpvStatus = "已恢复默认播放参数（路径未改动）。";
    }

    private void RefreshMpvStatus()
    {
        var resolved = MpvLocator.Find(_settings);
        MpvStatus = resolved is null
            ? "当前不可用：未找到 mpv。"
            : $"当前使用：{resolved}";
    }

    // ---------------- 快捷键 ----------------

    private void BuildShortcuts()
    {
        Shortcuts.Clear();
        foreach (var definition in MpvShortcuts.All)
        {
            var tokens = MpvShortcuts.JoinKeys(MpvShortcuts.ResolveTokens(_settings.Current, definition));
            Shortcuts.Add(new ShortcutRow(definition, tokens));
        }
    }

    /// <summary>
    /// 给某个功能设置新按键（keys 为空表示取消绑定），并把它从其它功能上摘掉——
    /// mpv 一个键只能绑一条命令，重复键留着只会让行为变得难以预料。
    /// </summary>
    public void SetShortcut(ShortcutRow row, IEnumerable<string> keys)
    {
        var next = keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cleared = new List<string>();
        foreach (var other in Shortcuts)
        {
            if (ReferenceEquals(other, row))
            {
                continue;
            }
            var current = MpvShortcuts.SplitKeys(other.Tokens);
            var kept = current.Where(k => !next.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            if (kept.Count == current.Count)
            {
                continue;
            }
            other.Tokens = MpvShortcuts.JoinKeys(kept);
            if (kept.Count == 0)
            {
                cleared.Add(other.Label);
            }
        }

        row.Tokens = MpvShortcuts.JoinKeys(next);
        PersistShortcuts();

        var display = MpvShortcuts.DisplayText(row.Tokens);
        ShortcutStatus = cleared.Count == 0
            ? $"「{row.Label}」已设为 {display}，下次开始播放时生效。"
            : $"「{row.Label}」已设为 {display}；{string.Join("、", cleared)} 的重复键已被解除。";
    }

    /// <summary>把当前按键写回设置；和默认值相同的项不落盘，设置文件保持干净。</summary>
    private void PersistShortcuts()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Shortcuts)
        {
            var definition = MpvShortcuts.Find(row.Id);
            if (definition is null)
            {
                continue;
            }
            var defaults = MpvShortcuts.JoinKeys(MpvShortcuts.SplitKeys(definition.DefaultKeys));
            if (string.Equals(row.Tokens, defaults, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            map[row.Id] = row.Tokens;
        }

        _settings.Current.MpvKeyBindings = map;
        _settings.Save();
    }

    [RelayCommand]
    private void ResetShortcuts()
    {
        _settings.Current.MpvKeyBindings.Clear();
        _settings.Save();
        BuildShortcuts();
        ShortcutStatus = "已恢复默认快捷键，下次开始播放时生效。";
    }

    // ---------------- 存储与缓存 ----------------

    [RelayCommand]
    private void ClearImageCache()
    {
        ImageCacheService.Clear();
        CacheStatus = "图片与网络缓存已清空，重新浏览时会重新下载。";
        RefreshCacheStatus();
    }

    [RelayCommand]
    private void ClearMetadataCache()
    {
        var removed = CacheService.Clear(CacheKind.Metadata);
        CacheStatus = $"已清理 {removed} 个元数据缓存文件，下次浏览会重新向服务器请求。";
        RefreshCacheStatus();
    }

    [RelayCommand]
    private void ClearDanmakuCache()
    {
        var removed = CacheService.Clear(CacheKind.Danmaku);
        CacheStatus = $"已清理 {removed} 个弹幕缓存文件。";
        RefreshCacheStatus();
    }

    [RelayCommand]
    private void ClearPlaybackCache()
    {
        var removed = CacheService.Clear(CacheKind.Playback);
        CacheStatus = $"已清理 {removed} 个播放缓存文件（外挂字幕 / 播放脚本）。";
        RefreshCacheStatus();
    }

    [RelayCommand]
    private void ClearAllCaches()
    {
        ImageCacheService.Clear();
        var removed = CacheService.ClearAll();
        CacheStatus = $"已清理全部缓存，共 {removed} 个文件。";
        RefreshCacheStatus();
    }

    [RelayCommand]
    private void OpenCacheFolder()
    {
        Directory.CreateDirectory(CacheService.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", CacheService.Root) { UseShellExecute = true });
    }

    /// <summary>重新统计四类缓存的体积与文件数。</summary>
    public void RefreshCacheStatus()
    {
        var metadata = CacheService.Measure(CacheKind.Metadata);
        var images = CacheService.Measure(CacheKind.Images);
        var danmaku = CacheService.Measure(CacheKind.Danmaku);
        var playback = CacheService.Measure(CacheKind.Playback);

        MetadataCacheSize = Format(metadata);
        ImageCacheSize = Format(images);
        DanmakuCacheSize = Format(danmaku);
        PlaybackCacheSize = Format(playback);

        var total = metadata.Bytes + images.Bytes + danmaku.Bytes + playback.Bytes;
        TotalCacheSize = total == 0
            ? "暂无缓存"
            : $"共 {DownloadProgress.FormatBytes(total)}（{CacheService.Root}）";
    }

    private static string Format((long Bytes, int Count) measure) =>
        measure.Count == 0
            ? "空"
            : $"{DownloadProgress.FormatBytes(measure.Bytes)} · {measure.Count} 个文件";

    partial void OnMetadataTtlIndexChanged(int value)
    {
        if (!_initialized || value < 0 || value >= MetadataTtlOptions.Length)
        {
            return;
        }
        _settings.Current.MetadataCacheMinutes = MetadataTtlOptions[value];
        _settings.Save();
    }

    partial void OnMetadataCacheLimitMbChanged(double value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.MetadataCacheLimitMb = (int)Math.Round(value);
        _settings.Save();
        CacheService.Trim(CacheKind.Metadata, CacheService.ToBytes(value));
        RefreshCacheStatus();
    }

    partial void OnImageCacheLimitMbChanged(double value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.ImageCacheLimitMb = (int)Math.Round(value);
        _settings.Save();
        CacheService.Trim(CacheKind.Images, CacheService.ToBytes(value));
        RefreshCacheStatus();
    }

    partial void OnDanmakuCacheLimitMbChanged(double value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.DanmakuCacheLimitMb = (int)Math.Round(value);
        _settings.Save();
        CacheService.Trim(CacheKind.Danmaku, CacheService.ToBytes(value));
        RefreshCacheStatus();
    }

    partial void OnPlaybackCacheLimitMbChanged(double value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.PlaybackCacheLimitMb = (int)Math.Round(value);
        _settings.Save();
        CacheService.Trim(CacheKind.Playback, CacheService.ToBytes(value));
        RefreshCacheStatus();
    }

    // ---------------- 关于 ----------------

    /// <summary>打开 GitHub 仓库主页。</summary>
    [RelayCommand]
    private async Task OpenGitHubAsync()
    {
        await Launcher.LaunchUriAsync(new Uri(UpdateService.RepositoryUrl));
    }

    [RelayCommand]
    private async Task OpenTmdbAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.themoviedb.org/settings/api"));
    }

    /// <summary>打开 OMDb 注册页（免费 Key，填了才显示 IMDb 评分）。</summary>
    [RelayCommand]
    private async Task OpenOmdbAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.omdbapi.com/apikey.aspx"));
    }

    [ObservableProperty]
    private bool _isCheckingUpdate;

    /// <summary>手动检查更新；查询 GitHub Release 后通过 MainWindow 弹出更新对话框。</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdate))]
    private async Task CheckUpdateAsync()
    {
        if (_navigation.Shell is null)
        {
            return;
        }
        IsCheckingUpdate = true;
        CheckUpdateCommand.NotifyCanExecuteChanged();
        try
        {
            var update = await _update.CheckForUpdateAsync();
            // 弹窗必须在 UI 线程；async/await 在 WinUI 默认回到 UI 线程
            await _navigation.Shell.ShowUpdateDialogAsync(update, isAutoCheck: false);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog($"手动检查更新失败：{ex}");
        }
        finally
        {
            IsCheckingUpdate = false;
            CheckUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCheckUpdate() => !IsCheckingUpdate;

    /// <summary>在下拉取值里找最接近的一项，避免设置文件被手改后索引越界。</summary>
    private static int NearestIndex(int[] values, int current)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (Math.Abs(values[i] - current) < Math.Abs(values[best] - current))
            {
                best = i;
            }
        }
        return best;
    }
}
