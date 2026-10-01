<#
.SYNOPSIS
    下载 Roslyn 编译器（csc.exe）到 .tools\roslyn-<version>\tasks\net472\，供 build.ps1 使用。

.DESCRIPTION
    项目用 Roslyn（C# 9+ 语法：模式匹配、??=、内插字符串等），系统自带的
    .NET Framework csc（v4.0.30319）只支持 C# 5，所以必须准备 Roslyn。
    官方 NuGet 包 Microsoft.Net.Compilers.Toolset 里就带 csc.exe，本脚本直接取它，
    不需要装 SDK、不需要管理员、不改系统环境。

    已存在则跳过（-Force 重新下载）。CI 与本地克隆都用同一份逻辑。

.EXAMPLE
    powershell -File tools\fetch-roslyn.ps1
    powershell -File tools\fetch-roslyn.ps1 -Version 4.14.0 -Force
#>
[CmdletBinding()]
param(
    [string]$Version = '4.14.0',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root ".tools\roslyn-$Version"
$csc = Join-Path $dest 'tasks\net472\csc.exe'

if ((Test-Path $csc) -and -not $Force) {
    Write-Host "已存在，跳过：$csc" -ForegroundColor Green
    exit 0
}

$nupkg = Join-Path $env:TEMP "Microsoft.Net.Compilers.Toolset.$Version.nupkg"
$url = "https://www.nuget.org/api/v2/package/Microsoft.Net.Compilers.Toolset/$Version"
Write-Host "下载 $url" -ForegroundColor Cyan
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing

if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# nupkg 就是 zip；只解出 tasks\net472（csc.exe + 依赖）
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
try {
    $n = 0
    foreach ($e in $zip.Entries) {
        if ($e.FullName -notlike 'tasks/net472/*') { continue }
        $rel = $e.FullName.Substring('tasks/net472/'.Length)
        if (-not $rel) { continue }
        $out = Join-Path (Join-Path $dest 'tasks\net472') $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $out -Parent) | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $out, $true)
        $n++
    }
    Write-Host "解出 $n 个文件" -ForegroundColor Green
} finally { $zip.Dispose() }
Remove-Item $nupkg -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $csc)) { throw "解包后仍找不到 csc.exe：$csc" }
& $csc /version
Write-Host "Roslyn 就绪：$csc" -ForegroundColor Green
