using System.Collections.ObjectModel;
using EmbyPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyPlayer.Views;

public sealed partial class MediaGridControl : UserControl
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(ObservableCollection<ItemCard>),
            typeof(MediaGridControl), new PropertyMetadata(null));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool),
            typeof(MediaGridControl), new PropertyMetadata(false));

    public static readonly DependencyProperty ErrorMessageProperty =
        DependencyProperty.Register(nameof(ErrorMessage), typeof(string),
            typeof(MediaGridControl), new PropertyMetadata(null));

    public ObservableCollection<ItemCard> Items
    {
        get => (ObservableCollection<ItemCard>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public string? ErrorMessage
    {
        get => (string?)GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    public event Action<ItemCard>? ItemChosen;

    public MediaGridControl()
    {
        InitializeComponent();
    }

    private void Grid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ItemCard card)
        {
            ItemChosen?.Invoke(card);
        }
    }
}
