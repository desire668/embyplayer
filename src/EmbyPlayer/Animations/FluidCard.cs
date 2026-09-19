using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace EmbyPlayer.Animations;

/// <summary>
/// 影片卡片的流体交互。
/// 对应 Apple 的四条硬指标：
/// · 按下即反馈 —— PointerPressed 立刻缩放，不等 PointerReleased；
/// · 连续运动   —— 缩放 / 位移 / 视差全部走弹簧，没有固定 duration；
/// · 携带动量   —— 抬起时把指针速度交给位移动画（速度移交）；
/// · 可打断     —— 复用同一个弹簧实例改 FinalValue，任何时刻都能从当前位置改向；
/// · 直接操纵   —— 海报内容随指针位置做视差偏移，1:1 跟手。
/// </summary>
public static class FluidCard
{
    public static readonly DependencyProperty IsFluidProperty =
        DependencyProperty.RegisterAttached("IsFluid", typeof(bool), typeof(FluidCard),
            new PropertyMetadata(false, OnIsFluidChanged));

    /// <summary>阴影宿主：必须放在卡片之下（XAML 中先声明）的空容器。</summary>
    public static readonly DependencyProperty ShadowHostProperty =
        DependencyProperty.RegisterAttached("ShadowHost", typeof(FrameworkElement), typeof(FluidCard),
            new PropertyMetadata(null));

    /// <summary>视差目标：随指针反向轻微位移的海报元素（通常是 Image）。</summary>
    public static readonly DependencyProperty ParallaxTargetProperty =
        DependencyProperty.RegisterAttached("ParallaxTarget", typeof(FrameworkElement), typeof(FluidCard),
            new PropertyMetadata(null));

    /// <summary>悬停时渐显的蒙层（播放提示 / 压暗层）。</summary>
    public static readonly DependencyProperty VeilProperty =
        DependencyProperty.RegisterAttached("Veil", typeof(FrameworkElement), typeof(FluidCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty HoverScaleProperty =
        DependencyProperty.RegisterAttached("HoverScale", typeof(double), typeof(FluidCard),
            new PropertyMetadata(1.04d));

    public static readonly DependencyProperty PressScaleProperty =
        DependencyProperty.RegisterAttached("PressScale", typeof(double), typeof(FluidCard),
            new PropertyMetadata(0.96d));

    /// <summary>悬停抬升高度（px，向上为负）。</summary>
    public static readonly DependencyProperty LiftProperty =
        DependencyProperty.RegisterAttached("Lift", typeof(double), typeof(FluidCard),
            new PropertyMetadata(8d));

    /// <summary>海报视差幅度（px）。</summary>
    public static readonly DependencyProperty ParallaxProperty =
        DependencyProperty.RegisterAttached("Parallax", typeof(double), typeof(FluidCard),
            new PropertyMetadata(10d));

    private static readonly DependencyProperty MotionProperty =
        DependencyProperty.RegisterAttached("Motion", typeof(CardMotion), typeof(FluidCard),
            new PropertyMetadata(null));

    public static bool GetIsFluid(DependencyObject obj) => (bool)obj.GetValue(IsFluidProperty);
    public static void SetIsFluid(DependencyObject obj, bool value) => obj.SetValue(IsFluidProperty, value);

    public static FrameworkElement? GetShadowHost(DependencyObject obj) =>
        (FrameworkElement?)obj.GetValue(ShadowHostProperty);
    public static void SetShadowHost(DependencyObject obj, FrameworkElement? value) =>
        obj.SetValue(ShadowHostProperty, value);

    public static FrameworkElement? GetParallaxTarget(DependencyObject obj) =>
        (FrameworkElement?)obj.GetValue(ParallaxTargetProperty);
    public static void SetParallaxTarget(DependencyObject obj, FrameworkElement? value) =>
        obj.SetValue(ParallaxTargetProperty, value);

    public static FrameworkElement? GetVeil(DependencyObject obj) =>
        (FrameworkElement?)obj.GetValue(VeilProperty);
    public static void SetVeil(DependencyObject obj, FrameworkElement? value) =>
        obj.SetValue(VeilProperty, value);

    public static double GetHoverScale(DependencyObject obj) => (double)obj.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject obj, double value) => obj.SetValue(HoverScaleProperty, value);

    public static double GetPressScale(DependencyObject obj) => (double)obj.GetValue(PressScaleProperty);
    public static void SetPressScale(DependencyObject obj, double value) => obj.SetValue(PressScaleProperty, value);

    public static double GetLift(DependencyObject obj) => (double)obj.GetValue(LiftProperty);
    public static void SetLift(DependencyObject obj, double value) => obj.SetValue(LiftProperty, value);

    public static double GetParallax(DependencyObject obj) => (double)obj.GetValue(ParallaxProperty);
    public static void SetParallax(DependencyObject obj, double value) => obj.SetValue(ParallaxProperty, value);

    private static void OnIsFluidChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var motion = (CardMotion?)element.GetValue(MotionProperty);
        if (true.Equals(e.NewValue))
        {
            motion ??= new CardMotion(element);
            element.SetValue(MotionProperty, motion);
        }
        else
        {
            motion?.Detach();
            element.ClearValue(MotionProperty);
        }
    }

