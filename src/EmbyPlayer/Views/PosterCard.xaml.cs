using EmbyPlayer.Animations;
using EmbyPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyPlayer.Views;

/// <summary>
/// 影片墙卡片。原来 LibraryPage / MediaGridControl 各写了一份卡片模板，
/// 现在统一到这一个控件，动效也只在这里接线。
/// </summary>
public sealed partial class PosterCard : UserControl
{
    public static readonly DependencyProperty CardProperty =
        DependencyProperty.Register(nameof(Card), typeof(ItemCard), typeof(PosterCard),
            new PropertyMetadata(null));

    public ItemCard? Card
    {
        get => (ItemCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    /// <summary>共享元素动画（列表 → 详情）用的海报元素。</summary>
    public FrameworkElement PosterElement => Poster;

    public PosterCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 阴影宿主与视差元素都在模板里，加载完成后再交给动效层
        FluidCard.SetShadowHost(CardRoot, ShadowHost);
        FluidCard.SetParallaxTarget(CardRoot, PosterImage);
        FluidCard.SetVeil(CardRoot, HoverVeil);
    }
}
