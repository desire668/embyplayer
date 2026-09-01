# EmbyPlayer

WinUI3 桌面客户端，用于浏览与播放 Emby 媒体服务器的内容，内置 mpv 播放器（中文右键菜单）。

## 功能特性

- 多服务器管理（添加 / 切换 / 公共信息探测）
- 媒体库浏览、详情页（分辨率 / 码率 / 字幕轨道）
- 聚合搜索：跨所有已登录 Emby 服务器同时检索
- 内置 mpv 播放器（已汉化右键菜单、字体、配置）
- 头像 / 服务器名 / 标题栏图标显示
- 崩溃日志记录到 `%TEMP%\EmbyPlayer.crash.log`

## 安装

前往 [Releases](https://github.com/desire668/embyplayer/releases) 下载对应版本：

| 文件名 | 说明 |
|------|------|
| `EmbyPlayer-Setup-x64-SC-x.y.z.exe` | **自带 .NET 9 运行时**，开箱即用（约 150 MB） |
| `EmbyPlayer-Setup-x64-FD-x.y.z.exe` | **精简版**，需自行安装 [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)（约 50 MB） |

仅支持 Windows 10 1809 (17763) 及以上 x64 系统。

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
│  ├─ MainWindow.xaml(.cs)     主窗口 + 服务器下拉
│  ├─ Views\                   LibraryPage / SearchPage / DetailPage 等
│  ├─ ViewModels\              与 Views 一一对应
│  ├─ Services\                EmbyApiClient / MpvLocator / ItemCardMapper 等
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
