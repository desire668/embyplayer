using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyPlayer.Services;
using EmbyPlayer.Views;

namespace EmbyPlayer.ViewModels;

public partial class ResumeViewModel : ObservableObject
{
    private readonly EmbyApiClient _api;
    private readonly NavigationService _navigation;

    [ObservableProperty]
    private ObservableCollection<ItemCard> _items = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public ResumeViewModel(EmbyApiClient api, NavigationService navigation)
    {
        _api = api;
        _navigation = navigation;
    }

    public async Task InitializeAsync()
    {
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var result = await _api.GetResumeAsync();
            Items = new ObservableCollection<ItemCard>(result.Items.Select(i => ItemCardMapper.ToCard(_api, i)));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void OpenItem(ItemCard card) => _navigation.NavigateTo<DetailPage>(card.Id);
}
