using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyPlayer.Animations;

/// <summary>
/// 页面转场与共享元素动画。
/// Apple 的做法：进入下一级是「从触发源长出来」，不是整页闪一下 ——
/// 卡片海报作为共享元素贯穿两个页面，页面自身的转场反而要让位（Suppress）。
/// </summary>
public static class FluidNavigation
{
    private const string PosterKey = "FluidPosterHero";
    private static bool _posterPrepared;

    /// <summary>前进：从右侧推入。</summary>
    public static NavigationTransitionInfo Forward =>
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight };

    /// <summary>返回：从左侧退回。</summary>
    public static NavigationTransitionInfo Backward =>
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft };

    /// <summary>已经有共享元素动画时抑制整页转场，避免两套动画打架。</summary>
    public static NavigationTransitionInfo PosterPush =>
        _posterPrepared
            ? new SuppressNavigationTransitionInfo()
            : Forward;

    /// <summary>把列表里的海报登记为共享元素（导航前调用）。</summary>
    public static void PreparePoster(UIElement? source)
    {
        _posterPrepared = false;
        if (source is null || !FluidMotion.AnimationsEnabled)
        {
            return;
        }

        try
        {
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(PosterKey, source);
            _posterPrepared = true;
        }
        catch
        {
            _posterPrepared = false;
        }
    }

    /// <summary>在详情页把共享元素动画接上（海报加载完成后调用）。</summary>
    public static void StartPoster(UIElement target, IEnumerable<UIElement>? coordinated = null)
    {
        _posterPrepared = false;
        if (!FluidMotion.AnimationsEnabled)
        {
            return;
        }

        try
        {
            var animation = ConnectedAnimationService.GetForCurrentView().GetAnimation(PosterKey);
            if (animation is null)
            {
                return;
            }

            if (coordinated is null)
            {
                animation.TryStart(target);
            }
            else
            {
                animation.TryStart(target, coordinated);
            }
        }
        catch
        {
            // 共享元素动画失败不影响页面显示
        }
    }

    /// <summary>在视觉树中按类型找子元素（用于从 GridView 容器里取出卡片控件）。</summary>
    public static T? FindDescendant<T>(DependencyObject? root) where T : class
    {
        if (root is null)
        {
            return null;
        }

        if (root is T match)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
