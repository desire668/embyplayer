using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Animations;
using EmbyPlayer.Services;
using EmbyPlayer.Views;
using Windows.System;

namespace EmbyPlayer.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly NavigationService _navigation;
    private readonly EmbyApiClient _api;
    private readonly UpdateService _update;

    [ObservableProperty]
    private bool _autoPlayNext;

    [ObservableProperty]
    private string _mpvPath = "";

    [ObservableProperty]
    private string _mpvStatus = "";

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private bool _reduceMotion;

    // 弹幕设置
    [ObservableProperty]
    private bool _danmakuEnabled;

    [ObservableProperty]
    private int _danmakuFontSize;

    [ObservableProperty]
    private double _danmakuOpacity;

    [ObservableProperty]
    private int _danmakuScrollDuration;

    [ObservableProperty]
    private double _danmakuDisplayArea;

    [ObservableProperty]
    private string _danmakuServerUrl = "";

    /// <summary>TMDB v3 API Key：内容源切到 TMDB 时用它拉在线推荐，保存在本机，不进源码。</summary>
    [ObservableProperty]
    private string _tmdbApiKey = "";

    private bool _initialized;

    public SettingsViewModel(SettingsService settings, NavigationService navigation, EmbyApiClient api, UpdateService update)
    {
        _settings = settings;
        _navigation = navigation;
        _api = api;
        _update = update;
        _autoPlayNext = settings.Current.AutoPlayNext;
        _mpvPath = settings.Current.MpvPath ?? "";
        _danmakuEnabled = settings.Current.DanmakuEnabled;
        _danmakuFontSize = settings.Current.DanmakuFontSize;
        _danmakuOpacity = settings.Current.DanmakuOpacity;
        _danmakuScrollDuration = settings.Current.DanmakuScrollDuration;
        _danmakuDisplayArea = settings.Current.DanmakuDisplayArea;
        _danmakuServerUrl = settings.Current.DanmakuServerUrl ?? "";
        _tmdbApiKey = settings.Current.TmdbApiKey ?? "";
        _reduceMotion = settings.Current.ReduceMotion;
        FluidMotion.ReduceMotion = _reduceMotion;
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

    /// <summary>当前应用版本号（如 "v1.0.1"），与 csproj AssemblyVersion 自动同步。</summary>
    public string AppVersion => $"v{UpdateService.CurrentVersionString}";

    /// <summary>GitHub 仓库主页 URL（用于设置页「关于」展示）。</summary>
    public string RepositoryUrl => UpdateService.RepositoryUrl;

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

    partial void OnReduceMotionChanged(bool value)
    {
        FluidMotion.ReduceMotion = value;
        if (!_initialized)
        {
            return;
        }
        _settings.Current.ReduceMotion = value;
        _settings.Save();
    }

    partial void OnDanmakuEnabledChanged(bool value) => SaveDanmaku();
    partial void OnDanmakuFontSizeChanged(int value) => SaveDanmaku();
    partial void OnDanmakuOpacityChanged(double value) => SaveDanmaku();
    partial void OnDanmakuScrollDurationChanged(int value) => SaveDanmaku();
    partial void OnDanmakuDisplayAreaChanged(double value) => SaveDanmaku();
    partial void OnDanmakuServerUrlChanged(string value) => SaveDanmaku();

    partial void OnTmdbApiKeyChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.TmdbApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _settings.Save();
    }

    private void SaveDanmaku()
    {
        if (!_initialized)
        {
            return;
        }
        _settings.Current.DanmakuEnabled = DanmakuEnabled;
        _settings.Current.DanmakuFontSize = DanmakuFontSize;
        _settings.Current.DanmakuOpacity = DanmakuOpacity;
        _settings.Current.DanmakuScrollDuration = DanmakuScrollDuration;
        _settings.Current.DanmakuDisplayArea = DanmakuDisplayArea;
        _settings.Current.DanmakuServerUrl = string.IsNullOrWhiteSpace(DanmakuServerUrl) ? null : DanmakuServerUrl;
        _settings.Save();
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

    /// <summary>打开 GitHub 仓库主页。</summary>
    [RelayCommand]
    private async Task OpenGitHubAsync()
    {
        await Launcher.LaunchUriAsync(new Uri(UpdateService.RepositoryUrl));
    }

    /// <summary>手动检查更新；查询 GitHub Release 后通过 MainWindow 弹出更新对话框。</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdate))]
    private async Task CheckUpdateAsync()
    {
        if (_navigation.Shell is null)
        {
            return;
        }
        IsCheckingUpdate = true;
        CheckUpdateCommand.NotifyCanExecuteChanged();
        try
        {
            var update = await _update.CheckForUpdateAsync();
            // 弹窗必须在 UI 线程；async/await 在 WinUI 默认回到 UI 线程
            await _navigation.Shell.ShowUpdateDialogAsync(update, isAutoCheck: false);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog($"手动检查更新失败：{ex}");
        }
        finally
        {
            IsCheckingUpdate = false;
            CheckUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCheckUpdate() => !IsCheckingUpdate;
}
