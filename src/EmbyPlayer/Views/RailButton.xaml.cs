using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyPlayer.Views;

/// <summary>
/// 悬浮侧栏上的一个圆形按钮：液态玻璃质感的圆底 + 图标；
/// 鼠标移上去图标轻微放大、右侧滑出文字说明；当前所在页面叠一层品牌蓝。
/// </summary>
public sealed partial class RailButton : UserControl
{
    public RailButton()
    {
        InitializeComponent();

        // 手型光标：UIElement.ProtectedCursor 是受保护成员，只能在自己派生出来的控件里设置
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

        // 事件挂在按钮那一块上：文字标签是伸出去的装饰，不该扩大点击范围
        Circle.PointerEntered += (_, _) => SetHover(true);
        Circle.PointerExited += (_, _) => SetHover(false);
        Circle.Tapped += (_, _) => Invoked?.Invoke(this, EventArgs.Empty);

        ApplyPillMode();
        ApplyActiveState();
    }

    /// <summary>点击（Tapped）时触发。</summary>
    public event EventHandler? Invoked;

    /// <summary>悬停时显示的文字。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RailButton), new PropertyMetadata(string.Empty));

    /// <summary>Segoe Fluent Icons 的字形码。</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(RailButton), new PropertyMetadata("\uE80F"));

    /// <summary>是否是当前所在页面（蓝色高亮）。</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(RailButton),
        new PropertyMetadata(false, (d, _) => ((RailButton)d).ApplyActiveState()));

    /// <summary>
    /// 是否嵌在胶囊外壳里（首页 + 媒体库共用一个胶囊）。
    /// 为 true 时隐去自己的材质底与高光，由外层胶囊提供，避免出现「胶囊里套两个圆」。
    /// </summary>
    public bool InPill
    {
        get => (bool)GetValue(InPillProperty);
        set => SetValue(InPillProperty, value);
    }

    public static readonly DependencyProperty InPillProperty = DependencyProperty.Register(
        nameof(InPill), typeof(bool), typeof(RailButton),
        new PropertyMetadata(false, (d, _) => ((RailButton)d).ApplyPillMode()));

    /// <summary>背景内容变了（换页 / 换大图）时刷新一次按钮材质。</summary>
    public void UpdateGlass()
    {
        // 云母材质由系统合成，不再需要手动采样背景
    }

    private void ApplyPillMode()
    {
        Material.Opacity = InPill ? 0 : 1;
        Gloss.Opacity = InPill ? 0 : 1;

        // 胶囊里的按钮与滑块同尺寸（38 宽 / 46 高的胶囊形），到外壳四边都是 4
        Circle.Width = InPill ? 38 : 46;

        // 胶囊里不显示右侧文字：它虽然透明但仍占位，会把按钮撑宽、顶出胶囊外边
        LabelHost.Visibility = InPill ? Visibility.Collapsed : Visibility.Visible;

        ApplyActiveState();
    }

    /// <summary>
    /// 选中：从 0.72 倍弹到原大小并淡入；取消：缩回去淡出。
    /// 嵌在胶囊里的两个按钮不画自己的高亮 —— 由胶囊里那个共用的滑块指示当前页面。
    /// </summary>
    private void ApplyActiveState()
    {
        var on = IsActive && !InPill;

        AnimateTo(ActiveTint, "Opacity", on ? 1.0 : 0.0, 180);
        AnimateTo(ActiveScale, "ScaleX", on ? 1.0 : 0.72, 240);
        AnimateTo(ActiveScale, "ScaleY", on ? 1.0 : 0.72, 240);
    }

    private void SetHover(bool on)
    {
        // 图标轻微放大 + 标签淡入并右移到位（胶囊内的按钮不弹标签，否则会盖住相邻按钮）
        AnimateTo(IconScale, "ScaleX", on ? 1.18 : 1.0, 150);
        AnimateTo(IconScale, "ScaleY", on ? 1.18 : 1.0, 150);
        AnimateTo(HoverTint, "Opacity", on ? 1.0 : 0.0, 130);
        AnimateTo(LabelHost, "Opacity", on && !InPill ? 1.0 : 0.0, 130);
        AnimateTo(LabelShift, "X", on ? 0.0 : -6.0, 150);
    }

    private static void AnimateTo(DependencyObject target, string property, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
