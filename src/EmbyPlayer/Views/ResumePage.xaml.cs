using EmbyPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace EmbyPlayer.Views;

public sealed partial class ResumePage : Page
{
    public ResumeViewModel ViewModel { get; }

    public ResumePage()
    {
        ViewModel = App.Services.GetRequiredService<ResumeViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    private void OnItemChosen(ItemCard card) => ViewModel.OpenItem(card);
}
