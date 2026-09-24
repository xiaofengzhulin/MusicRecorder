# MusicRecorder 构建 / 发布 / 打包脚本
#
# 用法：
#   pwsh -File build.ps1              # 构建（Release，框架依赖）
#   pwsh -File build.ps1 -Publish     # 发布单文件绿色版到 dist\
#   pwsh -File build.ps1 -Package     # 发布并打包成可分发 zip 到 release\
#
# 说明：发布为 win-x64 自包含单文件，目标机器无需安装 .NET 运行时。

param(
    [switch]$Publish,
    [switch]$Package,
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'MusicRecorder.csproj'

# 版本号取自 csproj 的 <Version>
$version = '1.0.0'
$m = Select-String -Path $project -Pattern '<Version>([^<]+)</Version>'
if ($m) { $version = $m.Matches[0].Groups[1].Value.Trim() }

function Get-DotNet {
    $candidates = @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw '未找到 dotnet，请先安装 .NET 8 SDK：https://aka.ms/dotnet/download'
}

$dotnet = Get-DotNet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Write-Host "dotnet : $dotnet"
Write-Host "版本号 : $version"
Write-Host ""

if (-not ($Publish -or $Package)) {
    & $dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }
    Write-Host "`n构建完成：$root\bin\$Configuration\net8.0-windows10.0.19041.0\MusicRecorder.exe"
    return
}

# ---------------------------------------------------------------- 发布单文件
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

& $dotnet publish $project -c $Configuration -r $RuntimeIdentifier --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $dist
if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }

$exe = Join-Path $dist 'MusicRecorder.exe'
Write-Host ("`n发布完成：{0}（{1:N1} MB）" -f $exe, ((Get-Item $exe).Length / 1MB))

if (-not $Package) { return }

# ---------------------------------------------------------------- 打包 zip
$stage = Join-Path $root 'release'
$pkgName = "MusicRecorder-v$version-$RuntimeIdentifier"
$pkgDir = Join-Path $stage $pkgName
$zip = Join-Path $stage "$pkgName.zip"

if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null

Copy-Item $exe (Join-Path $pkgDir 'MusicRecorder.exe')
foreach ($extra in '使用说明.txt', 'README.md', 'CHANGELOG.md') {
    $src = Join-Path $root $extra
    if (Test-Path $src) { Copy-Item $src (Join-Path $pkgDir $extra) }
}

Compress-Archive -Path (Join-Path $pkgDir '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host "`n打包完成："
Write-Host ("  目录: {0}" -f $pkgDir)
Write-Host ("  压缩包: {0}（{1:N1} MB）" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host ("  SHA256: {0}" -f (Get-FileHash $exe -Algorithm SHA256).Hash)
