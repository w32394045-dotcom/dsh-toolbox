# dsh-toolbox 路线图（ROADMAP）

> 作者：feature-scout（研究/规格角色）。配套文档：[FEATURES.md](FEATURES.md)（223 条命令条目 / 约 244 个子命令的完整 CLI 规格）。
> 契约基线：[ARCHITECTURE.md](ARCHITECTURE.md) v1 与 [CLI-CONTRACT.md](CLI-CONTRACT.md) v1（**已冻结，只能由 Lead 改**）。
> 本文回答四个问题：**现在到哪了**（§1–§2）、**接下来做什么、为什么、怎么验收**（§3–§4）、
> **明确不做什么**（§5）、**别人怎么做的、我们借什么**（§6）。

---

## 1. v0.1 现状盘点

### 1.1 已经存在且可用的部分（Lead 拥有）

| 组件 | 文件 | 状态 | 说明 |
|---|---|---|---|
| 构建 | `build.ps1` | ✅ 可用 | Roslyn 4.14 csc + .NET FW 4.8 引用集 + manifest 嵌入 + `-Test` 冒烟；产物 `dist\dsh-toolbox.exe`（59,904 字节） |
| 入口/解析/超时/日志 | `src\Program.cs` | ✅ 可用 | 全局选项、`--timeout`、运行记录 `runs.jsonl`、Ctrl+C、内建 `version`/`help` |
| 参数解析 | `src\Core\Cli.cs` | ✅ 可用 | `--k v`/`--k=v`/`-k`、可重复选项、时长/时间/大小解析、`BoolNames` 白名单 |
| 输出信封 | `src\Core\Runtime.cs` | ✅ 可用 | `Output.Result/Fail/Line/Row/Warn`、`--json`/`--jsonl`、人类表格、退出码常量、`ToolException`、`Paths`、`Registry`、`Ctx.ConfirmDestructive` |
| JSON | `src\Core\Json.cs` | ✅ 可用 | 自研保序序列化器 + `JavaScriptSerializer` 解析；`Json.Obj/Arr/Ordered/Parse` |
| 文件系统核心 | `src\Core\Fs.cs` | ✅ 可用 | `Glob`（`**`/`{a,b}`/`[...]`）、`FsScanOptions.FromArgs`（§7 词汇）、`Fs.Enumerate/Many`、`HashFile/QuickHash`（md5/sha1/sha256/sha384/sha512/crc32）、`ParallelMap`、`ParseSize/FormatSize`、`LongPath` |
| 核心命令 | `src\Commands\CoreCommands.cs` | ✅ 可用 | `doctor`、`sysinfo`、`env`（脱敏）、`manifest` |
| 数据目录 | `%LOCALAPPDATA%\dsh-toolbox\{logs,runs,jobs,cache}` | ✅ 可用 | `--home`/`DSH_TOOLBOX_HOME` 覆盖 |

### 1.2 v0.1 计划内模块的**实际状态**（截至本次侦察交付，2026-10-01 11:35 实测）

