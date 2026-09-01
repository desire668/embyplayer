using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyPlayer.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel { get; }

    public LibraryPage()
    {
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    private void ItemsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemCard card)
        {
            ViewModel.OpenItem(card);
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
