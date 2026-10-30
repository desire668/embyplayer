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
/// 交互一览（Netflix / Disney+ 桌面端风格）：
/// - 鼠标按住卡片或空白处左右拖动滚动；拖动不会误触发卡片点击；
/// - 垂直滚轮在本区域转成水平滚动；触控板水平两指滑动直接水平滚动；
///   两种输入都只在「还有内容可滚」时拦截，滚到头后事件冒泡给页面的纵向滚动；
/// - 鼠标进入区域时左右圆形按钮淡入，离开淡出（不影响滚动位置）；
/// - 点按钮平滑滚动约一个视口的 70%，滚动过程中按钮随边界实时启停。
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

        // 数据异步填充 / 尺寸变化都要重算箭头边界状态
        Loaded += (_, _) => UpdateArrows();
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

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // 拖动结束后的那次抬起不算点击
        if (_suppressNextTap)
        {
            _suppressNextTap = false;
            return;
        }

        if (Section is null && sender is FrameworkElement { DataContext: ItemCard card })
        {
            // 兜底：没绑 Section 时直接从卡片拿数据
            ItemInvoked?.Invoke(this, new MediaSectionItemEventArgs(card, null));
            return;
        }

        if (sender is FrameworkElement { DataContext: ItemCard item })
        {
            var poster = Animations.FluidNavigation.FindDescendant<PosterCard>(sender as DependencyObject);
            ItemInvoked?.Invoke(this, new MediaSectionItemEventArgs(item, poster));
        }
    }

    // ==================== 拖动滚动 ====================

    /// <summary>超过这个位移才判定为拖动（否则当作点卡片的预备动作）。</summary>
    private const double DragThreshold = 6.0;

    private bool _isDragging;
    private bool _suppressNextTap;
    private bool _pointerCaptured;
    private uint? _dragPointerId;
    private double _dragStartX;
    private double _dragStartOffset;

    private void Scroller_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 触摸留给系统惯性滚动；鼠标 / 触控笔走自绘拖动
        var point = e.GetCurrentPoint(Scroller);
        if (point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse &&
            point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen)
        {
            return;
        }

        _isDragging = false;
        _pointerCaptured = false;
        _dragPointerId = point.PointerId;
        _dragStartX = point.Position.X;
        _dragStartOffset = Scroller.HorizontalOffset;

        // 注意：这里刻意不抓指针。按下即抓会把卡片自己的 Tapped 吃掉，
        // 导致单击打不开详情；只有真的拖动起来（越过阈值）才抓。
        // 另外每次按下都把「吞掉下一次点击」的标记清掉，避免标记残留误伤后续点击。
        _suppressNextTap = false;
    }

    private void Scroller_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId != e.GetCurrentPoint(Scroller).PointerId)
        {
            return;
        }

        var x = e.GetCurrentPoint(Scroller).Position.X;
        var dx = x - _dragStartX;

        if (!_isDragging)
        {
            // 未越过阈值：当它是点卡片的预备动作，不动滚动
            if (Math.Abs(dx) < DragThreshold)
            {
                return;
            }

            _isDragging = true;
            StopSmoothScroll(null, null!); // 用户上手了，动画让位

            // 确认是拖动后再抓指针：之后移出列表也能继续收到 Moved / Released
            Scroller.CapturePointer(e.Pointer);
            _pointerCaptured = true;
        }

        // 拖多少滚多少：禁用动画，跟手
        var target = Math.Clamp(_dragStartOffset - dx, 0, Scroller.ScrollableWidth);
        Scroller.ChangeView(target, null, null, true);
    }

    private void Scroller_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerCaptured)
        {
            Scroller.ReleasePointerCapture(e.Pointer);
            _pointerCaptured = false;
        }

        if (_isDragging)
        {
            // 拖过就吞掉紧随其后的点击，避免拖完直接蹦进详情页
            _suppressNextTap = true;
            _isDragging = false;
        }

        _dragPointerId = null;
    }

    private void Scroller_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _pointerCaptured = false;
        _isDragging = false;
        _dragPointerId = null;
    }

    // ==================== 滚轮 / 触控板 ====================

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(Scroller).Properties;
        var max = Scroller.ScrollableWidth;
        if (max <= 0.5)
        {
            return; // 没有横向可滚空间：不拦截，让页面正常纵向滚动
        }

        double target;
        if (props.IsHorizontalMouseWheel)
        {
            // 触控板水平两指滑动：正值向右
            target = Scroller.HorizontalOffset + props.MouseWheelDelta;
        }
        else
        {
            // 垂直滚轮转横向：向上（正）往左、向下（负）往右，一次约一张海报
            target = Scroller.HorizontalOffset - props.MouseWheelDelta * 1.5;
        }

        target = Math.Clamp(target, 0, max);

        // 已经在边缘上没有可滚余量：同样放行给页面纵向滚动
        if (Math.Abs(target - Scroller.HorizontalOffset) < 0.5)
        {
            return;
        }

        e.Handled = true;
        SmoothScrollTo(target, shortStep: true);
    }

    // ==================== 箭头滚动 ====================

    private void PrevButton_Click(object sender, RoutedEventArgs e) =>
        SmoothScrollTo(Scroller.HorizontalOffset - ScrollStep());

    private void NextButton_Click(object sender, RoutedEventArgs e) =>
        SmoothScrollTo(Scroller.HorizontalOffset + ScrollStep());

    /// <summary>单次点击箭头移动的距离：约一个视口的 70%。</summary>
    private double ScrollStep() => Scroller.ViewportWidth * 0.7;

    // ==================== 平滑滚动 ====================

    private DispatcherTimer? _smoothTimer;
    private readonly Stopwatch _smoothClock = new();
    private double _smoothFrom;
    private double _smoothTo;
    private int _smoothDurationMs = 360;

    /// <summary>
    /// 带缓动的平滑滚动。WinUI3 的 ChangeView 不提供动画，这里用 16ms 节拍的计时器
    /// 逐帧推进偏移（ease-out cubic），每帧都走真实偏移，ItemsRepeater 的虚拟化
    /// 回收和箭头边界状态都能实时跟上。retarget 时从当前实际位置续走。
    /// </summary>
    private void SmoothScrollTo(double target, bool shortStep = false)
    {
        target = Math.Clamp(target, 0, Scroller.ScrollableWidth);
        _smoothFrom = Scroller.HorizontalOffset;
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
        var progress = _smoothClock.ElapsedMilliseconds / (double)_smoothDurationMs;
        if (progress >= 1)
        {
            StopSmoothScroll(null, null!);
            Scroller.ChangeView(_smoothTo, null, null, true);
            UpdateArrows();
            return;
        }

        var eased = 1 - Math.Pow(1 - progress, 3);
        Scroller.ChangeView(_smoothFrom + (_smoothTo - _smoothFrom) * eased, null, null, true);
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
        var canPrev = Scroller.HorizontalOffset > 1;
        var canNext = Scroller.HorizontalOffset < Scroller.ScrollableWidth - 1;

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

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        // 平滑滚动 / 拖动 / 惯性期间每帧都会触发，箭头状态随之实时更新
        UpdateArrows();

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateArrows();
}
