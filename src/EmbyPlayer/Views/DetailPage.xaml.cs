using EmbyPlayer.Animations;
using EmbyPlayer.Services;
using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
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

            // 「播放已结束」这类一次性消息改成轻提示，不占页面空间
            if (args.PropertyName == nameof(DetailViewModel.StatusText))
            {
                ShowToast(ViewModel.StatusText);
            }
        };

        Loaded += (_, _) => UpdateJumpButton();
    }

    // ==================== 轻提示 ====================

    private CancellationTokenSource? _toastCts;

    /// <summary>
    /// 底部浮一条短提示，2.4 秒后自己淡出。不参与布局，不会把页面顶长。
    /// </summary>
    private async void ShowToast(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IsLoaded)
        {
            return;
        }

        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;

        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        ToastShift.Y = 10;

        Animate(Toast, "Opacity", 1, 160);
        Animate(ToastShift, "Y", 0, 200);

        try
        {
            await Task.Delay(2400, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        Animate(Toast, "Opacity", 0, 220);
        try
        {
            await Task.Delay(230, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!cts.IsCancellationRequested)
        {
            Toast.Visibility = Visibility.Collapsed;
        }
    }

    private static void Animate(DependencyObject target, string property, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>共享元素动画是否已接管海报（每帧只播一次）。</summary>
    private bool _posterHandled;

    /// <summary>当前是否已滚动到底部（决定跳转按钮的方向）。</summary>
    private bool _atBottom;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _posterHandled = false;

        // 参数约定："autoplay:<itemId>" 表示加载完成后直接开始播放（海报墙首页大图「播放」按钮），
        // 其余情况参数就是 itemId 本身
        var param = e.Parameter as string;
        var autoPlay = false;
        var itemId = param;
        if (param?.StartsWith("autoplay:", StringComparison.Ordinal) == true)
        {
            autoPlay = true;
            itemId = param["autoplay:".Length..];
        }

        // "tmdb:<id>"：媒体库里没有这部片，详情页直接用 TMDB 的在线信息渲染
        if (param?.StartsWith("tmdb:", StringComparison.Ordinal) == true &&
            int.TryParse(param["tmdb:".Length..], out var tmdbId))
        {
            _ = ViewModel.LoadTmdbAsync(tmdbId);
        }
        else if (itemId is not null)
        {
            _ = LoadAndMaybePlayAsync(itemId, autoPlay);
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

    /// <summary>加载详情，海报墙首页带 autoplay 标记时在加载完成后立即触发播放。</summary>
    private async Task LoadAndMaybePlayAsync(string itemId, bool autoPlay)
    {
        await ViewModel.LoadAsync(itemId);
        if (autoPlay && ViewModel.ErrorMessage is null)
        {
            await ViewModel.PlayCommand.ExecuteAsync(null);
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

    /// <summary>
    /// 剧名 logo 是网络图片，即使 URL 拿到了也可能下不下来；
    /// 失败就把 logo 置空，界面自动退回文字片名，不留一块空白。
    /// </summary>
    private void TitleLogo_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ViewModel.TitleLogo = null;
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

    private void Episodes_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EpisodeRow row)
        {
            ViewModel.PlayEpisodeCommand.Execute(row.Item);
        }
    }

    /// <summary>「播放源」按钮：弹出已添加的服务器列表，点哪台就用哪台播。</summary>
    private void PlaySourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel.PlaybackSources.Count == 0)
        {
            return;
        }

        var flyout = new MenuFlyout();
        var currentId = ViewModel.SelectedPlaybackSource?.ServerId;
        foreach (var source in ViewModel.PlaybackSources)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = source.Name,
                IsChecked = source.ServerId == currentId
            };
            var option = source;
            item.Click += (_, _) => ViewModel.SelectPlaybackSourceCommand.Execute(option);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }

    /// <summary>点分类胶囊：跳媒体库并按该分类过滤。</summary>
    private void GenreChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GenreChip chip })
        {
            ViewModel.OpenGenreCommand.Execute(chip);
        }
    }

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
