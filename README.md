# dsh-toolbox —— DSH 工具箱

一个**单文件 Windows exe**，给 DSH agent 当"手和脚"：文件扫描、哈希、进程与服务、系统与网络诊断、
签名与完整性校验、后台任务与日志管理，并且都能被 agent 以稳定 JSON 契约驱动。

* 产物：`dist\dsh-toolbox.exe`（约 300 KB，**只依赖系统自带的 .NET Framework 4.8**）
* 无 Python / Node / .NET SDK / 管理员权限依赖；启动零延迟（适合被频繁调用）
* 机器可读优先：`--json` 一个信封、`--jsonl` 流式、严格退出码、永不静默成功
* 长连接通道：`serve --stdio`（NDJSON 上的 JSON-RPC 2.0），可订阅日志与任务事件
* 验收：`verify.ps1` **36 项全绿**（信封 / 退出码 / stdout 纯净性 / 结论语义 / 破坏性闸门 / 流式契约 / 通道）—— 见 [docs/EVIDENCE.md](docs/EVIDENCE.md)

## 为什么会有这个东西

起因是一次真实的故障排查：DSH 桌面端自动升级失败，报
`Command failed: set "PSModulePath=" & chcp 65001 >NUL & powershell.exe ... Get-AuthenticodeSignature ...`。
查下来是 electron-updater 6.8.9 把 PowerShell 签名校验**硬编码成 20 秒超时**，而这台机器
（机械硬盘 + Defender）实测需要 32~45 秒 —— 超时被 SIGTERM，错误被当成"签名无效"。

结论很直接：**很多系统级能力，agent 手里缺一个可靠、可编程、会说 JSON 的工具**。
于是有了这个工具箱，`sign.verify` 就是它的第一块招牌命令（不设超时、回报耗时毫秒）。

## GUI（双击即用）

同一个 exe：**双击 / 无参数且独占控制台 → 图形界面**；**带参数调用 → CLI**（agent 场景）。
`--gui` / `--cli` 可强制。（实现要点：console 子系统 + `GetConsoleProcessList` 归属判定；
不用 winexe，因为实测 winexe 的 stdout 在 PowerShell 管道捕获下会丢失。）

- 现代浅色 / **深色**双主题：标题栏太阳/月亮按钮一键切换，选择持久化到 `settings.json`
- **多语言**：中文 / English，设置卡片里切换或 `config.set --lang en-US`（也支持 `DSH_TOOLBOX_LANG`）
- **提权**：侧栏底部用户标签点击 → 先弹说明框 → 确认后自动弹 UAC，重启为管理员实例
- **实时进度**：安装/升级/拉取更新走 `--jsonl` 流式，进度条显示百分比与已下载量
- **活动日志**：单击弹出独立窗口（同风格、可选中、可导出），新日志实时同步
- 6 个页面：概览 / 安装与升级 / 维护刷新 / 环境体检 / 任务与日志 / 关于（含设置）
- GUI 与 agent **共用同一套命令实现**（界面通过子进程调自己的 CLI），界面能用即通道可用
## 快速开始

```powershell
$exe = '<repo-root>\dist\dsh-toolbox.exe'

& $exe help                      # 命令总览
& $exe manifest --json           # 机器可读清单（agent 自发现用）
& $exe doctor --json             # 环境自检
& $exe sysinfo
& $exe scan find --path . --ext .cs --newer 7d
& $exe proc.list --sort mem --top 15
& $exe sign.verify --path C:\Windows\System32\notepad.exe --json
& $exe log.tail --lines 30
```

详细接入方式（JSON 契约、退出码、日志与任务目录、serve 协议、安全约定）见
[docs/AGENT-GUIDE.md](docs/AGENT-GUIDE.md)。

**完整命令清单**（由 `manifest --json` 自动生成，51 条 / 14 组）：[docs/COMMANDS.md](docs/COMMANDS.md)

## 命令总览

