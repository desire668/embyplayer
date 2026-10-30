using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Models;
using EmbyPlayer.Services;
using EmbyPlayer.Views;
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
    private readonly RatingsService _ratings;
    private readonly TmdbClient _tmdb;
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

    /// <summary>集列表的展示行（带缩略图与简介）。</summary>
    [ObservableProperty]
    private ObservableCollection<EpisodeRow> _episodeRows = new();

    /// <summary>第三方评分胶囊（TMDB / IMDb / Trakt，取不到的源不显示）。</summary>
    public ObservableCollection<RatingChip> Ratings { get; } = new();

    [ObservableProperty]
    private bool _hasRatings;

    /// <summary>分类胶囊（点击跳转分类页）。</summary>
    public ObservableCollection<GenreChip> Genres { get; } = new();

    // ---------------- 播放源 ----------------

    /// <summary>可切换的播放源（所有已添加的 Emby 服务器）。</summary>
    public ObservableCollection<PlaybackSourceOption> PlaybackSources { get; } = new();

    [ObservableProperty]
    private PlaybackSourceOption? _selectedPlaybackSource;

    /// <summary>只有一台服务器时没有可切换的，按钮没必要占位置。</summary>
    [ObservableProperty]
    private bool _hasMultiplePlaybackSources;

    public string PlaybackSourceText =>
        SelectedPlaybackSource is { } source ? $"播放源：{source.Name}" : "播放源";

    partial void OnSelectedPlaybackSourceChanged(PlaybackSourceOption? value) =>
        OnPropertyChanged(nameof(PlaybackSourceText));

    public Microsoft.UI.Xaml.Visibility PlaybackSourceVisibility =>
        HasMultiplePlaybackSources && !IsTmdbItem
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnHasMultiplePlaybackSourcesChanged(bool value) =>
        OnPropertyChanged(nameof(PlaybackSourceVisibility));

    // ---------------- 媒体信息 ----------------

    /// <summary>分辨率 / 码率 / 音轨语言 / 字幕语言等，进详情页时探测一次。</summary>
    public ObservableCollection<MediaChip> MediaChips { get; } = new();

    [ObservableProperty]
    private bool _hasMediaInfo;

    public Microsoft.UI.Xaml.Visibility MediaInfoVisibility =>
        HasMediaInfo ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnHasMediaInfoChanged(bool value) => OnPropertyChanged(nameof(MediaInfoVisibility));

    /// <summary>播其它服务器的片子时用的独立客户端；换源 / 换片时释放。</summary>
    private EmbyApiClient? _playbackClient;

    // ---------------- TMDB 在线条目 ----------------

    /// <summary>当前详情页展示的是 TMDB 在线条目（媒体库里没有）：不能播、不能收藏。</summary>
    [ObservableProperty]
    private bool _isTmdbItem;

    [ObservableProperty]
    private string _tmdbNotice = "";

    public Microsoft.UI.Xaml.Visibility TmdbNoticeVisibility =>
        string.IsNullOrWhiteSpace(TmdbNotice)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnTmdbNoticeChanged(string value) => OnPropertyChanged(nameof(TmdbNoticeVisibility));

    /// <summary>播放 / 播放源 / 收藏：TMDB 在线条目没有片源，整排按钮隐藏。</summary>
    public Microsoft.UI.Xaml.Visibility PlayVisibility =>
        IsTmdbItem ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnIsTmdbItemChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayVisibility));
        OnPropertyChanged(nameof(PlaybackSourceVisibility));
    }

    [ObservableProperty]
    private int _genreCount;

    public Microsoft.UI.Xaml.Visibility GenresVisibility =>
        GenreCount > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnGenreCountChanged(int value) => OnPropertyChanged(nameof(GenresVisibility));

    partial void OnHasRatingsChanged(bool value) =>
        OnPropertyChanged(nameof(RatingsVisibility));

    public Microsoft.UI.Xaml.Visibility RatingsVisibility =>
        HasRatings ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

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

    /// <summary>
    /// TMDB 的剧名 logo：拿到了就用它当详情页标题；拿不到（没有 TMDB id / 没有 API Key /
    /// 这部片没有 logo / 图加载失败）时保持 null，界面回落显示影片名文字。
    /// </summary>
    [ObservableProperty]
    private ImageSource? _titleLogo;

    public Microsoft.UI.Xaml.Visibility TitleLogoVisibility =>
        TitleLogo is not null
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>有 logo 时隐掉文字标题，避免两种标题叠在一起。</summary>
    public Microsoft.UI.Xaml.Visibility TitleTextVisibility =>
        TitleLogo is null
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnTitleLogoChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(TitleLogoVisibility));
        OnPropertyChanged(nameof(TitleTextVisibility));
    }

    /// <summary>异步取剧名 logo：拿到就替换文字标题，拿不到就当什么都没发生。</summary>
    private async Task LoadTitleLogoAsync(BaseItemDto item)
    {
        // 服务器自己刮到 Logo 图时直接用：图就在本地服务器上，不必再问外部接口
        if (item.ImageTags?.TryGetValue("Logo", out var logoTag) == true &&
            !string.IsNullOrWhiteSpace(logoTag))
        {
            var localUrl = _api.GetLogoImageUrl(item.Id, logoTag);
            PostToUi(() => TitleLogo = ImageCacheService.Get(localUrl, decodeWidth: EmbyApiClient.LogoWidth));
            return;
        }

        if (!_tmdb.HasApiKey)
        {
            return;
        }

        try
        {
            // 剧集 / 季 / 集在 TMDB 都归 tv
            var isTv = item.Type is "Series" or "Season" or "Episode";
            string? tmdbId = item.ProviderIds is not null &&
                             item.ProviderIds.TryGetValue("Tmdb", out var id) &&
                             !string.IsNullOrWhiteSpace(id)
                ? id
                : null;

            if (tmdbId is null)
            {
                // 没刮到 TMDB id：按片名（+年份）搜一次再用它的 id
                var name = item.Type == "Episode" && !string.IsNullOrWhiteSpace(item.SeriesName)
                    ? item.SeriesName!
                    : item.Name;
                var found = await _tmdb.SearchAsync(name, item.ProductionYear, isTv);
                if (found is null)
                {
                    return;
                }
                tmdbId = found.Id.ToString();
            }

            var path = await _tmdb.GetLogoPathAsync(isTv ? "tv" : "movie", tmdbId);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // 网络是慢的：期间可能已经翻到别的片子
            if (!ReferenceEquals(Item, item))
            {
                return;
            }

            var url = _tmdb.GetImageUrl(path, TmdbClient.LogoWidth);
            var fallback = _tmdb.GetFallbackImageUrl(path, TmdbClient.LogoWidth);
            PostToUi(() => TitleLogo = ImageCacheService.Get(url, fallback, TmdbClient.LogoWidth));
        }
        catch
        {
            // logo 只是锦上添花：拿不到就显示文字片名
        }
    }

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
        DanmakuService danmaku, SettingsService settings, RatingsService ratings, TmdbClient tmdb)
    {
        _api = api;
        _navigation = navigation;
        _playback = playback;
        _danmaku = danmaku;
        _settings = settings;
        _ratings = ratings;
        _tmdb = tmdb;
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
            if (!string.IsNullOrWhiteSpace(item.OfficialRating))
            {
                parts.Add(item.OfficialRating);
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
        TitleLogo = null;
        MediaChips.Clear();
        HasMediaInfo = false;
        IsTmdbItem = false;
        TmdbNotice = "";
        // 每次进入详情都重置续播目标：上一部片的续播记录不能带到这一部
        _resumeEpisode = null;

        // 先用列表里已经解析过的海报 URL 顶上：命中缓存的话这一帧就能显示，
        // 不用等 GetItemAsync 回来，也不会出现「先空框、后蹦出图」。
        PosterSource = ImageCacheService.Get(ItemCardMapper.TryGetPosterUrl(itemId));

        try
        {
            var item = await _api.GetItemAsync(itemId);

            // 「继续播放」进来的是单集（首页继续播放卡片存的是集 Id）：
            // 单集页没有季 / 集列表上下文，先记下要续播哪一集，再落到剧集详情页。
            // 集详情刚拉过一遍已进元数据缓存，PlayAsync 里再取那一集时不会再走网络。
            if (item.Type == "Episode" && !string.IsNullOrEmpty(item.SeriesId))
            {
                _resumeEpisode = new ResumeEpisode(item.Id, ItemCardMapper.BuildEpisodeLabel(item));
                item = await _api.GetItemAsync(item.SeriesId);
            }

            Item = item;
            ItemCardMapper.RememberName(item.Id, item.Name);
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

            ApplyGenres(item);
            RefreshPlaybackSources();
            // 评分要打外部接口，先让页面出内容，评分回来了再补上
            _ = LoadRatingsAsync(item);
            // 剧名 logo 同理：TMDB 的 logo 图拿到了就把文字标题换掉
            _ = LoadTitleLogoAsync(item);
            // 媒体信息（分辨率 / 码率 / 音轨 / 字幕）要问一次服务器，同样异步补
            if (!IsSeries)
            {
                _ = LoadMediaInfoAsync(item.Id, item);
            }

            if (item.Type == "Series")
            {
                // 刚才由单集跳转过来时已经记了续播目标，别把「最近播放」页存的旧记录盖掉
                _resumeEpisode ??= ItemCardMapper.TryGetResumeEpisode(item.Id);
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

    /// <summary>
    /// 打开 TMDB 在线条目：媒体库里没有这部片时，详情页照样用 TMDB 的数据渲染，
    /// 不再跳到 the moviedb 网站。没有片源，所以播放 / 收藏一排按钮都不出现。
    /// </summary>
    public async Task LoadTmdbAsync(int tmdbId)
    {
        IsLoading = true;
        ErrorMessage = null;
        BackdropSource = null;
        PosterSource = null;
        TitleLogo = null;
        MediaChips.Clear();
        HasMediaInfo = false;
        IsTmdbItem = true;
        Ratings.Clear();
        HasRatings = false;
        Genres.Clear();
        GenreCount = 0;
        Seasons = new ObservableCollection<BaseItemDto>();
        SelectedSeason = null;
        Episodes = new ObservableCollection<BaseItemDto>();
        EpisodeRows = new ObservableCollection<EpisodeRow>();
        PlaybackSources.Clear();
        HasMultiplePlaybackSources = false;
        _resumeEpisode = null; // TMDB 在线条目没有续播概念，清掉上一部片的记录
        TmdbNotice = _settings.IsLoggedIn
            ? "媒体库里没有这部片，下面是 TMDB 的在线信息。"
            : "当前没有连接媒体服务器，下面是 TMDB 的在线信息。";

        try
        {
            var detail = await _tmdb.GetMovieDetailAsync(tmdbId);
            if (detail is null)
            {
                ErrorMessage = "没有取到这部片的在线信息。";
                return;
            }

            int? year = detail.ReleaseDate?.Length >= 4 &&
                        int.TryParse(detail.ReleaseDate[..4], out var parsedYear) ? parsedYear : null;

            Item = new BaseItemDto
            {
                Id = $"tmdb:{tmdbId}",
                Name = string.IsNullOrWhiteSpace(detail.Title) ? detail.OriginalTitle ?? "" : detail.Title!,
                OriginalTitle = detail.OriginalTitle,
                Type = "Movie",
                Overview = detail.Overview,
                ProductionYear = year,
                RunTimeTicks = detail.Runtime is int minutes ? minutes * 600_000_000L : null,
                Genres = detail.Genres.Select(g => g.Name).ToList(),
                ProviderIds = new Dictionary<string, string> { ["Tmdb"] = tmdbId.ToString() }
            };
            ItemCardMapper.RememberName(Item.Id, Item.Name);
            OnPropertyChanged(nameof(IsSeries));
            OnPropertyChanged(nameof(IsMovie));
            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(EpisodesVisibility));

            if (!string.IsNullOrEmpty(detail.PosterPath))
            {
                PosterSource = ImageCacheService.Get(
                    _tmdb.GetImageUrl(detail.PosterPath, EmbyApiClient.PosterWidth),
                    _tmdb.GetFallbackImageUrl(detail.PosterPath, EmbyApiClient.PosterWidth));
            }
            if (!string.IsNullOrEmpty(detail.BackdropPath))
            {
                BackdropSource = ImageCacheService.Get(
                    _tmdb.GetImageUrl(detail.BackdropPath, BackdropDecodeWidth),
                    _tmdb.GetFallbackImageUrl(detail.BackdropPath, BackdropDecodeWidth));
            }

            ApplyGenres(Item);
            _ = LoadTitleLogoAsync(Item);
            if (detail.VoteAverage is double vote)
            {
                Ratings.Add(new RatingChip("TMDB", vote.ToString("0.0"),
                    detail.VoteCount is int vc ? $"TMDB {vote:0.0} · {vc:N0} 人评分" : $"TMDB {vote:0.0}"));
                HasRatings = true;
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

    /// <summary>把 Emby 的类型标签变成可点击的分类胶囊。</summary>
    private void ApplyGenres(BaseItemDto item)
    {
        Genres.Clear();
        foreach (var genre in item.Genres ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(genre))
            {
                Genres.Add(new GenreChip(genre));
            }
        }
        GenreCount = Genres.Count;
    }

    /// <summary>
    /// 刷新播放源列表。默认选中当前服务器；用户手动选过之后，只要那台服务器还在就保持原选择。
    /// </summary>
    private void RefreshPlaybackSources()
    {
        var servers = _settings.Current.Servers;
        var previousId = SelectedPlaybackSource?.ServerId;

        PlaybackSources.Clear();
        foreach (var server in servers)
        {
            PlaybackSources.Add(new PlaybackSourceOption
            {
                ServerId = server.Id,
                Name = server.DisplayName
            });
        }

        HasMultiplePlaybackSources = PlaybackSources.Count > 1;

        var keep = PlaybackSources.FirstOrDefault(s => s.ServerId == previousId);
        SelectedPlaybackSource = keep
            ?? PlaybackSources.FirstOrDefault(s => s.ServerId == _settings.CurrentServer?.Id)
            ?? PlaybackSources.FirstOrDefault();
    }

    /// <summary>切换播放源：后续播放走选中的那台服务器。</summary>
    [RelayCommand]
    private void SelectPlaybackSource(PlaybackSourceOption? option)
    {
        if (option is null || option.ServerId == SelectedPlaybackSource?.ServerId)
        {
            return;
        }
        SelectedPlaybackSource = option;
        StatusText = $"播放源已切换为：{option.Name}";
    }

    /// <summary>
    /// 探测媒体信息：分辨率 / 码率 / 音轨语言 / 字幕语言。
    /// 走 PlaybackInfo 但不真的开流（autoOpenLiveStream=false），拿到就补在标题下方。
    /// </summary>
    private async Task LoadMediaInfoAsync(string itemId, BaseItemDto item)
    {
        MediaChips.Clear();
        HasMediaInfo = false;
        try
        {
            var info = await _api.GetPlaybackInfoAsync(itemId, autoOpenLiveStream: false);
            var source = info.MediaSources.FirstOrDefault();
            if (source is null)
            {
                return;
            }

            // 探测是异步回来的：期间用户可能已经翻到别的片子
            if (!ReferenceEquals(Item, item))
            {
                return;
            }

            foreach (var chip in BuildMediaChips(source))
            {
                MediaChips.Add(chip);
            }
            HasMediaInfo = MediaChips.Count > 0;
        }
        catch
        {
            // 媒体信息只是补充，取不到就不显示
        }
    }

    /// <summary>详情页只放三项：分辨率 / 码率 / 大小。其余（编码、音轨、字幕、容器）不再显示。</summary>
    private static List<MediaChip> BuildMediaChips(MediaSourceInfo source)
    {
        var chips = new List<MediaChip>();
        var streams = source.MediaStreams ?? new List<MediaStreamInfo>();
        var video = streams.FirstOrDefault(s => s.Type == "Video");

        if (video?.Width is int w && video.Height is int h && w > 0 && h > 0)
        {
            chips.Add(new MediaChip("分辨率", $"{w}×{h}（{ResolutionLabel(h)}）"));
        }

        var bitrate = source.Bitrate ?? video?.BitRate;
        if (bitrate is long bits and > 0)
        {
            chips.Add(new MediaChip("码率", $"{bits / 1_000_000.0:0.##} Mbps"));
        }

        if (source.Size is long size and > 0)
        {
            chips.Add(new MediaChip("大小", FormatSize(size)));
        }

        return chips;
    }

    /// <summary>文件体积：GB / MB / KB 三档，小文件保留一位小数，避免出现「0 MB」。</summary>
    private static string FormatSize(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (bytes >= gb)
        {
            return $"{bytes / gb:0.##} GB";
        }
        if (bytes >= mb)
        {
            return bytes / mb >= 100 ? $"{bytes / mb:0} MB" : $"{bytes / mb:0.#} MB";
        }
        return $"{Math.Max(1, bytes / kb):0} KB";
    }

    private static string ResolutionLabel(int height) => height switch
    {
        >= 2160 => "4K",
        >= 1440 => "2K",
        >= 1080 => "1080P",
        >= 720 => "720P",
        >= 480 => "480P",
        _ => "SD"
    };

    /// <summary>拉第三方评分；拿不到的源直接跳过，全部拿不到时整行隐藏。</summary>
    private async Task LoadRatingsAsync(BaseItemDto item)
    {
        Ratings.Clear();
        HasRatings = false;
        try
        {
            var info = await _ratings.GetAsync(item);
            var chips = new List<RatingChip>();
            if (info.Tmdb is double tmdb)
            {
                chips.Add(new RatingChip("TMDB", tmdb.ToString("0.0"),
                    info.TmdbVotes is int tv ? $"TMDB {tmdb:0.0} · {tv:N0} 人评分" : $"TMDB {tmdb:0.0}"));
            }
            if (info.Imdb is double imdb)
            {
                chips.Add(new RatingChip("IMDb", imdb.ToString("0.0"),
                    info.ImdbVotes is int iv ? $"IMDb {imdb:0.0} · {iv:N0} 人评分" : $"IMDb {imdb:0.0}"));
            }
            if (info.Trakt is double trakt)
            {
                chips.Add(new RatingChip("Trakt", trakt.ToString("0.0"),
                    info.TrakVotes is int kv ? $"Trakt {trakt:0.0} · {kv:N0} 人评分" : $"Trakt {trakt:0.0}"));
            }

            // 评分是异步回来的：期间用户可能已经切到别的片子，别再回填
            if (!ReferenceEquals(Item, item))
            {
                return;
            }

            foreach (var chip in chips)
            {
                Ratings.Add(chip);
            }
            HasRatings = chips.Count > 0;
        }
        catch
        {
            // 评分只是附加信息，失败就不显示
        }
    }

    /// <summary>点分类胶囊：跳媒体库并按该类型过滤（电影进电影库，剧集进剧集库）。</summary>
    [RelayCommand]
    private void OpenGenre(GenreChip? chip)
    {
        if (chip is null || string.IsNullOrWhiteSpace(chip.Name))
        {
            return;
        }
        var collectionType = Item?.Type == "Series" ? "tvshows" : "movies";
        _navigation.NavigateToRoot<LibraryPage>(new LibraryFilter(chip.Name, collectionType));
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
            EpisodeRows = new ObservableCollection<EpisodeRow>(episodes.Items.Select(e => new EpisodeRow
            {
                Item = e,
                Thumbnail = BuildEpisodeThumbnail(e)
            }));

            // 剧集本身没有媒体源：拿本季第一集去探测，展示这部的画质与音轨信息
            if (Item is not null && Episodes.FirstOrDefault() is { } first)
            {
                _ = LoadMediaInfoAsync(first.Id, Item);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>单集截图：优先用 Emby 里该集的缩略图，没有就返回 null（界面显示集号占位）。</summary>
    private ImageSource? BuildEpisodeThumbnail(BaseItemDto episode)
    {
        if (episode.ImageTags is null ||
            !episode.ImageTags.TryGetValue("Primary", out var tag) ||
            string.IsNullOrEmpty(tag))
        {
            return null;
        }
        const int width = 360;
        return ImageCacheService.Get(_api.GetPrimaryImageUrl(episode.Id, tag, width), decodeWidth: width);
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (Item is null)
        {
            return;
        }

        if (IsTmdbItem)
        {
            ErrorMessage = "媒体库里没有这部片，没有可播放的片源。";
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
        if (item is null || IsTmdbItem)
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
            var (playTarget, client) = await ResolvePlaybackTargetAsync(target);
            _playbackClient?.Dispose();
            _playbackClient = client;
            _plan = await _playback.PrepareAsync(playTarget, client);
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

    /// <summary>
    /// 决定这次播放实际用哪台服务器：
    /// 选中的播放源就是当前服务器时直接用原条目；否则到那台服务器按片名找同名条目，
    /// 并为它准备一个独立的 API 客户端（流地址与进度上报都走那边）。
    /// </summary>
    private async Task<(BaseItemDto Item, EmbyApiClient? Client)> ResolvePlaybackTargetAsync(BaseItemDto target)
    {
        var option = SelectedPlaybackSource;
        if (option is null || option.ServerId == _settings.CurrentServer?.Id)
        {
            return (target, null);
        }

        var server = _settings.Current.Servers.FirstOrDefault(s => s.Id == option.ServerId);
        if (server is null)
        {
            return (target, null);
        }

        var client = new EmbyApiClient(_settings);
        client.Configure(server.Url, server.AccessToken, server.UserId);
        try
        {
            var result = await client.SearchAsync(target.Name, 20);
            var match = result.Items.FirstOrDefault(i =>
                    string.Equals(i.Name, target.Name, StringComparison.OrdinalIgnoreCase))
                ?? result.Items.FirstOrDefault(i =>
                    i.Type == target.Type && i.IndexNumber == target.IndexNumber &&
                    i.ParentIndexNumber == target.ParentIndexNumber)
                ?? result.Items.FirstOrDefault();
            if (match is null)
            {
                client.Dispose();
                throw new InvalidOperationException($"「{server.DisplayName}」上没有找到《{target.Name}》。");
            }

            // 搜索结果缺 UserData 等字段，取完整条目才能拿到断点位置
            var full = await client.GetItemAsync(match.Id);
            return (full, client);
        }
        catch
        {
            client.Dispose();
            throw;
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
