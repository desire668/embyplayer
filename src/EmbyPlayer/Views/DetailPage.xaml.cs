using EmbyPlayer.Animations;
using EmbyPlayer.Services;
using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace EmbyPlayer.Views;

public sealed partial class DetailPage : Page
{
    public DetailViewModel ViewModel { get; }

    public DetailPage()
    {
        ViewModel = App.Services.GetRequiredService<DetailViewModel>();
        InitializeComponent();
        ViewModel.RequestSubtitleChoice += ShowSubtitleChoiceAsync;
    }

    /// <summary>共享元素动画是否已接管海报（每帧只播一次）。</summary>
    private bool _posterHandled;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _posterHandled = false;

        if (e.Parameter is string itemId)
        {
            _ = ViewModel.LoadAsync(itemId);
        }

        // 文字区与剧集区错峰浮起，让视线先落在海报上
        FluidItems.Reveal(InfoPanel, 0.06, 20f);
        FluidItems.Reveal(EpisodePanel, 0.14, 20f);

        // 没有海报图时不必等 ImageOpened，直接结束等待
        if (ViewModel.PosterSource is null)
        {
            _posterHandled = true;
            FluidNavigation.StartPoster(DetailPoster);
        }
    }

    /// <summary>海报解码完成后再接共享元素动画，避免先闪一下空框。</summary>
    private void DetailPoster_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (_posterHandled)
        {
            return;
        }

        _posterHandled = true;
        FluidNavigation.StartPoster(DetailPoster);
    }

    private async Task ShowSubtitleChoiceAsync()
    {
        SubtitleDialog.XamlRoot = XamlRoot;
        var result = await SubtitleDialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.ConfirmPlayAsync();
        }
    }

    private void Episodes_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.PlayEpisodeCommand.Execute(e.ClickedItem);

    private void BackButton_Click(object sender, RoutedEventArgs e) => ViewModel.GoBack();

    /// <summary>浏览本地弹幕 XML 文件（bilibili 格式）。</summary>
    private async void BrowseDanmaku_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".xml");

        var navigation = App.Services.GetRequiredService<NavigationService>();
        var hwnd = (navigation.Shell as MainWindow)?.Hwnd ?? IntPtr.Zero;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.SetDanmakuFile(file.Path);
            // 选择文件后自动开启弹幕开关
            if (!ViewModel.DanmakuEnabled)
            {
                ViewModel.DanmakuEnabled = true;
            }
        }
    }
}
