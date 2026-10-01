# dsh-toolbox 功能目录与 CLI 规格（FEATURES）

> 作者：feature-scout（研究/规格角色，不写代码）。版本：v0.2 规划稿（2026-02）。
> 本文档是**实现规格书**：每一项都给出命令名、选项语义与默认值、`data` 字段结构、退出码、破坏性闸门与依赖的核心 API，
> 目标是"照着写代码即可，不需要再设计"。
> **本文档不修改任何契约**：所有条目都必须复用 `docs/ARCHITECTURE.md` 与 `docs/CLI-CONTRACT.md` 已冻结的信封、
> 退出码（0/1/2/3/4/5/6/130）、全局选项、文件扫描选项词汇（§7）与数据目录（§6）。
> 需要改动冻结契约的地方（新增全局选项、新增 `serve` 方法、扩展 `BoolNames`）在 [附录 A](#附录-a需要-lead-改动的契约点) 单列，
> 只能由 Lead 落笔。
>
> 配套文档：实现顺序、批次理由、不做清单、同类工具对比见 [ROADMAP.md](ROADMAP.md)。

---

## 0. 规格约定（读命令条目之前先读这一节）

### 0.1 条目模板

每个命令条目固定包含 6 行要素：

```
#### `group.action` — 一句话
价值: 高/中/低（agent 为什么需要） · 复杂度: S/M/L · 风险: 无 / D 破坏性 / A 需管理员 / N 联网 / P 隐私 / X 执行外部代码
用法: `<一行 usage>`
选项: 表（选项 | 默认 | 语义）
data: `<JSON 结构片段>`（含 items/count/columns 约定）
退出码: 命中哪些码、什么条件
闸门: 破坏性命令的 --dry-run/--yes 行为；无副作用命令写「无」
```

### 0.2 复杂度标尺（S/M/L，用于排期与分工）

| 级 | 定义 | 典型工作量 |
|---|---|---|
| **S** | 纯 BCL 调用或简单字符串/正则处理；不需要 P/Invoke、不需要解析二进制格式 | ≤ 120 行，1 个文件内可完成 |
| **M** | 需要 P/Invoke、自有解析器（tar/PE/CSV/DNS/INI）或并发+落盘 | 120–400 行 |
| **L** | 多阶段流水线、需要权限提升、需要 COM/Windows API 组合、或需要跨命令共享状态 | > 400 行，建议独立模块 + 独立任务 |

### 0.3 风险标签

* **D 破坏性** → 必须走 `--dry-run`/`--yes` 闸门（CLI-CONTRACT §9），且必须在 `Ctx.DryRun` 时不产生任何副作用。
* **A 需管理员** → 非提权时返回 `exit 4` + `E_DENIED` + `hint`（不要抛未分类异常），`doctor --json` 已回报 `elevated`。
* **N 联网** → 必须有 `--timeout`（命令级默认值见条目），离线环境必须给出 `E_NET` 而不是挂死。
* **P 隐私/敏感** → 输出可能含密钥、用户名、路径、屏幕内容；默认脱敏，`--show-secrets` 才明文（对齐现有 `env` 的做法）。
* **X 执行外部代码** → 会运行用户/agent 提供的代码或程序；必须受 `exec policy`（§14.9）约束并有 `--yes`。

### 0.4 三条硬约束（所有条目默认成立，条目里不再重复）

1. **一切结果走 `Ctx.Out.Result(cmd, data)`**；人类可读文本走 `Ctx.Out.Line`（JSON 模式自动落 stderr）；进度/警告走 `Ctx.Out.Warn`。
2. **绝不静默成功**：批量操作个别失败进 `data.failures`（`[{path,error,code}]`）并返回 `exit 6`；整体失败抛 `ToolException`。
3. **长循环必须 `ctx.Cancel.ThrowIfCancellationRequested()`**（或用 `Ctx.Cancel` 传给 `Fs.ParallelMap`/`Fs.HashFile`）；
   `--jsonl` 流式命令每 N 条（默认 200）检查一次。

### 0.5 选项解析的三个真实陷阱（照抄会踩）

1. **布尔选项必须在 `ArgMap.BoolNames` 白名单里**（`src/Core/Cli.cs` 第 17–26 行）。
   不在白名单的 `--flag` 会被当成"取值选项"：`--flag --path x` 侥幸可用（下一 token 以 `-` 开头），
   但 `--flag value` 会把 `value` 吃掉。**任何新布尔选项都必须在附录 A 登记，由 Lead 加入白名单。**
2. **不要设计"可选值"选项**。契约里的 `--hash [algo]` 属于这一类，行为依赖后面跟的是不是选项。
   新命令一律用两段式：`--hash`（bool）+ `--hash-algo <name>`（默认 `sha256`）。这是对 §7 的**向后兼容细化**，不破坏老写法。
3. **短选项别名已占用**：`-h -j -q -v -y -n(max-results) -o(output) -p(path) -r(recursive) -d(depth)`。
   新命令不要复用这些字母表达别的含义；需要短名请 Lead 在 `ShortAliases` 里登记（附录 A）。
4. `--include "a,b"` **不会**按逗号拆分（契约 §1）；多值一律重复给。

### 0.6 破坏性命令统一写法（照抄这段）

```csharp
if (!ctx.ConfirmDestructive("删除 12 个文件（共 3.4 MB）"))   // dry-run 返回 false
{ /* 只输出 plan：ctx.Out.Result(cmd, Json.Obj("plan", plan, "applied", false, ...)); return ExitCodes.Ok; */ }
// 真正执行：先写 undo 日志（§15.1），再逐项执行，失败进 failures，最后 return failures.Count > 0 ? 6 : 0
```

未加 `--yes` 且未加 `--dry-run` 时 `Ctx.ConfirmDestructive` 抛 `E_NEEDS_CONFIRM` → `exit 2`，
`hint` 统一为「加 `--dry-run` 预览计划，或加 `--yes` 真正执行」（`Ctx.ConfirmDestructive` 已实现，别自己写）。

### 0.7 `data` 通用字段（跨命令统一，便于 agent 泛化处理）

| 字段 | 类型 | 语义 |
|---|---|---|
| `items` | array | 主结果列表（契约 §5） |
| `count` | int | `items` 长度 |
| `columns` | string[] | 人类模式列名（`Output.WriteHuman` 会读它） |
| `plan` | array | 破坏性命令的"将要做的事"，元素 `{action,target,detail}` |
| `applied` | bool | 本次是否真的产生副作用（`--dry-run` 时 `false`） |
| `failures` | array | `[{path,error,code}]`，非空则 `exit 6` |
| `scanned` / `matched` / `skipped` / `denied` | int | 过滤类命令的漏斗计数（契约 §5） |
| `truncatedAt` | string | `max-results` / `max-bytes` / `timeout` （配合信封锁 `truncated:true`） |
| `txid` | string | 破坏性事务 id，可用 `undo apply --txid` 回滚（§15.1） |
| `artifact` | object | `{id,bytes,sha256,inline:false}`，大结果落盘时替代内联 `items`（§14.2） |
| `elapsedMs` | int | 命令内部执行的子耗时（信封锁已有总耗时，此处用于分阶段） |

### 0.8 命名规范

* 命令名 `group.action`，**group 用单数小写名词**（`scan` `file` `text`）；CLI 写作空格：`scan find`。
* 已占用 group：`scan hash proc svc disk eventlog installed defender net sign job log run serve doctor sysinfo env manifest`。
* 动作名优先动词的裸形式：`find size tree dup snapshot diff verify`；破坏性动作显式：`rm mv rename clean set delete`。
* 不引入别名（alias）除非有强需求；别名的解析成本高于收益（`Registry.Find` 会遍历全部命令）。

---

## 1. 文件与磁盘（核心：灵活的文件扫描）

> 用户明确把"灵活的文件扫描"列为核心需求，所以本节最深：先把**选择器**做成一套通用的、可组合的词汇，
> 再让所有 `scan.*` / `file.*` 批量命令共享它。选择器的实现在 `FsScanOptions` + 一层新的 `Selector` 帮助类
> （建议放在 scanner-dev 拥有的新文件 `src/Core/Select.cs`，**需要 Lead 批准新增文件**；或先落在 `ScanCommands.cs` 内的静态类）。

### 1.0 统一选择器（S0–S9）—— 所有 `scan.*` 与 `file.*` 批量命令都支持

基础词汇全部来自 CLI-CONTRACT §7（`--path --include --exclude --exclude-dir --ext --name-regex --content-regex
--min-size --max-size --newer --older --depth --hidden --follow --max-results --parallel --sort --reverse --hash`），
下面是 §7 尚未覆盖、本批需要新增的 10 个选择器选项（**全部为布尔或取值明确，无"可选值"**）：

| 选项 | 默认 | 语义 | 新增布尔? |
|---|---|---|---|
| `--from-file <file>` | 无 | 从文本文件读取路径列表（每行一条，`#` 开头跳过）；与 `--path` 互斥或叠加（叠加取并集） | 否 |
| `--stdin` | false | 从 stdin 读路径列表（按 `--null-terminated` 决定分隔） | **是** |
| `--null-terminated` | false | 输入/输出以 `\0` 分隔（配合 `--print0`，兼容 `find -print0`/`fd`） | 已有 |
| `--print0` | false | 人类模式下路径以 `\0` 结尾输出（管道安全），JSON 模式忽略 | **是** |
| `--ignore-file <file>` | 无 | gitignore 语法的忽略文件（可重复）；v0.2 支持 `!` 取反、目录后缀 `/`、`**` | 否 |
| `--gitignore` | false | 自动加载每个扫描根及其祖先的 `.gitignore`（仅在根是 git 仓库内时生效） | **是** |
| `--smart-case` | false | 当 `--content-regex`/`--name-regex` 不含大写字母时自动忽略大小写（ripgrep 行为） | **是** |
| `--stable` | false | **确定性输出顺序**（按 `sort` 键排序后再输出）；与 `--jsonl` 并存时先缓冲再流式 | **是** |
| `--cross-volume` | false | 允许跨卷（默认在跨越卷/驱动器边界时停止向下，避免扫到网络盘/挂载点） | **是** |
| `--flush` | false | 每 N 条（`--flush-every <n>`，默认 200）flush stdout，长任务可被实时消费 | **是** |
| `--flush-every <n>` | 200 | 配合 `--flush` | 否 |
| `--root-fail-fast` | false | 某个 `--path` 根不存在时立即 `exit 3`（默认：记 warning 继续其余根） | **是** |
| `--dedupe-paths` | true? → 默认 **false** | 同一文件被多根/符号链接重复命中时按 final path 去重 | **是** |
| `--max-bytes <size>` | 无 | 内容类扫描（`--content-regex`、`scan content`）单文件读取上限，超出记 `skipped` | 否 |
| `--max-total-bytes <size>` | 无 | 整次扫描读取总量上限，超出提前终止（`truncatedAt:"max-bytes"`） | 否 |
| `--exclude-file <file>` | 无 | 读取排除 glob 列表（每行一条），与 `--exclude` 等价 | 否 |

**实现要点（照抄）**：

```csharp
var o = FsScanOptions.FromArgs(ctx);              // 已有：负责 §7 词汇
Selector.ApplyExtras(ctx, o);                     // 新增：负责上表（stdin/ignore-file/smart-case/...）
IEnumerable<FsEntry> src = o.HasPathList
    ? o.Paths.SelectMany(p => Fs.Enumerate(p, o)) // 已有
    : Fs.EnumerateMany(o.Paths, o);               // 已有
if (ctx.Flag("sort")) src = src.OrderByKey(ctx.Get("sort"), ctx.Flag("reverse"));  // 排序=必须缓冲
```

* `--sort` 与「边扫边出」冲突：实现上允许缓冲，但必须尊重 `--max-results`（先截断再排序），并在 `data.sorted=true`。
* `--include` 匹配**相对路径**（`Glob.MatchAny` 已实现"含分隔符或 `**` 时按相对路径、否则按文件名"）。
* `--exclude-dir` 只匹配**目录名**（不是路径），且对根目录自身不生效。
* 权限不足的目录进 `FsScanOptions.Denied`，最终 `warnings` 里给一句汇总（`跳过 N 个无权限目录`），**不要**逐条刷屏。
* **已知核心缺陷（需 Lead 修）**：`Fs.Enumerate` 的目录循环内没有取消检查，长扫描无法响应 `--timeout`；
  建议给 `Fs.Enumerate` 加 `CancellationToken ct = default` 重载（附录 A）。在修好之前，消费者用 `Select.TakeWhileCancel(ctx, ...)` 包装。

### 1.1 `scan.find` — 按条件递归扫描文件（v0.1 计划内，此处给出完整规格）

价值: 高（agent 的"眼睛"，所有文件任务的入口） · 复杂度: M · 风险: 无
依赖: `Fs.EnumerateMany` / `Glob` / `FsScanOptions` / `Output.EmitItem`（jsonl 流式）
用法: `scan find --path <dir> [--include <glob>]... [--content-regex <re>] [S0-S9]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--path <dir>` | 位置参数或 `--cwd` | 扫描根，可重复；`-p` 短名 |
| `--include/--exclude/--exclude-dir/--ext/--name-regex` | 无 | 见契约 §7 |
| `--content-regex <re>` | 无 | 命中即返回**该文件**（不做行级输出；要行级用 `scan content`）；触发读取，受 `--max-bytes` 限制 |
| `--hash` + `--hash-algo <a>` | false / `sha256` | 对结果计算哈希（`Fs.HashFile`，可并行） |
| `--files-only` / `--dirs-only` | true / false | 目录模式（`--dirs-only` 复用 `FsScanOptions.DirsOnly`） |
| `--count-only` | false | 只回计数，不逐条输出（大目录快速探查） |
| `--output <file>` | 无 | 结果写入文件（JSONL），`data.outFile` 回报；配合 `--flush` |
| `--relative` | false | 路径改为相对 `--cwd`（契约 §5） |

data: `{"items":[FsEntry.ToDictionary()...],"count":N,"scanned":N,"matched":N,"skipped":N,"denied":N,"sorted":bool,"byExt":{"log":12},"totalBytes":N,"columns":["path","size","mtime"]}`
退出码: 0 正常（含 0 命中）；3 根路径不存在且 `--root-fail-fast`；4 全部根都无权限；5 超时；6 部分根失败（`data.failures`）
闸门: 无（只读）。**注意**：`--delete`/`--exec` 不在此命令上开放，删除走 `file.rm --from-scan`（§2.4），执行走 `run`。

### 1.2 `scan.count` — 只计数（比 find 快一个量级）

价值: 中高（agent 先探查规模再决定策略，避免一次拉 10 万条） · 复杂度: S · 风险: 无
依赖: `Fs.Enumerate`（不构造 items）
用法: `scan count --path <dir> [S0-S9] [--by ext|dir|size-bucket|mtime-day]`
data: `{"files":N,"dirs":N,"bytes":N,"buckets":[{"key":".log","count":12,"bytes":345}],"denied":N,"columns":["key","count","bytes"]}`
退出码: 0/3/4/5；`--by` 非法 → 2

### 1.3 `scan.content` — 内容检索（带上下文，ripgrep 语义）

价值: 高（agent 定位代码/日志/配置的关键手段） · 复杂度: M · 风险: 无
依赖: 新增 `src/Core/Grep.cs`（建议 scanner-dev 拥有）：分块读取 + 正则 + 上下文环形缓冲；`Glob`；`Fs.ParallelMap`
用法: `scan content --path <dir> --pattern <re> [-A <n>] [-B <n>] [-C <n>] [--files-with-matches] [--count] [--only-matching] [S0-S9]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--pattern <re>` | 必填 | 正则；`Require` 缺失 → 2。`--pattern-file <f>` 可替代（多模式，每行一条） |
| `--ignore-case` / `--smart-case` | false | 大小写控制 |
| `-A/-B/-C <n>` | 0 | 后/前/前后上下文行数（`-C` 覆盖前两者） |
| `--max-count <n>` | 0 | 单文件最多命中数（预览优化） |
| `--max-columns <n>` | 200 | 单行显示上限，超出截断并置 `lineTruncated:true`（`--max-columns 0` = 不限） |
| `--encoding <name>` | 自动 | `auto`（BOM+UTF-8 校验→GB18030 回退）/`utf-8`/`gb18030`/`utf-16le`/`latin1` |
| `--multiline` | false | 正则跨行（`.git` 风格 `(?s)`）；内存代价高，需 `--max-bytes` 配合 |
| `--binary <skip\|text\|list>` | `skip` | 二进制文件（前 8KB 含 `\0`）：跳过 / 当文本 / 只列文件名 |
| `--files-with-matches` / `--files-without-match` | false | 只列文件（不输出行） |
| `--count` / `--only-matching` | false | 每文件计数 / 只输出匹配子串（含捕获组时输出组） |
| `--glob <g>` / `--type <t>` | 无 | 语法糖：`--glob` → `--include`；`--type` 映射内置类型表（`cs,js,py,json,log,md,xml,yaml,sql,ps1`，表内定义扩展名集合） |
| `--no-ignore` | false | 不使用 `--ignore-file`/`--gitignore` |
| `--line-number` / `--column` | true / false | 行号默认开；列号可选 |
| `--after-context-separator <s>` | `--` | 上下文块之间的分隔符 |

data: `{"items":[{"path":"C:\\x\\a.log","line":118,"col":7,"text":"...","contextBefore":["..."],"contextAfter":["..."],"match":true,"binary":false}],"count":N,"filesWithMatches":N,"bytesRead":N,"scanned":N,"skipped":N,"denied":N,"encodingUsed":{"gb18030":3},"columns":["path","line","text"]}`
退出码: 0 有命中（或无命中但 `--exit-code` 未开）；**1 无命中**（仅当 `--exit-code`，对齐 `rg`/`grep -q` 便于管道判断）；2 用法；3/4/5/6 同 `scan find`
闸门: 无。`--jsonl` 下一行一个 match，`type:"item"`。

> **ReDoS 防护（必须做）**：`new Regex(pattern, opts, TimeSpan.FromSeconds(2))`（.NET 支持每匹配超时），
> 超时记 `failures[{path,error:"E_REGEX_TIMEOUT"}]` 并继续；同时限制 `--pattern` 长度 ≤ 4KB。

### 1.4 `scan.size` — 目录/子项体积统计

价值: 高（"谁占了我的盘"） · 复杂度: M · 风险: 无
用法: `scan size --path <dir> [--depth 1] [--top <n>] [--apparent] [--hardlink-aware] [S0-S9]`
data: `{"total":N,"totalBytes":N,"items":[{"path":..,"bytes":N,"files":N,"dirs":N,"percent":12.3}],"byExt":{...},"apparent":bool,"columns":["path","bytes","files"]}`
退出码: 0/3/4/5/6
* `--apparent`：用逻辑大小而非占用簇（`--cluster-aware` 时按 4KB/簇对齐，默认关）。
* `--hardlink-aware`（默认 true）：同一 `(volume,fileId)` 只计一次（需 P/Invoke `GetFileInformationByHandleEx`，M 级）。

### 1.5 `scan.tree` — 目录树（含体积聚合）

价值: 中高（给 agent 一份可读的"地图"） · 复杂度: S/M · 风险: 无
用法: `scan tree --path <dir> [--depth 3] [--dirs-only] [--size] [--sort size] [--format text|json|md]`
data: `{"root":..,"items":[{"name":..,"path":..,"depth":1,"dir":true,"bytes":N,"children":N}],"count":N,"totalBytes":N}`
退出码: 0/3/4/5；`--format md` 时为缩进 markdown 列表（agent 可直接写进报告）

### 1.6 `scan.dup` — 重复文件查找（三段式）

价值: 高（清理磁盘、去重素材） · 复杂度: M · 风险: 无（发现阶段）
依赖: `Fs.QuickHash`（头尾 64KB + 长度）→ `Fs.HashFile`（全量）
用法: `scan dup --path <dir> [--min-size 1KB] [--strategy quick|full|both] [--keep first|oldest|newest|shortest-path] [--across-roots]`
data: `{"items":[{"hash":"...","size":N,"count":3,"files":[{"path":..,"mtime":..}],"wasted":2*N}],"groups":N,"duplicateFiles":N,"wastedBytes":N,"scanned":N,"strategy":"both","columns":["hash","count","size","wasted"]}`
退出码: 0/3/4/5/6
闸门: 无。**不提供 `--delete`**（与 ROADMAP"不做清单"一致）：删除由 `file.rm --from-json` 显式两步完成，避免一条命令毁掉用户数据。

### 1.7 `scan.snapshot` — 目录状态快照（变更检测基线）

价值: 高（agent 执行前后对比、增量判断、可恢复性） · 复杂度: M · 风险: 无
用法: `scan snapshot --path <dir> [--out <file>] [--hash] [--hash-algo sha256] [--max-bytes 2GB] [--meta-only]`
data: `{"outFile":"...\\snap-20260217-101530.jsonl","entries":N,"bytes":N,"hashed":true,"algorithm":"sha256","root":..,"snapshotId":"20260217-101530-ab12"}`
退出码: 0/3/4/5/6
* 输出格式：**JSONL**，每行 `{"p":"a/b.txt","s":123,"m":"2026-02-01T...","h":"<hash>","a":"Archive"}`（短键省空间）。
* `--meta-only`：不哈希，仅路径/mtime/size（快照 10 万文件 < 1 秒）。
* 存储默认落 `Paths.Home\snapshots\<snapshotId>.jsonl`，`--out` 可覆盖。

### 1.8 `scan.diff` — 两次快照对比（增/删/改/移动）

价值: 高（"我改了什么"、"任务是否真的生效"） · 复杂度: M · 风险: 无
用法: `scan diff --base <snapA> [--target <snapB>] [--live <dir>] [--detect-moves] [--hash] [--max-items <n>]`
data: `{"added":[..],"removed":[..],"modified":[{"path":..,"sizeBefore":..,"sizeAfter":..,"mtimeBefore":..,"hashChanged":true}],"moved":[{"from":..,"to":..,"confidence":"hash"}],"unchanged":N,"base":..,"target":..,"columns":["kind","path","detail"]}`
退出码: 0（无差异）/**1 有差异**（`--exit-code` 时，便于 CI/agent 判断）**但默认仍有差异也返回 0**；2/3/5
* `--live <dir>`：target 直接现扫（免落快照）。
* `--detect-moves`：用 `(size,hash)` 在 removed/added 里做匹配，命中即归入 `moved`（`confidence":"size+hash"`）。

### 1.9 `scan.verify` — 用快照/清单校验当前目录

价值: 高（完整性、防篡改、任务验收） · 复杂度: S/M · 风险: 无
用法: `scan verify --path <dir> --snapshot <file|id> [--hash-algo sha256] [--missing-ok] [--extra-ok]`
data: `{"ok":true,"checked":N,"mismatch":[{"path":..,"expected":"..","actual":"..","kind":"hash|size|mtime"}],"missing":[..],"extra":[..],"failures":N,"columns":["kind","path","detail"]}`
退出码: 0 全部一致 / **1 有不一致**（agent 主判据）/ 3 清单文件不存在 / 4 / 5

### 1.10 `scan.recent` — 最近变动

价值: 中高（"刚刚发生了什么"） · 复杂度: S · 风险: 无
用法: `scan recent --path <dir> [--since 2h|--newer 2h] [--until <date>] [--sort mtime] [--top 50] [--dirs]`
data: 同 `scan.find`，额外 `"window":{"since":..,"until":..}`
退出码: 0/3/4/5；`--since` 与 `--newer` 等价（`--newer` 已在 §7，保留 `--since` 作为可读别名，二选一）

### 1.11 `scan.large` — 大文件定位

价值: 中高（磁盘清理第一步） · 复杂度: S · 风险: 无
用法: `scan large --path <dir> [--min-size 100MB] [--top 20] [--sort size] [--apparent]`
data: `{"items":[FsEntry...],"threshold":104857600,"totalBytes":N,"columns":["path","size","mtime"]}`
退出码: 0/3/4/5/6

### 1.12 `scan.empty-dirs` — 空目录（含只含空目录的目录）

价值: 中（清理、判断项目骨架是否完整） · 复杂度: S · 风险: 无（发现）；删除走 `dir.rm`
用法: `scan empty-dirs --path <dir> [--recursive-prune] [--min-files 0] [--include-hidden]`
data: `{"items":[{"path":..,"depth":2,"files":0,"subdirs":1}],"count":N,"candidatesIfPruned":N,"columns":["path","depth"]}`
退出码: 0/3/4/5
* `--recursive-prune`：把"删掉子空目录后也变空"的父目录也算进来（纯计算，不删）。

### 1.13 `scan.types` — 文件类型统计（扩展名/MIME/魔数）

价值: 中高（agent 判断"这是个什么项目/目录"） · 复杂度: M（魔数表） · 风险: 无
用法: `scan types --path <dir> [--by ext|magic|magic-ext] [--top 30] [--unknown] [S0-S9]`
data: `{"items":[{"key":".cs","magic":"text/x-csharp","count":120,"bytes":N,"percent":64.2}],"unknown":N,"scanned":N,"columns":["key","count","bytes"]}`
* 魔数表：PE(`MZ`)、ZIP(`PK`)、GZip(`1F 8B`)、7z、RAR、PDF、PNG/JPEG/GIF/BMP/WebP、SQLite(`SQLite format 3`)、
  OLE2(D0CF11E0)、ELF、ICO、MP3(ID3/`FF FB`)、MP4(`ftyp`)、文本（无 `\0` 且 UTF-8/GBK 可解）——**只做前 64 字节判定，不解容器**。

### 1.14 `scan.age` — 按时间归档排查（陈旧文件）

价值: 中（清理候选集、合规保留） · 复杂度: S · 风险: 无
用法: `scan age --path <dir> --older 1y [--top 200] [--by-size] [--summary-only]`
data: `{"items":[FsEntry...],"buckets":[{"key":"1-2y","count":N,"bytes":N},{"key":">5y",...}],"totalBytes":N,"columns":["path","size","mtime"]}`

### 1.15 `scan.names` — 非法/危险文件名扫描

价值: 中高（跨平台拷贝、git checkout、归档前必查） · 复杂度: S · 风险: 无
用法: `scan names --path <dir> [--check illegal|reserved|trailing|case-conflict|all] [--max-path 259]`
data: `{"items":[{"path":..,"issue":"illegal-char|reserved-name|trailing-dot-space|case-conflict|too-long|empty-name","detail":":"}],"count":N,"byIssue":{"too-long":3},"columns":["issue","path","detail"]}`
* `reserved`：`CON PRN AUX NUL COM1-9 LPT1-9` 及其带扩展名形式。
* `case-conflict`：同一目录下仅大小写不同的名字（Windows 允许存在但多数工具会互相覆盖）。

### 1.16 `scan.long-path` — 超长路径定位

价值: 中（拷贝/归档/构建失败的常见根因） · 复杂度: S · 风险: 无
用法: `scan long-path --path <dir> [--limit 260] [--relative] [--fix-plan]`
data: `{"items":[{"path":..,"length":312,"exceeds":true,"suggestedShortName":"PROGRA~1"}],"count":N,"limit":260,"longPathsEnabled":false,"columns":["length","path"]}`
* `--fix-plan`：给出可用 `dir.shorten`（8.3 名）或 `file.rename --shorten` 缩短的方案（仍不执行）。
* 读注册表 `LongPathsEnabled` 与 `ctx.ExePath` 的 manifest（本 exe 已 `longPathAware`）。

### 1.17 `scan.locked` — 被占用的文件

价值: 高（"删不掉/覆盖不了"的第一诊断） · 复杂度: M · 风险: 无
依赖: Restart Manager（P/Invoke `rstrtmgr.dll`: `RmStartSession/RmRegisterResources/RmGetList/RmEndSession`）
用法: `scan locked --path <dir> [--by-process <pid|name>] [--path-list <file>] [--timeout 10s]`
data: `{"items":[{"path":..,"processes":[{"pid":1234,"name":"node.exe","startTime":..,"type":"文件|服务|应用"}]}],"count":N,"unresolved":N,"columns":["path","pid","name"]}`
退出码: 0（含 0 命中）；3 if `--path` 不存在；5（RM 调用整体超时）；6（部分路径 RM 失败）
* **不要**用 `NtQuerySystemInformation` 全量句柄枚举：需管理员 + 结构随版本变化 + 极易崩，列进不做清单（ROADMAP §5.2）。
* Restart Manager 单次注册上限 128 个资源 → 分批，每批独立 session。

### 1.18 `scan.ads` — 备用数据流（ADS）清点

价值: 中高（MOTW 之外还有隐藏数据；取证/清理） · 复杂度: S · 风险: 无
用法: `scan ads --path <dir> [--zone-only] [--min-size 0] [--content-preview 0]`
data: `{"items":[{"path":..,"stream":":Zone.Identifier","bytes":N,"zone":3,"referrer":"https://...","preview":null}],"count":N,"filesWithAds":N,"columns":["path","stream","bytes","zone"]}`
* 读取方式：`new FileStream(path + ":Zone.Identifier", ...)`（NTFS 直接支持）。
* 只读命令；删除 ADS 走 `file.ads-rm`（§2.20，破坏性）。

### 1.19 `scan.zone` — MOTW（下载来源）清点

价值: 中高（判断"这文件从哪来"、批量解除锁定前先看清单） · 复杂度: S · 风险: 无（只读）· 隐私: P（含下载 URL）
用法: `scan zone --path <dir> [--zone 3] [--ext exe,dll,ps1] [--group-by-host]`
data: `{"items":[{"path":..,"zone":3,"host":"github.com","referrer":..,"date":"20260201T..."}],"count":N,"byHost":{"github.com":12},"columns":["path","zone","host"]}`
* 解除锁定：`file.unblock`（§2.21，D 破坏性）。
* **与既有实现的重叠（见 [ROADMAP §1.4 D2](ROADMAP.md)）**：v0.1 已实现 `sign.motw`（查看/移除 MOTW）。
  建议**以 `sign.motw` 为唯一入口**并扩展 `--path` 批量与 `--only-zone`，本条与 §2.17 `file.unblock` 不另起实现。

### 1.20 `scan.encoding` — 文本编码清点

价值: 中高（乱码根因、批量转换前摸底） · 复杂度: M（探测启发式） · 风险: 无
依赖: 新增 `src/Core/EncodingDetect.cs`：BOM → 严格 UTF-8 校验 → GB18030 合法性 + 控制字符比例 → UTF-16 无 BOM 启发
用法: `scan encoding --path <dir> [--ext txt,csv,log] [--confidence 0.8] [--show-mixed] [S0-S9]`
data: `{"items":[{"path":..,"bom":"utf-8|utf-16le|none","encoding":"gb18030","confidence":0.93,"eol":"crlf|lf|mixed","nonAsciiRatio":0.04}],"byEncoding":{"utf-8":120,"gb18030":8},"columns":["path","encoding","eol"]}`
退出码: 0/3/4/5/6
* 只读前 64KB；`--show-mixed` 时统计每文件解码错误字节数与"疑似混合编码"（既有 UTF-8 又有 GBK 双字节）。

### 1.21 `scan.entropy` — 熵/压缩率扫描（加密、加壳、疑似勒索）

价值: 中高（agent 可做"异常检测"：突然出现大量高熵文件=勒索特征） · 复杂度: M · 风险: 无
用法: `scan entropy --path <dir> [--min-entropy 7.5] [--sample 65536] [--ext *] [--top 100]`
data: `{"items":[{"path":..,"entropy":7.94,"sampleBytes":65536,"compressible":false,"magic":"none"}],"count":N,"highEntropyFiles":N,"byExt":{".locked":120},"columns":["path","entropy","magic"]}`
* 熵按字节直方图 Shannon（0–8）；`--sample` 默认取头 64KB（大文件可选 `--spread` 三段采样）。

### 1.22 `scan.hardlinks` — 硬链接/重解析点清点

价值: 中（磁盘占用误判、备份工具行为差异、安全审查） · 复杂度: M · 风险: 无
依赖: P/Invoke `GetFileInformationByHandleEx(FileIdInfo)` / `FindFirstFileNameW`
用法: `scan hardlinks --path <dir> [--min-links 2] [--reparse-only] [--junction-target]`
data: `{"items":[{"path":..,"links":3,"fileId":"0x...","volumeSerial":"...","reparseTag":"IO_REPARSE_TAG_MOUNT_POINT","target":"C:\\Windows"}],"count":N,"columns":["links","path","target"]}`

### 1.23 `scan.owner` — 按属主统计

价值: 中（多用户机器、权限排查） · 复杂度: S/M · 风险: P
用法: `scan owner --path <dir> [--group-by user|group] [--top 20] [--denied-report]`
data: `{"items":[{"owner":"DOMAIN\\user","count":N,"bytes":N}],"denied":N,"columns":["owner","count","bytes"]}`
* 用 `File.GetAccessControl` 取 owner（M：ACL 读取可能很慢，建议 `--parallel` 默认 8）。

### 1.24 `scan.perm` — 权限风险扫描（Everyone 可写等）

价值: 中高（安全审查、提权面排查） · 复杂度: M · 风险: 无
用法: `scan perm --path <dir> [--flag everyone-write|world-writable|no-inherit|null-dacl|all] [--exclude-system]`
data: `{"items":[{"path":..,"issue":"everyone-write","aces":[{"identity":"Everyone","type":"Allow","rights":"Modify","inherited":false}]}],"count":N,"columns":["issue","path","detail"]}`

### 1.25 `scan.watch` — 持续监视（配合 job/jsonl）

价值: 中高（agent 长驻观察"什么时候出现文件"） · 复杂度: M · 风险: 无
依赖: `FileSystemWatcher`（内部缓冲易溢出 → 必须处理 `InternalBufferSize` 与 `Error` 事件重新枚举）+ `job.start`
用法: `scan watch --path <dir> [--events create,change,delete,rename] [--since-now] [--debounce 300ms] [--max-events 0] [--duration 10m]`
data（`--jsonl`）：每事件一行 `{"type":"item","item":{"kind":"created","path":..,"oldPath":null,"ts":..,"dir":false}}`；
结束时 `{"type":"summary","count":N,"dropped":N}`
退出码: 0（`--duration` 到点正常结束）/ 5 超时 / 130 Ctrl+C（需修 Program.cs，见附录 A）/ 4
* 必须实现 `--debounce`：编辑器保存会触发 3–5 个事件；按 `(path,kind)` 在窗口内合并。
* 因为要长跑，**默认要求 `--duration` 或 `--max-events` 之一**，否则拒绝执行（2），避免 agent 忘记收尾。

### 1.26 `scan.index` — 可选本地索引（加速重复查询）

价值: 中（同一目录反复扫描时 10–50× 加速） · 复杂度: L · 风险: 无（写 `Paths.Cache`）
用法: `scan index build --path <dir> [--out <file>] | scan index query --path <dir> --query <glob> | scan index info | scan index drop`
data: `build`: `{"indexFile":..,"entries":N,"bytes":N,"elapsedMs":N,"validUntil":..}`
`query`: `{"items":[FsEntry...],"count":N,"fromIndex":true,"indexAgeSec":N,"stale":false}`
* 索引格式：**有序 JSONL**（路径 + size + mtime + dir），按相对路径排序，查询用二分 + glob。
* 失效判定：索引年龄 > `--max-age`（默认 10m）或根目录 mtime 变化即 `stale:true` 并自动回退实时扫描（`fallback:"live"`）。
* **不引入** USN journal / MFT 直读（Everything 的做法）：需要管理员 + 卷句柄 + 版本脆弱，列不做清单。

### 1.27 `scan.by-owner` / `scan.by-time` 说明

这两个方向已由 `scan owner`（1.23）与 `scan age`/`scan recent`（1.14/1.10）覆盖，**不再新增命令**，避免词汇膨胀。

---
## 2. 文件与目录操作

> 本节是"手"：所有写操作都遵循 §0.6 的闸门模板，**并且每一个写操作都要写 undo 日志**（§15.1），
> 默认 `--dry-run` 输出 `data.plan`，`--yes` 才动手。原子性统一用「同目录临时文件 + `File.Replace`/`File.Move`」。

### 2.1 `file.rename` — 批量重命名（正则 + 模板 + 预览 + 回滚）

价值: 高（agent 整理素材/规范命名的高频动作） · 复杂度: M · 风险: **D**
依赖: `Selector`(§1.0) / `Regex.Replace` / undo 日志
用法: `file.rename --path <dir> --pattern <re> --to <template> [--dry-run|--yes] [--preview-limit 50] [--on-conflict fail|skip|suffix] [S0-S9]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--pattern <re>` | 必填 | 作用于**文件名（不含目录）**的正则；缺省 `^$`? → 一律必填 |
| `--to <template>` | 必填 | 替换模板；支持 `$1`/`${name}` 捕获组引用与 `{n}`（序号，从 `--start` 起）、`{ext}`、`{mtime:yyyyMMdd}`、`{parent}` |
| `--start <n>` | 1 | `{n}` 起始序号 |
| `--pad <n>` | 0 | `{n}` 补零宽度 |
| `--on-conflict <fail\|skip\|suffix>` | `fail` | 目标已存在时的策略；`suffix` 追加 ` (2)` |
| `--lower` / `--upper` | false | 结果整体大小写变换（在模板之后） |
| `--transliterate` | false | 非 ASCII → 音译/删音（文件名兼容性，v0.3 可简化为删音） |
| `--max-length <n>` | 255 | 结果文件名上限，超出截断并加短哈希 |
| `--jsonl` | — | 每项一行计划/结果，长批量可流式消费 |

data: `{"plan":[{"from":"a.txt","to":"a-20260217.txt","dir":false}],"renamed":12,"skipped":1,"failures":[],"applied":true,"txid":"20260217-101530-9f2a","columns":["from","to"]}`
退出码: 0 全部成功 / **1** 目标冲突且 `--on-conflict fail`（无副作用，**先全量校验再动手**）/ 2 缺 `--yes` 或模板非法 / 3 路径不存在 / 4 / 6 部分失败
闸门: `--dry-run` → `applied:false` + 只输出 `plan`；`--yes` → 执行并写 undo（`Path.Combine(Paths.Home,"undo",txid+".jsonl")`）。
* **两阶段必须**：先算全部 `plan` 并检测冲突/循环（`a→b, b→a` 需拓扑排序或用临时名中转），再执行。
* 失败可回滚：`undo apply --txid <txid>`（§15.1）按逆序改回。

### 2.2 `file.copy` — 批量复制（校验 + 增量）

价值: 高 · 复杂度: M · 风险: **D**（覆盖时）
用法: `file.copy --path <dir> --dest <dir> [--flatten] [--relative-root <dir>] [--verify hash|size|none] [--no-clobber] [--overwrite] [--skip-newer] [--atomic] [--hardlink] [S0-S9]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--dest <dir>` | 必填 | 目标目录（不存在则创建，除非 `--no-create-dirs`） |
| `--flatten` | false | 目标只保留文件名（跨目录同名会冲突） |
| `--verify <hash\|size\|none>` | `size` | 复制后校验；`hash` 用 `--hash-algo`（默认 sha256） |
| `--overwrite` / `--no-clobber` / `--skip-newer` | false | 冲突三策略（`--no-clobber` 时冲突记 `skipped`） |
| `--atomic` | true | 先写 `.part` 再 `File.Move`（同卷）或 `File.Replace`，避免半成品 |
| `--hardlink` | false | 同卷时用 `CreateHardLink`（P/Invoke）省空间；跨卷自动回退复制 |
| `--preserve <mode>` | `mtime` | `mtime`/`attrs`/`acl` 递增式保留 |

data: `{"copied":N,"bytes":N,"skipped":N,"overwritten":N,"verifyFailures":[],"failures":[],"plan":[...],"applied":true,"txid":..,"columns":["from","to","bytes"]}`
退出码: 0 / 2（缺 `--yes` 且将覆盖）/ 3 / 4 / 6 / 5
闸门: 若本次**不会覆盖或删除任何东西**（纯新增），可不需要 `--yes`；只要存在 `overwritten>0` 就必须走 `--yes`。
* 复制到 `Paths.Home` 之外的用户目录时必须由 `--dest` 显式给出（契约 §9.4 的"永不触碰用户数据目录除非显式给出"取反向约束）。

### 2.3 `file.move` — 批量移动（可跨卷，带校验）

价值: 高 · 复杂度: M · 风险: **D**
用法: `file.move --path <dir> --dest <dir> [--verify size|hash|none] [--cross-volume copy|fail] [--overwrite] [S0-S9]`
data: 同 `file.copy`，键名 `moved`；`crossVolume:"copy+delete"` 时额外给 `deletedSource:N`
退出码: 0/2/3/4/6
闸门: 同 `file.copy`。跨卷移动 = 复制成功 + 校验通过后才删源；校验失败则**保留源**并进 `failures`。

### 2.4 `file.rm` — 批量删除（默认进回收站）

价值: 高 · 复杂度: M · 风险: **D**（不可逆，除非回收站/undo）
依赖: P/Invoke `SHFileOperationW`（回收站）；长路径用 `IFileOperation`（v0.3，L）
用法: `file.rm --path <dir> [--recycle] [--permanent] [--min-age 0s] [--from-scan <file>] [--from-json <file>] [--empty-dirs] [--force] [S0-S9]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--recycle` | **true** | 进回收站（可恢复）；`--permanent` 时关闭 |
| `--permanent` | false | 直接删除（需 `--yes` + `--force`? 不需要 force，但输出里高亮 `irreversible:true`） |
| `--min-age <dur>` | `0s` | 只删 mtime 早于该时长的文件（防误删正在生成的文件，**建议 agent 总是给**，如 `--min-age 1h`） |
| `--from-scan <file>` | 无 | 读取 `scan find --jsonl` 的输出文件，逐项删除（两步式安全流） |
| `--empty-dirs` | false | 递归删除空目录 |
| `--max-items <n>` | 0 | 安全阀：命中数超过 n 时**拒绝执行**并返回 2（防止 `--path C:\` 事故） |
| `--force` | false | 忽略只读属性（清 `ReadOnly` 后再删） |

data: `{"plan":[...],"deleted":N,"recycled":N,"bytes":N,"failures":[],"applied":true,"irreversible":false,"txid":..,"columns":["path","bytes","action"]}`
退出码: 0/2（缺 `--yes` / 超 `--max-items`）/3/4/6
闸门: 强制。`--dry-run` 输出 `plan` + `reclaimableBytes`；`--yes` 执行；**回收站删除也算破坏性**（占空间、可能被清空）。
* **安全硬规则（写进代码，不只写文档）**：`--path` 解析后若等于驱动器根、`%SystemRoot%`、`%ProgramFiles%`、
  用户 Profile 根，除非同时给 `--i-know`（新布尔选项），否则拒绝并返回 2。这条比 `--max-items` 更能救命。

### 2.5 `file.mkdir` — 建目录（含中间目录）

价值: 中 · 复杂度: S · 风险: **D**（轻微）
用法: `file.mkdir <path>... [--parents] [--dry-run|--yes]`
data: `{"created":["C:\\a\\b"],"existed":["C:\\a"],"applied":true,"columns":["path","action"]}`
退出码: 0/2/3(父不存在且未 `--parents`)/4

### 2.6 `file.touch` — 创建/更新时间戳

价值: 中（给构建系统假依赖、生成标记文件） · 复杂度: S · 风险: **D**
用法: `file.touch <path>... [--time <date>] [--create] [--size 0] [--dry-run|--yes]`
data: `{"items":[{"path":..,"action":"created|updated","mtime":..}],"count":N,"applied":true}`
退出码: 0/2/3（不存在且未 `--create`）/4

### 2.7 `file.attr` — 属性读写

价值: 中（隐藏/只读/存档/系统；agent 处理"看不见的文件"） · 复杂度: S · 风险: **D**（写入）
用法: `file.attr --path <dir> [S0-S9] [--add hidden,readonly] [--remove system] [--set-normal] [--dry-run|--yes]`
data: `{"items":[{"path":..,"before":"Archive","after":"ReadOnly, Archive"}],"count":N,"applied":true,"columns":["path","before","after"]}`
退出码: 0/2/3/4/6
* 读模式（不带 `--add/--remove/--set-normal`）是只读命令，**无需 `--yes`**；写模式必须闸门。

### 2.8 `file.read` — 读取文件片段（agent 的"看文件"）

价值: 高（比 `type`/`cat` 可控：偏移、编码、字节上限、二进制安全） · 复杂度: S · 风险: 无
用法: `file.read <path> [--offset 0] [--limit 65536] [--lines <n>] [--tail] [--encoding auto|utf-8|gb18030|utf-16le|latin1] [--base64] [--hex] [--max-bytes 8MB] [--show-line-numbers]`
data: `{"path":..,"bytes":N,"totalBytes":N,"offset":0,"encoding":"utf-8","eol":"crlf","text":"...","base64":null,"truncated":false,"sha256":"..."}`
退出码: 0/3/4/5；文件 > `--max-bytes` 时 `truncated:true` + `truncatedAt:"max-bytes"`（**不报错**）
* 默认 `--limit` 64KB，防止 agent 一次读 2GB 把上下文烧光。
* `--hex` 时输出 `hex` 字段（每行 16 字节 + ASCII 侧栏）；二进制文件默认不尝试解码（`encoding:"binary"`）。

### 2.9 `file.write` — 写入/追加/原子替换

价值: 高（agent 落盘的唯一正道，替代 `echo >`） · 复杂度: S · 风险: **D**（覆盖）
用法: `file.write <path> [--content <text>] [--from-file <src>] [--stdin] [--append] [--encoding utf-8|utf-8bom|gb18030|utf-16le] [--eol keep|lf|crlf] [--newline-at-eof] [--no-clobber] [--dry-run|--yes]`
data: `{"path":..,"action":"created|overwritten|appended","bytesWritten":N,"bytesBefore":N,"sha256":..,"encoding":"utf-8","applied":true,"txid":..}`
退出码: 0/2（覆盖未确认）/3（目录不存在）/4
* 默认 **`--newline-at-eof`**；`--encoding` 默认 `utf-8`（无 BOM），与 `file.read` 的默认解码闭环。
* 覆盖前把原文件备份到 `Paths.Home\undo\<txid>\`（若 ≤ `--backup-max-size`，默认 1MB；超出的文件只在 undo 日志里记 hash，回滚时提示无法自动恢复）。

### 2.10 `file.replace` — 原子替换（跨目录/跨卷安全的"另存为覆盖"）

价值: 中高（部署产物、替换被占用的文件由内核完成） · 复杂度: S · 风险: **D**
用法: `file.replace <src> <dst> [--backup <file>] [--dry-run|--yes]`
data: `{"src":..,"dst":..,"backup":..,"bytes":N,"applied":true}`
* 用 `File.Replace(src, dst, backup)`，一次系统调用完成"覆盖 + 备份"，避免中途断电留下半个文件。

### 2.11 `file.truncate` — 截断到指定大小

价值: 低中（日志清理） · 复杂度: S · 风险: **D**
用法: `file.truncate <path> --size <n|0> [--keep-tail <n>] [--dry-run|--yes]`
data: `{"path":..,"bytesBefore":N,"bytesAfter":N,"tailPreserved":false,"applied":true}`

### 2.12 `file.link` — 创建链接（硬链接/联接/符号链接）

价值: 中（省空间、兼容旧路径、目录重定向） · 复杂度: M · 风险: **D** + **A**（符号链接需开发者模式或管理员）
依赖: P/Invoke `CreateHardLinkW` / `DeviceIoControl(FSCTL_SET_REPARSE_POINT)` 或 `cmd /c mklink`（不推荐，解析输出）
用法: `file.link <target> <linkPath> --type hardlink|junction|symlink [--dry-run|--yes]`
data: `{"target":..,"link":..,"type":"junction","applied":true,"requiresElevation":false}`
退出码: 0/2/3（target 不存在）/4（缺权限 → `E_DENIED` + hint "开发者模式或管理员"）
* 联接点（junction）必须用绝对路径、且 `IO_REPARSE_TAG_MOUNT_POINT`，实现较繁（M）；symboliclink 需 `SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE`(0x2) 回退。

### 2.13 `file.shorten` — 8.3 短名

价值: 低中（绕过旧工具的路径长度限制、生成稳定短路径） · 复杂度: S · 风险: 无
用法: `file.shorten <path>... [--scan --path <dir>]`
data: `{"items":[{"path":"C:\\Program Files","shortName":"PROGRA~1"}],"count":N,"columns":["path","shortName"]}`
* P/Invoke `GetShortPathNameW`；卷禁用 8.3 生成时返回原路径并 `note:"8.3 disabled"`。

### 2.14 `file.compare` — 二进制比较摘要

价值: 中（"这两个文件一样吗"比 `fc` 更好用） · 复杂度: S · 风险: 无
用法: `file.compare <a> <b> [--quick] [--base64-diff] [--max-diff-bytes 4096]`
data: `{"same":false,"sizeA":N,"sizeB":N,"sha256A":..,"sha256B":..,"quickEqual":false,"firstDiffOffset":128,"diffPreview":{"aHex":"...","bHex":"..."},"diffBytes":N}`
退出码: 0 相同 / **1 不同** / 3 / 4
* `--quick` 用 `Fs.QuickHash`（头尾 64KB + 长度），不做全量哈希；不同时仍给 `firstDiffOffset`（顺序比较，命中即停）。

### 2.15 `file.split` / `file.join` — 大文件分片与合并

价值: 中（搬运超限文件、分片上传前的准备） · 复杂度: S · 风险: **D**（写盘）
用法: `file.split <path> --part-size 100MB [--dest <dir>] [--name-format {name}.{index:000}] [--dry-run|--yes]`
`file.join --path <dir> --pattern <re|glob> --out <file> [--verify-order name] [--sha256 <expect>] [--dry-run|--yes]`
data: `split`: `{"parts":[{"path":..,"bytes":N}],"count":N,"sliceSha256":[...],"manifest":"<out>.manifest.json","applied":true}`
`join`: `{"out":..,"bytes":N,"parts":N,"sha256":..,"applied":true}`
退出码: 0/2/3/4/6/1（join 校验不匹配 → **1**，并删除半成品）

### 2.16 `file.ads-rm` — 删除备用数据流

价值: 中高（清理 MOTW/残留流） · 复杂度: S · 风险: **D**
用法: `file.ads-rm <path> --stream :Zone.Identifier [--all-ads] [--keep <name>]... [--dry-run|--yes]`
data: `{"items":[{"path":..,"stream":..,"bytes":N,"deleted":true}],"count":N,"applied":true,"failures":[]}`
退出码: 0/2/3/4/6
* `--all-ads` 会删掉全部非默认流（含 SmartScreen 的 `:Zone.Identifier`、`:SmartScreen`），必须 `--yes`。

### 2.17 `file.unblock` — 解除下载锁定（删 MOTW）

价值: 高（PowerShell/脚本/安装器被 MOTW 拦死的常见修复） · 复杂度: S · 风险: **D** + P
用法: `file.unblock --path <dir> [--ext ps1,exe,dll] [S0-S9] [--only-zone 3] [--dry-run|--yes]`
data: `{"plan":[{"path":..,"zone":3,"host":"github.com"}],"unblocked":N,"skipped":N,"bytesFreed":N,"applied":true,"columns":["path","zone","host"]}`
退出码: 0/2/3/4/6
* 等价于 `Unblock-File`；输出必须回显来源 host（审计价值）。
* **已实现重叠**：v0.1 的 `sign.motw` 已提供单文件查看/移除 → 批量能力优先扩展它（[ROADMAP §1.4 D2](ROADMAP.md)），本条作为其候选实现形态。

### 2.18 `dir.sync` — 目录镜像/同步（robocopy 语义，默认预览）

价值: 高（部署、备份、让两个目录一致） · 复杂度: L · 风险: **D**（可能大量删除）
用法: `dir.sync --src <dir> --dest <dir> [--mirror] [--delete-extra] [--exclude <glob>]... [--include <glob>]... [--compare size-mtime|hash|size] [--bwlimit 0] [--dry-run|--yes] [--max-delete <n>] [--parallel N]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--mirror` | false | `dest` 与 `src` 完全一致（隐式 `--delete-extra`） |
| `--delete-extra` | false | 删除 dest 中 src 不存在的条目 |
| `--compare <mode>` | `size-mtime` | 判定"需同步"的依据；`hash` 最精确但慢 |
| `--seeded-hash` | true | hash 模式先用 size+mtime 快速跳过 |
| `--max-delete <n>` | 100 | 单次删除上限，超过则**中止**（`E_SAFETY`，exit 2），除非 `--force-delete` |
| `--verify` | `size` | 拷贝后校验 |
| `--bwlimit <size/s>` | 0 | 限速（写之间 sleep，粗粒度即可） |
| `--backup-dir <dir>` | 无 | 被覆盖/删除的文件先移到这里（**强烈建议 agent 使用**） |

data: `{"plan":[{"action":"copy|update|delete|mkdir","path":..,"reason":"size differs"}],"copied":N,"updated":N,"deleted":N,"skipped":N,"bytes":N,"failures":[],"applied":true,"txid":..,"columns":["action","path","reason"]}`
退出码: 0/1（比较阶段发现 `dest` 是 `src` 的子目录等致命布局问题）/2/3/4/5/6
闸门: 强制；`--mirror` 时 `--dry-run` 为**默认行为**更好（提示"加 --yes 执行镜像"）。

### 2.19 `dir.compare` — 目录差异（不写盘）

价值: 中高（部署前复核、回答"这两个目录差在哪"） · 复杂度: M · 风险: 无
用法: `dir.compare --src <a> --dest <b> [--compare size-mtime|hash] [--report md|json] [--max-items <n>]`
data: `{"onlyInSrc":[..],"onlyInDest":[..],"different":[{"path":..,"sizeA":..,"sizeB":..,"mtimeA":..,"mtimeB":..}],"identical":N,"columns":["kind","path","detail"]}`
退出码: 0 完全一致 / **1 有差异** / 3 / 4 / 5

### 2.20 `dir.merge` — 合并目录（冲突策略）

价值: 中 · 复杂度: M · 风险: **D**
用法: `dir.merge --src <a> --dest <b> [--on-conflict skip|overwrite|newer|rename] [--dry-run|--yes]`
data: `{"plan":[...],"copied":N,"skipped":N,"overwritten":N,"renamed":N,"failures":[],"applied":true,"txid":..}`

---

## 3. 哈希与完整性

### 3.1 `hash.file` — 单文件/流哈希

价值: 高 · 复杂度: S · 风险: 无
依赖: `Fs.HashFile` / `Fs.SupportedHashes`
用法: `hash.file <path>... [--algo sha256|md5|sha1|sha384|sha512|crc32] [--all] [--stream] [--hmac <key>]`
data: `{"items":[{"path":..,"algo":"sha256","hash":"...","bytes":N,"elapsedMs":N,"hmac":null}],"count":N,"columns":["path","algo","hash"]}`
退出码: 0/3/4/5；`--all` 时每文件回全部算法（`hashes:{}`）
* `--stream`：从 stdin 读（配合管道），`path:"-"`。

### 3.2 `hash.dir` — 目录批量哈希（并发）

价值: 高 · 复杂度: M · 风险: 无
用法: `hash.dir --path <dir> [S0-S9] [--algo sha256] [--dedupe] [--total-yes] [--out <file>]`
data: `{"items":[{"path":..,"size":N,"hash":..}],"count":N,"bytes":N,"uniqueHashes":N,"byHash":{"<h>":[..]},"failures":[],"columns":["path","size","hash"]}`
退出码: 0/3/4/5/6（个别文件读失败进 `failures`）
* 必须并行（`--parallel` 默认 CPU 核，IO 密集时上限 8 更优 —— 建议默认值取 `min(8, cores)` 并允许覆盖）。
* 大目录自动落盘 `Paths.Runs\<runid>.out.json` 并回 `data.artifact`（§14.2），避免 500MB JSON 进 stdout。

### 3.3 `hash.manifest` — 生成校验和清单（sha256sum 兼容）

价值: 高（交付物完整性、可分发到任意机器校验） · 复杂度: S · 风险: 无
用法: `hash.manifest --path <dir> [S0-S9] [--algo sha256] [--out <file>|--stdout] [--format sha256sum|jsonl|csv] [--gnu-binary] [--relative]`
data: `{"outFile":"SHA256SUMS","entries":N,"bytes":N,"format":"sha256sum","algo":"sha256","root":..}`
`--format sha256sum` 输出行格式（**必须逐字节兼容 GNU**）：`<hex><space><space><path>`（文本模式两空格，二进制模式 `<hex><space>*<path>`）
退出码: 0/3/4/5/6
* 换行用 `\n`；路径分隔符用 `/`（GNU 工具跨平台解析）；含换行/`\` 的路径前缀 `\`（GNU 转义规则）。

### 3.4 `hash.check` — 校验清单（`sha256sum -c` 语义）

价值: 高（agent 验收下载物/构建产物） · 复杂度: S · 风险: 无
用法: `hash.check --file <SHA256SUMS> [--base <dir>] [--algo auto|sha256] [--ignore-missing] [--quiet]`
data: `{"ok":false,"checked":N,"okCount":N,"failed":[{"path":..,"expected":..,"actual":..,"reason":"mismatch|missing|size"}],"missing":N,"malformed":N,"columns":["status","path","detail"]}`
退出码: 0 全部通过 / **1 有不通过**（`--ignore-missing` 时缺失不算失败）/ 3 清单文件不存在 / 4 / 5
* 解析必须容忍 CRLF、UTF-8 BOM、`*`/空格两种模式、`\` 转义路径。

### 3.5 `hash.compare` — 两个目录/清单的哈希对比

价值: 中高（"这批文件有没有变过"） · 复杂度: S · 风险: 无
用法: `hash.compare --a <dir|file> --b <dir|file> [--algo sha256] [--by-path]`
data: `{"same":[..],"onlyInA":[..],"onlyInB":[..],"different":[{"path":..,"hashA":..,"hashB":..}],"identical":N}`
退出码: 0 相同 / **1 有差异** / 3 / 4 / 5

### 3.6 `hash.algo` — 列出支持的算法与用法

价值: 低（自描述；`capabilities` 已覆盖但单命令便于发现） · 复杂度: S · 风险: 无
`hash.algo` → data: `{"items":[{"name":"sha256","available":true,"note":"默认"}],"count":6,"columns":["name","available"]}`
（`md5/sha1` 标注 `weak:true`，agent 做安全判断时应被提示。）

---

## 4. 归档

> 技术约束（必须先知道，否则会写出跑不起来的规格）：.NET Framework 4.8 **只有 `System.IO.Compression`
> 的 ZIP 实现**（`ZipArchive`/`ZipFile`，引用已在 `build.ps1` 中）；**没有 `System.Formats.Tar`**（那是 .NET 7+），
> **没有加密 ZIP 读/写**（`ZipArchive` 遇到 AES/ZipCrypto 条目会抛异常）。因此 tar/tar.gz 必须自写（USTAR + PAX 头），
> 加密归档明确不做（ROADMAP §5）。

### 4.1 `archive.list` — 列出归档内容（机器可读）

价值: 高（探归档不落地，agent 决定要不要解） · 复杂度: M · 风险: 无
依赖: `ZipFile.OpenRead`（zip）；新增 `src/Core/Tar.cs`（USTAR/PAX 解析，M）+ `GZipStream`（tgz）
用法: `archive.list <file> [--format auto|zip|tar|tgz|7z|rar] [--include <glob>] [--long] [--max-entries 0]`
data: `{"items":[{"name":"a/b.txt","dir":false,"size":123,"compressedSize":80,"mtime":..,"crc32":"ab12cd34","mode":"-rw-r--r--","encrypted":false,"ratio":0.65}],"count":N,"totalBytes":N,"compressedBytes":N,"format":"zip","encrypted":false,"comment":null,"columns":["name","size","compressedSize"]}`
退出码: 0/3/4/**1**（格式不支持或损坏）/5
* 格式探测：扩展名 + 魔数（`PK\x03\x04`、`PK\x05\x06`），tar 靠 `ustar` 魔数偏移 257，tgz 靠 `1F 8B`。
* `7z/rar`：`--format` 显式给出时返回 `exit 1` + `E_UNSUPPORTED`（不做第三方解码）。

### 4.2 `archive.extract` — 解压（防路径穿越）

价值: 高 · 复杂度: M · 风险: **D**（写盘；zip-slip 安全）
用法: `archive.extract <file> --dest <dir> [--include <glob>]... [--exclude <glob>]... [--overwrite] [--flat] [--strip-components <n>] [--preserve-mtime] [--encoding auto|utf-8|gb18030] [--max-total-bytes 10GB] [--dry-run|--yes]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--dest <dir>` | 必填 | 目标目录（不存在则创建） |
| `--strip-components <n>` | 0 | 去掉前 n 层路径（解 tarball 的常见需求） |
| `--encoding` | `auto` | ZIP 文件名编码：无 UTF-8 标志位时按 CP437/GB18030 猜测（中文老 zip 的核心痛点） |
| `--max-total-bytes` | 10GB | 解压炸弹防护：累计解压字节超限即中止（`E_SAFETY`，exit 1，已解出的进 `extracted`） |
| `--max-ratio <n>` | 200 | 单条目压缩比超限（zip bomb 特征）即中止 |
| `--preserve-mtime` | true | 保留条目时间戳 |

data: `{"plan":[...],"extracted":N,"bytes":N,"skipped":N,"failures":[],"skippedEntries":[{"name":..,"reason":"zip-slip|absolute|symlink|filtered|ratio"}],"applied":true,"txid":..,"columns":["name","size","action"]}`
退出码: 0/1（`E_SAFETY`/损坏）/2（缺 `--yes`）/3/4/6
* **必须实现的安全检查**（逐条写死在代码里）：
  1. `Path.GetFullPath(Path.Combine(dest, entryName))` 必须仍在 `dest` 之下（否则 `zip-slip` 跳过）；
  2. 拒绝绝对路径、盘符路径、`..` 穿越、UNC；
  3. 拒绝符号链接/硬链接条目（`ExternalAttributes` 高位 0xA000/0x8000）默认跳过（`--allow-links` 需 `--yes` + 非管理员警告）；
  4. 目录条目与文件条目一致处理（先建目录）。

### 4.3 `archive.create` — 打包（zip/tar/tgz）

价值: 高（交付、备份、给 agent 一份可搬运的产物） · 复杂度: M · 风险: **D**（写目标文件）
用法: `archive.create --path <dir> --out <file> [--format auto|zip|tar|tgz] [--include <glob>]... [--exclude <glob>]... [S0-S9] [--level optimal|fastest|none] [--store] [--relative-root <dir>] [--strip-components 0] [--dry-run|--yes]`
data: `{"out":..,"format":"zip","entries":N,"bytes":N,"compressedBytes":N,"ratio":0.42,"skipped":N,"applied":true,"sha256":..}`
退出码: 0/2/3/4/6
* zip 用 `ZipArchive`（`CompressionLevel.Optimal` 默认）；tar 用自写 USTAR 写出（长名用 PAX `path` 扩展头）；
  tgz = tar → `GZipStream`（**注意：必须一层流包一层，不能先落 tar 再压缩，除非 `--keep-tar`**）。
* `--store` 只存不压（已压缩素材、加快打包）。

### 4.4 `archive.test` — 完整性测试（CRC 校验）

价值: 中高（下载后先验证再解） · 复杂度: S · 风险: 无
用法: `archive.test <file> [--deep]`
data: `{"ok":true,"entries":N,"crcOk":N,"crcBad":[],"truncated":false,"format":"zip","columns":["name","crcOk"]}`
退出码: 0 通过 / **1 有坏条目或截断** / 3 / 4 / 5
* zip：逐条目读取并比对 CRC（`--deep` 时读全量，否则只读中央目录做结构校验）。
* tar：检查 512 块边界与结尾两个零块；tgz：解压流校验。

### 4.5 `archive.add` / `archive.remove` — 增量修改归档

价值: 中（往交付包里补/删文件，避免整体重打） · 复杂度: M · 风险: **D**
用法: `archive.add <archive> <file>... [--as-name <name>] [--level optimal] [--dry-run|--yes]`
`archive.remove <archive> --include <glob>... [--dry-run|--yes]`
data: `{"archive":..,"added":N|"removed":N,"entries":N,"bytes":N,"applied":true,"txid":..}`
退出码: 0/2/3/4/1（tar 修改 = 重写整个文件，需临时文件 + `File.Replace`）
* **tar 不支持原地修改**：实现为"解到临时目录→改→重写"或"流式复制条目"，必须原子替换并写 undo。

### 4.6 `archive.convert` — 归档格式转换

价值: 中（`zip` ↔ `tar.gz`，跨平台交付） · 复杂度: M · 风险: **D**
用法: `archive.convert <in> --out <out> [--format zip|tar|tgz] [--level] [--dry-run|--yes]`
data: `{"in":..,"out":..,"from":"zip","to":"tgz","entries":N,"bytes":N,"compressedBytes":N,"applied":true}`
退出码: 0/2/3/4/1（含加密/不支持条目）

---
## 5. 文本处理

> 通用约定：文本类命令默认 **UTF-8 无 BOM** 读、`auto` 探测（BOM → UTF-8 严格校验 → GB18030/UTF-16 启发）；
> 写文件一律走 §2.9 的原子写 + undo；行尾默认**保持原样**（`--eol keep`），只有显式指定才改。

### 5.1 `text.enc.detect` — 单文件编码探测

价值: 中高（乱码诊断第一步） · 复杂度: S/M · 风险: 无
依赖: `EncodingDetect`（§1.20 同款）
用法: `text.enc.detect <file>... [--sample 65536] [--confidence]`
data: `{"items":[{"path":..,"encoding":"gb18030","bom":"none","confidence":0.93,"eol":"crlf","isValidUtf8":false,"nonAsciiRatio":0.21,"candidates":[{"enc":"gb18030","score":0.93},{"enc":"cp936","score":0.93}]}],"count":N,"columns":["path","encoding","confidence"]}`
退出码: 0/3/4/5

### 5.2 `text.enc.convert` — 编码/BOM/行尾转换（批量、原子、可回滚）

价值: 高（GBK↔UTF-8 是中文 Windows 上最高频的"救命"操作） · 复杂度: M · 风险: **D**（覆盖原文件）
用法: `text.enc.convert --path <dir> [S0-S9] --to utf-8|utf-8bom|gb18030|utf-16le|utf-16be [--from auto|...] [--eol keep|crlf|lf] [--backup] [--backup-dir <dir>] [--skip-if-same] [--dry-run|--yes]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--to <enc>` | 必填 | 目标编码；`utf-8` 无 BOM，`utf-8bom` 有 BOM |
| `--from <enc>` | `auto` | 源编码；`auto` 用探测，`confidence < --min-confidence`（默认 0.7）时跳过并记 `failures` |
| `--eol <keep\|crlf\|lf>` | `keep` | 行尾转换 |
| `--backup` | **true** | 原文件备份到 `Paths.Home\undo\<txid>\`（保留相对路径结构） |
| `--backup-dir <dir>` | 无 | 覆盖备份位置（agent 想放到项目内时用） |
| `--on-unmappable <fail\|replace\|skip>` | `replace` | 目标编码无法表示的字符：`replace` 用 `?`，`fail` 整文件跳过 |
| `--skip-if-same` | true | 已经是目标编码+行尾则跳过（幂等！） |

data: `{"plan":[{"path":..,"from":"gb18030","to":"utf-8","eol":"crlf→keep","bytes":N}],"converted":N,"skipped":N,"bytesAfter":N,"failures":[],"applied":true,"txid":..,"backupDir":..,"columns":["path","from","to"]}`
退出码: 0/1（`--on-unmappable fail` 且有不可映射且全部失败）/2/3/4/6
闸门: 强制（原地覆盖）。`--dry-run` 只输出 `plan` 与每文件 `from/to/eol`。

### 5.3 `text.eol` — 行尾检查/统一

价值: 中高（git 噪音、跨平台构建失败） · 复杂度: S · 风险: 无（检查）/ D（转换）
用法: `text.eol --path <dir> [S0-S9] [--set lf|crlf] [--mixed-only] [--dry-run|--yes]`
data: `{"items":[{"path":..,"eol":"mixed","crlf":12,"lf":340,"loneCr":0}],"byEol":{"lf":120,"crlf":8,"mixed":3},"columns":["path","eol","crlf","lf"]}`
退出码: 0/2（`--set` 未加 `--yes`）/3/4/6

### 5.4 `text.stats` — 行/词/字符/熵统计（wc 增强）

价值: 中高（快速了解大文本、生成报告数据） · 复杂度: S · 风险: 无
用法: `text.stats <file>... [--words] [--chars] [--entropy] [--longest-line] [--empty-lines] [--jsonl]`
data: `{"items":[{"path":..,"lines":1204,"words":8342,"chars":45210,"bytes":46000,"emptyLines":88,"longestLine":312,"entropy":4.62,"encoding":"utf-8","eol":"crlf"}],"count":N,"total":{"lines":N,"words":N,"bytes":N},"columns":["path","lines","words","bytes"]}`
退出码: 0/3/4/5
* 用流式读取（`StreamReader` + 固定缓冲），**不要** `ReadAllText`（可能 2GB）。

### 5.5 `text.head` / `text.tail` — 取头/尾 N 行

价值: 高（看日志尾部、看文件开头判断格式） · 复杂度: S · 风险: 无
用法: `text.head <file> [-n 20|--lines 20] [--bytes <n>] [--encoding auto]`
`text.tail <file> [-n 20] [--follow] [--follow-interval 500ms] [--max-events 0]`
data: `{"path":..,"totalBytes":N,"lines":[{"n":1180,"text":"..."}],"eol":"lf","encoding":"utf-8","followed":false,"columns":["n","text"]}`
退出码: 0/3/4/5（`--follow` 时超时 → 5）
* `--follow` 用"记住 offset + 轮询增长 + 处理文件被 rotate（句柄失效则重开）"实现，禁止 busy-loop（间隔 ≥ 200ms）。
  长跑请配合 `job start`（§14.9）。

### 5.6 `text.slice` — 按行区间取内容

价值: 中高（agent 精读大文件的某一节） · 复杂度: S · 风险: 无
用法: `text.slice <file> --lines 100-200 [--line-numbers] [--encoding auto] [--out <file>]`
data: `{"path":..,"from":100,"to":200,"returned":101,"text":"...","lines":[{"n":100,"text":..}],"columns":["n","text"]}`
退出码: 0/3/4/5；区间越界自动收敛（`clamped:true`），**不报错**

### 5.7 `text.replace` — 文本替换（正则/字面量，可计数、可预览）

价值: 高（批量改配置、修脚本） · 复杂度: M · 风险: **D**（写文件）
用法: `text.replace --path <dir> [S0-S9] --pattern <re> --to <replacement> [--literal] [--ignore-case] [--multiline] [--max-count 0] [--encoding auto] [--eol keep] [--dry-run|--yes] [--diff]`
data: `{"plan":[{"path":..,"replacements":12,"preview":[{"line":118,"before":..,"after":..}]}],"filesChanged":N,"replacements":N,"failures":[],"applied":true,"txid":..,"columns":["path","replacements"]}`
退出码: 0（含 0 命中）/2/3/4/6
* `--diff`：先输出 unified diff（`data.unifiedDiff`）再执行；**建议 agent 总是先 `--dry-run --diff`**。
* 正则超时必须设（`TimeSpan` 参数），超时文件进 `failures` 不中断整批。

### 5.8 `text.dedupe` — 行去重（保序/排序）

价值: 中高（清洗列表、日志去重） · 复杂度: S · 风险: 无（stdout）/ D（`--in-place`）
用法: `text.dedupe <file> [--in-place] [--ignore-case] [--trim] [--count] [--keep first|last] [--drop-empty] [--dry-run|--yes]`
data: `{"path":..,"linesIn":N,"linesOut":N,"removed":N,"duplicates":[{"text":..,"count":3}],"applied":false,"columns":["text","count"]}`
退出码: 0/2（`--in-place` 未确认）/3/4

### 5.9 `text.sort` — 行排序（字段/数值/自然序）

价值: 中（生成稳定输出、比对前归一化） · 复杂度: M（自然序/多键） · 风险: 无（stdout）/ D（`--in-place`）
用法: `text.sort <file> [--by 1|1,2|--field-sep ,] [--numeric] [--natural] [--reverse] [--ignore-case] [--unique] [--stable] [--in-place] [--dry-run|--yes]`
data: `{"path":..,"linesIn":N,"linesOut":N,"unique":N,"applied":false}`
* 内存策略：文件 > `--max-memory`（默认 64MB）时用**外部归并排序**（分块排序 → 临时文件 → 归并），实现 M。

### 5.10 `text.uniq` — 连续重复行压缩（`uniq` 语义）

价值: 中（日志压缩、时序去抖） · 复杂度: S · 风险: 无/D
用法: `text.uniq <file> [--count] [--repeated-only] [--unique-only] [--ignore-case] [--in-place] [--dry-run|--yes]`
data: `{"path":..,"groups":[{"text":..,"count":4,"firstLine":12,"lastLine":15}],"linesIn":N,"linesOut":N}`

### 5.11 `text.normalize` — 文本归一化

价值: 中高（Unicode 一致性、去零宽字符、去 BOM、折叠空白 —— 处理"看起来一样但不相等"的坑） · 复杂度: M · 风险: **D**（`--in-place`）
用法: `text.normalize <file>... [--form nfc|nfd|nfkc|nfkd] [--strip-bom] [--strip-zero-width] [--collapse-whitespace] [--trim-lines] [--tabs-to-spaces 0] [--strip-control] [--in-place] [--dry-run|--yes]`
data: `{"items":[{"path":..,"changed":true,"replacementsN":3,"removedChars":12}],"count":N,"applied":false,"columns":["path","changed","removedChars"]}`

### 5.12 `text.diff` — 文本差异（unified diff）

价值: 高（agent 复核改动、生成评审意见） · 复杂度: M（LCS/Myers） · 风险: 无
用法: `text.diff <a> <b> [--context 3] [--ignore-case] [--ignore-whitespace] [--ignore-eol] [--word-diff] [--stat] [--color] [--max-lines 200000]`
data: `{"same":false,"hunks":[{"aStart":10,"aCount":4,"bStart":10,"bCount":5,"lines":[{"kind":" ","text":..},{"kind":"-","text":..},{"kind":"+","text":..}]}],"added":12,"removed":8,"unifiedDiff":"--- a\n+++ b\n@@ ...","stat":{"a":..,"b":..},"columns":["kind","text"]}`
退出码: 0 相同 / **1 有差异** / 3 / 4 / 5
* 大文件：`--max-lines` 超出时退化为"按分块哈希对齐 + 仅报告变更块"（`mode:"blockwise"`），避免 O(n²) 内存。

### 5.13 `text.patch` — 应用 unified diff（v0.3）

价值: 中（agent 想用 diff 描述改动而不是重写整文件） · 复杂度: L · 风险: **D**
用法: `text.patch --path <dir> --patch <file> [--strip 1] [--reverse] [--fuzz 2] [--dry-run|--yes] [--check]`
data: `{"applied":false,"hunks":N,"hunksApplied":N,"rejected":[],"files":[...],"txid":..}`
退出码: 0/1（hunk 无法定位）/2/3/4/6

### 5.14 `text.template` — 极简模板渲染（JSON 数据 → 文本）

价值: 高（agent 生成报告/配置/脚本体，避免在字符串里手拼） · 复杂度: M · 风险: 无（stdout）/ D（`--out`）
用法: `text.template --template <file|string> [--data <file|json string>] [--data-file <f>]... [--out <file>] [--missing keep|empty|error|fail] [--strict]`
语法（自研，明确子集，**不做函数/循环嵌套**）：
`{{path.to.value}}` 取值；`{{#list}}...{{/list}}` 迭代（`{{.}}` 当前项）；`{{^key}}...{{/key}}` 取反段；
`{{key|upper}}` 过滤器（`upper lower trim json base64 url md5 sha256 date:yyyyMMdd default:x`）；`{{{raw}}}` 不转义。
data: `{"outFile":..,"bytes":N,"variables":[...],"missing":[..],"rendered":"..."}`
退出码: 0 / 1（`--strict` 且缺变量）/ 2（模板语法错）/ 3 / 4

### 5.15 `text.find-lines` — 行级查找（不做全文正则时的轻量版）

价值: 中（比 `scan content` 简单：已知文件、只看行） · 复杂度: S · 风险: 无
用法: `text.find-lines <file> --pattern <re> [--ignore-case] [--invert] [--context 2] [-n] [--max 200]`
data: `{"path":..,"items":[{"n":118,"text":..,"context":["..."]}],"count":N,"columns":["n","text"]}`
退出码: 0 有命中 / **1 无命中**（`--exit-code` 时）/ 3 / 4

### 5.16 `text.join` — 合并多文件为一个

价值: 中（拼日志、拼分片） · 复杂度: S · 风险: **D**
用法: `text.join --path <dir> [S0-S9] --out <file> [--ensure-newline] [--header-comment "# {name}"] [--separator "\n---\n"] [--dry-run|--yes]`
data: `{"out":..,"sources":N,"bytes":N,"applied":true,"sha256":..}`

---

## 6. 结构化数据

> 技术约束：**没有 System.Text.Json**（禁止），JSON 解析用现有 `Json.Parse`（`JavaScriptSerializer`，已设 `MaxJsonLength=int.MaxValue`、`RecursionLimit=256`）。
> 因此 **JSON 数字类型**会退化为 `int`/`decimal`/`double`，规格里所有整数比较必须显式 `Convert.ToInt64`。
> 没有 YAML 库：`data.yaml` 只做**文档化子集**（见 6.13）。

### 6.1 `data.json.get` — JSON 取值（路径查询）

价值: 高（agent 解析任意 JSON 输出的主力） · 复杂度: M · 风险: 无
用法: `data.json.get <file|-> --path <expr> [--default <json>] [--raw] [--first] [--all] [--json-pointer]`
路径语法（**两种都支持**，默认点号）：
* 点号/方括号：`a.b[0].c`、`items[*].path`、`items[?name=foo].path`（`?key=value` 简单谓词，只支持 `= != ~`）
* JSON Pointer：`--json-pointer` 时按 RFC 6901（`/a/b/0`，`~0`/`~1` 转义）
data: `{"expr":..,"found":true,"matches":N,"value":<任意>,"values":[..],"type":"array|object|string|number|bool|null","columns":[]}`
退出码: 0 命中 / **3 路径不存在且未给 `--default`** / 1 JSON 非法 / 4
* `--raw`：字符串值直接输出原文到 stdout（人类模式便捷）；JSON 模式下仍是信封。
* 输入 `-` 表示 stdin；`--default` 命中不到时返回该值并 `found:false`。

### 6.2 `data.json.validate` — JSON 校验（含行/列）

价值: 高（agent 确认自己/上游产物是否合法） · 复杂度: M · 风险: 无
用法: `data.json.validate <file|-> [--strict] [--max-depth 256] [--jsonl] [--require-keys a,b] [--no-duplicate-keys]`
data: `{"valid":false,"error":{"message":"Invalid object passed in, ':' or '}' expected. (12)","line":12,"column":31,"charIndex":245,"snippet":"...这里 ^ ..."},"objects":N,"maxDepth":6,"duplicateKeys":["a"]}`
退出码: 0 合法 / **1 非法** / 3 / 4 / 5
* `--jsonl`：逐行校验，`data.items` 每行 `{line,valid,error}`，**首错不中断**（大日志文件友好）。
* 行/列定位：`JavaScriptSerializer` 不报行列 → 用**自研 tokenizer**（只做结构扫描，M）或从异常的 `(N)` 位置反推字符偏移再换算行列
  （推荐后者：S，`ex.Message` 里 JavaScriptSerializer 会带 `(N)` 偏移）。

### 6.3 `data.json.fmt` — 格式化/压缩/键排序

价值: 中高（人类可读、稳定 diff） · 复杂度: S · 风险: 无（stdout）/ D（`--in-place`）
用法: `data.json.fmt <file|-> [--indent 2] [--minify] [--sort-keys] [--ascii] [--in-place] [--eol lf|crlf] [--dry-run|--yes]`
data: `{"path":..,"bytesBefore":N,"bytesAfter":N,"indent":2,"sorted":true,"applied":false}`
* 输出用现有 `Json.Write(value, pretty:true)`；但 `Json.Obj` 底层是 `Dictionary`（**顺序不保证**），
  需要保序时必须走 `Json.Ordered`/`JsonObject` 重建（实现要点，别指望 Dictionary 顺序）。

### 6.4 `data.json.query` — 列表筛选/投影（JSON/JSONL 通用）

价值: 高（从 `scan`/`proc` 结果里挑字段，替代手写脚本） · 复杂度: M · 风险: 无
用法: `data.json.query <file|-> [--path <expr>] [--select a,b,c] [--where "<pred>"...] [--order-by key[:desc]] [--limit 50] [--distinct-by key] [--count-only] [--group-by key]`
谓词语法（**受限 DSL，不是 jq**）：`field=value`、`field!=value`、`field~regex`、`field>num`、`field>=num`、`size<1000`；
多谓词 AND；`--or` 切换为 OR。字段名支持 `a.b`。
data: `{"items":[...],"count":N,"scanned":N,"matched":N,"groups":[{"key":..,"count":N,"sum":N}]}`
退出码: 0 有结果 / **1 无匹配**（`--exit-code`）/ 3（路径不存在）/ 1（JSON 非法）
* 大 JSONL 必须**流式**处理（逐行 parse），不要整体载入。

### 6.5 `data.json.set` — 写入/修改 JSON 字段（原子 + 可回滚）

价值: 中高（改配置文件、改 package.json 版本号） · 复杂度: M · 风险: **D**
用法: `data.json.set <file> --path <expr> --value <json|string> [--type auto|string|number|bool|null|json] [--indent 2] [--create-missing] [--append] [--backup] [--dry-run|--yes]`
data: `{"path":..,"expr":..,"before":..,"after":..,"created":false,"applied":true,"txid":..,"bytesBefore":N,"bytesAfter":N}`
退出码: 0/1（JSON 非法）/2/3/4
闸门: 强制。`--dry-run` 输出 `before/after` 与 diff 预览。

### 6.6 `data.json.merge` — 合并 JSON（深/浅）

价值: 中（合并配置、多个结果集） · 复杂度: M · 风险: 无/D（`--out` 覆盖）
用法: `data.json.merge <a> <b>... [--deep] [--array-mode concat|replace|union-by key] [--on-conflict a|b|fail] [--out <file>] [--dry-run|--yes]`
data: `{"merged":true,"sources":N,"keysAdded":N,"keysOverwritten":N,"conflicts":[],"result":{...},"outFile":..}`

### 6.7 `data.json.diff` — 结构化差异

价值: 中高（配置漂移检测、API 响应对比） · 复杂度: M · 风险: 无
用法: `data.json.diff <a> <b> [--ignore-order] [--max-depth 0] [--ignore-keys <list>]`
data: `{"same":false,"added":[{"path":"$.a.b","value":..}],"removed":[..],"changed":[{"path":..,"from":..,"to":..}],"arraysCompared":{"items":{"added":2,"removed":1}},"count":N,"columns":["kind","path","detail"]}`
退出码: 0 相同 / **1 有差异** / 3 / 1（JSON 非法）

### 6.8 `data.json.schema` — 从样本推断 schema（自描述）

价值: 中高（agent 拿到陌生 JSON 先摸结构，再决定 query 路径） · 复杂度: M · 风险: 无
用法: `data.json.schema <file|-> [--sample 100] [--title <s>] [--draft 2020-12] [--required-threshold 1.0] [--out <file>]`
data: `{"schema":{"type":"object","properties":{"ok":{"type":"boolean"},"data":{"type":"object","properties":{...}},"warnings":{"type":"array","items":{"type":"string"}}}},"required":["ok","cmd"],"samples":100,"fields":N}`
* 合并规则：数组取元素类型并集（冲突 → `anyOf`）；数字区分 `integer`/`number`；出现的键按 `required-threshold` 决定是否 required。
* 输出可直接喂给 `capabilities`/`commands.schema` 的 `outputSchema`（§14.4）。

### 6.9 `data.json.to-jsonl` / `data.jsonl.to-json` — 互转

价值: 中高（在流式工具与信封之间搭桥） · 复杂度: S · 风险: 无/D（`--out`）
用法: `data.json.to-jsonl <file> [--path items] [--out <file>] [--max-items 0]`
`data.jsonl.to-json <file> [--array] [--out <file>] [--pretty]`
data: `to-jsonl`: `{"outFile":..,"items":N,"bytes":N}`；`to-jsonl` 在 `--jsonl` 模式下直接流式输出
退出码: 0/1（某行非法 → 记 failures，返回 6）/3/4

### 6.10 `data.csv.info` — CSV 方言探测

价值: 中高（CSV 最大的坑是方言，不是解析） · 复杂度: M · 风险: 无
用法: `data.csv.info <file> [--sample 200] [--encoding auto]`
data: `{"delimiter":",","quote":"\"","escape":"\"\"","hasHeader":true,"columns":["a","b"],"rows":1204,"ragged":0,"encoding":"utf-8","bom":"none","eol":"crlf","sniffConfidence":0.98}`
* 探测：候选分隔符 `, ; \t |`，按每行字段数一致性评分；引号内换行与 `""` 转义必须正确处理。

### 6.11 `data.csv.query` — CSV 查询（select/where/order/limit/聚合）

价值: 中高（agent 处理报表/日志表，无需 SQLite） · 复杂度: L · 风险: 无/D（`--out`）
用法: `data.csv.query <file> [--delimiter auto|,] [--header] [--select a,b,c] [--where "col=value"...] [--order-by col[:desc]] [--limit 50] [--group-by col] [--agg "count,sum:amount,avg:qty"] [--no-header] [--encoding auto] [--out <file>] [--json]`
data: `{"items":[{"a":"1","b":"x"}],"count":N,"scanned":N,"columns":["a","b"],"groups":[{"key":..,"count":N,"sum_amount":12.5}],"dialect":{"delimiter":",","hasHeader":true}}`
退出码: 0 / 1（列名不存在）/ 3 / 4 / 6（个别行字段数不符 → `failures`）
* 解析器自写（RFC 4180 + 容错：允许行尾多余/缺失字段，记 `raggedRows`）。**不要**用 `Microsoft.VisualBasic.FileIO.TextFieldParser`（需要额外引用 Microsoft.VisualBasic.dll，Lead 未在 build.ps1 引用）。

### 6.12 `data.csv.to-json` / `data.json.to-csv` — CSV ↔ JSON

价值: 中高 · 复杂度: S · 风险: 无/D（`--out`）
用法: `data.csv.to-json <file> [--header a,b] [--infer-types] [--out <file>] [--jsonl]`
`data.json.to-csv <file|-> [--path items] [--columns a,b] [--delimiter ,] [--always-quote] [--out <file>] [--utf8-bom]`
data: `{"outFile":..,"rows":N,"columns":[..],"bytes":N}`
* `--utf8-bom` 必给：Excel 打开无 BOM 的 UTF-8 CSV 会乱码（中文场景刚需）。

### 6.13 `data.yaml.get` — YAML 子集读取（**明确受限**）

价值: 中高（配置/CI 文件多为 YAML，agent 常常只需要取几个键） · 复杂度: M（子集解析器）/ L（完整） · 风险: 无
用法: `data.yaml.get <file> --path <expr> [--default x] [--strict-support]`
**支持子集（文档化，测试用例覆盖）**：块映射/块序列、`- ` 项、缩进（2/4 空格，禁止 tab）、单/双引号标量、`|`/`>` 块标量（折叠）、行内 `[a, b]`/`{a: b}`（单层）、注释 `#`、`---` 单文档、布尔/数字/null 类型推断。
**不支持（返回 `exit 1` + `E_UNSUPPORTED`，并在 hint 里指出行号）**：锚点/别名（`&`/`*`）、多文档合并（`<<`）、标签（`!!`）、复杂流式嵌套、tab 缩进。
data: `{"path":..,"value":..,"found":true,"unsupported":[],"documents":1,"notes":["用于 line 12 的折叠标量"]}`
退出码: 0/1（不支持或语法错，含 `line`）/3/4

### 6.14 `data.xml.xpath` — XML 查询

价值: 中高（config/csproj/MSBuild/plist/AndroidManifest） · 复杂度: S/M · 风险: 无
依赖: `System.Xml.XPath`（引用 `System.Xml.dll`、`System.Xml.Linq.dll` 已在 build.ps1）
用法: `data.xml.xpath <file> --xpath <expr> [--attr <name>] [--text] [--ns <prefix=uri>]... [--default-ns <uri>] [--out <file>]`
data: `{"expr":..,"found":true,"matches":N,"items":[{"name":"PropertyGroup","value":"...","attrs":{"Condition":"..."},"line":12}],"columns":["name","value"]}`
退出码: 0 命中 / **3 无命中且未给 default** / 1（XML 非法，含行列）/ 4
* **必须禁用 DTD 与外部实体**（`XmlReaderSettings { DtdProcessing = Prohibit, XmlResolver = null }`）——防 XXE。
* `--ns` 支持前缀映射；默认命名空间用 `--default-ns` 注入。

### 6.15 `data.xml.validate` — XML 合法性/结构检查

价值: 中（构建文件坏了的快速定位） · 复杂度: S · 风险: 无
用法: `data.xml.validate <file> [--xsd <file>] [--max-errors 20]`
data: `{"valid":false,"errors":[{"line":12,"column":4,"message":"...","severity":"error"}],"elements":120,"depth":6}`
退出码: 0 合法 / **1 非法** / 3 / 4

### 6.16 `data.ini.get` / `data.ini.set` — INI 读写

价值: 中（大量 Windows 工具/游戏/老配置用 INI） · 复杂度: S · 风险: 无/D（`--set`）
用法: `data.ini.get <file> [--section <s>] [--key <k>] [--list-sections]`
`data.ini.set <file> --section <s> --key <k> --value <v> [--create] [--dry-run|--yes]`
data: `get`: `{"sections":[{"name":"General","keys":[{"key":"path","value":"C:\\x"}]}],"count":N,"columns":["section","key","value"]}`
`set`: `{"path":..,"section":..,"key":..,"before":..,"after":..,"created":false,"applied":true,"txid":..}`
* 保留注释与顺序（不要用 `WritePrivateProfileString`，它会重排文件）；自写行级编辑器（S）。

### 6.17 `data.env-file` — `.env` 读写（dotenv）

价值: 中高（项目配置、给子进程注入变量） · 复杂度: S · 风险: P（值可能是密钥）/ D（写）
用法: `data.env-file get <file> [--key <k>] [--show-secrets] [--export]`
`data.env-file set <file> --key <k> --value <v> [--quote auto] [--dry-run|--yes]`
data: `{"items":[{"key":"API_KEY","value":"sk-****","redacted":true,"line":3,"quoted":false,"comment":null}],"count":N,"columns":["key","value"]}`
* 默认脱敏规则复用 `env` 命令的 `secretRe`（token/secret/password/key/credential/auth）。

### 6.18 `data.jsonl.stats` — JSONL 结构统计

价值: 中高（分析 `scan find --jsonl`/`runs.jsonl`/日志） · 复杂度: M · 风险: 无
用法: `data.jsonl.stats <file> [--sample 10000] [--fields] [--top path:20] [--bad-lines]`
data: `{"lines":N,"valid":N,"invalid":[],"fields":[{"name":"type","count":N,"types":["string"],"topValues":{"item":N}}],"sizeBytes":N,"columns":["field","count","types"]}`
退出码: 0/1（存在非法行 → 仍返回 0，非法行进 `invalid`；`--strict` 时返回 1）/3/4

---
## 7. 编解码与转换（agent 每次要写一行脚本的地方）

> 全部为 S 级、纯 BCL。统一形态：`codec.<name> <action|值>`，默认从位置参数或 `--stdin` 取值，结果进 `data`；
> 人类模式打印结果行（方便 shell 直接抓）。`--decode` / `--encode` 是各命令的主开关。

### 7.1 `codec.base64` — Base64 编解码

价值: 高（处理二进制内联、JWT、证书、curl 结果） · 复杂度: S · 风险: 无
用法: `codec.base64 [--encode|--decode] [--input <text>|--stdin|--file <path>] [--url-safe] [--no-padding] [--wrap 76] [--out <file>]`
data: `{"in":{"bytes":12,"kind":"text"},"out":{"text":"aGVsbG8=","bytes":8,"urlSafe":false},"roundTripOk":true}`
退出码: 0/1（非法 Base64）/2（既没输入也没 `--stdin`）/3/4
* `--wrap` 默认 0（不换行）；`--no-padding` 与 `--url-safe` 组合覆盖 JWT 的 base64url。

### 7.2 `codec.hex` — 十六进制编解码/转储

价值: 高（读二进制头、构造字节串） · 复杂度: S · 风险: 无
用法: `codec.hex [--encode|--decode] [--input|--stdin|--file] [--dump] [--offset 0] [--length 64] [--uppercase] [--spaces] [--out <file>]`
data: `{"out":{"hex":"4d5a9000","bytes":4},"dump":"00000000  4D 5A 90 00 03 00 00 00  ...  MZ......"}`
退出码: 0/1/2/3/4

### 7.3 `codec.url` — URL 编解码（组件级/整串）

价值: 高（构造查询串、解析重定向链接） · 复杂度: S · 风险: 无
用法: `codec.url [--encode|--decode] [--input <s>] [--component|--full] [--query <k=v>...] [--parse] [--decode-plus]`
data: `parse`: `{"scheme":"https","host":"x.com","port":443,"path":"/a b","query":{"q":"1"},"fragment":null,"userInfo":null,"isAbsolute":true,"percentEncoded":["%20"]}`
退出码: 0/1（非法百分号编码）/2

### 7.4 `codec.html` — HTML 实体编解码

价值: 中（抓取/生成 HTML 片段） · 复杂度: S · 风险: 无
用法: `codec.html [--encode|--decode] [--input|--stdin] [--named|--numeric]`
data: `{"out":{"text":"&lt;a&gt;"},"replacements":2}`（解码用 `WebUtility.HtmlDecode`，在 `System.dll` 内）

### 7.5 `codec.jwt` — JWT 解析与签名校验

价值: 高（调试鉴权失败、检查过期） · 复杂度: S/M · 风险: P（含身份信息）
用法: `codec.jwt <token> [--verify --secret <s>|--public-key <pem>] [--algo auto|HS256|RS256] [--leeway 60s] [--show-secrets]`
data: `{"header":{"alg":"HS256","typ":"JWT"},"payload":{"sub":"123","exp":1760000000,"expLocal":"2025-10-09 13:33:20","expired":false},"signature":{"present":true,"verified":true,"algo":"HS256","reason":null},"leewaySec":60,"columns":["claim","value"]}`
退出码: 0 / **1 签名校验失败或已过期**（`--verify` 时）/ 3（格式非法）/ 4
* HS256 用 `HMACSHA256`；RS256 用 `System.Security.Cryptography` 的 `RSACryptoServiceProvider` + PEM 解析（M，含 PKCS#1/PKCS#8）。
* **默认不打印 payload 里疑似敏感的键**（`sub` 之外不做脱敏，但 `--show-secrets` 控制 `secret`/`key` 类字段）。

### 7.6 `codec.guid` — GUID/UUID 生成与转换

价值: 中（生成标识符、解析用户给的 id） · 复杂度: S · 风险: 无
用法: `codec.guid [--count 1] [--format D|N|B|P] [--upper] [--nil] [--parse <s>] [--short <n>] [--seeded]`
data: `{"items":[{"value":"9f2a...","format":"D","version":4}],"count":N,"columns":["value"]}`
* `--short <n>`：截取随机字节转 base32/base62 短 id（用于 txid/jobid 的人类友好 id，**不作为安全 token**）。

### 7.7 `codec.time` — 时间/时区/时长转换

价值: 高（agent 到处遇到 epoch/ISO/本地时间） · 复杂度: S/M（时区库用系统 `TimeZoneInfo`） · 风险: 无
用法: `codec.time [<value>] [--to epoch|iso|local|utc|filetime|rfc2822] [--from auto|epoch|epoch-ms|filetime|iso] [--tz <iana|windows id>] [--add 2d] [--diff <other>] [--now] [--format <pattern>]`
data: `{"in":{"raw":"1760000000","kind":"epoch-seconds"},"out":{"iso":"2025-10-09T13:33:20.000+08:00","utc":"2025-10-09T05:33:20.000Z","epoch":1760000000,"epochMs":1760000000000,"filetime":133800000000000000,"weekday":"Thursday","isoWeek":"2025-W41","tz":"China Standard Time","utcOffset":"+08:00"},"diffSec":null}`
退出码: 0/1（无法解析 → `E_USAGE`? 用 2）/2/4

### 7.8 `codec.binary` — 数值位模式/字节序转换

价值: 中（读 PE/协议头里的整数、理解浮点二进制） · 复杂度: S · 风险: 无
用法: `codec.binary [<value>] [--from dec|hex|bin|float|bytes] [--to dec|hex|bin|bytes|int8|int16|int32|int64|uint32|float|double] [--endian le|be] [--signed] [--bits 32]`
data: `{"in":..,"out":{"hex":"0x00000080","dec":128,"bytes":"80 00 00 00","endian":"le","bits":32},"bitPattern":"00000000 00000000 00000000 10000000"}`
退出码: 0/2（数值超出位宽）

### 7.9 `codec.escape` — 字符串转义

价值: 中（生成 C#/JSON/正则字面量） · 复杂度: S · 风险: 无
用法: `codec.escape [--input|--stdin] [--mode csharp|json|regex|powershell|xml|bash] [--unescape] [--quote]`
data: `{"mode":"csharp","out":"\"C:\\\\path\\n\"","escapes":3}`
退出码: 0/2

---

## 8. 进程与线程

> 实现要点：`Process` 类能拿到的信息有限，且**跨位数访问受限**。本 exe 是 `anycpu`（64 位系统上跑 64 位），
> 因此访问同位数进程没问题；32 位进程的模块/路径可能需 `--via wmi`。
> `proc.kill` 的"整树终止"在 .NET FW 上**没有** `Kill(entireProcessTree)`（那是 Core 3.0+），
> 必须用 WMI `Win32_Process.ParentProcessId` 建树后叶子优先终止。

### 8.1 `proc.list` — 进程列表

价值: 高（agent 的基础态势感知） · 复杂度: M · 风险: 无
依赖: `Process.GetProcesses` + WMI `Win32_Process`（cmdline/parent）
用法: `proc.list [--name <regex>] [--pid <n>]... [--user <name>] [--sort cpu|mem|pid|name|start] [--top 30] [--tree] [--cmdline] [--owners]`
data: `{"items":[{"pid":1234,"ppid":1200,"name":"node.exe","path":"C:\\...\\node.exe","cmdline":"node index.js","user":"PC\\me","sessionId":1,"startTime":..,"cpuSec":12.4,"cpuPercent":3.2,"workingSet":123456789,"privateBytes":..,"threads":12,"handles":340,"responding":true,"elevated":false,"company":"Node.js","version":"20.11.0"}],"count":N,"totalProcesses":N,"columns":["pid","name","cpuPercent","workingSet"]}`
退出码: 0/3（`--pid` 指定但不存在）/4（`--owners` 且全部取不到属主）/5
* `--sort cpu` 需要**两次采样**（间隔 `--sample-interval`，默认 500ms）才能算百分比；不采样时只给 `cpuSec`（累计）。
* `--tree`：按 `ppid` 构建，输出 `depth` 字段供人类模式缩进。
* `workingSet`/`privateBytes` 单位 **字节**（整数），人类模式由 `Fs.FormatSize` 渲染。

### 8.2 `proc.tree` — 进程树

价值: 中高（找到"是谁拉起的这个进程"） · 复杂度: M · 风险: 无
用法: `proc.tree [--root <pid|name>] [--cmdline] [--format text|json|md] [--depth 0]`
data: `{"roots":[{"pid":4,"name":"System","children":[...]}],"count":N,"orphans":[{"pid":..,"ppid":..}],"columns":["pid","name","cmdline"]}`
退出码: 0/3（root 不存在）

### 8.3 `proc.find` — 按条件找进程（按端口/路径/命令行/句柄）

价值: 高（"谁占用了 3000 端口"、"谁在用这个 dll"） · 复杂度: M · 风险: 无
用法: `proc.find [--port <n>] [--path <glob>] [--cmdline <re>] [--module <name|glob>] [--user <u>] [--socket <local|remote>]`
data: `{"items":[{"pid":..,"name":..,"match":"port 3000 listening","detail":"0.0.0.0:3000","cmdline":..}],"count":N,"columns":["pid","name","match","detail"]}`
退出码: 0（含 0 命中）/ 3（`--port` 无任何匹配时**返回 3**，方便 agent 判断"端口空闲"）/ 4 / 5
* `--port` 实现：`GetExtendedTcpTable`/`GetExtendedUdpTable`（P/Invoke `iphlpapi.dll`）一次拿到 `(协议, 本地/远端地址, 状态, PID)`；
  **不要**解析 `netstat -ano` 的输出（本地化列名会变），但可作为 `--via netstat` 的降级实现（S，纯解析）。
* `--module`：`Process.Modules`（对同位数进程有效；失败进 `failures` 并提示 `--via wmi`）。

### 8.4 `proc.kill` — 终止进程（含整树）

价值: 高（清理挂死进程、重启服务前必备） · 复杂度: M · 风险: **D** + A（其他用户/系统进程需提权）
用法: `proc.kill --pid <n>... [--name <regex>] [--tree] [--force] [--graceful 5s] [--exit-code] [--dry-run|--yes]`
data: `{"plan":[{"pid":..,"name":..,"signal":"CloseMainWindow|Kill","tree":true}],"killed":[{"pid":..,"exitCode":-1,"method":"kill","elapsedMs":23}],"survivors":[],"failures":[],"applied":true,"txid":..,"columns":["pid","name","method"]}`
退出码: 0/2/3/4/6/5
闸门: 强制。`--graceful <dur>`：先 `CloseMainWindow()`（GUI 程序）等待，超时再 `Kill()`；`--force` 跳过优雅阶段。
* **自我保护**：拒绝 kill 当前 exe 的 PID、当前进程的祖先链（防 agent 把自己或 shell 干掉）；
  需要时给出 `--allow-self` 显式开关（默认关闭，返回 2 并解释）。
* `--exit-code`：`Kill()` 后等 `WaitForExit` 读 `ExitCode`（需要句柄权限，失败则 `exitCode:null`）。

### 8.5 `proc.wait` — 等待进程结束/启动

价值: 中高（编排：等安装器结束、等构建完成） · 复杂度: M · 风险: 无
用法: `proc.wait (--pid <n>|--name <regex>) [--for exit|start] [--timeout 5m] [--poll 500ms] [--exit-code]`
data: `{"pid":..,"name":..,"reached":"exit","exitCode":0,"waitedMs":12345,"stillRunning":false}`
退出码: 0（等到）/ **1**（进程已不存在，`--for start` 时的常见分支）/ 3（`--pid` 不存在）/ 5（超时，`stillRunning:true`）
* `--for exit` 用 `Process.WaitForExit(timeout)`（比轮询省 CPU）；`--for start` 用轮询（默认 500ms，可 `--timeout`）。

### 8.6 `proc.port` — 端口 ↔ 进程双向查询（语法糖）

价值: 中高（`proc.find --port` 的专用入口，降低 agent 试错） · 复杂度: S · 风险: 无
用法: `proc.port <n>... [--proto tcp|udp|both] [--state listen|established|all] [--remote <host>]`
data: `{"items":[{"proto":"tcp","local":"0.0.0.0:3000","remote":"0.0.0.0:0","state":"Listen","pid":1234,"name":"node.exe","path":..}],"count":N,"columns":["proto","local","state","pid","name"]}`

### 8.7 `proc.modules` — 进程模块（DLL）列表

价值: 中高（排查 dll 劫持/版本冲突） · 复杂度: M · 风险: A（部分进程/受保护进程）
用法: `proc.modules --pid <n> [--name <regex>] [--sort size] [--path <glob>] [--via auto|process|wmi|psapi]`
data: `{"pid":..,"items":[{"name":"ntdll.dll","path":"C:\\Windows\\System32\\ntdll.dll","base":"0x7ff...","size":2097152,"version":"10.0.19041.1","company":"Microsoft"}],"count":N,"failures":[],"columns":["name","path","size","version"]}`
退出码: 0/3/4/6
* 32 位目标进程从 64 位进程枚举模块会失败 → 提示 `--via wmi` 或说明"需要 /platform:x86 版本"。

### 8.8 `proc.threads` — 线程列表

价值: 中（卡死诊断、CPU 定位） · 复杂度: M · 风险: A
用法: `proc.threads --pid <n> [--top 20] [--sort cpu|start] [--wait-reason] [--start-address]`
data: `{"pid":..,"items":[{"tid":1234,"state":"Wait","waitReason":"UserRequest","startTime":..,"cpuSec":1.2,"priority":8,"startAddress":"0x7ff..."}],"count":N,"columns":["tid","state","waitReason","cpuSec"]}`
退出码: 0/3/4/5

### 8.9 `proc.env` — 读取目标进程的环境变量（v0.3，需提权）

价值: 中高（复现子进程环境差异、"这个服务用什么代理"） · 复杂度: L · 风险: **A** + P
用法: `proc.env --pid <n> [--match <re>] [--show-secrets]`
data: `{"pid":..,"items":[{"name":"PATH","value":"..."}],"count":N,"availability":"ok|denied|unsupported","columns":["name","value"]}`
退出码: 0/4（非管理员访问其他会话/受保护进程 → `E_DENIED` + hint）/3
* 实现：`NtQueryInformationProcess(ProcessBasicInformation)` 取 PEB → `ReadProcessMemory` 读 `ProcessParameters` → UNICODE_STRING 解析。
  结构随 Windows 版本变化，**必须**在失败时优雅降级（`availability:"unsupported"`，返回 0 而不是崩）。
* 另一条更稳的路径：父进程用 `run --capture-env` 主动记录子进程环境到 job 目录（§14.9），推荐 agent 用它而不是事后读 PEB。

### 8.10 `proc.handles` — 全系统句柄枚举（v0.3，可选）

价值: 中（找"谁锁着这个文件"的另一条路径） · 复杂度: L · 风险: **A**
用法: `proc.handles [--pid <n>] [--type file|key|mutant|event] [--path <glob>] [--limit 10000]`
data: `{"items":[{"pid":..,"name":"\\Device\\HarddiskVolume3\\x\\a.txt","type":"File","handle":1234,"grantedAccess":"0x12019f"}],"count":N,"truncated":false,"columns":["pid","type","name"]}`
退出码: 0/4/5
* 实现：`NtQuerySystemInformation(SystemExtendedHandleInformation)` + `DuplicateHandle` 到自身 + `GetFinalPathNameByHandleW` 解析设备路径。
* **优先级低于 `scan.locked`**（Restart Manager 无需提权且稳定）；本条只在明确需要"反查路径→句柄"时实现，
  且必须 `--limit` 与超时保护，崩了就降级返回部分结果。

### 8.11 `proc.snapshot` / `proc.diff` — 进程集合快照与对比

价值: 高（agent 执行安装/构建前后对比，抓"一闪而过的"新进程） · 复杂度: M · 风险: 无
用法: `proc.snapshot [--out <file>] [--cmdline] [--hash-exe]`
`proc.diff --base <snapA> [--target <snapB>|--live] [--by pid|name|path+cmdline] [--detect-respawn]`
data: `snapshot`: `{"outFile":..,"count":N,"snapshotId":"20260217-101530-ab12","hashed":false}`
`diff`: `{"started":[{"pid":..,"name":..,"cmdline":..}],"exited":[..],"changed":[{"pid":..,"key":"cmdline","from":..,"to":..}],"respawned":[{"name":..,"oldPid":..,"newPid":..}],"unchanged":N,"columns":["kind","pid","name"]}`
退出码: 0（无变化）/ **1 有变化**（`--exit-code`）/ 3 / 5
* `--by path+cmdline` 可识别"同一程序换 PID"（`respawned`）；这是抓"某进程反复重启"的关键。

### 8.12 `proc.watch` — 进程出现/消失监视（长期）

价值: 中高（守株待兔抓瞬时进程） · 复杂度: M · 风险: 无
用法: `proc.watch [--name <regex>|--path <glob>] [--events start,exit] [--interval 500ms] [--duration 10m] [--max-events 100]`
data（`--jsonl`）：`{"type":"item","item":{"kind":"started","pid":..,"name":..,"cmdline":..,"ts":..}}`；结束给 summary
退出码: 0/5/130/2（没给 duration/max-events）
* 实现用 WMI 事件（`__InstanceCreationEvent`）比轮询更省 CPU，但 WMI 事件订阅在部分机器上不稳 → 默认轮询，`--via wmi` 可选。

---

## 9. 系统管理（服务/磁盘/日志/软件/启动项/任务/驱动/补丁/性能/电源/注册表/清理）

### 9.1 `svc.list` — 服务列表

价值: 高 · 复杂度: S/M · 风险: 无
依赖: `System.ServiceProcess.ServiceController`（引用已在 build.ps1）
用法: `svc.list [--name <regex>] [--state running|stopped|all] [--start-mode auto|manual|disabled|all] [--with-path] [--with-deps] [--sort name|state|pid]`
data: `{"items":[{"name":"wuauserv","displayName":"Windows Update","state":"Running","startMode":"Manual","pid":1234,"canStop":true,"canPauseAndContinue":false,"account":"LocalSystem","path":"C:\\Windows\\system32\\svchost.exe -k netsvcs","dependencies":["rpcss"]}],"count":N,"byState":{"Running":120},"columns":["name","state","startMode","pid"]}`
退出码: 0/4（部分服务查询被拒 → `failures`，返回 6）

### 9.2 `svc.control` — 启停/重启服务

价值: 高（修服务、让配置生效） · 复杂度: M · 风险: **D** + **A**
用法: `svc.control <name>... --action start|stop|restart|pause|continue [--wait] [--timeout 30s] [--if-running] [--dry-run|--yes]`
data: `{"plan":[{"name":"wuauserv","action":"stop","stateBefore":"Running"}],"results":[{"name":..,"stateBefore":..,"stateAfter":..,"ok":true,"elapsedMs":1200,"error":null}],"applied":true,"txid":..,"columns":["name","action","ok","detail"]}`
退出码: 0/2（缺 `--yes`）/3（服务不存在）/4（非管理员 → `E_DENIED`，「需要管理员权限」）/6/5
闸门: 强制。`--wait` 默认开（等待目标状态，超时进 `failures` 并返回 6）。
* 停服务前**先检查依赖服务**（`ServicesDependedOn`/dependent services），不自动连带停止（危险），但要在 `warnings` 里列出。

### 9.3 `svc.get` — 单个服务详情（含 config/recovery）

价值: 中高 · 复杂度: M · 风险: 无
用法: `svc.get <name> [--config] [--recovery] [--sid]`
data: `{"name":..,"displayName":..,"description":..,"state":..,"startMode":..,"path":..,"account":..,"delayedAutoStart":false,"failureActions":[{"action":"Restart","delayMs":60000}],"serviceSid":"unrestricted","triggers":[...]}`
* config/recovery 用 P/Invoke `QueryServiceConfig2W`（M）；`ServiceController` 不给这些。

### 9.4 `svc.config` — 修改服务启动类型/账号（v0.3）

价值: 中（排障：把服务设成手动） · 复杂度: M · 风险: **D** + **A**
用法: `svc.config <name> --start-mode auto|manual|disabled|delayed-auto [--account <user>] [--dry-run|--yes]`
data: `{"name":..,"before":{"startMode":"Auto"},"after":{"startMode":"Manual"},"applied":true,"txid":..}`
退出码: 0/2/3/4

### 9.5 `disk.space` — 卷空间

价值: 高（第一诊断） · 复杂度: S · 风险: 无
用法: `disk.space [--path <dir>]... [--all] [--fixed-only] [--show-unc] [--threshold 10%]`
data: `{"items":[{"drive":"C:","label":"System","fs":"NTFS","total":N,"free":N,"usedPercent":78.2,"volumeSerial":"...","isReady":true,"driveType":"Fixed","belowThreshold":false}],"count":N,"columns":["drive","label","fs","total","free","usedPercent"]}`
退出码: 0 / **1**（有卷低于 `--threshold`，便于 agent 直接判断）/ 3 / 4

### 9.6 `disk.volumes` — 卷/分区/挂载点清单

价值: 中（找挂载点、确认哪个盘是网络盘） · 复杂度: M · 风险: 无
依赖: WMI `Win32_Volume` / `Win32_LogicalDisk` / `Win32_DiskPartition` / `Win32_DiskDrive`；`GetVolumeInformationW`
用法: `disk.volumes [--include-network] [--include-removable] [--mount-points] [--physical]`
data: `{"items":[{"id":"\\\\?\\Volume{...}\\","drive":"C:","label":..,"fs":"NTFS","total":N,"free":N,"mountPoints":["C:\\"],"diskIndex":0,"partitionIndex":1,"busType":"NVMe","physical":"Samsung SSD 990","serial":"...","compressed":false,"bitlocker":"unknown"}],"count":N,"columns":["drive","label","fs","total","free"]}`
* `physical` 需管理员（`Win32_DiskDrive` 的 `SerialNumber` 在非提权下可能为空）→ 降级为 `null` 而不是失败。

### 9.7 `disk.health` — SMART/健康（尽力而为）

价值: 中高（"盘是不是要坏了"） · 复杂度: M · 风险: **A**（多数机器需要管理员）· 联网: N（部分）
用法: `disk.health [--drive <n|C:>] [--predict] [--temperature]`
data: `{"items":[{"disk":0,"model":..,"status":"OK|Warning|Unhealthy|Unknown","predictFailure":false,"temperatureC":38,"powerOnHours":1200,"reallocatedSectors":0,"sources":["wmi:MSStorageDriver_FailurePredictStatus","wmi:Win32_DiskDrive.Status"]}],"count":N,"availability":"partial|admin-required","columns":["disk","model","status","temperatureC"]}`
退出码: 0 / 4（需管理员 → `E_DENIED` + hint）/ 5
* 数据源优先级：`Win32_DiskDrive.Status`（免提权）→ `MSStorageDriver_FailurePredictStatus`（需管理员）→ `MSFT_PhysicalDisk`（Win10+）。
  任何一个可用即返回 0，并在 `sources` 里说明。

### 9.8 `eventlog.query` — 事件日志查询（XPath）

价值: 高（诊断崩溃/更新失败/登录异常） · 复杂度: M · 风险: 无（部分日志需管理员）
依赖: `System.Diagnostics.Eventing.Reader`（`System.Core.dll`，已在引用内）
用法: `eventlog.query --log System|Application|Security|<custom> [--level error|warning|information|critical|all] [--provider <name>] [--id 1000,1001] [--since 2h] [--until <date>] [--xpath "<full>"|--filter <simple>] [--max 100] [--message] [--xml] [--sort newest|oldest]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--log` | `System` | 可重复；也支持 `--log-file <path.evtx>` 查离线文件 |
| `--message` | true | 渲染消息文本（需要 provider 元数据，偶有慢/失败 → 失败时 `message:null` 并保留 `properties`） |
| `--max` | 100 | 上限（安全阀，`--max 0` = 不限但必须配 `--since`，否则拒绝执行返回 2） |
| `--xpath` | 无 | 完整 XPath，会与其它过滤**互斥**（给了就只管它） |
| `--oldest-first` | false | 默认最新在前 |

data: `{"items":[{"log":"System","time":..,"level":"Error","levelValue":2,"id":7000,"provider":"Service Control Manager","task":null,"opcode":null,"recordId":123456,"computer":..,"user":null,"message":"...","properties":["..."]}],"count":N,"scanned":N,"truncated":false,"columns":["time","level","id","provider","message"]}`
退出码: 0（含 0 命中）/ 2（`--max 0` 无 `--since`）/ 3（日志名不存在 → `E_NOT_FOUND`）/ 4（Security 日志需管理员）/ 5
* **注意**：`--since` 必须转成 `SystemTime` XPath 条件而不是先拉全量再筛（否则 Security 日志会拉到几百万条）。
* 消息渲染失败不能拖垮整个查询：批量取 `EventLogPropertySelector`，单条 try/catch。

### 9.9 `eventlog.tail` — 事件日志实时跟踪

价值: 中高（agent 长跑观察安装/崩溃） · 复杂度: M · 风险: 无
用法: `eventlog.tail [--log System] [--level error,warning] [--provider <n>] [--duration 5m] [--max-events 100] [--poll 1s]`
data（`--jsonl`）：每条事件一行 `{"type":"item","item":{...同 eventlog.query 的 item...}}`
退出码: 0/5/130（需修 Program.cs，见附录 A）/2（duration/max-events 都没给）
* 用 `EventLogWatcher`（推送，免轮询），失败回退轮询；必须处理"日志被清空/回滚"（`EventLogException`）。

### 9.10 `eventlog.export` — 导出事件（CSV/JSON/evtx）

价值: 中（把证据交给别的工具/人） · 复杂度: S · 风险: 无
用法: `eventlog.export --log <name> [--out <file>] [--format csv|json|jsonl|evtx] [--since 1d] [--max 10000]`
data: `{"outFile":..,"format":"csv","events":N,"bytes":N,"elapsedMs":N}`
退出码: 0/2/3/4/5；`evtx` 用 `EventLogSession.ExportLogAndMessages`（需 `--out` 且目标不存在）

### 9.11 `installed.list` — 已安装软件

价值: 高（"装了什么版本"、找卸载串） · 复杂度: M · 风险: 无
用法: `installed.list [--name <regex>] [--publisher <re>] [--min-size 0] [--sort name|date|size] [--scope machine|user|both] [--with-store-apps]`
data: `{"items":[{"name":"7-Zip 23.01 (x64)","version":"23.01","publisher":"Igor Pavlov","installDate":"2024-01-02","installLocation":"C:\\Program Files\\7-Zip","uninstallString":"\"C:\\...\\Uninstall.exe\"","quietUninstallString":null,"estimatedSize":5242880,"scope":"machine","hive":"HKLM\\...\\Uninstall","arch":"x64","productCode":"{...}"}],"count":N,"byPublisher":{"Microsoft Corporation":42},"columns":["name","version","publisher","installDate"]}`
退出码: 0/4
* 扫描 5 处：`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`（64 位视图）、
  `HKLM\SOFTWARE\WOW6432Node\...`、`HKCU\SOFTWARE\...`、`HKLM\...\Uninstall` 的 per-user 变体、
  外加 AppX（`--with-store-apps` 时用 `Get-AppxPackage` 或直接读 `HKLM\...\Appx\AppxAllUserStore`，M）。
* 读 64/32 位注册表视图必须显式用 `RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64|Registry32)`。

### 9.12 `installed.find` — 通过软件反查可执行文件

价值: 中高（"这个 exe 属于哪个产品/版本"） · 复杂度: M · 风险: 无
用法: `installed.find <path-or-name> [--deep]`
data: `{"query":..,"items":[{"path":"C:\\...\\node.exe","product":"Node.js","version":"20.11.0","company":"Node.js Foundation","package":null,"source":"installed-list|version-info|path-prefix"}],"count":N,"columns":["path","product","version"]}`
* 三步：注册表 `installLocation` 前缀匹配 → 文件 `VersionInfo`（`FileVersionInfo.GetVersionInfo`）→ 深度模式扫描 `uninstallString` 反查。

### 9.13 `startup.list` — 启动项全清点（autorun 方向）

价值: 高（安全审查、启动变慢、清理常驻） · 复杂度: M · 风险: 无（读）
用法: `startup.list [--scope user|machine|all] [--kind run|runonce|folder|logon-script|wmi|service|task|all] [--unsigned-only] [--verify-signature] [--missing-only]`
data: `{"items":[{"kind":"run","scope":"machine","name":"SecurityHealth","command":"%windir%\\system32\\SecurityHealthSystray.exe","location":"HKLM\\...\\Run","enabled":true,"exists":true,"signed":"Valid","publisher":"Microsoft Corporation","fileSha256":..}],"count":N,"byKind":{"run":18,"task":42},"columns":["kind","name","command","signed"]}`
退出码: 0/4/6
* 来源清单（每条一个 `kind`）：`HKLM/HKCU ...\Run`、`RunOnce`、`Winlogon\Userinit|Shell`、Startup 文件夹（含 common）、
  登录脚本（`UserInitMprLogonScript`）、WMI 持久化（`__EventFilter`+`CommandLineEventConsumer`，需管理员，可 `--skip-wmi`）、
  服务（复用 `svc.list` 的 `startMode=Auto`）、计划任务（复用 `task.list` 的 logon/boot 触发器）。
* `--missing-only`：命令指向的文件已不存在的启动项（清理候选，**只报告不删**）。

### 9.14 `startup.disable` — 禁用启动项（v0.3）

价值: 中（清理常驻） · 复杂度: M · 风险: **D**（改注册表/移动文件）+ A（机器级）
用法: `startup.disable --kind run --name <name> --scope machine [--dry-run|--yes] [--restore]`
data: `{"plan":[{"kind":"run","name":..,"action":"remove-value","backup":"...\\undo\\<txid>\\startup.reg"}],"applied":false,"txid":..}`
* 实现：**先导出 `.reg` 备份**（`reg export` 或自写），再删值；`--restore` 用备份回写（`reg import`，仍需 `--yes`）。
* Startup 文件夹项用"移动到 `%LOCALAPPDATA%\\dsh-toolbox\\disabled-startup\\`"而不是删除。

### 9.15 `task.list` — 计划任务

价值: 高 · 复杂度: M · 风险: 无（读）
依赖: **没有托管 API**（FW 上无 `TaskScheduler` 库）→ 调 `schtasks.exe /query /XML ONE` 或 `/FO CSV /V` 并解析
用法: `task.list [--path <folder glob>] [--state ready|running|disabled|all] [--trigger logon|boot|daily|all] [--author <re>] [--hidden] [--next-run] [--via xml|csv]`
data: `{"items":[{"name":"\\Microsoft\\Windows\\UpdateOrchestrator\\Schedule Scan","path":"\\Microsoft\\...","state":"Ready","enabled":true,"author":"Microsoft","triggerSummary":["At 03:00 daily"],"action":"C:\\Windows\\system32\\usoclient.exe StartScan","runAs":"SYSTEM","lastRun":..,"nextRun":..,"lastResult":0,"hidden":false,"source":"xml"}],"count":N,"byState":{...},"columns":["name","state","nextRun","action"]}`
退出码: 0/4/6/1（`schtasks` 不可用 → `E_TOOL_MISSING`，hint 说明系统缺组件）
* `--via xml` 给最全信息（作者、触发器、动作），需要管理员才能看部分 SYSTEM 任务（否则缺失字段为 `null`，不算失败）。
* **不要**解析本地化的 CSV 列名（中文 Windows 上列名是中文）→ XML 优先，CSV 只作降级且按列位置解析。

### 9.16 `task.get` — 单个任务详情（XML）

价值: 中高（看清触发器/动作/条件） · 复杂度: S · 风险: 无
用法: `task.get --name <full\path> [--raw-xml]`
data: `{"name":..,"xml":"<Task ...>","parsed":{"triggers":[...],"actions":[...],"conditions":{...},"settings":{...}},"columns":[]}`

### 9.17 `task.run` — 立即运行任务

价值: 中（触发系统维护、测试任务） · 复杂度: S · 风险: **D**（执行系统动作）
用法: `task.run --name <full\path> [--dry-run|--yes]`
data: `{"name":..,"started":true,"lastResult":0,"applied":true,"txid":..}`
退出码: 0/2/3/4

### 9.18 `task.create` / `task.delete`（v0.3）

价值: 中（agent 注册自己的周期任务；也是恶意软件常用手法 → 必须显式确认） · 复杂度: M · 风险: **D** + A
用法: `task.create --name <n> --command <cmd> [--args ..] [--trigger boot|logon|daily --at 03:00|--interval 1h] [--run-as SYSTEM|current] [--xml <file>] [--dry-run|--yes]`
`task.delete --name <n> [--dry-run|--yes]`
data: `{"name":..,"created":true,"xml":..,"applied":true,"txid":..}`
* 优先 `--xml <file>`（agent 生成完整 XML，避免我们拼触发器）；`schtasks /create /xml` 需要文件落地到临时目录。

### 9.19 `driver.list` — 驱动与设备

价值: 中高（蓝屏/外设/驱动版本排查） · 复杂度: M · 风险: 无/A（部分字段）
用法: `driver.list [--kind driver|device|all] [--state running|stopped|problem] [--signed-only|--unsigned-only] [--class <name>] [--problem-only]`
data: `{"items":[{"kind":"driver","name":"nvlddmkm","displayName":"NVIDIA Windows Kernel Mode Driver","state":"Running","startMode":"Manual","path":"C:\\Windows\\System32\\drivers\\nvlddmkm.sys","version":"31.0.15.3699","signed":"Valid","signer":"Microsoft Windows Hardware Compatibility Publisher","problemCode":0,"deviceId":"PCI\\VEN_10DE&DEV_..."}],"count":N,"byState":{...},"problemCount":2,"columns":["name","state","version","signed"]}`
退出码: 0/6（部分 WMI 查询失败 → `failures`）
* `Win32_SystemDriver` + `Win32_PnPEntity`（`ConfigManagerErrorCode != 0` 视为 problem）；签名用 §11.1 的实现复用（不要重写）。

### 9.20 `patch.list` — 补丁与更新状态

价值: 中高（合规、排查"是否装了某个 KB"） · 复杂度: M · 风险: 无/A
用法: `patch.list [--kb <id>] [--since <date>] [--pending] [--history] [--format table|json]`
data: `{"items":[{"hotfixId":"KB5034441","description":"Security Update","installedOn":"2024-01-10","installedBy":"NT AUTHORITY\\SYSTEM","source":"wmi:Win32_QuickFixEngineering"},{"hotfixId":"KB5039212","result":"Succeeded","date":..,"source":"history"}],"count":N,"lastInstalled":..,"pendingReboot":{"required":true,"reasons":["PendingFileRenameOperations","CBS RebootPending"],"source":"registry"},"columns":["hotfixId","description","installedOn"]}`
退出码: 0 / **1**（`--pending` 时确有挂起重启）/ 4 / 5
* `--pending` 检查 3 个注册表位置：`CBS\RebootPending`、`WindowsUpdate\Auto Update\RebootRequired`、`Session Manager\PendingFileRenameOperations`。
* `--history` 用 COM `Microsoft.Update.Session`（late-bound COM，**用 `Type.InvokeMember` 反射，不要 `dynamic`**
  —— `build.ps1` 未引用 `Microsoft.CSharp.dll`，`dynamic` 会在运行期缺 binder 失败；见附录 A）。

### 9.21 `perf.sample` — 性能采样（CPU/内存/磁盘/网络）

价值: 中高（判断"是不是卡在 IO"、"谁在吃 CPU"） · 复杂度: M · 风险: 无
依赖: `PerformanceCounter`（`System.Diagnostics`，`System.dll`）+ 进程两次采样
用法: `perf.sample [--duration 10s] [--interval 1s] [--counter cpu,mem,disk,net,proc] [--top 10] [--include-process <regex>]`
data: `{"samples":[{"ts":..,"cpuPercent":12.3,"memUsedPercent":64.1,"memAvailableBytes":N,"diskReadBytesPerSec":N,"diskWriteBytesPerSec":N,"netRecvBytesPerSec":N,"netSendBytesPerSec":N,"top":[{"pid":..,"name":..,"cpuPercent":4.2,"workingSet":N}]}],"durationSec":10,"intervalSec":1,"sampleCount":10,"summary":{"cpuAvg":12.1,"cpuMax":45.0,"memAvg":64.0},"columns":["ts","cpuPercent","memUsedPercent"]}`
退出码: 0/5/4
* **已知坑**：性能计数器库损坏时 `PerformanceCounter` 会抛 `InvalidOperationException`（需 `lodctr /r` 修复）。
  必须捕获并降级为"只用进程累计 CPU 做差分"，在 `degraded:true` + `warnings` 里说明。
* 网络/磁盘计数器的本地化实例名（中文 Windows 上 `Network Interface` 会变成中文）→ 用 `new PerformanceCounterCategory("Network Interface").GetInstanceNames()` 动态取实例名，**硬编码英文名必挂**。

### 9.22 `perf.top` — 当前资源占用 Top N（轻量替代）

价值: 中高（比 `perf.sample` 更快的一条命令） · 复杂度: S · 风险: 无
用法: `perf.top [--by cpu|mem|io] [--top 15] [--interval 500ms]`
data: `{"items":[{"pid":..,"name":..,"cpuPercent":12.3,"workingSet":N,"threads":N,"handles":N,"startTime":..}],"count":N,"sampledMs":500,"columns":["pid","name","cpuPercent","workingSet"]}`

### 9.23 `power.battery` — 电池/电源

价值: 中（笔记本 agent 判断能否跑重活） · 复杂度: S/M · 风险: 无
用法: `power.battery [--report] [--cycles]`
data: `{"present":true,"acOnline":false,"chargePercent":82,"designCapacity":52000,"fullChargeCapacity":48000,"healthPercent":92.3,"estimatedRuntimeMin":210,"cycles":340,"chemistry":"Li-Ion","sources":["wmi:Win32_Battery","powercfg:/batteryreport"],"columns":["key","value"]}`
退出码: 0 / **3**（`--report` 但无电池设备）/ 4/5
* 无电池（台式机）时 `present:false` 并返回 0（不是错误）。
* `--cycles` 需解析 `powercfg /batteryreport /XML`（生成到临时目录再解析，S）。

### 9.24 `power.thermal` — 温度（尽力而为）

价值: 中 · 复杂度: M · 风险: **A**
用法: `power.thermal [--zone all|0] [--unit c|f]`
data: `{"items":[{"source":"wmi:MSAcpi_ThermalZoneTemperature","zone":"\\_TZ.TZ00","temperatureC":54.2,"active":true}],"count":N,"availability":"ok|unsupported|admin-required","columns":["zone","temperatureC"]}`
退出码: 0 / 4 / 3（`availability:"unsupported"` 且无任何源）
* 很多机器/虚拟机不支持 ACPI 热区 → 返回 0 + `availability:"unsupported"` + hint（别让 agent 以为命令坏了）。
* 备选源：`MSFT_StorageTemperature`（NVMe，需管理员）。

### 9.25 `power.plan` — 电源计划（只读）

价值: 低中（"为什么机器这么卡/这么费电"） · 复杂度: M · 风险: 无
用法: `power.plan [--list] [--active] [--sleep-timeouts] [--hibernate-available]`
data: `{"active":"Balanced","plans":[{"guid":"...","name":"Balanced","active":true}],"sleep":{"ac":"15m","dc":"5m"},"hibernateEnabled":true,"fastStartup":true,"columns":["name","active"]}`
* 读 `HKLM\SYSTEM\CurrentControlSet\Control\Power` + `powercfg /list`；**不提供修改**（改电源计划风险高、收益低 → 不做清单）。

### 9.26 `registry.get` — 读注册表

价值: 高（几乎一切 Windows 配置都在注册表） · 复杂度: S/M · 风险: 无/P
用法: `registry.get --key <path> [--name <v>] [--recurse] [--depth 1] [--view 64|32|auto] [--values-only] [--types] [--raw]`
data: `{"key":"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion","exists":true,"items":[{"key":..,"name":"ProgramFilesDir","type":"REG_SZ","value":"C:\\Program Files","bytes":30}],"count":N,"view":"64","columns":["name","type","value"]}`
退出码: 0 命中 / **3 键不存在**（`exists:false`）/ 4（拒绝访问，如 SAM）/ 1（类型异常）
* 接受简写 `HKLM`/`HKCU`/`HKCR`/`HKU`/`HKCC`，并接受 `HKEY_LOCAL_MACHINE` 全名。
* 默认 `REG_BINARY`/`REG_MULTI_SZ` 转成可读形式（hex 字符串 / 数组）；`--raw` 时给 `{type,raw:base64}`。
* **必须显式指定 hive 视图**（`RegistryView.Registry64`/`Registry32`），否则 WOW64 重定向会让 32/64 结果不同。

### 9.27 `registry.set` — 写注册表（v0.3，受控）

价值: 中（改文件关联、开长路径、设环境变量） · 复杂度: M · 风险: **D** + **A**
用法: `registry.set --key <path> --name <v> --value <data> [--type string|expand|dword|qword|multi|binary] [--view 64|32] [--create-key] [--backup] [--dry-run|--yes]`
data: `{"plan":[{"key":..,"name":..,"before":..,"after":..,"type":"REG_DWORD"}],"applied":true,"backupFile":"...\\undo\\<txid>\\hkcu-software-x.reg","txid":..}`
退出码: 0/2/3/4
* **强制前置保护**：写入前自动导出该键到 undo 目录（自写 `.reg` 文本，或调 `reg export`）；白名单外的高危根
  （`HKLM\SYSTEM`、`HKLM\SECURITY`、`HKLM\SAM`、`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon`）
  需额外 `--i-know`。

### 9.28 `registry.list` — 列出子键/值（`registry.get` 的别名语义）

价值: 中 · 复杂度: S · 风险: 无
用法: `registry.list --key <path> [--keys-only|--values-only] [--view 64|32] [--count-only]`
data: `{"key":..,"subKeys":["..."],"values":[{"name":..,"type":..,"value":..}],"subKeyCount":N,"valueCount":N}`
* 与 `registry.get --recurse` 的区别：本命令**只一层**，适合快速浏览；两者共享实现。

### 9.29 `temp.clean` — 临时文件清理（**白名单制**）

价值: 高（回收空间、最常用于"磁盘满了"） · 复杂度: L · 风险: **D**（唯一能误删用户数据的地方，必须极度保守）
用法: `temp.clean [--profile safe|standard|aggressive] [--older-than 7d] [--targets <id>...] [--dry-run(default)] [--yes] [--max-bytes 10GB] [--report <file>]`

| 目标 id | 路径 | safe | standard | aggressive |
|---|---|---|---|---|
| `user-temp` | `%TEMP%` | ✅ | ✅ | ✅ |
| `win-temp` | `%WINDIR%\Temp` | ❌ | ✅(A) | ✅(A) |
| `inet-cache` | `%LOCALAPPDATA%\Microsoft\Windows\INetCache` | ✅ | ✅ | ✅ |
| `explorer-thumbs` | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db` | ❌ | ✅ | ✅ |
| `wupdate-cache` | `%WINDIR%\SoftwareDistribution\Download` | ❌ | ❌ | ✅(A) |
| `delivery-optimization` | `...\DeliveryOptimization\Cache` | ❌ | ❌ | ✅(A) |
| `crash-dumps` | `%LOCALAPPDATA%\CrashDumps`, `%WINDIR%\Minidump` | ❌ | ✅ | ✅ |
| `wer-report-queue` | `%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue` | ❌ | ✅ | ✅ |
| `dsh-toolbox-cache` | `Paths.Cache` | ✅ | ✅ | ✅ |

data: `{"plan":[{"target":"user-temp","path":..,"files":N,"bytes":N}],"items":[{"target":..,"path":..,"files":N,"bytes":N,"freed":N,"skipped":N,"skipReasons":{"in-use":12,"too-new":340,"outside-root":0}}],"totalBytes":N,"freedBytes":N,"failures":[],"applied":false,"txid":..,"columns":["target","path","files","bytes"]}`
退出码: 0/2/3/4/6
闸门: **默认 `--dry-run` 行为**（不给 `--yes` 就只预览；这与契约"默认 exit 2"不同 →
按契约实现为：不给 `--yes` 时若未显式 `--dry-run` 则返回 2；但 hint 里给出本次可回收空间数值，引导 agent 先 preview）。
**安全规则（写死在代码里）**：
1. **绝不**跟随联接点/符号链接（清空前逐条检查 `ReparsePoint`，是链接一律跳过并记 `outside-root`）；
2. 逐条用 Restart Manager 检查占用（§1.17），在用的跳过；
3. 只删 `--older-than`（默认 `7d`）之前的文件；正在被写入的（mtime 在 60s 内）跳过；
4. 只删**文件**，目录只删空的（且必须在我们计算出的目标根之下）；
5. 每条路径必须 `Fs.IsSubPath(target, expectedRoot)` 二次校验；
6. 删除走回收站（复用 `file.rm` 的实现）——**临时文件也进回收站**，因为代价远小于误删；
7. `--max-bytes` 超限即停止并在 `truncatedAt:"max-bytes"` 说明。

### 9.30 `sys.uptime` / `sys.boot` — 启动与运行时长（并入 sysinfo 的补充）

价值: 低中 · 复杂度: S · 风险: 无
* **决定不新增命令**：`sysinfo` 已回 `uptime`/`bootApprox`；需要精确启动时间请用
  `eventlog.query --log System --provider EventLog --id 6005 --max 1`。避免命令膨胀（ROADMAP §5）。

---
## 10. 网络

> 通用约束：所有联网命令**必须**有默认超时（下表逐个给出），且失败要给 `E_NET` + 明确 hint，不能挂死。
> 批量联网命令（`net.http`）在 `--jsonl` 下逐条流式输出，整体有失败则 `exit 6`。
> HTTP 一律用 `HttpWebRequest`（`ARCHITECTURE.md` §2.1 指定；`HttpClient` 的高级特性禁用），
> 并**显式设置** `ServicePointManager.SecurityProtocol`（FW 4.8 默认可能不含 TLS1.2 → 必须开）。

### 10.1 `net.adapters` — 网卡与地址（契约中的 `net.ip`）

价值: 高（找本机 IP、判断在哪个网段/VPN 是否连上） · 复杂度: S · 风险: 无
用法: `net.adapters [--up-only] [--include-virtual] [--with-gateway] [--with-dns] [--alias net.ip]`
data: `{"items":[{"name":"Ethernet","description":"Intel(R) Ethernet Connection","status":"Up","type":"Ethernet","speedMbps":1000,"mac":"AA-BB-...","ipv4":["192.168.1.20/24"],"ipv6":["fe80::1%12"],"gateway":["192.168.1.1"],"dns":["192.168.1.1"],"dhcp":true,"virtual":false}],"count":N,"defaultRoute":"192.168.1.1","columns":["name","status","ipv4","mac"]}`
退出码: 0/5（WMI 超时）
* **命名裁决（见 [ROADMAP §1.4 D5](ROADMAP.md)）**：v0.1 实现已注册 **`net.ip`**，因此**不新增 `net.adapters`**；
  本条规格整体适用于 `net.ip`（若 Lead 想让 `net.adapters` 生效，用 `Registry.Add(..., aliases:)` 挂别名即可）。

### 10.2 `net.ports` — 监听/连接与进程映射

价值: 高（"端口被谁占了"、"有没有后门监听"） · 复杂度: M · 风险: 无
依赖: `GetExtendedTcpTable`/`GetExtendedUdpTable`（P/Invoke `iphlpapi.dll`）；降级 `--via netstat` 解析
用法: `net.ports [--state listen|established|all] [--port <n>]... [--pid <n>]... [--process <re>] [--proto tcp|udp|both] [--exclude-loopback] [--via auto|table|netstat]`
data: `{"items":[{"proto":"tcp","family":"IPv4","localAddress":"0.0.0.0","localPort":3000,"remoteAddress":"0.0.0.0","remotePort":0,"state":"Listen","pid":1234,"process":"node.exe","path":..,"offloadState":null}],"count":N,"byState":{"Listen":24,"Established":63},"columns":["proto","localAddress","localPort","state","pid","process"]}`
退出码: 0（含 0 命中）/ 3（`--port` 无匹配）/ 4（进程名解析失败 → 仍返回 0，`process:null`）/ 5
* **优先用 IP Helper API**：一次调用拿到全部条目 + PID，比 `netstat` 快 10× 且不受本地化影响。

### 10.3 `net.connections` — 连接统计与 Top 会话

价值: 中高（异常外联、流量大客户） · 复杂度: S · 风险: 无
用法: `net.connections [--by process|remote-ip|port] [--top 20] [--established-only] [--external-only]`
data: `{"items":[{"key":"chrome.exe (1234)","count":42,"remoteIps":["142.250.1.1"],"ports":[443]}],"count":N,"total":N,"columns":["key","count","remoteIps"]}`
* `--external-only`：排除 RFC1918/loopback/link-local 目标（脚本判断"是否在往公网发东西"）。

### 10.4 `net.tcp` — TCP 连通性/端口探测

价值: 高（判断服务是否起来、防火墙是否放行） · 复杂度: S · 风险: 无
用法: `net.tcp <host:port>... [--timeout 3s] [--parallel 16] [--expect-open|--expect-closed] [--banner 0] [--source <ip>]`
data: `{"items":[{"host":"127.0.0.1","port":3000,"open":true,"elapsedMs":3,"error":null,"banner":null,"resolvedIp":"127.0.0.1"}],"count":N,"openCount":N,"closedCount":N,"columns":["host","port","open","elapsedMs"]}`
退出码: 0（全部符合期望）/ **1**（`--expect-open` 时有端口关闭）/ 2（地址格式非法）/ 5（整体超时）
* `--banner <n>`：连接后读 n 字节（默认 0 = 不读），可用于识别 SSH/SMTP/HTTP 服务。

### 10.5 `net.ping` — ICMP 探测

价值: 中高（延迟/丢包/RTT 抖动） · 复杂度: S · 风险: 无
依赖: `System.Net.NetworkInformation.Ping`
用法: `net.ping <host>... [--count 4] [--timeout 2s] [--interval 500ms] [--size 32] [--ttl 128] [--dont-fragment] [--ipv6] [--resolve]`
data: `{"items":[{"host":"8.8.8.8","resolvedIp":"8.8.8.8","sent":4,"received":4,"lossPercent":0,"minMs":12,"avgMs":14.2,"maxMs":22,"jitterMs":3.1,"status":"Success","replies":[{"seq":1,"ms":14,"ttl":117,"status":"Success"}]}],"count":N,"reachable":N,"columns":["host","avgMs","lossPercent","status"]}`
退出码: 0（全部可达）/ **1**（有不可达/丢包，agent 主判据）/ 2/5
* `--dont-fragment` 与 `--size` 组合可做 MTU 探测（`PingOptions.DontFragment`）。

### 10.6 `net.trace` — 路由跟踪（ICMP TTL 递增）

价值: 中（排查"卡在哪一跳"） · 复杂度: M · 风险: 无
用法: `net.trace <host> [--max-hops 30] [--timeout 2s] [--probes 3] [--resolve-names] [--tcp-port <n>]`
data: `{"items":[{"hop":3,"ip":"10.0.0.1","host":null,"rttMs":[12,11,13],"avgMs":12,"status":"Success","asn":null}],"count":N,"target":"8.8.8.8","reached":true,"columns":["hop","ip","avgMs","status"]}`
退出码: 0（到达目标）/ **1**（未到达）/ 2/5/4（需要原始套接字权限 → hint 说明可用 `net.tcp` 代替）
* 用 `Ping` + `PingOptions.Ttl` 递增实现（**不需要** `tracert.exe`，也不需原始套接字）；`--tcp-port` 时改用 TCP SYN 实现则需要原始套接字 → 需管理员，失败降级 ICMP。

### 10.7 `net.dns` — DNS 解析（含记录类型）

价值: 高（验证 DNS、查 MX/TXT、排查劫持） · 复杂度: M · 风险: 无
用法: `net.dns <name>... [--type A,AAAA,CNAME,MX,TXT,NS,SOA,PTR,SRV,CAA,ANY] [--server <ip>] [--timeout 3s] [--no-cache] [--reverse <ip>] [--dnssec]`

| 实现路径 | 覆盖类型 |
|---|---|
| `Dns.GetHostEntry`/`GetHostAddresses`（托管，S） | A/AAAA/PTR（其他类型不支持） |
| 自写 DNS 客户端（UDP 53 + 2 字节长度 TCP 回退，M） | 全部类型，`--server` 自定义解析器 |

data: `{"items":[{"name":"example.com","type":"MX","ttl":3600,"value":"10 mail.example.com","section":"answer","server":"192.168.1.1","elapsedMs":12}],"count":N,"rcode":"NOERROR","authoritative":false,"truncated":false,"columns":["name","type","ttl","value"]}`
退出码: 0 有记录 / **3 NXDOMAIN 或无记录** / 1（`SERVFAIL`/超时 → 5）/ 2（类型非法）
* `--type ANY` 在多数解析器上被拒（返回空）→ 提示需要指定类型。

### 10.8 `net.http` — HTTP 批量探测（含证书链）

价值: **很高**（agent 验证服务/接口/下载源是否可用；一条命令并行探 50 个 URL） · 复杂度: M · 风险: N
依赖: `HttpWebRequest` + `X509Chain`（证书链）
用法: `net.http <url>... [--method GET|HEAD|POST|PUT|DELETE] [--header "K: V"]... [--body <s>|--body-file <f>] [--timeout 15s] [--parallel 8] [--follow 5|--no-follow] [--max-bytes 65536] [--show-body] [--show-headers] [--insecure] [--expect-status 200,204] [--proxy <url>] [--no-proxy] [--retry 1] [--cert-chain] [--jsonl]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--timeout <dur>` | 15s | **连接+读**分别计时；总耗时进 `elapsedMs`（分 `dnsMs/connectMs/tlsMs/ttfbMs/totalMs`） |
| `--follow <n>` | 5 | 最多重定向次数；`redirectChain` 回报每跳 `{url,status,location}` |
| `--max-bytes <n>` | 64KB | 响应体读取上限（防拉到 2GB）；超出 `truncated:true` |
| `--expect-status` | 无 | 期望码集合；不符则该项 `ok:false` 并计入 `failures`（整体 `exit 6`） |
| `--insecure` | false | 跳过证书校验（**必须**在输出里 `insecureUsed:true` 警示） |
| `--cert-chain` | true | 构建并回报证书链 |

data: `{"items":[{"url":..,"finalUrl":..,"status":200,"reasonPhrase":"OK","ok":true,"elapsedMs":312,"dnsMs":8,"connectMs":30,"tlsMs":110,"ttfbMs":150,"bytes":12345,"truncated":false,"server":"nginx","contentType":"text/html","headers":{"date":"...","etag":"..."},"redirects":[{"status":301,"location":"https://..."}],"tls":{"protocol":"Tls12","cipher":"ECDHE-RSA-AES256-GCM-SHA384","subject":"CN=example.com","issuer":"CN=R3, O=Let's Encrypt","notBefore":..,"notAfter":..,"daysToExpiry":62,"chain":["CN=example.com","CN=R3","CN=ISRG Root X1"],"chainErrors":[],"sctCount":2,"ocspStapled":false,"san":["example.com","www.example.com"]}}],"count":N,"okCount":N,"failures":[],"columns":["url","status","elapsedMs","tls.daysToExpiry"]}`
退出码: 0 全部符合期望 / **1**（全部失败）/ **6**（部分失败）/ 2/5（整体超时）
闸门: 无副作用（GET/HEAD）；`--method POST/PUT/DELETE` 会改远端状态 → 需要 `--yes`（把 HTTP 写方法也纳入破坏性闸门，
理由：agent 误触 DELETE 的代价和本地删除一样大）。
* 证书错误必须分类：`RemoteCertificateNameMismatch` / `RemoteCertificateChainErrors` / `RemoteCertificateNotAvailable`，
  并把 `X509ChainStatus` 的 `Status`+`StatusInformation` 原样带回（这是排 TLS 问题最有用的信息）。
* `--no-proxy` 要显式设 `request.Proxy = null`；否则系统代理会静默生效（很多"为什么超时"的根因）。
* 同一主机并发 > 6 时注意 `ServicePointManager.DefaultConnectionLimit`（FW 默认 2！**必须**设成 `--parallel`）——
  这是 FW 上 HTTP 性能最经典的坑。

### 10.9 `net.tls` — TLS 握手探测（协议/加密套件/证书）

价值: 中高（合规检查、老系统兼容性） · 复杂度: M · 风险: N
用法: `net.tls <host:port> [--protocols tls12,tls13] [--timeout 10s] [--all] [--sni <name>] [--show-cert] [--check-expiry 30d]`
data: `{"host":..,"port":443,"results":[{"protocol":"Tls12","supported":true,"cipher":"...","keyExchange":"...","hash":"...","error":null,"elapsedMs":120}],"preferred":"Tls12","cert":{...同 net.http 的 tls...},"daysToExpiry":62,"expiryWarning":false,"columns":["protocol","supported","cipher"]}`
退出码: 0 / **1**（`--check-expiry` 时即将过期）/ 3 / 5（全部协议握手失败）
* 用 `SslStream.AuthenticateAsClient` + `SslProtocols` 逐协议尝试；TLS1.3 枚举在 FW 4.8 上需 `12288`(Tls13) 常量，不可用则跳过并注明。
* **不做**加密套件级暴力枚举（需要 Schannel 注册表控制，改动系统状态）→ 只报协商结果。

### 10.10 `net.download` — 下载（断点续传 + 校验 + 原子落盘）

价值: 高（agent 取工具/依赖/证据） · 复杂度: M · 风险: N + **D**（写盘覆盖）
依赖: `HttpWebRequest` + `FileStream`
用法: `net.download <url> --out <file> [--sha256 <expect>] [--size <expect>] [--resume] [--timeout 30s] [--retry 2] [--proxy <url>] [--header "K: V"]... [--user-agent <s>] [--max-bytes 0] [--yes] [--dry-run]`
data: `{"url":..,"out":..,"bytes":N,"contentLength":N,"resumed":true,"resumeFromBytes":1048576,"elapsedMs":N,"speedBytesPerSec":N,"sha256":..,"sha256Expected":..,"verified":true,"status":200,"redirects":[],"applied":true}`
退出码: 0/1（哈希或大小不匹配 → **1**，并**删除**半成品）/2（覆盖已存在文件未加 `--yes`）/3/4/5/6
* 先下载到 `<out>.part`，校验通过再 `File.Move`（原子）；`--resume` 用 `Range: bytes=N-` 续传，
  服务器不支持（非 206）则从头开始并在 `warnings` 说明。
* 失败重试用 `--retry`（指数退避 1s/2s/4s，`--no-retry` 可关）。

### 10.11 `net.proxy` — 代理探测

价值: 中高（很多网络问题源于代理配置不一致） · 复杂度: S · 风险: P
用法: `net.proxy [--test] [--bypass-list] [--winhttp] [--process <pid>]`
data: `{"user":{"autoConfigUrl":null,"proxyEnable":true,"proxyServer":"127.0.0.1:7890","proxyOverride":"<local>","autoDetect":false},"winhttp":{"proxy":"DIRECT","bypass":[]},"environment":{"HTTP_PROXY":..,"HTTPS_PROXY":..,"NO_PROXY":..},"effective":"http://127.0.0.1:7890","test":{"ok":true,"elapsedMs":320,"status":200},"columns":["source","value"]}`
退出码: 0 / **1**（`--test` 失败）/ 5
* 三处来源都要报：WinINET（`HKCU\...\Internet Settings` 或 `WinHttpGetIEProxyConfigForCurrentUser`）、WinHTTP（`netsh winhttp show proxy`）、进程环境变量。
  "为什么 PowerShell 能上网而我的程序不能"通常就是这三者不一致。

### 10.12 `net.hosts` — hosts 文件管理

价值: 中高（本地域名映射、屏蔽、测试） · 复杂度: S · 风险: **D** + A + P
用法: `net.hosts list [--match <re>]`
`net.hosts add --ip <ip> --name <host>... [--comment <s>] [--dry-run|--yes]`
`net.hosts remove --name <host>... [--dry-run|--yes]`
`net.hosts restore --backup <file> [--dry-run|--yes]`
data: `list`: `{"path":"C:\\Windows\\System32\\drivers\\etc\\hosts","items":[{"line":12,"ip":"127.0.0.1","names":["x.local"],"comment":"# dev","enabled":true}],"count":N,"sha256":..,"columns":["line","ip","names"]}`
`add/remove`: `{"path":..,"before":..,"after":..,"applied":true,"backupFile":"...\\undo\\<txid>\\hosts","txid":..,"flushDns":true}`
退出码: 0/2/3/4（非管理员）/6
* 保留原有换行/编码（hosts 可能不是 UTF-8）→ 读写用"字节级行编辑"，不整文件转码。
* 修改后自动 `ipconfig /flushdns`（可 `--no-flush`）；同时打印 diff。

### 10.13 `net.firewall` — 防火墙规则（**只读**）

价值: 中高（"是不是防火墙挡了"） · 复杂度: M · 风险: A（部分规则）
用法: `net.firewall [--profile domain|private|public|all] [--enabled-only] [--direction in|out] [--action allow|block] [--match <re>] [--for-program <path>] [--for-port <n>] [--summary]`
data: `{"profiles":[{"name":"Public","enabled":true,"defaultInbound":"Block","defaultOutbound":"Allow","notifyOnBlock":true}],"items":[{"name":"Node.js","displayName":"Node.js: Server-side JavaScript","enabled":true,"direction":"Inbound","action":"Allow","protocol":"TCP","localPorts":"3000","remoteAddresses":"*","profiles":["Public"],"program":"C:\\...\\node.exe","group":"Node.js","source":"com:HNetCfg.FwPolicy2"}],"count":N,"byAction":{"Allow":1200},"columns":["name","direction","action","enabled","localPorts"]}`
退出码: 0/4（读规则需管理员时降级为 profiles-only + `rules:"admin-required"`）/5
* 读规则用 COM `HNetCfg.FwPolicy2`（late-bound，用 `Type.InvokeMember` 反射，不用 `dynamic`），
  降级用 `netsh advfirewall firewall show rule name=all verbose` 解析（**本地化输出**：中文 Windows 是中文标签 →
  解析要按"中文/英文两种标签"匹配，或直接放弃降级并报 4）。
* **明确不做**规则增删改（`netsh advfirewall ... add rule` 属高危系统面，见 ROADMAP §5）。

### 10.14 `net.shares` — 共享/会话/打开的文件

价值: 中高（暴露面审查、谁在连着我的共享） · 复杂度: M · 风险: A + P
用法: `net.shares [--shares] [--sessions] [--open-files] [--all] [--include-admin-shares]`
data: `{"shares":[{"name":"Data","path":"D:\\Data","type":"Disk","remark":"","permissions":null,"currentUses":2}],"sessions":[{"user":"PC\\me","computer":"10.0.0.5","sessionId":1,"idleSec":120,"connectedSec":3600,"clientType":"Windows","openFiles":3}],"openFiles":[{"id":1,"path":"D:\\Data\\a.xlsx","user":"PC\\me","locks":0,"mode":"Read/Write"}],"count":N,"adminRequired":false,"columns":["kind","name","detail"]}`
退出码: 0/4（sessions/open-files 需管理员 → 降级只回 shares）/5
* WMI：`Win32_Share`（免提权）、`Win32_ServerSession`/`Win32_ServerConnection`/`Win32_ServerSessionProcess`（需管理员）。

### 10.15 `net.whois` — WHOIS 查询

价值: 中（域名归属/注册时间，安全研判） · 复杂度: S · 风险: N
用法: `net.whois <domain|ip>... [--server <host>] [--timeout 10s] [--raw] [--fields registrar,created,expires,ns,org]`
data: `{"items":[{"query":"example.com","server":"whois.verisign-grs.com","fields":{"registrar":"RESERVED-Internet Assigned Numbers Authority","created":"1995-08-14","expires":"2025-08-13","ns":["a.iana-servers.net"]},"raw":"..."}],"count":N,"columns":["query","registrar","expires"]}`
退出码: 0/3（无记录）/5
* 极简实现：TCP 43 发查询 + 读全文；`--fields` 用通用 `key: value` 解析（不做每家格式适配）。

### 10.16 `net.wol` — 网络唤醒（Wake-on-LAN）

价值: 低中（agent 唤醒测试机） · 复杂度: S · 风险: **D**（会让机器开机）
用法: `net.wol --mac <AA:BB:CC:DD:EE:FF> [--broadcast 255.255.255.255] [--port 9] [--count 3] [--dry-run|--yes]`
data: `{"mac":"AA:BB:...","broadcast":..,"port":9,"packets":3,"applied":true,"txBytes":306}`
退出码: 0/2（Mac 格式非法或未加 `--yes`）

### 10.17 `net.forward` — 端口转发（netsh portproxy，v0.3）

价值: 中（暴露容器/WSL 服务给局域网；任务描述里点名的"端口转发/占位"能力） · 复杂度: M · 风险: **D** + **A** + N
用法: `net.forward list | net.forward add --listen <addr:port> --connect <addr:port> [--protocol tcp|udp] [--dry-run|--yes] | net.forward remove --listen <addr:port> [--dry-run|--yes]`
data: `list`: `{"items":[{"listenAddress":"0.0.0.0","listenPort":8080,"connectAddress":"127.0.0.1","connectPort":3000}],"count":N,"ipv6Forwarding":false,"sources":["netsh:portproxy"]}`
`add`: `{"listen":..,"connect":..,"applied":true,"backup":{"file":"...\\undo\\<txid>\\portproxy.txt"},"txid":..,"firewallRuleAdded":false}`
退出码: 0/2/3/4/6
* 实现：`netsh interface portproxy add v4tov4 ...`（**需要**管理员）。
* `add` 时默认**不**自动加防火墙规则（要 `--allow-firewall` 才调 `netsh advfirewall`）——因为放开入站是安全决策，必须显式。
* 回滚：`remove` 用 listen 定位删除；`list` 结果写入 undo 备份以便整体还原。

---

## 11. 安全与完整性

> 本组是"判断可信性"的工具箱。原则：**只读优先**，需要改系统状态的一律 `--yes` + 备份 + undo。

### 11.1 `sign.verify` — Authenticode 签名校验（**无 20s 硬超时**）

价值: **很高**（v0.1 的核心痛点之一：electron-updater 用 20 秒硬超时调 `Get-AuthenticodeSignature`，
本机实测需 32–45 秒导致升级失败 —— 本命令必须不做该限制并回报真实耗时） · 复杂度: M · 风险: 无（`--revocation online` 时 N）
依赖: P/Invoke `WinVerifyTrust`（`wintrust.dll`）+ `X509Certificate.CreateFromSignedFile`（取签名者）+ `X509Chain`
用法: `sign.verify <path>... [--all-signatures] [--revocation none|online|cache-only] [--timestamp] [--catalog] [--timeout 0|120s] [--jsonl] [--parallel 4] [--deep]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--revocation <mode>` | **`none`** | 吊销检查；`online` 会显著变慢（网络），**默认关**（这正是原来慢/超时的根源之一） |
| `--timeout <dur>` | **0（不限）** | 命令级自发超时；默认不限，与"不要 20s 硬超时"一致；agent 需要上限时自己给 |
| `--catalog` | true | 允许通过目录签名（catalog）验证（Windows 自家文件的常见情况） |
| `--all-signatures` | false | 返回**所有**签名（含双签名/嵌套签名），不只是主签名 |
| `--timestamp` | true | 回报签名时间戳（RFC3161 与 Authenticode 时间戳都要支持） |
| `--deep` | false | 对目录递归（`--path`）批量校验 |

data: `{"items":[{"path":"C:\\...\\app.exe","status":"Valid","statusMessage":"签名有效","verified":true,"signed":true,"elapsedMs":38200,"signers":[{"subject":"CN=OpenAI, O=OpenAI, L=San Francisco, S=California, C=US","issuer":"CN=DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1","thumbprint":"...","notBefore":..,"notAfter":..,"serialNumber":"...","timestamp":{"present":true,"time":..,"authority":"CN=DigiCert SHA2 Timestamp Responder","type":"RFC3161","verified":true},"chain":[{"subject":..,"issuer":..,"status":"Valid","revocationStatus":"Unknown"}],"chainErrors":[],"isCounterSignature":false}],"publisher":"OpenAI","product":"ChatGPT","fileVersion":"1.2025.1","catalogSigned":false,"revocation":"none","trustError":{"code":2148204800,"message":"证书链已处理，但终止于不受信任的根证书"}}],"count":N,"validCount":N,"invalidCount":N,"unsignedCount":N,"elsapsedMsAvg":38200,"columns":["path","status","publisher","elapsedMs"]}`
退出码: 0（全部有效）/ **1**（有无效/未签名 —— agent 主判据）/ 3 / 4 / 5（仅当显式给了 `--timeout`）/ 6（部分项异常）
* `WinVerifyTrust` 需 `WINTRUST_DATA` 结构体（`WTD_REVOKE_NONE`/`WTD_REVOKE_WHOLECHAIN`、`WTD_CHOICE_FILE`、`WTD_STATEACTION_VERIFY/CLOSE`）；
  **必须**在结束前用 `WTD_STATEACTION_CLOSE` 释放，否则内存泄漏/句柄泄漏。
* 时间戳验证：解析 `PKCS#7` 的 unsigned attributes（`szOID_RFC3161_counterSign` = `1.3.6.1.4.1.311.3.3.1`）。
* **不要**用 PowerShell 的 `Get-AuthenticodeSignature`（就是它带来 20s 超时的调用链，也是我们的替代理由）。

### 11.2 `sign.chain` — 证书链构与信任判定

价值: 中高（解释"为什么签名无效"） · 复杂度: M · 风险: N（online 吊销）
用法: `sign.chain <path|cer|pfx> [--revocation none|online] [--extra-store] [--disable-ai] [--url-retrieval] [--pfx-password <s>]`
data: `{"subject":..,"issuer":..,"chains":[[{"subject":..,"issuer":..,"thumbprint":..,"notBefore":..,"notAfter":..,"status":["UntrustedRoot"],"statusText":"不受信任的根"}]],"valid":false,"trustedRoots":["CN=DigiCert Global Root G2"],"revocationMode":"none","buildFlags":["DisableAI"],"columns":["depth","subject","status"]}`
退出码: 0 链有效 / **1 链无效** / 2（密码错/PFX 无法打开）/ 3 / 4 / 5
* **必须** `chain.ChainPolicy.DisableAI = true`（禁用 AuthRoot 自动更新下载），否则离线机器上会静默联网/卡住。

### 11.3 `sign.verify-dir` — 批量签名校验（含递归、并行、CSV 报告）

价值: 高（"这个目录里哪些 exe 没签名"） · 复杂度: S（复用 11.1）· 风险: 无
用法: `sign.verify-dir --path <dir> [S0-S9] [--ext exe,dll,sys,ps1,msi,cab] [--only-unsigned] [--only-invalid] [--out <file>] [--format csv|jsonl|md] [--parallel 4] [--revocation none]`
data: `{"items":[同 sign.verify 的 item],"count":N,"signed":N,"unsigned":N,"invalid":N,"byPublisher":{"Microsoft Corporation":420},"slowest":[{"path":..,"elapsedMs":45000}],"columns":["path","status","publisher","elapsedMs"]}`
退出码: 0（全部有效）/ **1**（有未签名或无效）/ 3/4/5/6
* 并行度默认 **4**（不是 CPU 核数）：签名验证会调 crypt32，过高并行反而更慢且可能触发系统的网络吊销节流。
* `--out` 落 CSV 时用 `--utf8-bom`（Excel 中文友好）。

### 11.4 `sign.timestamp` — 只取签名时间戳信息

价值: 中（判断"这个文件是不是最近签的"、构建时间线） · 复杂度: S · 风险: 无
用法: `sign.timestamp <path>... [--jsonl]`
data: `{"items":[{"path":..,"signed":true,"timestamp":..,"timestampAuthority":..,"type":"RFC3161","verified":true}],"count":N,"columns":["path","timestamp","authority"]}`

### 11.5 `pe.info` — PE 头与版本信息

价值: 高（判断文件是什么、是否 .NET、是否被加壳） · 复杂度: M · 风险: 无
用法: `pe.info <path>... [--sections] [--entropy] [--checksum-verify] [--dotnet]`
data: `{"items":[{"path":..,"magic":"PE32+|PE32","machine":"AMD64","subsystem":"Windows GUI","characteristics":["DLL"],"timestamp":..,"linkerVersion":"14.38","entryPoint":"0x1234","imageBase":"0x140000000","sizeOfImage":123456,"sections":[{"name":".text","vsize":N,"rawSize":N,"entropy":6.12,"characteristics":["CODE","EXECUTE"]}],"isDotnet":true,"clrVersion":"v4.0.30319","dotnetFlags":["ILOnly"],"overlayBytes":1024,"checksumOk":true,"fileVersion":"1.2.3.4","productVersion":"..","company":"..","product":"..","originalFilename":"..","isSigned":true,"importCount":42,"delayImportCount":0}],"count":N,"columns":["path","machine","subsystem","isDotnet","fileVersion"]}`
退出码: 0 / 1（非 PE 或损坏 → `E_FORMAT`，**不是** 3）/ 3 / 4
* 手写 PE 解析（`IMAGE_DOS_HEADER` → `IMAGE_NT_HEADERS` → section table → data directories），**不依赖** `System.Reflection`（对 native 无用）。
* `--dotnet` 读 CLR 头（`IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR`）；版本信息用 `FileVersionInfo`（BCL，S）。

### 11.6 `pe.imports` — 导入表/导入函数（v0.3）

价值: 中高（恶意行为研判：`CreateRemoteThread`、`VirtualAllocEx` 等危险 API） · 复杂度: L · 风险: 无
用法: `pe.imports <path>... [--dll <name>] [--flags suspicious] [--delay] [--resolve-names]`
data: `{"items":[{"path":..,"dlls":[{"name":"KERNEL32.dll","functions":[{"name":"CreateFileW","ordinal":null,"byOrdinal":false,"delay":false}],"suspicious":["WriteProcessMemory"]}]}],"count":N,"suspiciousTotal":3,"columns":["dll","function","delay"]}`
退出码: 0/1/3/4
* 解析 ordinal-only 导入需要 `.rdata` 里的名称表；`--resolve-names` 需加载系统 dll（**不做**，避免 DLL 加载副作用）→ 只报 ordinal 数字。

### 11.7 `pe.strings` — 可打印字符串提取

价值: 中高（快速看文件里有没有 URL/路径/密钥/错误信息） · 复杂度: S · 风险: P
用法: `pe.strings <path> [--min-length 4] [--encoding ascii,utf16le] [--match <re>] [--urls-only] [--paths-only] [--max 500] [--offset]`
data: `{"items":[{"offset":4096,"encoding":"ascii","text":"https://api.example.com/v1"}],"count":N,"byKind":{"url":12,"path":30,"other":120},"columns":["offset","encoding","text"]}`
退出码: 0/3/4/**1**（`--urls-only`/`--match` 无命中时可选，默认 0）
* 只扫前 `--max-bytes`（默认 16MB）；**不做**反混淆/解密。

### 11.8 `cert.list` — 证书存储清点

价值: 中高（找过期证书、可疑根证书） · 复杂度: S/M · 风险: P
用法: `cert.list [--store My|Root|CA|TrustedPublisher|all] [--scope user|machine|both] [--expiring 30d] [--match <re>] [--with-private-key] [--sort expiry]`
data: `{"items":[{"store":"My","scope":"machine","subject":"CN=...","issuer":"CN=...","thumbprint":..,"notBefore":..,"notAfter":..,"daysToExpiry":62,"hasPrivateKey":true,"keyAlgorithm":"RSA","keySize":2048,"signatureAlgorithm":"sha256RSA","eku":["1.3.6.1.5.5.7.3.3"],"selfSigned":false,"serialNumber":..}],"count":N,"expiringCount":2,"selfSignedRoots":N,"columns":["store","subject","notAfter","daysToExpiry"]}`
退出码: 0 / **1**（`--expiring` 命中）/ 3（store 名不存在）/ 4（Root 存储修改类操作这里没有，读取通常免提权；但 machine Root 的某些读取需提权 → 降级）/ 5
* 用 `X509Store`（`StoreLocation`）；**只读**，绝不提供证书删除/导入（见不做清单）。

### 11.9 `cert.info` — 单个证书文件解析

价值: 中（看 .cer/.pfx/.pem 里到底是什么） · 复杂度: S · 风险: P
用法: `cert.info <file> [--password <s>] [--dump-pem] [--chain] [--key-info]`
data: `{"file":..,"format":"pfx|cer|pem|der","certificates":[{"subject":..,"issuer":..,"thumbprint":..,"notBefore":..,"notAfter":..,"serialNumber":..,"keyUsage":["DigitalSignature"],"extendedKeyUsage":["Server Authentication"],"san":["dns:example.com"],"basicConstraints":"CA:FALSE","hasPrivateKey":true,"keySize":2048}],"count":N,"pemBlocks":["CERTIFICATE","PRIVATE KEY"]}`
退出码: 0/1（格式/密码错）/3/4

### 11.10 `acl.get` — 读取 ACL（DACL/owner/审计）

价值: 高（权限排查、审计） · 复杂度: M · 风险: P
依赖: `File.GetAccessControl`/`Directory.GetAccessControl`（`System.Security.AccessControl`，mscorlib 内）+ `Fs.LongPath`
用法: `acl.get <path>... [--sacl] [--inherited] [--effective --for <user>] [--format text|sddl|json] [--recurse] [--depth 1]`
data: `{"items":[{"path":..,"owner":"BUILTIN\\Administrators","group":"PC\\None","daclProtected":false,"sddl":"D:AI(A;ID;FA;;;BA)","aces":[{"identity":"NT AUTHORITY\\SYSTEM","type":"Allow","rights":["FullControl"],"inherited":true,"inheritedFrom":..,"flags":["Inherited","ContainerInherit"]}],"sacl":"(none)","auditAces":[],"effectiveFor":null}],"count":N,"failures":[],"columns":["path","identity","type","rights","inherited"]}`
退出码: 0/3/4（读 DACL 需 READ_CONTROL，多数用户对自己的文件有）/6
* `--effective --for <user>`：用 `AuthzAccessCheck` 或 `GetEffectiveRightsFromAcl`（P/Invoke `authz.dll`/`advapi32.dll`）计算有效权限（M）；失败降级为只回 ACL。
* `--format sddl` 直接给 `FileSecurity.GetSecurityDescriptorSddlForm`（喂给其它工具最方便）。

### 11.11 `acl.set` — 修改 ACL（v0.3，受控）

价值: 中（修"拒绝访问"、给服务账号授权） · 复杂度: M · 风险: **D** + A + **自锁风险**
用法: `acl.set <path> --grant <user>:<rights> [--deny <user>:<rights>] [--remove <user>] [--set-owner <user>] [--reset-inheritance] [--copy-from <path>] [--recurse] [--recurse-mode container|object] [--dry-run|--yes]`
data: `{"plan":[{"path":..,"identity":"PC\\me","rights":"Modify","action":"grant"}],"applied":true,"before":{...},"after":{...},"backupSddl":"...\\undo\\<txid>\\sddl.json","txid":..,"failures":[]}`
退出码: 0/2/3/4/6
* 语义按钮：`read`(RX)、`write`(W)、`modify`(M)、`full`(F)、`list`、`execute`。
* **强制**：改 ACL 前把原 SDDL 存进 undo（可一键还原）；`--recurse` 时按目录顺序自底向上（避免改了父目录后无法遍历子项）；
  **拒绝**把 Everyone 设成 FullControl 除非 `--i-know`（并只做警告不做禁止）。
* **绝不**触碰 `C:\Windows`、`C:\Program Files`、用户 Profile 根（与 §2.4 同一套保护规则）。

### 11.12 `acl.copy` — 复制 ACL/属主到另一路径

价值: 中（批量对齐权限） · 复杂度: M · 风险: **D**
用法: `acl.copy --from <src> --to <dst> [--owner] [--recurse] [--dry-run|--yes]`
data: `{"from":..,"to":..,"applied":true,"itemsCopied":N,"failures":[],"txid":..}`

### 11.13 `acl.reset` — 恢复继承（去掉所有显式 ACE）

价值: 中（"权限被我改乱了"的急救） · 复杂度: S · 风险: **D**
用法: `acl.reset <path>... [--recurse] [--keep-owner] [--dry-run|--yes]`
data: `{"plan":[{"path":..,"acesRemoved":3}],"applied":true,"failures":[],"txid":..}`
* 等价 `icacls <path> /reset /T`；**必须先备份 SDDL**。

### 11.14 `defender.status` — Defender 状态

价值: 高（判断杀软是否在运行/实时保护是否开启） · 复杂度: M · 风险: 无/A
依赖: PowerShell `Get-MpComputerStatus`（优先）或 WMI `root\Microsoft\Windows\Defender:MSFT_MpComputerStatus`（需管理员）
用法: `defender.status [--full] [--via auto|powershell|wmi]`
data: `{"available":true,"antivirusEnabled":true,"realTimeProtectionEnabled":true,"behaviorMonitorEnabled":true,"ioavProtectionEnabled":true,"antispywareEnabled":true,"tamperProtectionEnabled":true,"engineVersion":"1.1.24010.1","signatureVersion":"1.411.1.0","signatureLastUpdated":..,"signatureAgeHours":5,"quickScanAgeDays":1,"fullScanAgeDays":null,"lastQuickScan":..,"runningMode":"Normal","isVirtualMachine":false,"sources":["powershell:Get-MpComputerStatus"],"columns":["key","value"]}`
退出码: 0 / **1**（`antivirusEnabled:false` 或实时保护关闭 —— 这是 agent 应该看到的"危险"信号）/ 4 / 3（Defender 不可用/被第三方杀软接管 → `available:false` 返回 0 + hint）
* 三源降级：`Get-MpComputerStatus`（最快）→ WMI `MSFT_MpComputerStatus` → 注册表 `HKLM\SOFTWARE\Microsoft\Windows Defender` 键存在性（只判断是否可用）。
* **不要**依赖单一源：第三方杀软接管时 Defender 命令会报错，这本身是有效信息。

### 11.15 `defender.exclusions` — 排除项查/增/删（**需提权**）

价值: 高（构建/编译被误杀时最常用的修复；也是我们给 DSH 自身加白名单的手段） · 复杂度: M · 风险: **D** + **A**（安全影响极大）
用法: `defender.exclusions list [--json]`
`defender.exclusions add --path <dir|file> [--process <name>] [--extension <ext>] [--dry-run|--yes]`
`defender.exclusions remove (--path <p>|--process <n>|--extension <e>) [--dry-run|--yes]`
data: `list`: `{"paths":["C:\\dev"],"processes":["node.exe"],"extensions":[".tmp"],"applied":false,"requiresElevation":true}`
`add/remove`: `{"plan":[{"kind":"path","value":"C:\\dev","action":"add"}],"before":{...},"after":{...},"applied":true,"txid":..,"revertCommand":"dsh-toolbox defender exclusions remove --path \"C:\\dev\" --yes"}`
退出码: 0/2/3/4（**非管理员 → `E_DENIED` + hint "需要管理员 PowerShell/CMD"**）/6
* 实现：`Add-MpPreference -ExclusionPath` / `Set-MpPreference`，或用 WMI `MSFT_MpPreference` 的 `ExclusionPath`/`ExclusionProcess`/`ExclusionExtension` 属性写入。
* **必须**在输出里给出 `revertCommand`（一条可直接执行的撤销命令），这是安全设计的核心：
  加排除项是有风险的持久化改动，必须"一键可撤"。
* 默认**只允许**加当前用户 Profile 下的路径（`%USERPROFILE%`、`%TEMP%`）与项目目录；系统路径（`C:\Windows`）需 `--i-know`。

### 11.16 `defender.scan` — 触发扫描

价值: 中（验证样本是否被检出） · 复杂度: S/M · 风险: **D**（占用大量 CPU）+ A（部分模式）
用法: `defender.scan --path <dir>|--quick|--full [--async] [--timeout 10m] [--dry-run|--yes]`
data: `{"scanType":"custom","path":..,"started":true,"async":false,"completed":true,"threatsFound":0,"elapsedMs":45000,"applied":true,"txid":..}`
退出码: 0/2/5（超时但扫描仍在后台运行 → `async:true` 提示）/4
* `--async` 默认 **true**（`Start-MpScan -AsJob` 语义），避免 agent 被一次全盘扫描卡 30 分钟；同步模式才等结果。

### 11.17 `defender.threats` — 威胁历史

价值: 中高（"刚才那个 exe 是不是被杀了"） · 复杂度: S · 风险: P
用法: `defender.threats [--since 7d] [--active-only] [--max 50]`
data: `{"items":[{"threatId":..,"name":"Trojan:Win32/Wacatac.B!ml","severity":"Severe","category":"Trojan","status":"Quarantined","detectedAt":..,"actionSuccess":true,"resources":["C:\\x\\tool.exe"],"process":"node.exe"}],"count":N,"activeCount":0,"columns":["name","severity","status","detectedAt","resources"]}`
退出码: 0 / **1**（有未处理的 active 威胁）/ 3/4/5
* 实现用 `Get-MpThreatDetection` / `Get-MpThreat`（PowerShell）或 Event Log 的 `Microsoft-Windows-Windows Defender/Operational`（id 1116/1117）作为降级 —— **两条路都写上**，因为第三方杀软环境下 PowerShell 路径会失败。

---

## 12. 开发与构建辅助

### 12.1 `dev.build` — 构建 dsh-toolbox 自身（调用 build.ps1）

价值: 高（agent 自己改代码后要能立刻构建验证 —— 这正是本仓库的工作流） · 复杂度: S · 风险: **X**（执行脚本）
用法: `dev.build [--out <name>] [--test] [--debug] [--warnaserror] [--root <dir>] [--timeout 5m] [--yes]`
data: `{"root":..,"script":..,"exit":0,"ms":4200,"out":"...\\dist\\dsh-toolbox.exe","bytes":59904,"errors":0,"warnings":3,"tail":["...最后 20 行输出..."],"artifacts":[{"path":..,"bytes":N,"sha256":..}]}`
退出码: 0 构建成功 / 1（编译错误，**并把 `errors`/`failures` 填好**）/ 3（找不到 build.ps1）/ 5
* 默认 `--out` 用**独立名字**（如 `dsh-toolbox-<user>.exe`），避免多人并行构建互相覆盖（`build.ps1 -Out` 已支持）。
* 必须捕获 stdout/stderr 并把最后 20 行放进 `data.tail`（agent 不用再跑一次看错误）。

### 12.2 `dev.compile` — 用仓库内 Roslyn 编译任意 C#（脚手架）

价值: 高（agent 写小程序验证想法、编译单文件工具；这也是本仓库"零 SDK"工作流的核心能力） · 复杂度: M · 风险: **X**（产出可执行文件）
依赖: `.tools\roslyn-4.14.0\tasks\net472\csc.exe`（路径发现逻辑与 `build.ps1` 一致：`Get-ChildItem .tools -Recurse -Filter csc.exe` 里取 `net472` 最新的）
用法: `dev.compile --src <file>... [--out <exe|dll>] [--target exe|library|winexe] [--ref <dll>]... [--define SYM]... [--unsafe] [--langversion latest] [--respond-file] [--keep-rsp] [--timeout 2m] [--yes]`
data: `{"out":..,"target":"exe","exit":0,"ms":1800,"bytes":4608,"errors":[],"warnings":[{"code":"CS0219","message":"...","file":..,"line":12}],"rspFile":..,"sha256":..}`
退出码: 0/1（编译错误）/2/3/4/5
* 默认引用集：与 `build.ps1` 相同的 `$wantRefs`（System/System.Core/System.Xml/System.Management/... ）——
  **实现时把该列表抽到共享常量**，避免两处漂移（建议 Lead 在 `Core` 里放 `BuildRefs`），否则 `dev.compile` 会经常"缺引用"。
* `--respond-file`：把参数写到临时 rsp 再传给 csc（避免 32KB 命令行上限；本仓库 build.ps1 就是这么做的）。
* csc 路径与 `-nostdlib-`/`/codepage:65001` 等开关照抄 `build.ps1`，否则中文源码会挂。

### 12.3 `dev.run-cs` — 编译并运行 C# 片段（**X，必须显式确认**）

价值: 中高（agent 做一次性计算/转换，不用落一堆临时文件） · 复杂度: M · 风险: **X**（执行代码）
用法: `dev.run-cs [--code <s>|--src <file>] [--arg <a>]... [--stdin <s>] [--timeout 30s] [--out <exe>] [--dry-run|--yes]`
data: `{"exit":0,"ms":320,"stdout":"...","stderr":"","stdoutBytes":12,"exe":..,"compiledMs":1500}`
退出码: 0/1（编译或运行非 0）/2（缺 `--yes`）/5
闸门: **强制**（这是"执行外部代码"）。`--code` 内容需落在临时目录（`Paths.Cache\snippets\<hash>\`），可被 `cache clean` 回收。
* 与 §14.9 `run` 的关系：`dev.run-cs` 负责"编译"，之后**必须**把执行交给 `run`（受同一 exec policy 约束），
  不要在 `dev.run-cs` 里另起一套进程执行逻辑。

### 12.4 `git.status` — Git 状态摘要（结构化）

价值: 高（agent 改完仓库要确认改了什么） · 复杂度: S · 风险: 无
依赖: 外部 `git.exe`（PATH 或 `--git <path>`）；不存在 → exit 3 + `E_TOOL_MISSING`
用法: `git.status [--path <dir>] [--porcelain] [--untracked] [--branch] [--ahead-behind] [--stash]`
data: `{"repo":"C:\\Users\\ptfm\\DSH-Toolbox","branch":"main","upstream":"origin/main","ahead":1,"behind":0,"detached":false,"head":"abc1234","dirty":true,"items":[{"path":"docs/FEATURES.md","x":"?","y":"?","status":"untracked","staged":false,"renamed":false,"oldPath":null}],"counts":{"modified":2,"added":1,"deleted":0,"untracked":3,"conflicted":0},"stashes":0,"columns":["status","path"]}`
退出码: 0/3（不是仓库/无 git）/5
* 解析用 `git status --porcelain=v2 --branch -z`（**`-z` 防路径含空格/换行解析错**），不要用默认人类输出。

### 12.5 `git.diff` — 差异摘要/内容

价值: 高（生成评审意见、确认改动范围） · 复杂度: S/M · 风险: 无
用法: `git.diff [--path <dir>] [--staged] [--rev <a>..<b>] [--stat] [--name-only] [--patch] [--context 3] [--max-lines 5000] [--include-untracked] [--file <path>]`
data: `{"repo":..,"rev":"HEAD","staged":false,"files":[{"path":"src/Core/Cli.cs","status":"M","added":12,"removed":3,"binary":false,"patch":null}],"totalAdded":N,"totalRemoved":N,"stat":"...","patchTruncated":false,"columns":["status","path","added","removed"]}`
退出码: 0（无差异或有差异都返回 0）/**1 有差异**（仅 `--exit-code` 时）/3/5
* `--stat` 用 `git diff --numstat -z`（机器可读）；`--patch` 用 `git diff --unified=<n>` 并做 `--max-lines` 截断。

### 12.6 `git.log` — 提交历史（结构化）

价值: 中高（"最近改了什么"、追责/回溯） · 复杂度: S · 风险: 无
用法: `git.log [--path <dir>] [--max 20] [--since 1w] [--author <re>] [--path-filter <glob>] [--stat]`
data: `{"items":[{"sha":"abc1234","short":"abc1234","author":"ptfm","email":..,"date":..,"subject":"...","parents":["..."],"refs":["HEAD -> main"],"filesChanged":3,"added":120,"removed":8}],"count":N,"columns":["short","date","author","subject"]}`
* 用 `git log --pretty=format:%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%P%x1f%D%x1f%s -z`（`\x1f` 分隔 + `-z` 记录分隔，避免解析歧义）。

### 12.7 `git.repo` — 仓库摘要（给 agent 的"项目速览"）

价值: 高（agent 进新仓库第一步） · 复杂度: M · 风险: 无
用法: `git.repo [--path <dir>] [--top 20] [--language] [--readme]`
data: `{"repo":..,"root":..,"branch":..,"remote":[{"name":"origin","url":..}],"head":..,"commitCount":120,"firstCommit":..,"lastCommit":..,"contributors":N,"languages":{"C#":0.92,"PowerShell":0.08},"tracked":42,"untracked":5,"sizeBytes":N,"readme":{"path":"README.md","firstLines":["..."]},"gitDir":..,"isBare":false,"columns":["key","value"]}`
退出码: 0/3/5
* `--language` 用 `git ls-files -z` 按扩展名统计（**不要**跑 `linguist`）；`--readme` 只读前 20 行。

### 12.8 `path.resolve` — 路径解析/规范化

价值: 高（agent 到处在拼路径，最容易出错） · 复杂度: S · 风险: 无
用法: `path.resolve [<path>...] [--cwd <dir>] [--relative-to <dir>] [--absolute] [--long-path] [--short-name] [--exists] [--case-check]`
data: `{"items":[{"input":"..\\x\\..\\y","absolute":"C:\\Users\\ptfm\\y","normalized":"C:\\Users\\ptfm\\y","exists":false,"isDir":false,"isReparsePoint":false,"longPath":..,"shortName":null,"parent":"C:\\Users\\ptfm","root":"C:\\","parts":["C:","Users","ptfm","y"],"caseMismatch":null,"kind":"file"}],"count":N,"columns":["input","absolute","exists"]}`
退出码: 0 / 1（`--case-check` 发现大小写与磁盘实际不符）/ 2（路径非法）

### 12.9 `path.info` — 单路径详细信息（体积/属主/时间/卷）

价值: 中高（ls -l 的结构化增强版） · 复杂度: S/M · 风险: 无
用法: `path.info <path>... [--hash sha256] [--acl] [--ads] [--zone] [--volume]`
data: `{"items":[{"path":..,"kind":"file","bytes":12345,"allocatedBytes":16384,"mtime":..,"ctime":..,"atime":..,"attrs":["Archive"],"owner":..,"volume":"C:","fileId":"0x...","hardLinks":1,"isReparsePoint":false,"reparseTag":null,"target":null,"hash":"...","ads":[":Zone.Identifier"],"zone":3}],"count":N,"columns":["path","kind","bytes","mtime"]}`
退出码: 0/3/4

### 12.10 `env.patch` — 环境变量补丁（进程/用户级）

价值: 中高（设置 PATH、给后续命令注入变量） · 复杂度: M · 风险: **D** + A（机器级）
用法: `env.patch [--set K=V]... [--append K=V] [--prepend K=V] [--remove K]... [--scope process|user|machine] [--dry-run|--yes]`
data: `{"scope":"user","plan":[{"key":"PATH","action":"prepend","beforeLen":1200,"afterLen":1250,"value":"C:\\tools"}],"applied":true,"backup":"...\\undo\\<txid>\\env-user.json","txid":..,"broadcast":true}`
退出码: 0/2/3（变量不存在且 `--remove`）/4/6
* `scope=user`：写 `HKCU\Environment` 后必须广播 `WM_SETTINGCHANGE`（`SendMessageTimeout(HWND_BROADCAST, ..., "Environment")`），
  否则新进程看不到（P/Invoke，M）。
* `scope=process`：只改当前进程（对 agent 基本无用，因为命令执行完就退出）→ 默认 `user`，并在 hint 里说明
  "对子进程生效请用 `run --env K=V`"（§14.9）。
* 展开 `%VAR%` 语义差异：注册表里 `REG_EXPAND_SZ` 保留变量引用，`REG_SZ` 立即展开 → `--keep-refs` 控制。

### 12.11 `dev.ps` — 运行 PowerShell 片段（X，v0.3）

价值: 中高（PowerShell 覆盖面极广；很多系统信息只有它有 cmdlet） · 复杂度: S · 风险: **X** + P
用法: `dev.ps [--script <s>|--file <f>] [--arg <a>]... [--json] [--no-profile] [--timeout 60s] [--out <file>] [--dry-run|--yes]`
data: `{"exit":0,"ms":420,"stdout":"...","stderr":"","objects":[..],"truncated":false}`
退出码: 0/1/2/5
* 默认 `-NoProfile -NonInteractive -ExecutionPolicy Bypass`；脚本走 `-Command -`（stdin）避免转义地狱。
* `--json` 时在脚本尾部追加 `| ConvertTo-Json -Depth 10 -Compress`（由我们包一层）→ 结构化输出。
* 与 `run` 的关系同 §12.3：执行必须经 exec policy。

---
## 13. 媒体、桌面与文档（扩展想象力：agent 的"眼睛和手"）

> 可行性前提：`build.ps1` 已引用 `System.Drawing.dll` 与 `System.Windows.Forms.dll`，所以图像/剪贴板/窗口能力**不需要新依赖**。
> 但注意两个硬约束：(1) `Clipboard` 与多数 COM/WinForms API 要求 **STA 线程**，而 `Program.Main` 现在没有 `[STAThread]`
> → 必须在专用 STA 线程里跑（`new Thread(...) { SetApartmentState(ApartmentState.STA) }`），**不要**依赖给 Main 加属性（附录 A）；
> (2) 无桌面会话（服务/SSH/计划任务）时这些命令必须返回 `exit 1` + `E_NO_SESSION`，不能崩。

### 13.1 `image.info` — 图像元信息

价值: 中高（素材清点、生成缩略图前的判断） · 复杂度: S · 风险: 无
依赖: `System.Drawing.Image.FromFile`（只读头，不加载像素）
用法: `image.info <path>... [--exif] [--gps]`
data: `{"items":[{"path":..,"format":"Png","width":1920,"height":1080,"dpiX":96,"dpiY":96,"pixelFormat":"Format32bppArgb","bytes":123456,"hasAlpha":true,"frameCount":1,"exif":{"DateTimeOriginal":"2025:01:02 03:04:05","Make":"Apple","Model":"iPhone 15","Orientation":1,"GPS":{"lat":31.23,"lon":121.47}}}],"count":N,"totalPixels":N,"columns":["path","format","width","height","bytes"]}`
退出码: 0/1（非图像/损坏 → `E_FORMAT`）/3/4/5
* 用 `Image.FromStream(new MemoryStream(File.ReadAllBytes(path)))` 并**显式限定**读取（`Image.FromFile` 会锁定文件句柄）；
  超大图只读头（`--headers-only` 默认 true，用 `Image.FromStream(..., validateImageData:false)`）。

### 13.2 `image.convert` — 格式转换

价值: 中（png↔jpg↔bmp↔gif↔ico；生成 favicon） · 复杂度: S · 风险: **D**（写文件）
用法: `image.convert --path <dir> [S0-S9] --to png|jpg|bmp|gif|ico|tiff [--quality 85] [--background #FFFFFF] [--out-dir <dir>] [--overwrite] [--dry-run|--yes]`
data: `{"converted":N,"skipped":N,"failures":[],"items":[{"from":..,"to":..,"bytesBefore":N,"bytesAfter":N}],"applied":true,"txid":..,"columns":["from","to","bytesAfter"]}`
退出码: 0/2/3/4/6
* JPEG 质量用 `ImageCodecInfo` + `EncoderParameters`；PNG 无质量参数（忽略并 `note`）。
* ICO 多尺寸：`--ico-sizes 16,32,48,256`（自写 ICO 容器头，S/M）。

### 13.3 `image.resize` — 缩放/裁剪

价值: 中高（给 agent 生成"能塞进上下文的小图"是刚需：截图太大时可读性优先） · 复杂度: S/M · 风险: **D**
用法: `image.resize --path <dir> [S0-S9] [--width 1024] [--height 0] [--max-side 1568] [--fit cover|contain|stretch] [--crop x,y,w,h] [--rotate 90] [--quality 85] [--out-dir <dir>] [--suffix .small] [--dry-run|--yes]`
data: `{"items":[{"from":..,"to":..,"fromSize":[1920,1080],"toSize":[1024,576],"bytes":..}],"count":N,"applied":true,"txid":..}`
* `--max-side` 是最常用形态（保持比例，长边不超过 n）；`--fit contain` 时补背景色。
* 用 `Graphics` + `InterpolationMode.HighQualityBicubic` + `PixelOffsetMode.HighQuality`（质量差异巨大）。

### 13.4 `image.capture` — 截屏/窗口截图

价值: 高（agent 需要"看"GUI；GUI 自动化/验收的最基本能力） · 复杂度: M · 风险: **P**（屏幕内容含隐私）+ **D**（写文件）
用法: `image.capture [--out <file>] [--window <title|handle|foreground>] [--monitor all|1|2|primary] [--region x,y,w,h] [--cursor] [--format png|jpg] [--max-side 1568] [--dry-run(仅预览目标) |--yes]`
data: `{"out":"...\\shot-20260217-101530.png","bytes":N,"width":1920,"height":1080,"monitors":[{"index":1,"bounds":[0,0,1920,1080],"primary":true,"dpi":96}],"window":{"title":"Notepad","handle":"0x001234","bounds":[...]},"applied":true}`
退出码: 0/1（无桌面会话 → `E_NO_SESSION`）/2（`--window` 找不到）/3/4
* 多显示器与 DPI 缩放：必须用 `SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE)`（P/Invoke `shcore.dll`）否则
  在 150% 缩放下截出来的图会偏移/模糊（**最经典的坑**）。
* 默认 `--out` 落到 `Paths.Home\captures\`；配合 `image.resize --max-side 1568` 得到适合模型输入的大小。

### 13.5 `image.compare` — 图像差异（像素级 + 阈值）

价值: 中（UI 回归验证、确认"改动是否影响了渲染"） · 复杂度: M · 风险: **D**（写 diff 图）
用法: `image.compare <a> <b> [--tolerance 8] [--diff-out <file>] [--fail-percent 0.1] [--ignore-anti-aliasing]`
data: `{"same":false,"sizeA":[1920,1080],"sizeB":[1920,1080],"differentPixels":1234,"differentPercent":0.059,"maxChannelDelta":128,"bbox":[120,340,400,380],"diffImage":..,"columns":[]}`
退出码: 0 相同（在容差内）/ **1 有差异** / 2（尺寸不一致）/ 3/4
* 逐像素比较用 `Bitmap.LockBits`（`Marshal.Copy` 到 `byte[]`），**不要** `GetPixel`（慢 1000×）。

### 13.6 `window.list` — 窗口清点

价值: 中高（GUI 自动化第一步：找到目标窗口） · 复杂度: M · 风险: **P**
依赖: P/Invoke `user32.dll`：`EnumWindows`/`GetWindowTextW`/`IsWindowVisible`/`GetWindowRect`/`GetWindowThreadProcessId`
用法: `window.list [--visible-only] [--title-regex <re>] [--class-regex <re>] [--process <n|re>] [--all-desktops]`
data: `{"items":[{"handle":"0x0021A3","title":"未命名 - 记事本","class":"Notepad","pid":1234,"process":"notepad.exe","visible":true,"minimized":false,"maximized":false,"foreground":true,"bounds":[100,100,900,700],"monitor":1,"zOrder":0}],"count":N,"foreground":"0x0021A3","columns":["handle","title","process","bounds"]}`
退出码: 0（含 0 命中）/ 5
* 必须用 `EnumWindows` + `GetWindowTextLength` 处理空标题（`GetWindowTextW` 对跨进程窗口可能取不到 → 用 `SendMessageTimeoutW(WM_GETTEXT)` 兜底）。

### 13.7 `window.control` — 窗口操作（激活/移动/最小化/关闭）

价值: 中（GUI 自动化；关闭弹窗） · 复杂度: M · 风险: **D**（`close`/`kill` 影响用户前台程序）+ P
用法: `window.control --handle <h|--title-regex <re>> --action activate|focus|minimize|maximize|restore|move|resize|close|topmost [--bounds x,y,w,h] [--wait 200ms] [--dry-run|--yes]`
data: `{"plan":[{"handle":..,"title":..,"action":"activate"}],"results":[{"handle":..,"ok":true,"stateBefore":"minimized","stateAfter":"normal"}],"applied":true,"txid":..}`
退出码: 0/2（缺 `--yes`）/3（窗口不存在）/4
* `close` 先发 `WM_CLOSE`（优雅），`--force` 才用 `WM_ENDSESSION`/`TerminateProcess`（那属于 `proc.kill`）。
* 激活窗口受前台锁限制（`SetForegroundWindow` 会失败）→ 用 `AttachThreadInput` + `AllowSetForegroundWindow` 组合（M），失败时返回 `ok:false` 而不是报错。

### 13.8 `clipboard.get` / `clipboard.set` — 剪贴板读写

价值: 中高（agent 与用户手工操作之间的最简接口） · 复杂度: S（需 STA 线程） · 风险: **P**（内容可能含敏感信息）/ D（写）
用法: `clipboard.get [--format text|html|rtf|image|files|all] [--out <file>] [--max-bytes 1MB] [--show-secrets]`
`clipboard.set [--text <s>|--file <f>|--image <f>|--files <path>...] [--clear-first] [--dry-run|--yes]`
data: `get`: `{"formats":["Text","HTML Format"],"text":"...","html":null,"image":null,"files":["C:\\x\\a.txt"],"bytes":123,"truncated":false,"columns":[]}`
`set`: `{"formats":["Text"],"bytes":123,"applied":true}`
退出码: 0/1（`E_NO_SESSION` 无桌面）/2/3/4
* 必须重试 `Clipboard` 的 `ExternalException`（有进程占用剪贴板时抛 "CLIPBRD_E_CANT_OPEN"）→ 重试 5 次 × 100ms。
* 默认**不落盘**图片/明显敏感文本；`--out` 才写文件。

### 13.9 `office.xlsx.read` — 读 Excel（xlsx，只读）

价值: 高（用户给的表格是 agent 最常见的输入之一；**零依赖**实现的关键能力） · 复杂度: M · 风险: 无
依赖: `ZipFile`（已在引用中）+ `XmlReader`：xlsx = zip(`xl/workbook.xml`, `xl/worksheets/sheetN.xml`, `xl/sharedStrings.xml`)
用法: `office.xlsx.read <file> [--sheet <name|index>] [--range A1:D100] [--header] [--max-rows 1000] [--formulas] [--out <file>] [--json] [--csv]`
data: `{"sheet":"Sheet1","sheets":["Sheet1","数据"],"rows":N,"cols":N,"header":["名称","数量"],"items":[{"名称":"a","数量":12}],"cells":[["名称","数量"],["a",12]],"formulas":[{"cell":"B2","formula":"SUM(B1:B1)","value":12}],"merged":["A1:B1"],"date1904":false,"columns":[]}`
退出码: 0/1（非 xlsx/损坏 → `E_FORMAT`）/3（sheet 不存在）/4/5
* 必须处理：`sharedStrings` 索引、`inlineStr`、`t="s"`、数字格式日期（`numFmtId` 14–22/45–47 → 转日期）、
  `t="b"` 布尔、公式缓存值 `<v>`、合并单元格（`mergeCells`）、`1904` 日期系统、稀疏行（`r` 属性跳格）。
* `.xls`（BIFF 二进制，OLE2）**不支持** → 返回 1 + hint "先转存为 .xlsx"（Excel/`office.xlsx.write` 都能转）。

### 13.10 `office.xlsx.write` — 写 Excel（xlsx，v0.3）

价值: 中高（agent 输出表格交付物） · 复杂度: L（要写 OOXML + 样式 + 共享字符串）
用法: `office.xlsx.write --out <file> [--from-json <file>] [--from-csv <file>] [--sheet <name>] [--header] [--autofit] [--number-format <fmt>] [--dry-run|--yes]`
data: `{"out":..,"bytes":N,"sheets":["Sheet1"],"rows":N,"cols":N,"applied":true,"sha256":..}`
退出码: 0/2/3/4/6
* 最小可用实现：`[Content_Types].xml`、`_rels/.rels`、`xl/workbook.xml`、`xl/_rels/workbook.xml.rels`、`xl/worksheets/sheet1.xml`、
  `xl/styles.xml`（至少要一个 cellXfs）、`docProps/app.xml`+`core.xml`；字符串直接内联（`inlineStr`）可**免去 sharedStrings**，大幅简化。
* 已有同类交付物时优先建议用户/agent 用 `office.xlsx.read` + `data.csv.query`；**写 Excel 不是本工具的核心竞争力**（见 ROADMAP 不做清单）。

### 13.11 `office.docx.text` — 提取 Word 文本

价值: 中高（读需求文档/合同） · 复杂度: S · 风险: 无
用法: `office.docx.text <file> [--paragraphs] [--tables] [--headers] [--images-list] [--out <file>]`
data: `{"text":"...","paragraphs":[{"index":0,"style":"Heading1","text":"标题"}],"tables":[[["a","b"]]],"headers":["页眉"],"images":[{"name":"image1.png","bytes":N}],"words":N}`
退出码: 0/1（非 docx）/3/4
* 解析 `word/document.xml`：`w:p` → 段落（拼接 `w:t`），`w:tbl` → 行/格；`w:br`/`w:tab` 转 `\n`/`\t`。
* `.doc`（OLE2）**不支持** → 1 + hint。

### 13.12 `office.pptx.text` — 提取 PPT 文本

价值: 中（读演示稿） · 复杂度: S/M · 风险: 无
用法: `office.pptx.text <file> [--notes] [--outline] [--out <file>]`
data: `{"slides":[{"index":1,"title":"...","bullets":["..."],"notes":"...","shapes":N}],"count":N,"words":N}`
* 解析 `ppt/slides/slideN.xml` + `ppt/notesSlides/notesSlideN.xml` + `_rels` 建立 slide↔notes 映射；按 `ppt/presentation.xml` 的 `sldIdLst` 顺序排序（**不要**按文件名字符串排序，`slide10` 会排在 `slide2` 前）。

### 13.13 `pdf.text` — PDF 取文本（v0.3，尽力而为）

价值: 中（很多资料只有 PDF） · 复杂度: L · 风险: 无
用法: `pdf.text <file> [--pages 1-10] [--layout] [--out <file>] [--meta]`
data: `{"pages":N,"text":"...","perPage":["..."],"meta":{"title":..,"author":..,"producer":..},"extractionQuality":"good|partial|failed","notes":["3 页使用 CID 字体，文本可能缺失"],"columns":[]}`
退出码: 0/1（加密或扫描件 → `E_UNSUPPORTED`，hint 建议 OCR）/3/4
* 实现范围（明确）：仅支持 **FlateDecode** 内容流（`zlib` 用 `DeflateStream` 去掉 2 字节 zlib 头），
  解析 `Tj/TJ/'/"` 操作符 + `Tf` 字体映射（Type1/TrueType 的 `ToUnicode` CMap）。
  **不做**：加密 PDF、JBIG2/JPX 图像、CID→Unicode 缺失时的猜测、OCR。识别不出就诚实返回 `extractionQuality:"failed"`。
* OCR **不做**（需要 WinRT `Windows.Media.Ocr` 或第三方引擎，见 ROADMAP §5）。

---

## 14. Agent 协作能力（**重点章节**）

> 目标：让 agent **长期连接、可观测、可恢复**。设计原则四条：
> 1. **状态全在磁盘**（不用进程内状态），任何命令都能从任意进程续上；
> 2. **写操作幂等或可重放**（`--idem-key` + artifact 化结果）；
> 3. **队列/工件/上下文一律用 JSONL + 原子重命名**，天然抗崩溃；
> 4. **每个能力都要有"只读探测"形态**（`* list`/`* get`/`* check`），让 agent 能先观察再动手。
> 数据目录沿用 `CLI-CONTRACT.md` §6（`jobs/`、`runs/`、`cache/`、`logs/`），本组新增 `queues/`、`artifacts/`、`context/`、`undo/`、`idem/`、`snapshots/`。

### 14.1 `task.add` — 加入任务队列

价值: 高（agent 自我分解 + 多 agent 并行；比"一个巨型提示词"可靠得多） · 复杂度: M · 风险: 无
依赖: 新文件 `src/Core/TaskStore.cs`（建议 agentio-dev 拥有）
用法: `task.add [--queue default] [--id <id>] [--payload <json>] [--payload-file <f>] [--stdin] [--priority 5] [--tags a,b] [--max-attempts 3] [--not-before <date>] [--dedupe-key <k>] [--fail-if-exists]`
data: `{"item":{"id":"t-20260217-101530-0001","queue":"default","status":"pending","priority":5,"attempts":0,"maxAttempts":3,"payload":{...},"tags":["build"],"createdAt":..,"notBefore":null,"dedupeKey":null,"owner":null,"leaseUntil":null},"created":true,"queueDepth":12}`
退出码: 0（创建）/ 0 + `created:false`（`--dedupe-key` 命中，`deduped:true`）/ 2（payload 非法 JSON）/ 3（`--fail-if-exists` 且 id 已存在）
* 存储：`Paths.Home\queues\<queue>.jsonl`，**追加一行事件**（`{"ev":"add",...}`）；`--id` 缺省生成 `t-<ts>-<seq4>`。
* 并发：追加时用 `FileStream(path, Append, Write, FileShare.Read)` 单次 `Write` 一行（< 4KB 时同一写入原子），
  并加 `queues\<queue>.lock`（`FileShare.None` + 重试 ≤ `--lock-timeout`，默认 2s）保证 id 生成不冲突。

### 14.2 `task.pull` — 原子取任务（含租约）

价值: **很高**（多 agent 安全并行的核心原语；"谁拿到谁负责"） · 复杂度: L（原子性 + 租约 + 依赖） · 风险: 无
用法: `task.pull [--queue default] [--owner <id>] [--lease 5m] [--tag <t>] [--min-priority 0] [--max-priority 10] [--ids <id>...] [--wait 0s|--wait 30s] [--poll 500ms] [--fail-if-empty]`
data: `{"claimed":true,"item":{...同 task.add，status:"running",owner:"agent-a",leaseUntil:..,"attempts":1,"startedAt":..},"queueDepth":11,"waitedMs":0,"columns":[]}`
`--wait`：长轮询到有任务或超时（超时返回 `claimed:false` + `exit 0`，`truncatedAt:"timeout"`）
退出码: 0（拿到或空）/ **3**（`--fail-if-empty` 且队列空）/ 5（`--wait` 超时且 `--fail-on-timeout`）/ 2（`--owner` 缺失且无 `DSH_TOOLBOX_OWNER`）
* **原子取任务算法**（必须照此实现，否则会双取）：
  1. 独占打开 `queues\<queue>.lock`（`FileShare.None`，重试间隔 50ms，超 `--lock-timeout` → `E_LOCKED` exit 1）；
  2. 读 `queues\<queue>.jsonl` **尾部倒序扫描**（最多 `--scan-events 10000`）折叠出每个 id 的最新状态；
     若事件数超过阈值则先 `task.compact`（见 14.10）；
  3. 过滤：`status==pending`、`notBefore` 已到、优先级/标签匹配、依赖（`deps`）全部 done、未被租约占用；
  4. 按 `(priority desc, createdAt asc)` 排序取第一个；
  5. 追加 `{"ev":"claim","id":..,"owner":..,"leaseUntil":..,"at":..}` 到 jsonl，`Flush(true)`（fsync）；
  6. 释放锁，返回该项。
* 租约过期（`leaseUntil < now`）的任务在 `task.reap` 或下次 pull 时自动回到 pending（`attempts` 已计入）。
* `--owner` 缺省取 `DSH_TOOLBOX_OWNER` 环境变量，再缺省取 `"pid-<pid>"`。

### 14.3 `task.done` / `task.fail` — 完成任务/报错

价值: 高 · 复杂度: S · 风险: 无
用法: `task.done --id <id> [--result <json>] [--result-file <f>] [--artifact <id>] [--owner <o>]`
`task.fail --id <id> --error <msg> [--retry] [--retry-delay 60s] [--artifact <id>]`
data: `done`: `{"id":..,"status":"done","finishedAt":..,"durationMs":1234,"result":{...},"artifact":null,"queueDepth":10,"nextPending":3}`
`fail`: `{"id":..,"status":"failed|pending","attempts":1,"maxAttempts":3,"requeued":true,"retryAt":..,"error":"..."}`
退出码: 0/1（id 不存在或租约不属于该 owner → `E_LOCKED`）/2（`--result` 非法 JSON）
* 必须校验租约归属：非 owner 调用 `task.done` 默认拒绝（`--force` 才允许，用于回收）。
* `task.fail --retry`：`attempts < maxAttempts` 时置回 pending 并设 `notBefore = now + retryDelay`（默认指数：60s × 2^(n-1)）；否则 `status:"failed"` 终态。

### 14.4 `task.list` / `task.get` — 观察队列

价值: 高（agent 与人都要能"看进度"） · 复杂度: S/M · 风险: 无
用法: `task.list [--queue default] [--status pending|running|done|failed|all] [--owner <o>] [--tag <t>] [--limit 50] [--sort priority|created|status] [--counts-only]`
`task.get --id <id> [--history]`
data: `list`: `{"items":[{...item...}],"count":N,"counts":{"pending":3,"running":1,"done":20,"failed":2},"oldestPendingAgeSec":120,"columns":["id","status","priority","attempts","owner","tags"]}`
`get --history`: `{"id":..,"item":{...},"events":[{"ev":"add","at":..},{"ev":"claim","at":..,"owner":..},{"ev":"done","at":..}]}`
退出码: 0/3（`--id` 不存在）/5

### 14.5 `task.heartbeat` — 续租（长任务保活）

价值: 高（长任务不被 reap 抢走） · 复杂度: S · 风险: 无
用法: `task.heartbeat --id <id> [--owner <o>] [--lease 5m] [--progress 40] [--message "编译中"]`
data: `{"id":..,"leaseUntil":..,"extendedMs":300000,"progress":40,"message":"编译中"}`
退出码: 0/1（租约已失效或不属于该 owner → `E_LEASE_LOST`，agent 应立即停止工作）/3

### 14.6 `task.requeue` — 手动退回/移交

价值: 中高（agent 崩溃后另一个接手；人工干预） · 复杂度: S · 风险: 无
用法: `task.requeue --id <id> [--to <owner>] [--reset-attempts] [--reason <s>] [--force]`
data: `{"id":..,"status":"pending","owner":null,"attempts":1,"reason":"..."}`

### 14.7 `task.reap` — 回收过期租约

价值: 中高（保证"崩溃的 agent 不会永久占着任务"） · 复杂度: M · 风险: 无（但会改状态）
用法: `task.reap [--queue default] [--dry-run] [--grace 1m] [--fail-exhausted]`
data: `{"items":[{"id":..,"owner":"agent-a","leaseExpiredAgoSec":120,"action":"requeue|failed","attempts":2}],"requeued":N,"failed":N,"applied":false,"columns":["id","owner","action"]}`
退出码: 0/1（锁竞争超时）
* `--dry-run` 是**默认**（只报告）；真正改状态需要 `--apply`（新布尔）或 `--yes`（复用契约的 `--yes` 更一致 → 用 `--yes`）。
  建议实现：无 `--yes` 时返回 2 + hint（与契约 §9 一致），但这会让"定时 reap"不方便 → **折中**：`--dry-run` 显式预览，否则需 `--yes`。

### 14.8 `task.watch` — 队列变化监视（长连接友好）

价值: 中高（多 agent 编排的观察端；也可让人类盯进度） · 复杂度: M · 风险: 无
用法: `task.watch [--queue default] [--events add,claim,done,fail] [--duration 10m] [--max-events 200] [--poll 1s] [--from-now]`
data（`--jsonl`）：每事件一行 `{"type":"item","item":{"ev":"claim","id":..,"owner":..,"at":..,"queueDepth":11}}`；结束给 summary
退出码: 0/5/130/2（duration 与 max-events 都没给）
* 实现用"记住 jsonl 上次 offset，轮询文件长度变化"（简单可靠），`--from-now` 时从文件尾开始。

### 14.9 `task.compact` — 队列压缩（事件日志 → 状态快照）

价值: 中（长期运行的队列 jsonl 会无限增长） · 复杂度: M · 风险: **D**（重写队列文件）
用法: `task.compact --queue default [--keep-done 1000] [--keep-failed 200] [--keep-events 0] [--dry-run|--yes]`
data: `{"queue":..,"eventsBefore":N,"eventsAfter":N,"bytesBefore":N,"bytesAfter":N,"kept":{"done":1000,"failed":200,"pending":3,"running":1},"applied":true,"txid":..}`
退出码: 0/2/1（压缩期间有并发写入 → 中止重试）/3
* 算法：取锁 → 写 `queue.jsonl.tmp`（每个 id 一行 `snapshot` 事件 + 保留的终态事件）→ `File.Replace` 原子替换 → 释放锁。
* 触发阈值：`task.pull` 发现事件数 > `--scan-events` 时自动建议（写 `warnings`），但**不自动执行**。

### 14.10 `artifact.put` — 存工件（内容寻址）

价值: **很高**（agent 产出的日志/大 JSON/截图不能塞进上下文；这是"外置内存"） · 复杂度: M · 风险: **D**（写盘/去重）
用法: `artifact.put (--from-file <f>|--stdin|--from-command <cmd>|--json <s>) [--name <n>] [--content-type <mime>] [--tags a,b] [--ttl 7d] [--compress gzip|none] [--max-bytes 0] [--no-store-if-known]`
data: `{"id":"art-<sha12>","sha256":"...","bytes":N,"storedBytes":N,"path":"...\\artifacts\\a1\\a1b2....bin","deduped":true,"contentType":"application/json","tags":["build-log"],"expiresAt":..,"createdAt":..,"columns":[]}`
退出码: 0/1（超 `--max-bytes` → `E_TOO_LARGE`，不落盘）/2/3（`--from-file` 不存在）/4/5
* 布局：`artifacts\<前2位>\<sha256>`（数据）+ 同名 `.json`（元数据；元数据缺失时可从数据重算）；
  **`artifact.id` 用 sha256 前 12 位**，但查找必须容忍"前缀唯一匹配"并在歧义时报 2（`E_AMBIGUOUS`）。
* 去重：内容相同即 `deduped:true`（不重复占空间）；元数据合并 tags/ttl（取并集/更晚）。
* `--compress gzip`：对文本类压缩后再存（`sha256` 仍按**原始内容**算，保证幂等）。

### 14.11 `artifact.get` — 按偏移读取工件（**agent 长连接的关键**）

价值: **很高**（分页读 500MB 日志，不用一次进上下文） · 复杂度: M · 风险: 无
用法: `artifact.get <id> [--offset 0] [--limit 65536] [--lines <n>] [--tail] [--grep <re>] [--max-matches 100] [--encoding utf8|auto|base64] [--out <file>] [--decompress]`
data: `{"id":..,"totalBytes":N,"offset":0,"bytes":65536,"eof":false,"nextOffset":65536,"sha256":..,"text":"...","base64":null,"lines":[{"n":1,"text":"..."}],"matches":[{"line":118,"text":"..."}],"columns":[]}`
退出码: 0/3（id 不存在）/2（`--grep` 正则非法）/4/1（内容被 gzip 但未 `--decompress`？→ 自动解压，不报错）
* `--tail`：从末尾读（内部用"末尾 4MB 窗口 + 行边界对齐"，避免读整个大文件）。
* `--grep`：流式扫描（分块 1MB），命中数达 `--max-matches` 即停（`truncatedAt:"max-matches"`）。
* **与契约 §6 的关系**：`runs\<runid>.out.json` 也是工件，`artifact.get <runid>` 必须能直接读它（id 解析顺序：artifact id → runId → 文件路径）。

### 14.12 `artifact.list` / `artifact.rm` / `artifact.prune` — 工件生命周期

价值: 高（磁盘不能无限涨；agent 需要"我有哪些产物"） · 复杂度: M · 风险: **D**（rm/prune 删数据）
用法: `artifact.list [--tag <t>] [--since 1d] [--sort size|created|lastRead] [--limit 50] [--total] [--unreferenced]`
`artifact.rm <id>... [--dry-run|--yes]`
`artifact.prune [--older-than 30d] [--max-total 5GB] [--keep-tags <t>] [--keep-referenced] [--dry-run|--yes]`
data: `list`: `{"items":[{"id":..,"name":..,"bytes":N,"storedBytes":N,"createdAt":..,"lastReadAt":..,"tags":[...],"contentType":..,"referencedBy":["task:t-..."],"columns":["id","name","bytes","createdAt"]}],"count":N,"totalBytes":N,"quotaBytes":N,"unreferencedBytes":N}`
`prune`: `{"plan":[{"id":..,"bytes":N,"reason":"older-than|over-quota|unreferenced"}],"deleted":N,"freedBytes":N,"applied":false,"txid":..}`
退出码: 0/2/3/4/6
* `--keep-referenced` 默认 **true**：被 task result / checkpoint / report 引用的工件不删（引用关系由元数据的 `referencedBy` 维护）。
* `prune` 默认策略：先删 "过期且未被引用"，再按 LRU 删到 `--max-total` 之下；绝不删除 24h 内的新工件。

### 14.13 `context.get` / `context.set` / `context.list` / `context.delete` — 会话级上下文

价值: 高（agent 跨调用记住"当前项目/当前用户/上次结论"，而不用每次塞进提示词） · 复杂度: M · 风险: **P**（可能存敏感值）
用法: `context.set --key <k> --value <v|json> [--session <s>] [--ttl 8h] [--type auto|string|json] [--if-version <n>]`
`context.get --key <k> [--session <s>] [--default <v>]`
`context.list [--session <s>] [--match <re>] [--show-secrets] [--values]`
`context.delete --key <k>|--all [--dry-run|--yes]`
data: `set`: `{"session":"default","key":"project.root","value":..,"version":3,"bytes":42,"expiresAt":..,"applied":true}`
`get`: `{"session":..,"key":..,"found":true,"value":..,"version":3,"updatedAt":..,"expiresAt":..}`
`list`: `{"items":[{"key":..,"type":"string","bytes":12,"version":3,"updatedAt":..,"redacted":false,"preview":"..."}],"count":N,"session":..,"totalBytes":N,"columns":["key","type","updatedAt"]}`
退出码: 0/3（key 不存在且无 default）/2（`--if-version` 版本冲突 → `E_CONFLICT`，**用于乐观并发**）/4
* 存储：`context\<session>\<key 的 url-safe 编码>.json`（**一键一文件** → 天然并发安全 + 单键 TTL + 不用锁整个文件）。
* session 解析顺序：`--session` > `DSH_TOOLBOX_SESSION` > `"default"`；`context.list --sessions` 可列出所有 session 及大小。
* 默认对 `token|secret|password|key|credential|auth` 命名的键值脱敏（复用 `env` 的 `secretRe`）。
* TTL 过期不主动删（惰性：读取时判定），`context.delete --expired` 可批量清（需 `--yes`）。

### 14.14 `capabilities` — 能力自描述（**agent 自配置的入口**）

价值: **很高**（agent 不必硬编码命令清单；也是 MCP 桥接的基础） · 复杂度: M · 风险: 无
用法: `capabilities [--format json|mcp|openai] [--group <g>] [--command <name>] [--names-only] [--with-schemas] [--with-output-schema] [--stable]`
data（`--format json`）:
```json
{"version":"0.1.0","protocol":"1","generatedAt":"...","home":"...",
 "exitCodes":{"0":"成功","1":"运行期错误","2":"用法错误","3":"目标不存在","4":"权限被拒绝","5":"超时","6":"部分成功","130":"被取消"},
 "globalOptions":[{"name":"--json","type":"bool","desc":"..."}],
 "groups":[{"name":"scan","count":26,"summary":"..."}],
 "commands":[{
   "name":"scan.find","group":"scan","action":"find","summary":"按条件递归扫描文件",
   "usage":"scan find --path <dir> [--include <glob>]...",
   "params":[{"name":"--path","type":"path","required":true,"repeatable":false,"desc":"扫描根"},
             {"name":"--include","type":"glob","required":false,"repeatable":true,"default":null,"desc":"白名单"},
             {"name":"--depth","type":"int","required":false,"default":0,"min":0,"desc":"最大深度，0=不限"},
             {"name":"--sort","type":"enum","enum":["name","path","size","mtime"],"default":null}],
   "annotations":{"readOnlyHint":true,"destructiveHint":false,"idempotentHint":true,"openWorldHint":false,"requiresElevation":false,"networkAccess":false},
   "exitCodes":[0,2,3,4,5,6],
   "examples":["dsh-toolbox scan find --path . --include \"**/*.log\" --json"],
   "outputSchema":{...可选，见 data.json.schema...},
   "dataFields":["items","count","scanned","matched","denied","columns","truncatedAt"],
   "storage":["cache/index"], "related":["scan.count","scan.content"]}],
 "count":240}
