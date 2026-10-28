# EmbyPlayer

WinUI3 桌面客户端，用于浏览与播放 Emby 媒体服务器的内容，内置 mpv 播放器（中文右键菜单）。


![图片描述](./window.png)  
![图片描述](./Detail.png)

## 功能特性

- 服务器 / 内容源管理页：侧栏「服务器」入口统一管理 Emby 服务器与在线服务（TMDB），可改别名、切换、删除
- 海报墙首页：TMDB「本周热门电影」大图轮播（中文标题 / 评分 / 类型 / 片长）与「受欢迎的影片」海报行，播放前在媒体库中按片名匹配
- 媒体库浏览、详情页（分辨率 / 码率 / 大小、TMDB / IMDb / Trakt 多平台评分、分类点击跳转、剧集缩略图大卡）
- 聚合搜索：跨所有已登录 Emby 服务器同时检索，结果按服务器分组，剧集自动折叠
- 搜索历史：搜索框下方下拉历史卡片，支持点词条搜索、单条删除与一键清空
- 内置 mpv 播放器（已汉化右键菜单 / OSC / 字体 / 配置），13 项常用快捷键可在设置页自行录制
- 外部服务联通性校验：TMDB 支持 v3 API Key 与 v4 Token，OMDb 支持粘贴完整 URL，实时显示连接状态
- 缓存管理：元数据 / 图片 / 弹幕 / 播放四类缓存分别显示占用、单独清理，可设上限与有效期
- 云母侧栏 + 自绘标题栏（最小化 / 最大化 / 关闭）+ 面包屑导航
- 内置自动更新：检测到新版本后可一键下载安装包并静默覆盖安装
- 崩溃日志记录到 `%TEMP%\EmbyPlayer.crash.log`

## 安装

前往 [Releases](https://github.com/desire668/embyplayer/releases) 下载对应版本：

| 文件名 | 说明 |
| --- | --- |
| `EmbyPlayer-Setup-x64-SC-x.y.z.exe` | **自带 .NET 9 运行时**，开箱即用（约 150 MB） |
| `EmbyPlayer-Setup-x64-FD-x.y.z.exe` | **精简版**，需自行安装 [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)（约 50 MB） |

仅支持 Windows 10 1809 (17763) 及以上 x64 系统。

### 自动更新

启动时静默检查 GitHub 最新 Release，设置页「关于 → 检查更新」也可手动触发。发现新版本时：

1. 弹窗展示版本号与 Release notes，按钮为「立即更新」；
2. 按当前安装形态自动挑选 SC / FD 安装包（依据程序目录是否自带 `coreclr.dll` 判断），下载到 `%TEMP%\EmbyPlayer\Updates`，弹窗内显示进度；
3. 下载完成后以 `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS` 启动安装包，沿用上次的安装目录与任务做覆盖安装；
4. 安装结束由安装包重新启动应用（安装日志：`%TEMP%\EmbyPlayer\Updates\install.log`）。

安装在 Program Files 等受保护目录时会自动请求管理员授权（UAC）；Release 中没有匹配安装包时，「立即更新」退化为「前往下载」打开 Release 页面。

## 从源码构建

### CI 自动构建（推荐）

推送 `v*` 形式的 tag（如 `git tag v1.0.0 && git push origin v1.0.0`）即可触发 GitHub Actions：  
自动安装 .NET 9 SDK + Inno Setup 6、构建 SC / FD 两个安装包，并发布对应版本的 Release。

也可在 GitHub 仓库 **Actions** 页手动触发 `Build and Release` workflow（仅构建 artifact，默认不发 Release）。

### 依赖

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)（仅编译需要，运行时可由安装包附带）
- [Inno Setup 6](https://jrsoftware.org/isdl.php)（仅构建安装包需要）
- Windows 10 17763+ SDK（由 csproj `TargetFramework` 自动拉取）

### 一键构建安装包

```powershell
# 自动 publish SC + FD 两个变体并生成 setup.exe
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Variant All
```

可选参数：`-Variant SC` 或 `-Variant FD` 只构建其中一个变体；`-NoClean` 复用上次 publish 输出。

### 仅构建应用（调试）

```powershell
dotnet build src\EmbyPlayer\EmbyPlayer.csproj -p:Platform=x64 -p:Configuration=Debug
```

## 项目结构

```
EmbyPlayer.sln
├─ src\EmbyPlayer\
│  ├─ EmbyPlayer.csproj        WinUI3 unpackaged 项目
│  ├─ App.xaml(.cs)            应用入口 + 异常捕获
│  ├─ MainWindow.xaml(.cs)     主窗口 + 云母侧栏 + 自绘标题栏
│  ├─ Views\                   HomePage / LibraryPage / SearchPage / DetailPage / ServersPage 等
│  ├─ ViewModels\              与 Views 一一对应
│  ├─ Services\                EmbyApiClient / TmdbClient / CacheService / MpvShortcuts 等
│  ├─ Models\                  API 数据模型
│  ├─ mpv\portable_config\     mpv 中文菜单 / 配置（拷到输出目录）
│  └─ Assets\                  应用图标
├─ tools\mpv\                  开发期使用的 mpv 二进制
├─ installer\EmbyPlayer.iss    Inno Setup 脚本（SC / FD 单脚本两产物）
├─ scripts\build-installer.ps1 发布编排
└─ .github\
   ├─ workflows\build-release.yml   CI 构建（tag push / 手动触发）
   └─ release-notes\v1.0.0.md       版本对应的 Release 说明
```

## License

私有项目，保留所有权利。