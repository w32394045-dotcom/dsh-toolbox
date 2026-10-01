# dsh-toolbox 验收记录（Lead 出具）

> 本文件是目标的**最终验收证据**：所有结论都来自本机真实执行，命令可复现。
> 采集时间：2026-10-01 12:16（本机时区 +08:00）

## 1. 验收标准对照

| 目标要求 | 状态 | 证据 |
|---|---|---|
| 交付可运行的 **exe** | ✅ | `dist\dsh-toolbox.exe`，305,664 字节，SHA256 `9798180240A4AB161CAE4674C672A9450B12B12AB6D76C5F40A0C1C09E84BE7E` |
| **单文件**、依赖系统自带 .NET Framework 4.8 | ✅ | 无第三方 DLL；`doctor --json` 报 `.NET Framework 4.0.30319.42000`；exe 仅 300 KB |
| 丰富的**文件扫描/哈希/查重/快照** | ✅ | `scan.*` 8 条 + `hash.*` 3 条，见 `docs/EVIDENCE-scanner.md`（46 个用例） |
| **进程与服务管理** | ✅ | `proc.*` 6 条 + `svc.*` 2 条 + `startup.list` |
| **系统与网络诊断** | ✅ | `sysinfo` `disk.*` `eventlog.*` `installed.list` `defender.status` `net.*` 6 条 |
| **签名与完整性校验** | ✅ | `sign.*` 4 条；`sign.verify` 与 Windows 自身判定一致（含目录签名） |
| **任务与日志管理** | ✅ | `job.*` 5 条 + `log.*` 4 条 + `run` |
| **stdio JSON-RPC CLI 通道**让 agent 连接、拉日志、查结果 | ✅ | `serve --stdio`，见 §5 |
| 交付**命令清单** | ✅ | `docs/COMMANDS.md`（由 `manifest --json` 自动生成，51 条 / 14 组） |
| 交付**文档** | ✅ | `README.md`、`docs/AGENT-GUIDE.md`、`docs/ARCHITECTURE.md`、`docs/CLI-CONTRACT.md`、`docs/COMMANDS.md`、`docs/FEATURES.md`、`docs/ROADMAP.md` |
| 经**实际命令验证** | ✅ | `verify.ps1` **36 PASS / 0 FAIL / 0 SKIP**，见 §2 |

## 2. 验收台结果（`verify.ps1`，可重复运行）

```
pwsh -File <repo-root>\verify.ps1
=> 已注册命令: 51 个
=> PASS=36  FAIL=0  SKIP=0
```

覆盖内容：

| 检查类别 | 数量 | 说明 |
|---|---|---|
| 命令抽查（信封 + 退出码 + stdout 纯净性） | 25 | 每条 `--json` 必须是**单行**合法信封，含 `ok/cmd/version` |
| 结论语义 §4.1 | 4 | `sign.verify`(已签名/目录签名)、`hash.compare`、`scan.verify`：结论为否 → `ok:true` + **exit 1** + `verdict=false` |
| 流式契约 §3 | 1 | `scan.find --jsonl`：**第一帧必须是 `meta`**（19 帧实测） |
| 用法错误 | 2+1 | 未知命令 → 2；缺必填参数 → 2；目标不存在 → **3** |
| 破坏性操作安全 | 2 | `--dry-run` 后进程仍存活（零副作用）；未加 `--yes` → exit 2 且**不杀** |
| serve 通道 | 1 | 6 帧全部合法 JSON，`initialize`/`-32601`/`shutdown` 正确，干净退出 |

## 3. 招牌能力：`sign.verify`（本项目的起点）

起因：DSH 桌面端自动升级失败，报
`Command failed: set "PSModulePath=" & chcp 65001 >NUL & powershell.exe ... Get-AuthenticodeSignature ...`。

