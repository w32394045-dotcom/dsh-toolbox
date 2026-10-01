#Requires -Version 5.1
<#
.SYNOPSIS
    dsh-toolbox 验收台：对已构建的 exe 跑一批真实命令，逐条核对契约与退出码。

.DESCRIPTION
    检查项：
      1) 每条命令 --json 的 stdout 必须是**单行合法 JSON 信封**（ok/cmd/version 三字段齐全）；
      2) 退出码必须与 ok 语义一致（含 §4.1 验证类例外：结论为否 = ok:true + exit 1）；
      3) 未知命令/缺必填参数 → exit 2；
      4) 破坏性命令 --dry-run 必须**零副作用**（用一个真实进程做证）；
      5) serve --stdio 的 JSON-RPC 帧与错误码；
      6) 全量命令清单与分组统计。
    未注册的命令自动 SKIP（便于在并行开发中途使用）。

.EXAMPLE
    pwsh -File verify.ps1
    pwsh -File verify.ps1 -Exe dist\check-joint.exe -Verbose
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [switch]$SkipServe,
    [switch]$SkipDestructive
)

$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'dist\dsh-toolbox.exe' }
elseif (-not [System.IO.Path]::IsPathRooted($Exe)) { $Exe = Join-Path $root $Exe }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "找不到 exe: $Exe" -ForegroundColor Red; exit 2 }

$script:Pass = 0; $script:Fail = 0; $script:Skip = 0
$script:Rows = New-Object System.Collections.Generic.List[object]

function Record($name, $status, $detail) {
    $script:Rows.Add([pscustomobject]@{ 检查项 = $name; 结果 = $status; 说明 = $detail })
    switch ($status) {
        'PASS' { $script:Pass++; Write-Host ("  [PASS] {0}  {1}" -f $name, $detail) -ForegroundColor Green }
        'FAIL' { $script:Fail++; Write-Host ("  [FAIL] {0}  {1}" -f $name, $detail) -ForegroundColor Red }
        'SKIP' { $script:Skip++; Write-Host ("  [SKIP] {0}  {1}" -f $name, $detail) -ForegroundColor DarkGray }
    }
}

# ---------------------------------------------------------------- 运行包装
function Invoke-Tb {
    param([string[]]$Args2, [int]$TimeoutSec = 120)
    $raw = @(& $Exe @Args2 2>$null)
    $code = $LASTEXITCODE
    $text = ($raw | Out-String).Trim()
    $obj = $null
    try { $obj = $text | ConvertFrom-Json } catch { }
    return [pscustomobject]@{ Exit = $code; Text = $text; Obj = $obj; Lines = $raw.Count }
}

# ---------------------------------------------------------------- 清单与可用命令
Write-Host "=== dsh-toolbox 验收台 ===" -ForegroundColor Cyan
Write-Host ("exe: {0}  ({1:N0} 字节)" -f $Exe, (Get-Item -LiteralPath $Exe).Length)
$mf = Invoke-Tb @('manifest', '--json')
if (-not $mf.Obj -or -not $mf.Obj.ok) { Write-Host "manifest 不可用，无法继续" -ForegroundColor Red; exit 2 }
$available = @{}
foreach ($c in $mf.Obj.data.commands) { $available[$c.name] = $true }
Write-Host ("已注册命令: {0} 个" -f $available.Count) -ForegroundColor Cyan