| 组 | 命令 | 状态 |
|---|---|---|
| 核心 | `doctor` `sysinfo` `env` `manifest` `version` `help` | ✅ v0.1 |
| 文件扫描 | `scan.find` `scan.size` `scan.tree` `scan.dup` `scan.snapshot` `scan.verify` `scan.recent` `scan.empty-dirs` | ✅ 已验收 |
| 哈希 | `hash.file` `hash.dir` `hash.compare` | ✅ 已验收 |
| 进程 | `proc.list` `proc.tree` `proc.find` `proc.kill` `proc.wait` `proc.port` | ✅ 已验收 |
| 服务与系统 | `svc.list` `svc.control` `disk.space` `eventlog.query` `installed.list` `defender.status` `startup.list` | ✅ 已验收 |
| 网络 | `net.ports` `net.tcp` `net.http` `net.dns` `net.ip` `net.download` | ✅ 已验收 |
| 签名与完整性 | `sign.verify` `sign.chain` `sign.hash` `sign.motw` | ✅ 已验收 |
| 任务与日志 | `run` `job.start/list/status/output/kill` `log.append/tail/search/runs` | ✅ 已验收 |
| 通道 | `serve --stdio`（JSON-RPC 2.0） | ✅ 已验收 |
| 第二阶段 | 批量改名/归档/编码转换/ACL/同步、json·csv·yaml 工具、事件与性能采样、artifact 存储、报告生成… | 📋 [docs/FEATURES.md](docs/FEATURES.md) |

图例：✅ 已实现并通过真实调用验证（36 项验收全绿）；📋 已规格化待排期（见 docs/FEATURES.md 与 docs/ROADMAP.md）。

## 构建

```powershell
pwsh -File build.ps1                       # → dist\dsh-toolbox.exe
pwsh -File build.ps1 -Out check-me.exe     # 并行开发时各用各的输出名
pwsh -File build.ps1 -Test                 # 构建后跑冒烟测试
```

编译器是 NuGet 上的 **Roslyn 4.14**（已缓存在 `.tools\roslyn-4.14.0\`），
目标运行时是系统自带的 **.NET Framework 4.8** —— 所以 exe 只有几十 KB，不需要装任何 SDK。
首次构建前需要 `.tools\roslyn-4.14.0\tasks\net472\csc.exe` 存在（若缺失，脚本会回退到系统自带 csc）。

## 架构一页

```
src\Program.cs            入口：全局选项、命令解析、超时、运行记录、错误兜底、help
src\Core\Json.cs          零依赖 JSON（保序写出 + 复用系统解析器）
src\Core\Cli.cs           选项解析（--k v / --k=v / -k v，可重复累加，不做逗号分割）
src\Core\Runtime.cs       Ctx / Output(信封) / Log(JSONL) / Paths / Registry / ExitCodes / ToolException
src\Core\Fs.cs            glob→regex、安全遍历、大小与时长解析、哈希(md5..sha512+crc32)、快速指纹、并行映射
src\Commands\*.cs         各命令模块，每个模块一个 Register()，由 Program 统一挂载
```

契约是**冻结**的：`docs/CLI-CONTRACT.md`（对外承诺）与 `docs/ARCHITECTURE.md`（内部约定）。
两者是实现与并行开发的唯一依据。

## 设置

```
%LOCALAPPDATA%\dsh-toolbox\settings.json   { "lang": "zh-CN", "theme": "light" }
```

```powershell
& $exe config.get --json                    # 读取语言/主题/路径
& $exe config.set --theme dark --json        # 深色
& $exe config.set --lang en-US --json        # 英文
& $exe compat.check --json                   # 环境体检（含 CPU 指令集检测）
& $exe install.check --json                  # 官方源最新版本/大小/SHA512
& $exe maint.kill-leftovers --dry-run --json # 残留进程（零副作用预览）
```
## 运行时数据

```
%LOCALAPPDATA%\dsh-toolbox\
  logs\toolbox-YYYYMMDD.jsonl   结构化日志
  runs\runs.jsonl               每次调用记录（cmd/argv/exit/ms/cwd/pid）
  runs\<runid>.out.json         大结果落盘
  jobs\<jobid>\                 后台任务（cmd.json / stdout.log / stderr.log / status.json）
```

可用 `--home <dir>` 或环境变量 `DSH_TOOLBOX_HOME` 覆盖。

## 安全约定

* 破坏性操作默认拒绝执行（退出码 2）：先 `--dry-run` 预览，确认后 `--yes`。
* `--dry-run` 保证零副作用。
* `env` 默认对疑似密钥脱敏，`--show-secrets` 才显示原值。
* 只结束明确匹配目标的进程（按名称 / 可执行文件路径），绝不误伤无关进程。
