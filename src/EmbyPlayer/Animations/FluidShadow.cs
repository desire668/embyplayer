using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace EmbyPlayer.Animations;

/// <summary>
/// 可动画的深度阴影：在宿主元素（置于海报下方的空 Grid）上挂一个 SpriteVisual + DropShadow。
/// 与 XAML 静态阴影不同，模糊半径 / 偏移 / 透明度全部走弹簧，
/// 卡片「抬起 — 落下」是一个连续的物理过程，而不是两张状态的切换。
/// </summary>
public sealed class FluidShadow
{
    private readonly SpriteVisual _visual;
    private readonly DropShadow _shadow;

    private FluidShadow(SpriteVisual visual, DropShadow shadow)
    {
        _visual = visual;
        _shadow = shadow;
    }

    /// <summary>在宿主元素上创建阴影；失败时返回 null（不影响其它动效）。</summary>
    public static FluidShadow? Attach(FrameworkElement? host)
    {
        if (host is null)
        {
            return null;
        }

        try
        {
            var compositor = FluidMotion.Compositor;
            var visual = compositor.CreateSpriteVisual();
            var shadow = compositor.CreateDropShadow();
            shadow.Color = Colors.Black;
            shadow.BlurRadius = RestBlur;
            shadow.Offset = new Vector3(0, RestOffsetY, 0);
            shadow.Opacity = RestOpacity;

            // 让阴影自动跟随宿主尺寸，省去 SizeChanged 同步
            visual.RelativeSizeAdjustment = Vector2.One;
            visual.Shadow = shadow;

            ElementCompositionPreview.SetElementChildVisual(host, visual);
            return new FluidShadow(visual, shadow);
        }
        catch
        {
            return null;
        }
    }

    // 静止 / 悬停两档深度：差值就是「抬起」的高度感
    public const float RestBlur = 12f;
    public const float RestOffsetY = 3f;
    public const float RestOpacity = 0.28f;

    public const float HoverBlur = 30f;
    public const float HoverOffsetY = 14f;
    public const float HoverOpacity = 0.46f;

    /// <summary>抬升到悬停深度。</summary>
    public void Elevate() => Apply(HoverBlur, HoverOffsetY, HoverOpacity);

    /// <summary>落回静止深度。</summary>
    public void Settle() => Apply(RestBlur, RestOffsetY, RestOpacity);

    private void Apply(float blur, float offsetY, float opacity)
    {
        _shadow.SpringTo("BlurRadius", blur, FluidSpring.Depth);
        _shadow.SpringTo("Offset", new Vector3(0, offsetY, 0), FluidSpring.Depth);
        _shadow.SpringTo("Opacity", opacity, FluidSpring.Depth);
    }

    /// <summary>容器回收 / 卸载时摘掉阴影，避免合成层泄漏。</summary>
    public void Detach()
    {
        _visual.Shadow = null;
        _visual.Dispose();
        _shadow.Dispose();
    }
}