# ---------------------------------------------------------------- 1. 命令抽查
Write-Host "`n--- 1) 命令抽查（--json 信封 + 退出码） ---" -ForegroundColor Cyan
$cases = @(
    @{ n = 'doctor';          a = @('doctor', '--json');                                   exp = 0 },
    @{ n = 'sysinfo';         a = @('sysinfo', '--fast', '--json');                        exp = 0 },
    @{ n = 'env';             a = @('env', '--match', '^DSH_', '--json');                  exp = 0 },
    @{ n = 'scan.find';       a = @('scan.find', '--path', $root, '--ext', '.cs', '--json'); exp = 0 },
    @{ n = 'scan.size';       a = @('scan.size', '--path', $root, '--top', '5', '--json'); exp = 0 },
    @{ n = 'scan.tree';       a = @('scan.tree', '--path', $root, '--depth', '2', '--json'); exp = 0 },
    @{ n = 'scan.recent';     a = @('scan.recent', '--path', $root, '--newer', '7d', '--json'); exp = 0 },
    @{ n = 'scan.dup';        a = @('scan.dup', '--path', (Join-Path $root 'src'), '--json'); exp = 0 },
    @{ n = 'scan.empty-dirs'; a = @('scan.empty-dirs', '--path', $root, '--json');         exp = 0 },
    @{ n = 'hash.file';       a = @('hash.file', '--path', (Join-Path $root 'src\Program.cs'), '--algo', 'sha256', '--json'); exp = 0 },
    @{ n = 'hash.dir';        a = @('hash.dir', '--path', (Join-Path $root 'src\Core'), '--json'); exp = 0 },
    @{ n = 'proc.list';       a = @('proc.list', '--top', '5', '--json');                  exp = 0 },
    @{ n = 'proc.find';       a = @('proc.find', '--name', 'explorer', '--json');           exp = 0 },
    @{ n = 'proc.port';       a = @('proc.port', '--port', '19387', '--json');              exp = 0 },
    @{ n = 'svc.list';        a = @('svc.list', '--max', '3', '--json');                    exp = 0 },
    @{ n = 'disk.space';      a = @('disk.space', '--json');                               exp = 0 },
    @{ n = 'installed.list';  a = @('installed.list', '--max', '5', '--json');              exp = 0 },
    @{ n = 'startup.list';    a = @('startup.list', '--json');                             exp = 0 },
    @{ n = 'net.ports';       a = @('net.ports', '--json');                                exp = 0 },
    @{ n = 'net.tcp';         a = @('net.tcp', '--host', '127.0.0.1', '--port', '19387', '--json'); exp = 0 },
    @{ n = 'net.ip';          a = @('net.ip', '--json');                                   exp = 0 },
    @{ n = 'log.append';      a = @('log.append', '--msg', 'verify.ps1 smoke', '--json');  exp = 0 },
    @{ n = 'log.tail';        a = @('log.tail', '--lines', '3', '--json');                 exp = 0 },
    @{ n = 'log.runs';        a = @('log.runs', '--limit', '3', '--json');                 exp = 0 },
    @{ n = 'job.list';        a = @('job.list', '--json');                                 exp = 0 }
)
foreach ($c in $cases) {
    if (-not $available.ContainsKey($c.n)) { Record $c.n 'SKIP' '命令未注册'; continue }
    $r = Invoke-Tb $c.a
    if ($r.Exit -ne $c.exp) { Record $c.n 'FAIL' ("退出码 {0}，期望 {1}" -f $r.Exit, $c.exp); continue }
    if (-not $r.Obj) { Record $c.n 'FAIL' 'stdout 不是合法 JSON'; continue }
    if ($null -eq $r.Obj.ok -or -not $r.Obj.cmd -or -not $r.Obj.version) { Record $c.n 'FAIL' '信封缺 ok/cmd/version'; continue }
    if ($r.Lines -ne 1) { Record $c.n 'FAIL' ("stdout 应为 1 行，实际 {0} 行（JSON 模式必须纯净）" -f $r.Lines); continue }
    Record $c.n 'PASS' ("{0}ms ok={1}" -f $r.Obj.elapsedMs, $r.Obj.ok)
}

