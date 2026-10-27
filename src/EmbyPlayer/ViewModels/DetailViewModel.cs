using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Models;
using EmbyPlayer.Services;
using Microsoft.UI.Xaml.Media;

namespace EmbyPlayer.ViewModels;

public record SubtitleOption(int StreamIndex, string Label, MediaStreamInfo? Stream);

public partial class DetailViewModel : ObservableObject
{
    private readonly EmbyApiClient _api;
    private readonly NavigationService _navigation;
    private readonly PlaybackService _playback;
    private readonly DanmakuService _danmaku;
    private readonly SettingsService _settings;
    private readonly SynchronizationContext? _uiContext;

    private PlaybackPlan? _plan;

    /// <summary>剧集详情页的续播目标（来自「最近播放」列表记录的那一集）。</summary>
    private ResumeEpisode? _resumeEpisode;

    // 弹幕源：手动选择的 XML 文件（优先），或从弹幕服务器自动获取的结果
    private string? _danmakuXmlPath;
    private List<DanmakuEntry>? _autoFetchedDanmaku;

    [ObservableProperty]
    private BaseItemDto? _item;

    [ObservableProperty]
    private ObservableCollection<BaseItemDto> _seasons = new();

    [ObservableProperty]
    private BaseItemDto? _selectedSeason;

    [ObservableProperty]
    private ObservableCollection<BaseItemDto> _episodes = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPreparingPlayback;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private List<SubtitleOption> _subtitleOptions = new();

    [ObservableProperty]
    private SubtitleOption? _selectedSubtitle;

    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>
    /// 详情页海报。与列表卡片共用 ImageCacheService 里的同一个 BitmapImage，
    /// 所以从影片墙点进来时海报是「已经在内存里」的，不会再走一次网络与解码。
    /// </summary>
    [ObservableProperty]
    private ImageSource? _posterSource;

    /// <summary>
    /// 详情页背景：影片背景图（没有背景图时退回海报）。
    /// 只铺在页面上半部分，所以按中等宽度请求即可，放大倍数小、不再糊成一片。
    /// </summary>
    [ObservableProperty]
    private ImageSource? _backdropSource;

    /// <summary>
    /// 背景请求与解码宽度。海报统一按 300 解码（够用且省内存），
    /// 但背景要放大到半个页面宽，300 会糊，所以单独走接近原图的尺寸。
    /// </summary>
    private const int BackdropDecodeWidth = 1280;

    public Microsoft.UI.Xaml.Visibility BackdropVisibility =>
        BackdropSource is not null
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnBackdropSourceChanged(ImageSource? value) =>
        OnPropertyChanged(nameof(BackdropVisibility));

    // 弹幕 UI 绑定
    [ObservableProperty]
    private bool _danmakuEnabled;

    [ObservableProperty]
    private string _danmakuFileName = "未选择";

    [ObservableProperty]
    private string _danmakuStatus = "";

    /// <summary>由页面订阅，弹出字幕选择对话框；用户确认后调用 ConfirmPlayAsync。</summary>
    public event Func<Task>? RequestSubtitleChoice;

    /// <summary>由页面调用：用户选好弹幕 XML 文件后设置路径并更新显示名。</summary>
    public void SetDanmakuFile(string path)
    {
        _danmakuXmlPath = path;
        _autoFetchedDanmaku = null; // 手动选择优先，清除自动获取结果
        DanmakuFileName = string.IsNullOrEmpty(path) ? "未选择" : Path.GetFileName(path);
    }

    public DetailViewModel(EmbyApiClient api, NavigationService navigation, PlaybackService playback,
        DanmakuService danmaku, SettingsService settings)
    {
        _api = api;
        _navigation = navigation;
        _playback = playback;
        _danmaku = danmaku;
        _settings = settings;
        _uiContext = SynchronizationContext.Current;

        _playback.StatusChanged += s => PostToUi(() => StatusText = s);
        _playback.PlaybackEnded += () => PostToUi(() => _ = ReloadAsync());
    }

    public bool IsSeries => Item?.Type is "Series" or "Season";
    public bool IsMovie => Item?.Type is "Movie" or "Video";

    public string MetaText
    {
        get
        {
            var item = Item;
            if (item is null)
            {
                return "";
            }
            var parts = new List<string>();
            if (item.ProductionYear is int year)
            {
                parts.Add(year.ToString());
            }
            if (item.RunTimeTicks is long ticks && ticks > 0)
            {
                parts.Add(FormatDuration(ticks));
            }
            return string.Join(" · ", parts);
        }
    }

    public string PlayButtonText
    {
        get
        {
            // 从「最近播放」进来时，剧集详情页直接续播上次那一集
            if (_resumeEpisode is { } resume)
            {
                return string.IsNullOrWhiteSpace(resume.Label) ? "继续播放" : $"继续播放 {resume.Label}";
            }
            var ticks = Item?.UserData?.PlaybackPositionTicks ?? 0;
            return ticks > 0 ? $"继续播放（{FormatDuration(ticks)}）" : "播放";
        }
    }