```
`--format mcp` 输出 MCP 兼容的工具清单：
```json
{"tools":[{"name":"scan_find","description":"按条件递归扫描文件 ...",
  "inputSchema":{"type":"object","properties":{"path":{"type":"string"},"include":{"type":"array","items":{"type":"string"}},"depth":{"type":"integer"}},"required":["path"]},
  "annotations":{"readOnlyHint":true,"destructiveHint":false,"idempotentHint":true,"openWorldHint":false}}]}
```
（注解字段名对齐 MCP 的 `ToolAnnotations`：`readOnlyHint`/`destructiveHint`/`idempotentHint`/`openWorldHint`
—— 见 [MCP C# SDK ToolAnnotations](https://csharp.sdk.modelcontextprotocol.io/v1/api/ModelContextProtocol.Protocol.ToolAnnotations.html)。
命令名转 MCP 时把 `.` 换成 `_`。）
退出码: 0 / 3（`--command` 不存在）
* `--stable`：去掉 `generatedAt` 等易变字段，便于 agent 做"能力是否变化"的哈希对比。
* **实现依赖**：`Registry` 目前只有 name/summary/usage/examples，**没有参数元数据** →
  需要一个 `ParamSpec` 注册表（新文件 `src/Core/ParamSpec.cs`，与命令同处注册），或在 `capabilities` 里维护静态表。
  **强烈建议**做 `ParamSpec`，因为 `serve` 的 `commands.schema`（契约 §8）也依赖它（附录 A）。

### 14.15 `report.gen` — 生成 Markdown 运行报告

价值: 高（把一次 agent 会话的产物汇总成人类可读交付物） · 复杂度: M · 风险: **D**（写 `--out`）
用法: `report.gen [--title <s>] [--runs <runId>...] [--since 1h] [--cmd <name>...] [--tasks <queue>] [--jobs <jobId>...] [--artifacts <id>...] [--template <file>] [--out <file>] [--max-items 200] [--include-json] [--dry-run|--yes]`
data: `{"out":"...\\report-20260217-101530.md","bytes":N,"sections":["摘要","命令时间线","任务","工件","失败项"],"runs":12,"tasks":8,"artifacts":3,"failures":1,"markdown":"# ...","columns":[]}`
退出码: 0/2/3/4
* 数据源：`runs\runs.jsonl`（契约 §6）、`queues\*.jsonl`、`jobs\<id>\status.json`、artifact 元数据。
* 内置默认模板（`text.template` 语法），`--template` 可覆盖；`--include-json` 时把原始信封折叠进附录（`<details>` 块）。
* **必须**包含：失败项汇总、退出码直方图、耗时 Top 10、工件清单（带 `artifact.get` 用法示例）——这是"可交付性"的核心。

### 14.16 `audit.runs` — 运行记录查询

价值: 中高（"这台机器上 agent 都干了什么"；事后复盘与合规） · 复杂度: S/M · 风险: **P**
用法: `audit.runs [--since 1d] [--cmd <name>] [--exit <n>] [--failed] [--slow 1s] [--cwd <dir>] [--user <u>] [--limit 100] [--summary] [--by-cmd]`
data: `{"items":[{"runId":..,"ts":..,"cmd":"file.rm","argv":[...],"exit":0,"ms":420,"cwd":..,"user":..,"dryRun":false,"pid":..}],"count":N,"byCmd":[{"cmd":"scan.find","runs":42,"failures":1,"avgMs":320}],"failures":N,"totalRuns":N,"columns":["ts","cmd","exit","ms"]}`
退出码: 0（含 0 命中）/ 1（`--failed` 时确有失败记录）/ 3（`runs.jsonl` 不存在且非 `--allow-empty`）/ 5
* 读 `Paths.Runs\runs.jsonl`，**流式**（不要整体 parse）；损坏行进 `badLines`（不中断）。

### 14.17 `audit.timeline` — 活动时间线（跨数据源合并）

价值: 中高（把 runs + tasks + undo 记录合成一条人类可读时间线；定位"哪一步毁了环境"） · 复杂度: M · 风险: **P**
用法: `audit.timeline [--since 2h] [--until <date>] [--sources runs,tasks,undo,jobs] [--cmd <name>] [--destructive-only] [--limit 200] [--format text|json|md]`
data: `{"items":[{"ts":..,"source":"runs","kind":"command","summary":"file.rm --path C:\\tmp (12 项)","runId":..,"exit":0,"txid":"20260217-101530-9f2a"},{"ts":..,"source":"undo","kind":"destructive","summary":"删除 12 个文件","txid":..,"undoable":true}],"count":N,"window":{"since":..,"until":..},"destructiveCount":2,"columns":["ts","source","kind","summary"]}`
退出码: 0/3/5
* 这条命令的价值在于**恢复性**：`destructiveCount > 0` 时每条都带 `txid`，agent/人可以立即 `undo apply --txid`。

### 14.18 `checkpoint.save` / `checkpoint.restore` — 工作状态检查点

价值: 高（长任务中途崩溃后恢复；before/after 对比） · 复杂度: L · 风险: **D**（restore 会写回文件）
用法: `checkpoint.save --name <s> [--paths <dir>...] [--include-context] [--include-queue <q>] [--artifact] [--max-bytes 2GB]`
`checkpoint.restore <id> [--dry-run|--yes] [--only-paths] [--only-context]`
data: `save`: `{"id":"cp-20260217-101530","name":"before-migration","snapshotId":..,"artifacts":[..],"contextKeys":N,"queues":["default"],"bytes":N,"entries":N}`
`restore`: `{"id":..,"plan":[{"path":..,"action":"restore|skip","reason":"unchanged|missing|hash-differs"}],"restored":N,"skipped":N,"failures":[],"applied":false,"txid":..}`
退出码: 0/2/3/4/6
* `save` = 目录快照（复用 `scan.snapshot`，§1.7）+ context 键值 + 队列位置（每个队列的"未完成 id 集合"）+ 可选工件；
  全部落 `checkpoints\<id>.json`（引用快照文件，不复制文件内容）。
* `restore` **只恢复被改动过的**文件（用快照 hash 判断），且每个被覆盖的文件先进 undo（§15.1）。
* **不做**"全盘还原"（超出工具职责；见 ROADMAP §5）。

### 14.19 `run` — 执行外部命令（v0.1 计划内，本处给出硬化后的完整规格）

价值: **很高**（agent 的"手脚"；所有外部工具调用的统一入口） · 复杂度: L · 风险: **X** + P + N
用法: `run [--] <prog> [args...] [--cwd <dir>] [--env K=V]... [--env-inherit none|all|safe] [--stdin <s>|--stdin-file <f>|--stdin`|`--stdin-null] [--timeout 2m] [--max-output 8MB] [--spill-artifact] [--capture-env] [--idem-key <k>] [--idem-window 24h] [--retry 2] [--retry-delay 1s] [--retry-on <codes>] [--job-object] [--no-job-object] [--policy auto|<name>|none] [--elevate] [--shell] [--jsonl] [--yes]`

| 选项 | 默认 | 语义 |
|---|---|---|
| `--timeout <dur>` | `2m` | 超时**杀进程树**（不是只杀父进程）并返回 5，`data.timedOut:true` |
| `--max-output <size>` | `8MB` | stdout/stderr 各上限；超出则截断 + 自动落 artifact（`truncated:true`、`artifacts:[...]`） |
| `--spill-artifact` | true | 输出超 `--max-output` 时自动 `artifact.put`，`data.stdoutArtifact` 给 id |
| `--idem-key <k>` | 无 | 幂等键；命中则**不执行**，直接重放上次结果（`replayed:true`） |
| `--retry <n>` / `--retry-delay` | `0` / `1s` | 失败重试（指数退避：1s,2s,4s…上限 30s）；`--no-retry` 关闭 |
| `--retry-on <codes>` | 非 0 | 哪些子进程退出码算"可重试"；默认任何非 0 |
| `--job-object` | **true** | 用 Windows Job Object 托管子进程（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）→ **我们死了子进程不会变孤儿** |
| `--env-inherit <mode>` | `safe` | `all`=完全继承；`safe`=剔除疑似密钥变量（`*TOKEN*`,`*SECRET*`,`*PASSWORD*`,`AWS_*`…）；`none`=只给 PATH/SystemRoot/TEMP |
| `--capture-env` | false | 记录子进程启动时的**有效**环境变量到 artifact（供 `proc.env` 的替代，§8.9） |
| `--shell` | false | 用 `cmd /c`（或 `--shell powershell`）执行，支持管道/通配；**降低可审计性**，需 `--yes` |
| `--policy` | `auto` | 见 §15.2；`auto` = 有 `policy.json` 就启用，否则放行 |
| `--elevate` | false | 以管理员启动（`ShellExecute runas`，会弹 UAC；无人值守环境会失败 → 返回 4 + hint） |

data: `{"prog":"git","args":["status"],"pid":1234,"exit":0,"exitCode":0,"ms":420,"timedOut":false,"stdout":"...","stderr":"","stdoutBytes":120,"stderrBytes":0,"truncated":false,"stdoutArtifact":null,"policy":{"applied":true,"allowed":true,"rule":"allow:git status"},"idem":{"key":..,"hit":false,"replayed":false},"jobObject":true,"retries":0,"envCaptured":null,"columns":[]}`
退出码: 0 子进程成功 / **1** 子进程非 0（子进程真实码在 `data.exitCode`）/ 2（策略拒绝、缺 `--yes`、参数非法）/ 3（`prog` 不存在 → `E_TOOL_MISSING` + hint）/ 4（UAC 被拒/权限）/ 5 超时 / 6（重试后仍失败但可重试类错误）
* **退出码传递约定**：默认把子进程非 0 归一为 1（避免与我们的语义码冲突）；`--exit-code-passthrough`（新布尔）时，
  仅当子进程码 ∈ {0,1,2,3,4,5,6} 才原样返回，否则仍归一为 1。这条必须写进 `capabilities`，否则 agent 会误判。
* **孤儿进程防护**：`CreateJobObject` + `SetInformationJobObject(JobObjectExtendedLimitInformation)` +
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_ACTIVE_PROCESS(默认 64)` + `AssignProcessToJobObject`；
  超时时 `TerminateJobObject`（**一次杀掉整棵子进程树**，比 `Process.Kill` 可靠得多）。