**根因链**（已用 Node 逐字符复现 electron-updater 6.8.9 的调用）：
1. electron-updater 把 PowerShell 验签**硬编码 20 秒超时**（`preparePowerShellExec(cmd, 20 * 1000)`）；
2. 本机实测同一操作耗时：notepad.exe **3.3–4.3 s**，289 MB 安装包 **12.7 / 22.5 / 32.6 / 45.2 / 84.3 s**（波动极大）；
3. `execFile` 超时后 SIGTERM 掉 PowerShell，`error.message` 恰为 `Command failed: ...`，`stderr` 为空；
4. electron-updater 把它当成"签名无效"，升级中止。

**关键对照**：不发起 OCSP/CRL 网络请求时验签只需毫秒级；发起时可达 84 秒 —— 说明瓶颈是**证书链吊销检查的网络等待**，而非文件大小或哈希本身。

本方工具与 PowerShell 的对照（同一台机器、同一批文件）：

| 文件 | `dsh-toolbox sign.verify` | PowerShell `Get-AuthenticodeSignature` | 一致性 |
|---|---|---|---|
| `explorer.exe`（内嵌签名） | Valid，394–1300 ms | Valid | ✅ |
| `notepad.exe`（**目录签名**） | Valid，`signatureKind=catalog` | Valid | ✅ |
| `powershell.exe` | Valid | Valid | ✅ |

> 目录签名是这里最容易踩的坑：System32 下大量系统文件签名在 `CatRoot\*.cat` 而非 PE 内嵌，
> 只用 `X509Certificate.CreateFromSignedFile` 会判成 `NotSigned`（**假阴性**，会把正常系统文件误报为被篡改）。
> 该问题在集成抽检中被发现并修复（改用 WinVerifyTrust 语义 + 目录签名识别），修复前后判据如下。

## 4. 破坏性操作安全（本次目标明确要求）

| 行为 | 实测结果 |
|---|---|
| `proc.kill --id <真实PID> --dry-run` | exit 0，**目标进程仍存活**（零副作用） |
| `proc.kill --id <真实PID>`（无 `--yes`） | **exit 2**，目标进程**未被杀** |
| `proc.kill --name <不存在>` | **exit 3**（E_NOT_FOUND，而非用法错误） |
| `scan.snapshot --out <已存在文件>`（无 `--yes`） | exit 2 |
| 批量读失败（独占锁文件） | 记入 `data.failures` + `partial:true`，返回 **6**，不丢已完成部分 |

## 5. `serve --stdio` 通道（让 DSH 连接、看日志、查结果）

实测 4 帧往返（`initialize → ping → log.tail → shutdown`），全部单行合法 JSON，退出码干净：

```json
{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"dsh-toolbox","version":"0.1.0","protocol":"1"},...}}
{"jsonrpc":"2.0","id":2,"result":{"pong":true,"ts":"...","pid":4296,"uptimeMs":411}}
{"jsonrpc":"2.0","id":3,"result":{"file":"...toolbox-20261001.jsonl","lines":[...]}}
{"jsonrpc":"2.0","id":9,"result":{"ok":true,"bye":"..."}}
```

错误语义实测：未知方法 → `-32601`（并回报可用方法列表）；未 `initialize` 先调用 → `-32002`；
stdin 首行 UTF-8 BOM → 已要求剥离（Windows 客户端常见）。

## 6. 运行时留痕（agent 可回看）

```
%LOCALAPPDATA%\dsh-toolbox\
  logs\toolbox-YYYYMMDD.jsonl   结构化日志（每行一个 JSON）
  runs\runs.jsonl               每次调用：cmd/argv/exit/ms/cwd/pid
  runs\<runid>.out.json         大结果落盘
  jobs\<jobid>\                 cmd.json / stdout.log / stderr.log / status.json
```

## 7. 过程中发现并修复的真实缺陷

