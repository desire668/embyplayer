using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyPlayer.Views;

public sealed partial class DetailPage : Page
{
    public DetailViewModel ViewModel { get; }

    public DetailPage()
    {
        ViewModel = App.Services.GetRequiredService<DetailViewModel>();
        InitializeComponent();
        ViewModel.RequestSubtitleChoice += ShowSubtitleChoiceAsync;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string itemId)
        {
            _ = ViewModel.LoadAsync(itemId);
        }
    }

    private async Task ShowSubtitleChoiceAsync()
    {
        SubtitleDialog.XamlRoot = XamlRoot;
        var result = await SubtitleDialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.ConfirmPlayAsync();
        }
    }

    private void Episodes_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.PlayEpisodeCommand.Execute(e.ClickedItem);

    private void BackButton_Click(object sender, RoutedEventArgs e) => ViewModel.GoBack();
}
