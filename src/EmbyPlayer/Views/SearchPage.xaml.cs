using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyPlayer.Views;

public sealed partial class SearchPage : Page
{
    public SearchViewModel ViewModel { get; }

    /// <summary>当前搜索词（面包屑用它拼「搜索：xxx」）。</summary>
    public string? Query { get; private set; }

    private CancellationTokenSource? _debounce;

    public SearchPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();

        // 点历史词条时 ViewModel 把词回传给输入框，页面自己不重复执行搜索
        ViewModel.ApplySearchTerm += term => SearchBox.Text = term;
        // 历史条数变了（清空 / 删掉一条）要跟着显隐
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SearchViewModel.HasSearchHistory))
            {
                UpdateHistoryVisibility();
            }
        };
        UpdateHistoryVisibility();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 从搜索弹层过来时参数就是搜索词：直接显示结果，不用再点一次
        if (e.Parameter is string term && !string.IsNullOrWhiteSpace(term))
        {
            Query = term.Trim();
            QueryTitle.Text = Query;
            QueryHeader.Visibility = Visibility.Visible;
            SearchBox.Text = Query;
            _ = ViewModel.CommitSearchAsync(Query);
        }
        else
        {
            Query = null;
            QueryHeader.Visibility = Visibility.Collapsed;
            if (e.NavigationMode == NavigationMode.New)
            {
                SearchBox.Focus(FocusState.Programmatic);
            }
        }
        UpdateHistoryVisibility();
    }

    /// <summary>点进搜索框（里面还空着）就把历史记录展开。</summary>
    private void SearchBox_GotFocus(object sender, RoutedEventArgs e) => UpdateHistoryVisibility();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHistoryVisibility();
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        var term = SearchBox.Text;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                if (!token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() => _ = ViewModel.SearchAsync(term));
                }
            }
            catch (TaskCanceledException)
            {
            }
        });
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CommitSearch(SearchBox.Text);
        }
    }

    private void SearchGo_Click(object sender, RoutedEventArgs e) => CommitSearch(SearchBox.Text);

    /// <summary>回车 / 点放大镜才算一次「主动搜索」，只有这时才记进历史。</summary>
    private void CommitSearch(string? term)
    {
        _debounce?.Cancel();
        var query = term?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        Query = query;
        QueryTitle.Text = query;
        QueryHeader.Visibility = Visibility.Visible;
        UpdateHistoryVisibility();
        _ = ViewModel.CommitSearchAsync(query);
    }

    /// <summary>
    /// 输入框空着的时候把历史记录展开（像下拉栏一样），一旦有输入就收起，
    /// 让位给结果。这样点进搜索框就能看到历史词和「清空」。
    /// </summary>
    private void UpdateHistoryVisibility() =>
        HistoryCard.Visibility = ViewModel.HasSearchHistory && SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void HistoryItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Grid { Tag: string term })
        {
            ViewModel.UseHistoryItemCommand.Execute(term);
        }
    }

    private void RemoveHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string term })
        {
            ViewModel.RemoveHistoryItemCommand.Execute(term);
        }
    }

    /// <summary>叉号自己吃掉这次点击，别把它当成「点了这一条去搜索」。</summary>
    private void RemoveHistoryItem_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    /// <summary>点海报：先备好共享元素动画，再进详情页。</summary>
    private void GroupGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ItemCard card)
        {
            return;
        }

        if (sender is GridView grid)
        {
            var poster = Animations.FluidNavigation.FindDescendant<PosterCard>(grid.ContainerFromItem(card));
            Animations.FluidNavigation.PreparePoster(poster?.PosterElement);
        }

        ViewModel.OpenItem(card);
    }
}
