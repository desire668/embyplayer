using System.Diagnostics;
using EmbyPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyPlayer.Views;

/// <summary>卡片被点击：卡片数据 + 共享元素动画用的海报元素。</summary>
public sealed class MediaSectionItemEventArgs : EventArgs
{
    public MediaSectionItemEventArgs(ItemCard card, PosterCard? poster)
    {
        Card = card;
        Poster = poster;
    }

    public ItemCard Card { get; }
    public PosterCard? Poster { get; }
}

/// <summary>
/// 媒体库概览页的一个横向分类区。
///
/// 卡片列表与首页海报行同款（ListView + IsItemClickEnabled + ItemClick），
/// 卡片动画直接复用 PosterCard（悬停放大 / 按下缩放 / 视差 / 阴影），
/// 点击由 ListView 原生处理，不再有自绘拖动抢指针、吞点击的问题。
/// 另外保留：垂直滚轮转横向、触控板水平滑动、悬停显隐的左右圆形箭头
/// （点击箭头平滑滚动约一个视口的 70%，到边界自动隐藏并禁用）。
/// </summary>
public sealed partial class HorizontalMediaSection : UserControl, System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>x:Bind 的 OneWay 靠这个刷新（ViewAllVisibility / DescriptionVisibility 是计算属性）。</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    public HorizontalMediaSection()
    {
        InitializeComponent();

        // 悬停显隐箭头：区域级事件挂在根上（卡片里进出也算区域内）
        Root.PointerEntered += (_, _) => SetArrowsVisible(true);
        Root.PointerExited += (_, _) => SetArrowsVisible(false);

        // 模板应用后才能拿到 ListView 内部的 ScrollViewer
        Loaded += (_, _) => EnsureScroller();
        Unloaded += StopSmoothScroll;
    }

    // ==================== 依赖属性 ====================

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(HorizontalMediaSection),
        new PropertyMetadata(string.Empty));

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(HorizontalMediaSection),
        new PropertyMetadata(null, (d, _) =>
            ((HorizontalMediaSection)d).OnPropertyChanged(nameof(DescriptionVisibility))));

    public System.Collections.IEnumerable? Items
    {
        get => (System.Collections.IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(System.Collections.IEnumerable), typeof(HorizontalMediaSection),
        new PropertyMetadata(null, (d, _) => ((HorizontalMediaSection)d).OnItemsChanged()));

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(HorizontalMediaSection),
        new PropertyMetadata(true));

    /// <summary>绑定的分类数据（「查看全部」时回传给页面决定去哪）。</summary>
    public MediaSection? Section
    {
        get => (MediaSection?)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(
        nameof(Section), typeof(MediaSection), typeof(HorizontalMediaSection),
        new PropertyMetadata(null, (d, _) => ((HorizontalMediaSection)d).OnSectionChanged()));

    /// <summary>「查看全部」按钮显隐：跟绑定的分类数据（空数据 / 加载中不显示）。</summary>
    public Visibility ViewAllVisibility =>
        Section?.ViewAllVisibility ?? Visibility.Collapsed;

    /// <summary>分类描述显隐：没写描述就不占位。</summary>
    public Visibility DescriptionVisibility =>
        string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;

    // Section 变化 / 其属性变化时刷新按钮显隐（x:Bind 的 OneWay 根在这里）
    private void OnSectionChanged()
    {
        if (_trackedSection is not null)
        {
            _trackedSection.PropertyChanged -= OnSectionPropertyChanged;
        }
        _trackedSection = Section;
        if (_trackedSection is not null)
        {
            _trackedSection.PropertyChanged += OnSectionPropertyChanged;
        }
        OnPropertyChanged(nameof(ViewAllVisibility));
    }

    private MediaSection? _trackedSection;

    private void OnSectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaSection.ViewAllVisibility))
        {
            OnPropertyChanged(nameof(ViewAllVisibility));
        }
    }

    // ==================== 事件 ====================

    /// <summary>点了卡片（非拖动）；页面里接住做共享元素动画并跳详情页。</summary>
    public event EventHandler<MediaSectionItemEventArgs>? ItemInvoked;

    /// <summary>点了「查看全部」。</summary>
    public event EventHandler? ViewAllRequested;

    private void OnItemsChanged()
    {
        // 新数据可能更长或更短，布局稳定后重算边界
        DispatcherQueue.TryEnqueue(UpdateArrows);
    }

    private void ViewAll_Click(object sender, RoutedEventArgs e) =>
        ViewAllRequested?.Invoke(this, EventArgs.Empty);

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ItemCard card)
        {
            return;
        }

        // 与首页海报行一致：点击的海报作为共享元素飞入详情页
        PosterCard? poster = null;
        if (sender is ListView list)
        {
            poster = Animations.FluidNavigation.FindDescendant<PosterCard>(list.ContainerFromItem(card));
        }

        ItemInvoked?.Invoke(this, new MediaSectionItemEventArgs(card, poster));
    }

    // ==================== 内部 ScrollViewer ====================

    private ScrollViewer? _scroller;

    /// <summary>
    /// ListView 内部的横向 ScrollViewer：箭头平滑滚动 / 边界判断都要在它身上做。
    /// 模板未应用前拿不到，所以按需查找并挂 ViewChanged。
    /// </summary>
    private void EnsureScroller()
    {
        if (_scroller is not null)
        {
            return;
        }

        _scroller = Animations.FluidNavigation.FindDescendant<ScrollViewer>(List);
        if (_scroller is null)
        {
            return;
        }

        _scroller.ViewChanged += (_, _) => UpdateArrows();
        _scroller.SizeChanged += (_, _) => UpdateArrows();
        UpdateArrows();
    }

    // ==================== 滚轮 / 触控板 ====================

    private void List_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var scroller = _scroller ??= Animations.FluidNavigation.FindDescendant<ScrollViewer>(List);
        if (scroller is null)
        {
            return;
        }

        var props = e.GetCurrentPoint(List).Properties;
        var max = scroller.ScrollableWidth;
        if (max <= 0.5)
        {
            return; // 没有横向可滚空间：不拦截，让页面正常纵向滚动
        }

        double target;
        if (props.IsHorizontalMouseWheel)
        {
            // 触控板水平两指滑动：正值向右
            target = scroller.HorizontalOffset + props.MouseWheelDelta;
        }
        else
        {
            // 垂直滚轮转横向：向上（正）往左、向下（负）往右，一次约一张海报
            target = scroller.HorizontalOffset - props.MouseWheelDelta * 1.5;
        }

        target = Math.Clamp(target, 0, max);

        // 已经在边缘上没有可滚余量：同样放行给页面纵向滚动
        if (Math.Abs(target - scroller.HorizontalOffset) < 0.5)
        {
            return;
        }

        e.Handled = true;
        SmoothScrollTo(target, shortStep: true);
    }

    // ==================== 箭头滚动 ====================

    private void PrevButton_Click(object sender, RoutedEventArgs e) =>
        SmoothScrollTo(Offset() - ScrollStep());

    private void NextButton_Click(object sender, RoutedEventArgs e) =>
        SmoothScrollTo(Offset() + ScrollStep());

    /// <summary>单次点击箭头移动的距离：约一个视口的 70%。</summary>
    private double ScrollStep() => (_scroller?.ViewportWidth ?? 0) * 0.7;

    private double Offset() => _scroller?.HorizontalOffset ?? 0;

    // ==================== 平滑滚动 ====================

    private DispatcherTimer? _smoothTimer;
    private readonly Stopwatch _smoothClock = new();
    private double _smoothFrom;
    private double _smoothTo;
    private int _smoothDurationMs = 360;

    /// <summary>
    /// 带缓动的平滑滚动。WinUI3 的 ChangeView 不提供动画，这里用 16ms 节拍的计时器
    /// 逐帧推进偏移（ease-out cubic），每帧都走真实偏移，虚拟化和箭头边界状态都能实时跟上。
    /// retarget 时从当前实际位置续走。
    /// </summary>
    private void SmoothScrollTo(double target, bool shortStep = false)
    {
        var scroller = _scroller ??= Animations.FluidNavigation.FindDescendant<ScrollViewer>(List);
        if (scroller is null)
        {
            return;
        }

        target = Math.Clamp(target, 0, scroller.ScrollableWidth);
        _smoothFrom = scroller.HorizontalOffset;
        _smoothTo = target;
        _smoothDurationMs = shortStep ? 180 : 360;
        _smoothClock.Restart();

        if (_smoothTimer is null)
        {
            _smoothTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _smoothTimer.Tick += OnSmoothScrollTick;
        }
        _smoothTimer.Start();
    }

    private void OnSmoothScrollTick(object? sender, object e)
    {
        var scroller = _scroller;
        if (scroller is null)
        {
            StopSmoothScroll(null, null!);
            return;
        }

        var progress = _smoothClock.ElapsedMilliseconds / (double)_smoothDurationMs;
        if (progress >= 1)
        {
            StopSmoothScroll(null, null!);
            scroller.ChangeView(_smoothTo, null, null, true);
            UpdateArrows();
            return;
        }

        var eased = 1 - Math.Pow(1 - progress, 3);
        scroller.ChangeView(_smoothFrom + (_smoothTo - _smoothFrom) * eased, null, null, true);
    }

    private void StopSmoothScroll(object? sender, RoutedEventArgs e) => _smoothTimer?.Stop();

    // ==================== 箭头显隐与边界 ====================

    private bool _arrowsHovered;

    /// <summary>鼠标进入区域显示按钮，离开隐藏；滚动位置不动。</summary>
    private void SetArrowsVisible(bool visible)
    {
        _arrowsHovered = visible;
        UpdateArrows();
    }

    /// <summary>
    /// 按当前位置刷新箭头：不在边界内的（悬停时）淡入，在边界上的保持隐藏并禁用。
    /// 滚动 / 尺寸变化 / 数据变化都会走到这里，实现「滚动过程中实时更新」。
    /// </summary>
    private void UpdateArrows()
    {
        var scroller = _scroller ??= Animations.FluidNavigation.FindDescendant<ScrollViewer>(List);
        if (scroller is null)
        {
            return;
        }

        var canPrev = scroller.HorizontalOffset > 1;
        var canNext = scroller.HorizontalOffset < scroller.ScrollableWidth - 1;

        ShowArrow(PrevButton, _arrowsHovered && canPrev);
        ShowArrow(NextButton, _arrowsHovered && canNext);
    }

    private static void ShowArrow(Button button, bool show)
    {
        // 到边界的箭头直接禁用（不可点）；可见性由悬停 + 是否可滚共同决定
        button.IsEnabled = show;
        button.IsHitTestVisible = show;
        AnimateOpacity(button, show ? 1.0 : 0.0);
    }

    private static void AnimateOpacity(UIElement element, double to)
    {
        // 已经在目标值就不再起动画，避免每次 ViewChanged 都闪一下
        if (element.Opacity == to)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
