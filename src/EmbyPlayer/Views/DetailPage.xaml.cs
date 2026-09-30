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

        // 剧集元数据回来后才知道要不要显示跳转按钮（电影详情页不显示）
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DetailViewModel.IsSeries) or nameof(DetailViewModel.EpisodesVisibility))
            {
                UpdateJumpButton();
            }
        };

        Loaded += (_, _) => UpdateJumpButton();
    }

    /// <summary>共享元素动画是否已接管海报（每帧只播一次）。</summary>
    private bool _posterHandled;

    /// <summary>当前是否已滚动到底部（决定跳转按钮的方向）。</summary>
    private bool _atBottom;

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

    /// <summary>滚动到底／回顶部的快捷跳转。</summary>
    private void JumpButton_Click(object sender, RoutedEventArgs e)
    {
        var target = _atBottom ? 0.0 : RootScroll.ExtentHeight;
        // 第四个参数 = 禁用动画，跟随「减弱动效」开关
        RootScroll.ChangeView(null, target, null, FluidMotion.ReduceMotion);
    }

    private void RootScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdateJumpButton();

    private void EpisodePanel_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateJumpButton();

    /// <summary>根据内容高度与滚动位置决定按钮是否显示、以及它是「到底部」还是「回顶部」。</summary>
    private void UpdateJumpButton()
    {
        if (!IsLoaded)
        {
            return;
        }

        var scrollableHeight = RootScroll.ExtentHeight - RootScroll.ViewportHeight;
        // 内容不够长（或不是剧集）时没必要显示
        JumpButton.Visibility = scrollableHeight > 24 && ViewModel.IsSeries
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 距底部 24px 以内视为已到底（容忍布局缩放带来的误差）
        _atBottom = scrollableHeight - RootScroll.VerticalOffset <= 24;

        JumpIcon.Glyph = _atBottom ? "\uE70D" : "\uE70E";
        JumpLabel.Text = _atBottom ? "回顶部" : "到底部";
        ToolTipService.SetToolTip(JumpButton, _atBottom ? "回到页面顶部" : "直达集列表底部");
    }

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