* **幂等落盘**：`idem\<sha1(cmd+args+cwd)>.json` 存 `{idemKey, exit, exitCode, stdoutArtifact, stderrArtifact, startedAt, finishedAt, expiresAt}`；
  `--idem-key` 显式给出时用该键；未给出时**不**启用幂等（避免误重放副作用命令）。
* 与 `job.start` 的分工：`run` 同步等结果（默认 ≤ 2m）；`job.start` 异步派发（§14.20）。`run --detach` 是 `job.start` 的别名。

### 14.20 `job.*` 扩展（v0.1 计划内，补充契约 §6 的语义）

价值: 高（长任务：构建、扫描、下载、监视） · 复杂度: M · 风险: **D**（`job.kill`）
用法: `job.start <prog> [args...] [--cwd <dir>] [--env K=V]... [--timeout 30m] [--max-output 0] [--idem-key <k>] [--policy auto] [--tag <t>]`
`job.list [--state running|exited|failed|all] [--tag <t>] [--since 1d] [--limit 50]`
`job.status <jobId> [--with-progress]`
`job.output <jobId> [--stream both|stdout|stderr] [--offset 0] [--limit 65536] [--tail] [--follow] [--max-events 0] [--grep <re>]`
`job.wait <jobId> [--timeout 10m] [--poll 500ms]`
`job.kill <jobId> [--tree] [--force] [--dry-run|--yes]`
data: `status`: `{"jobId":..,"state":"running","pid":1234,"prog":"git","args":["clone",..],"startedAt":..,"finishedAt":null,"exit":null,"ms":12000,"bytesOut":12345,"bytesErr":0,"progress":{"percent":40,"message":"Receiving objects","at":..},"idemKey":null,"tags":["clone"],"jobObject":true,"timeoutSec":1800,"columns":[]}`
`output`: `{"jobId":..,"stream":"stdout","offset":0,"bytes":65536,"eof":false,"nextOffset":65536,"text":"...","lines":[{"n":1,"text":..}],"columns":[]}`
退出码: 0/3（jobId 不存在）/2/4/5（`job.wait` 超时）/6（job 以非 0 退出时 `job.wait` 返回 1）
* 磁盘布局沿用契约 §6：`jobs\<jobid>\cmd.json`（程序/参数/cwd/env 摘要/策略/超时）、`stdout.log`、`stderr.log`、
  `status.json`（原子重写；**必须**含 `progress` + `pid` + `jobObject`）、`events.jsonl`（状态变更与进度事件）。