# ---------------------------------------------------------------- 2. 验证类退出码（§4.1）
Write-Host "`n--- 2) 验证类命令的结论语义（§4.1） ---" -ForegroundColor Cyan
if ($available.ContainsKey('sign.verify')) {
    $r = Invoke-Tb @('sign.verify', '--path', 'C:\Windows\explorer.exe', '--json')
    if ($r.Exit -eq 0 -and $r.Obj.ok -and $r.Obj.data.valid) {
        Record 'sign.verify(已签名)' 'PASS' ("Valid, verdict={0}, {1}ms" -f $r.Obj.data.verdict, $r.Obj.data.elapsedMs)
    } else { Record 'sign.verify(已签名)' 'FAIL' ("exit={0} ok={1} valid={2}" -f $r.Exit, $r.Obj.ok, $r.Obj.data.valid) }

    # System32 目录签名文件：必须与系统判定一致（这是最容易假阴性的地方）
    $cat = Invoke-Tb @('sign.verify', '--path', 'C:\Windows\System32\notepad.exe', '--json')
    $sysStatus = (Get-AuthenticodeSignature -LiteralPath 'C:\Windows\System32\notepad.exe').Status.ToString()
    if ($cat.Obj -and $cat.Obj.data.valid -and $sysStatus -eq 'Valid') {
        Record 'sign.verify(目录签名)' 'PASS' ("notepad.exe: kind={0} 与系统判定一致" -f $cat.Obj.data.signatureKind)
    } else {
        Record 'sign.verify(目录签名)' 'FAIL' ("工具 valid={0} signatureKind={1}，系统判定={2}" -f $cat.Obj.data.valid, $cat.Obj.data.signatureKind, $sysStatus)
    }
} else { Record 'sign.verify' 'SKIP' '命令未注册' }

