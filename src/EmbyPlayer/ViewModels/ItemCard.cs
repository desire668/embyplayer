using EmbyPlayer.Services;
using Microsoft.UI.Xaml.Media;

namespace EmbyPlayer.ViewModels;

public sealed class ItemCard
{
    private ImageSource? _poster;
    private bool _posterResolved;

    /// <summary>
    /// 海报位图。走 ImageCacheService，列表与详情页拿到的是同一个 BitmapImage 实例，
    /// 因此进入详情时海报不会重新下载、重新解码。
    /// </summary>
    public ImageSource? Poster
    {
        get
        {
            if (!_posterResolved)
            {
                _posterResolved = true;
                _poster = ImageCacheService.Get(ImageUri?.AbsoluteUri, ImageFallbackUri?.AbsoluteUri);
            }

            return _poster;
        }
    }

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";

    /// <summary>来源服务器 ID（聚合搜索时用于定位所属服务器）。</summary>
    public string? ServerId { get; set; }

    /// <summary>TMDB 电影 ID：非空表示这张卡来自 TMDB 在线推荐，没有 Emby 条目 ID。</summary>
    public int? TmdbId { get; set; }

    public string? TmdbPageUrl => TmdbId is int id ? $"https://www.themoviedb.org/movie/{id}" : null;

    public string? Year { get; set; }

    /// <summary>卡片副标题（如剧集卡片的 "S01E05"）。为空时副标题回退显示年份。</summary>
    public string? Subtitle { get; set; }

    public Uri? ImageUri { get; set; }

    /// <summary>海报备用地址：主地址加载失败时换它重试（TMDB 图片走镜像兜底）。</summary>
    public Uri? ImageFallbackUri { get; set; }
    public bool Watched { get; set; }
    public double ResumePercent { get; set; }

    /// <summary>副标题行实际显示的文本：优先 Subtitle，其次 Year。</summary>
    public string SubtitleOrYear => !string.IsNullOrEmpty(Subtitle) ? Subtitle! : Year ?? "";

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public bool HasImage => ImageUri is not null;
    public bool ShowResume => ResumePercent is > 0 and < 100;

    public Microsoft.UI.Xaml.Visibility WatchedVisibility =>
        Watched ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ImageVisibility =>
        HasImage ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>无图占位（首字母）的可见性，与 ImageVisibility 互补。</summary>
    public Microsoft.UI.Xaml.Visibility ImagePlaceholderVisibility =>
        HasImage ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility ResumeVisibility =>
        ShowResume ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility YearVisibility =>
        string.IsNullOrEmpty(Year) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility SubtitleOrYearVisibility =>
        string.IsNullOrEmpty(SubtitleOrYear) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
}