* `job.output --follow` 支持"从当前 offset 持续读新内容 + job 结束自动收尾"，是长连接的读端（配合 `serve` 推送）。
* 进度：子进程无法主动上报 → 我们提供两条路：(1) `job.heartbeat`（agent 自己更新 `progress`）；(2) `--progress-from-regex <re>`（从 stdout 抓 `\d+%`）。
* `job.kill --tree` 走 Job Object `TerminateJobObject`（若 `jobObject:false` 则回退进程树递归终止，§8.4）。

### 14.21 `serve` 方法扩展（**契约变更，需 Lead 批准**）

在 `CLI-CONTRACT.md` §8 的 12 个方法之上，建议 v0.2 增加（完整清单在附录 A）：

| method | 说明 |
|---|---|
| `task.add` / `task.pull` / `task.done` / `task.fail` / `task.requeue` | 队列原语（长连接下省去每次起进程） |
| `task.watch` | 队列事件推送（服务端 `event` 通知） |
| `artifact.put` / `artifact.get` | 工件读写（`get` 支持分页，避免大 payload 进 JSON-RPC） |
| `context.get` / `context.set` | 会话上下文（`session` 从 `initialize` 的握手参数继承） |
| `capabilities` | 等价 `capabilities --format json`（含参数 schema，供客户自配置） |
| `events.subscribe` / `events.unsubscribe` | 统一订阅：`log|job|task|progress`（替代只能 tail 日志的现状） |
| `progress` 通知 | 服务端主动推送 `{"method":"progress","params":{"id":<requestId>,"percent":..,"message":..}}` |
| `$/cancelRequest` | 取消正在执行的 `call`（对齐 LSP/MCP 的通用取消约定） |
| `initialize` 扩展 | 返回值增加 `capabilities`、`session`、`policy`、`home`、`limits`（maxInlineBytes 等） |

