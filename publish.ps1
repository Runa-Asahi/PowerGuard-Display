<#
.SYNOPSIS
  构建、测试并发布 PowerGuard Display（win-x64）。同时生成两种包：
    publish\win-x64      自包含单文件（约 70 MB，无需安装 .NET）
    publish\win-x64-fd   依赖框架单文件（几 MB，需要 .NET 10 Desktop Runtime x64）
.EXAMPLE
  ./publish.ps1                 # 默认输出到 .\publish
  ./publish.ps1 -SkipTests -Zip # CI：额外打包 zip
#>
param(
    [string]$Configuration = "Release",
    [string]$Output = (Join-Path $PSScriptRoot "publish"),
    [switch]$SkipTests,
    [switch]$Zip,
    # 版本号（不带 v）。为空时使用 csproj 中的 <Version>；CI 在打 tag 时传入 tag 对应的版本
    [string]$Version = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 dotnet。请安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0"
}
Write-Host "dotnet SDK: $(dotnet --version)"

if (-not $SkipTests) {
    dotnet test tests/PowerGuard.Core.Tests/PowerGuard.Core.Tests.csproj -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "单元测试失败（退出码 $LASTEXITCODE）" }
}

$proj = "src/PowerGuard.App/PowerGuard.App.csproj"
$csprojVersion = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$version = if ($Version) { $Version.TrimStart('v') } else { $csprojVersion }
if ($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw "版本号格式不正确：$version" }
$commit = (git rev-parse HEAD 2>$null)
$verArgs = @("-p:Version=$version")
if ($commit) { $verArgs += "-p:SourceRevisionId=$commit" }
Write-Host "发布版本：$version（csproj：$csprojVersion，提交：$commit）"
$sc = Join-Path $Output "win-x64"
$fd = Join-Path $Output "win-x64-fd"

dotnet publish $proj -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none @verArgs -o $sc
if ($LASTEXITCODE -ne 0) { throw "自包含发布失败（退出码 $LASTEXITCODE）" }

dotnet publish $proj -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none @verArgs -o $fd
if ($LASTEXITCODE -ne 0) { throw "依赖框架发布失败（退出码 $LASTEXITCODE）" }

foreach ($d in $sc, $fd) {
    $exe = Join-Path $d "PowerGuardDisplay.exe"
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    $pv = (Get-Item $exe).VersionInfo.ProductVersion
    if (-not $pv.StartsWith($version)) { throw "$exe 的 ProductVersion=$pv 与期望版本 $version 不一致" }
    Write-Host "发布完成：$exe（$size MB，ProductVersion $pv）" -ForegroundColor Green
}

if ($Zip) {
    $z1 = Join-Path $Output "PowerGuardDisplay-$version-win-x64-self-contained.zip"
    $z2 = Join-Path $Output "PowerGuardDisplay-$version-win-x64-framework-dependent.zip"
    Remove-Item $z1, $z2 -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $sc "*") -DestinationPath $z1
    Compress-Archive -Path (Join-Path $fd "*") -DestinationPath $z2
    Write-Host "已打包：$z1"
    Write-Host "已打包：$z2"
}

Write-Host ""
Write-Host "建议把 exe 复制到固定位置（例如 %LOCALAPPDATA%\Programs\PowerGuardDisplay）后再开启登录自启动，"
Write-Host "因为自启动任务记录的是 exe 的完整路径。"
