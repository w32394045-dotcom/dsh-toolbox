# dsh-toolbox CLI 契约（冻结版 v1）

> 契约是**对外承诺**：agent 会按此解析输出。任何破坏兼容性的改动必须升 major 版本并通知 Lead。

## 1. 调用形式

```
dsh-toolbox <command> [options] [positional...]
dsh-toolbox <group> <action> [options] [positional...]
```

* 命令名 `group.action`，CLI 上写作空格分隔：`scan find` → `scan.find`。
* 单词命令（无 group）：`doctor`、`sysinfo`、`manifest`、`version`、`help`。
* 选项：`--name value`、`--name=value`、`-n value`；可重复选项累加（`GetAll`）。
* **不做逗号自动分割**：`--include "a,b"` 是一个模式。多值请重复给。
* `--` 之后全部视作位置参数。

## 2. 全局选项（所有命令通用，由 `Program.cs` 统一解析）

| 选项 | 说明 |
|---|---|
| `--json` | stdout 只输出**一个** JSON 信封（默认人类可读） |
| `--jsonl` | 流式：每行一个 JSON 对象（适合大量结果，边扫边出） |
| `--quiet` | 抑制 stderr 的进度/警告 |
| `--no-color` | 关闭颜色（`--json` 时自动关闭） |
| `--verbose` | 额外诊断信息写入 stderr 与日志 |
| `--dry-run` | 破坏性命令只报告计划，**零副作用** |
| `--yes` | 确认执行破坏性操作 |
| `--timeout <dur>` | 全局超时，如 `30s`、`5m`；超时 → exit 5 |
| `--home <dir>` | 覆盖数据目录（默认见 §6） |
| `--cwd <dir>` | 覆盖工作目录（相对路径基准） |
| `--no-log` | 本次调用不写运行记录 |
| `-h` / `--help` | 命令帮助（`--json` 时输出结构化帮助） |
| `--version` | 版本 |

时间/大小字面量：`30s 5m 2h 3d 1w`；`10KB 4MB 2GB 1TB`（1024 进制，后缀可省略）。
时间点：`2026-10-01`、`2026-10-01T12:00:00`、`-2h`（相对现在）。

## 3. 输出信封（`--json`）

stdout **必须只有一个 JSON 对象**，且不夹带任何其它文本（人类日志走 stderr）。

成功：
```json
{
  "ok": true,
  "cmd": "scan.find",
  "version": "0.2.0",
  "elapsedMs": 128,
  "data": { "...": "命令自定义，见 §5" },
  "warnings": ["跳过 3 个无权限目录"],
  "truncated": false
}
```

失败：
```json
{
  "ok": false,
  "cmd": "scan.find",
  "version": "0.2.0",
  "elapsedMs": 12,
  "error": { "code": "E_USAGE", "message": "缺少必填选项 --path", "hint": "例：scan find --path ." }
}
```

`--jsonl`：第一行 `{"type":"meta","cmd":...,"version":...}`，随后每行

> **流式命令必须在产生第一帧之前调用 `Ctx.Out.BeginStream(cmd)`**。
> 未调用时由 `Output.Result` 在收尾补写 meta，此时 meta 会出现在 item 之后——那是实现兜底，调用方不应依赖该顺序。

随后每行
`{"type":"item",...}`，最后一行 `{"type":"summary","ok":true,"count":N,"elapsedMs":...}`。

## 4. 退出码（严格语义，agent 依赖它做判断）

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 运行期错误（E_*） |
| 2 | 用法错误（缺参数/参数非法/未加 `--yes`） |
| 3 | 目标不存在 |
| 4 | 权限被拒绝 |
| 5 | 超时 |
| 6 | 部分成功（有失败项，结果仍有效，见 `data.failures`） |
| 130 | 被取消（Ctrl+C） |
### 4.2 包装 / 后台类命令（`run`、`job.*`）的退出码

被包装的外部命令**自身失败**时：工具本身是成功的，所以返回 `ok:true` + **exit 6（部分成功）**，
失败细节放在 `data.exitCode` / `data.stderr`（不要让工具的退出码直接等于子进程退出码，
否则 3 会被误读成 E_NOT_FOUND、5 被误读成超时）。调用方判断"子命令是否成功"请看 `data.exitCode`。
若需要"子命令失败即整体失败"的严格语义，用 `run --fail-on-error`（可选实现）。

`job.*` 只描述任务状态：`data.state ∈ starting|running|completed|failed|killed`，
查询类命令（list/status/output）自身永远是 exit 0；只有"任务 id 不存在"才返回 3。
### 4.1 验证 / 断言类命令的退出码例外

`sign.verify`、`sign.chain`、`hash.file --expected`、`hash.compare`、`scan.verify`、
`net.http --expect-status` 这类**结论型**命令，退出码语义如下：

| 情况 | ok | 退出码 | 必须字段 |
|---|---|---|---|
| 验证通过 | `true` | 0 | `data.verdict = true` |
| 验证未通过（命令本身执行成功，只是结论为否） | `true` | **1** | `data.verdict = false` + `data.reason` |
| 运行期错误（文件不存在/权限/超时/参数非法） | `false` | 2/3/4/5 | `error` |

要点：

