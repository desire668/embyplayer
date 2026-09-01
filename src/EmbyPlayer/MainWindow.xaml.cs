using EmbyPlayer.Models;
using EmbyPlayer.Services;
using EmbyPlayer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using Windows.Graphics;
using Windows.System;

namespace EmbyPlayer;

public sealed partial class MainWindow : Window
{
    private bool _suppressServerChange;

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

        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.ServersChanged += () => DispatcherQueue.TryEnqueue(RefreshServerBox);
        RefreshServerBox();
        _ = EnsureServerNamesAsync();
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

    private void RefreshServerBox()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        _suppressServerChange = true;
        ServerBox.ItemsSource = settings.Current.Servers;
        ServerBox.SelectedItem = settings.CurrentServer;
        _suppressServerChange = false;
    }

    private void ServerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressServerChange)
        {
            return;
        }
        if (ServerBox.SelectedItem is not ServerEntry server)
        {
            return;
        }
        var settings = App.Services.GetRequiredService<SettingsService>();
        if (settings.CurrentServer?.Id == server.Id)
        {
            return;
        }

        // 延迟到 ComboBox 事件处理完成后执行，避免在回调中切换页面导致 WinUI stowed exception 闪退
        DispatcherQueue.TryEnqueue(() =>
        {
            settings.SwitchServer(server.Id);

            var api = App.Services.GetRequiredService<EmbyApiClient>();
            api.Configure(server.Url, server.AccessToken);

            var navigation = App.Services.GetRequiredService<NavigationService>();
            navigation.ShowShellNavigation(true);
            navigation.NavigateTo<LibraryPage>();
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
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var navigation = App.Services.GetRequiredService<NavigationService>();
        switch (args.InvokedItemContainer?.Tag as string)
        {
            case "Library":
                navigation.NavigateTo<LibraryPage>();
                break;
            case "Resume":
                navigation.NavigateTo<ResumePage>();
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

                dialog.Content = contentPanel;
            }

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && update is not null && !string.IsNullOrEmpty(update.ReleaseUrl))
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
}
