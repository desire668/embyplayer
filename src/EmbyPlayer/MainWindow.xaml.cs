using EmbyPlayer.Models;
using EmbyPlayer.Services;
using EmbyPlayer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using Windows.Graphics;

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
}
