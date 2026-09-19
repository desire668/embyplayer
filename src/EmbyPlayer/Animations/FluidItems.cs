using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyPlayer.Animations;

/// <summary>
/// 影片墙的错峰入场：新卡片从下方 14px、0 透明度浮起，按索引递延 16ms。
/// 换算到 Apple 的语言：这是「时间延迟」而非「时长」，每个卡片自己跑一条弹簧，
/// 所以滚动过程中被打断、被回收都不会出现跳帧。
///
/// 只作用于首屏：切分类 / 排序会整体替换列表、重建全部容器，如果每次都重播，
/// 整屏卡片会一起消失再浮现，看起来就是「闪一下」。
/// </summary>
public static class FluidItems
{
    /// <summary>入场只播给最先 realize 的这么多个容器（约等于首屏 + 一屏预取）。</summary>
    private const int FirstScreenCount = 48;

    public static readonly DependencyProperty EntranceEnabledProperty =
        DependencyProperty.RegisterAttached("EntranceEnabled", typeof(bool), typeof(FluidItems),
            new PropertyMetadata(false, OnEntranceEnabledChanged));

    /// <summary>入场起始位移（px，向上浮起）。</summary>
    public static readonly DependencyProperty EntranceOffsetProperty =
        DependencyProperty.RegisterAttached("EntranceOffset", typeof(double), typeof(FluidItems),
            new PropertyMetadata(14d));

    /// <summary>相邻卡片的递延间隔（秒）。</summary>
    public static readonly DependencyProperty EntranceStaggerProperty =
        DependencyProperty.RegisterAttached("EntranceStagger", typeof(double), typeof(FluidItems),
            new PropertyMetadata(0.016d));

    /// <summary>递延总上限（秒），避免长列表尾部等太久。</summary>
    public static readonly DependencyProperty EntranceMaxDelayProperty =
        DependencyProperty.RegisterAttached("EntranceMaxDelay", typeof(double), typeof(FluidItems),
            new PropertyMetadata(0.16d));

    private static readonly DependencyProperty PlayedProperty =
        DependencyProperty.RegisterAttached("Played", typeof(bool), typeof(FluidItems),
            new PropertyMetadata(false));

    // 记录每个列表播到第几个容器了，以及上一次的索引（用于识别快速滚动）
    private static readonly ConditionalWeakTable<ListViewBase, IndexTracker> Trackers = new();

    private sealed class IndexTracker
    {
        public int LastIndex;
        public int Played;
    }

    /// <summary>重置入场计数，让下一次填充重新播一次（切换整页内容时用）。</summary>
    public static void ResetEntrance(ListViewBase list)
    {
        if (Trackers.TryGetValue(list, out var tracker))
        {
            tracker.Played = 0;
        }
    }

    public static bool GetEntranceEnabled(DependencyObject obj) => (bool)obj.GetValue(EntranceEnabledProperty);
    public static void SetEntranceEnabled(DependencyObject obj, bool value) => obj.SetValue(EntranceEnabledProperty, value);

    public static double GetEntranceOffset(DependencyObject obj) => (double)obj.GetValue(EntranceOffsetProperty);
    public static void SetEntranceOffset(DependencyObject obj, double value) => obj.SetValue(EntranceOffsetProperty, value);

    public static double GetEntranceStagger(DependencyObject obj) => (double)obj.GetValue(EntranceStaggerProperty);
    public static void SetEntranceStagger(DependencyObject obj, double value) => obj.SetValue(EntranceStaggerProperty, value);

    public static double GetEntranceMaxDelay(DependencyObject obj) => (double)obj.GetValue(EntranceMaxDelayProperty);
    public static void SetEntranceMaxDelay(DependencyObject obj, double value) => obj.SetValue(EntranceMaxDelayProperty, value);

    private static void OnEntranceEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListViewBase list)
        {
            return;
        }

        list.ContainerContentChanging -= OnContainerContentChanging;
        if (true.Equals(e.NewValue))
        {
            list.ContainerContentChanging += OnContainerContentChanging;
        }
    }

    private static void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var container = args.ItemContainer;
        if (container is null)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            container.ClearValue(PlayedProperty);
            container.SetInstant("Translation", Vector3.Zero);
            container.Opacity = 1;
            return;
        }

        if (args.Phase != 0 || true.Equals(container.GetValue(PlayedProperty)))
        {
            return;
        }

        var tracker = Trackers.GetValue(sender, static _ => new IndexTracker());
        var jumped = Math.Abs(args.ItemIndex - tracker.LastIndex) > 40;
        tracker.LastIndex = args.ItemIndex;

        // 首屏播完就不再播：后续无论是滚动还是整表刷新，卡片都直接就位
        if (jumped || tracker.Played >= FirstScreenCount)
        {
            return;
        }

        container.SetValue(PlayedProperty, true);
        tracker.Played++;

        var offset = (float)GetEntranceOffset(sender);
        var stagger = GetEntranceStagger(sender);
        var maxDelay = GetEntranceMaxDelay(sender);
        var delay = Math.Min(args.ItemIndex * stagger, maxDelay);

        Reveal(container, delay, offset);
    }

    /// <summary>通用浮现：从下方 offset 像素、透明状态浮起到位。页面内容也可直接调用。</summary>
    public static void Reveal(UIElement element, double delaySeconds = 0, float offset = 28f)
    {
        if (!FluidMotion.AnimationsEnabled)
        {
            return;
        }

        element.SetInstant("Translation", new Vector3(0f, offset, 0f));
        element.Opacity = 0;
        element.SpringTo("Translation", Vector3.Zero, FluidSpring.Entrance, delaySeconds: delaySeconds);
        element.SpringTo("Opacity", 1f, FluidSpring.Entrance, delaySeconds: delaySeconds);
    }
}