### 14.22 `mcp` — MCP 桥（v0.3，可选）

价值: 高（把 dsh-toolbox 直接暴露成 MCP server，任何 MCP 客户端可调） · 复杂度: L · 风险: X
用法: `mcp --stdio [--tools <glob>...] [--write-tools] [--max-inline-bytes 1MB]`
* 实现 `initialize` / `tools/list`（数据来自 `capabilities --format mcp`）/ `tools/call`（映射到 `serve` 的 `call`）/
  `notifications/progress`；只读命令默认暴露，`destructiveHint:true` 的命令需 `--write-tools` 才暴露。
* **前置条件**：`capabilities --format mcp` 与 `ParamSpec` 先落地（14.14），否则桥会退化成手写映射表。

---
## 15. 跨切面机制（比单个命令更重要）

> 这一节的机制如果缺席，上面 223 条命令条目都会变成"危险的玩具"。它们必须**在写任何破坏性命令之前**先落地。

### 15.1 `undo` — 统一回滚日志（**所有破坏性命令的基础设施**）

价值: **很高**（"可恢复"是让 agent 敢于执行写操作的前提） · 复杂度: L · 风险: **D**（回滚本身是写操作）
依赖: 新文件 `src/Core/UndoJournal.cs`（建议 Lead 拥有，因为它被所有破坏性命令调用）

