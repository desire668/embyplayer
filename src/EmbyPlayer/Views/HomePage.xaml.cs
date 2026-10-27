using EmbyPlayer.Animations;
using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyPlayer.Views;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; }

    /// <summary>轮播计时器的推进步长（秒）：既驱动切换，也驱动指示条进度。</summary>
    private const double TickSeconds = 0.1;

    private DispatcherTimer? _carousel;
    private double _heroElapsed;

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += (_, _) => _carousel?.Stop();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        StartCarousel();
    }

    /// <summary>
    /// 大图自动轮播：每 12 秒切下一屏，同时按 100ms 的节奏把进度写回指示条，
    /// 让当前那一格从左往右填满，直观显示还要多久切换。内容不足两屏时不启动。
    /// </summary>
    private void StartCarousel()
    {
        if (ViewModel.Heroes.Count < 2)
        {
            // 只有一屏：不必轮播，指示条直接显示为已填满
            ViewModel.UpdateHeroProgress(1);
            return;
        }

        if (_carousel is null)
        {
            _carousel = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
            _carousel.Tick += OnCarouselTick;
        }

        if (!_carousel.IsEnabled)
        {
            _heroElapsed = 0;
            _carousel.Start();
        }
    }

    private void OnCarouselTick(object? sender, object e)
    {
        _heroElapsed += TickSeconds;
        if (_heroElapsed >= HomeViewModel.HeroAutoPlaySeconds)
        {
            _heroElapsed = 0;
            ViewModel.NextHeroCommand.Execute(null);
            return;
        }

        ViewModel.UpdateHeroProgress(_heroElapsed / HomeViewModel.HeroAutoPlaySeconds);
    }

    /// <summary>点指示条：跳到对应的一屏。</summary>
    private void HeroDot_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HeroDot dot })
        {
            ViewModel.GoToHero(dot.Index);
            RestartCarousel();
        }
    }

    /// <summary>点大图左侧：上一屏。</summary>
    private void HeroPrev_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ViewModel.Heroes.Count < 2)
        {
            return;
        }
        ViewModel.PrevHeroCommand.Execute(null);
        RestartCarousel();
    }

    /// <summary>点大图右侧：下一屏。</summary>
    private void HeroNext_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ViewModel.Heroes.Count < 2)
        {
            return;
        }
        ViewModel.NextHeroCommand.Execute(null);
        RestartCarousel();
    }

    /// <summary>手动切换后重置自动轮播计时与进度，避免刚点完就立刻自动跳走。</summary>
    private void RestartCarousel()
    {
        if (_carousel is null || !_carousel.IsEnabled)
        {
            return;
        }
        _heroElapsed = 0;
        ViewModel.UpdateHeroProgress(0);
        _carousel.Stop();
        _carousel.Start();
    }

    private void ResumeList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemCard card)
        {
            ViewModel.OpenItemCommand.Execute(card);
        }
    }

    private void LatestList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ItemCard card)
        {
            return;
        }

        // 点击的海报作为共享元素飞入详情页（与媒体库页一致）
        if (sender is ListView list)
        {
            var poster = FluidNavigation.FindDescendant<PosterCard>(list.ContainerFromItem(card));
            FluidNavigation.PreparePoster(poster?.PosterElement);
        }

        ViewModel.OpenItemCommand.Execute(card);
    }
}
