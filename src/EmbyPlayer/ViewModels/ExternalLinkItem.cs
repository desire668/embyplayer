using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyPlayer.Services;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EmbyPlayer.ViewModels;

/// <summary>
/// 设置页「外部服务」里的一张服务卡片：显示连接状态、收凭据、负责保存与验证。
/// 一个服务一张卡，用户填自己的 Key 就是「连接自己的账号 / 添加自己的 API」。
/// </summary>
public sealed partial class ExternalLinkItem : ObservableObject
{
    private readonly SettingsService _settings;

    public ExternalLinkItem(
        ExternalServiceId id,
        string name,
        string purpose,
        string credentialLabel,
        string placeholder,
        string siteUrl,
        string siteLabel,
        SettingsService settings,
        string? fallbackCredential = null)
    {
        Id = id;
        Name = name;
        Purpose = purpose;
        CredentialLabel = credentialLabel;
        Placeholder = placeholder;
        SiteUri = new Uri(siteUrl);
        SiteLabel = siteLabel;
        FallbackCredential = fallbackCredential;
        _settings = settings;

        _credential = ReadCredential() ?? "";
        Refresh();
    }

    public ExternalServiceId Id { get; }

    public string Name { get; }

    /// <summary>这个服务在本应用里负责什么，写在卡片副标题。</summary>
    public string Purpose { get; }

    public string CredentialLabel { get; }

    public string Placeholder { get; }

    public Uri SiteUri { get; }

    /// <summary>「去 TMDB 申请」这类跳转按钮的文案。</summary>
    public string SiteLabel { get; }

    /// <summary>用户没填凭据时可供使用的内置凭据（目前只有 OMDb 有）。</summary>
    public string? FallbackCredential { get; }

    /// <summary>用户填的 Key / Client ID，双向绑定到输入框。</summary>
    [ObservableProperty]
    private string _credential = "";

    [ObservableProperty]
    private ExternalLinkState _state = ExternalLinkState.NotConfigured;

    /// <summary>状态徽章文字，如「未连接」「已连接」。</summary>
    [ObservableProperty]
    private string _stateText = "未连接";

    /// <summary>状态徽章颜色，直接给 XAML 用，免得再写转换器。</summary>
    [ObservableProperty]
    private SolidColorBrush _stateBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

    /// <summary>徽章图标（Segoe Fluent）：勾 / 叉 / 时钟 / 链接。</summary>
    [ObservableProperty]
    private string _stateGlyph = "\uE71B";

    /// <summary>验证结果说明，显示在卡片底部。</summary>
    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isBusy;

    public bool HasCredential => !string.IsNullOrWhiteSpace(Credential);

    /// <summary>凭据改动后立刻落盘，状态回到「已保存待验证」——不假装它一定可用。</summary>
    partial void OnCredentialChanged(string value)
    {
        WriteCredential(value);
        _settings.Save();
        Refresh();
    }

    /// <summary>拿当前凭据真的打一次接口，把结果写在卡片上。</summary>
    [RelayCommand]
    private async Task VerifyAsync()
    {
        var credential = string.IsNullOrWhiteSpace(Credential) ? FallbackCredential : Credential;
        if (string.IsNullOrWhiteSpace(credential))
        {
            StatusText = "请先填写" + CredentialLabel + "，再点验证。";
            SetState(ExternalLinkState.NotConfigured, "未连接");
            return;
        }

        IsBusy = true;
        SetState(ExternalLinkState.Checking, "验证中");
        StatusText = "正在连接…";

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var (ok, message) = await ExternalLinkService.VerifyAsync(Id, credential, _settings.Current, cts.Token);
            StatusText = message;
            SetState(ok ? ExternalLinkState.Connected : ExternalLinkState.Failed,
                ok ? "已连接" : "连接失败");
        }
        catch (Exception ex)
        {
            StatusText = "验证时出错：" + ex.Message;
            SetState(ExternalLinkState.Failed, "连接失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>断开：清掉本机保存的凭据，对应功能随即停用。</summary>
    [RelayCommand]
    private void Disconnect()
    {
        Credential = "";
        WriteCredential(null);
        _settings.Save();
        Refresh();
        StatusText = "已断开，凭据只从本机删除；账号本身不受影响。";
    }

    [RelayCommand]
    private async Task OpenSiteAsync() => await Launcher.LaunchUriAsync(SiteUri);

    /// <summary>从设置读回凭据并重算状态（进入设置页、断开后都会调）。</summary>
    public void Refresh()
    {
        var saved = ReadCredential();
        if (string.IsNullOrWhiteSpace(saved))
        {
            // OMDb 有内置公共 Key：即使不填也能用，只是受公共额度限制
            if (!string.IsNullOrEmpty(FallbackCredential))
            {
                SetState(ExternalLinkState.Saved, "内置 Key");
                return;
            }
            SetState(ExternalLinkState.NotConfigured, "未连接");
            return;
        }

        SetState(ExternalLinkState.Saved, "已保存");
    }

    private void SetState(ExternalLinkState state, string text)
    {
        State = state;
        StateText = text;
        StateGlyph = state switch
        {
            ExternalLinkState.Connected => "\uE73E",   // CheckMark
            ExternalLinkState.Failed => "\uE711",      // Cancel
            ExternalLinkState.Checking => "\uE823",    // Clock
            ExternalLinkState.Saved => "\uE8D1",       // 已保存
            _ => "\uE71B"                              // Link
        };
        StateBrush = new SolidColorBrush(state switch
        {
            ExternalLinkState.Connected => Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xA8, 0x4B),
            ExternalLinkState.Failed => Windows.UI.Color.FromArgb(0xFF, 0xE0, 0x52, 0x4B),
            ExternalLinkState.Checking or ExternalLinkState.Saved =>
                Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x6F, 0xEB),
            _ => Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)
        });
    }

    private string? ReadCredential() => Id switch
    {
        ExternalServiceId.Tmdb => _settings.Current.TmdbApiKey,
        ExternalServiceId.Omdb => _settings.Current.OmdbApiKey,
        _ => null
    };

    private void WriteCredential(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        switch (Id)
        {
            case ExternalServiceId.Tmdb:
                _settings.Current.TmdbApiKey = trimmed;
                break;
            case ExternalServiceId.Omdb:
                _settings.Current.OmdbApiKey = trimmed;
                break;
        }
    }
}
