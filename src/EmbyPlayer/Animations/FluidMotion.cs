using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EmbyPlayer.Animations;

/// <summary>
/// Apple 弹簧参数：阻尼比 dampingRatio（越小越弹）+ 响应时间 response（秒，物体趋于静止的时间）。
/// WinUI 的弹簧用「周期 Period」而非响应时间，按阻尼振荡模型换算：
/// 静止时间 ≈ 0.637 × Period / ζ  →  Period ≈ 1.571 × ζ × response。
/// </summary>
public readonly record struct FluidSpring(float DampingRatio, TimeSpan Period)
{
    /// <summary>把 Apple 的「阻尼比 + 响应时间」翻译成 WinUI 的「阻尼比 + 周期」。</summary>
    public static FluidSpring FromResponse(float dampingRatio, double responseSeconds) =>
        new(dampingRatio, TimeSpan.FromSeconds(1.571f * dampingRatio * responseSeconds));

    /// <summary>指针进入 / 抬起：轻微软弹，0.30s 落位。</summary>
    public static FluidSpring Hover => FromResponse(0.85f, 0.30);

    /// <summary>按下：临界阻尼、无弹跳，0.18s 立即跟手（按下即反馈）。</summary>
    public static FluidSpring Press => FromResponse(1.00f, 0.18);

    /// <summary>回到静止：略微收敛，避免生硬。</summary>
    public static FluidSpring Rest => FromResponse(0.92f, 0.32);

    /// <summary>指针视差跟随：高阻尼快收敛，保证 1:1 跟手而不抖动。</summary>
    public static FluidSpring Follow => FromResponse(0.95f, 0.20);

    /// <summary>卡片入场：允许一次轻微过冲，营造「浮现」感。</summary>
    public static FluidSpring Entrance => FromResponse(0.78f, 0.45);

    /// <summary>手势释放后的抛掷 / 归位：带一点弹跳，携带动量。</summary>
    public static FluidSpring Gesture => FromResponse(0.75f, 0.40);

    /// <summary>阴影深度：比主体慢半拍，形成主次层次。</summary>
    public static FluidSpring Depth => FromResponse(0.90f, 0.38);
}

/// <summary>
/// 流体动效物理层。
/// 准则（Apple WWDC《Designing Fluid Interfaces》）：
/// 1) 即时响应——按下就有反馈，不等抬起；
/// 2) 连续运动——动画无固定时长，只有弹簧物理；
/// 3) 携带动量——手势释放时把速度交给动画；
/// 4) 可打断——新动画从屏幕当前位置接手，不跳变。
/// </summary>
public static class FluidMotion
{
    private static Compositor? _compositor;
    private static bool? _systemAnimationsEnabled;

    /// <summary>当前 XAML 线程的合成器（必须在 UI 线程调用）。</summary>
    public static Compositor Compositor =>
        _compositor ??= CompositionTarget.GetCompositorForCurrentThread();

    /// <summary>应用内「减弱动效」开关，设置页可接；与系统开关取「或」。</summary>
    public static bool ReduceMotion { get; set; }

    /// <summary>系统「动画效果」开关（设置 → 辅助功能 → 视觉效果）。</summary>
    public static bool SystemAnimationsEnabled =>
        _systemAnimationsEnabled ??= ReadSystemAnimationsEnabled();

    /// <summary>为 false 时所有动效退化为瞬时状态切换（不跳变、不位移）。</summary>
    public static bool AnimationsEnabled => SystemAnimationsEnabled && !ReduceMotion;

    private static bool ReadSystemAnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }
}

/// <summary>把弹簧动画挂到 UIElement / CompositionObject 上的扩展方法。</summary>
public static class FluidAnimation
{
    private static readonly ConditionalWeakTable<UIElement, SpringMap> Vector3Springs = new();
    private static readonly ConditionalWeakTable<UIElement, ScalarSpringMap> ScalarSprings = new();
    private static readonly ConditionalWeakTable<CompositionObject, ScalarSpringMap> ObjectScalarSprings = new();
    private static readonly ConditionalWeakTable<CompositionObject, SpringMap> ObjectVector3Springs = new();

    // 复用同一个弹簧实例是「可打断动画」的关键：改 FinalValue 后重新 StartAnimation，
    // 系统会从当前值与当前速度继续，而不是从旧目标值重新开始。
    private sealed class SpringMap : Dictionary<string, SpringVector3NaturalMotionAnimation>;
    private sealed class ScalarSpringMap : Dictionary<string, SpringScalarNaturalMotionAnimation>;

