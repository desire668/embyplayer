using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Animations;
using EmbyPlayer.LiquidGlass;
using EmbyPlayer.Models;
using EmbyPlayer.Services;
using EmbyPlayer.Views;
using Microsoft.UI.Xaml;
using Windows.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace EmbyPlayer.ViewModels;

/// <summary>海报墙首页大图轮播的一屏：背景图 + 标题 + 元信息 + 简介。</summary>
public sealed class HeroItem
{
    /// <summary>Emby 媒体库条目 ID。来自 Emby 的轮播项必填；TMDB 推荐项为空，播放前按片名检索。</summary>
    public string Id { get; init; } = "";
    public required string Name { get; init; }

    /// <summary>原始（通常为英文）片名，用于在 Emby 媒体库中做二次检索。</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>TMDB 电影 ID，用于拼接网页详情地址。</summary>
    public int? TmdbId { get; init; }

    public string? TmdbPageUrl => TmdbId is int id ? $"https://www.themoviedb.org/movie/{id}" : null;

    /// <summary>「2026 · 2 小时 9 分钟」这类元信息行。</summary>
    public string MetaText { get; init; } = "";

    /// <summary>「★ 7.4」评分行，无评分时为空。</summary>
    public string RatingText { get; init; } = "";

    public List<string> Genres { get; init; } = new();

    public string Overview { get; init; } = "";

    /// <summary>全屏背景图。走独立 BitmapImage（1280px 解码），不进海报小图缓存。</summary>
    public ImageSource? Backdrop { get; init; }
}

/// <summary>
/// 轮播指示条的一格：所有格等宽，当前格按轮播进度从左往右填充白色（其余格不填充）。
/// ProgressWidth 会随轮播计时变化，所以需要属性变更通知。
/// </summary>
public sealed partial class HeroDot : ObservableObject
{
    /// <summary>每一格的宽度（全部等宽，当前格不再放大）。</summary>
    public const double BarWidth = 22;

    /// <summary>对应的轮播下标，点击时用它跳转。</summary>
    public int Index { get; init; }

    /// <summary>这一格的总宽度。</summary>
    public double TrackWidth { get; init; } = BarWidth;

    /// <summary>底槽颜色：当前格稍亮，其余更淡。</summary>
    public Brush TrackBrush { get; init; } = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    /// <summary>已播放进度对应的填充宽度（0 ~ TrackWidth）。</summary>
    [ObservableProperty]
    private double _progressWidth;
}

public partial class HomeViewModel : ObservableObject
{
    private readonly EmbyApiClient _api;
    private readonly TmdbClient _tmdb;
    private readonly SettingsService _settings;
    private readonly NavigationService _navigation;

    [ObservableProperty]
    private ObservableCollection<HeroItem> _heroes = new();

    [ObservableProperty]
    private int _heroIndex;

    [ObservableProperty]
    private ObservableCollection<HeroDot> _dots = new();

    [ObservableProperty]
    private ObservableCollection<ItemCard> _resumeItems = new();

    /// <summary>「最新上架」海报墙：媒体库最新入库的影片 / 剧集海报。</summary>
    [ObservableProperty]
    private ObservableCollection<ItemCard> _latestItems = new();

    /// <summary>「受欢迎的影片」海报墙：TMDB popular，与热门趋势不同的一批片。</summary>
    [ObservableProperty]
    private ObservableCollection<ItemCard> _popularItems = new();

    /// <summary>「受欢迎的影片」标题（固定文案，行里没有「全部 ›」）。</summary>
    [ObservableProperty]
    private string _popularTitle = "受欢迎的影片";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoggedIn;

    /// <summary>内容源是否为 TMDB 在线推荐（侧栏内容源下拉切到 TMDB 时为真）。</summary>
    [ObservableProperty]
    private bool _isTmdbSource;

    /// <summary>海报墙标题：TMDB 源显示「热门趋势」，媒体库源显示「最新上架」。</summary>
    [ObservableProperty]
    private string _wallTitle = "最新上架";

