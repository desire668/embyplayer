using EmbyPlayer.Animations;
using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EmbyPlayer.Views;

public sealed partial class HomePage : Page, IRefreshablePage
{
    public HomeViewModel ViewModel { get; }

    /// <summary>轮播计时器的推进步长（秒）：既驱动切换，也驱动指示条进度。</summary>
    private const double TickSeconds = 0.1;

    private DispatcherTimer? _carousel;
    private double _heroElapsed;

    /// <summary>首屏数据是否已拉过（切走再回来不重复加载，要更新点侧栏的刷新按钮）。</summary>
    private bool _initialized;

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();

        // 缓存页面实例：在首页 / 媒体库之间来回切不再重建 UI，滚动位置与已加载的海报都留着
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        Loaded += OnPageLoaded;
        Unloaded += (_, _) => _carousel?.Stop();
    }

    /// <summary>侧栏刷新按钮：强制重新拉一次首屏内容。</summary>
    public async Task RefreshAsync()
    {
        await ViewModel.InitializeAsync();
        RestartCarousel();
    }

    /// <summary>
    /// 大图真正解码完成后才让侧栏玻璃按钮重新采样背后的画面。
    /// 切屏时新图可能还在下载，过早采样会抓到黑底，圆点就会发黑。
    /// </summary>
    private void HeroBackdrop_ImageOpened(object sender, RoutedEventArgs e) =>
        EmbyPlayer.LiquidGlass.GlassRefresh.Request();

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            _initialized = true;
            await ViewModel.InitializeAsync();
        }
        else if (ViewModel.IsSourceChanged)
        {
            // 在「服务器」页换过内容源：回到首页时把大图与海报墙整块换掉，
            // 否则缓存的页面会一直显示上一台服务器的内容
            await ViewModel.InitializeAsync();
        }

        StartCarousel();
    }

    /// <summary>
    /// 大图自动轮播：默认每 12 秒（可在设置页调整）切下一屏，同时按 100ms 的节奏把进度写回指示条，
    /// 让当前那一格从左往右填满，直观显示还要多久切换。内容不足两屏或设为「不自动切换」时不启动。
    /// </summary>
    private void StartCarousel()
    {
        if (ViewModel.Heroes.Count < 2 || ViewModel.HeroIntervalSeconds <= 0)
        {
            // 不轮播：指示条直接显示为已填满
            ViewModel.UpdateHeroProgress(1);
            return;
        }

        if (_carousel is null)
        {
            _carousel = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
            _carousel.Tick += OnCarouselTick;
        }

        if (!_carousel.IsEnabled)
        {
            _heroElapsed = 0;
            _carousel.Start();
        }
    }

    private void OnCarouselTick(object? sender, object e)
    {
        var interval = ViewModel.HeroIntervalSeconds;
        if (interval <= 0)
        {
            return;
        }

        _heroElapsed += TickSeconds;
        if (_heroElapsed >= interval)
        {
            _heroElapsed = 0;
            ViewModel.NextHeroCommand.Execute(null);
            return;
        }

        ViewModel.UpdateHeroProgress(_heroElapsed / interval);
    }

    /// <summary>点指示条：跳到对应的一屏。</summary>
    private void HeroDot_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HeroDot dot })
        {
            ViewModel.GoToHero(dot.Index);
            RestartCarousel();
        }
    }

    // ==================== 左右滑动切屏 ====================

    /// <summary>判定为一次滑动的最小水平位移（太小就当普通点击）。</summary>
    private const double SwipeThreshold = 60;

    private Point? _swipeStart;

    /// <summary>刚处理过一次滑动：接下来的 Tapped 要忽略，否则滑一次会连切两张。</summary>
    private bool _swipeHandled;

    private void HeroHost_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(HeroHost);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _swipeStart = point.Position;
        _swipeHandled = false;
        // 捕获指针：拖到窗口外再松手也能收到 Released
        HeroHost.CapturePointer(e.Pointer);
    }

    private void HeroHost_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_swipeStart is not Point start)
        {
            return;
        }

        var end = e.GetCurrentPoint(HeroHost).Position;
        _swipeStart = null;
        HeroHost.ReleasePointerCapture(e.Pointer);

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        // 位移够长且以横向为主才算滑动（纵向拖动多半是在滚动页面）
        if (Math.Abs(dx) < SwipeThreshold || Math.Abs(dx) <= Math.Abs(dy))
        {
            return;
        }

        _swipeHandled = true;
        if (dx > 0)
        {
            GoPrevHero();
        }
        else
        {
            GoNextHero();
        }
    }

    private void HeroHost_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _swipeStart = null;
        HeroHost.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>点大图左侧：上一屏。</summary>
    private void HeroPrev_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ConsumeSwipe())
        {
            return;
        }
        GoPrevHero();
    }

    /// <summary>点大图右侧：下一屏。</summary>
    private void HeroNext_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ConsumeSwipe())
        {
            return;
        }
        GoNextHero();
    }

    /// <summary>滑动之后紧跟的那次点击要吃掉；用完即清零。</summary>
    private bool ConsumeSwipe()
    {
        if (!_swipeHandled)
        {
            return false;
        }

        _swipeHandled = false;
        return true;
    }

    private void GoPrevHero()
    {
        if (ViewModel.Heroes.Count < 2)
        {
            return;
        }
        ViewModel.PrevHeroCommand.Execute(null);
        RestartCarousel();
    }

    private void GoNextHero()
    {
        if (ViewModel.Heroes.Count < 2)
        {
            return;
        }
        ViewModel.NextHeroCommand.Execute(null);
        RestartCarousel();
    }

    /// <summary>手动切换后重置自动轮播计时与进度，避免刚点完就立刻自动跳走。</summary>
    private void RestartCarousel()
    {
        if (_carousel is null || !_carousel.IsEnabled)
        {
            return;
        }
        _heroElapsed = 0;
        ViewModel.UpdateHeroProgress(0);
        _carousel.Stop();
        _carousel.Start();
    }

    /// <summary>
    /// 欢迎面板的「填写 TMDB API Key」：没有 Emby 服务器时，用户也能用一个免费的
    /// TMDB Key 把在线推荐海报墙打开（Key 只存本机 settings.json）。
    /// </summary>
    private async void TmdbKey_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Services.GetRequiredService<Services.SettingsService>();

        var box = new TextBox
        {
            Header = "TMDB API Key（v3 auth，密钥）",
            PlaceholderText = "32 位密钥，形如 0123456789abcdef0123456789abcdef",
            Text = settings.Current.TmdbApiKey ?? "",
        };

        var hint = new TextBlock
        {
            Text = "两种都支持：32 位的「API Key（v3 auth）」密钥，或以 eyJ 开头的「API Read Access Token」令牌，整段复制粘贴即可。"
                 + "免费申请：themoviedb.org → 账户设置 → API → 申请 API 密钥（用途选个人 / 开发者即可）。留空并保存则关闭在线推荐。",
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(box);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "填写 TMDB API Key",
            Content = panel,
            PrimaryButtonText = "保存并加载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var key = box.Text.Trim();
        settings.Current.TmdbApiKey = string.IsNullOrEmpty(key) ? null : key;
        settings.Save();

        // 重新拉取首页：填了 Key 会立刻出现 TMDB 推荐，清空则回到欢迎引导
        await ViewModel.InitializeAsync();
        StartCarousel();
    }

    private void ResumeList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemCard card)
        {
            ViewModel.OpenItemCommand.Execute(card);
        }
    }

    private void LatestList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ItemCard card)
        {
            return;
        }

        // 点击的海报作为共享元素飞入详情页（与媒体库页一致）
        if (sender is ListView list)
        {
            var poster = FluidNavigation.FindDescendant<PosterCard>(list.ContainerFromItem(card));
            FluidNavigation.PreparePoster(poster?.PosterElement);
        }

        ViewModel.OpenItemCommand.Execute(card);
    }
}
