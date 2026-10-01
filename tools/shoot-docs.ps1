<#
.SYNOPSIS
    用脱敏模式重新生成 docs\screenshots 下的界面截图（供发布/文档用）。

.DESCRIPTION
    - 先打开 DSH_TOOLBOX_REDACT=1，界面里显示的用户名与主目录会变成 <user> / %USERPROFILE% 之类
    - exe 会先复制到 C:\Users\Public\dsh-toolbox\ 再运行，这样"关于"页里的可执行文件路径本身也是中性的
    - 逐页截图，最后由若干张拼出 gui-theme-lang.png（浅/深 × 中/英）与 gui-fixed-layout-sheet.png
    - 只影响本次运行的子进程环境，不动用户的 settings.json 之外的任何东西（结束会复位为 auto/auto）

.EXAMPLE
    powershell -File tools\shoot-docs.ps1
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$NeutralDir = 'C:\Users\Public\dsh-toolbox'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'docs\screenshots'
if (-not $Exe) { $Exe = Join-Path $root 'dist\dsh-toolbox.exe' }

New-Item -ItemType Directory -Force -Path $outDir, $NeutralDir | Out-Null
$neutralExe = Join-Path $NeutralDir 'dsh-toolbox.exe'
Copy-Item $Exe $neutralExe -Force

# 脱敏开关：子进程继承
$env:DSH_TOOLBOX_REDACT = '1'

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
[StructLayout(LayoutKind.Sequential)] public struct RCS { public int L, T, R, B; }
public static class ShootWin {
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EP cb, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RCS r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  static IntPtr Find(uint pid, string prefix, bool exact) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) return true;
      var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
      string s = t.ToString();
      bool hit = exact ? s == prefix : s.StartsWith(prefix);
      if (hit) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static IntPtr FindMain(uint pid) { return Find(pid, "dsh-toolbox", false); }
  public static IntPtr LogWindow(uint pid) { return Find(pid, "活动日志", true); }
}
'@

function Set-Cfg($lang, $theme) {
    & $neutralExe config.set --lang $lang --theme $theme --json 2>$null | Out-Null
}

function Shoot([string]$name, [string]$lang, [string]$theme, [int]$page, [switch]$LogWindow, [int]$WaitSec = 4) {
    Set-Cfg $lang $theme
    $args2 = @('--gui', '--page', "$page")
    if ($LogWindow) { $args2 += '--log-window' }
    $pr = Start-Process -FilePath $neutralExe -ArgumentList $args2 -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 22; $i++) {
        Start-Sleep -Milliseconds 800
        $pr.Refresh(); if ($pr.HasExited) { break }
        $h = if ($LogWindow) { [ShootWin]::LogWindow([uint32]$pr.Id) } else { [ShootWin]::FindMain([uint32]$pr.Id) }
        if ($h -ne [IntPtr]::Zero) { break }
    }
    if ($h -eq [IntPtr]::Zero) {
        Write-Host "  [跳过] $name（窗口未出现）" -ForegroundColor Yellow
        if (-not $pr.HasExited) { Stop-Process -Id $pr.Id -Force -ErrorAction SilentlyContinue }
        return $null
    }
    [ShootWin]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Seconds $WaitSec
    $r = New-Object RCS; [ShootWin]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap(($r.R - $r.L), ($r.B - $r.T))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($bmp.Width, $bmp.Height)))
    $g.Dispose()
    if (-not $pr.HasExited) { Stop-Process -Id $pr.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 400
    Write-Host ("  {0,-22} {1}x{2}" -f $name, $bmp.Width, $bmp.Height) -ForegroundColor Green
    return , $bmp
}

