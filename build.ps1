#Requires -Version 5.1
<#
.SYNOPSIS
    构建 dsh-toolbox.exe（Roslyn csc + 系统自带 .NET Framework 4.8，无第三方依赖）。

.DESCRIPTION
    1) 定位 Roslyn csc（.tools\roslyn-*\tasks\net472\csc.exe），找不到则回退系统自带 csc；
    2) 递归收集 src\**\*.cs；
    3) 引用 .NET Framework 4.8 运行时程序集；
    4) 嵌入 src\app.manifest（longPathAware / UTF-8 / asInvoker）；
    5) 输出 dist\dsh-toolbox.exe。

.EXAMPLE
    pwsh -File build.ps1                 # 构建
    pwsh -File build.ps1 -Test           # 构建后跑冒烟测试
    pwsh -File build.ps1 -DebugBuild     # 带调试符号、不优化
#>
[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$DebugBuild,
    [switch]$WarnAsError,
    [string[]]$Only = @(),             # 只编译指定命令模块（文件名=类名），其余用临时桩；并行开发时各自验证用
    [string]$Out = ''      # 输出文件名或路径；多人并行开发时各用自己的名字，避免互相覆盖
)

# -File 传参不会把 "A,B" 拆成数组，这里统一归一化，-Only A,B 与 -Only A -Only B 都能用
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$srcDir  = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'
$outExe  = if ($Out) { if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $distDir $Out } } else { Join-Path $distDir 'dsh-toolbox.exe' }

# ---------------------------------------------------------------- 1) 编译器
$csc = Get-ChildItem -Path (Join-Path $root '.tools') -Recurse -Filter 'csc.exe' -ErrorAction SilentlyContinue |
       Where-Object { $_.FullName -match 'net472' } |
       Sort-Object FullName -Descending |
       Select-Object -First 1
$cscIsRoslyn = $true
if (-not $csc) {
    $csc = Get-Item 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' -ErrorAction SilentlyContinue
    $cscIsRoslyn = $false
}
if (-not $csc) { throw "找不到 C# 编译器（既没有 .tools\roslyn-*，也没有系统自带 csc.exe）" }
Write-Host ("编译器: {0}  (Roslyn: {1})" -f $csc.FullName, $cscIsRoslyn) -ForegroundColor Cyan

# ---------------------------------------------------------------- 2) 源码
if (-not (Test-Path $srcDir)) { throw "源码目录不存在: $srcDir" }
$allCmdFiles = @(Get-ChildItem -Path (Join-Path $srcDir 'Commands') -Filter '*.cs' -ErrorAction SilentlyContinue | Sort-Object Name)
$coreFiles   = @(Get-ChildItem -Path (Join-Path $srcDir 'Core') -Filter '*.cs' -ErrorAction SilentlyContinue | Sort-Object Name)
# 除 Commands 目录外（它由 -Only 逻辑单独处理），src 下所有 .cs 都要编进来（Core / Gui / Program …）
$cmdDir = Join-Path $srcDir 'Commands'
$sources = @(Get-ChildItem -Path $srcDir -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
                 Where-Object { $_.DirectoryName -ne $cmdDir } |
                 Sort-Object FullName | ForEach-Object { $_.FullName })
