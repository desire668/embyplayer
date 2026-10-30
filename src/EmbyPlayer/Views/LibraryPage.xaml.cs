using EmbyPlayer.Animations;
using EmbyPlayer.Services;
using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyPlayer.Views;

public sealed partial class LibraryPage : Page, IRefreshablePage
{
    public LibraryViewModel ViewModel { get; }

    /// <summary>详情页点分类跳过来时带的过滤条件（OnNavigatedTo 早于 Loaded）。</summary>
    private LibraryFilter? _filter;

    /// <summary>首屏数据是否已拉过（在首页 / 媒体库之间来回切不再重复请求）。</summary>
    private bool _initialized;

    public LibraryPage()
    {
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        InitializeComponent();

        // 缓存页面实例：切走再回来沿用同一份数据与滚动位置，要更新点侧栏的刷新按钮
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>侧栏刷新按钮：强制重新拉一次媒体库内容。</summary>
    public async Task RefreshAsync() => await ViewModel.InitializeAsync(_filter);

    private async Task LoadAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.InitializeAsync(_filter);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 从详情页带新的分类条件跳过来：条件变了才重新拉，否则沿用已加载的数据
        var filter = e.Parameter as LibraryFilter;
        if (_initialized && !Equals(filter, _filter))
        {
            _filter = filter;
            _ = ViewModel.InitializeAsync(_filter);
        }
        else
        {
            _filter = filter;
        }
    }

    private void ItemsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ItemCard card)
        {
            return;
        }

        // 点击的海报作为共享元素飞入详情页：先把源元素登记，再导航
        if (sender is GridView grid)
        {
            var poster = FluidNavigation.FindDescendant<PosterCard>(grid.ContainerFromItem(card));
            FluidNavigation.PreparePoster(poster?.PosterElement);
        }

        ViewModel.OpenItem(card);
    }

    /// <summary>概览横向分类区里点了卡片：与网格视图一样做共享元素动画后进详情。</summary>
    private void Section_ItemInvoked(object sender, MediaSectionItemEventArgs e)
    {
        FluidNavigation.PreparePoster(e.Poster?.PosterElement);
        ViewModel.OpenItem(e.Card);
    }

    /// <summary>概览分类区的「查看全部」：媒体分类切到对应网格，「继续观看」去续播页。</summary>
    private void Section_ViewAllRequested(object sender, EventArgs e)
    {
        if (sender is HorizontalMediaSection { Section: { } section })
        {
            switch (section.ViewAll)
            {
                case MediaSectionViewAll.View when section.TargetView is { } view:
                    ViewModel.SelectedView = view;
                    break;
                case MediaSectionViewAll.Resume:
                    App.Services.GetRequiredService<NavigationService>().NavigateTo<ResumePage>();
                    break;
            }
        }
    }

    private void ShowAllViews_Click(object sender, RoutedEventArgs e)
    {
        // 打开前定位到当前选中项（滚动到可见位置）
        AllViewsList.ScrollIntoView(ViewModel.SelectedView);
    }

    private void AllViewsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ViewInfo view)
        {
            ViewModel.SelectedView = view;
            AllViewsFlyout.Hide();
        }
    }
}
