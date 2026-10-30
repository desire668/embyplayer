using EmbyPlayer.Models;
using EmbyPlayer.Services;
using EmbyPlayer.ViewModels;
using EmbyPlayer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System.ComponentModel;
using System.IO;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace EmbyPlayer;

/// <summary>面包屑上的一节：标题 + 层级（0 是最外层）。</summary>
public sealed class CrumbItem
{
    public CrumbItem(string title, int depth)
    {
        Title = title;
        Depth = depth;
    }

    public string Title { get; }

    public int Depth { get; }

    public override string ToString() => Title;
}

public sealed partial class MainWindow : Window
{
    /// <summary>非首页页面左侧给悬浮侧栏留出的宽度（圆点 44 + 左右边距）。</summary>
    private const double RailInset = 76;

    /// <summary>自绘标题栏条的高度，和 XAML 里 TitleBarHost 保持一致（TitleBarHeightOption.Tall）。</summary>
    private const double TitleBarHeight = 40;

    public MainWindow()
    {
        InitializeComponent();

        var navigation = App.Services.GetRequiredService<NavigationService>();
        navigation.Frame = ContentFrame;
        navigation.Shell = this;

        ContentFrame.Navigated += ContentFrame_Navigated;

        AppWindow.Resize(new SizeInt32(1280, 800));

        // 设置窗口标题栏图标（任务栏 / Alt+Tab 仍用得到；窗口左上角不再显示图标和名字）
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app-icon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        SetupCustomTitleBar();

        // 标题栏跟随应用主题：暗色纯黑、亮色白色，主题切换时同步刷新
        // （Window 本身无 ActualTheme，借助根元素的主题）
        ApplyThemeTitleBar(Root.ActualTheme);
        Root.ActualThemeChanged += (_, _) => ApplyThemeTitleBar(Root.ActualTheme);

        // 设置页切换「颜色模式」后会 Save()，这里统一应用，避免各页面各自处理
        var themeSettings = App.Services.GetRequiredService<SettingsService>();
        ApplyThemeMode(themeSettings.Current.ThemeMode);
        themeSettings.SettingsChanged += () =>
            DispatcherQueue.TryEnqueue(() => ApplyThemeMode(themeSettings.Current.ThemeMode));

        _ = EnsureServerNamesAsync();
    }

    /// <summary>
    /// 自绘标题栏：内容延伸到窗口顶部（左上角的系统图标和应用名随之消失），
    /// 顶部由 XAML 画一条云母横条，右上角三个系统按钮背景设为透明，透出这条云母。
    /// </summary>
    private void SetupCustomTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

