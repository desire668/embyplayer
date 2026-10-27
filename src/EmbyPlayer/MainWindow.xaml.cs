using EmbyPlayer.Models;
using EmbyPlayer.Services;
using EmbyPlayer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.IO;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace EmbyPlayer;

/// <summary>
/// 侧栏宽度拖拽条：完全透明的热区，只在鼠标变成左右箭头光标时暴露自己。两个关键点：
/// 1) WinUI3 里 UIElement.ProtectedCursor 是受保护成员，必须派生控件才能设置左右调整箭头光标；
/// 2) 必须由这个 Grid 自己作为命中目标（Background 设 Transparent 才参与命中测试），
///    若套一层子元素，指针会命中到没有光标的子元素，表现成"鼠标放上去没反应、也拖不动"。
/// </summary>
public sealed class PaneResizeGrip : Grid
{
    public PaneResizeGrip() =>
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}

public sealed partial class MainWindow : Window
{
    /// <summary>侧栏宽度可拖动范围与默认值。</summary>
    private const int MinPaneWidth = 180;
    private const int MaxPaneWidth = 480;
    private const int DefaultPaneWidth = 320;

    private bool _suppressServerChange;
    private bool _resizingPane;

    public MainWindow()
    {
        InitializeComponent();

        var navigation = App.Services.GetRequiredService<NavigationService>();
        navigation.Frame = ContentFrame;
        navigation.Shell = this;

        AppWindow.Resize(new SizeInt32(1280, 800));

        // 设置窗口标题栏图标
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app-icon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // 标题栏跟随应用主题：暗色纯黑、亮色白色，主题切换时同步刷新
        // （Window 本身无 ActualTheme，借助根元素 NavView 的主题）
        ApplyThemeTitleBar(NavView.ActualTheme);
        NavView.ActualThemeChanged += (_, _) => ApplyThemeTitleBar(NavView.ActualTheme);

        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.ServersChanged += () => DispatcherQueue.TryEnqueue(RefreshServerBox);
        RefreshServerBox();
        _ = EnsureServerNamesAsync();

        InitPaneResizer();
    }

    /// <summary>
    /// 左栏宽度：先用记住的宽度，再挂上拖拽条。
    /// NavigationView 自身用 PaneDisplayMode=Auto 按窗口宽度在「展开 / 图标栏 / 抽屉」间自适应，
    /// 展开时用户可以拖动右边缘改宽，双击恢复默认，改动写回设置。
    /// </summary>
    private void InitPaneResizer()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        var width = settings.Current.NavigationPaneWidth;
        if (width < MinPaneWidth || width > MaxPaneWidth)
        {
            width = DefaultPaneWidth;
        }

        NavView.OpenPaneLength = width;

