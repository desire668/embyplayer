using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyPlayer.Views;

public sealed partial class ServersPage : Page
{
    public ServersViewModel ViewModel { get; }

    public ServersPage()
    {
        ViewModel = App.Services.GetRequiredService<ServersViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 每次进来都重读设置：从添加服务器页返回时列表要即时更新
        ViewModel.Refresh();
    }

    /// <summary>点整行即切换为当前内容源（右侧的编辑 / 删除按钮不会冒泡到这里）。</summary>
    private void ServerRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerListItem item })
        {
            return;
        }

        // 延迟到点击事件处理完再切页，避免在回调里换 Frame 触发 WinUI stowed exception
        DispatcherQueue.TryEnqueue(() => ViewModel.Switch(item));
    }

    private void AddServer_Click(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(ViewModel.AddServer);

    /// <summary>编辑服务器别名：只改显示名，地址与令牌不动。</summary>
    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerListItem item })
        {
            return;
        }

        var box = new TextBox
        {
            Text = item.Name,
            PlaceholderText = "给这台服务器起个名字",
            MinWidth = 320
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑服务器名称",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = item.Detail, FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap
                    },
                    box
                }
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Rename(item.Id, box.Text);
        }
    }

    /// <summary>删除服务器：二次确认后连同登录凭据一起从本机移除。</summary>
    private async void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerListItem item })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除这台服务器？",
            Content = $"将从本机删除「{item.Name}」以及它的登录凭据；服务器上的媒体文件不受影响。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Remove(item.Id);
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "退出当前账号？",
            Content = "会移除当前这条服务器记录；已添加其它服务器时自动切换过去。",
            PrimaryButtonText = "退出登录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            DispatcherQueue.TryEnqueue(ViewModel.Logout);
        }
    }
}