            // 顶部那条登记为可拖动区（Caption），再把面包屑那一块登记为 Passthrough，
            // 这样拖动窗口照常，面包屑的点击也能传回 XAML。
            Root.SizeChanged += (_, _) =>
            {
                UpdateDragRegion();
                if (SearchOverlay.Visibility == Visibility.Visible)
                {
                    PositionSearchPanel();
                }
            };
            Root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(UpdateDragRegion);
            UpdateDragRegion();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog($"设置自绘标题栏失败：{ex}");
        }
    }

    /// <summary>
    /// 调整拖动条：面包屑显示时从它右边开始，面包屑那块留在客户区，点击照常生效；
    /// 面包屑隐藏时整条顶部都能拖动。
    /// </summary>
    private void UpdateDragRegion()
    {
        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            var width = (int)Math.Round(Root.ActualWidth * scale);
            var height = (int)Math.Round(TitleBarHeight * scale);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // 整条顶部都能拖动（面包屑在它下面，属于客户区，点击照常生效）
            source.SetRegionRects(NonClientRegionKind.Caption, new[] { new RectInt32(0, 0, width, height) });
        }
        catch (Exception ex)
        {
            App.WriteCrashLog($"调整标题栏拖动区域失败：{ex}");
        }
    }

    /// <summary>应用颜色模式：system 跟随 Windows，light / dark 强制覆盖（ThemeResource 会自动重取）。</summary>
    private void ApplyThemeMode(string? mode) =>
        Root.RequestedTheme = mode?.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

    /// <summary>当前页面类型，用于空判断（首次导航前为 null）。</summary>
    private Type? _currentPageType;

    /// <summary>
    /// 换页时同步侧栏高亮，并按页面决定内容区左边距：
    /// 首页 / 登录页铺满整窗（侧栏浮在大图上），其余页面留出侧栏宽度，内容不会被圆点压住。
    /// </summary>
    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        _currentPageType = e.SourcePageType;
        UpdateRailSelection();
        // 换页时收起搜索弹层（在弹层里点搜索结果、或点侧栏其它按钮）
        CloseSearchOverlay();

        // 首页 / 登录页 / 详情页整窗铺满：背景大图一直铺到窗口顶部（标题栏透明，图上透上来）
        // 和窗口左边（侧栏悬浮在图上）；
        // 其余页面顶部让开标题栏条、左边让开侧栏，页面内容不会被它们盖住。
        // 详情页的内容位置由页面自身的 Padding 让开（见 DetailPage.xaml），所以铺满后排版不变。
        var fullBleed = e.SourcePageType == typeof(HomePage)
            || e.SourcePageType == typeof(LoginPage)
            || e.SourcePageType == typeof(DetailPage);
        ContentFrame.Margin = fullBleed
            ? new Thickness(0)
            : new Thickness(RailInset, TitleBarHeight, 0, 0);

        AttachDetailTracking();
        UpdateBreadcrumb();

    }

    // ==================== 面包屑 ====================

    /// <summary>当前跟踪的详情页 ViewModel（标题异步加载完成后再刷新一次面包屑）。</summary>
    private DetailViewModel? _trackedDetail;

    private void AttachDetailTracking()
    {
        if (_trackedDetail is not null)
        {
            _trackedDetail.PropertyChanged -= OnDetailPropertyChanged;
            _trackedDetail = null;
        }
        if (ContentFrame.Content is DetailPage detail)
        {
            _trackedDetail = detail.ViewModel;
            _trackedDetail.PropertyChanged += OnDetailPropertyChanged;
        }
    }

    private void OnDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetailViewModel.Item))
        {
            UpdateBreadcrumb();
        }
    }

    /// <summary>
    /// 用页面栈拼出「首页 › 媒体库 › 影片名」。根页面自身不显示（那里没有上一级，
    /// 而且会压住页面标题）；子页面才显示，点任一级沿栈退回到对应页面。
    /// </summary>
    private void UpdateBreadcrumb()
    {
        var currentType = ContentFrame.CurrentSourcePageType;
        if (currentType is null || !ContentFrame.CanGoBack || IsRootPage(currentType))
        {
            BreadcrumbHost.Visibility = Visibility.Collapsed;
            return;
        }

        var titles = new List<string>();
        foreach (var entry in ContentFrame.BackStack)
        {
            titles.Add(TitleFor(entry));
        }
        titles.Add(CurrentPageTitle(currentType));

        // 相邻重复（例如首页 → 首页）合并掉，避免出现「首页 › 首页」
        var items = new List<CrumbItem>();
        foreach (var title in titles)
        {
            if (items.Count > 0 && items[^1].Title == title)
            {
                continue;
            }
            items.Add(new CrumbItem(title, items.Count));
        }

        Crumbs.ItemsSource = items;
        _crumbItems = items;
        BreadcrumbHost.Visibility = Visibility.Visible;
    }

    /// <summary>当前面包屑的层级列表（BreadcrumbBar 只暴露 ItemsSource，这里自己留一份）。</summary>
    private List<CrumbItem> _crumbItems = new();

    private void Crumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is not CrumbItem crumb)
        {
            return;
        }


        // 当前页在最后一节，点第 depth 节就要退 (末级 - depth) 步
        var steps = _crumbItems.Count - 1 - crumb.Depth;
        var navigation = App.Services.GetRequiredService<NavigationService>();
        for (var i = 0; i < steps && navigation.CanGoBack; i++)
        {
            navigation.GoBack();
        }
    }

    private static bool IsRootPage(Type type) =>
        type == typeof(HomePage) || type == typeof(LibraryPage) ||
        type == typeof(FavoritesPage) || type == typeof(ServersPage) || type == typeof(SettingsPage) ||
        type == typeof(LoginPage);

    /// <summary>路径的根节点用当前内容源的名字（例如「乐蛙」），比一律写「首页」更容易分清在看哪台服务器。</summary>
    private string RootTitle()
    {
        if (Settings.IsTmdbSource)
        {
            return "TMDB 在线推荐";
        }
        return Settings.CurrentServer?.DisplayName ?? "首页";
    }

    /// <summary>当前页标题：详情页取影片名（还没加载完时先用「详情」占位），搜索页带上是搜的什么词。</summary>
    private string CurrentPageTitle(Type type)
    {
        if (ContentFrame.Content is DetailPage detail)
        {
            return detail.ViewModel.Item?.Name ?? "详情";
        }
        if (ContentFrame.Content is SearchPage search && !string.IsNullOrWhiteSpace(search.Query))
        {
            return SearchTitle(search.Query!);
        }
        return TitleFor(type);
    }

    private static string SearchTitle(string query) => $"搜索：{query}";

    /// <summary>页面栈里的一节标题：详情页用记录的影片名（栈里只存了 itemId），搜索页用参数里的词。</summary>
    private string TitleFor(PageStackEntry entry)
    {
        if (entry.SourcePageType == typeof(DetailPage))
        {
            var id = entry.Parameter as string;
            if (id?.StartsWith("autoplay:", StringComparison.Ordinal) == true)
            {
                id = id["autoplay:".Length..];
            }
            return ItemCardMapper.TryGetName(id) ?? "详情";
        }
        if (entry.SourcePageType == typeof(SearchPage) && entry.Parameter is string query &&
            !string.IsNullOrWhiteSpace(query))
        {
            return SearchTitle(query);
        }
        return TitleFor(entry.SourcePageType);
    }

    private string TitleFor(Type type)
    {
        if (type == typeof(HomePage)) return RootTitle();
        if (type == typeof(LibraryPage)) return "媒体库";
        if (type == typeof(SearchPage)) return "搜索";
        if (type == typeof(FavoritesPage)) return "收藏";
        if (type == typeof(ServersPage)) return "服务器";
        if (type == typeof(SettingsPage)) return "设置";
        if (type == typeof(ResumePage)) return "继续观看";
        if (type == typeof(LoginPage)) return "登录";
        if (type == typeof(DetailPage)) return "详情";
        return "页面";
    }

    private void UpdateRailSelection()
    {
        // 侧栏指示的是「当前所在区块」而不是当前页面类型：
        // 从媒体库点进详情页时，页面类型是 DetailPage，但人还在媒体库这一栏，
        // 面包屑已经按页面栈显示了正确路径，滑块和高亮必须跟着停在来源那一栏。
        var section = CurrentSection();

        RailHome.IsActive = section == typeof(HomePage);
        RailLibrary.IsActive = section == typeof(LibraryPage);
        RailSearch.IsActive = section == typeof(SearchPage);
        RailFavorites.IsActive = section == typeof(FavoritesPage);
        RailServers.IsActive = section == typeof(ServersPage);
        RailSettings.IsActive = section == typeof(SettingsPage);

        UpdatePillThumb(section);
    }

    /// <summary>
    /// 当前所在区块：先看当前页面，页面不属于任何一栏（详情页等子页面）时，
    /// 沿页面栈由近及远找最近的一栏 —— 也就是面包屑里它挂在谁的下面。
    /// 找不到就归到首页。
    /// </summary>
    private Type CurrentSection()
    {
        if (IsRailPage(_currentPageType))
        {
            return _currentPageType!;
        }

        for (var i = ContentFrame.BackStack.Count - 1; i >= 0; i--)
        {
            var type = ContentFrame.BackStack[i].SourcePageType;
            if (IsRailPage(type))
            {
                return type;
            }
        }

        return typeof(HomePage);
    }

    /// <summary>页面自身就是侧栏上的一栏（与之相对的是详情页这类挂在某一栏下面的子页面）。</summary>
    private static bool IsRailPage(Type? type) =>
        type == typeof(HomePage) || type == typeof(LibraryPage) || type == typeof(SearchPage) ||
        type == typeof(FavoritesPage) || type == typeof(ServersPage) || type == typeof(SettingsPage);

    /// <summary>
    /// 胶囊只有首页 / 媒体库两档：区块是这两者时把滑块拨过去；
    /// 其余区块（搜索、收藏、服务器、设置）不在胶囊里，滑块留在原处不动。
    /// </summary>
    private void UpdatePillThumb(Type section)
    {
        if (section == typeof(LibraryPage))
        {
            MovePillThumb(true);
        }
        else if (section == typeof(HomePage))
        {
            MovePillThumb(false);
        }
    }

    /// <summary>两档之间的行程：按钮 46 高 + 中间 2 间距。</summary>
    private const double PillThumbTravel = 48.0;

    private bool _pillThumbSet;

    /// <summary>
    /// 把胶囊里的滑块拨到首页（上）或媒体库（下）。
    /// 滑动时先压扁一点再回弹，像拨动开关而不是两张图硬切；首次定位不带动画。
    /// </summary>
    private void MovePillThumb(bool toLibrary)
    {
        var target = toLibrary ? PillThumbTravel : 0.0;

        if (!_pillThumbSet)
        {
            _pillThumbSet = true;
            ThumbShift.Y = target;
            return;
        }

        if (Math.Abs(ThumbShift.Y - target) < 0.5)
        {
            return;
        }

        AnimateTo(ThumbShift, "Y", target, 280, new CubicEase { EasingMode = EasingMode.EaseOut });

        // 挤压回弹：中段压扁拉长，落位恢复
        var squash = new DoubleAnimationUsingKeyFrames
        {
            EnableDependentAnimation = true
        };
        squash.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1 });
        squash.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), Value = 0.86 });
        squash.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)),
            Value = 1,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Run(ThumbScale, "ScaleY", squash);

        var stretch = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        stretch.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1 });
        stretch.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), Value = 1.07 });
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)),
            Value = 1,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Run(ThumbScale, "ScaleX", stretch);
    }

    private static void AnimateTo(DependencyObject target, string property, double to, int milliseconds,
        EasingFunctionBase? easing = null)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
            EnableDependentAnimation = true
        };

        Run(target, property, animation);
    }

    private static void Run(DependencyObject target, string property, Timeline animation)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void RailHome_Invoked(object? sender, EventArgs e) => Navigate<HomePage>();
    private void RailLibrary_Invoked(object? sender, EventArgs e) => Navigate<LibraryPage>();
    private void RailFavorites_Invoked(object? sender, EventArgs e) => Navigate<FavoritesPage>();
    private void RailServers_Invoked(object? sender, EventArgs e) => Navigate<ServersPage>();
    private void RailSettings_Invoked(object? sender, EventArgs e) => Navigate<SettingsPage>();

    // ==================== 搜索弹层 ====================

    private SettingsService Settings =>
        _settings ??= App.Services.GetRequiredService<SettingsService>();

    private SettingsService? _settings;

    /// <summary>点侧栏「搜索」：不换页，先在图标右侧弹胶囊输入框 + 历史记录。</summary>
    private void RailSearch_Invoked(object? sender, EventArgs e) => OpenSearchOverlay();

    private void OpenSearchOverlay()
    {
        RefreshSearchHistory();
        PositionSearchPanel();
        SearchOverlay.Visibility = Visibility.Visible;
        SearchInput.Text = "";
        SearchInput.Focus(FocusState.Programmatic);
    }

    private void CloseSearchOverlay() => SearchOverlay.Visibility = Visibility.Collapsed;

    /// <summary>
    /// 弹层贴着搜索图标：左边从圆形图标右缘再让 10，竖直方向与图标居中。
    /// 注意不能用 RailSearch.ActualWidth —— 它把悬停时才滑出的文字标签也算进去了，
    /// 那样胶囊会被推到离图标很远的地方。
    /// 窗口尺寸变化时重新算一次。
    /// </summary>
    private void PositionSearchPanel()
    {
        if (RailSearch.ActualWidth <= 0 || Root.ActualWidth <= 0)
        {
            return;
        }

        const double iconSize = 46;
        var origin = RailSearch.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        var top = origin.Y + Math.Max(0, (RailSearch.ActualHeight - iconSize) / 2);
        SearchPanel.Margin = new Thickness(origin.X + iconSize + 10, top, 0, 0);
    }

    private void RefreshSearchHistory()
    {
        var terms = Settings.Current.SearchHistory
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        SearchHistoryList.ItemsSource = terms;
        SearchHistoryCard.Visibility = terms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>输入完词按回车 / 点放大镜：记进历史，再真正进入搜索页。</summary>
    private void PerformSearch(string? term)
    {
        var query = term?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchInput.Focus(FocusState.Programmatic);
            return;
        }

        Settings.RememberSearchTerm(query);
        CloseSearchOverlay();
        App.Services.GetRequiredService<NavigationService>().NavigateTo<SearchPage>(query);
    }

    private void SearchGo_Click(object sender, RoutedEventArgs e) => PerformSearch(SearchInput.Text);

    private void SearchInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            PerformSearch(SearchInput.Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseSearchOverlay();
        }
    }

    /// <summary>输入过程中把剩下的历史词过滤一下，方便直接点。</summary>
    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var typed = SearchInput.Text.Trim();
        var terms = Settings.Current.SearchHistory
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => typed.Length == 0 || t.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SearchHistoryList.ItemsSource = terms;
        SearchHistoryCard.Visibility = terms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBackdrop_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        CloseSearchOverlay();

    private void SearchHistoryItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string term })
        {
            e.Handled = true;
            PerformSearch(term);
        }
    }

    private void RemoveHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string term })
        {
            Settings.RemoveSearchTerm(term);
            RefreshSearchHistory();
        }
    }

    private void ClearSearchHistory_Click(object sender, RoutedEventArgs e)
    {
        Settings.ClearSearchHistory();
        RefreshSearchHistory();
    }

    /// <summary>
    /// 刷新：首页 / 媒体库是缓存的页面实例（来回切不重新加载），
    /// 想拿到服务器上的最新内容就点这个按钮，强制重新拉一次。
    /// </summary>
    private async void RailRefresh_Invoked(object? sender, EventArgs e)
    {
        // 刷新要的是最新数据，先把元数据缓存丢掉，避免又命中旧响应
        CacheService.Clear(CacheKind.Metadata);

        if (ContentFrame.Content is IRefreshablePage page)
        {
            await page.RefreshAsync();
        }
    }

    private static void Navigate<TPage>() where TPage : Page =>
        App.Services.GetRequiredService<NavigationService>().NavigateToRoot<TPage>();

    /// <summary>登录 / 添加服务器等全屏页面里把悬浮侧栏收起来。</summary>
    public void ShowShellNavigation(bool show)
    {
        Rail.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            UpdateRailSelection();
        }
    }

    /// <summary>为缺少名称的服务器条目补齐 /System/Info 中的 ServerName。</summary>
    private async Task EnsureServerNamesAsync()
    {
        try
        {
            var settings = App.Services.GetRequiredService<SettingsService>();
            var updated = false;
            foreach (var server in settings.Current.Servers.Where(s => string.IsNullOrEmpty(s.ServerName)).ToList())
            {
                var name = await EmbyApiClient.GetServerNameAsync(server.Url, server.AccessToken);
                if (!string.IsNullOrEmpty(name))
                {
                    server.ServerName = name;
                    updated = true;
                }
            }
            if (updated)
            {
                // 名称补全会写回设置；服务器页每次进入自己重新读取，这里不用额外刷新
                settings.Save();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"补齐服务器名失败：{ex}");
        }
    }

    public IntPtr Hwnd => (IntPtr)(long)AppWindow.Id.Value;

    /// <summary>
    /// 标题栏跟随应用主题。窗口顶部已由 XAML 画了云母横条，所以系统标题栏背景一律透明，
    /// 右上角缩小 / 全屏 / 关闭键的背景同样透明 —— 看到的云母就是那条横条本身；
    /// 悬停 / 按下只在云母上叠一层极淡的灰，不让按钮变成一块实色。
    /// </summary>
    private void ApplyThemeTitleBar(ElementTheme theme)
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var dark = theme == ElementTheme.Dark;

            titleBar.BackgroundColor = Colors.Transparent;
            titleBar.InactiveBackgroundColor = Colors.Transparent;
            titleBar.ForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.InactiveForegroundColor = Color.FromArgb(255, 150, 150, 150);

            // 按钮背景透明 → 透出下面的云母条；图标颜色随主题深浅
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 150, 150, 150);

            // 悬停 / 按下：暗主题叠白、亮主题叠黑，各约 12% / 19%
            titleBar.ButtonHoverBackgroundColor = dark
                ? Color.FromArgb(30, 255, 255, 255)
                : Color.FromArgb(26, 0, 0, 0);
            titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonPressedBackgroundColor = dark
                ? Color.FromArgb(48, 255, 255, 255)
                : Color.FromArgb(40, 0, 0, 0);
            titleBar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog($"应用标题栏主题色失败：{ex}");
        }
    }

    /// <summary>
    /// 显示更新对话框。传入非空 update 则提示新版本，传入 null 行为取决于 isAutoCheck：
    /// 自动检查（启动时）静默不弹窗；手动检查（设置页）提示「已是最新版本」。
    /// 有新版本且 Release 附带安装包时，Primary 按钮为「立即更新」：下载后静默覆盖安装。
    /// </summary>
    public async Task ShowUpdateDialogAsync(UpdateInfo? update, bool isAutoCheck = false)
    {
        try
        {
            // 自动检查没发现更新：不打扰用户
            if (update is null && isAutoCheck)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.Content?.XamlRoot
                    ?? throw new InvalidOperationException("MainWindow.Content.XamlRoot 尚未就绪"),
                PrimaryButtonText = "前往下载",
                SecondaryButtonText = "暂不更新",
                DefaultButton = ContentDialogButton.Primary
            };

            if (update is null)
            {
                // 手动检查发现已是最新版
                dialog.Title = "已是最新版本";
                dialog.Content = new TextBlock
                {
                    Text = $"当前版本 v{UpdateService.CurrentVersionString}",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                };
                dialog.PrimaryButtonText = "";
                dialog.SecondaryButtonText = "好的";
                dialog.DefaultButton = ContentDialogButton.Secondary;
            }
            else
            {
                dialog.Title = $"发现新版本 {update.LatestTag}";
                dialog.PrimaryButtonText = update.HasInstaller ? "立即更新" : "前往下载";
                dialog.SecondaryButtonText = "暂不更新";

                // 内容：版本提示 + Release notes（Markdown 原文，纯文本展示）
                var notes = string.IsNullOrEmpty(update.ReleaseNotes)
                    ? "（无更新说明）"
                    : update.ReleaseNotes;
                if (notes.Length > 1500)
                {
                    notes = notes.Substring(0, 1500) + "\n\n……（完整说明见 Release 页面）";
                }

                var contentPanel = new StackPanel { Spacing = 8 };
                contentPanel.Children.Add(new TextBlock
                {
                    Text = $"当前版本 v{UpdateService.CurrentVersionString}  →  最新版本 {update.LatestTag}",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                });

                if (update.HasInstaller)
                {
                    var sizeText = update.InstallerSize > 0
                        ? $"安装包：{update.InstallerAssetName}（{DownloadProgress.FormatBytes(update.InstallerSize)}）"
                        : $"安装包：{update.InstallerAssetName}";
                    contentPanel.Children.Add(new TextBlock
                    {
                        Text = $"{sizeText}，点「立即更新」将自动下载并覆盖安装，完成后重新启动。",
                        Opacity = 0.7,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                contentPanel.Children.Add(new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = notes,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    },
                    MaxHeight = 320,
                    Padding = new Thickness(0)
                });

                // 下载进度区（默认隐藏，点「立即更新」后显示）
                var statusText = new TextBlock
                {
                    Visibility = Visibility.Collapsed,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                    FontSize = 13
                };
                var progressBar = new ProgressBar
                {
                    Visibility = Visibility.Collapsed,
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0
                };
                contentPanel.Children.Add(statusText);
                contentPanel.Children.Add(progressBar);

                if (update.HasInstaller)
                {
                    var updateService = App.Services.GetRequiredService<UpdateService>();
                    dialog.PrimaryButtonClick += (sender, args) =>
                    {
                        // 保持对话框打开，下载进度直接显示在弹窗里
                        var deferral = args.GetDeferral();
                        args.Cancel = true;
                        _ = RunAutoUpdateAsync(update, updateService, dialog, statusText, progressBar, deferral);
                    };
                }

                dialog.Content = contentPanel;
            }

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && update is not null && !update.HasInstaller && !string.IsNullOrEmpty(update.ReleaseUrl))
            {
                await Launcher.LaunchUriAsync(new Uri(update.ReleaseUrl));
            }
        }
        catch (Exception ex)
        {
            // XamlRoot 未就绪 / 弹窗竞态等：写崩溃日志但不影响用户使用
            App.WriteCrashLog($"显示更新对话框失败：{ex}");
        }
    }

    /// <summary>
    /// 「立即更新」流程：下载安装包 → 静默启动安装 → 关闭本程序（安装完成后由安装包重新启动）。
    /// 失败时在弹窗内显示原因并恢复按钮，用户可重试或前往 Release 页面手动下载。
    /// </summary>
    private static async Task RunAutoUpdateAsync(
        UpdateInfo update,
        UpdateService updateService,
        ContentDialog dialog,
        TextBlock statusText,
        ProgressBar progressBar,
        ContentDialogButtonClickDeferral deferral)
    {
        try
        {
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            statusText.Visibility = Visibility.Visible;
            progressBar.Visibility = Visibility.Visible;
            progressBar.ShowError = false;
            progressBar.Value = 0;

            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.TotalBytes is > 0)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = p.Ratio * 100;
                    statusText.Text = $"正在下载安装包… {p.ReceivedText} / {p.TotalText}";
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                    statusText.Text = $"正在下载安装包… {p.ReceivedText}";
                }
            });

            statusText.Text = "正在下载安装包…";
            var installerPath = await updateService.DownloadInstallerAsync(update, progress);

            statusText.Text = "下载完成，正在启动安装程序…（本程序即将关闭，安装完成后自动重启）";
            progressBar.IsIndeterminate = true;
            await Task.Delay(400);

            if (UpdateService.LaunchInstaller(installerPath))
            {
                // 退出自身：WinUI3 的 Application.Exit 不能保证进程立即结束，直接用 Environment.Exit
                App.ExitForUpdate();
                return;
            }

            throw new InvalidOperationException("无法启动安装程序（可能已取消管理员授权，或被安全软件拦截）。");
        }
        catch (Exception ex)
        {
            App.WriteCrashLog($"自动更新失败：{ex}");
            progressBar.IsIndeterminate = false;
            progressBar.ShowError = true;
            statusText.Text = $"更新失败：{ex.Message}\n你也可以前往 Release 页面手动下载安装。";
            dialog.IsPrimaryButtonEnabled = true;
            dialog.IsSecondaryButtonEnabled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }
}