**日志格式**：`Paths.Home\undo\<txid>.jsonl` + `<txid>.meta.json`
```jsonl
{"ev":"begin","txid":"20260217-101530-9f2a","cmd":"file.rm","argv":[...],"cwd":"...","user":"...","at":"...","pid":1234,"dryRun":false,"reversible":true}
{"ev":"backup","path":"C:\\x\\a.txt","backup":"undo\\20260217-101530-9f2a\\files\\a1b2","bytes":1234,"sha256":"...","mtime":"...","attrs":"Archive"}
{"ev":"op","kind":"delete","path":"C:\\x\\a.txt","backupRef":"a1b2","at":"..."}
{"ev":"op","kind":"write","path":"C:\\x\\b.txt","beforeSha256":"...","backupRef":null,"at":"..."}
{"ev":"op","kind":"acl","path":"C:\\x\\c","beforeSddl":"D:AI(A;ID;FA;;;BA)","at":"..."}
{"ev":"op","kind":"registry","key":"HKCU\\Environment","name":"PATH","beforeType":"REG_EXPAND_SZ","beforeValue":"...","backupRef":"hkcu-environment.reg","at":"..."}
{"ev":"end","txid":..,"ok":true,"ops":2,"failures":0,"at":"..."}
```

用法: `undo list [--since 1d] [--cmd <name>] [--reversible-only] [--limit 50]`
`undo apply --txid <id> [--only <path-glob>] [--dry-run|--yes] [--stop-on-error]`
`undo prune --older-than 30d [--keep-reversible 50] [--max-total 2GB] [--dry-run|--yes]`
data: `list`: `{"items":[{"txid":..,"cmd":"file.rm","at":..,"ops":12,"deleted":12,"reversible":true,"bytesBackedUp":N,"expiresAt":..}],"count":N,"totalBackupBytes":N,"columns":["txid","cmd","at","ops","reversible"]}`
`apply`: `{"txid":..,"plan":[{"path":..,"action":"restore|revert-write|revert-acl|revert-registry|skip","reason":null}],"restored":N,"skipped":N,"failures":[],"applied":false,"newTxid":..,"columns":["action","path","detail"]}`
退出码: 0（含"无可回滚项"）/ 1（部分回滚失败）/ 2（缺 `--yes`）/ 3（txid 不存在）/ 4 / 6
**契约要求（必须做到，否则等于没有）**：
1. 破坏性命令**先写 begin + 全部 backup 事件并 fsync，再开始动手**（崩溃时最多留下"有备份但没操作"的日志，可安全忽略）；
2. 每个不可逆操作（覆盖/删除）在动手前必须有 `backup` 或至少 `beforeSha256`（大文件不备份时，回滚时明确报 `reason:"no-backup-too-large"`）；
3. 备份放 `undo\<txid>\files\<随机名>`（**不要**保留原始目录结构再拼路径——超长路径会失败）；
4. `undo apply` 是**逆序**重放；删除类 → 从 backup 复制回来；覆盖类 → 写回备份；ACL/注册表 → 用记录的 before 值；
5. `data.txid` 出现在所有破坏性命令的返回里（已在各条目里写明）；
6. 永不自动 prune 掉"可逆且 7 天内"的记录。