    /// <summary>Vector3 弹簧（Scale / Translation）。velocity 非空时做速度移交。</summary>
    public static void SpringTo(
        this UIElement element,
        string target,
        Vector3 value,
        FluidSpring spring,
        Vector3? velocity = null,
        double delaySeconds = 0)
    {
        if (!FluidMotion.AnimationsEnabled)
        {
            SetInstant(element, target, value);
            return;
        }

        var map = Vector3Springs.GetValue(element, static _ => new SpringMap());
        if (!map.TryGetValue(target, out var anim))
        {
            anim = FluidMotion.Compositor.CreateSpringVector3Animation();
            anim.Target = target;
            map[target] = anim;
        }

        anim.FinalValue = value;
        anim.DampingRatio = spring.DampingRatio;
        anim.Period = spring.Period;
        anim.DelayTime = TimeSpan.FromSeconds(delaySeconds);
        if (velocity is { } v)
        {
            anim.InitialVelocity = v;
        }

        element.StartAnimation(anim);
    }

    /// <summary>标量弹簧（Opacity 等）。</summary>
    public static void SpringTo(
        this UIElement element,
        string target,
        float value,
        FluidSpring spring,
        double delaySeconds = 0)
    {
        if (!FluidMotion.AnimationsEnabled)
        {
            SetInstant(element, target, value);
            return;
        }

        var map = ScalarSprings.GetValue(element, static _ => new ScalarSpringMap());
        if (!map.TryGetValue(target, out var anim))
        {
            anim = FluidMotion.Compositor.CreateSpringScalarAnimation();
            anim.Target = target;
            map[target] = anim;
        }

        anim.FinalValue = value;
        anim.DampingRatio = spring.DampingRatio;
        anim.Period = spring.Period;
        anim.DelayTime = TimeSpan.FromSeconds(delaySeconds);
        element.StartAnimation(anim);
    }

    /// <summary>合成对象上的标量弹簧（阴影 BlurRadius / Opacity）。</summary>
    public static void SpringTo(
        this CompositionObject target,
        string property,
        float value,
        FluidSpring spring,
        double delaySeconds = 0)
    {
        if (!FluidMotion.AnimationsEnabled)
        {
            SetInstant(target, property, value);
            return;
        }

        var map = ObjectScalarSprings.GetValue(target, static _ => new ScalarSpringMap());
        if (!map.TryGetValue(property, out var anim))
        {
            anim = FluidMotion.Compositor.CreateSpringScalarAnimation();
            anim.Target = property;
            map[property] = anim;
        }

        anim.FinalValue = value;
        anim.DampingRatio = spring.DampingRatio;
        anim.Period = spring.Period;
        anim.DelayTime = TimeSpan.FromSeconds(delaySeconds);
        target.StartAnimation(property, anim);
    }

    /// <summary>合成对象上的 Vector3 弹簧（阴影 Offset）。</summary>
    public static void SpringTo(
        this CompositionObject target,
        string property,
        Vector3 value,
        FluidSpring spring)
    {
        if (!FluidMotion.AnimationsEnabled)
        {
            SetInstantVector(target, property, value);
            return;
        }

        var map = ObjectVector3Springs.GetValue(target, static _ => new SpringMap());
        if (!map.TryGetValue(property, out var anim))
        {
            anim = FluidMotion.Compositor.CreateSpringVector3Animation();
            anim.Target = property;
            map[property] = anim;
        }

        anim.FinalValue = value;
        anim.DampingRatio = spring.DampingRatio;
        anim.Period = spring.Period;
        target.StartAnimation(property, anim);
    }

    /// <summary>不播放动画地落位（减弱动效 / 初始化起点）。</summary>
    public static void SetInstant(this UIElement element, string target, Vector3 value)
    {
        switch (target)
        {
            case "Scale":
                element.Scale = value;
                break;
            case "Translation":
                element.Translation = value;
                break;
        }
    }

    public static void SetInstant(this UIElement element, string target, float value)
    {
        if (target == "Opacity")
        {
            element.Opacity = value;
        }
    }

    private static void SetInstant(CompositionObject target, string property, float value)
    {
        if (target is DropShadow shadow)
        {
            switch (property)
            {
                case "BlurRadius":
                    shadow.BlurRadius = value;
                    break;
                case "Opacity":
                    shadow.Opacity = value;
                    break;
            }
        }
    }

    private static void SetInstantVector(CompositionObject target, string property, Vector3 value)
    {
        if (target is DropShadow shadow && property == "Offset")
        {
            shadow.Offset = value;
        }
    }

    /// <summary>把缩放中心固定在元素中心，避免从左上角放大。</summary>
    public static void CenterPivot(this FrameworkElement element)
    {
        var w = (float)element.ActualWidth;
        var h = (float)element.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        element.CenterPoint = new Vector3(w / 2f, h / 2f, 0f);
    }
}