    public Microsoft.UI.Xaml.Visibility EpisodesVisibility =>
        IsSeries ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string FavoriteButtonText => IsFavorite ? "♥ 已收藏" : "♡ 收藏";

    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteButtonText));

    private static string FormatDuration(long ticks)
    {
        var ts = TimeSpan.FromTicks(ticks);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分钟"
            : $"{ts.Minutes} 分钟";
    }

    /// <summary>
    /// 取详情页背景图：优先自己的背景图 → 上级（剧集）背景图 → 海报。
    /// 三者都按 BackdropDecodeWidth 请求，铺满页面时放大成柔化底纹；都没有则返回 null（不显示背景）。
    /// </summary>
    private ImageSource? BuildBackdrop(BaseItemDto item, string? primaryTag)
    {
        if (item.BackdropImageTags?.FirstOrDefault() is { } tag)
        {
            return ImageCacheService.Get(_api.GetBackdropImageUrl(item.Id, tag, BackdropDecodeWidth),
                decodeWidth: BackdropDecodeWidth);
        }

        if (!string.IsNullOrEmpty(item.ParentBackdropItemId) &&
            item.ParentBackdropImageTags?.FirstOrDefault() is { } parentTag)
        {
            return ImageCacheService.Get(
                _api.GetBackdropImageUrl(item.ParentBackdropItemId!, parentTag, BackdropDecodeWidth),
                decodeWidth: BackdropDecodeWidth);
        }

        return primaryTag is null
            ? null
            : ImageCacheService.Get(_api.GetPrimaryImageUrl(item.Id, primaryTag, BackdropDecodeWidth),
                decodeWidth: BackdropDecodeWidth);
    }

    public async Task LoadAsync(string itemId)
    {
        IsLoading = true;
        ErrorMessage = null;
        BackdropSource = null;

        // 先用列表里已经解析过的海报 URL 顶上：命中缓存的话这一帧就能显示，
        // 不用等 GetItemAsync 回来，也不会出现「先空框、后蹦出图」。
        PosterSource = ImageCacheService.Get(ItemCardMapper.TryGetPosterUrl(itemId));

        try
        {
            var item = await _api.GetItemAsync(itemId);
            Item = item;
            IsFavorite = item.UserData?.IsFavorite == true;
            var primaryTag = item.ImageTags is not null && item.ImageTags.TryGetValue("Primary", out var tag)
                ? tag : null;
            // 与列表走同一个宽度常量，保证 URL 一致 → 命中同一份缓存
            PosterSource = primaryTag is null
                ? null
                : ImageCacheService.Get(_api.GetPrimaryImageUrl(item.Id, primaryTag));
            BackdropSource = BuildBackdrop(item, primaryTag);
            OnPropertyChanged(nameof(IsSeries));
            OnPropertyChanged(nameof(IsMovie));
            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(EpisodesVisibility));

            if (item.Type == "Series")
            {
                _resumeEpisode = ItemCardMapper.TryGetResumeEpisode(item.Id);
                var seasons = await _api.GetSeasonsAsync(item.Id);
                Seasons = new ObservableCollection<BaseItemDto>(seasons.Items);
                SelectedSeason = Seasons.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ReloadAsync()
    {
        var id = Item?.Id;
        if (id is not null)
        {
            await LoadAsync(id);
        }
    }

    partial void OnSelectedSeasonChanged(BaseItemDto? value)
    {
        if (value is not null && Item is not null)
        {
            _ = LoadEpisodesAsync();
        }
    }

    private async Task LoadEpisodesAsync()
    {
        var item = Item;
        var season = SelectedSeason;
        if (item is null || season is null)
        {
            return;
        }
        try
        {
            var episodes = await _api.GetEpisodesAsync(item.Id, season.Id);
            Episodes = new ObservableCollection<BaseItemDto>(episodes.Items);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (Item is null)
        {
            return;
        }

        if (!IsSeries)
        {
            await PreparePlaybackAsync(Item);
            return;
        }

        // 有续播记录时直接播上次那一集（其 UserData 带断点位置，mpv 会从断点开始）
        if (_resumeEpisode is { } resume)
        {
            try
            {
                await PreparePlaybackAsync(await _api.GetItemAsync(resume.EpisodeId));
                return;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                return;
            }
        }

        await PreparePlaybackAsync(Episodes.FirstOrDefault() ?? Item);
    }

    [RelayCommand]
    private Task PlayEpisodeAsync(BaseItemDto? episode) =>
        episode is null ? Task.CompletedTask : PreparePlaybackAsync(episode);

    public void GoBack() => _navigation.GoBack();

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        var item = Item;
        if (item is null)
        {
            return;
        }
        try
        {
            var newValue = !IsFavorite;
            await _api.SetFavoriteAsync(item.Id, newValue);
            IsFavorite = newValue;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task PreparePlaybackAsync(BaseItemDto target)
    {
        ErrorMessage = null;
        IsPreparingPlayback = true;
        try
        {
            _plan = await _playback.PrepareAsync(target);
            var options = new List<SubtitleOption> { new(-1, "无字幕", null) };
            options.AddRange(_plan.SubtitleStreams.Select(s =>
                new SubtitleOption(s.Index,
                    string.IsNullOrEmpty(s.DisplayTitle)
                        ? (string.IsNullOrEmpty(s.Language) ? $"字幕 #{s.Index}" : s.Language)
                        : s.DisplayTitle,
                    s)));
            SubtitleOptions = options;
            SelectedSubtitle = options.FirstOrDefault(o => o.Stream?.IsDefault == true) ?? options[0];

            // 初始化弹幕选项：从设置读取开关，清空上次手动选择，尝试从服务器自动获取
            DanmakuEnabled = _settings.Current.DanmakuEnabled;
            _danmakuXmlPath = null;
            _autoFetchedDanmaku = null;
            DanmakuFileName = "未选择";
            DanmakuStatus = "";
            await TryAutoFetchDanmakuAsync(target);

            IsPreparingPlayback = false;
            if (RequestSubtitleChoice is not null)
            {
                await RequestSubtitleChoice.Invoke();
            }
            else
            {
                await ConfirmPlayAsync();
            }
        }
        catch (Exception ex)
        {
            IsPreparingPlayback = false;
            _plan = null;
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>若配置了弹幕服务器 URL，尝试用标题自动获取弹幕。失败静默忽略。</summary>
    private async Task TryAutoFetchDanmakuAsync(BaseItemDto target)
    {
        var serverUrl = _settings.Current.DanmakuServerUrl;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return;
        }
        try
        {
            DanmakuStatus = "正在从弹幕服务器获取…";
            if (serverUrl.Contains("{title}", StringComparison.OrdinalIgnoreCase))
            {
                // 兼容旧版：URL 模板直接返回 bilibili XML
                var title = Uri.EscapeDataString(target.Name);
                var url = serverUrl.Replace("{title}", title, StringComparison.OrdinalIgnoreCase);
                _autoFetchedDanmaku = await _danmaku.ParseUrlAsync(url);
            }
            else
            {
                // LogVar 弹幕 API：search/anime -> bangumi -> comment?format=xml
                var isEpisode = target.Type == "Episode";
                var searchTitle = isEpisode && !string.IsNullOrWhiteSpace(target.SeriesName)
                    ? target.SeriesName!
                    : target.Name;
                var episodeNumber = isEpisode ? target.IndexNumber : null;
                _autoFetchedDanmaku = await _danmaku.FetchFromLogVarAsync(
                    serverUrl, searchTitle, season: null, episode: episodeNumber);
            }
            DanmakuStatus = _autoFetchedDanmaku.Count > 0
                ? $"弹幕服务器返回 {_autoFetchedDanmaku.Count} 条弹幕"
                : "弹幕服务器未返回弹幕";
        }
        catch (Exception ex)
        {
            _autoFetchedDanmaku = null;
            DanmakuStatus = $"弹幕服务器获取失败：{ex.Message}";
        }
    }

    /// <summary>用户在对话框中切换弹幕开关后清除手动选择时调用。</summary>
    public void ClearDanmakuSelection()
    {
        _danmakuXmlPath = null;
        DanmakuFileName = "未选择";
    }

    public async Task ConfirmPlayAsync()
    {
        var plan = _plan;
        _plan = null;
        if (plan is null)
        {
            return;
        }
        try
        {
            string? danmakuAssPath = null;
            if (DanmakuEnabled)
            {
                danmakuAssPath = await PrepareDanmakuAssAsync(plan.Item);
            }
            await _playback.PlayAsync(plan, SelectedSubtitle?.Stream, danmakuAssPath);
            StatusText = $"正在播放：{plan.Item.Name}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>将弹幕源（手动文件优先，否则用自动获取结果）转换为 ASS 字幕文件。</summary>
    private async Task<string?> PrepareDanmakuAssAsync(BaseItemDto item)
    {
        List<DanmakuEntry>? entries = null;
        if (!string.IsNullOrEmpty(_danmakuXmlPath))
        {
            entries = await Task.Run(() => _danmaku.ParseFile(_danmakuXmlPath!));
        }
        else if (_autoFetchedDanmaku is { Count: > 0 })
        {
            entries = _autoFetchedDanmaku;
        }

        if (entries is null || entries.Count == 0)
        {
            return null;
        }

        var opts = new DanmakuAssOptions
        {
            FontSize = _settings.Current.DanmakuFontSize,
            Opacity = _settings.Current.DanmakuOpacity,
            ScrollDuration = _settings.Current.DanmakuScrollDuration,
            DisplayArea = _settings.Current.DanmakuDisplayArea
        };
        return await Task.Run(() => _danmaku.ConvertToAssFile(entries, opts));
    }

    private void PostToUi(Action action)
    {
        if (_uiContext is not null)
        {
            _uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }
}