function Save-Compose($items, $scale, $path) {
    $cells = @()
    foreach ($b in $items) { if ($b -ne $null) { $cells += , @{ img = $b; w = [int]($b.Width * $scale); h = [int]($b.Height * $scale) } } }
    if ($cells.Count -eq 0) { return }
    $W = [int](($cells | ForEach-Object { $_.w } | Measure-Object -Maximum).Maximum)
    $H = [int](($cells | ForEach-Object { $_.h + 6 } | Measure-Object -Sum).Sum) + 6
    # 注意：必须用 -ArgumentList，New-Object Bitmap($w,$h) 会把两个值当数组传，报 "Parameter is not valid"
    $sheet = New-Object System.Drawing.Bitmap -ArgumentList $W, $H
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(120, 120, 128))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $y = 6
    foreach ($c in $cells) { $g.DrawImage($c.img, [int](($W - $c.w) / 2), $y, $c.w, $c.h); $y += $c.h + 6 }
    $g.Dispose()
    $sheet.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host ("  拼图 {0}  {1}x{2}" -f (Split-Path $path -Leaf), $W, $H) -ForegroundColor Cyan
    $sheet.Dispose()
}

Write-Host "脱敏模式: DSH_TOOLBOX_REDACT=$env:DSH_TOOLBOX_REDACT   exe: $neutralExe" -ForegroundColor Cyan

$ov  = Shoot 'gui-overview'    'zh-CN' 'light' 0
$ins = Shoot 'gui-install'     'zh-CN' 'light' 1
$mt  = Shoot 'gui-maint'       'zh-CN' 'light' 2
$cp  = Shoot 'gui-compat'      'zh-CN' 'light' 3
$lg  = Shoot 'gui-logs'        'zh-CN' 'light' 4
$ab  = Shoot 'gui-about'       'zh-CN' 'light' 5
$abd = Shoot 'gui-about-en-dark' 'en-US' 'dark' 5
$ove = Shoot 'gui-en-overview'  'en-US' 'light' 0
$cpm = Shoot 'gui-compat-dark-zh' 'zh-CN' 'dark' 3
$inm = Shoot 'gui-install-dark-en' 'en-US' 'dark' 1
$abe = Shoot 'gui-about-light-en' 'en-US' 'light' 5
# 日志窗口：停在"环境体检"页，最新的日志行是体检明细（不含用户名/主目录）
$lw  = Shoot 'gui-log-window'  'zh-CN' 'light' 3 -LogWindow -WaitSec 7
$tm  = Shoot 'gui-terminal'    'zh-CN' 'light' 6

foreach ($pair in @(
        @{ b = $ov; f = 'gui-overview.png' }, @{ b = $ins; f = 'gui-install.png' },
        @{ b = $mt; f = 'gui-maint.png' }, @{ b = $cp; f = 'gui-compat.png' },
        @{ b = $lg; f = 'gui-logs.png' }, @{ b = $ab; f = 'gui-about.png' },
        @{ b = $abd; f = 'gui-about-en-dark.png' }, @{ b = $ove; f = 'gui-en-overview.png' },
        @{ b = $lw; f = 'gui-log-window.png' },
        @{ b = $cpm; f = 'gui-compat-dark-zh.png' }, @{ b = $inm; f = 'gui-install-dark-en.png' },
        @{ b = $abe; f = 'gui-about-light-en.png' }, @{ b = $tm; f = 'gui-terminal.png' })) {
    if ($pair.b -ne $null) { $pair.b.Save((Join-Path $outDir $pair.f), [System.Drawing.Imaging.ImageFormat]::Png) }
}

Save-Compose @($ov, $cpm) 0.62 (Join-Path $outDir 'gui-theme-lang.png')
Save-Compose @($ov, $ins, $lg, $lw) 0.5 (Join-Path $outDir 'gui-fixed-layout-sheet.png')
Save-Compose @($ov, $tm) 0.62 (Join-Path $outDir 'gui-web-terminal.png')
Save-Compose @($ove, $abe) 0.62 (Join-Path $outDir 'gui-en-final.png')

foreach ($b in @($ov, $ins, $mt, $cp, $lg, $ab, $abd, $ove, $cpm, $inm, $abe, $lw, $tm)) { if ($b -ne $null) { $b.Dispose() } }

Set-Cfg 'auto' 'auto'
Remove-Item Env:DSH_TOOLBOX_REDACT -ErrorAction SilentlyContinue
Get-Process -Name 'dsh-toolbox' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host "完成。设置已复位为 auto/auto。" -ForegroundColor Green