$stubDir = $null
if ($Only.Count -gt 0 -and ($Only -notcontains 'CoreCommands')) { $Only = @($Only) + @('CoreCommands') }   # 核心命令始终参与编译
if ($Only.Count -gt 0) {
    $stubDir = Join-Path $env:TEMP ('dsh-toolbox-stubs-' + $PID)
    if (Test-Path $stubDir) { Remove-Item $stubDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stubDir -Force | Out-Null
    $realCount = 0; $stubCount = 0
    foreach ($f in $allCmdFiles) {
        $cls = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
        if ($Only -contains $cls) { $sources += $f.FullName; $realCount++ }
        else {
            $code = 'using DshToolbox.Core;' + "`r`n" + 'namespace DshToolbox.Commands { public static class ' + $cls + ' { public static void Register() { } } }' + "`r`n"
            Set-Content -LiteralPath (Join-Path $stubDir ($cls + '.cs')) -Value $code -Encoding UTF8
            $stubCount++
        }
    }
    $sources += @(Get-ChildItem -Path $stubDir -Filter '*.cs' | ForEach-Object { $_.FullName })
    Write-Host ("部分构建：真实模块 {0} 个，桩 {1} 个" -f $realCount, $stubCount) -ForegroundColor Yellow
} else {
    $sources += @($allCmdFiles | ForEach-Object { $_.FullName })
}
if ($sources.Count -eq 0) { throw "src 下没有任何 .cs 文件" }
Write-Host ("源码: {0} 个 .cs 文件" -f $sources.Count) -ForegroundColor Cyan

# ---------------------------------------------------------------- 3) 引用程序集
$fwDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wantRefs = @(
    'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xml.Linq.dll',
    'System.Management.dll', 'System.ServiceProcess.dll',
    'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
    'System.Web.Extensions.dll', 'System.Net.Http.dll', 'System.Drawing.dll',
    'System.Windows.Forms.dll', 'System.Runtime.Serialization.dll'
)
$refs = @()
foreach ($r in $wantRefs) {
    $p = Join-Path $fwDir $r
    if (Test-Path $p) { $refs += ('/r:"' + $p + '"') }
    else { Write-Host ("  跳过缺失引用: {0}" -f $r) -ForegroundColor DarkGray }
}

# ---------------------------------------------------------------- 4) 编译
New-Item -ItemType Directory -Path (Split-Path $outExe -Parent) -Force | Out-Null
if (Test-Path $outExe) { Remove-Item $outExe -Force }

$manifest = Join-Path $srcDir 'app.manifest'

$icon = Join-Path $srcDir 'app.ico'
$argList = New-Object System.Collections.Generic.List[string]
$argList.Add('/nologo')
$argList.Add('/target:exe')
$argList.Add('/platform:anycpu')
$argList.Add('/langversion:latest')
$argList.Add('/codepage:65001')
$argList.Add('/nostdlib-')
$argList.Add('/nowarn:1701,1702,1591,0618,0612,0067')
if ($WarnAsError) { $argList.Add('/warnaserror+') }
if ($DebugBuild) { $argList.Add('/debug+'); $argList.Add('/optimize-') }
else             { $argList.Add('/debug-'); $argList.Add('/optimize+') }
$argList.Add('/out:"' + $outExe + '"')
if (Test-Path $manifest) { $argList.Add('/win32manifest:"' + $manifest + '"') }
if (Test-Path $icon) { $argList.Add('/win32icon:"' + $icon + '"') }
else { Write-Host "警告: 未找到图标 $icon，将不嵌入图标" -ForegroundColor Yellow }
$argList.AddRange([string[]]$refs)
$argList.AddRange([string[]]($sources | ForEach-Object { '"' + $_ + '"' }))

$rsp = Join-Path $env:TEMP ('dsh-toolbox-build-{0}.rsp' -f $PID)
Set-Content -LiteralPath $rsp -Value ($argList -join "`r`n") -Encoding UTF8

Write-Host "编译中..." -ForegroundColor Cyan
# 并行开发时多人同时构建/写文件会撞上"源文件被占用"(CS1504)：用全局互斥串行化 + 失败重试
$buildMutex = New-Object System.Threading.Mutex($false, 'Global\dsh-toolbox-build')
$null = $buildMutex.WaitOne(180000)
$code = 1
$output = @()
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $output = @(& $csc.FullName ('@' + $rsp) 2>&1)
        $code = $LASTEXITCODE
        $locked = @($output | Where-Object { $_ -match 'CS1504' }).Count -gt 0
        if (-not $locked) { break }
        Write-Host ("  源文件正被占用，2 秒后重试（{0}/3）" -f $attempt) -ForegroundColor Yellow
        Start-Sleep -Seconds 2
    }
} finally {
    try { $buildMutex.ReleaseMutex() } catch { }
    try { $buildMutex.Dispose() } catch { }
    $sw.Stop()
}
Remove-Item $rsp -Force -ErrorAction SilentlyContinue

$errors = @($output | Where-Object { $_ -match ':\s*error\s+CS\d+' })
$warns  = @($output | Where-Object { $_ -match ':\s*warning\s+CS\d+' })
foreach ($l in $output) { Write-Host $l }
if ($stubDir) { Remove-Item -LiteralPath $stubDir -Recurse -Force -ErrorAction SilentlyContinue }

if ($code -ne 0 -or $errors.Count -gt 0) {
    Write-Host ("构建失败：{0} 个错误（{1:N1}s）" -f $errors.Count, $sw.Elapsed.TotalSeconds) -ForegroundColor Red
    exit 1
}
Write-Host ("构建成功：{0}（{1:N0} 字节，{2:N1}s，{3} 个警告）" -f $outExe, (Get-Item $outExe).Length, $sw.Elapsed.TotalSeconds, $warns.Count) -ForegroundColor Green

# ---------------------------------------------------------------- 5) 冒烟测试
if ($Test) {
    Write-Host "`n=== 冒烟测试 ===" -ForegroundColor Cyan
    foreach ($args2 in @(@('version'), @('doctor','--json'), @('manifest','--json'))) {
        Write-Host ("--- dsh-toolbox {0}" -f ($args2 -join ' ')) -ForegroundColor DarkCyan
        & $outExe @args2
        Write-Host ("    exit={0}" -f $LASTEXITCODE) -ForegroundColor DarkGray
    }
}
