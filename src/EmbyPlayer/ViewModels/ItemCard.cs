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
                _poster = ImageCacheService.Get(ImageUri?.AbsoluteUri);
            }

            return _poster;
        }
    }

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";

    /// <summary>来源服务器 ID（聚合搜索时用于定位所属服务器）。</summary>
    public string? ServerId { get; set; }

    public string? Year { get; set; }
    public Uri? ImageUri { get; set; }
    public bool Watched { get; set; }
    public double ResumePercent { get; set; }

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public bool HasImage => ImageUri is not null;
    public bool ShowResume => ResumePercent is > 0 and < 100;

    public Microsoft.UI.Xaml.Visibility WatchedVisibility =>
        Watched ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ImageVisibility =>
        HasImage ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ResumeVisibility =>
        ShowResume ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility YearVisibility =>
        string.IsNullOrEmpty(Year) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
}