* **结论为否 ≠ 命令失败**：`ok` 仍为 `true`，`data` 必须完整（这样 agent 既能看退出码，也能看细节）。
* `data.verdict` 是统一字段（布尔）；各命令原有的布尔字段（如 `valid`、`match`）**可以保留**，
  但 `verdict` 必须存在，方便 agent 用一套逻辑处理所有校验类命令。
* 断言类选项（`--publisher`、`--expected`）不匹配时同样走 exit 1 + `--verdict=false`。

## 5. `data` 通用约定

* 列表类命令：`data.items`（数组）+ `data.count`（整数）+ `data.columns`（可选，人类模式列名）。
* 过滤类命令：`data.scanned`、`data.matched`、`data.skipped`、`data.denied`。
* 截断时 `truncated=true` 且 `data.truncatedAt` 说明原因（`max-results` / `max-bytes` / `timeout`）。
* 失败项进 `data.failures`（数组，元素含 `path` 与 `error`），并据此返回 6。
* 所有路径默认**绝对路径**；`--relative` 可改为相对 `--cwd`。

## 6. 数据目录（默认）

```
%LOCALAPPDATA%\dsh-toolbox\
  logs\toolbox-YYYYMMDD.jsonl     每行一条结构化日志
  runs\runs.jsonl                 每次调用的运行记录（命令/参数/退出码/耗时）
  runs\<runid>.out.json           大结果落盘（配合 job/artifact 读取）
  jobs\<jobid>\                   后台任务：cmd.json / stdout.log / stderr.log / status.json
  cache\                          临时缓存
```
覆盖优先级：`--home` > 环境变量 `DSH_TOOLBOX_HOME` > 上述默认。
`doctor --json` 必须回报实际生效路径。

## 7. 文件扫描的选项词汇（**统一命名**，各命令复用）

| 选项 | 语义 |
|---|---|
| `--path <dir>` | 扫描根（可重复；也可用位置参数） |
| `--include <glob>` | 白名单（可重复，`**`/`{a,b}` 支持），匹配**相对路径** |
| `--exclude <glob>` | 黑名单（可重复） |
| `--exclude-dir <name>` | 目录名黑名单（可重复），如 `node_modules`、`.git` |
| `--ext <ext>` | 扩展名白名单（可重复，可带可不带点） |
| `--name-regex <re>` | 文件名正则 |
| `--content-regex <re>` | 文件内容正则（触发读取；配合 `--ignore-case`） |
| `--min-size/--max-size <size>` | 大小区间 |
| `--newer/--older <dur\|date>` | mtime 区间（`--newer 2h` = 最近 2 小时） |
| `--depth <n>` | 最大深度，0=不限 |
| `--hidden` | 包含隐藏/系统文件（默认排除） |
| `--follow` | 跟随符号链接/联接点（默认不跟随，防环） |
| `--max-results <n>` | 提前终止 |
| `--parallel <n>` | 并行度，默认 CPU 核数，上限 32 |
| `--sort <key>` | `name\|path\|size\|mtime`，配合 `--reverse` |
| `--hash [algo]` | 对结果计算哈希（默认 sha256） |

## 8. stdio 通道（`serve --stdio`）

面向"让 DSH 常驻连接、持续读日志/结果"的需求。**面向行**（NDJSON）的 JSON-RPC 2.0：

* 请求：`{"jsonrpc":"2.0","id":1,"method":"call","params":{"cmd":"scan find","args":["--path","."],"--json":true}}`
* 响应：`{"jsonrpc":"2.0","id":1,"result":{"ok":true,"data":{...}}}`
* 通知（服务端主动推送，无 `id`）：`{"jsonrpc":"2.0","method":"event","params":{"type":"log","line":{...}}}`

必需方法：

| method | 说明 |
|---|---|
| `initialize` | 返回 `{serverInfo, protocolVersion, home, commands:[...]}` |
| `ping` | `{"pong":true,"ts":...}` |
| `commands.list` | 命令清单（等价 `manifest`） |
| `commands.schema` | 某命令的参数 schema |
| `call` | 执行命令，返回信封（等价一次 CLI 调用） |
| `log.tail` | `{file?, lines?, follow?}` 返回最近 N 行；`follow=true` 时持续推送 `event` |
| `log.search` | `{pattern, since?, limit?}` 在日志目录内检索 |
| `job.list` / `job.get` / `job.kill` | 后台任务 |
| `artifact.read` | 按 runid/offset/limit 读取大结果 |
| `shutdown` | 退出 |

约束：stdout **只允许**协议帧；一切人类文本走 stderr。每帧必须是单行合法 JSON。
`initialize` 前收到其它请求 → 返回 `-32002`。未知方法 → `-32601`。

## 9. 破坏性操作的统一约束

删除/移动/覆盖/杀进程/改注册表/改 ACL/写系统设置：

1. 默认（无 `--dry-run` 无 `--yes`）→ **exit 2**，错误信息写明"加 --yes 执行，或 --dry-run 预览"。
2. `--dry-run` → 正常返回计划（`data.plan`），`ok=true`，exit 0，**零副作用**。
3. 批量操作中个别失败 → 记入 `data.failures`，返回 exit 6（不要中途抛出丢失已完成部分）。
4. 永不触碰用户数据目录，除非显式给出该路径。
