# EVIDENCE — scan.* / hash.*（task-1，所有者 scanner-dev）

> 本文件是 task-1 的真实运行证据。所有命令都在本机（Windows + .NET Framework 4.8 + 慢速机械盘）真实执行过，
> 原始 stdout/stderr 逐条保存在 `var\test\out\<case>.json` / `.err`（46 个用例），可一键复现。

## 0. 交付物与构建

| 项 | 值 |
|---|---|
| 写入范围（仅此三处） | `src\Commands\ScanCommands.cs`、`src\Commands\HashCommands.cs`、`docs\EVIDENCE-scanner.md` |
| 构建命令（验收用，全量） | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <repo-root>\build.ps1 -Out check-scanner.exe` |
| 构建结果（全量） | **构建成功：`dist\check-scanner.exe`（301,056 字节，0 error / 4 warning；4 个 warning 全是他人文件的 CS0649 未赋值字段，我的两个文件 0 warning）** |
| 隔离构建（本模块单独，便于排查） | `build.ps1 -Only ScanCommands,HashCommands -Out check-scanner.exe` → 同样 0 error / 0 warning |
| 产物 SHA256（全量构建） | `A0DB87B14B633DA3E9F642EA2B6FA6C80919314CB95290E61A9920C33DAFE8B8` |
| 证据采集 | §1 的 46 个用例全部在**该全量构建产物**上重跑过一遍，退出码与下表一致 |
| 复现脚本 | `var\test\run-scanner-evidence.ps1`（重建测试树 → 跑完 46 个用例 → 汇总退出码） |
| 测试数据 | `var\test\scanner\`（自造小文件，共 11 个文件/目录节点；**只扫这个目录，绝不扫整个 C:**） |

测试树（复现脚本会重建）：

```
scanner\a\one.txt (12B)          scanner\a\one-copy.txt (12B，与 one.txt 同内容)
scanner\a\sub\two.log (50B，含 "TODO")   scanner\b\three.md (17B)
scanner\b\hidden.txt (14B，Hidden 属性)  scanner\b\empty\        (空目录)
scanner\b\emptytree\inner\       (空子树)  scanner\c\four.bin (20B)
scanner\c\four-copy.bin (20B，同上)      scanner\c\deep\d\e\four-deep.bin (20B，同上)
```

## 1. 用例与退出码总表（46 个用例，全部真实执行）

| 用例 | 命令要点 | exit |
|---|---|---|
| 01-find-all | `scan find --path <T> --json` | 0 |
| 02-find-include-log | `--include "**/*.log"` | 0 |
| 03-find-ext-minsize | `--ext txt --ext bin --min-size 15` | 0 |
| 04a / 04b / 04c | `--name-regex ^one` / `--content-regex TODO` / `--ext log --content-regex TODO` | 0 |
| 05-find-sort-max | `--newer 24h --sort size --reverse --max-results 3` | 0（`truncated=true`） |
| 05b / 05c | `--newer 2h` / `--older 1w` | 0 |
| 06-find-hash | `--include "**/*.txt" --hash sha256` | 0 |
| 07-find-hidden | `--hidden --ext txt` | 0 |
| 08a / 08b | `--ext bin --jsonl` / `--sort path --jsonl` | 0 |
| 09 / 09b / 09c | `--depth 1` / `--depth 2` / `--dirs-only` | 0 |
| 10 / 11 | `scan size --top 5` / `--dirs-only --relative` | 0 |
| 12 | `scan tree --depth 2` | 0 |
| 13 | `scan dup --path <T>` | 0 |
| 15 / 15b / 16 | `scan recent --newer 1h --max-results 3` / `--newer 2h` / `--newer 1s` | 0 |
| 17 | `scan empty-dirs` | 0 |
| 18 / 19 / 20 | `hash file`（默认三算法）/ `--expected <正确 sha256>` / `--expected deadbeef` | 0 |
| 21 / 22 | `hash dir` / `hash dir --out` | 0 |
| 23 / 24 / 25 | `hash compare` 同内容 / 不同内容 / 两份清单 | 0 |
| 14 / 32 | `scan snapshot --out` / `scan verify`（变更前） | 0 |
| 33 / 34 | `scan verify`（变更后）/ `--full` | 0 |
| 35 / 36 | 独占锁住文件后 `hash file` / `hash dir` → **部分失败** | **6 / 6** |
| 26 / 27 / 28 | 缺 `--path` / 目录不存在 / `hash dir --algo crc32` | **2 / 3 / 2** |
| 29 / 39 | 覆盖已存在文件但没加 `--yes` | **2** |
| 30 / 37 | `--dry-run` | 0（零副作用） |
| 38 / 40 | 新建快照 / 覆盖加 `--yes` | 0 |
| 31 | `help --json` → 11 个命令全部注册 | 0 |

非 0 退出用例（全部符合契约 §4）：
`26=2, 27=3, 28=2, 35=6, 36=6, 39=2`。

## 2. 逐命令证据

### 2.1 `scan.find` — 灵活递归扫描

**01 基线**（`scan find --path var\test\scanner --json`）：`count=7 scanned=8 matched=7 skipped=1 denied=0 failures=0`。
7 个非隐藏文件全部命中；`hidden.txt` 被默认排除（但计入 `scanned`，故 8→7）。

**02 `--include "**/*.log"`**：`count=1 matched=1`，命中 `a\sub\two.log`（`**/` 支持 0 层与多层）。
**03 `--ext txt --ext bin --min-size 15`**：`count=3` —— 3 个 20B 的 `.bin` 命中，12B 的 `.txt` 被 `--min-size` 排除。
**04a `--name-regex ^one`**：`count=2`（`one.txt`、`one-copy.txt`）。
**04b `--content-regex TODO`**：`count=1`（只有 `two.log` 文本含 TODO，说明确实读了内容）。
**04c `--ext log --content-regex TODO`**：`count=1`（扩展名 + 内容两个条件同时生效）。
**05 `--newer 24h --sort size --reverse --max-results 3`**：`count=3`、`truncated=true`、`data.truncatedAt="max-results"`。
**05b `--newer 2h` → 7；05c `--older 1w` → 0**（所有文件都是刚创建的，符合 mtime 区间语义；两处都由 Core 的 `ArgMap.TryParseDate` 解析裸时长，本模块不再自定义时间解析）。
**06 `--hash sha256`**：命中项带 `hash` 字段（`one.txt` / `one-copy.txt` 均为 `19273dfe5b6b9acdeaf793df4006435ee903ea1f32a7b01493d700942fa00f9b`，与 `scan.dup`/`hash.dir` 的结论一致），`data.hashAlgo="sha256"`。
**07 `--hidden --ext txt`**：`count=3`（多出被隐藏的 `hidden.txt`）。
**09 `--depth 1` → 0 项**（根目录下没有直接文件）；**09b `--depth 2` → 5 项**（`a\one.txt`、`a\one-copy.txt`、`b\three.md`、`c\four.bin`、`c\four-copy.bin`），深度 3 的 `a\sub\two.log` 与深度 5 的 `four-deep.bin` 被排除 —— 严格按契约 §7「`--depth <n>` 最大深度」。
**09c `--dirs-only`**：`count=10`，返回 10 个目录（不含文件）。

**08a `--jsonl`（真流式，边扫边发）** 实际输出：

```
{"type":"item","item":{...four-copy.bin...}}
{"type":"item","item":{...four.bin...}}
{"type":"item","item":{...four-deep.bin...}}
{"type":"meta","cmd":"scan.find","version":"0.2.0","protocol":"1"}
{"type":"summary","ok":true,"count":3,"elapsedMs":219,"truncated":false,"warnings":[]}
```

3 条 item **无重复**、末尾 summary、exit 0。**注意顺序**：真流式时 item 出现在 meta 之前（`Out.Result` 才写 meta），与契约 §3「第一行 meta」不一致 —— 见 §5 遗留问题，已上报 Lead 决策。
**08b `--jsonl --sort path`**：排序需要缓冲，故非流式（meta → 3 item → summary，同样无重复）。

### 2.2 `scan.size` — 目录体积排行

**10 `--top 5`**：`count=3`（根目录只有 3 个子项），`totalSize=151`（`74+60+17`），占比四舍五入到 2 位：

```
dir a  74 B  49.01%  files=3
dir c  60 B  39.74%  files=3
dir b  17 B  11.26%  files=1
```

**11 `--dirs-only --relative`**：`count=3`，`path` 为相对路径（`--relative` 生效）。
（`scan.size` 为了统计"体积"，会忽略 `--min-size/--max-size/--newer/--older/--depth`；`--ext/--include/--exclude/--exclude-dir/--hidden` 仍生效。）

### 2.3 `scan.tree` — 目录树（含每层汇总）

**12 `--depth 2`**：`count=8` 个节点（root + 7 个深度 ≤2 的目录），`data.levels` 每层汇总：

```
d0: dirs=1 files=0 size=0 B
d1: dirs=3 files=5 size=81 B
d2: dirs=4 files=1 size=50 B