    private sealed class CardMotion
    {
        private readonly FrameworkElement _card;
        private FluidShadow? _shadow;
        private bool _hovering;
        private bool _pressed;
        private bool _ready;

        private long _lastTicks;
        private Vector2 _lastPosition;
        private Vector2 _velocity;

        public CardMotion(FrameworkElement card)
        {
            _card = card;
            _card.Loaded += OnLoaded;
            _card.SizeChanged += (_, _) => _card.CenterPivot();
            _card.PointerEntered += OnPointerEntered;
            _card.PointerMoved += OnPointerMoved;
            _card.PointerPressed += OnPointerPressed;
            _card.PointerReleased += OnPointerReleased;
            _card.PointerCaptureLost += OnPointerLost;
            _card.PointerCanceled += OnPointerLost;
            _card.PointerExited += OnPointerExited;
            _card.GotFocus += (_, _) => { _hovering = true; Apply(); };
            _card.LostFocus += (_, _) => { _hovering = false; _pressed = false; Apply(); };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _card.CenterPivot();
            if (GetParallaxTarget(_card) is { } parallax)
            {
                parallax.CenterPivot();
            }

            _ready = true;
            Reset();
        }

        public void Detach()
        {
            _ready = false;
            _shadow?.Detach();
            _shadow = null;
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _hovering = true;
            Apply();
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(_card).Position;
            TrackVelocity(new Vector2((float)point.X, (float)point.Y));
            if (!_ready)
            {
                return;
            }

            var width = _card.ActualWidth;
            var height = _card.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // 指针在卡片内的归一化位置 → 海报反向轻微位移，形成跟随指针的视差
            var nx = (float)(point.X / width - 0.5);
            var ny = (float)(point.Y / height - 0.5);
            var amount = (float)GetParallax(_card);

            if (GetParallaxTarget(_card) is { } target)
            {
                target.SpringTo("Translation",
                    new Vector3(-nx * amount, -ny * amount, 0f), FluidSpring.Follow);
            }
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 不抓取指针：GridView 依赖 PointerReleased 冒泡来触发 ItemClick，
            // 抓取可能打断命中链路；按下反馈本身已经即时，不需要抓取也能成立
            _pressed = true;
            Apply();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var wasPressed = _pressed;
            _pressed = false;

            // 速度移交：把手指离开瞬间的速度交给归位动画，动画接着手的势头走
            Apply(wasPressed ? new Vector3(_velocity.X, _velocity.Y, 0f) : null);
        }

        private void OnPointerLost(object sender, PointerRoutedEventArgs e)
        {
            _pressed = false;
            Apply();
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _hovering = false;
            _pressed = false;
            Apply();
        }

        private void Apply(Vector3? velocity = null)
        {
            if (!_ready)
            {
                return;
            }

            var scale = _pressed
                ? (float)GetPressScale(_card)
                : _hovering ? (float)GetHoverScale(_card) : 1f;
            var spring = _pressed ? FluidSpring.Press : _hovering ? FluidSpring.Hover : FluidSpring.Rest;

            _card.SpringTo("Scale", new Vector3(scale, scale, 1f), spring);
            _card.SpringTo("Translation",
                new Vector3(0f, _hovering ? -(float)GetLift(_card) : 0f, 0f),
                FluidSpring.Rest, velocity);

            if (GetParallaxTarget(_card) is { } target)
            {
                var inner = _hovering ? 1.08f : 1f;
                target.SpringTo("Scale", new Vector3(inner, inner, 1f), spring);
                if (!_hovering)
                {
                    target.SpringTo("Translation", Vector3.Zero, FluidSpring.Follow);
                }
            }

            if (GetVeil(_card) is { } veil)
            {
                veil.SpringTo("Opacity", _hovering ? 1f : 0f, FluidSpring.Hover);
            }

            if (_hovering)
            {
                _shadow ??= FluidShadow.Attach(GetShadowHost(_card));
                _shadow?.Elevate();
            }
            else
            {
                _shadow?.Settle();
            }
        }

        private void Reset()
        {
            _card.SetInstant("Scale", new Vector3(1f, 1f, 1f));
            _card.SetInstant("Translation", Vector3.Zero);
        }

        private void TrackVelocity(Vector2 position)
        {
            var now = Environment.TickCount64;
            var elapsed = now - _lastTicks;
            if (elapsed > 0 && _lastTicks != 0)
            {
                var seconds = elapsed / 1000d;
                if (seconds > 0)
                {
                    var raw = (position - _lastPosition) / (float)seconds;
                    // 一阶低通滤波，避免单帧抖动放大成甩飞
                    _velocity = Vector2.Lerp(_velocity, Clamp(raw, 2400f), 0.6f);
                }
            }

            _lastPosition = position;
            _lastTicks = now;
        }

        private static Vector2 Clamp(Vector2 value, float max)
        {
            var length = value.Length();
            return length <= max ? value : value / length * max;
        }
    }
}
