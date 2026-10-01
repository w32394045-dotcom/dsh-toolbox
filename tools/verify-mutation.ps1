<#
.SYNOPSIS
    变异测试：确认 verify.ps1 不会被"只会回合法信封的假 exe"骗过。

.DESCRIPTION
    生成一个桩子：伪造 manifest（让 68 条命令看起来都"已注册"），其余调用一律返回
    合法 JSON 信封 + exit 0，但**什么实事都不做**。然后用 verify.ps1 去跑它，
    断言通过项不超过阈值——否则说明验收台只验形状、不验实质。

    这个检查存在的理由：验收台最初的 36 项里，25 个"逐命令检查"只验证
    「退出码 + stdout 单行 + 信封含 ok/cmd/version」，实测这样一个空壳能通过 26 项。

.EXAMPLE
    powershell -File tools\verify-mutation.ps1
    powershell -File tools\verify-mutation.ps1 -MaxPass 20
#>
[CmdletBinding()]
param([int]$MaxPass = 30)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'dist\dsh-toolbox.exe'
$verify = Join-Path $root 'verify.ps1'
if (-not (Test-Path -LiteralPath $exe)) { throw "先构建 exe：powershell -File build.ps1" }

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('tb-mutation-' + $PID)
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    (& $exe manifest --json --quiet) | Set-Content -LiteralPath (Join-Path $tmp 'manifest-raw.json') -Encoding UTF8

    $stubLines = @(
        '$null = $args',
        "if ((`$args -join ' ') -match 'manifest') { Get-Content -LiteralPath (Join-Path `$PSScriptRoot 'manifest-raw.json') -Raw | Write-Output; exit 0 }",
        "Write-Output '{""ok"":true,""cmd"":""stub"",""version"":""0.2.0"",""protocol"":""1"",""elapsedMs"":1,""data"":{""verdict"":true},""items"":[],""count"":0}'",
        'exit 0'
    )
    $stub = Join-Path $tmp 'fake.ps1'
    Set-Content -LiteralPath $stub -Value $stubLines -Encoding UTF8

    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify -Exe $stub -SkipServe 2>&1
    $pass = @($out | Where-Object { $_ -match '\[PASS\]' }).Count
    $fail = @($out | Where-Object { $_ -match '\[FAIL\]' }).Count
    Write-Host ("假 exe 成绩：PASS={0} FAIL={1}（阈值 MaxPass={2}）" -f $pass, $fail, $MaxPass)
    if ($pass -gt $MaxPass) {
        Write-Host ("变异测试失败：一个什么都不做的桩子竟通过 {0} 项 —— 验收台需要更多实质断言" -f $pass) -ForegroundColor Red
        exit 1
    }
    Write-Host ("变异测试通过：桩子只骗过 {0} 项，其余检查识别出它没干实事" -f $pass) -ForegroundColor Green
    exit 0
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