scanner/ 131 B |   a/ 74 B |     sub/ 50 B |   b/ 17 B |     empty/ 0 B |     emptytree/ 0 B |   c/ 40 B |     deep/ 0 B
```

每个节点的 `size` 是**子树**大小，`directFiles` 是本层直接文件数；`tree` 字段是带缩进的可读列（人类模式用）。

### 2.4 `scan.dup` — 重复文件（三段式）

**13**：`groupCount=2 duplicateFiles=5 reclaimable=52 B`，`sizeCandidates=2 quickCandidates=2 quickHashed=5 fullHashed=5`。
两个组：3×`four*.bin`（20B，回收 40B）+ 2×`one*.txt`（12B，回收 12B）。
三段式（`size` 分组 → `Fs.QuickHash` 预筛 → 全量 `sha256` 确认）通过 `quickHashed/fullHashed` 计数可验证；默认 `--min-size 1` 跳过 0 字节噪声。

### 2.5 `scan.recent` — 最近修改

**15 `--newer 1h --max-results 3`**：`count=3`、`matched=7`、`truncated=true`；每条含 `ageSeconds`/`ageHuman`。
**15b `--newer 2h`** → 7（`since` = 当前时间-2h）；**16 `--newer 1s`** → 0。

### 2.6 `scan.empty-dirs`

**17**：`emptyCount=2`（严格空目录：`b\empty`、`b\emptytree\inner`）、`filelessCount=3`（无文件子树，多出 `b\emptytree`，见 `data.filelessDirs`），`items` 只列严格空目录并带 `empty`/`fileless` 标志。

> ⚠️ `--depth` 会同时缩短"空"的判定视界：例如 `scan empty-dirs --depth 2` 会把 `c\deep` 报成严格空目录，
> 因为它的唯一文件 `c\deep\d\e\four-deep.bin` 在视界之外（未被枚举）。**找空目录做清理时不要加 `--depth`**（默认 0 = 不限）。

### 2.7 `scan.snapshot` + `scan.verify` — 快照与差异校验

**14 `scan snapshot --path <T> --out var\test\snap.json`**：`count=7`、`totalSize=151`、`bytesWritten=2602`。
快照文件内 `entries[].path` **恒为相对根路径**（另有 `full`/`size`/`mtimeMs`/`mtime`/`attrs`/`hash`），可移植、可跨机校验；信封里的 `data.items[].path` 默认绝对路径（契约 §5），`--relative` 转相对。

**32 变更前 `scan verify --snapshot snap.json`**：`consistent=true`、`unchanged=7`、`items=[]`。

随后制造 4 类变更：`a\one.txt` 改内容但**大小不变且 mtime 原样写回**、`b\three.md` 改大小、删除 `a\one-copy.txt`、新增 `b\new-file.txt`。

**33 默认 `scan verify`**（size + mtime 判定）：

```
consistent=false added=1 removed=1 modified=1 corrupted=0 unchanged=5
items: removed:a\one-copy.txt | added:b\new-file.txt | modified:b\three.md(size)
```

`a\one.txt` **未被报出** —— 元数据完全一致（这正是默认策略的边界）。

**34 `scan verify --full`**（额外对未变更文件做全量哈希）：

```
consistent=false added=1 removed=1 modified=1 corrupted=1 unchanged=4
items: removed:a\one-copy.txt | corrupted:a\one.txt | added:b\new-file.txt | modified:b\three.md
```

同大小 + 同 mtime 但内容被改的 `a\one.txt` 被判定为 **corrupted**。`--tolerance <dur>`（默认 1500ms）可调 mtime 容差。

**覆盖闸门 / `--dry-run`**：
* `29`/`39` 覆盖已存在的 `snap.json`/`tmp-snap.json` 且没加 `--yes` → `E_NEEDS_CONFIRM`，**exit 2**；
* `30`/`37` `--dry-run` → `bytesWritten=0`，且 `snap.json` 的 SHA256 前后**完全一致**（`identical=True`），零副作用；
* `38` 新建 → 0；`40` 覆盖加 `--yes` → 0。

### 2.8 `hash.file`

**18 默认三算法**（`md5`/`sha1`/`sha256`）：`data.hashes` 为算法→hex 映射，`data.primary` 指向 sha256，`items`/`count`/`columns` 齐全（人类模式三行表格）。
**19 `--expected <正确 sha256>`**：`matched=true`、exit 0。
**20 `--expected deadbeef`**：`matched=false`、exit 0（stderr 一条 `哈希与 --expected 不匹配` 警告）。
`--expected` 支持 `sha256:<hex>` 形式（`expectedAlgo` 会指明只比对该算法）。

### 2.9 `hash.dir`

**21 `hash dir --path <T>`**：`count=7`、`totalSize=151`、`treeHash=3b40adf6…5539`、`complete=true`。
清单文本规范：`相对路径<TAB>size|hash` 按相对路径排序（OrdinalIgnoreCase 再 Ordinal）后拼成 UTF-8 文本，再对该文本做一次 `sha256` → `treeHash`。因此**同一份目录内容，换机器/换盘符/换扫描顺序都得到同一个 treeHash**。
**22 `--out var\test\dirhash.json`**：写出清单（`entries[].path` 为相对路径，含 `full`），`bytesWritten` 有值；覆盖同样需要 `--yes`，`--dry-run` 不写盘。
`--algo crc32` 被明确拒绝（`28`，exit 2）—— crc32 不适合做目录汇总哈希。

### 2.10 `hash.compare`

**23 两个同内容文件**：`mode=file`、`equal=true`、`items=[{field:sha256,equal:true}]`。
**24 两个不同文件**：`mode=file`、`equal=false`、`items=[size:false, sha256:false]`。
**25 两份清单**（`scan.snapshot` 产物 vs `hash.dir` 产物，同一目录）：`mode=manifest`、`equal=true`、`unchanged=7`、`items=[]`、`treeHashEqual=null`（快照没算 treeHash）。
若一边是清单一边是普通文件 → `E_USAGE`（exit 2）。

### 2.11 部分失败（硬性要求 4：不整体崩，记 failures + exit 6）

用独占句柄（`FileShare.None`）锁住 `b\three.md` 后运行：

* **35 `hash file`**：**exit 6**，`partial=true`，`data.failures` 3 条（md5/sha1/sha256 各一条 IOException），`hashes` 为 `{}`；
* **36 `hash dir`**：**exit 6**，`partial=true`，`failures=1`，`complete=false`，其余 6 个文件仍正常出哈希、`treeHash` 仍给出（`78b685fc…2055`）。

`scan.find --content-regex` 的读取失败同样进入 `data.failures`（`op="content-regex"`）并返回 6。

## 3. 与 CLI 契约的对应

| 契约条款 | 实现 |
|---|---|
| §3 一个 JSON 信封，人类文本走 stderr | 全部结果走 `ctx.Out.Result`，本模块**没有一处** `Console.WriteLine` 输出 JSON |
| §4 退出码 | 0 成功 / 2 用法 / 3 目标不存在 / 6 部分成功，均有真实用例（见 §1） |
| §5 列表类 `items`+`count`+`columns` | 所有列表命令齐备；过滤类含 `scanned/matched/skipped/denied`；截断时 `truncated=true` + `data.truncatedAt`（`max-results`/`top`） |
| §5 读失败 → `data.failures` + exit 6 | 用例 35/36（另有 `partial` 布尔便于机器判断） |
| §5 默认绝对路径，`--relative` 转相对 | 所有列表命令的 `path` 字段；**清单文件内部**恒用相对路径（可移植） |
| §7 扫描选项词汇 | `--path/--include/--exclude/--exclude-dir/--ext/--name-regex/--content-regex/--min-size/--max-size/--newer/--older/--depth/--hidden/--follow/--max-results/--parallel/--sort/--reverse/--hash/--relative` 全部实现；可重复选项走 `ctx.GetAll`，**不做逗号分割** |
| §9 破坏性操作 | 只有 `scan.snapshot --out`、`hash.dir --out` 会写盘；覆盖已存在文件需 `--yes`，`--dry-run` 零副作用（用例 29/30/37/39/40） |
| §6 `--jsonl` 边扫边发 | `scan.find` 真流式（用例 08a），无重复、末尾 summary、exit 0 |

## 4. 设计决策与已知边界

1. **`--depth` 上界过滤**：Core 的 `Fs.Enumerate` 在 `--depth n` 下会多吐一层（目录内条目先 yield、再判断是否下推），我在 `WalkMany/WalkOne` 里按相对层数做上界过滤，保证用户看到的就是 n 层（用例 09/09b 证明）。
   ⚠️ 副作用：`scan.empty-dirs` 的"空/无文件"判定视界也会被 `--depth` 截断（视界外的文件看不见）→ 清理空目录时用默认的 `--depth 0`；`scan.tree`/`scan.size` 的大小汇总同理只在视界内。已用真实用例确认并写入 §2.6。
2. **`scan.find --hash` 与 `--sort`/`--content-regex`**：哈希与排序需要先收集再并行处理，因此这三项存在时不做流式（`--jsonl` 仍输出合法 NDJSON）；仅"无排序/无内容正则/无哈希"时边扫边发。
3. **内容正则**：单文件最多读 8MB 文本（UTF-8/带 BOM 自适应），正则带 **5 秒超时**，超时记入 `failures` 而不是挂死。
4. **并行**：`Fs.ParallelMap`，`--parallel` 默认 CPU 核数、上限 32；`scan.size` 对每个子目录用独立 options 副本求和，避免共享可变状态。
5. **`scan.dup` 三段式**：size 分组 → `Fs.QuickHash`（头尾 64KB 指纹）预筛 → 全量哈希确认，`reclaimable = Σ size×(count-1)`。
6. **`hash.file --expected` / `scan.verify` / `hash.compare` 的"不一致"不改变退出码**：一律 `ok=true` + 明确布尔字段（`matched`/`consistent`/`equal`），只有"用法/不存在/读取失败"才非 0。这样 agent 能区分"命令失败"与"校验结论是否定"。
7. **`scan.snapshot` 只接受一个根**（多根会让相对路径产生歧义）；`hash.dir` 同理。
8. **`scan.size` 忽略 `--min-size/--max-size/--newer/--older/--depth`**：体积统计需要全量，故这四项在 size 场景下无意义。

## 5. 遗留问题 / 需要 Lead 决策

1. **`--jsonl` 帧顺序**（真实存在，需契约或 Core 二选一）：
   契约 §3 要求"第一行 meta"，但真实流式必须"先出 item、扫描结束才由 `Out.Result` 写 meta+summary"。
   现状（用例 08a）：`item… → meta → summary`，无重复、summary 的 `count` 正确。
   可选方案：**(a)** 契约放宽为"以 `type` 字段分派、meta 可能后置"（我这边零改动）；**(b)** Core 增加 `Out.BeginStream(cmd)` 预写 meta（`Result` 检测已写则跳过），我把 `scan.find` 改成先调一次即可严格 meta 在前。
   我倾向 (b) 更符合 agent 消费习惯，但要动 `Runtime.cs`（Lead 的文件）。
2. **Core 两处修复已由 Lead 落地并验证**：`Runtime.EmitItem` 现在流式输出且**不驻留内存**（`_emitted` 计数），`Cli.TryParseDate` 认得裸时长（`--newer 2h` 用例 05b/15b）。我已删掉本模块自己的时间归一化，改为直接依赖 Core。我的 `JsonlFlushDuplicatesStreamed()` 能力探测现在返回"可以安全流式"，若 Core 再变它会自动退回非流式，保证 NDJSON 不重复。
3. **全量 `build.ps1` 已 0 error**（曾一度因他人模块 5 个错误而失败，现已修好）。当前全量构建 4 个 warning 全部来自 `ProcCommands.cs` / `NetCommands.cs` 的 CS0649（未赋值字段），我的两个文件 0 error / 0 warning。
4. 本机慢盘导致的取舍：`Get-AuthenticodeSignature` 之类 30~45s 的操作本模块完全没碰；`scan.tree`/`scan.empty-dirs` 每个目录额外做一次 `GetFiles()`（不递归重扫），深度受限时开销可控。

## 6. 原始证据文件索引

```
var\test\run-scanner-evidence.ps1      一键复现（重建测试树 + 46 个用例 + 退出码汇总）
var\test\out\<case>.json               每个用例的 stdout（46 个）
var\test\out\<case>.err                每个用例的 stderr（含警告/错误信封时的正文）
var\test\scanner\                      测试数据（可随时重建）
var\test\snap.json                      scan.snapshot 产物（含相对路径/哈希）
var\test\dirhash.json                   hash.dir 清单产物
```