    [ObservableProperty]
    private string? _errorMessage;

    public HomeViewModel(EmbyApiClient api, TmdbClient tmdb, SettingsService settings, NavigationService navigation)
    {
        _api = api;
        _tmdb = tmdb;
        _settings = settings;
        _navigation = navigation;
        IsLoggedIn = settings.IsLoggedIn;
    }

    public HeroItem? CurrentHero =>
        HeroIndex >= 0 && HeroIndex < Heroes.Count ? Heroes[HeroIndex] : null;

    public Visibility LoggedInVisibility => IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoggedOutVisibility => IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>未连接服务器且连 TMDB 推荐都没有时，显示居中的欢迎引导面板。</summary>
    public Visibility WelcomePanelVisibility =>
        !IsLoggedIn && Heroes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>轮播内容（含无背景图的兜底项）是否就绪。与登录状态无关（TMDB 推荐未登录也可看）。</summary>
    public Visibility HeroVisibility =>
        Heroes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoHeroVisibility =>
        Heroes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「继续播放」整块（含标题）的可见性。</summary>
    public Visibility ResumeSectionVisibility =>
        IsLoggedIn && ResumeItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「最新上架 / TMDB 热门趋势」海报墙整块的可见性。</summary>
    public Visibility LatestSectionVisibility =>
        LatestItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「受欢迎的影片」整块的可见性。</summary>
    public Visibility PopularSectionVisibility =>
        PopularItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>海报墙右上角「全部 ›」：TMDB 源没有对应的媒体库列表页，隐藏。</summary>
    public Visibility ShowAllLatestVisibility =>
        !IsTmdbSource && LatestItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>错误提示：没有错误时整块 Collapsed，避免空 TextBlock 的上下留白把大图顶下去。</summary>
    public Visibility ErrorMessageVisibility =>
        string.IsNullOrWhiteSpace(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnErrorMessageChanged(string? value) =>
        OnPropertyChanged(nameof(ErrorMessageVisibility));

    partial void OnHeroIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentHero));
        RebuildDots();
        // 大图换了：悬浮侧栏的玻璃按钮要重新采样背后的画面
        GlassRefresh.Request();
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(LoggedInVisibility));
        OnPropertyChanged(nameof(LoggedOutVisibility));
        OnPropertyChanged(nameof(WelcomePanelVisibility));
        OnPropertyChanged(nameof(HeroVisibility));
        OnPropertyChanged(nameof(NoHeroVisibility));
        OnPropertyChanged(nameof(ResumeSectionVisibility));
        OnPropertyChanged(nameof(LatestSectionVisibility));
        OnPropertyChanged(nameof(PopularSectionVisibility));
        OnPropertyChanged(nameof(ShowAllLatestVisibility));
    }

    partial void OnIsTmdbSourceChanged(bool value)
    {
        WallTitle = value ? "热门趋势" : "最新上架";
        OnPropertyChanged(nameof(ShowAllLatestVisibility));
    }

    public async Task InitializeAsync()
    {
        // 记下来源：用户切了服务器 / 在线推荐后，页面重新显示时要再拉一次
        _loadedSourceKey = _settings.CurrentSourceKey;
        IsTmdbSource = _settings.IsTmdbSource;
        WallTitle = IsTmdbSource ? "热门趋势" : "最新上架";
        _trending = null;
        _popular = null;
        IsLoggedIn = _settings.IsLoggedIn;
        ErrorMessage = null;
        ResumeItems.Clear();
        LatestItems = new ObservableCollection<ItemCard>();
        PopularItems = new ObservableCollection<ItemCard>();
        OnPropertyChanged(nameof(ResumeSectionVisibility));
        OnPropertyChanged(nameof(LatestSectionVisibility));
        OnPropertyChanged(nameof(PopularSectionVisibility));
        OnPropertyChanged(nameof(ShowAllLatestVisibility));

        IsLoading = true;
        try
        {
            var heroes = new List<HeroItem>();
            var posters = new List<ItemCard>();

            // 内容源切到 TMDB：无论是否已添加服务器，首屏与海报墙都走 TMDB 在线推荐
            if (IsTmdbSource)
            {
                if (!_tmdb.HasApiKey)
                {
                    ErrorMessage = "使用「TMDB 在线推荐」前，请先在「设置 → 外部服务」里填写 TMDB API Key。";
                }
                else
                {
                    try
                    {
                        heroes = await BuildTmdbHeroesAsync();
                        posters = await BuildTmdbWallAsync();
                        // 受欢迎的影片与热门趋势不同源，多给一行选择；失败不影响首屏
                        try
                        {
                            PopularItems = new ObservableCollection<ItemCard>(
                                await BuildTmdbPopularWallAsync(posters));
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"受欢迎的影片加载失败：{ex.Message}");
                        }
                        OnPropertyChanged(nameof(PopularSectionVisibility));
                    }
                    catch (Exception ex)
                    {
                        ErrorMessage = $"TMDB 推荐加载失败：{ex.Message}";
                    }
                }
            }
            else if (_settings.IsLoggedIn)
            {
                // 默认使用本机 Emby 媒体库：海报与背景图都来自自己的服务器（内网直连、秒开、内容对得上）
                try
                {
                    var library = await LoadLibraryContentAsync();
                    heroes = library.Heroes;
                    posters = library.Posters;
                }
                catch (Exception ex)
                {
                    ErrorMessage = ex.Message;
                }
            }

            if (posters.Count > 0)
            {
                LatestItems = new ObservableCollection<ItemCard>(posters);
            }
            OnPropertyChanged(nameof(LatestSectionVisibility));

            // 没有可用媒体库（未连接 / 库为空 / 拉取失败）：退回 TMDB 热门推荐填充首屏
            if (heroes.Count == 0 && _tmdb.HasApiKey)
            {
                try
                {
                    heroes = await BuildTmdbHeroesAsync();
                    // 兜底时海报墙也一起补上 TMDB 海报，避免出现「有大图、下面是空的」
                    if (LatestItems.Count == 0)
                    {
                        LatestItems = new ObservableCollection<ItemCard>(await BuildTmdbWallAsync());
                        WallTitle = "热门趋势";
                        OnPropertyChanged(nameof(LatestSectionVisibility));
                    }
                    if (PopularItems.Count == 0)
                    {
                        PopularItems = new ObservableCollection<ItemCard>(
                            await BuildTmdbPopularWallAsync(LatestItems));
                        OnPropertyChanged(nameof(PopularSectionVisibility));
                    }
                }
                catch (Exception)
                {
                    // TMDB 也不可达：进入空态
                }
            }

            Heroes = new ObservableCollection<HeroItem>(heroes);
            HeroIndex = 0;
            // HeroIndex 本来就在 0 时赋值不会触发变更通知；不显式通知一次，
            // 首屏绑定 CurrentHero.* 的标题、元信息与背景图就会一直停在初始的空值上
            OnPropertyChanged(nameof(CurrentHero));
            OnPropertyChanged(nameof(HeroVisibility));
            OnPropertyChanged(nameof(NoHeroVisibility));
            OnPropertyChanged(nameof(WelcomePanelVisibility));
            RebuildDots();

            if (_settings.IsLoggedIn && !IsTmdbSource)
            {
                try
                {
                    var resume = await _api.GetResumeAsync(limit: 12);
                    ResumeItems = new ObservableCollection<ItemCard>(
                        resume.Items.Select(BuildResumeCard));
                    OnPropertyChanged(nameof(ResumeSectionVisibility));
                }
                catch (Exception ex) when (Heroes.Count > 0)
                {
                    // 轮播正常时「继续播放」失败不打扰首屏
                    System.Diagnostics.Debug.WriteLine($"继续播放加载失败：{ex.Message}");
                }
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

    /// <summary>一次加载周期内复用 TMDB 热门列表：轮播与海报墙用同一批数据，不重复请求。</summary>
    private List<TmdbMovie>? _trending;

    /// <summary>上次拉首屏时用的内容源（服务器 ID 或 "tmdb"）。</summary>
    private string? _loadedSourceKey;

    /// <summary>
    /// 内容源是否已经变了（在「服务器」页或设置里切了服务器 / 在线推荐）。
    /// 首页是缓存页面，重新显示时据此判断要不要再拉一次首屏，否则会一直显示旧服务器的内容。
    /// </summary>
    public bool IsSourceChanged =>
        !string.Equals(_loadedSourceKey, _settings.CurrentSourceKey, StringComparison.Ordinal);

    private async Task<List<TmdbMovie>> LoadTrendingAsync()
    {
        if (_trending is not null)
        {
            return _trending;
        }
        _trending = await _tmdb.GetTrendingAsync();
        return _trending;
    }

    /// <summary>TMDB 热门海报墙：与轮播同源，点击按片名回媒体库检索，未命中则打开 TMDB 页面。</summary>
    private async Task<List<ItemCard>> BuildTmdbWallAsync()
    {
        var trending = await LoadTrendingAsync();
        return trending
            .Where(m => !string.IsNullOrEmpty(m.PosterPath))
            .Take(24)
            .Select(BuildTmdbCard)
            .ToList();
    }

    /// <summary>一次加载周期内复用 TMDB popular 列表。</summary>
    private List<TmdbMovie>? _popular;

    private async Task<List<TmdbMovie>> LoadPopularAsync()
    {
        if (_popular is not null)
        {
            return _popular;
        }
        _popular = await _tmdb.GetPopularAsync();
        return _popular;
    }

    /// <summary>
    /// 「受欢迎的影片」：TMDB popular，与热门趋势不同的一批片。
    /// exclude 里出现过的（已在热门趋势一行）不再重复展示。
    /// </summary>
    private async Task<List<ItemCard>> BuildTmdbPopularWallAsync(IEnumerable<ItemCard>? exclude = null)
    {
        var popular = await LoadPopularAsync();
        var seen = new HashSet<int>(exclude?.Select(c => c.TmdbId ?? 0) ?? Enumerable.Empty<int>());
        return popular
            .Where(m => !string.IsNullOrEmpty(m.PosterPath) && !seen.Contains(m.Id))
            .Take(24)
            .Select(BuildTmdbCard)
            .ToList();
    }

    private ItemCard BuildTmdbCard(TmdbMovie movie)
    {
        var posterUrl = _tmdb.GetImageUrl(movie.PosterPath, 400);
        var posterFallback = _tmdb.GetFallbackImageUrl(movie.PosterPath, 400);
        var year = movie.ReleaseDate is { Length: >= 4 } date ? date[..4] : null;

        return new ItemCard
        {
            // 没有 Emby 条目 ID，用 TMDB ID 标记来源；Id 留空避免被当成媒体库条目直接进详情
            Id = "",
            TmdbId = movie.Id,
            Name = string.IsNullOrWhiteSpace(movie.Title) ? movie.OriginalTitle ?? "未知影片" : movie.Title!,
            Type = "Movie",
            Year = year,
            ImageUri = posterUrl is null ? null : new Uri(posterUrl),
            ImageFallbackUri = posterFallback is null ? null : new Uri(posterFallback)
        };
    }

    /// <summary>TMDB「本周热门电影」构建轮播：中文标题 / 简介 / 评分，并发拉详情补片长与类型。</summary>
    private async Task<List<HeroItem>> BuildTmdbHeroesAsync()
    {
        var trending = await LoadTrendingAsync();
        var candidates = trending
            .Where(m => !string.IsNullOrEmpty(m.BackdropPath))
            .Take(8)
            .ToList();
        // 绝大多数条目都带背景图，但个别批次会缺失；不足 4 屏时退回整批，
        // 没有背景图的用竖版海报裁切顶上，至少不是一片纯黑
        if (candidates.Count < 4)
        {
            candidates = trending.Take(8).ToList();
        }
        if (candidates.Count == 0)
        {
            return new List<HeroItem>();
        }

        // 并发补详情：runtime / 类型中文名（单个失败不影响整屏）
        var details = await Task.WhenAll(candidates.Select(m => TryGetTmdbDetailAsync(m.Id)));

        var heroes = new List<HeroItem>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var movie = candidates[i];
            var detail = details[i];

            var metaParts = new List<string>();
            if (movie.ReleaseDate is { Length: >= 4 } date)
            {
                metaParts.Add(date[..4]);
            }
            if (detail?.Runtime is int minutes and > 0)
            {
                metaParts.Add(minutes >= 60
                    ? $"{minutes / 60} 小时 {minutes % 60} 分钟"
                    : $"{minutes} 分钟");
            }

            // 没有背景图的条目用竖版海报当背景（横版容器会裁切，但比纯黑好）
            var imagePath = !string.IsNullOrEmpty(movie.BackdropPath) ? movie.BackdropPath : movie.PosterPath;
            var imageUrl = _tmdb.GetImageUrl(imagePath);
            heroes.Add(new HeroItem
            {
                Name = string.IsNullOrWhiteSpace(movie.Title) ? movie.OriginalTitle ?? "未知影片" : movie.Title!,
                OriginalTitle = movie.OriginalTitle,
                TmdbId = movie.Id,
                MetaText = string.Join(" · ", metaParts),
                RatingText = movie.VoteAverage is double vote and > 0 ? $"★ {vote:0.0}" : "",
                Genres = detail?.Genres.Take(2).Select(g => g.Name).ToList() ?? new List<string>(),
                Overview = TrimOverview(movie.Overview),
                Backdrop = imageUrl is null
                    ? null
                    : CreateBackdropImage(imageUrl,
                        _tmdb.GetFallbackImageUrl(imagePath, TmdbClient.BackdropWidth))
            });
        }
        return heroes;
    }

    private async Task<TmdbMovieDetail?> TryGetTmdbDetailAsync(int movieId)
    {
        try
        {
            return await _tmdb.GetMovieDetailAsync(movieId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>单个媒体库的最新内容：轮播候选与海报墙候选。</summary>
    private sealed record ViewLatest(List<HeroItem> Heroes, List<ItemCard> Posters);

    /// <summary>
    /// 首页内容全部来自自己的媒体库：逐个媒体库取最新入库条目，再交错合并。
    /// 不能只按「全局最新」取——某个刚导入、尚未刮削的库会一次占满最新几百条，
    /// 结果是首页一片没有海报的空白（实测有的库 400 条里仅 12 条有图）。
    /// </summary>
    private async Task<(List<HeroItem> Heroes, List<ItemCard> Posters)> LoadLibraryContentAsync()
    {
        var views = await _api.GetViewsAsync();
        if (views.Count == 0)
        {
            return (new List<HeroItem>(), new List<ItemCard>());
        }

        var perView = await Task.WhenAll(views.Select(LoadViewLatestAsync));
        var heroes = RoundRobin(perView.Select(v => (IReadOnlyList<HeroItem>)v.Heroes).ToList(), 8);
        var posters = RoundRobin(perView.Select(v => (IReadOnlyList<ItemCard>)v.Posters).ToList(), 24);
        return (heroes, posters);
    }

    private async Task<ViewLatest> LoadViewLatestAsync(BaseItemDto view)
    {
        var types = view.CollectionType switch
        {
            "movies" => "Movie",
            "tvshows" => "Series",
            "boxsets" => "BoxSet",
            _ => "Movie,Series"
        };

        try
        {
            var result = await _api.GetItemsAsync(view.Id, types, limit: 12,
                sortBy: "DateCreated", sortOrder: "Descending");
            var items = result.Items;

            // 只保留真的有图的条目：无图条目上大图 / 海报都是空框
            var illustrated = items
                .Where(i => i.BackdropImageTags is { Count: > 0 } ||
                            i.ImageTags?.ContainsKey("Primary") == true)
                .ToList();

            return new ViewLatest(
                illustrated.Take(4).Select(BuildHero).ToList(),
                illustrated.Take(6).Select(i => ItemCardMapper.ToCard(_api, i)).ToList());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"媒体库 {view.Name} 加载失败：{ex.Message}");
            return new ViewLatest(new List<HeroItem>(), new List<ItemCard>());
        }
    }

    /// <summary>按列表轮流取元素，让各媒体库的内容均匀出现在首页，而不是被某一个库包场。</summary>
    private static List<T> RoundRobin<T>(List<IReadOnlyList<T>> lists, int max)
    {
        var pools = lists.Where(l => l.Count > 0).ToList();
        var result = new List<T>(max);
        for (var index = 0; result.Count < max; index++)
        {
            var added = false;
            foreach (var pool in pools)
            {
                if (index >= pool.Count)
                {
                    continue;
                }
                result.Add(pool[index]);
                added = true;
                if (result.Count == max)
                {
                    break;
                }
            }
            if (!added)
            {
                break;
            }
        }
        return result;
    }

    private HeroItem BuildHero(BaseItemDto item)
    {
        ImageSource? backdrop = null;
        var backdropTag = item.BackdropImageTags?.FirstOrDefault();
        if (backdropTag is not null)
        {
            backdrop = CreateBackdropImage(_api.GetBackdropImageUrl(item.Id, backdropTag));
        }
        else if (item.ImageTags is not null && item.ImageTags.TryGetValue("Primary", out var primaryTag))
        {
            // 服务器没有背景图时用主图顶上（会被裁切成横版），至少不是一片纯黑
            backdrop = CreateBackdropImage(_api.GetPrimaryImageUrl(item.Id, primaryTag, EmbyApiClient.BackdropWidth));
        }

        var metaParts = new List<string>();
        if (item.ProductionYear is int year)
        {
            metaParts.Add(year.ToString());
        }
        if (item.RunTimeTicks is long ticks && ticks > 0)
        {
            metaParts.Add(FormatDuration(ticks));
        }

        return new HeroItem
        {
            Id = item.Id,
            Name = item.Name,
            MetaText = string.Join(" · ", metaParts),
            RatingText = item.CommunityRating is { } rating ? $"★ {rating:0.0}" : "",
            Genres = item.Genres?.Take(2).ToList() ?? new List<string>(),
            Overview = TrimOverview(item.Overview),
            Backdrop = backdrop
        };
    }

    /// <summary>
    /// 大图 BitmapImage。传入 fallbackUrl 时，主图源加载失败（国内直连 TMDB 图片 CDN 常被拦）
    /// 会自动换成镜像地址重试一次，避免整屏只剩渐变底色。
    /// </summary>
    private static BitmapImage CreateBackdropImage(string url, string? fallbackUrl = null)
    {
        var image = new BitmapImage
        {
            DecodePixelWidth = EmbyApiClient.BackdropWidth,
            DecodePixelType = DecodePixelType.Logical
        };

        if (!string.IsNullOrEmpty(fallbackUrl))
        {
            image.ImageFailed += (_, _) =>
            {
                try
                {
                    // 已经切到镜像就不再重复设置，否则失败会无限循环
                    if (!string.Equals(image.UriSource?.OriginalString, fallbackUrl,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        image.UriSource = new Uri(fallbackUrl);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"背景图镜像兜底失败：{ex.Message}");
                }
            };
        }

        image.UriSource = new Uri(url);
        return image;
    }

    private static string TrimOverview(string? overview)
    {
        var text = overview ?? "";
        return text.Length > 120 ? text[..120].TrimEnd() + "…" : text;
    }

    /// <summary>
    /// 继续播放卡片：剧集用剧集名 + 「S01E06 · 1天前」副标题、缩略图用单集主图（横版场景图），
    /// 点击进入单集详情即可从断点续播；电影保持原条目。
    /// </summary>
    private ItemCard BuildResumeCard(BaseItemDto item)
    {
        var card = ItemCardMapper.ToCard(_api, item);
        var timeAgo = FormatTimeAgo(item.UserData?.LastPlayedDate);

        if (item.Type == "Episode")
        {
            var label = ItemCardMapper.BuildEpisodeLabel(item);
            card.Name = string.IsNullOrWhiteSpace(item.SeriesName) ? item.Name : item.SeriesName!;
            card.Subtitle = string.IsNullOrEmpty(label) ? timeAgo : $"{label} · {timeAgo}";
        }
        else if (!string.IsNullOrEmpty(timeAgo))
        {
            card.Subtitle = timeAgo;
        }

        return card;
    }

    private void RebuildDots()
    {
        var dots = new List<HeroDot>(Heroes.Count);
        for (var i = 0; i < Heroes.Count; i++)
        {
            var current = i == HeroIndex;
            dots.Add(new HeroDot
            {
                Index = i,
                TrackBrush = new SolidColorBrush(Color.FromArgb(current ? (byte)0x66 : (byte)0x33, 0xFF, 0xFF, 0xFF))
            });
        }
        Dots = new ObservableCollection<HeroDot>(dots);
    }

    /// <summary>大图自动轮播间隔（秒）；0 表示不自动切换。取值来自设置页，页面按它驱动轮播与指示条进度。</summary>
    public double HeroIntervalSeconds => Math.Max(0, _settings.Current.HeroAutoPlaySeconds);

    /// <summary>轮播进度 0~1：当前指示条按此比例填充，直观显示还要多久切下一张。</summary>
    public void UpdateHeroProgress(double ratio)
    {
        if (HeroIndex < 0 || HeroIndex >= Dots.Count)
        {
            return;
        }

        var dot = Dots[HeroIndex];
        dot.ProgressWidth = Math.Clamp(ratio, 0, 1) * dot.TrackWidth;
    }

    private static string FormatDuration(long ticks)
    {
        var ts = TimeSpan.FromTicks(ticks);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分钟"
            : $"{ts.Minutes} 分钟";
    }

    private static string FormatTimeAgo(DateTime? time)
    {
        if (time is not DateTime t)
        {
            return "";
        }
        var local = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
        var delta = DateTime.Now - local;
        if (delta.TotalSeconds < 0)
        {
            return "";
        }
        if (delta.TotalMinutes < 1)
        {
            return "刚刚";
        }
        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}分钟前";
        }
        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}小时前";
        }
        if (delta.TotalDays < 30)
        {
            return $"{(int)delta.TotalDays}天前";
        }
        return local.ToString("yyyy-MM-dd");
    }

    [RelayCommand]
    private void NextHero()
    {
        if (Heroes.Count > 1)
        {
            HeroIndex = (HeroIndex + 1) % Heroes.Count;
        }
    }

    [RelayCommand]
    private void PrevHero()
    {
        if (Heroes.Count > 1)
        {
            HeroIndex = (HeroIndex - 1 + Heroes.Count) % Heroes.Count;
        }
    }

    /// <summary>点击指示条直接跳到对应的一屏。</summary>
    public void GoToHero(int index)
    {
        if (index >= 0 && index < Heroes.Count)
        {
            HeroIndex = index;
        }
    }

    /// <summary>
    /// 大图「播放」：Emby 条目直接进详情页自动播放；TMDB 推荐先按片名（中文 / 原名）
    /// 检索媒体库，命中则播放，未命中提示；未登录时先进连接服务器流程。
    /// </summary>
    [RelayCommand]
    private async Task PlayHeroAsync()
    {
        if (CurrentHero is not { } hero)
        {
            return;
        }

        if (!string.IsNullOrEmpty(hero.Id))
        {
            _navigation.NavigateTo<DetailPage>("autoplay:" + hero.Id);
            return;
        }

        if (!_settings.IsLoggedIn)
        {
            GoConnectServer();
            return;
        }

        ErrorMessage = null;
        var match = await FindEmbyMatchAsync(hero);
        if (match is null)
        {
            ErrorMessage = $"媒体库中未找到《{hero.Name}》，无法直接播放。";
            return;
        }
        _navigation.NavigateTo<DetailPage>("autoplay:" + match.Id);
    }

    /// <summary>
    /// 大图「详情」：媒体库命中进 Emby 详情页；没命中也留在应用里，
    /// 用 TMDB 的在线信息渲染详情页，不再跳到 the moviedb 网站。
    /// </summary>
    [RelayCommand]
    private async Task OpenHeroDetailAsync()
    {
        if (CurrentHero is not { } hero)
        {
            return;
        }

        if (!string.IsNullOrEmpty(hero.Id))
        {
            _navigation.NavigateTo<DetailPage>(hero.Id);
            return;
        }

        if (_settings.IsLoggedIn)
        {
            var match = await FindEmbyMatchAsync(hero);
            if (match is not null)
            {
                _navigation.NavigateTo<DetailPage>(match.Id);
                return;
            }
        }

        if (hero.TmdbId is int id)
        {
            _navigation.NavigateTo<DetailPage>($"tmdb:{id}");
        }
    }

    /// <summary>用中文片名与原始片名在媒体库中检索最匹配的电影条目。</summary>
    private async Task<BaseItemDto?> FindEmbyMatchAsync(HeroItem hero)
    {
        var terms = new[] { hero.Name, hero.OriginalTitle }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var term in terms)
        {
            try
            {
                var result = await _api.SearchAsync(term, 20);
                var match = result.Items
                    .FirstOrDefault(i => i.Type == "Movie" &&
                        string.Equals(i.Name, term, StringComparison.OrdinalIgnoreCase))
                    ?? result.Items.FirstOrDefault(i => i.Type == "Movie");
                if (match is not null)
                {
                    return match;
                }
            }
            catch (Exception)
            {
                // 单个检索词失败时继续尝试下一个
            }
        }
        return null;
    }

    /// <summary>点击任意海报卡片：媒体库条目带共享元素动画进详情页；TMDB 卡片先回媒体库检索。</summary>
    [RelayCommand]
    private async Task OpenItemAsync(ItemCard card)
    {
        if (card.TmdbId is not null)
        {
            await OpenTmdbCardAsync(card);
            return;
        }

        if (string.IsNullOrEmpty(card.Id))
        {
            return;
        }

        _navigation.NavigateTo<DetailPage>(card.Id, FluidNavigation.PosterPush);
    }

    /// <summary>
    /// TMDB 海报卡片：已连接服务器时按片名在媒体库里找对应条目，命中直接进详情；
    /// 未命中（或没连服务器）也进详情页，只是内容换成 TMDB 的在线信息，不打开网页。
    /// </summary>
    private async Task OpenTmdbCardAsync(ItemCard card)
    {
        ErrorMessage = null;

        if (_settings.IsLoggedIn)
        {
            var match = await FindEmbyMatchAsync(new HeroItem { Name = card.Name });
            if (match is not null)
            {
                _navigation.NavigateTo<DetailPage>(match.Id, FluidNavigation.PosterPush);
                return;
            }
            ErrorMessage = $"媒体库中未找到《{card.Name}》，显示的是 TMDB 在线信息。";
        }

        if (card.TmdbId is int id)
        {
            _navigation.NavigateTo<DetailPage>($"tmdb:{id}", FluidNavigation.PosterPush);
        }
    }

    [RelayCommand]
    private void ShowAllResume() =>
        _navigation.NavigateTo<ResumePage>();

    [RelayCommand]
    private void ShowAllLatest() =>
        _navigation.NavigateTo<LibraryPage>();

    /// <summary>未登录时的引导按钮：进入连接服务器（登录）页。</summary>
    [RelayCommand]
    private void GoConnectServer()
    {
        _navigation.ShowShellNavigation(false);
        _navigation.NavigateTo<LoginPage>();
    }
}
