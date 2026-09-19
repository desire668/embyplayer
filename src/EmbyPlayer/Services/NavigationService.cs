using EmbyPlayer.Animations;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyPlayer.Services;

public sealed class NavigationService
{
    public Frame? Frame { get; set; }
    public MainWindow? Shell { get; set; }

    /// <summary>
    /// 导航到指定页面。transition 为空时走 Frame 默认转场；
    /// 下钻到详情时传 FluidNavigation.PosterPush —— 有共享元素动画就抑制整页转场。
    /// </summary>
    public void NavigateTo<TPage>(object? parameter = null, NavigationTransitionInfo? transition = null)
        where TPage : Page =>
        Frame?.Navigate(typeof(TPage), parameter, transition);

    public void GoBack()
    {
        if (Frame?.CanGoBack != true)
        {
            return;
        }

        // 返回用向左退回，与前进方向严格对称（Apple：进出路径必须互为镜像）
        var transition = FluidMotion.AnimationsEnabled
            ? FluidNavigation.Backward
            : new SuppressNavigationTransitionInfo();

        Frame.GoBack(transition);
    }

    public void ShowShellNavigation(bool show) => Shell?.ShowShellNavigation(show);
}
