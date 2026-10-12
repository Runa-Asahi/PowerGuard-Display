<#
.SYNOPSIS
  构建、测试并发布 PowerGuard Display（win-x64 自包含单文件）。
.EXAMPLE
  ./publish.ps1                 # 默认输出到 .\publish\win-x64
  ./publish.ps1 -SkipTests -Output D:\Tools\PowerGuardDisplay
#>
param(
    [string]$Configuration = "Release",
    [string]$Output = (Join-Path $PSScriptRoot "publish\win-x64"),
    [switch]$SkipTests
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

dotnet publish src/PowerGuard.App/PowerGuard.App.csproj `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "发布失败（退出码 $LASTEXITCODE）" }

$exe = Join-Path $Output "PowerGuardDisplay.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "发布完成：$exe（$size MB）" -ForegroundColor Green
Write-Host "建议把整个目录复制到固定位置（例如 %LOCALAPPDATA%\Programs\PowerGuardDisplay）后再开启登录自启动，"
Write-Host "因为自启动任务会指向 exe 的实际路径。"
