<#
.SYNOPSIS
    由 `manifest --json` 重新生成 docs\COMMANDS.md（命令清单）。

.DESCRIPTION
    保留文件里手写的前言部分（双模说明 / 全局选项 / 退出码），只重建 `## 命令` 段落，
    并刷新表头里的版本、命令总数、分组数、生成时间与 exe 的 SHA256。

.EXAMPLE
    powershell -File tools\gen-commands.ps1
#>
[CmdletBinding()]
param([string]$Exe = '', [string]$Out = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Exe) { $Exe = Join-Path $root 'dist\dsh-toolbox.exe' }
if (-not $Out) { $Out = Join-Path $root 'docs\COMMANDS.md' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "找不到 exe: $Exe（先跑 build.ps1）" }

$m = (& $Exe manifest --json --quiet | Out-String).Trim() | ConvertFrom-Json
if (-not $m.ok) { throw "manifest 返回失败" }
$d = $m.data

# 保留手写前言（到 "## 命令" 之前），只刷新表头统计
$head = @(
    '# dsh-toolbox 命令清单（自动生成）',
    '',
    '> 由 `manifest --json` 自动生成，**请勿手改**；重新生成：`powershell -File tools\gen-commands.ps1`',
    '',
    ('- 版本 `{0}`　协议 `{1}`　命令总数 **{2}**　分组 {3} 个' -f $d.version, $d.protocol, $d.count, ($d.commands | Group-Object group).Count),
    ('- 生成时间 {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')),
    ('- SHA256 `{0}`' -f (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash),
    '',
    '## 双模说明',
    '',
    '- 双击 / 无参数且独占控制台 → **启动 GUI**；`--gui` 强制 GUI，`--cli` 强制命令行。',
    '- 带参数调用（agent 场景）→ **CLI**，stdout 输出 JSON 信封。',
    '',
    '## 全局选项'
)
foreach ($o in $d.globalOptions) { $head += ('- `{0}`{1} — {2}' -f $o.name, $(if ($o.type) { " ($($o.type))" } else { '' }), $o.desc) }
$head += @('', '## 退出码')
foreach ($e in $d.exitCodes) { $head += ('- `{0}` — {1}' -f $e.code, $e.desc) }
$head += @('', '## 命令', '')

$sb = New-Object System.Text.StringBuilder
foreach ($line in $head) { [void]$sb.AppendLine($line) }

foreach ($g in ($d.commands | Group-Object group | Sort-Object { if ($_.Name) { $_.Name } else { ' ' } })) {
    $label = if ($g.Name) { $g.Name } else { '（无分组）' }
    [void]$sb.AppendLine(('### {0}（{1} 条）' -f $label, $g.Count))
    [void]$sb.AppendLine()
    foreach ($c in ($g.Group | Sort-Object name)) {
        [void]$sb.AppendLine(('#### `{0}`' -f $c.name))
        [void]$sb.AppendLine()
        [void]$sb.AppendLine($c.summary)
        [void]$sb.AppendLine()
        if ($c.usage) { [void]$sb.AppendLine(('- 用法：`{0}`' -f $c.usage)) }
        foreach ($ex in @($c.examples)) { if ($ex) { [void]$sb.AppendLine(('- 例：`{0}`' -f $ex)) } }
        [void]$sb.AppendLine()
    }
}

[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("已生成 {0}：{1} 条命令 / {2} 个分组，{3} 行" -f (Split-Path $Out -Leaf), $d.count, ($d.commands | Group-Object group).Count, ($sb.ToString() -split "`n").Count) -ForegroundColor Green