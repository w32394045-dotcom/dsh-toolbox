# dsh-toolbox —— Agent 接入指南

面向 **DSH agent（以及其它自动化程序）**。人类的入门请看 `README.md`。

## 0. 产物位置

```
exe      <repo-root>\dist\dsh-toolbox.exe     （单文件，约 60 KB）
源码     <repo-root>\src\                      （C#，Roslyn 编译）
构建     <repo-root>\build.ps1                 （pwsh -File build.ps1）
文档     docs\ARCHITECTURE.md  docs\CLI-CONTRACT.md  docs\FEATURES.md  docs\ROADMAP.md
运行时   %LOCALAPPDATA%\dsh-toolbox\   （logs / runs / jobs / cache）
```

依赖：仅系统自带 **.NET Framework 4.8**。无需 Python / Node / .NET SDK / 管理员权限。

## 1. 三种接入方式（按场景选）

| 方式 | 适用 | 特点 |
|---|---|---|
| **A. 一次性 CLI** | 绝大多数情况 | 每次调用一个新进程，`--json` 拿一个信封 |
| **B. `--jsonl` 流式** | 结果可能很多 / 要边扫边处理 | 每行一个 JSON，首行 meta、末行 summary |
| **C. `serve --stdio`** | 需要长连接、实时看日志/任务 | NDJSON 上的 JSON-RPC 2.0，可订阅事件 |

Agent 的典型用法就是 A：**运行命令 → 解析一个 JSON 对象 → 看 `ok` 与退出码**。

## 2. 一次性调用（推荐）

```powershell
$exe = '<repo-root>\dist\dsh-toolbox.exe'

# 机器可读清单：先看有哪些命令（agent 自发现）
& $exe manifest --json | ConvertFrom-Json

# 单条命令
$r = & $exe scan find --path '<repo-root>' --ext .cs --json | ConvertFrom-Json
if ($r.ok) { $r.data.items | Select-Object -First 5 } else { $r.error }
```

解析要点（契约细节见 `docs/CLI-CONTRACT.md`）：

* stdout **永远只有一个对象**（`--json`）或纯帧（`--jsonl`）；人类日志、警告一律走 stderr。
* 成功：`{ok:true, cmd, version, elapsedMs, data, warnings, truncated}`。
* 失败：`{ok:false, cmd, error:{code, message, hint}}` **且退出码非 0**。
* 列表类结果在 `data.items`，配 `data.count`；`data.columns` 是人类表格列名。
* 失败项在 `data.failures`，此时退出码是 **6（部分成功）**，`data` 仍可用。
* `truncated:true` 表示有截断（`--max-results` / 超时），需要完整结果就加 `--jsonl`。

### 退出码（判断成败请用退出码，不要只匹配文案）

| 0 成功 | 1 运行期错误 | 2 用法错误 | 3 目标不存在 | 4 权限被拒绝 | 5 超时 | 6 部分成功 | 130 被取消 |
|---|---|---|---|---|---|---|---|

## 3. 让 agent "看日志 / 检查结果"

所有调用都会留痕，agent 可以随时回看：

```
%LOCALAPPDATA%\dsh-toolbox\logs\toolbox-YYYYMMDD.jsonl   结构化日志（每行一个 JSON）
%LOCALAPPDATA%\dsh-toolbox\runs\runs.jsonl               每次调用：cmd/argv/exit/ms/cwd/pid
%LOCALAPPDATA%\dsh-toolbox\runs\<runid>.out.json         大结果落盘
%LOCALAPPDATA%\dsh-toolbox\jobs\<jobid>\                 后台任务：cmd.json / stdout.log / stderr.log / status.json
```

```powershell
& $exe log.tail --lines 50 --json          # 最近日志
& $exe log.search --pattern 'E_' --limit 20 --json
& $exe log.runs --exit 1 --json            # 只看失败过的调用
& $exe run -- cmd /c 'echo hi & exit 3' --json   # 执行并完整记录
& $exe job.start -- cmd /c 'ping -n 30 127.0.0.1' --json
& $exe job.status --id <jobid> --json
& $exe job.output --id <jobid> --tail 40 --json
```

环境变量 `DSH_TOOLBOX_RUN_ID` 会传给子进程，便于把外部工具的输出与某次调用关联。

## 4. 长连接通道 `serve --stdio`

用于"DSH 常驻连接工具箱、持续读日志与任务结果"。协议是 **NDJSON 上的 JSON-RPC 2.0**，
完整方法表见 `docs/CLI-CONTRACT.md` §8。

```jsonc
// 请求（每行一个 JSON）
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
{"jsonrpc":"2.0","id":2,"method":"call","params":{"cmd":"doctor","args":["--json"]}}
{"jsonrpc":"2.0","id":3,"method":"log.tail","params":{"lines":20,"follow":true}}
{"jsonrpc":"2.0","id":9,"method":"shutdown"}
```

* `initialize` 之前发其它请求 → `-32002`；未知方法 → `-32601`；参数错误 → `-32602`。
* `follow:true` 时，服务端以**无 id 的 `event` 通知**持续推送新日志行。
* stdout 只允许协议帧；stderr 是人类文本。

