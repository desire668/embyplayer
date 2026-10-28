namespace EmbyPlayer.Views;

/// <summary>
/// 能被侧栏刷新按钮强制重新拉数据的页面。
/// 首页与媒体库会缓存页面实例（切走再回来不重复请求），要更新内容就点刷新。
/// </summary>
public interface IRefreshablePage
{
    Task RefreshAsync();
}
