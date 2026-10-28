using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyPlayer.Services;
using EmbyPlayer.Views;
using Microsoft.UI.Xaml;

namespace EmbyPlayer.ViewModels;

/// <summary>内容源按「协议」分的类：在线服务（HTTP API）与 Emby 媒体服务器。</summary>
public enum ContentSourceKind
{
    /// <summary>在线服务：TMDB 等，不需要 Emby 服务器。</summary>
    Online,

    /// <summary>Emby 协议的媒体服务器。</summary>
    Emby
}

/// <summary>内容源列表里的一行：一个 Emby 服务器，或一个在线服务（TMDB）。</summary>
public sealed class ServerListItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "\uE968";
    public ContentSourceKind Kind { get; init; } = ContentSourceKind.Emby;

    /// <summary>右侧的小标签：Emby / 在线，一眼看出这条是什么协议。</summary>
    public string ProtocolLabel => Kind == ContentSourceKind.Emby ? "Emby" : "在线";

    public bool IsCurrent { get; init; }

    /// <summary>当前正在使用的内容源：右侧显示「当前」标记。</summary>
    public Visibility CurrentBadgeVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>非当前项：右侧显示「切换」提示。</summary>
    public Visibility SwitchHintVisibility => IsCurrent ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>只有 Emby 服务器能改别名 / 删除；在线服务的凭据在「设置 → 外部服务」里维护。</summary>
    public Visibility ManageVisibility =>
        Kind == ContentSourceKind.Emby ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>一组内容源：组标题 + 组说明 + 组内条目。</summary>
public sealed class ServerGroup
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public List<ServerListItem> Items { get; init; } = new();

    /// <summary>组内没有任何条目时显示的提示（例如还没添加服务器）。</summary>
    public string EmptyHint { get; init; } = "";
    public Visibility EmptyHintVisibility =>
        Items.Count == 0 && !string.IsNullOrEmpty(EmptyHint) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ItemsVisibility => Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 服务器 / 内容源页：按协议分成「在线服务」（TMDB 在最上，方便以后接其它 API）