## 4.5 宿主运维与安装命令（v0.2 新增）

```powershell
& $exe host.status --json                  # 桌面端/CLI 安装情况、版本、进程、Web 端口、权限
& $exe compat.check --json                 # 环境体检（13 项：OS/CPU 指令集/.NET/PowerShell/长路径/Defender/磁盘/TLS/VC++/更新源/残留进程）
& $exe compat.check --fast --json          # 跳过联网探测（~0.2 秒）
& $exe compat.check --deep --json          # 额外启动 PowerShell 探测 LanguageMode（本机约 3 秒）
& $exe compat.fix --id longpaths --yes     # 可自动修复项（需管理员 → 用 elevate.run）
& $exe maint.kill-leftovers --dry-run      # 结束 DSH 残留进程（--yes 真执行）
& $exe maint.clean-cache --dry-run         # 清理缓存（--all 含会话存储）
& $exe maint.restart-host --yes            # 重启宿主（刷新插件）
& $exe maint.pull-update --yes             # 清更新缓存并重启，重新拉取
& $exe maint.rebuild-self                  # 用 build.ps1 重建工具箱自身
& $exe install.check --json                # 官方更新源：最新版本/URL/大小/SHA512
& $exe install.desktop --yes               # 安装/升级桌面端（校验大小+SHA512+签名→清残留→静默安装→校验→启动）
& $exe install.cli --yes                   # 安装官方 CLI（npm 包 @deepseek-ai/dsh，用户级，不需要管理员）
& $exe install.verify --file x.exe --json  # 只校验不安装
& $exe elevate.run -- compat.fix --id longpaths --yes --json   # 弹 UAC 执行（结果经临时文件回传）
& $exe config.get --json / config.set --theme dark --lang en-US
```

要点：

* **验证类命令**（`sign.verify`/`hash.compare`/`scan.verify`/`hash.file --expected`/`install.verify`）：
  结论为否时 `ok:true` + **exit 1** + `data.verdict=false`（见契约 §4.1）。
* **包装/后台类**（`run`/`job.*`）：被包装命令失败时工具返回 exit 6，子命令退出码在 `data.exitCode`（契约 §4.2）。
* **需要管理员的命令**在非管理员下返回 `E_ELEVATION_REQUIRED`（exit 4）；agent 应改用 `elevate.run`（会弹 UAC，需要人在场）。
* **语言**：所有命令都支持 `--lang zh-CN|en-US`；GUI 与 CLI 共享 `settings.json`。
* **进度**：`install.desktop` 等长任务在 `--jsonl` 模式下会输出 `{"type":"item",...,"percent":45,"done":"128 MB","total":"276 MB"}` 进度帧。
## 5. 安全约定（agent 必须遵守）

1. **破坏性操作**（删除/移动/覆盖/杀进程/写注册表/改 ACL）默认拒绝，退出码 2；
   先 `--dry-run` 预览，确认后再 `--yes`。
2. `--dry-run` **保证零副作用**，可以放心用。
3. 不要用工具箱去杀 `DeepSeek Harness` / DSH 自身进程，除非明确要求重启宿主。
4. `env` 默认对疑似密钥脱敏（`token/secret/password/key/...`），需要原值显式加 `--show-secrets`。
5. 批量操作优先 `--jsonl` + `--max-results`，避免一次把巨量结果读进内存。

## 6. 给 agent 的调用模板

```powershell
function Invoke-Toolbox {
  param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Args2)
  $exe = '<repo-root>\dist\dsh-toolbox.exe'
  $raw = & $exe @Args2 --json
  $code = $LASTEXITCODE
  $obj  = $null
  try { $obj = $raw | ConvertFrom-Json } catch { }
  [pscustomobject]@{ ExitCode = $code; Result = $obj; Raw = $raw }
}

$r = Invoke-Toolbox doctor
if ($r.ExitCode -eq 0) { '环境正常' } else { $r.Result.error.message }
```

## 7. 扩展一个新命令（开发者）

1. 在 `src\Commands\` 下挑一个你拥有的文件（或新建 `XxxCommands.cs`，但要先告知 Lead 并让 Lead 在
   `Program.RegisterAll()` 挂上 `XxxCommands.Register()`）。
2. 在 `Register()` 里 `Registry.Add("group.action", "一句话说明", "用法", RunXxx)`。
3. `RunXxx(Ctx ctx)` 里：读参数 → 干活 → `ctx.Out.Result("group.action", data)` → `return ExitCodes.Ok`。
4. 构建：`pwsh -File build.ps1`（并行开发时用 `-Out check-<名字>.exe` 避免覆盖）。
5. 自测：`& dist\dsh-toolbox.exe group action --json`，确认信封字段与退出码。

常见坑：

* **不要**自己 `Console.WriteLine` JSON；一律走 `ctx.Out.Result`，否则 stdout 不再纯净。
* C# 运行时是 **.NET Framework 4.8**：不能用 `record` / `init` / `Index`·`Range` / `System.Text.Json`。
* 构建脚本已加 `/codepage:65001`：源文件按 UTF-8 解析，中文字面量安全。
* 长循环必须检查 `ctx.Cancel`（支持 `--timeout`）。