        // 首帧布局完成时 DisplayMode 可能还是 Compact，只在 Loaded 里算一次会一直判成"不展开"，
        // 所以窗口缩放 / 侧栏开合 / 布局变化都要重算（UpdateResizer 自身幂等，不会抖动）
        NavView.SizeChanged += (_, _) => UpdateResizer();
        NavView.LayoutUpdated += (_, _) => UpdateResizer();
        UpdateResizer();
    }

    /// <summary>拖拽条只在左栏真正展开（且侧栏可见）时贴在左栏右边缘，避免挡住内容点击。</summary>
    private void UpdateResizer()
    {
        var visible = NavView.IsPaneVisible &&
                      NavView.DisplayMode == NavigationViewDisplayMode.Expanded &&
                      NavView.IsPaneOpen;

        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (PaneResizer.Visibility != target)
        {
            PaneResizer.Visibility = target;
        }

        if (!visible)
        {
            return;
        }

        // 拖拽条中心对准左栏右边缘（Width=8，所以左移半个身位）
        var left = NavView.OpenPaneLength - 4;
        if (Math.Abs(PaneResizer.Margin.Left - left) > 0.5)
        {
            PaneResizer.Margin = new Thickness(left, 0, 0, 0);
        }
    }

    private void NavView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) =>
        UpdateResizer();

    private void PaneResizer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _resizingPane = true;
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PaneResizer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingPane)
        {
            return;
        }

        // Root 铺满窗口内容区，指针 X 即目标宽度
        var x = e.GetCurrentPoint(Root).Position.X;
        NavView.OpenPaneLength = Math.Clamp((int)Math.Round(x), MinPaneWidth, MaxPaneWidth);
        UpdateResizer();
        e.Handled = true;
    }

    private void PaneResizer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingPane)
        {
            return;
        }

        _resizingPane = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);

        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.Current.NavigationPaneWidth = (int)NavView.OpenPaneLength;
        settings.Save();
        e.Handled = true;
    }

    private void PaneResizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        NavView.OpenPaneLength = DefaultPaneWidth;
        UpdateResizer();

        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.Current.NavigationPaneWidth = DefaultPaneWidth;
        settings.Save();
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
                settings.Save();
                DispatcherQueue.TryEnqueue(RefreshServerBox);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"补齐服务器名失败：{ex}");
        }
    }

    public IntPtr Hwnd => (IntPtr)(long)AppWindow.Id.Value;

    /// <summary>标题栏跟随应用主题：暗色纯黑白字、亮色白底黑字；按钮悬停/按下用中性灰阶。</summary>
    private void ApplyThemeTitleBar(ElementTheme theme)
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            if (theme == ElementTheme.Dark)
            {
                titleBar.BackgroundColor = Colors.Black;
                titleBar.ForegroundColor = Colors.White;
                titleBar.InactiveBackgroundColor = Colors.Black;
                titleBar.InactiveForegroundColor = Color.FromArgb(255, 160, 160, 160);

                titleBar.ButtonBackgroundColor = Colors.Black;
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 40, 40, 40);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 64, 64, 64);
                titleBar.ButtonPressedForegroundColor = Colors.White;
            }
            else
            {
                titleBar.BackgroundColor = Colors.White;
                titleBar.ForegroundColor = Colors.Black;
                titleBar.InactiveBackgroundColor = Colors.White;
                titleBar.InactiveForegroundColor = Color.FromArgb(255, 160, 160, 160);

                titleBar.ButtonBackgroundColor = Colors.White;
                titleBar.ButtonForegroundColor = Colors.Black;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 229, 229, 229);
                titleBar.ButtonHoverForegroundColor = Colors.Black;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 204, 204, 204);
                titleBar.ButtonPressedForegroundColor = Colors.Black;
            }

            titleBar.ButtonInactiveBackgroundColor = titleBar.InactiveBackgroundColor;
            titleBar.ButtonInactiveForegroundColor = titleBar.InactiveForegroundColor;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog($"应用标题栏主题色失败：{ex}");
        }
    }

    private void RefreshServerBox()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        _suppressServerChange = true;
        // 末项是内置的「TMDB 在线推荐」，已添加服务器的用户可直接切过去看在线海报墙
        // 注意：选中项必须取自同一个列表实例，否则 ComboBox 匹配不到会显示空白
        var options = settings.SourceOptions;
        ServerBox.ItemsSource = options;
        ServerBox.SelectedItem =
            options.FirstOrDefault(o => o.Id == settings.CurrentSourceOption?.Id)
            // 还没有任何服务器时，直接停在 TMDB 上（首页也正是 TMDB 内容）
            ?? options.FirstOrDefault(o => o.IsTmdb);
        _suppressServerChange = false;
    }

    private void ServerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressServerChange)
        {
            return;
        }
        if (ServerBox.SelectedItem is not ContentSourceOption option)
        {
            return;
        }
        var settings = App.Services.GetRequiredService<SettingsService>();
        // 选中项没变（含「已选中」与「切到空」）时不做任何跳转，避免首次填充下拉就触发导航
        if (option.Id == settings.CurrentSourceOption?.Id)
        {
            return;
        }

        // 延迟到 ComboBox 事件处理完成后执行，避免在回调中切换页面导致 WinUI stowed exception 闪退
        DispatcherQueue.TryEnqueue(() =>
        {
            settings.SetSourceMode(option.IsTmdb);

            // 切到某个服务器时同步切换 API 凭据；切到 TMDB 时保留当前服务器，
            // 这样首页大图点「播放」仍能按片名在自己的媒体库里检索并播放。
            if (option.Server is { } server)
            {
                settings.SwitchServer(server.Id);
                var api = App.Services.GetRequiredService<EmbyApiClient>();
                api.Configure(server.Url, server.AccessToken);
            }

            var navigation = App.Services.GetRequiredService<NavigationService>();
            navigation.ShowShellNavigation(true);
            navigation.NavigateTo<HomePage>();
        });
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        // 收起下拉并延迟导航，避免弹窗/选择处理期间切换 Frame 引发闪退
        ServerBox.IsDropDownOpen = false;

        var navigation = App.Services.GetRequiredService<NavigationService>();
        DispatcherQueue.TryEnqueue(() =>
        {
            navigation.ShowShellNavigation(false);
            navigation.NavigateTo<LoginPage>("add");
        });
    }

    public void ShowShellNavigation(bool show)
    {
        NavView.IsPaneVisible = show;
        if (show)
        {
            NavView.SelectedItem = NavView.MenuItems.FirstOrDefault();
        }
        UpdateResizer();
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var navigation = App.Services.GetRequiredService<NavigationService>();
        switch (args.InvokedItemContainer?.Tag as string)
        {
            case "Home":
                navigation.NavigateTo<HomePage>();
                break;
            case "Library":
                navigation.NavigateTo<LibraryPage>();
                break;
            case "Search":
                navigation.NavigateTo<SearchPage>();
                break;
            case "Favorites":
                navigation.NavigateTo<FavoritesPage>();
                break;
            case "Settings":
                navigation.NavigateTo<SettingsPage>();
                break;
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