/// 与「Emby 媒体服务器」两组，点任意一行即切换首页内容源并回首页刷新。
/// </summary>
public partial class ServersViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly EmbyApiClient _api;
    private readonly NavigationService _navigation;

    /// <summary>
    /// 内置在线服务清单：以后接新 API（豆瓣、Trakt 榜单等）在这里加一行即可，
    /// 它们统一排在 Emby 服务器上面。
    /// </summary>
    private static readonly (string Id, string Name, string Detail, string Glyph)[] OnlineSources =
    {
        (SettingsService.TmdbSourceId, "TMDB 在线推荐",
            "用 TMDB 接口取热门影视，不需要 Emby 服务器；Key 在「设置 → 外部服务」里填", "\uE774")
    };

    public ObservableCollection<ServerGroup> Groups { get; } = new();

    public ServersViewModel(SettingsService settings, EmbyApiClient api, NavigationService navigation)
    {
        _settings = settings;
        _api = api;
        _navigation = navigation;
        Refresh();
    }

    [ObservableProperty]
    private string _summary = "";

    /// <summary>没有任何服务器可退出时，隐藏「退出当前账号」。</summary>
    public Visibility LogoutVisibility => _settings.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh()
    {
        Groups.Clear();

        // ① 在线服务：TMDB 排第一行
        var online = OnlineSources
            .Select(s => new ServerListItem
            {
                Id = s.Id,
                Name = s.Name,
                Detail = s.Detail,
                Glyph = s.Glyph,
                Kind = ContentSourceKind.Online,
                IsCurrent = _settings.IsTmdbSource &&
                            string.Equals(s.Id, SettingsService.TmdbSourceId, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();

        Groups.Add(new ServerGroup
        {
            Title = "在线服务",
            Subtitle = "脱离 Emby 服务器也能看的海报墙",
            Items = online
        });

        // ② Emby 协议的媒体服务器
        var servers = _settings.Current.Servers
            .Select(server => new ServerListItem
            {
                Id = server.Id,
                Name = string.IsNullOrWhiteSpace(server.ServerName)
                    ? (string.IsNullOrWhiteSpace(server.UserName) ? "Emby 服务器" : $"{server.UserName} 的服务器")
                    : server.ServerName!,
                Detail = string.IsNullOrWhiteSpace(server.UserName)
                    ? server.Url
                    : $"{server.UserName} · {server.Url}",
                Glyph = "\uE968",
                Kind = ContentSourceKind.Emby,
                IsCurrent = !_settings.IsTmdbSource && server.Id == _settings.CurrentServer?.Id
            })
            .ToList();

        Groups.Add(new ServerGroup
        {
            Title = "Emby 媒体服务器",
            Subtitle = "Emby 协议，局域网直连；播放走自己的服务器",
            Items = servers,
            EmptyHint = "还没有添加 Emby 服务器。点右上角「添加服务器」连接。"
        });

        Summary = _settings.Current.Servers.Count == 0
            ? "当前使用在线推荐。添加 Emby 服务器后，首页就能显示自己媒体库的内容。"
            : $"已添加 {_settings.Current.Servers.Count} 个 Emby 服务器，点任意一行即可切换首页内容源。";
        OnPropertyChanged(nameof(LogoutVisibility));
    }

    /// <summary>切换内容源：在线服务只切源；Emby 服务器同时切换 API 凭据，然后回首页。</summary>
    public void Switch(ServerListItem item)
    {
        if (item.IsCurrent)
        {
            return;
        }

        if (item.Kind == ContentSourceKind.Online)
        {
            // 目前只有 TMDB；以后接别的在线 API，在这里按 item.Id 分支并各自带一份 Key
            if (string.Equals(item.Id, SettingsService.TmdbSourceId, StringComparison.OrdinalIgnoreCase))
            {
                _settings.SetSourceMode(true);
            }
        }
        else if (_settings.Current.Servers.FirstOrDefault(s => s.Id == item.Id) is { } server)
        {
            _settings.SetSourceMode(false);
            _settings.SwitchServer(server.Id);
            _api.Configure(server.Url, server.AccessToken);
        }

        // 换源后回首页：首页发现内容源变了会自己重新拉一次首屏
        _navigation.ShowShellNavigation(true);
        _navigation.NavigateToRoot<HomePage>();
    }

    /// <summary>添加服务器：与登录页一致，全屏打开、收起悬浮侧栏。</summary>
    public void AddServer()
    {
        _navigation.ShowShellNavigation(false);
        _navigation.NavigateTo<LoginPage>("add");
    }

    /// <summary>改别名（纯显示名，不动地址与令牌）。</summary>
    public void Rename(string serverId, string? name)
    {
        _settings.RenameServer(serverId, name);
        Refresh();
    }

    /// <summary>删除服务器：连同它的登录凭据一起从本机删掉，并把 API 切到剩下的第一台。</summary>
    public void Remove(string serverId)
    {
        _settings.RemoveServer(serverId);

        var current = _settings.CurrentServer;
        if (current is not null && !string.IsNullOrEmpty(current.AccessToken))
        {
            _api.Configure(current.Url, current.AccessToken);
        }

        Refresh();
    }

    /// <summary>退出当前账号：删掉当前这条服务器记录，没有别的服务器时回到登录页。</summary>
    public void Logout()
    {
        _settings.LogoutCurrent();
        var current = _settings.CurrentServer;
        if (current is not null && !string.IsNullOrEmpty(current.AccessToken))
        {
            _api.Configure(current.Url, current.AccessToken);
            Refresh();
            return;
        }

        Refresh();
        _navigation.ShowShellNavigation(false);
        _navigation.NavigateTo<LoginPage>();
    }
}