### 15.2 `policy.*` — 命令执行策略（白名单/超时/输出上限/沙箱）

价值: **很高**（让 agent 的"手"有刹车：默认拒绝危险程序、限制输出与时间、禁止联网） · 复杂度: L · 风险: 无（策略本身只读；`policy.set` 是 D）
依赖: `Paths.Home\policy.json` + `run`/`dev.run-cs`/`dev.ps` 统一调用

**策略文件格式（v1，公开格式，agent 可读写）**：
```json
{
  "version": 1,
  "mode": "allowlist",
  "defaultMaxSeconds": 120,
  "defaultMaxOutputBytes": 8388608,
  "maxParallel": 8,
  "allowNetwork": true,
  "allowUnsigned": false,
  "jobObjectMemoryMb": 2048,
  "jobObjectActiveProcesses": 64,
  "deny": [
    {"prog": "*", "argsRegex": "(?i)\\b(format|diskpart|bcdedit|shutdown|vssadmin)\\b", "reason": "高危系统命令"},
    {"prog": "*", "argsRegex": "(?i)(rm\\s+-rf\\s+/|Remove-Item\\s+-Recurse\\s+C:\\\\)", "reason": "递归删除根"}
  ],
  "allow": [
    {"prog": "git", "argsPrefix": ["status", "diff", "log", "ls-files", "rev-parse"], "maxSeconds": 60},
    {"prog": "pwsh", "argsPrefix": ["-NoProfile", "-Command"], "maxSeconds": 300, "allowNetwork": true, "note": "受 dev.ps 包装时使用"},
    {"prog": "node", "maxSeconds": 600},
    {"prog": "**\\node.exe", "maxSeconds": 600}
  ],
  "pathGuard": {"protect": ["%SystemRoot%", "%ProgramFiles%", "%ProgramFiles(x86)%", "C:\\Users\\*\\Documents"], "requireIKnow": true}
}
```
用法: `policy.show [--json] [--effective]`
`policy.check --prog <p> --args <a>... [--cwd <dir>]` → `{allowed:bool, rule:.., reason:.., wouldRun:{prog,args,maxSeconds,env,network}}`（**不执行**，纯判定）
`policy.set --file <policy.json> [--dry-run|--yes]` / `policy.init [--force]`
data: `check`: `{"allowed":false,"matched":{"type":"deny","prog":"*","argsRegex":"...","reason":"高危系统命令"},"prog":"format","args":["C:"],"suggestions":["如果你确实需要，请临时用 policy.set 增加窄规则"],"wouldRun":null}`
退出码: 0（允许）/ **1**（拒绝 —— 让 agent 用退出码判断，不必解析 JSON）/ 2（策略文件非法）/ 3（无策略文件且 `--require`）
* 匹配规则（**顺序即优先级**）：`deny`（glob on prog + regex on 拼接后的 args）→ `allow`（prog glob + args 前缀）→ `mode`：
  `allowlist` 时未命中 allow 即拒绝；`blocklist` 时未命中 deny 即允许；`permissive` 时只记录。
