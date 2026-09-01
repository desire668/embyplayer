using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Services;
using EmbyPlayer.Views;

namespace EmbyPlayer.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private readonly NavigationService _navigation;

    [ObservableProperty]
    private string _serverUrl = "http://";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public LoginViewModel(EmbyApiClient api, SettingsService settings, NavigationService navigation)
    {
        _api = api;
        _settings = settings;
        _navigation = navigation;
        if (!string.IsNullOrEmpty(settings.ServerUrl))
        {
            ServerUrl = settings.ServerUrl;
        }
    }

    public void ClearForAdd()
    {
        ServerUrl = "http://";
        Username = "";
        Password = "";
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(ServerUrl) || ServerUrl.Trim() == "http://")
        {
            ErrorMessage = "请输入服务器地址。";
            return;
        }
        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "请输入用户名。";
            return;
        }

        IsBusy = true;
        try
        {
            var url = EmbyApiClient.NormalizeUrl(ServerUrl);
            _api.Configure(url, null);
            var auth = await _api.AuthenticateByNameAsync(Username.Trim(), Password);
            var serverName = await EmbyApiClient.GetServerNameAsync(url, auth.AccessToken);
            _settings.AddOrUpdateServer(url, auth.AccessToken, auth.User?.Id ?? "", auth.User?.Name ?? Username, serverName);
            _api.Configure(url, auth.AccessToken);
            _navigation.ShowShellNavigation(true);
            _navigation.NavigateTo<LibraryPage>();
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "无法连接到服务器，请检查地址和网络。";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
