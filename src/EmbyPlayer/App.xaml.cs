using EmbyPlayer.Services;
using EmbyPlayer.ViewModels;
using EmbyPlayer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace EmbyPlayer;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // 捕获非 XAML 线程的致命异常与未观察任务异常，便于排查闪退
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog($"AppDomain 未处理异常 (IsTerminating={e.IsTerminating})：{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog($"未观察任务异常：{e.Exception}");
            e.SetObserved();
        };
    }

    internal static void WriteCrashLog(string content)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "EmbyPlayer.crash.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n{content}\n\n");
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<EmbyApiClient>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<MpvController>();
        services.AddSingleton<PlaybackSessionManager>();
        services.AddSingleton<PlaybackService>();
        services.AddSingleton<UpdateService>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<DetailViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<ResumeViewModel>();
        services.AddTransient<FavoritesViewModel>();
        Services = services.BuildServiceProvider();

        _window = new MainWindow();
        _window.Activate();

        var settings = Services.GetRequiredService<SettingsService>();
        var navigation = Services.GetRequiredService<NavigationService>();
        var api = Services.GetRequiredService<EmbyApiClient>();

        api.Unauthorized += () => _window?.DispatcherQueue.TryEnqueue(() =>
        {
            settings.LogoutCurrent();
            var current = settings.CurrentServer;
            if (current is not null && !string.IsNullOrEmpty(current.AccessToken))
            {
                api.Configure(current.Url, current.AccessToken);
                navigation.ShowShellNavigation(true);
                navigation.NavigateTo<LibraryPage>();
            }
            else
            {
                navigation.ShowShellNavigation(false);
                navigation.NavigateTo<LoginPage>();
            }
        });

        if (settings.IsLoggedIn)
        {
            navigation.ShowShellNavigation(true);
            navigation.NavigateTo<LibraryPage>();
        }
        else
        {
            navigation.ShowShellNavigation(false);
            navigation.NavigateTo<LoginPage>();
        }

        // 启动时静默检查 GitHub Release 是否有新版本，发现新版本时弹窗让用户选择前往下载或暂不更新
        // fire-and-forget：不阻塞启动流程；任何失败写入崩溃日志但不打扰用户
        _ = CheckForUpdateOnLaunchAsync();
    }

    /// <summary>启动后异步检查更新；发现新版本时切回 UI 线程弹窗。</summary>
    private async Task CheckForUpdateOnLaunchAsync()
    {
        try
        {
            // 等待窗口内容渲染完成，避免 ContentDialog 抢占 XamlRoot 出现竞态
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            var updateService = Services.GetRequiredService<UpdateService>();
            var update = await updateService.CheckForUpdateAsync();
            if (update is null)
            {
                return;
            }

            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                if (_window is MainWindow mw)
                {
                    _ = mw.ShowUpdateDialogAsync(update, isAutoCheck: true);
                }
            });
        }
        catch (Exception ex)
        {
            WriteCrashLog($"启动时检查更新失败：{ex}");
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteCrashLog($"XAML 未处理异常：{e.Exception}");
        System.Diagnostics.Debug.WriteLine($"未处理异常：{e.Exception}");
    }

    private Window? _window;
}