> 侦察期间三个实现成员并行落地了 v0.1 的全部计划命令。下表是**实测 `Registry.Add` 注册表**（共 51 条命令，含 4 条核心命令），
> 证据目录：`var\test\out\`（scanner 的 30 个用例输出）、`var\agentio-evidence.txt`、`dist\check-*.exe`。

| 模块 | 归属 | 计划清单 | 实测状态 |
|---|---|---|---|
| `ScanCommands.cs` | scanner-dev | `scan.find/size/tree/dup/snapshot/verify/recent/empty-dirs` | ✅ **8/8 已注册**，`var\test\out\` 有 30 个用例（含 `--jsonl`、`--dry-run`、错误码） |
| `HashCommands.cs` | scanner-dev | `hash.file/dir/compare` | ✅ 3/3 已注册（`--algo` 可重复、`--expected`、汇总哈希） |
| `ProcCommands.cs` | sysdev | `proc.list/tree/find/kill/wait/port` | ✅ 6/6 已注册 |
| `SysCommands.cs` | sysdev | `svc.list/control`、`disk.space`、`eventlog.query/export`、`installed.list`、`defender.status`、`startup.list`、`task.list` | ⚠️ 部分：已注册 `svc.list/control`、`disk.space/health`、`eventlog.query/list`、`installed.list`、`defender.status`（**含排除项**）、`startup.list`；**缺 `eventlog.export`、`task.*`** |
| `NetCommands.cs` | sysdev | `net.ports/tcp/http/dns/ip/download` | ✅ 6/6 已注册（`net.ip` 即规格里的"网卡与地址"） |
| `SignCommands.cs` | sysdev | `sign.verify/chain/hash/motw` | ✅ 4/4 已注册（含 `sign.hash` 对 electron-builder `latest.yml` 的支持；`sign.verify` 默认严格吊销检查、不设 20s 超时） |
| `JobCommands.cs` | agentio-dev | `job.start/list/status/output/kill` | ✅ 5/5 已注册（`--follow`/`--tail`/破坏性闸门） |
| `LogCommands.cs` | agentio-dev | `log.append/tail/search/runs`、`run` | ✅ 5/5 已注册 |
| `ServeCommand.cs` | agentio-dev | `serve --stdio`（契约 §8） | ⚠️ 已注册 `serve`；方法覆盖度需按契约 §8 逐项验证（见 §1.4 D5） |
| `Core\JobStore.cs` | agentio-dev | 任务/运行记录持久化 | ✅ 已实现（`jobs\<id>\{cmd.json,stdout.log,stderr.log,index.jsonl,status.json,helper.log}`，现场有运行产物） |

**结论**：v0.1 的"有没有命令"问题基本解决；剩下的差距集中在**契约兑现（参数 schema / 退出码 130 / jsonl 内存）、
写能力（文件/文本/数据/归档）、Agent 协作（队列/工件/上下文/能力自描述）、undo/策略、以及计划外的系统与网络深度**。

### 1.3 契约覆盖率（v0.1 承诺 vs 实测）

| 契约条款 | 承诺 | 实测 | 差距 |
|---|---|---|---|
| §3 输出信封 | `--json` 单对象 | ✅ | — |
| §3 `--jsonl` | meta/item/summary 三段 | ✅ 已有多命令用例 | ⚠️ `Output.EmitItem` 仍会把已发项留在 `_items`（内存增长，见 §2 / 附录 A#2） |
| §4 退出码 0/1/2/3/4/5/**130** | 130 = Ctrl+C | ❌ 仍不可达 | Ctrl+C 走 `OperationCanceledException` → 统一映射为 **5**（`Program.cs` L129-132） |
| §7 文件扫描词汇（19 项） | 全支持 | ⚠️ 部分 | `FsScanOptions.FromArgs` 仍**不解析** `--content-regex`/`--sort`/`--reverse`/`--hash`；scanner 已在 `scan find` 内部自实现部分语义 → 建议抽到公共 `Selector`（B1） |
| §8 `serve` 12 方法 | — | ⚠️ 待逐项验证 | 已注册命令，但 `initialize/ping/commands.list/commands.schema/call/log.tail/log.search/job.*/artifact.read/shutdown` 需逐项证据（B2 验收项） |
| §9 破坏性闸门 | `--dry-run`/`--yes` | ✅ 已有实现（`proc.kill`/`svc.control`/`job.kill` 等） | ⚠️ **仍无 undo/`txid`**：回滚只能靠回收站与系统备份（B0 + B4） |
| §6 数据目录 | `logs/runs/jobs/cache` | ✅ | 工件/队列/上下文/undo 目录**尚无位置**（B0） |
| §2.1 语言白名单 | 禁 `System.Text.Json` 等 | ✅ | `dynamic`（COM 常用）仍缺 `Microsoft.CSharp.dll` 引用 → 需 Lead 决定（附录 A#7） |
| `docs\EVIDENCE.md` | ARCHITECTURE §7 要求 | ⚠️ 分散 | 证据目前散落在 `var\test\out\`、`var\agentio-evidence.txt`、`docs\AGENT-GUIDE.md`；建议统一到 `docs\EVIDENCE.md`（每批一节） |

### 1.4 实现与规格的已知分歧（**请 Lead 裁决**，不是因为实现错，而是需要统一对外契约）

| # | 分歧点 | 现状（实现） | 规格建议 | 建议裁决 |
|---|---|---|---|---|
| D1 | `sign.verify` 默认吊销策略 | 默认**严格吊销检查**，不设 20s 超时 | 默认 `--revocation none`，`online` 显式开启 | 先实测本机 `elapsedMs`：若严格模式稳定 <10s 就保留现状（更安全）；若 >20s，则默认改 `cache-only`，否则"替代 electron-updater 超时"的目标会打折 |
| D2 | MOTW 入口重复 | 已有 `sign.motw`（查看/移除） | 规格另有 `scan.zone`（清点）+ `file.unblock`（批量解除） | **以 `sign.motw` 为唯一入口**，扩展 `--path`（批量）+ `--only-zone`；FEATURES 的 `file.unblock`/`scan.zone` 作为别名候选，不新增重复实现 |
| D3 | Defender 排除项 | `defender.status` 已含排除项（只读） | 规格另有 `defender.exclusions`（add/remove，需提权 + `revertCommand`） | 保留只读写侧在 `status`；写侧独立为 `defender.exclusions`（因为它需要 `--yes` + 管理员 + 撤销命令，混进 `status` 会破坏只读语义） |
| D4 | 校验和清单（`sha256sum` 兼容） | `hash.dir` 只给清单+汇总哈希 | 规格有 `hash.manifest`（GNU 逐字节兼容）+ `hash.check`（`-c` 语义） | **B1 必补**：这是"交付物可被任意机器验收"的关键，且是 `sign.hash` 的通用化版本 |
| D5 | 网卡命令命名 | `net.ip`（与契约 §7 的旧计划一致） | 规格用 `net.adapters` | **以 `net.ip` 为准**（尊重既有实现与 v0.1 契约），规格侧不再引入 `net.adapters`；如需别名由 Lead 在 `Registry.Add(aliases:)` 登记 |
| D6 | 计划外增益命令 | `eventlog.list`、`disk.health`、`sign.hash` 为计划外新增 | 规格未列（`disk.health` 在 §9.7、`eventlog.export` 在 §9.10） | 认可并纳入契约：`eventlog.list` 补进 FEATURES §9（本次已按现状记录）；`hash.*` 的 `check/manifest/algo` 仍待补 |
| D7 | 缺 `eventlog.export`、`task.*`（计划内但未注册） | 未实现 | 规格 §9.10/§9.15–9.18 | 归入 B3 剩余项，与 `driver.list`/`patch.list`/`perf.*`/`registry.*` 一起做 |


---

## 2. 差距分析（按"agent 的手和脚"这个目标拆）

| # | 缺口 | 影响（agent 会怎样受伤） | 对应批次 |
|---|---|---|---|
| G1 | **扫描能过滤但不够"取证"**：`scan find` 已支持 glob/正则/大小/时间/内容匹配，但没有**行级上下文检索**（`-A/-B/-C`）、确定性排序（`--sort/--stable`）、忽略文件（`.gitignore`）、类型/编码/熵/占用者等深度视角 | agent 拿不到"精确到行的证据"，仍要自己 grep；大规模重复查询无缓存 | B1 |
| G2 | **没有参数 schema**（`Registry.Add` 只有 usage 字符串） | agent 无法自配置：`capabilities`、`serve.commands.schema`、MCP 桥全部受阻；靠猜参数 | B0 |
| G3 | **没有 undo / txid** | 任何写操作都是"单向门"（现有实现只能靠回收站），agent 不敢动手，人也不敢放手 | B0 + B4 |
| G4 | **没有任务队列与工件存储** | 多 agent 无法并行；大结果只能进上下文（烧 token）或落临时文件（无序） | B2 |
| G5 | **没有执行策略** | `run`/`job.start` 已存在但无白名单、无输出上限、无 Job Object 孤儿子进程防护 | B0 + B2 |
| G6 | **`serve` 方法覆盖度未验证** | 长连接的"能连"有了，"能持续读日志/结果、能取消、能推进度"需按契约 §8 + §14.21 逐项验收 | B2 |
| G7 | **系统面部分覆盖**：已有 `svc.*`/`disk.*`/`eventlog.query,list`/`installed.list`/`startup.list`/`proc.*` | 仍缺 `task.*`（计划任务）、`eventlog.export`、`driver.list`、`patch.list`、`perf.*`、`power.*`、`registry.*`、`temp.clean` | B3 |
| G8 | **安全面部分覆盖**：已有 `sign.verify/chain/hash/motw`、`defender.status` | 仍缺 `acl.*`、`defender.exclusions`（写侧 + 撤销命令）、MOTW 批量、PE 分析、证书清点 | B3 + C2 |
| G9 | **网络面已覆盖基础**：`net.ports/tcp/http/dns/ip/download` | 仍缺 `net.tls`（协议/套件）、`net.proxy`、`net.hosts`、`net.firewall`、`net.shares`、`net.ping/trace`、`net.whois`、批量探测的并发与超时治理 | B5 |
| G10 | **架构性缺陷 15 项**（Ctrl+C 退出码、jsonl 内存、`Fs.Enumerate` 无取消、`BoolNames` 缺 `show-secrets`/`fast`、无 STA、`BuildRefs` 重复、COM `dynamic` 缺引用…） | 后面的命令会不断踩同一批坑，且契约承诺无法兑现 | B0 |
| G11 | **无 `caps`/`SideEffects` 元数据** | 无法自动判断"这条命令能不能跑"（只读/破坏性/需提权/联网），无法生成安全审计报告 | B0 |
| G12 | **证据链未统一** | 证据散在 `var\test\out\`、`var\agentio-evidence.txt`、`docs\AGENT-GUIDE.md`，ARCHITECTURE §7 要求的 `docs\EVIDENCE.md` 不存在 | 每批都要产出 |

---

## 3. v0.2 分批计划（地基 + 核心能力）

> 分批原则：**先修地基（B0）→ 再做用户点名的核心（B1）→ 再做 agent 协作（B2）→ 再做系统与安全（B3）→ 再做写能力（B4）→ 最后网络（B5）**。
> 每批都要求：命令能用 `--json` 跑通、出现在 `manifest --json`、有 `docs\EVIDENCE.md` 记录、破坏性命令有 `--dry-run` 零副作用证据。

### B0. 契约与地基修复（Lead；**必须最先做，其他批次全部依赖**）

**为什么是这些**：B0 的每一项都是"多个命令的公共前置"。如果不先做，B1–B5 的实现者会各自绕过（自己写锁、自己拼参数、自己抛错），
那就回到 PRD 想避免的"各写一套"。具体清单见 [FEATURES.md 附录 A](FEATURES.md#附录-a需要-lead-改动的契约点)，核心是：

1. `ParamSpec` + `CommandInfo.SideEffects`（自动生成 `BoolNames`、`help --json` 参数、`capabilities`、`commands.schema`）；
2. `ExitCodes.Cancelled(130)` 可达（区分用户取消与超时）；
3. `Output.Streaming`（jsonl 不累计 `_items`）；
4. `Fs.Enumerate(root, o, CancellationToken)`；
5. `Paths` 新增 `Queues/Artifacts/Context/Undo/Idem/Snapshots/Checkpoints/Captures` + `doctor` 回报；
6. `Core\LockFile.cs`、`Core\UndoJournal.cs`、`Core\SafetyGuard.cs`、`Core\SecretRedactor.cs`、`Core\Sta.cs`、`Core\BuildRefs.cs`；
7. `Paths`/`runs.jsonl` 字段扩展（`txid/policyApplied/artifactIds/owner/session/degraded`）；
8. 契约文档更新（`CLI-CONTRACT.md` §2/§5/§6/§8/§9 + 错误码表；`ToolInfo.Version=0.2.0`、`Protocol=2`）。

**验收方式**：
```powershell
build.ps1 -Test                                    # 零错误
dist\dsh-toolbox.exe manifest --json               # 每个命令含 params/sideEffects
dist\dsh-toolbox.exe help scan.find --json         # 返回结构化参数
# 130 可达性：起一个 scan.watch（或长任务）后 Ctrl+C / taskkill 前的最终 exit
# LockFile：并行跑 20 个 task.add，断言无重复 id
```
* 断言脚本建议落在 `docs\EVIDENCE.md`（命令 + 退出码 + 关键字段 `jq` 断言）。

### B1. 文件与扫描的深度（scanner-dev；用户点名的**核心**）

**为什么是这些**：用户明确"灵活的文件扫描"是核心；agent 的第一步永远是"找到东西"。
本批把"选择器"做成一套共享词汇（§1.0 的 S0–S9），之后所有批量命令复用，避免每个命令长出自己的过滤选项。

包含（[FEATURES.md §1/§3](FEATURES.md)）：**已落地**的 `scan.find/size/tree/dup/snapshot/verify/recent/empty-dirs`、`hash.file/dir/compare`；
**本批要补的**：`scan.count`、`scan.content`（行级上下文检索）、`scan.types/age/names/long-path/diff`、
`hash.manifest/check/algo`（`sha256sum` 兼容）、
`Selector`（把 `--from-file/--stdin/--ignore-file/--gitignore/--smart-case/--stable/--print0/--flush/--cross-volume/--max-bytes/--max-total-bytes` 抽成公共选择器，
并把 `scan find` 内部已有的内容匹配/排序语义收敛过来）、`Core\Grep.cs`（分块 + 上下文 + 编码探测 + 正则超时）、`Core\EncodingDetect.cs`。
（`scan.locked`、`scan.entropy`、`scan.ads`、`scan.hardlinks`、`scan.perm/owner` 因需要 P/Invoke/启发式，放 v0.3 §4 C2。）

**验收方式**：
```powershell
# 1) 基本过滤 + 漏斗计数
scan find --path fixture --include "**/*.log" --exclude-dir node_modules --json
#   断言 .data.matched==3、.ok==true、.data.denied 存在
# 2) 内容检索 + 上下文 + 流式
scan content --path fixture --pattern "ERROR" -C 1 --jsonl
#   断言首行 type==meta、末行 type==summary、每 match 行含 path/line/contextAfter
# 3) 排序确定性 + 截断
scan find --path fixture --sort size --reverse --max-results 5 --json
#   断言 .truncated==true、.data.truncatedAt=="max-results"、items 按 size 降序
# 4) 去重（三段式，构造 2 组重复）
scan dup --path fixture --json          # 断言 .data.groups==2、.data.wastedBytes 精确
# 5) 变更检测闭环
scan snapshot --path fixture --hash --out snapA.jsonl
scan diff --base snapA.jsonl --live fixture   # 无改动 → data.added/removed/modified 全空
scan verify --path fixture --snapshot snapA.jsonl   # 断言 exit 0
# 6) 性能与取消
#   10 万文件（脚本生成）：scan count --parallel 8 --timeout 30s  → 记录 elapsedMs
#   长扫描中触发 --timeout：断言 exit 5 且进程在 2s 内退出
# 7) 确定性：同一命令跑两次，--stable 下 items 顺序完全一致（哈希对比）
```
**风险**：`--sort`/`--stable` 需要缓冲，与 `--jsonl` 流式冲突 → 规则：`--sort` 时先缓冲再流式（`data.sorted:true`），并在文档写明。

### B2. Agent 长连接、可观测、可恢复（agentio-dev；用户点名的**重点**）

**为什么是这些**：用户要"让 agent 长期连接、可观测、可恢复"。这三件事对应三块基础设施：
**队列**（可协作）、**工件+上下文**（可观测/不烧上下文）、**undo+幂等+策略**（可恢复/可放手）。
它们必须是 v0.2 的一部分，而不是"以后再说"——否则后续所有破坏性命令都没有安全网。

包含（[FEATURES.md §14/§15](FEATURES.md)）：**全新**：`task.add/pull/done/fail/list/get/heartbeat/requeue/reap/compact/watch`、
`artifact.put/get/list/rm/prune`、`context.get/set/list/delete`、`capabilities`、`report.gen`、`audit.runs`、
`undo.list/apply/prune`、`policy.show/check/set`；
**已落地但要硬化/扩展**：`run`（Job Object 防孤儿、`--idem-key`、`--max-output` + 自动 artifact、exec policy 接入）、
`job.*`（`progress`/`index.jsonl` 事件、`job.wait`、`--follow` 与 serve 推送对接）、`log.*`、`serve --stdio`（契约 §8 逐项验收 + §14.21 扩展）。

**验收方式**：
```powershell
# 1) 队列原子性（并发压测）
1..50 | ForEach-Object -Parallel { dsh-toolbox task.add --queue q --payload "{\"n\":$_}" --json }
#   断言：50 个 id 唯一；task.list --counts-only → pending==50
1..8 | ForEach-Object -Parallel { dsh-toolbox task.pull --queue q --owner "w$_" --lease 2s --json }
#   断言：8 个返回的 id 互不相同（无双重领取）
# 2) 租约回收
Start-Sleep 3; dsh-toolbox task.reap --queue q --yes --json   # 断言 requeued>0
# 3) 工件分页
artifact.put --from-file big.log --json                        # 记下 id
artifact.get <id> --offset 0 --limit 1024 --json               # 断言 totalBytes 与 nextOffset 正确、eof 逻辑正确
artifact.get <id> --grep "ERROR" --max-matches 5 --json        # 断言 matches 恰好 ≤5 且含行号
# 4) run 的刹车
run cmd /c "exit 3" --json                                     # 断言 exit==1、data.exitCode==3
run --prog format --args C: --json                             # 断言被 policy 拒绝 → exit 1（E_DENIED 类）
run node -e "setInterval(()=>{},1000)" --timeout 2s --json      # 断言 exit 5，且 tasklist 里没有残留 node.exe（job object 生效）
run git status --idem-key k1 --json; run git status --idem-key k1 --json  # 断言第二次 replayed==true
# 5) 自我描述
capabilities --format mcp --json | jq '.data.tools|length'      # 断言 == 命令数，且含 annotations.readOnlyHint
# 6) 可恢复
file.rm --path fixture\tmp --yes --json                         # 记下 data.txid
undo apply --txid <txid> --yes --json                           # 断言文件全部回来（hash 与删除前一致）
# 7) 长连接
'{"jsonrpc":"2.0","id":1,"method":"initialize"}' | serve --stdio   # 断言单行合法 JSON、含 commands/home
#   以及 call/log.tail follow/task.pull/artifact.get 四个方法各一条真实帧
# 8) 报告
report.gen --since 1h --out report.md --json                     # 断言 markdown 含"失败项""工件清单"两节
```
**约束**：`serve` 的方法表扩展属**协议变更**，必须由 Lead 先改 `CLI-CONTRACT.md` 并广播（B0 的一部分）。

### B3. 系统、安全与清理（sysdev）

**为什么是这些**：这是"Windows 上排障/取证"最常用的一组：服务、事件日志、启动项、计划任务、驱动、补丁、性能、签名、ACL、Defender、临时清理。
其中 `sign.verify` 是 v0.1 记录在案的**真实痛点**（electron-updater 20s 硬超时 vs 实测 32–45s）。

包含（[FEATURES.md §8–§11、§9.29](FEATURES.md)）：**已落地**：`proc.list/tree/find/kill/wait/port`、`svc.list/control`、`disk.space/health`、
`eventlog.query/list`、`installed.list`、`defender.status`、`startup.list`、`sign.verify/chain/hash/motw`；
**本批要补**：`proc.modules/threads/snapshot/diff`、`svc.get`、`disk.volumes`、`eventlog.tail/export`、`installed.find`、
`task.list/get/run`、`driver.list`、`patch.list`、`perf.sample/top`、`power.battery/thermal`、`registry.get/list`、
`sign.verify-dir/timestamp`、`defender.exclusions`（写侧 + `revertCommand`）、`scan.locked`（Restart Manager）、
`temp.clean`（白名单制）、`file.rm/file.copy/file.move`（依赖 B0 的 undo）。

**验收方式**：
```powershell
# 1) 签名（核心痛点）：不设 20s 限制，回报真实耗时
sign.verify "C:\...\ChatGPT.exe" --json     # 断言 elapsedMs 可 >30000（不超时）、status 字段正确
sign.verify-dir --path C:\Windows\System32 --ext dll --only-unsigned --json --parallel 4
# 2) 事件日志：XPath 下推（Security 日志 100 万条也必须在秒级返回）
eventlog.query --log System --level error --since 2h --max 50 --json  # 断言 count<=50、ts 降序
# 3) 服务控制闸门
svc.control wuauserv --action stop --json    # 断言 exit 2（缺 --yes）+ E_NEEDS_CONFIRM
svc.control wuauserv --action stop --dry-run --json   # 断言 exit 0、applied=false、服务仍在 Running
# 4) 性能采样不崩（含计数器损坏降级）
perf.sample --duration 3s --interval 1s --json  # 断言 sampleCount==3，或 degraded==true 且 warnings 非空
# 5) 临时清理安全（最高风险命令）
temp.clean --profile safe --dry-run --json   # 断言 applied=false、plan 中每条都在允许根之下
#   在 %TEMP% 放一个 junction 指向 D:\protected，断言它被记为 outside-root 且未被删除
# 6) proc.kill 自我保护
proc.kill --pid <自己 exe 的 pid> --yes --json   # 断言 exit 2 + 明确拒绝
# 7) ACL 往返
acl.get <dir> --format sddl --json ; acl.set <dir> --grant "Everyone:read" --yes --json ; undo apply --txid <id> --yes
```
**优先级内序**：`sign.verify` → `eventlog.query` → `proc.*` → `svc.*` → `installed/startup/task` → `perf/power` → `temp.clean`（最后，因为它最危险）。

### B4. 写能力：文件、文本、数据（scanner-dev + 一个新的写侧成员，如 `filesdev`）

**为什么是这些**：读能力有了之后，agent 需要**安全地写**：批量改名/复制/移动/删除、编码转换、正则替换、JSON 改值。
全部依赖 B0 的 `UndoJournal` + `SafetyGuard`，因此必须排在 B0 之后（与 B2/B3 可并行，因为它们之间无共享文件）。

包含（[FEATURES.md §2/§4/§5/§6](FEATURES.md)）：`file.rename/copy/move/rm/mkdir/touch/attr/read/write/replace/truncate/split/join/compare/ads-rm/shorten/link`、
`dir.sync/compare/merge`、`archive.list/extract/create/test/add/remove/convert`、
`text.enc.detect/enc.convert/eol/stats/head/tail/slice/replace/dedupe/sort/uniq/normalize/diff/template/find-lines/join`、
`data.json.get/validate/fmt/query/set/merge/diff/jsonl.stats`。

**验收方式**：
```powershell
# 1) dry-run 零副作用（ARCHITECTURE §7 的硬要求）：用 mtime+hash 前后对比证明
#   记录 fixture 全部文件的 (mtime, sha256) → 跑每条破坏性命令 --dry-run → 断言完全一致
# 2) 两阶段改名（含 a↔b 互换）
file.rename --path fixture --pattern "^(.+)\.txt$" --to "$1-renamed.txt" --dry-run --json   # plan 正确
file.rename ... --yes --json ; undo apply --txid <id> --yes --json                          # 名字全回来
# 3) GBK→UTF-8 幂等
text.enc.convert --path fixture --to utf-8 --yes --json   # converted==N
text.enc.convert --path fixture --to utf-8 --yes --json   # 断言 skipped==N（--skip-if-same）
# 4) 归档安全
#   构造 zip-slip 样本（..\..\evil.txt）与 zip bomb（10KB→1GB）
archive.extract evil.zip --dest out --yes --json   # 断言 skippedEntries 含 reason=="zip-slip"，out 外无文件
archive.extract bomb.zip --dest out --yes --json   # 断言触发 E_SAFETY/ratio 中止
# 5) tar/tgz 往返（自写 USTAR）
archive.create --path fixture --out a.tgz --format tgz --yes --json
archive.test a.tgz --json ; archive.list a.tgz --json ; archive.extract a.tgz --dest back --yes --json
#   断言 back 与 fixture 的哈希清单完全一致（含 mtime）
# 6) 部分失败 → exit 6
#   把 fixture 中一个文件设为只读并加独占句柄 → file.rm --yes 应返回 6 且 data.failures 非空
# 7) JSON 写往返
data.json.set fixture\a.json --path ok --value false --yes --json   # before/after 正确；undo 可回滚
```

### B5. 网络探测（sysdev）

**为什么是这些**：agent 需要"确认服务活着、证书没过期、代理没配错、端口没被占"。
这些都是**只读探测**（唯一写操作 `net.download`/`net.hosts` 有闸门），风险低、价值高。

包含（[FEATURES.md §10](FEATURES.md)）：**已落地**：`net.ports/tcp/http/dns/ip/download`；
**本批要补**：`net.connections/ping/trace/tls/proxy/hosts/firewall/shares/whois/wol`（`net.forward` 归 C2）。
其中 `net.tls`（协议/套件/证书到期）、`net.proxy`（WinINET/WinHTTP/环境变量三处不一致——最常见的"能上网但程序不能"根因）、
`net.ping/trace`（延迟与路径）优先级最高。

**验收方式**：
```powershell
net.http https://example.com https://expired.badssl.com --cert-chain --json
#   断言：example.com 链有效、daysToExpiry 有值；badssl 项 ok=false + chainErrors 非空
net.http https://httpbin.org/status/500 --expect-status 200 --json   # 断言 exit 6 + failures
net.ports --port 3000 --json        # 起一个本地监听后断言 pid 正确；无监听时断言 exit 3
net.dns example.com --type TXT,MX --json    # 断言 rcode/src 正确（自写 DNS 客户端）
net.tcp 127.0.0.1:1 --timeout 1s --json     # 断言 open=false 且 exit 1（--expect-open 语义）
net.download <file url> --out tmp.bin --sha256 <expect> --yes --json  # 断言 verified==true；错 hash → exit 1 且无残留文件
```
**FU 约束**：`ServicePointManager.DefaultConnectionLimit`（FW 默认 2）必须按 `--parallel` 设置，否则并发探测退化成串行（验收里要有耗时对比）。

---

## 4. v0.3 分批计划（广度、深度与治理）

### C1. 数据、文档与桌面（给 agent 眼睛和手）

**为什么是这些**：用户/上游给 agent 的输入大量是 **xlsx/docx/csv/图片**，而"看屏幕/剪贴板/窗口"是 GUI 自动化的最小闭环。
它们全部可以在**零第三方依赖**下实现（zip + XML + System.Drawing）。
包含：`data.csv.info/query/to-json`、`data.json.to-csv/to-jsonl/jsonl.to-json/schema`、`data.yaml.get`（子集）、
`data.xml.xpath/validate`、`data.ini.get/set`、`data.env-file`、`codec.*`（base64/hex/url/html/jwt/guid/time/binary/escape）、
`office.xlsx.read/write`、`office.docx.text`、`office.pptx.text`、`pdf.text`（FlateDecode 子集）、
`image.info/convert/resize/capture/compare`、`window.list/control`、`clipboard.get/set`、
`git.status/diff/log/repo`、`dev.build/dev.compile/dev.run-cs/dev.ps`、`path.resolve/path.info`。
**验收方式**：每种文件格式用真实样本（含中文路径、含 DPI 150% 的截图、含 xlsx 日期/公式/合并单元格）跑一遍并核对字段；
`image.capture` 必须在 125%/150% 缩放下坐标正确（对比全屏截图尺寸）；`codec.jwt` 用已签发的 HS256/RS256 样本校验。

### C2. 安全深化与受控系统写（需要 `--i-know` 的高危面）

**为什么放在 v0.3**：这些命令要么需要管理员、要么会持久化改变系统状态，必须在 B0 的 `SafetyGuard`/`UndoJournal` 成熟之后再做。
包含：`scan.encoding/entropy/perm/owner/hardlinks/ads/zone`、`pe.info/imports/strings`、`cert.list/cert.info`、
`acl.get/set/copy/reset`、`defender.status/exclusions/scan/threats`、`file.reputation`（仅本地信号）、
`startup.disable`、`task.create/delete`、`svc.config`、`registry.set`、`env.patch`、`policy.set`、
`net.forward`、`scan.watch`、`proc.snapshot/diff/watch`、`proc.handles`（可选）、`file.link`。
**验收方式**：每条写命令都必须有"`--dry-run` 零副作用 + 备份文件存在 + `undo apply` 可还原"三件套证据；
Defender 排除项必须回报 `revertCommand` 且该命令真的能撤销（端到端跑一次）。

### C3. 规模、恢复与生态（收口）

**为什么最后**：这些是"锦上添花但价值高"的能力，且都依赖前面所有批次。
包含：`checkpoint.save/restore`、`audit.timeline`、`report.gen` 模板体系、`scan.index`（大规模加速）、
`serve` 的进度事件与 `$/cancelRequest`、`mcp --stdio` 桥、`undo.prune` 策略、配额与 `doctor` 扩容检查、
`dev.run-cs` 沙箱化（Job Object 内存/CPU 限额）、`--progress` 全命令覆盖。
**验收方式**：`mcp --stdio` 用官方 MCP Inspector 或一个最小客户端跑通 `initialize/tools.list/tools.call`；
`checkpoint.save` → 改 10 个文件 → `checkpoint.restore` → 断言目录 hash 回到检查点状态。

---
## 5. 明确不做清单（附理由）

> 这份清单和功能清单一样重要：**范围失控是这类工具最常见的死法**。每条都给出理由与"替代方案"。
> 如果将来要做，必须先有一条任务说明"为什么现在做、新增什么依赖、如何可回滚"。

### 5.1 平台与形态

| 不做 | 理由 | 替代 |
|---|---|---|
| GUI / 托盘 / 安装器 / 自动更新 | 使用者是 agent；GUI 会带来大量不可测代码与 UI 依赖 | 单文件 exe + JSON |
| 常驻 Windows 服务 / 后台守护进程 | 需要安装、提权、生命周期管理；与"一次调用一个结果"的模型冲突 | `serve --stdio`（由父进程管理生命周期）+ `job.*` |
| 多语言 UI / 本地化 | 使用者是程序；字段名稳定比界面翻译重要 | 中文人类输出 + 英文 JSON 字段 |
| 插件系统 / 动态加载第三方 DLL | 破坏"零依赖 + 可审计"；供应链风险 | 新能力走 `dev.compile`（自己编译，可审计） |
| TUI / 全屏交互 / 进度条动画 | 管道与 JSON 场景下无意义 | `--progress` 事件 + `--jsonl` |

### 5.2 需要内核/驱动/高权限的危险面

| 不做 | 理由 | 替代 |
|---|---|---|
| 内核驱动、ETW 深度采集、内核回调（Sysmon 类能力） | 需要签名驱动与安装提权，违背"仅系统自带" | WMI + 事件日志 + `proc.*` 用户态采样 |
| 卷句柄 / MFT / USN Journal 直读（Everything 路线） | 需管理员 + 卷独占 + 结构随版本变化 | `scan.index`（JSONL 索引 + 二分） |
| 全量句柄枚举作为默认能力（`handle.exe` 路线） | 需 `NtQuerySystemInformation` + 提权 + 结构脆弱 | `scan.locked`（Restart Manager，免提权）；`proc.handles` 仅 v0.3 可选且带限额 |
| 内存 dump / 进程注入 / 调试器 / API Hook | 需 SeDebugPrivilege、与杀软冲突、可被用于恶意目的 | 事件日志 + `proc.modules` + `pe.*` |
| 磁盘分区/格式化/TRIM/碎片整理/坏道修复 | 不可回滚、破坏性极强、与 DSH 目标无关 | `disk.space/volumes/health`（只读） |
| 电源计划修改 / 关机重启休眠 / 锁屏 | 影响用户会话且易被滥用 | `power.battery/thermal/plan`（只读） |
| 用户账户与凭据操作（`net user`、凭据管理器、LSASS、证书私钥导出） | 安全与法律红线；私钥外泄不可挽回 | `cert.list/cert.info`（只读） |
| 关闭/削弱安全防护（实时保护、ASR、云保护、防火墙规则增删改） | 直接削弱机器安全；属于"agent 不该有的能力" | `defender.status`（读）+ `defender.exclusions`（受控加白，必须给 `revertCommand`） |
| 注册表批量导入/任意 `.reg`、删除整棵子树 | 一条命令就能毁掉系统；不可审计 | `registry.set`（单键 + 备份 + `undo`）；`registry.get/list`（读） |
| 远程横向执行（PsExec/WinRM/SSH 到其他机器） | 工具会变成横向移动/攻击工具，超出"本机手脚" | 明确要求用户用专门的运维通道 |
| 端口扫描器（批量扫远端端口段） | 攻击工具语义；合规风险 | `net.tcp`（单点连通性）/`net.ports`（本机） |

### 5.3 需要第三方库/超范围的功能

| 不做 | 理由 | 替代 |
|---|---|---|
| 加密 ZIP（AES/ZipCrypto）读写、7z/rar 解码 | .NET FW 的 `ZipArchive` 不支持；解码需第三方（违反零依赖） | 明文 zip/tar/tgz；加密包请用系统/7-Zip 手工处理 |
| 完整 YAML 1.2（锚点/别名/标签/多文档） | 无 BCL 支持，自研完整解析器 = 数千行 + 无穷边界 | `data.yaml.get` 的**文档化子集**，其余返回 `E_UNSUPPORTED` + 行号 |
| 完整 jq 语言（管道/函数/递归下降） | 实现成本极高、语义面巨大 | `data.json.get`（路径/JSON Pointer）+ `data.json.query`（受限谓词 DSL） |
| 自研正则引擎 / PCRE 完全兼容 | 无必要；.NET Regex 足够且支持超时 | `Regex` + 每次匹配 `TimeSpan` 超时（ReDoS 防护） |
| OCR、语音、视频转码、媒体播放 | 需 WinRT/第三方，收益低 | `office.*`/`pdf.text` 尽力而为；扫描件 OCR 请外部工具 |
| 爬虫 / HTML DOM / CSS 选择器 | 需要完整 HTML 解析器，且容易越界成"网页自动化" | `net.http`（原始内容）+ `data.xml.xpath`（严格 XML） |
| PDF 完整渲染/加密 PDF/PDF 编辑 | 与 PDF 规范复杂度不成比例 | `pdf.text` 仅 FlateDecode + ToUnicode 子集，失败要诚实报告 |
| `.doc`/`.xls`（OLE2/BIFF）与加密 Office 文件 | 需 OLE2 + RC4/AES 解析，投入产出比低 | 提示先转存为 `.docx`/`.xlsx` |
| 二进制补丁 / PE 重写 / 加壳脱壳 / 签名伪造 | 复杂且可被直接用于恶意目的 | `pe.info/imports/strings`（只读分析） |
| SQLite / 数据库引擎 | 需 native `sqlite3.dll`（第三方） | JSONL + `data.*`；需要 SQL 请外部工具 |
| 云备份 / rsync 协议 / 增量块去重传输 | 网络协议 + 服务端，超出单机工具职责 | `dir.sync`（本地镜像）+ `archive.create` |
| 包管理器语义（`installed.install` 包装 winget/choco/npm） | 供应链与责任风险 | `run winget ...`（显式、可审计、受 policy 约束） |

### 5.4 我们**已有能力但刻意收窄**的

| 收窄项 | 保持什么 | 禁止什么 | 理由 |
|---|---|---|---|
| 删除 | `file.rm`（默认回收站）、`temp.clean`（白名单） | 任何"自动清理/优化"式的一键命令；`scan.dup --delete` | 删除是唯一不可逆的常见操作；宁可两步 |
| 执行 | `run`、`job.start`、`dev.compile` | 隐式执行（例如某命令内部偷偷调 PowerShell 完成本职） | 可审计性；策略要能拦到所有执行点 |
| 网络 | 只读探测 + `net.download` | 上传任意内容、发送邮件、webhook 回调 | 数据外泄面 |
| 提权 | 检测并提示（`elevated`、`--elevate` 走 UAC） | 静默自我提权 / 绕过 UAC / 存储管理员凭据 | 安全与信任 |

---

## 6. 与同类工具的对比与借鉴

### 6.1 对比总表

| 参照物 | 借什么 | 不借什么 | 对应实现 |
|---|---|---|---|
| **Sysinternals**（`sigcheck`/`handle`/`autoruns`/`accesschk`/`TCPView`/PsExec） | 命令切分方式（"一件事一条命令"）、签名校验不设人为超时、启动项的多来源清点思路（Run/RunOnce/服务/任务/登录脚本/WMI）、ACL 的 `-a` 语义 | 内核驱动（Sysmon/Procmon）、PsExec 式远程执行、GUI 依赖 | `sign.verify`、`scan.locked`（Restart Manager 而非 handle.exe）、`startup.list`、`acl.get`、`net.ports` |
| **ripgrep**（`rg`） | **`--json` 事件流形态**（`begin`/`match`/`end`/`summary`）、上下文 `-A/-B/-C` 语义、`--smart-case`、`--max-columns`、`--files-with-matches`/`--count`、`--type` 预设、`.gitignore` 感知、退出码"无命中=1"（`--exit-code` 时） | Rust 正则引擎与其语法差异、并行目录遍历的激进并发（我们默认更保守）、`--pcre2` | `scan.content`、`scan.find`、`Selector`（S0–S9）、[rg 手册](http://man.m.sourcentral.org/ubuntu2404/1+rg) |
| **fd** | `--changed-within`/`--changed-before`、`--type f/d/l`、`--max-depth`、`--exclude`、`--print0`、`--exec`（我们不做 `--exec`，见下） | `--exec`/`--exec-batch`（在我们这里等于"扫描命令偷偷执行"，违反 §5.4）、`--absolute-path` 默认（契约已定绝对路径默认，`--relative` 可切） | `scan.find/recent/age`、`--print0`、`--newer/--older` |
| **Everything** | "索引换速度"的产品直觉、`--max-age` 式的陈旧判定 | **USN/MFT 直读索引**（需管理员 + 卷句柄 + 版本脆弱，见 §5.2）、常驻索引服务 | `scan.index`（JSONL + 二分 + 陈旧回退实时扫描）、[USN journal 讨论](https://voidtools.com/forum/viewtopic.php?f=5&p=48860&t=11919) |
| **7-Zip** | `l -slt` 式**机器可读清单**（每条目含 size/crc/mtime/mode）、`-x` 排除模式、`-o` 输出、`-y` 静默确认、`-t` 完整性测试 | 捆绑 `7z.dll`/7-Zip 代码（第三方）、加密包、`archive.extract` 默认覆盖 | `archive.list/test/extract/create` |
| **jq** | `-r` 原始字符串输出、`-e` 退出码语义（无匹配=1）、`--slurp`/紧凑输出、`--sort-keys` 式稳定化 | **完整 jq 语言**（管道/函数/递归）、`@base64` 等高级过滤器 | `data.json.get/query/fmt/set/merge/diff`（路径 + 受限谓词 DSL） |
| **GNU coreutils**（`sha256sum`/`diff`/`sort`/`uniq`/`wc`/`cmp`） | `sha256sum` 的文件格式**逐字节兼容**（两空格/`*`、`\` 转义、`-c` 校验语义）、`diff -u` 的 hunk 结构、`sort --stable`、`uniq -c`、`wc` 的多计数 | 外部进程依赖（我们用 BCL 实现） | `hash.manifest/check`、`text.diff/sort/uniq/stats`、`file.compare` |
| **robocopy** | `/MIR` 的镜像语义与"先列出计划"的思路、`/XD /XF` 排除、`/R /W` 重试、`/TEE` 日志 | **默认破坏性**（`/MIR` 直接删）、参数风格（`/X` 与我们的 `--x` 统一） | `dir.sync`（默认 `--dry-run` 语义 + `--max-delete` 安全阀） |
| **MCP 工具约定** | 工具需自带 **JSON Schema 参数描述** 与 **行为注解**（`readOnlyHint`/`destructiveHint`/`idempotentHint`/`openWorldHint`）、`tools/list`+`tools/call` 的稳定握手、`notifications/progress`、请求取消 | 只做 **stdio 传输**（不做 HTTP/SSE 服务器、不做 OAuth/多租户） | `capabilities --format mcp`、`ParamSpec`、`serve` 的 `progress`/`$/cancelRequest`、`mcp --stdio`，见 [MCP ToolAnnotations](https://csharp.sdk.modelcontextprotocol.io/v1/api/ModelContextProtocol.Protocol.ToolAnnotations.html) |
| **Windows 自带 CLI**（`schtasks`/`netsh`/`reg`/`icacls`/`powercfg`/`Get-MpPreference`/`Get-AuthenticodeSignature`） | 能力背后的 **API/数据源**（Task Scheduler COM/XML、`HNetCfg.FwPolicy2`、`WinVerifyTrust`、`Win32_QuickFixEngineering`、MSFT_MpPreference） | **解析本地化文本输出**（中文 Windows 上列名会变）、`Get-AuthenticodeSignature` 的 20s 调用链、`icacls` 的文本解析 | `task.*`（XML 优先）、`net.firewall`（COM 优先）、`sign.verify`（`WinVerifyTrust` 直调）、`installed.list`（注册表）、`defender.*`（PowerShell + WMI 双路） |
| **PowerShell** | "覆盖面广"的教训：很多系统信息只有它给；`Get-MpComputerStatus` 等 cmdlet 作为**降级路径** | 把 PowerShell 当实现语言（启动慢、文本解析脆弱、执行策略不可控） | `dev.ps`（显式、受 policy 约束的可选通道）+ 各命令的 `--via powershell` 降级 |

### 6.2 从对比中提炼的四条设计结论

1. **我们的差异化不是"功能最多"，而是"给程序的确定性"**：每个命令有稳定 JSON、严格退出码、`--dry-run` 保真度、
   完整参数 schema、可回滚事务。Sysinternals 强在深度、ripgrep 强在速度，而它们对 agent 都缺"结构化契约 + 回滚"。
2. **凡是"文本输出解析"能避免的，一律避免**：优先 API（`GetExtendedTcpTable`/`WinVerifyTrust`/`Task Scheduler XML`/注册表），
   文本解析只作降级，且降级必须写进 `warnings`/`degraded`。
3. **凡是"需要管理员 + 结构脆弱"的（MFT/USN/句柄枚举/内核）一律不做**，用免提权的等价物（Restart Manager、JSONL 索引、WMI）替代——
   这不是能力缺失，而是**可靠性选择**：不支持的能力比"有时崩的能力"更有价值。
4. **执行与网络的入口必须收敛**（`run`/`job.start`/`dev.*`/`net.download`/`net.http` 写方法），
   这样才能用一份 `policy.json` 管住所有风险点。

---

## 7. 风险登记与缓解

| # | 风险 | 触发场景 | 缓解措施（必须落到代码） |
|---|---|---|---|
| R1 | **误删用户数据** | `file.rm`/`dir.sync --mirror`/`temp.clean` | 默认回收站；`--max-items` 安全阀；`SafetyGuard` 保护根路径（驱动器根/Windows/ProgramFiles/Profile）；`--min-age`；undo 备份；`temp.clean` 不跟随重解析点 |
| R2 | **归档炸弹 / zip-slip** | `archive.extract` | 解压前逐条解析并校验落在 dest 之下；`--max-ratio`/`--max-total-bytes`；拒绝链接条目；已经解出的部分要如实报告 |
| R3 | **ACL/属主自锁** | `acl.set/reset/copy` | 先存 SDDL；自底向上递归；保护根路径；`undo apply` 可还原；拒绝 Everyone+FullControl（除非 `--i-know`） |
| R4 | **Defender 排除项被滥用** | `defender.exclusions add` | 需管理员 + `--yes`；只允许用户目录/项目目录；输出 `revertCommand` 且做端到端撤销测试 |
| R5 | **agent 授权执行恶意/危险命令** | `run`/`dev.*`/`job.start` | `policy.json` 默认白名单；危险模式硬拒；输出/时间/内存限额；Job Object 保证无孤儿；`runs.jsonl` 留痕 |
| R6 | **长任务泄漏与失控** | `scan.watch`/`eventlog.tail`/`job.start` | 必须给 `--duration`/`--max-events`；Job Object `KILL_ON_JOB_CLOSE`；`job.kill --tree` 用 `TerminateJobObject` |
| R7 | **大结果 OOM / 烧上下文** | `--jsonl` 百万项、`hash.dir` 50 万文件 | `Output.Streaming`；`--max-inline-bytes` 自动 artifact；`artifact.get` 分页；`--max-results` 提前终止 |
| R8 | **正则 ReDoS** | `--content-regex`/`--pattern` | 每匹配 `TimeSpan` 超时（2s）；模式长度上限 4KB；超时进 `failures` 不中断整批 |
| R9 | **COM/WMI 兼容性与本地化** | `patch.list --history`、`net.firewall`、`perf.sample` | COM 用 `Type.InvokeMember` 反射（不用 `dynamic`）；性能计数器实例名动态获取；WMI 失败降级并置 `degraded:true` |
| R10 | **签名校验超时反复出现** | `sign.verify` | 不做人为 20s 限制；`--revocation none` 为默认；`--catalog` 并行；回报 `elapsedMs`；`sign.verify-dir` 并行度默认 4 |
| R11 | **网络命令挂死** | `net.http/download/dns/tcp` | 每命令默认超时；`--no-proxy`/`--proxy` 显式；`DefaultConnectionLimit` 跟随 `--parallel`；失败必须给 `E_NET` |
| R12 | **队列双取/状态撕裂** | 多 agent 并行 `task.pull` | 单一独占锁 + 追加事件 + fsync；租约 + `task.reap`；`task.compact` 用原子替换；压测 8 并发无重复 |
| R13 | **契约漂移**（实现者各自扩展选项/字段） | 并行开发 | 契约改动只能 Lead；本文件 §8 的变更流程；`capabilities` 作为唯一真源；每批验收含 `manifest --json` 结构断言 |
| R14 | **范围失控** | "顺便再加一个功能" | §5 不做清单；新增命令必须回答"价值/复杂度/风险/依赖"四问（FEATURES 模板）；v0.2 只做 B0–B5 |
| R15 | **性能计数器/热区不可用被误判为 bug** | `perf.sample`、`power.thermal` | `availability`/`degraded` 字段 + hint；返回 0 并说明，而不是报错 |

---

## 8. 版本、兼容与契约变更流程

### 8.1 版本策略（`ToolInfo.Version` / `ToolInfo.Protocol`）

| 变更类型 | 影响 | 版本动作 |
|---|---|---|
| 新增命令 / 新增可选参数 / 新增 `data` 字段 | 向后兼容 | minor（`0.2.0`） |
| 修改命令行为但保持字段与退出码 | 兼容 | minor + 在 `docs\` 记录行为变化 |
| 删除/重命名命令、参数；改变字段类型/语义；改变退出码含义 | **破坏兼容** | major（`1.0.0`）+ 至少一个 minor 的弃用期（`warnings` 里提示 `deprecated`，并在 `capabilities` 标 `deprecated:true`） |
| `serve` 新增方法 / 通知 | 协议向后兼容 | `Protocol` +1（`2`），`initialize` 回报 `protocolVersion`，客户端按版本协商 |
| `serve` 删除/改语义 | 破坏 | `Protocol` major 递增 |

**消费方约定（写进 `capabilities`）**：agent 必须忽略未知字段、按 `name`（而非顺序）取字段、按退出码而非文案判断成败。

### 8.2 契约变更流程（防止并行开发互相打架）

1. 只有 **Lead** 可以改 `docs\ARCHITECTURE.md`、`docs\CLI-CONTRACT.md`、`src\Program.cs`、`src\Core\*.cs`（他人文件）。
2. 变更必须**先**更新文档，再改代码，然后**广播**给全部成员（含"受影响命令清单"）。
3. 每个成员在收到广播后**先 rebase 自己的实现**（重新 `read` 受影响的 `Core` 文件）再继续。
4. 语法/语义变更必须同步更新 `manifest --json` 与 `capabilities`（否则 agent 会按旧契约调用）。
5. 每批交付前，Lead 跑一次"契约一致性检查"：`manifest --json` 中每个命令的 `params` 与实际解析逻辑一致（抽样 + 全量 `--help` 断言）。

### 8.3 本路线图建议 Lead 一次性完成的契约修订

* `ToolInfo.Version = "0.2.0"`、`ToolInfo.Protocol = "2"`（B0 完成时）；
* `CLI-CONTRACT.md`：§2 新增全局选项（`--max-inline-bytes/--progress/--owner/--session/--i-know/--lock-timeout/--max-items/--exit-code`）、
  §5 新增 `plan/applied/txid/failures[].code/artifact/truncatedAt`、§6 新增 8 个数据目录、§8 新增 9 个方法、§9 增补执行类命令的 `--dry-run` 语义；
* `ARCHITECTURE.md`：§3 写入范围新增（`src\Core\{LockFile,UndoJournal,SafetyGuard,SecretRedactor,Sta,BuildRefs,ParamSpec,ArtifactStore,TaskStore,Select,Grep,EncodingDetect,Tar}.cs` 以及 §5 新增模块）、§4 核心 API 增补、§6 质量要求增补（undo/工件/策略/脱敏）。

---

## 9. 批次分工与写入范围（给 Lead 排任务用）

> 写作范围是**建议值**，用于避免并行写冲突；每个新文件都必须由 Lead 批准并在 `ARCHITECTURE.md` §3 登记。

| 批次 | 建议 owner | 建议写入范围（新文件为主） | 依赖 |
|---|---|---|---|
| **B0** | Lead | `src\Core\{ParamSpec,LockFile,UndoJournal,SafetyGuard,SecretRedactor,Sta,BuildRefs}.cs`、`src\Program.cs`、`src\Core\{Cli,Runtime,Fs}.cs`、`build.ps1`、两份契约 md | — |
| **B1** | scanner-dev | `src\Commands\{ScanCommands,HashCommands}.cs`、`src\Core\{Select,Grep,EncodingDetect}.cs` | B0 |
| **B2** | agentio-dev | `src\Commands\{JobCommands,LogCommands,ServeCommand}.cs`、`src\Core\{TaskStore,ArtifactStore,ContextStore,Capabilities,ReportGen}.cs`、`src\Core\JobStore.cs` | B0（`serve` 方法表需 Lead 先改契约） |
| **B3** | sysdev | `src\Commands\{ProcCommands,SysCommands,SignCommands,NetCommands}.cs`、`src\Core\{RestartManager,WinTrust,EventLogQuery,PerfSampler}.cs` | B0、B1（`scan.locked` 复用 `RestartManager`；`temp.clean` 依赖 `SafetyGuard`） |
| **B4** | scanner-dev + filesdev（新增） | `src\Commands\{FileCommands,DirCommands,ArchiveCommands,TextCommands,DataCommands,UndoCommands}.cs`、`src\Core\{Tar,TarWriter,Csv,Ini,UnifiedDiff,Template}.cs` | B0（`UndoJournal`/`SafetyGuard`） |
| **B5** | sysdev | `src\Commands\NetCommands.cs`（扩展）、`src\Core\{TcpTable,DnsClient,HttpProbe,TlsProbe}.cs` | B0、B3（复用 `WinTrust` 做证书链） |
| **C1–C3** | 按模块认领 | `src\Commands\{CodecCommands,OfficeCommands,ImageCommands,DesktopCommands,GitCommands,DevCommands,PathCommands,PeCommands,CertCommands,AclCommands,DefenderCommands,McpCommand}.cs`、`src\Core\{Xlsx,Docx,Pptx,Pdf,Pe,Png,CsvQuery,YamlSubset}.cs` | B0–B5 |

**并行规则**（与 Agent Teams 的工作流一致）：
1. 一个文件只有一个作者；跨文件复用必须先由 Lead 把公共能力放进 `src\Core\`；
2. 每个成员**先用 `-Out <name>` 构建自己的 exe**（`build.ps1 -Out dsh-toolbox-<member>.exe`），
   避免并行构建互相覆盖 `dist\dsh-toolbox.exe`；Lead 在集成时统一构建主产物；
3. 破坏性命令的 `--dry-run` 证据写入 `docs\EVIDENCE.md`（每批一节，含命令、退出码、断言与前后 hash 对比）。

---

## 10. 总验收清单（每批 + 收口）

### 10.1 每批都必须满足（DoD）

- [ ] `pwsh -File build.ps1` 零 **CS 错误**（警告不强制，但不得新增）；`-Test` 冒烟通过；
- [ ] 本批每个命令都能用 `--json` 跑通，并出现在 `manifest --json`（含 `params` 与 `sideEffects`）；
- [ ] `--jsonl` 命令通过"首行 meta / 末行 summary / 中间 item"的帧结构断言；
- [ ] 破坏性命令：`--dry-run` **零副作用**（用 fixture 全量 `(mtime, sha256)` 前后对比证明）、
      未加 `--yes` 返回 exit 2 且 hint 正确、执行后可 `undo apply` 还原；
- [ ] 部分失败返回 **exit 6** 且 `data.failures` 非空（不得中途抛出丢失已完成部分）；
- [ ] 长任务可 `--timeout` 中止（exit 5）且**不留孤儿进程**（Job Object）；
- [ ] 外部命令依赖（git/schtasks/reg）缺失时返回 exit 3 + `E_TOOL_MISSING` + 安装提示；
- [ ] 需要管理员的路径在非提权下返回 exit 4 + `E_DENIED` + hint（**不允许**抛未分类异常）；
- [ ] `docs\EVIDENCE.md` 有本批的真实调用记录（命令 + 退出码 + 关键字段断言）；
- [ ] 敏感信息默认脱敏（抽查 `env`/`context.list`/`report.gen`/`job.output` 输出）。

### 10.2 收口验收（对应最初的三条用户诉求）

| 用户诉求 | 验收场景（一条命令链） |
|---|---|
| **"灵活的文件扫描"** | `scan find --path <项目> --include "**/*.cs" --content-regex "TODO" --sort mtime --reverse --max-results 20 --json`<br>→ 再 `scan snapshot --hash` + `scan diff --live` + `hash.manifest --out SHA256SUMS` + `hash.check --file SHA256SUMS`<br>→ 断言：过滤、内容匹配、排序、截断、快照、差异、清单、校验 8 个环节串起来 | 
| **"让 agent 长期连接、可观测"** | `serve --stdio` 一次连接内依次执行 `initialize` → `capabilities` → `call scan.find` → `task.add`/`task.pull` → `job.start` → `job.output --follow` → `artifact.get --offset/--limit` → `log.tail --follow` → `progress` 通知 → `$/cancelRequest` → `shutdown`<br>→ 断言：stdout 全是单行合法 JSON、无人类文本混入、每个 method 有真实响应帧 |
| **"可恢复"** | 一段"多步破坏性操作"（`file.rename` + `text.enc.convert` + `file.rm` + `registry.set`）<br>→ `audit.timeline --destructive-only --json` 拿到全部 `txid`<br>→ 依次 `undo apply --txid ...` → 断言目录/注册表/编码全部回到操作前（fixture hash 对比） |

### 10.3 明确不放进 v0.2 的（避免范围失控）

`data.csv.query`（L 级）、`office.xlsx.write`（L）、`pdf.text`（L）、`proc.handles`（L + 提权）、
`checkpoint.*`（L）、`scan.index`（L）、`mcp --stdio`（L）、`text.patch`（L）、`dev.run-cs` 沙箱限额、
`net.forward`、`task.create/delete`、`svc.config`、`registry.set`、`env.patch`、`acl.set/reset/copy`、`startup.disable`、
`data.yaml.get`（子集解析器）、`pe.imports`（L）。
以上全部在 [FEATURES.md](FEATURES.md) 有完整规格，等 v0.3 按 C1–C3 认领即可 —— **规格已完成，不需要重新调研**。

---

## 附：本文档与 FEATURES 的对应关系

| ROADMAP 章节 | FEATURES 章节 |
|---|---|
| §3 B1 扫描核心 | [FEATURES §1（选择器 S0–S9 + scan.*）](FEATURES.md)、[§3 hash.*](FEATURES.md) |
| §3 B2 Agent 协作 | [FEATURES §14（task/artifact/context/capabilities/run/serve）](FEATURES.md)、[§15（undo/policy）](FEATURES.md) |
| §3 B3 系统与安全 | [FEATURES §8（proc）](FEATURES.md)、[§9（系统管理）](FEATURES.md)、[§11（签名/ACL/Defender）](FEATURES.md) |
| §3 B4 写能力 | [FEATURES §2（file/dir）](FEATURES.md)、[§4（archive）](FEATURES.md)、[§5（text）](FEATURES.md)、[§6（data）](FEATURES.md) |
| §3 B5 网络 | [FEATURES §10（net.*）](FEATURES.md) |
| §4 C1 数据/文档/桌面 | [FEATURES §6/§7/§12/§13](FEATURES.md) |
| §4 C2 安全深化 | [FEATURES §11](FEATURES.md)、[§1.17–1.23](FEATURES.md) |
| §4 C3 规模与生态 | [FEATURES §14.18/§14.22](FEATURES.md)、[§1.26](FEATURES.md) |
| §8 契约变更清单 | [FEATURES 附录 A](FEATURES.md#附录-a需要-lead-改动的契约点) |

