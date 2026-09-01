using Microsoft.UI.Xaml.Controls;

namespace EmbyPlayer.Services;

public sealed class NavigationService
{
    public Frame? Frame { get; set; }
    public MainWindow? Shell { get; set; }

    public void NavigateTo<TPage>(object? parameter = null) where TPage : Page =>
        Frame?.Navigate(typeof(TPage), parameter);

    public void GoBack()
    {
        if (Frame?.CanGoBack == true)
        {
            Frame.GoBack();
        }
    }

    public void ShowShellNavigation(bool show) => Shell?.ShowShellNavigation(show);
}