# ---------------------------------------------------------------- 3. 用法错误
if ($available.ContainsKey('hash.compare')) {
    $tmp = Join-Path $env:TEMP ('tb-verify-cmp-{0}' -f $PID)
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    Set-Content -Path (Join-Path $tmp 'a') -Value 'same'
    Copy-Item (Join-Path $tmp 'a') (Join-Path $tmp 'b') -Force
    Set-Content -Path (Join-Path $tmp 'c') -Value 'diff'
    $eq = Invoke-Tb @('hash.compare', '--a', (Join-Path $tmp 'a'), '--b', (Join-Path $tmp 'b'), '--json')
    $ne = Invoke-Tb @('hash.compare', '--a', (Join-Path $tmp 'a'), '--b', (Join-Path $tmp 'c'), '--json')
    if ($eq.Exit -eq 0 -and $eq.Obj.data.verdict -eq $true -and $ne.Exit -eq 1 -and $ne.Obj.data.verdict -eq $false) {
        Record 'hash.compare 结论语义' 'PASS' '相同→exit0/verdict=true；不同→exit1/verdict=false'
    } else {
        Record 'hash.compare 结论语义' 'FAIL' ("eq exit={0} verdict={1}; ne exit={2} verdict={3}" -f $eq.Exit, $eq.Obj.data.verdict, $ne.Exit, $ne.Obj.data.verdict)
    }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($available.ContainsKey('scan.verify') -and $available.ContainsKey('scan.snapshot')) {
    $tmp2 = Join-Path $env:TEMP ('tb-verify-tree-{0}' -f $PID)
    $snap = Join-Path $env:TEMP ('tb-verify-snap-{0}.json' -f $PID)
    New-Item -ItemType Directory -Path $tmp2 -Force | Out-Null
    Set-Content -Path (Join-Path $tmp2 'x.txt') -Value 'v1'
    $null = Invoke-Tb @('scan.snapshot', '--path', $tmp2, '--out', $snap, '--json')
    $v0 = Invoke-Tb @('scan.verify', '--path', $tmp2, '--snapshot', $snap, '--json')
    Set-Content -Path (Join-Path $tmp2 'x.txt') -Value 'v2'
    $v1 = Invoke-Tb @('scan.verify', '--path', $tmp2, '--snapshot', $snap, '--json')
    if ($v0.Exit -eq 0 -and $v0.Obj.data.verdict -eq $true -and $v1.Exit -eq 1 -and $v1.Obj.data.verdict -eq $false) {
        Record 'scan.verify 结论语义' 'PASS' '无变更→exit0/verdict=true；有变更→exit1/verdict=false'
    } else {
        Record 'scan.verify 结论语义' 'FAIL' ("v0 exit={0} verdict={1}; v1 exit={2} verdict={3}" -f $v0.Exit, $v0.Obj.data.verdict, $v1.Exit, $v1.Obj.data.verdict)
    }
    Remove-Item $tmp2 -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $snap -Force -ErrorAction SilentlyContinue
}
if ($available.ContainsKey('scan.find')) {
    $jl = @(& $Exe 'scan.find' '--path' $root '--ext' '.cs' '--jsonl' 2>$null)
    $first = ''
    if ($jl.Count -gt 0) { try { $first = ($jl[0] | ConvertFrom-Json).type } catch { $first = 'parse-error' } }
    if ($jl.Count -ge 2 -and $first -eq 'meta') { Record 'jsonl meta 前置' 'PASS' ("{0} 帧，第一帧 meta（契约 §3）" -f $jl.Count) }
    else { Record 'jsonl meta 前置' 'FAIL' ("{0} 帧，第一帧 {1}" -f $jl.Count, $first) }
}
Write-Host "`n--- 3) 用法错误与未知命令 ---" -ForegroundColor Cyan
$u1 = Invoke-Tb @('nosuchcmd', '--json')
if ($u1.Exit -eq 2 -and $u1.Obj -and -not $u1.Obj.ok -and $u1.Obj.error.code) { Record '未知命令' 'PASS' ("exit=2 code={0}" -f $u1.Obj.error.code) }
else { Record '未知命令' 'FAIL' ("exit={0}" -f $u1.Exit) }
if ($available.ContainsKey('scan.find')) {
    $u2 = Invoke-Tb @('scan.find', '--json')
    if ($u2.Exit -eq 2) { Record '缺必填参数' 'PASS' 'exit=2' } else { Record '缺必填参数' 'FAIL' ("exit={0}，期望 2" -f $u2.Exit) }
}
if ($available.ContainsKey('proc.kill')) {
    # 目标不存在时正确语义是 3（E_NOT_FOUND），不是用法错误
    $u3 = Invoke-Tb @('proc.kill', '--name', '__definitely_not_running__', '--json')
    if ($u3.Exit -eq 3) { Record 'proc.kill 目标不存在' 'PASS' 'exit=3（E_NOT_FOUND）' }
    else { Record 'proc.kill 目标不存在' 'FAIL' ("exit={0}，期望 3" -f $u3.Exit) }
}

# ---------------------------------------------------------------- 4. --dry-run 零副作用（真实进程做证）
Write-Host "`n--- 4) --dry-run 零副作用（真实进程做证） ---" -ForegroundColor Cyan
if ($SkipDestructive -or -not $available.ContainsKey('proc.kill')) {
    Record 'dry-run 零副作用' 'SKIP' '跳过'
} else {
    $victim = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c','ping -n 120 127.0.0.1 >NUL' -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    try {
        $alive0 = -not $victim.HasExited
        $dr = Invoke-Tb @('proc.kill', '--id', "$($victim.Id)", '--dry-run', '--json')
        Start-Sleep -Seconds 1
        $victim.Refresh()
        $alive1 = -not $victim.HasExited
        if ($alive0 -and $alive1 -and $dr.Exit -eq 0 -and $dr.Obj.ok) {
            Record 'dry-run 零副作用' 'PASS' ("PID {0} 在 --dry-run 后仍存活，exit=0" -f $victim.Id)
        } else {
            Record 'dry-run 零副作用' 'FAIL' ("alive0={0} alive1={1} exit={2}" -f $alive0, $alive1, $dr.Exit)
        }
        # 真实目标 + 未加 --yes：必须被闸门挡住，且不得杀掉进程
        $ng = Invoke-Tb @('proc.kill', '--id', "$($victim.Id)", '--json')
        Start-Sleep -Milliseconds 800
        $victim.Refresh()
        $alive2 = -not $victim.HasExited
        if ($ng.Exit -eq 2 -and $alive2) {
            Record '破坏性操作未确认' 'PASS' ("exit=2 且 PID {0} 未被杀" -f $victim.Id)
        } else {
            Record '破坏性操作未确认' 'FAIL' ("exit={0} alive={1}（期望 exit=2 且存活）" -f $ng.Exit, $alive2)
        }
    } finally {
        try { if (-not $victim.HasExited) { Stop-Process -Id $victim.Id -Force } } catch { }
    }
}

