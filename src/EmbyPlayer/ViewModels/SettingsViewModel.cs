using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Services;
using EmbyPlayer.Views;

namespace EmbyPlayer.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly NavigationService _navigation;
    private readonly EmbyApiClient _api;

    [ObservableProperty]
    private bool _autoPlayNext;

    [ObservableProperty]
    private string _mpvPath = "";

    [ObservableProperty]
    private string _mpvStatus = "";

    private bool _initialized;

    public SettingsViewModel(SettingsService settings, NavigationService navigation, EmbyApiClient api)
    {
        _settings = settings;
        _navigation = navigation;
        _api = api;
        _autoPlayNext = settings.Current.AutoPlayNext;
        _mpvPath = settings.Current.MpvPath ?? "";
        _initialized = true;
        RefreshMpvStatus();
    }

    public string ServerInfo
    {
        get
        {
            return string.IsNullOrEmpty(_settings.ServerUrl)
                ? "未登录"
                : $"{_settings.UserName} @ {_settings.ServerUrl}";
        }
    }

    partial void OnAutoPlayNextChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.AutoPlayNext = value;
        _settings.Save();
    }

    partial void OnMpvPathChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.MpvPath = string.IsNullOrWhiteSpace(value) ? null : value;
        _settings.Save();
        RefreshMpvStatus();
    }

    [RelayCommand]
    private void DetectMpv()
    {
        var found = MpvLocator.Find(_settings);
        if (found is null)
        {
            MpvStatus = "未找到 mpv，请手动输入完整路径。";
        }
        else
        {
            MpvPath = found;
            RefreshMpvStatus();
        }
    }

    private void RefreshMpvStatus()
    {
        var resolved = MpvLocator.Find(_settings);
        MpvStatus = resolved is null
            ? "当前不可用：未找到 mpv。"
            : $"当前使用：{resolved}";
    }

    [RelayCommand]
    private void Logout()
    {
        _settings.LogoutCurrent();
        var current = _settings.CurrentServer;
        if (current is not null && !string.IsNullOrEmpty(current.AccessToken))
        {
            _api.Configure(current.Url, current.AccessToken);
            _navigation.ShowShellNavigation(true);
            _navigation.NavigateTo<LibraryPage>();
        }
        else
        {
            _navigation.ShowShellNavigation(false);
            _navigation.NavigateTo<LoginPage>();
        }
    }
}