* `--policy none` 可以绕过，但**必须**在 `run` 返回里 `policy.applied:false` 且在运行记录（`runs.jsonl`）里留痕
  （`runs.jsonl` 由 `Program.cs` 写，需要 Lead 扩展字段，见附录 A）。
* `pathGuard` 与 §2.4/§11.11 的保护规则**共用同一份实现**（一个 `SafetyGuard` 类），不要各写一份。

### 15.3 大结果外溢（inline → artifact）与流式

价值: 高（防止 200MB JSON 灌进 agent 上下文或 stdout 卡死） · 复杂度: M · 风险: 无
**规则（全局，所有命令一致）**：
1. 若预计 `items` 序列化后 > `--max-inline-bytes`（**新全局选项，默认 1MB**），则写 `artifact.put` 并把
   `data.items` 换成 `data.artifact = {id,bytes,sha256,inline:false,count}`，`data.count` 仍给总数；
2. `--jsonl` 模式下**边扫边出**，不缓冲（`Output.EmitItem` 已支持，但**当前实现会把发出的项也存进 `_items` 列表** →
   百万项会 OOM；需要 Lead 给 `Output` 加 `Streaming` 标志，见附录 A）；
3. `truncated:true` 时必须给 `data.truncatedAt`（`max-results`/`max-bytes`/`max-matches`/`max-inline-bytes`/`timeout`）；
4. 大文本（`file.read`/`artifact.get`）用 `--offset/--limit` 分页，默认窗口 64KB。

### 15.4 敏感信息脱敏（横切）

价值: 中高（日志、报告、`data` 会进模型上下文和文件） · 复杂度: S · 风险: P
* 统一 `SecretRedactor`：正则 `(?i)(token|secret|password|passwd|pwd|api[-_]?key|credential|auth|bearer|private[-_]?key|ssh[-_]?key)`。
* 规则：值长度 ≤ 4 全 `****`；否则保留首 2 + 尾 2；**默认脱敏**，`--show-secrets` 才明文（与 `env` 行为一致）。
* 适用范围：`env`、`data.env-file`、`context.*`、`report.gen`、`audit.*`、`log.*`、`job.output`（`--show-secrets` 控制）。
* 运行记录与 toolbox 日志**永不**记录疑似密钥的 `--value` 参数（写 `runs.jsonl` 前过滤，需要 Lead 在 `Program.AppendRun` 里接）。

### 15.5 安全护栏（`SafetyGuard`，横切）

| 规则 | 行为 |
|---|---|
| 驱动器根 / `%SystemRoot%` / `%ProgramFiles%` / 用户 Profile 根 | 破坏性操作**拒绝**（exit 2），除非 `--i-know` |
| 目标路径含 `..` 解析后跳出给定根 | 拒绝（防 zip-slip 类与目录穿越） |
| 路径是重解析点（junction/symlink） | 默认不跟进（`--follow` 才进），删除时**只删链接本身** |
| 回收站删除 | 默认（`file.rm`/`temp.clean`）；`--permanent` 显式关闭 |
| 单次删除/覆盖项数 | 超 `--max-items`（默认 10000，`file.rm` 为 1000）拒绝执行 |
| `--cwd`/`--path` 指向 `Paths.Home` 下的 `undo/`、`artifacts/` | 破坏性操作拒绝（防 agent 自毁日志） |
| 写系统目录（`C:\Windows`、`Program Files`） | 需管理员 + `--i-know` |

### 15.6 `--dry-run` 保真度要求（逐命令类的验收标准）

| 命令类 | `--dry-run` 必须做到 | 明确禁止 |
|---|---|---|
| 删除/移动/覆盖（`file.rm/move/copy/replace/truncate`、`dir.sync`、`temp.clean`） | 输出完整 `plan`（含每个目标与原文件前 `beforeSha256`）、`reclaimableBytes`、`applied:false`，**不创建任何文件/目录/undo 目录** | 不得为了"算 hash"而修改 mtime（只读不改） |
| 改名（`file.rename`） | 输出 `from→to` 全集 + 冲突检测结果 | 不得创建临时中转文件 |
| 文本/编码/数据写（`text.*`、`data.json.set`、`data.ini.set`） | 输出 before/after 预览 + diff | 不得写 `.bak`/`.tmp` |
| 系统写（`svc.control`、`registry.set`、`net.hosts`、`acl.*`、`defender.exclusions`） | 输出目标当前状态 + 将执行的动作 + `revertCommand` | 不得先停服务再报告失败 |
| 执行类（`run`、`dev.ps`、`job.start`） | 输出 `policy.check` 结果与将运行的命令行/env 摘要 | **不做**"试运行"（无法保证无副作用）→ `--dry-run` 时直接返回计划，`applied:false` |

### 15.7 运行记录与日志扩展（需要 Lead 动 `Program.cs`）

`runs.jsonl` 现有字段：`runId/ts/cmd/argv/exit/ms/cwd/user/dryRun/pid`。建议新增：
`txid`（破坏性事务）、`policyApplied`、`reverted`（该 run 是否被 undo）、`artifactIds`、`bytesOut`、`owner`、`session`、`degraded`（是否走了降级路径）。
理由：`audit.runs` / `report.gen` / `undo list` 都依赖这几个字段才能把"命令—事务—工件—会话"串起来。

### 15.8 并发与锁约定

| 场景 | 机制 |
|---|---|
| 队列事件追加 | `FileShare.Read` 追加单行（≤4KB 原子）+ 序号由 `queues\<q>.lock` 独占锁保护 |
| 队列状态读取 | 倒序扫描 + 事件折叠（无需锁，允许读到略旧状态） |
| 工件写入 | 先写 `.tmp` → `File.Move` 到 `<sha256>`；**已存在即去重**（内容寻址天然幂等） |
| 上下文单键写 | 每键一文件 + `File.Replace` 原子写 |
| undo 日志 | 单进程顺序追加 + `Flush(true)`；跨进程并发由 txid 隔离（无需锁） |
| 索引/快照 | 写临时文件 + 原子替换；读者用 `--max-age` 判定新鲜度 |
| 通用文件锁帮助类 | `Core\LockFile.cs`：`using (LockFile.Acquire(path, timeout)) {...}`，重试 50ms，超时抛 `E_LOCKED`（exit 1，与其他 exit 语义不冲突） |

### 15.9 幂等与重试约定

* 幂等键：`--idem-key <k>`（`run`/`job.start`/`dev.*`/`net.download`）。
  存储 `idem\<sha1(key | prog | args | cwd | inputHash)>.json`；命中窗口 `--idem-window`（默认 24h）。
* 只对**读类或可重放**命令默认开启自动重试（`net.http`/`net.download`/`net.dns`）；
  写类命令**默认不重试**（重试一次删除就是两次删除）。
* 所有可能被重试的命令必须保证"重试后结果一致"或明确 `--no-retry` 才允许调用。

---

## 附录 A：需要 Lead 改动的契约点

> 本附录是 feature-scout 对 Lead 的**请求清单**。全部落在 Lead 拥有的文件
> （`src/Core/Cli.cs`、`src/Program.cs`、`src/Core/Runtime.cs`、`src/Core/Fs.cs`、`docs/CLI-CONTRACT.md`、`build.ps1`）。
> 未完成前，对应命令**无法正确解析选项或缺依赖**，请按批次处理。

### A.1 `ArgMap.BoolNames` 必须新增的布尔选项（按优先级）

**P0（v0.2 首批，几乎每个命令都用）**：
`show-secrets`（**已在 `CoreCommands.env` 里使用但白名单缺失**）、`fast`（**已在 `sysinfo` 里使用但缺失**）、
`stdin`、`print0`、`smart-case`、`stable`、`flush`、`gitignore`、`i-know`、`in-place`、`verified`?（取值）、
`recurse-prune`、`keep-links`?（取值）、`allow-links`、`keep-tar`、`store`、`deep`、`no-follow`、`no-proxy`、`insecure`、
`cert-chain`、`resume`、`show-body`、`show-headers`、`all-signatures`、`only-unsigned`、`only-invalid`、`expiring`?（取值）、
`no-infer`、`with-schemas`、`names-only`、`stable`(已列)、`counts-only`、`history`、`apply`、`reset-attempts`、`fail-if-empty`、
`force-delete`、`skip-newer`、`no-clobber`(已存在)、`atomic`、`flatten`、`text`(已存在)、`headers`/`header`、
`no-header`(已存在)、`infer-types`、`utf8-bom`、`always-quote`、`array`、`pretty`(已存在)、`sort-keys`、`minify`、`ascii`、
`words`、`chars`、`entropy`、`longest-line`、`empty-lines`、`line-numbers`、`paragraphs`、`tables`、`notes`、`outline`、`meta`、
`exif`、`gps`、`formulas`、`cursor`、`topmost`、`clear-first`、`notes`(重复)、`magic`、`sections`、`dotnet`、`imports`、`delay`、
`dnssec`、`no-cache`、`sessions`、`open-files`、`include-admin-shares`、`windows-only`?、`mixed-only`、`drop-empty`、`trim`、
`natural`、`numeric`(取值)、`unique`(已存在)、`repeated-only`、`unique-only`、`strip-bom`、`strip-zero-width`、
`collapse-whitespace`、`trim-lines`、`strip-control`、`word-diff`、`stat`、`invert`(已存在)、`reverse`(已存在)、
`strict`、`json-pointer`、`raw`(已存在)、`first`、`create-missing`、`strip`?（取值）、`check`、`reverse-patch`?、
`no-flush`、`enabled-only`、`signed-only`、`unsigned-only`、`problem-only`、`with-private-key`、`self-signed`?、
`dont-fragment`、`resolve`/`resolve-names`、`ipv6`、`external-only`、`established-only`、`up-only`、`include-virtual`、
`with-gateway`、`with-dns`、`all-signatures`、`only-missing`、`keep-owner`、`owner`?、`sacl`、`inherited`、`effective`、
`recurse`(已存在)、`keep-progress`?、`idempotent`?、`replayed`?（这些是返回字段，不是选项）。

**建议做法**：不要一个一个加，而是把"布尔选项白名单"改成**由 `Registry.Add` 的 `ParamSpec` 自动填充**
（见 A.3），否则白名单会永远落后于命令实现 —— 这是当前设计最大的可维护性风险。

### A.2 建议新增的全局选项（`Program.cs` + `CLI-CONTRACT.md` §2）

| 选项 | 默认 | 语义 | 影响的命令 |
|---|---|---|---|
| `--max-inline-bytes <size>` | `1MB` | 结果超过则自动落 artifact（§15.3） | 全部列表类 |
| `--progress` | false | 输出进度事件（jsonl 加 `{"type":"progress"}`；serve 推送通知） | 长任务全部 |
| `--owner <id>` | `DSH_TOOLBOX_OWNER` 或 `pid-<pid>` | 任务/租约归属（§14） | `task.*`、`run`、`job.*` |
| `--session <id>` | `DSH_TOOLBOX_SESSION` 或 `default` | 上下文命名空间（§14.13） | `context.*`、`report.gen` |
| `--i-know` | false | 越过 SafetyGuard 的高危路径保护（§15.5） | 破坏性命令 |
| `--lock-timeout <dur>` | `2s` | 文件锁等待上限（§15.8） | `task.*`、缓存写入类 |
| `--max-items <n>` | `10000` | 破坏性命令的项数安全阀（§15.5） | 破坏性命令 |
| `--exit-code` | false | "有差异/无命中即返回 1"的统一开关 | `scan.content`/`scan.diff`/`text.diff`/`net.ping` 等 |
| `--relative`（已在契约 §5 提到） | false | 输出相对 `--cwd` 路径 | 全部路径类（**需要 Lead 在 `FsEntry.ToDictionary` 侧统一支持**） |

### A.3 `Program.cs` / `Core` 层的必要修复（按优先级）

| # | 问题 | 现状 | 建议修复 | 影响 |
|---|---|---|---|---|
| 1 | **Ctrl+C 退出码错误** | `cts.Cancel()` → `OperationCanceledException` → 统一映射为 `exit 5`（`Program.cs` L129-132） | 区分"用户取消"与"超时"：`CancelKeyPress` 设 `ctx.UserCancelled=true` → 返回 `ExitCodes.Cancelled(130)`；超时才 5 | 契约 §4 承诺 130，现无法达成 |
| 2 | **`--jsonl` 内存增长** | `Output.EmitItem` 既写行又 `_items.Add`（`Runtime.cs` L178-183） | 加 `Output.Streaming`：jsonl 模式下不累计 `_items`（`Result` 里 summary 的 count 用计数器） | 百万项扫描会 OOM |
| 3 | **`Fs.Enumerate` 无取消** | 目录循环不检查 `ct` | 加 `Fs.Enumerate(root, o, CancellationToken ct = default)`，循环内 `ct.ThrowIfCancellationRequested()`；`EnumerateMany` 顺带透传 | 长扫描无法被 `--timeout` 中止 |
| 4 | **命令参数无 schema** | `Registry.Add` 只有 name/summary/usage/aliases/examples | 新增 `ParamSpec`（name/type/required/repeatable/default/enum/desc）+ `Registry.Add(name, summary, usage, run, params, ...)` 重载；`capabilities` 与 `serve.commands.schema` 都读它；**并自动生成 BoolNames** | 阻塞 `capabilities`、`commands.schema`、MCP 桥、UI 自动表单 |
| 5 | **`Fs.ScanOptions` 缺 §7 的 `--content-regex`/`--sort`** | `FromArgs` 不解析这两项 | 由 scanner-dev 在 `Selector` 层实现（不改 `Fs.cs`）；`--sort` 触发缓冲排序 | 契约 §7 词汇"名存实亡" |
| 6 | **`BuildRefs` 硬编码在 build.ps1** | `dev.compile` 需要同一份引用列表 | 抽到 `src/Core/BuildRefs.cs`（或 `build.ps1` 生成 `refs.json` 供 exe 读） | `dev.compile` 会缺引用 |
| 7 | **`dynamic` 不可用** | `build.ps1` 未引用 `Microsoft.CSharp.dll` → 运行期缺 binder | `GetTypeFromProgID` 的 COM 调用统一用 `Type.InvokeMember` 反射；或 Lead 决定加 `Microsoft.CSharp.dll` 引用 | 影响 `patch.list --history`、`net.firewall`、`svc.config` 的 COM 路径 |
| 8 | **`ctx.TimeoutSec` 从未赋值**；命令无法与全局超时共存 | `Program.cs` 只做 `cts.CancelAfter` | 设 `ctx.TimeoutSec`，并把"这是全局超时"与"命令自发的 per-item 超时"区分开（后者用 `ToolException.Timeout`） | `sign.verify --timeout 0`（不限）无处实现 |
| 9 | **`Registry.Add` 无副作用元数据** | 无 `destructive`/`readOnly` 标注 | 增加 `CommandInfo.SideEffects`（`destructive/readOnly/idempotent/openWorld/needsAdmin/network`），供 `capabilities`/`manifest`/`help`/安全审计使用 | agent 无法自动判断"这条命令能不能随便跑" |
| 10 | **`help --json` 不返回参数** | 只有 usage 字符串 | 返回 `params`（来自 ParamSpec） | agent 靠 usage 字符串猜参数，错误率高 |
| 11 | **`Paths` 缺新目录** | 只有 `logs/runs/jobs/cache` | 增加 `Queues/Artifacts/Context/Undo/Idem/Snapshots/Checkpoints/Captures` 并纳入 `Ensure()` 与 `doctor` | §14/§15 的存储无处可放 |
| 12 | **`AppendRun` 字段不足** | 缺 `txid/policyApplied/artifactIds/owner/session/degraded` | 扩展（见 §15.7） | `audit.*`/`report.gen` 无法串联 |
| 13 | **`Output.Fail` 不写日志** | 失败只进 stdout/stderr | `Program.cs` 失败分支已有 `log.Error`，但 `Output.Fail` 的 `data` 不进日志 → 补一条结构化记录 | 事后复盘缺错误明细 |
| 14 | **`--yes`/`--dry-run` 的 hint 文案不统一** | 目前只有 `Ctx.ConfirmDestructive` | 全部破坏性命令统一走它（不要自己抛 `E_NEEDS_CONFIRM`），保证 hint 与 exit 2 一致 | agent 解析一致性 |
| 15 | **STA 线程帮助类缺失** | `Program.Main` 无 `[STAThread]` | `Core\Sta.cs`：`Sta.Run(() => ...)`（专用 STA 线程 + 异常回传） | `clipboard.*`/`image.capture` 直接崩 |

### A.4 建议新增的错误码（`data.error.code`，`ToolException` 静态工厂）

| 码 | exit | 用途 | 建议工厂 |
|---|---|---|---|
| `E_UNSUPPORTED` | 1 | 明确不支持（yaml 高级特性、加密 zip、xls、pdf 加密） | `ToolException.Unsupported(msg, hint)` |
| `E_FORMAT` | 1 | 文件格式损坏/非法（非 PE、非 xlsx） | `ToolException.Format(...)` |
| `E_SAFETY` | 2 | 安全阀触发（超 `--max-items`、zip bomb、高危路径） | `ToolException.Safety(...)` |
| `E_NET` | 1 | 网络失败（DNS/连接/TLS） | `ToolException.Net(...)` |
| `E_TOOL_MISSING` | 3 | 外部程序不存在（git/schtasks/reg） | `ToolException.ToolMissing(...)` |
| `E_NO_SESSION` | 1 | 无交互桌面会话 | `ToolException.NoSession(...)` |
| `E_CONFLICT` | 2 | 乐观并发冲突（`context.set --if-version`） | `ToolException.Conflict(...)` |
| `E_LEASE_LOST` | 1 | 任务租约失效（agent 应立即停止） | `ToolException.LeaseLost(...)` |
| `E_TOO_LARGE` | 1 | 超出 `--max-bytes` 且拒绝落盘 | `ToolException.TooLarge(...)` |
| `E_AMBIGUOUS` | 2 | 前缀匹配到多个 id | `ToolException.Ambiguous(...)` |
| `E_REGEX_TIMEOUT` | 1 | 单文件正则超时（进 `failures`，不中断） | 直接 `new ToolException(...)` |

### A.5 建议写入 `CLI-CONTRACT.md` 的正式条目

1. §6 新增目录：`queues\ artifacts\ context\ undo\ idem\ snapshots\ checkpoints\ captures\`（含语义一句话）。
2. §5 新增 `data.artifact` / `data.plan` / `data.applied` / `data.txid` / `data.failures[].code` 的正式定义（本文档 §0.7 已给出草案）。
3. §8 新增方法表（§14.21 的 9 项）。
4. §9 补充："执行外部代码类命令（`run`/`dev.*`/`job.start`）的 `--dry-run` 语义是输出计划而非试运行"。
5. §4 补充："`run` 的子进程非 0 退出默认归一为 exit 1，真实码见 `data.exitCode`；`--exit-code-passthrough` 可原样传递 ∈{0..6}"。
6. 版本号：本批全部落地后建议 `ToolInfo.Version = "0.2.0"`、`Protocol = "2"`（`serve` 方法表扩展属协议变更）。

---

## 附录 B：速查表

### B.1 退出码分配速查（按命令类）

| 命令类 | 0 | 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|---|
| 只读列表/查询（`scan.*`/`proc.list`/`installed.*`） | 成功（含 0 命中） | 格式/解析错 | 参数非法 | 目标不存在 | 无权限 | 超时 | 部分失败 |
| 过滤/内容检索（`scan.content`/`text.find-lines`） | 有命中 | `--exit-code` 时无命中 | 参数/正则非法 | 路径不存在 | 无权限 | 超时 | 部分文件读失败 |
| 比较类（`hash.check`/`text.diff`/`dir.compare`/`sign.verify`） | 一致/全部有效 | **有差异/有无效** | 参数 | 清单/路径不存在 | 无权限 | 超时 | 部分项异常 |
| 破坏性（`file.*`/`dir.sync`/`acl.*`/`registry.set`） | 全部成功 | 执行期错 | 未加 `--yes`/安全阀 | 目标不存在 | 无权限/需提权 | 超时 | 部分失败（`data.failures`） |
| 执行类（`run`/`job.*`） | 子进程 0 | 子进程非 0（真码在 `data.exitCode`） | 策略拒绝/缺 `--yes` | prog 不存在 | 权限/UAC 拒 | 超时 | 重试后仍失败 |
| 队列（`task.*`） | 成功（`pull` 空队列也算 0） | 锁/租约错 | 参数/payload 非法 | id 不存在 | — | `--wait` 超时 | — |
| 长任务（`* --follow`/`watch`） | 正常到点结束 | — | 缺 duration/max-events | — | — | 超时 | — |

### B.2 `data` 字段速查（agent 泛化解析用）

```
列表类:      items[] count columns[]
漏斗类:      scanned matched skipped denied
破坏性:      plan[] applied txid failures[] irreversible
截断:        truncated(信封) truncatedAt
大结果:      artifact{id,bytes,sha256,count,inline:false}
一致性:      ok(命令级) 与信封 ok(调用级) 可同时存在 → 语义：信封 ok=true 表示"命令正常完成"
比较类:      same/ok(bool) + 差异明细（added/removed/modified/different/failed）
执行类:      exit exitCode pid ms stdout stderr stdoutArtifact policy idem
队列:        item | claimed | counts{status:数量} queueDepth
工件:        id sha256 bytes storedBytes path deduped expiresAt
上下文:      session key value version expiresAt
```

### B.3 命令计数（本文档 = **228 个 `###` 规格条目**，其中 **223 条为新增命令条目**，5 条为说明/机制条目）

> 计数口径：条目数按 `###N.M` 小节计（一个条目可能覆盖多个子动作，例如 `undo` 含 `list/apply/prune`、
> `net.hosts` 含 `list/add/remove/restore`、`text.head`/`text.tail` 合为一条）。若按可独立调用的**子命令**计，合计约 **244** 条。
> 5 条非命令条目：1.27（不新增命令的说明）、9.30（并入 `sysinfo` 的说明）、14.20（`job.*` 扩展）、14.21（`serve` 方法扩展，属契约变更）、15.6（`--dry-run` 保真度要求）。

| 组 | 条目数 | 组 | 条目数 | 组 | 条目数 |
|---|---|---|---|---|---|
| `scan.*`（含 1 条说明） | 27 | `data.*`（含 csv/xml/ini/env-file） | 19 | 系统管理组（svc/disk/eventlog/installed/startup/task/driver/patch/perf/power/registry/temp，含 1 条说明） | 29 |
| `file.*` / `dir.*` | 20 | `codec.*` | 9 | `net.*` | 17 |
| `hash.*` | 6 | `proc.*` | 12 | `sign/pe/cert/acl/defender` | 17 |
| `archive.*` | 6 | `dev.*/git.*/path.*/env.patch` | 11 | `image/window/clipboard/office/pdf` | 13 |
| `text.*` | 16 | Agent 组（task/artifact/context/capabilities/report/audit/checkpoint/run/mcp/serve，含 2 条扩展说明） | 26 | `undo` / `policy.*` | 2 |
| | | | | **合计** | **228** |


---

> 全文完。规格共 228 个条目（223 条新增命令，按子命令计约 244 条）；实现顺序与分批见 [ROADMAP.md](ROADMAP.md)；
> 需要 Lead 落笔的契约改动集中在 [附录 A](#附录-a需要-lead-改动的契约点)。
