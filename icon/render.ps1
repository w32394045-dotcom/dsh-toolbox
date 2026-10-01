<#
.SYNOPSIS
    把 icon\*.svg 渲染成 icon\png\icon-<size>.png（Chrome headless，透明背景）。

.DESCRIPTION
    - ≤24px 使用 app-icon-small.svg（小尺寸简化版），其余使用 app-icon.svg
    - 透明背景：--default-background-color=00000000
    - 用独立的 --user-data-dir，不碰你自己的 Chrome 配置
    - 渲染完清理中间文件 icon\.render

.EXAMPLE
    powershell -File icon\render.ps1
#>
[CmdletBinding()]
param(
    [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 192, 256, 512),
    [string]$Chrome = ''
)

$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot
$pngDir = Join-Path $base 'png'
$work = Join-Path $base '.render'
$profile = Join-Path $work 'chrome-profile'

if (-not $Chrome) {
    foreach ($c in @(
            "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
            "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
            "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
            "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe")) {
        if (Test-Path $c) { $Chrome = $c; break }
    }
}
if (-not $Chrome -or -not (Test-Path $Chrome)) { throw '找不到 Chrome/Edge，无法渲染 SVG' }

New-Item -ItemType Directory -Force -Path $pngDir, $work | Out-Null
$master = [System.IO.File]::ReadAllText((Join-Path $base 'app-icon.svg'), [System.Text.Encoding]::UTF8)
$small = [System.IO.File]::ReadAllText((Join-Path $base 'app-icon-small.svg'), [System.Text.Encoding]::UTF8)

Write-Host "渲染器: $Chrome" -ForegroundColor Cyan
foreach ($s in $Sizes) {
    $svg = if ($s -le 24) { $small } else { $master }
    $svg = $svg -replace 'width="256" height="256"', ('width="{0}" height="{1}"' -f $s, $s)
    $html = '<!doctype html><html><head><meta charset="utf-8"><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}svg{display:block}</style></head><body>' + $svg + '</body></html>'
    $hp = Join-Path $work ("r$s.html")
    [System.IO.File]::WriteAllText($hp, $html, (New-Object System.Text.UTF8Encoding($false)))

    $out = Join-Path $pngDir ("icon-$s.png")
    if (Test-Path $out) { Remove-Item $out -Force }
    $common = @('--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
        '--default-background-color=00000000', "--user-data-dir=$profile",
        '--no-first-run', '--no-default-browser-check',
        "--screenshot=$out", "--window-size=$s,$s", ('file:///' + ($hp -replace '\\', '/')))
    foreach ($mode in @('--headless=new', '--headless')) {
        $p = Start-Process -FilePath $Chrome -ArgumentList (@($mode) + $common) -PassThru -WindowStyle Hidden
        $p.WaitForExit(60000) | Out-Null
        if (Test-Path $out) { break }
    }
    if (Test-Path $out) {
        Write-Host ("  {0,4}px  {1,8:N0} 字节" -f $s, (Get-Item $out).Length) -ForegroundColor Green
    } else {
        Write-Host ("  {0,4}px  失败" -f $s) -ForegroundColor Red
    }
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '完成。重新组装 ICO:  python icon\build_ico.py icon\app.ico' -ForegroundColor Cyan