# ---------------------------------------------------------------- 5. serve 通道
Write-Host "`n--- 5) serve --stdio 通道 ---" -ForegroundColor Cyan
if ($SkipServe -or -not $available.ContainsKey('serve')) {
    Record 'serve 通道' 'SKIP' '跳过'
} else {
    $inF = Join-Path $env:TEMP ('tb-verify-rpc-{0}.ndjson' -f $PID)
    $outF = Join-Path $env:TEMP ('tb-verify-rpc-out-{0}.ndjson' -f $PID)
    $errF = Join-Path $env:TEMP ('tb-verify-rpc-err-{0}.txt' -f $PID)
    @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}',
        '{"jsonrpc":"2.0","id":2,"method":"ping"}',
        '{"jsonrpc":"2.0","id":3,"method":"commands.list"}',
        '{"jsonrpc":"2.0","id":4,"method":"call","params":{"cmd":"disk.space","args":["--json"]}}',
        '{"jsonrpc":"2.0","id":5,"method":"nosuch.method"}',
        '{"jsonrpc":"2.0","id":6,"method":"shutdown"}'
    ) -join "`r`n" | ForEach-Object { [System.IO.File]::WriteAllText($inF, $_, (New-Object System.Text.UTF8Encoding($false))) }
    $proc = Start-Process -FilePath $Exe -ArgumentList 'serve', '--stdio' `
                          -RedirectStandardInput $inF -RedirectStandardOutput $outF -RedirectStandardError $errF `
                          -PassThru -NoNewWindow
    $exited = $proc.WaitForExit(60000)
    if (-not $exited) { try { $proc.Kill() } catch { } }
    $frames = @(Get-Content -LiteralPath $outF -Encoding UTF8 -ErrorAction SilentlyContinue)
    $okFrames = 0; $detail = ''
    foreach ($f in $frames) { try { $null = $f | ConvertFrom-Json; $okFrames++ } catch { } }
    $hasInit = @($frames | Where-Object { $_ -match '"id":1' -and $_ -match 'serverInfo' }).Count -gt 0
    $hasUnknown = @($frames | Where-Object { $_ -match '\-32601' }).Count -gt 0
    $hasBye = @($frames | Where-Object { $_ -match '"id":6' }).Count -gt 0
    if ($exited -and $okFrames -eq $frames.Count -and $frames.Count -ge 6 -and $hasInit -and $hasUnknown -and $hasBye) {
        Record 'serve 通道' 'PASS' ("{0} 帧全部合法 JSON，initialize/-32601/shutdown 均正确，干净退出" -f $frames.Count)
    } else {
        Record 'serve 通道' 'FAIL' ("exited={0} frames={1} valid={2} init={3} unknown={4} bye={5}" -f $exited, $frames.Count, $okFrames, $hasInit, $hasUnknown, $hasBye)
    }
    Remove-Item $inF, $outF, $errF -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- 汇总
Write-Host "`n=== 汇总 ===" -ForegroundColor Cyan
$script:Rows | Format-Table -AutoSize | Out-String | Write-Host
Write-Host ("PASS={0}  FAIL={1}  SKIP={2}" -f $script:Pass, $script:Fail, $script:Skip) -ForegroundColor $(if ($script:Fail -gt 0) { 'Red' } else { 'Green' })
if ($script:Fail -gt 0) { exit 1 }
exit 0