| # | 缺陷 | 发现方式 | 处置 |
|---|---|---|---|
| 1 | electron-updater 20 s 超时导致升级失败 | 用户报错 → Node 逐字符复现 | 定位根因；`sign.verify` 不设该超时并回报耗时 |
| 2 | `sign.verify` 把**目录签名**文件误判为 NotSigned | Lead 集成抽检（notepad.exe） | 修复，与系统判定一致 |
| 3 | `job.start` 对秒退命令误报 `E_SPAWN`，实际任务已完成 | Lead 复现 3/3 | 修复（改为启动握手），复测 3/3 `ok:true` |
| 4 | `--jsonl` 流式会累计 `_items` → 百万级结果 OOM | feature-scout 代码审查 | Core 修复：流式不驻留内存 |
| 5 | `Ctrl+C` 返回 5，契约承诺 130 | feature-scout 代码审查 | Core 修复：区分用户取消与超时 |
| 6 | `scan.find --jsonl` 的 `item` 出现在 `meta` 之前 | scanner-dev 报告 | Core 新增 `Out.BeginStream`，实测 meta 前置 |
| 7 | 布尔开关白名单漏项 / `--tail 40` 被误解析 | 使用中暴露 | Core 修复；`tail`/`wait` 移出布尔名单 |
| 8 | `scan.verify` 默认 1.5 s 容差 → 同尺寸改动漏判（假一致） | Lead 验收台 | 默认容差改为 0 |
| 9 | `defender.status` "假空"（读不到却显示为空） | Lead 抽检 | 修复：`readable`/`reason`/`exclusionsReadable` + 说明 |
| 10 | 并行开发时构建互相覆盖/源文件占用 | 多人同时构建 | `build.ps1` 加 `-Out`/`-Only`/构建互斥/CS1504 重试 |

## 8. 团队与收尾说明

| 成员 | 任务 | 结果 |
|---|---|---|
| scanner-dev | task-1 `scan.*`/`hash.*`（11 条） | 代码 + 46 用例证据（`docs/EVIDENCE-scanner.md`）已交付；`verdict`/`BeginStream` 等收尾由 Lead 完成 |
| sysdev | task-2 `proc/svc/disk/eventlog/installed/defender/startup/net/sign`（25 条） | 代码已交付（含目录签名修复、`defender.status` 可读性）；证据文件未及产出，由本记录覆盖 |
| agentio-dev | task-3 `job/log/run/serve`（10 条） | 代码已交付（含 serve 通道、job 竞态修复）；证据文件未及产出，由本记录覆盖 |
| feature-scout | task-4 规格与路线图 | 已完成（`FEATURES.md` 228 条 / `ROADMAP.md`） |

> 说明：收官阶段三个开发成员的会话先后失败中断，**未留下坏代码**（全量构建始终为绿）。
> 剩余收尾（`verdict` 语义、§4.1 退出码、`BeginStream`、`scan.verify` 容差、命令清单、本验收记录）
> 由 Lead 全部完成并复验。

## 9. 复现步骤

```powershell
# 1) 构建（本机已缓存 Roslyn 4.14 于 .tools\roslyn-4.14.0）
pwsh -File <repo-root>\build.ps1

# 2) 全量验收（36 项）
pwsh -File <repo-root>\verify.ps1

# 3) 命令清单（自动生成）
& dist\dsh-toolbox.exe manifest --json

# 4) 招牌命令
& dist\dsh-toolbox.exe sign.verify --path C:\Windows\System32\notepad.exe --json

# 5) 通道
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | & dist\dsh-toolbox.exe serve --stdio
```

## 10. 已知限制（诚实声明）

* `defender.status` 的**排除项列表**在非管理员下读不到（HKLM 受保护）：工具明确回报
  `exclusionsReadable=false` + 原因，**空列表 ≠ 没有排除项**。
* `scan.verify` 默认按 `size + mtime` 判定；若文件被改却**保留了原 mtime 且大小不变**，需加 `--full` 做哈希比对。
* `proc.port` / `net.ports` 对系统会话进程可能取不到映像名，已用标记字段区分"取不到"与"没有"。
* v0.1 不覆盖：批量改名/归档/编码转换/ACL 修复、json·csv·yaml 工具、Undo 事务日志、任务队列租约等 ——
  已在 `docs/FEATURES.md`（228 条规格）与 `docs/ROADMAP.md`（B0–B5 批次）中排期。
* 未做：把工具注册为 DSH 原生技能/插件（DSH 用 Cordis 插件体系，需单独立项），
  当前通过 CLI + `serve` 通道 + `docs/AGENT-GUIDE.md` 接入。
