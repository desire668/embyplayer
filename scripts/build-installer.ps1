<#
.SYNOPSIS
  构建 EmbyPlayer 安装包（Self-Contained + Framework-Dependent 两变体）。

.DESCRIPTION
  1. publish WinUI3 项目两次：
       SC：Self-Contained=true   —— 自带 .NET 9 运行时（约 150MB）
       FD：Self-Contained=false  —— 用户自行安装 .NET 9 Desktop Runtime（约 50MB）
     两者均设 WindowsAppSDKSelfContained=true（Windows App SDK DLLs 跟随 app）。
  2. 用 Inno Setup (ISCC.exe) 对每个变体生成 setup.exe。

.PARAMETER Variant
  SC | FD | All（默认 All）

.PARAMETER NoClean
  跳过清理上次 publish 输出。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Variant All
  powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Variant SC
  powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Variant FD
#>
[CmdletBinding()]
param(
    [ValidateSet('SC','FD','All')]
    [string]$Variant = 'All',
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'

# 解析仓库根目录（scripts 的父目录）
# 多 fallback 兼容 -File 调用、& 调用、pwsh 7+、WinPS 5.1
$scriptDir = $PSScriptRoot
if (-not $scriptDir -and $PSCommandPath) { $scriptDir = Split-Path $PSCommandPath -Parent }
if (-not $scriptDir -and $MyInvocation.MyCommand.Path) { $scriptDir = Split-Path $MyInvocation.MyCommand.Path -Parent }
if (-not $scriptDir) { throw "无法解析脚本目录，请用 powershell -File 显式调用本脚本" }
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
$csproj   = Join-Path $repoRoot 'src\EmbyPlayer\EmbyPlayer.csproj'
$iss      = Join-Path $repoRoot 'installer\EmbyPlayer.iss'
$distDir  = Join-Path $repoRoot 'dist'

Write-Host "Repo root : $repoRoot"
Write-Host "Project   : $csproj"
Write-Host "Installer : $iss"
Write-Host "Variant   : $Variant"
Write-Host ""

# 定位 dotnet（优先用户级，系统级 dotnet 只有 runtime 没有 SDK；CI 走 PATH）
$dotnet = $null
$candidates = @(
    "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
    "$env:ProgramFiles\dotnet\dotnet.exe",
    "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
)
foreach ($c in $candidates) {
    if (Test-Path $c) { $dotnet = $c; break }
}
if (-not $dotnet) {
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}
if (-not $dotnet) { throw "dotnet.exe 未找到，请先安装 .NET 9 SDK" }
Write-Host ("dotnet    : {0}" -f $dotnet)

# 定位 ISCC（兼容本地用户级安装、CI 的 Program Files 安装、以及 PATH 中的 iscc）
$iscc = $null
$candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
foreach ($c in $candidates) {
    if ($c -and (Test-Path $c)) { $iscc = $c; break }
}
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw "ISCC.exe 未找到，请先安装 Inno Setup 6" }
Write-Host ("ISCC      : {0}" -f $iscc)
Write-Host ""

# 创建 dist 输出目录
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

function Publish-Variant {
    param(
        [string]$Name,         # 'SC' or 'FD'
        [bool]$SelfContained
    )
    $publishDir = Join-Path $distDir "publish-$($Name.ToLower())"
    Write-Host "=== Publish $Name -> $publishDir ===" -ForegroundColor Cyan
    if (-not $NoClean -and (Test-Path $publishDir)) {
        Remove-Item $publishDir -Recurse -Force
    }
    $scFlag = if ($SelfContained) { 'true' } else { 'false' }
    & $dotnet publish $csproj `
        -c Release `
        -p:Platform=x64 `
        -r win-x64 `
        "-p:SelfContained=$scFlag" `
        -p:WindowsAppSDKSelfContained=true `
        -p:PublishSingleFile=false `
        --nologo `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 (变体 $Name, exit $LASTEXITCODE)" }

    $exe = Join-Path $publishDir 'EmbyPlayer.exe'
    if (Test-Path $exe) {
        $info = Get-Item $exe
        Write-Host ("  EmbyPlayer.exe: {0:N0} bytes ({1:yyyy-MM-dd HH:mm:ss})" -f $info.Length, $info.LastWriteTime) -ForegroundColor Green
    } else {
        throw "publish 输出中未找到 EmbyPlayer.exe: $publishDir"
    }

    # dotnet publish 已知坑：WinUI3 unpackaged 应用的 <AssemblyName>.pri（XAML 资源索引，
    # 内含 .xbf 编译产物）不会随 publish 自动拷贝到输出目录，导致运行时
    # Microsoft.UI.Xaml.Markup.XamlParseException: XAML parsing failed。
    # 此处从 build 输出目录手动补回。
    $priInPublish = Join-Path $publishDir 'EmbyPlayer.pri'
    if (-not (Test-Path $priInPublish)) {
        $priInBuild = Get-ChildItem -Path (Join-Path $repoRoot 'src\EmbyPlayer\bin\x64\Release') `
            -Filter 'EmbyPlayer.pri' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($priInBuild) {
            Copy-Item $priInBuild.FullName $priInPublish -Force
            Write-Host ("  补回 EmbyPlayer.pri ({0:N1} KB) <- {1}" -f ($priInBuild.Length / 1KB), $priInBuild.FullName) -ForegroundColor Yellow
        } else {
            Write-Warning "未在 bin\x64\Release 下找到 EmbyPlayer.pri，运行时将 XAML 解析失败！"
        }
    }

    # 简要列出 publish 目录总大小
    $totalBytes = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("  publish 总大小: {0:N1} MB" -f ($totalBytes / 1MB))
    return $publishDir
}

function Build-Installer {
    param([string]$Name)  # 'SC' or 'FD'
    Write-Host "=== ISCC build for variant $Name ===" -ForegroundColor Cyan
    Push-Location $repoRoot
    try {
        # /D$Name 在脚本里通过 #ifdef SC / #ifdef FD 切换 PublishDir 等参数
        & $iscc "/D$Name" $iss
        if ($LASTEXITCODE -ne 0) { throw "ISCC 失败 (变体 $Name, exit $LASTEXITCODE)" }
    } finally {
        Pop-Location
    }
}

# Step 1: publish
if ($Variant -in 'SC','All') { Publish-Variant -Name 'SC' -SelfContained $true  | Out-Null }
if ($Variant -in 'FD','All') { Publish-Variant -Name 'FD' -SelfContained $false | Out-Null }

# Step 2: 编译安装包
if ($Variant -in 'SC','All') { Build-Installer -Name 'SC' }
if ($Variant -in 'FD','All') { Build-Installer -Name 'FD' }

# Step 3: 汇总输出
Write-Host ""
Write-Host "=== Build artifacts ===" -ForegroundColor Green
Get-ChildItem $distDir -Filter 'EmbyPlayer-Setup-*.exe' | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
