# dsh-toolbox 命令清单（自动生成）

> 由 `manifest --json` 自动生成，**请勿手改**。

- 版本 `0.2.0`　协议 `1`　命令总数 **68**　分组 20 个
- 生成时间 2026-10-01 13:02:06
- SHA256 `D4A2213C8E8D1ED029034CD2AACE0EC9C1FDD98B56CE7CC440C1B5257049EB6D`

## 双模说明

- 双击 / 无参数且独占控制台 → **启动 GUI**；`--gui` 强制 GUI，`--cli` 强制命令行。
- 带参数调用（agent 场景）→ **CLI**，stdout 输出 JSON 信封。

## 全局选项
- `--json` (bool) — stdout 输出单个 JSON 信封
- `--jsonl` (bool) — 流式 JSON，每行一条
- `--quiet` (bool) — 抑制 stderr 信息
- `--dry-run` (bool) — 破坏性操作只预览
- `--yes` (bool) — 确认破坏性操作
- `--timeout` (duration) — 全局超时，如 30s/5m
- `--home` (path) — 数据目录
- `--cwd` (path) — 相对路径基准
- `--verbose` (bool) — 诊断信息
- `--no-log` (bool) — 不写运行记录

## 退出码
- `0` — 成功
- `1` — 运行期错误
- `2` — 用法错误
- `3` — 目标不存在
- `4` — 权限被拒绝
- `5` — 超时
- `6` — 部分成功
- `130` — 被取消

## 命令

### (无分组)（6 条）

#### `doctor`

自检：环境、权限、数据目录、磁盘、命令可用性

```
dsh-toolbox doctor [--json]
```
- 例：`dsh-toolbox doctor --json`

#### `env`

环境变量查询（默认脱敏疑似密钥）

```
dsh-toolbox env [--match <regex>] [--show-secrets]
```
- 例：`dsh-toolbox env --match ^DSH_`
- 例：`dsh-toolbox env --show-secrets --json`

#### `manifest`

输出全部命令的机器可读清单（agent 用）

```
dsh-toolbox manifest [--json]
```
- 别名：`commands`
- 例：`dsh-toolbox manifest --json`

#### `run`

执行一条外部命令并完整记录（stdout/stderr/退出码/耗时/工作目录）

```
dsh-toolbox run [--cwd <dir>] [--env K=V]... [--shell] [--timeout <dur>] [--capture[=false]] [--max-bytes <n>] [--cmd <原始命令行>] -- <命令> [参数...]
```
- 例：`dsh-toolbox run --json -- cmd /c "echo hi & exit 3"`
- 例：`dsh-toolbox run --timeout 30s -- git status`
- 例：`dsh-toolbox run --shell --cmd "dir /b" --json`

#### `serve`

常驻 stdio JSON-RPC 2.0 服务（NDJSON）：读日志/查结果/发任务

```
dsh-toolbox serve --stdio
```
- 例：`dsh-toolbox serve --stdio`

#### `sysinfo`

系统信息：OS/CPU/内存/磁盘/启动时间

```
dsh-toolbox sysinfo [--fast]
```
- 例：`dsh-toolbox sysinfo`
- 例：`dsh-toolbox sysinfo --json`


### compat（2 条）

#### `compat.check`

环境体检：OS/CPU 指令集/.NET/PowerShell/长路径/Defender/磁盘/网络

```
dsh-toolbox compat check [--fast] [--json]
```
- 例：`dsh-toolbox compat.check --json`
- 例：`dsh-toolbox compat.check --fast`

#### `compat.fix`

按体检结论执行修复（部分需管理员，会自动提示提权）

```
dsh-toolbox compat fix --id <longpaths\|defender\|caches\|leftovers> [--yes]
```
- 例：`dsh-toolbox compat.fix --id longpaths --yes --json`


### defender（1 条）

#### `defender.status`

Windows Defender 状态与排除项（WMI SecurityCenter2 + 注册表）

```
dsh-toolbox defender status [--json]
```
- 例：`dsh-toolbox defender status --json`


### disk（2 条）

#### `disk.health`

物理磁盘健康概览（WMI 状态 + SMART 预测，尽力而为）

```
dsh-toolbox disk health [--json]
```
- 例：`dsh-toolbox disk health --json`

#### `disk.space`

各卷容量/可用/文件系统/使用率

```
dsh-toolbox disk space [--path <p>] [--fixed-only]
```
- 别名：`df`
- 例：`dsh-toolbox disk space --json`


### elevate（1 条）

#### `elevate.run`

以管理员身份执行一条工具箱命令（弹 UAC；结果通过临时文件回传）

```
dsh-toolbox elevate run -- <命令> [参数...]
```
- 例：`dsh-toolbox elevate.run -- compat.fix --id longpaths --yes --json`


### eventlog（2 条）

#### `eventlog.list`

可用事件日志清单（名称/条目数）

```
dsh-toolbox eventlog list [--json]
```
- 例：`dsh-toolbox eventlog list --json`

#### `eventlog.query`

事件日志查询：LogName/Level/Provider/Id/时间区间/关键字

```
dsh-toolbox eventlog query [--log System] [--level error\|warning\|info\|critical\|verbose]... [--provider <n>]... [--id <n>]... [--since <-2h\|date>] [--until <date>] [--keyword <s>] [--message-regex <re>] [--max <n>] [--oldest] [--no-message]
```
- 别名：`evt`
- 例：`dsh-toolbox eventlog query --log System --level error --max 10 --json`
- 例：`dsh-toolbox eventlog query --log Application --since -24h --keyword crash`


### hash（3 条）

#### `hash.compare`

比对两个文件或两个哈希清单

```
dsh-toolbox hash compare <a> <b> \| --a <a> --b <b> [--algo <algo>]... [--max-results <n>]
```
- 别名：`hash.diff`
- 例：`dsh-toolbox hash compare a.txt b.txt --json`
- 例：`dsh-toolbox hash compare --a var\before.json --b var\after.json --json`

#### `hash.dir`

目录整体哈希清单 + 汇总哈希（判断目录是否变化）

```
dsh-toolbox hash dir --path <dir> [--algo <algo>] [--out <file>] [--include <glob>]... [--exclude <glob>]... [--exclude-dir <name>]... [--ext <ext>]... [--depth <n>] [--hidden] [--follow] [--max-results <n>] [--parallel <n>] [--relative]
```
- 别名：`hash.tree`
- 例：`dsh-toolbox hash dir --path C:\data --json`
- 例：`dsh-toolbox hash dir --path . --algo sha256 --out var\dir-hash.json --json`

#### `hash.file`

单文件多算法哈希（--algo 可重复），可选 --expected 比对

```
dsh-toolbox hash file --path <file> [--algo <algo>]... [--expected <hex>]
```
- 例：`dsh-toolbox hash file --path readme.md --json`
- 例：`dsh-toolbox hash file --path setup.exe --algo md5 --algo sha1 --algo sha256`
- 例：`dsh-toolbox hash file --path a.bin --algo sha256 --expected 9f86d081... --json`


### host（1 条）

#### `host.status`

DSH 宿主状态：桌面端/CLI 安装情况、版本、进程、数据目录、权限

```
dsh-toolbox host status [--json]
```
- 别名：`status`
- 例：`dsh-toolbox host.status --json`


### install（4 条）

#### `install.check`

检查官方更新源：最新版本/下载地址/大小/SHA512/是否有更新

```
dsh-toolbox install check [--json]
```
- 例：`dsh-toolbox install.check --json`

#### `install.cli`

安装官方 CLI（npm 包 @deepseek-ai/dsh，用户级，不需要管理员）

```
dsh-toolbox install cli [--version <v>] [--file <tgz>] [--registry <url>] [--dry-run] [--yes]
```
- 例：`dsh-toolbox install.cli --dry-run --json`
- 例：`dsh-toolbox install.cli --yes`

#### `install.desktop`

安装/升级官方桌面端（下载→校验→清残留→静默安装→校验→启动）

```
dsh-toolbox install desktop [--file <exe>] [--url <url>] [--sha512 <b64>] [--dry-run] [--yes] [--no-start]
```
- 例：`dsh-toolbox install.desktop --dry-run --json`
- 例：`dsh-toolbox install.desktop --yes`

#### `install.verify`

只做校验：对已有安装包验证 SHA512 与数字签名（不安装）

```
dsh-toolbox install verify --file <exe> [--sha512 <b64>] [--publisher <名>]
```
- 例：`dsh-toolbox install.verify --file x.exe --json`


### installed（1 条）

#### `installed.list`

已安装程序（HKLM + WOW6432Node + HKCU 的 Uninstall 键）

```
dsh-toolbox installed list [--match <re>] [--publisher <s>] [--all] [--sort name\|date\|size]
```
- 别名：`apps`
- 例：`dsh-toolbox installed list --match "node|python" --json`
- 例：`dsh-toolbox installed list --sort size --json`


### job（5 条）

#### `job.kill`

结束后台任务（破坏性：需 --yes；--dry-run 只预览）

```
dsh-toolbox job kill (<jobId>\|--id <jobId>) [--dry-run\|--yes] [--reason <文本>]
```
- 例：`dsh-toolbox job kill <jobId> --dry-run`
- 例：`dsh-toolbox job kill <jobId> --yes`

#### `job.list`

列出后台任务（默认 50 条，新的在前）

```
dsh-toolbox job list [--limit <n>] [--state <state>] [--all]
```
- 例：`dsh-toolbox job list --json`
- 例：`dsh-toolbox job list --state running`

#### `job.output`

读取后台任务输出（--tail / --since / --follow）

```
dsh-toolbox job output (<jobId>\|--id <jobId>) [--tail <n>] [--since <dur\|date>] [--stream stdout\|stderr\|both] [--follow] [--max-bytes <n>]
```
- 例：`dsh-toolbox job output <jobId> --tail 50 --json`
- 例：`dsh-toolbox job output <jobId> --follow --jsonl`

#### `job.start`

后台启动一条命令（分离进程，不随 CLI 退出而终止）

```
dsh-toolbox job start [--name <标签>] [--cwd <dir>] [--env K=V]... [--shell] [--timeout <dur>] [--cmd <原始命令行>] -- <命令> [参数...]
```
- 例：`dsh-toolbox job start -- cmd /c "ping -n 20 127.0.0.1"`
- 例：`dsh-toolbox job start --name build --timeout 10m -- dotnet build -c Release`
- 例：`dsh-toolbox job start --shell -- "echo hi > out.txt & exit 3"`

#### `job.status`

查看某个后台任务的状态与文件位置

```
dsh-toolbox job status (<jobId>\|--id <jobId>)
```
- 别名：`job.get`
- 例：`dsh-toolbox job status 20260101-120000-000-ab12cd --json`


### log（4 条）

#### `log.append`

往结构化日志追加一条（toolbox-YYYYMMDD.jsonl）

```
dsh-toolbox log append --msg <文本> [--level <级别>] [--key K=V]... [--file <文件>]
```
- 例：`dsh-toolbox log append --msg "构建完成" --level info --key step=build`

#### `log.runs`

查看运行记录 runs.jsonl（命令/参数/退出码/耗时）

```
dsh-toolbox log runs [--limit <n>] [--cmd <子串>] [--exit <码>] [--since <date\|dur>] [--type run\|call]
```
- 例：`dsh-toolbox log runs --limit 10 --json`
- 例：`dsh-toolbox log runs --exit 6`

#### `log.search`

在日志目录内按正则检索（跨天文件，新的优先）

```
dsh-toolbox log search --pattern <正则> [--since <date\|dur>] [--limit <n>] [--ignore-case] [--file <文件>] [--level <级别>]
```
- 例：`dsh-toolbox log search --pattern "E_DENIED" --since 3d --json`

#### `log.tail`

读取当天/指定日志文件最近 N 行（--follow 持续输出）

```
dsh-toolbox log tail [--file <文件>] [--lines <n>] [--since <dur\|date>] [--level <级别>] [--follow]
```
- 例：`dsh-toolbox log tail --lines 20 --json`
- 例：`dsh-toolbox log tail --follow --jsonl`


### maint（5 条）

#### `maint.clean-cache`

清理 DSH 缓存（Code Cache/GPUCache/Cache 等；需先关闭应用）

```
dsh-toolbox maint clean-cache [--dry-run] [--yes] [--all]
```
- 例：`dsh-toolbox maint.clean-cache --dry-run --json`

#### `maint.kill-leftovers`

结束 DSH 残留进程（先优雅后强制，含子进程）

```
dsh-toolbox maint kill-leftovers [--dry-run] [--yes] [--force] [--include-children]
```
- 例：`dsh-toolbox maint.kill-leftovers --dry-run --json`

#### `maint.pull-update`

重新拉取更新：清除 pending 缓存后重启宿主，让它重新检查更新

```
dsh-toolbox maint pull-update [--dry-run] [--yes]
```
- 例：`dsh-toolbox maint.pull-update --dry-run --json`

#### `maint.rebuild-self`

用 build.ps1 重新构建工具箱自身（开发用）

```
dsh-toolbox maint rebuild-self [--out <name>] [--only <mods>]
```
- 例：`dsh-toolbox maint.rebuild-self --json`

#### `maint.restart-host`

重启 DSH 桌面端（结束全部进程后重新拉起）

```
dsh-toolbox maint restart-host [--dry-run] [--yes] [--wait <dur>]
```
- 例：`dsh-toolbox maint.restart-host --yes --json`


### net（6 条）

#### `net.dns`

DNS 解析：A/AAAA/CNAME 记录 + 耗时（DnsQuery + System.Net.Dns 对照）

```
dsh-toolbox net dns --host <h> [--type A\|AAAA\|CNAME]... [--server <ip>]
```
- 别名：`dns`
- 例：`dsh-toolbox net dns --host github.com --json`
- 例：`dsh-toolbox net dns --host localhost --type A`

#### `net.download`

下载到文件：断点续传 + 速率 + 哈希校验

```
dsh-toolbox net download --url <u> --out <file> [--resume] [--overwrite] [--hash sha256] [--expected <hex\|base64>] [--timeout <dur>]
```
- 例：`dsh-toolbox net download --url https://example.com/f.bin --out f.bin --json`

#### `net.http`

HTTP 探测：状态码/耗时/头部/证书主体与有效期（HttpWebRequest）

```
dsh-toolbox net http --url <u> [--method <m>] [--header "K: V"]... [--body <s>\|--body-file <f>] [--timeout <dur>] [--max-body <n>] [--insecure] [--no-redirect]
```
- 例：`dsh-toolbox net http --url https://example.com --json`
- 例：`dsh-toolbox net http --url https://api.github.com --header "Accept: application/json" --method GET`

#### `net.ip`

本机网卡与地址（IPv4/IPv6/网关/DNS/MAC/速率）

```
dsh-toolbox net ip [--all] [--json]
```
- 例：`dsh-toolbox net ip --json`

#### `net.ports`

监听端口列表（TCP LISTEN + UDP，本地地址/端口/PID/进程名）

```
dsh-toolbox net ports [--all] [--state <s>] [--port <n>] [--pid <n>] [--proto tcp\|udp] [--sort port\|pid\|name]
```
- 别名：`ports`
- 例：`dsh-toolbox net ports --json`
- 例：`dsh-toolbox net ports --all --pid 9096`

#### `net.tcp`

TCP 连通性测试：解析 + 逐 IP 连接，返回连接耗时毫秒

```
dsh-toolbox net tcp --host <h> --port <n> [--timeout <dur>] [--all-ips]
```
- 例：`dsh-toolbox net tcp --host 127.0.0.1 --port 19387 --json`
- 例：`dsh-toolbox net tcp --host example.com --port 443 --timeout 5s`


### proc（6 条）

#### `proc.find`

按名称/路径/命令行/端口定位进程，并给出同名同路径多实例（残留）判定

```
dsh-toolbox proc find [--name <s\|glob>] [--path <s\|glob>] [--cmdline-regex <re>] [--port <n>] [--pid <n>]
```
- 例：`dsh-toolbox proc find --name "DeepSeek Harness" --json`
- 例：`dsh-toolbox proc find --port 19387`

#### `proc.kill`

结束进程：先 CloseMainWindow 优雅退出，再 taskkill /T /F（需 --yes）

```
dsh-toolbox proc kill (--id <pid>\|--name <exact\|glob>\|--path <exact\|glob>\|--port <n>) [--tree] [--force] [--grace <dur>] [--timeout <dur>] [--dry-run\|--yes]
```
- 例：`dsh-toolbox proc kill --name notepad --dry-run --json`
- 例：`dsh-toolbox proc kill --name notepad --yes`

#### `proc.list`

列出进程：PID/名称/路径/命令行/启动时间/CPU/内存/是否提权

```
dsh-toolbox proc list [--name <s\|glob>] [--name-regex <re>] [--path <s\|glob>] [--cmdline-regex <re>] [--port <n>] [--pid <n>] [--elevated] [--sort pid\|name\|cpu\|mem\|start\|path] [--reverse] [--top <n>] [--owner]
```
- 别名：`ps`
- 例：`dsh-toolbox proc list --json`
- 例：`dsh-toolbox proc list --name "DeepSeek Harness"`
- 例：`dsh-toolbox proc list --sort mem --top 15 --json`

#### `proc.port`

端口占用归属：本地端口 -> 进程（PID/名称/路径/命令行）

```
dsh-toolbox proc port --port <n> [--all]
```
- 别名：`port`
- 例：`dsh-toolbox proc port --port 19387 --json`
- 例：`dsh-toolbox proc port 19387`

#### `proc.tree`

进程父子树（缩进 + parent 字段）

```
dsh-toolbox proc tree [--root <pid\|name>] [--max-depth <n>]
```
- 例：`dsh-toolbox proc tree --root 9096`
- 例：`dsh-toolbox proc tree --json`

#### `proc.wait`

等待进程退出或出现（--timeout 上限）

```
dsh-toolbox proc wait (--id <pid>\|--name <s\|glob>\|--path <s\|glob>\|--port <n>) [--for exit\|appear] [--interval <dur>] [--timeout <dur>]
```
- 例：`dsh-toolbox proc wait --name notepad --for exit --timeout 30s`
- 例：`dsh-toolbox proc wait --name setup --for appear --timeout 2m --json`


### scan（8 条）

#### `scan.dup`

重复文件检测（size 分组 → 快速指纹 → 全量哈希确认）

```
dsh-toolbox scan dup --path <dir>... [--algo <algo>] [--min-size <size>] [--min-count <n>] [--exclude-dir <name>]... [--hidden] [--parallel <n>] [--max-results <n>] [--relative]
```
- 别名：`scan.dupes`, `scan.duplicate`
- 例：`dsh-toolbox scan dup --path . --json`
- 例：`dsh-toolbox scan dup --path D:\photos --algo sha256 --min-size 1KB --json`

#### `scan.empty-dirs`

空目录查找（含无文件子树统计）

```
dsh-toolbox scan empty-dirs --path <dir> [--depth <n>] [--exclude-dir <name>]... [--hidden] [--max-results <n>] [--relative]
```
- 别名：`scan.emptydirs`
- 例：`dsh-toolbox scan empty-dirs --path C:\data --json`
- 例：`dsh-toolbox scan empty-dirs --path . --exclude-dir .git`

#### `scan.find`

按条件递归扫描文件（glob / 正则 / 大小 / 时间 / 内容）

```
dsh-toolbox scan find --path <dir>... [--include <glob>]... [--exclude <glob>]... [--exclude-dir <name>]... [--ext <ext>]... [--name-regex <re>] [--content-regex <re>] [--min-size <size>] [--max-size <size>] [--newer <dur\|date>] [--older <dur\|date>] [--depth <n>] [--hidden] [--follow] [--max-results <n>] [--parallel <n>] [--sort name\|path\|size\|mtime] [--reverse] [--hash [algo]] [--relative] [--jsonl]
```
- 例：`dsh-toolbox scan find --path . --include "**/*.log" --json`
- 例：`dsh-toolbox scan find --path C:\data --ext txt --ext md --newer 2h --sort mtime --reverse --json`
- 例：`dsh-toolbox scan find --path . --content-regex "TODO|FIXME" --ignore-case --max-results 20 --jsonl`

#### `scan.recent`

最近修改的文件（默认最近 24 小时）

```
dsh-toolbox scan recent --path <dir>... [--newer <dur\|date>] [--max-results <n>] [--sort name\|path\|size\|mtime] [--reverse] [--exclude-dir <name>]... [--hidden] [--relative]
```
- 例：`dsh-toolbox scan recent --path . --json`
- 例：`dsh-toolbox scan recent --path C:\work --newer 2h --max-results 20`

#### `scan.size`

目录体积排行（top-N，含占比百分比）

```
dsh-toolbox scan size --path <dir> [--top <n>] [--parallel <n>] [--exclude-dir <name>]... [--hidden] [--dirs-only] [--relative]
```
- 例：`dsh-toolbox scan size --path C:\data --top 15 --json`
- 例：`dsh-toolbox scan size --path . --exclude-dir node_modules --dirs-only`

#### `scan.snapshot`

生成目录快照清单（path/size/mtime/hash）写入 JSON 文件

```
dsh-toolbox scan snapshot --path <dir> --out <file> [--algo <algo>] [--no-hash] [--exclude-dir <name>]... [--hidden] [--parallel <n>] [--max-results <n>]
```
- 例：`dsh-toolbox scan snapshot --path C:\data --out var\snap.json --json`
- 例：`dsh-toolbox scan snapshot --path . --out var\snap.json --dry-run --json`

#### `scan.tree`

目录树（限深/限条数，含每层大小汇总）

```
dsh-toolbox scan tree --path <dir> [--depth <n>] [--max-results <n>] [--exclude <glob>]... [--exclude-dir <name>]... [--hidden] [--dirs-only] [--relative]
```
- 例：`dsh-toolbox scan tree --path . --depth 2 --max-results 100 --json`
- 例：`dsh-toolbox scan tree --path C:\data --depth 3 --exclude-dir node_modules`

#### `scan.verify`

用快照校验目录（新增/删除/修改/损坏）

```
dsh-toolbox scan verify --snapshot <file> [--path <dir>] [--full] [--tolerance <dur>] [--parallel <n>] [--max-results <n>] [--relative]
```
- 例：`dsh-toolbox scan verify --snapshot var\snap.json --json`
- 例：`dsh-toolbox scan verify --snapshot var\snap.json --path C:\data --full --json`


### sign（4 条）

#### `sign.chain`

只做证书链构建，逐级输出 ChainStatus

```
dsh-toolbox sign chain --file <exe> [--revocation online\|cache\|none] [--no-revocation]
```
- 例：`dsh-toolbox sign chain --file C:\Windows\System32\notepad.exe --json`

#### `sign.hash`

计算 sha1/sha256/sha512 并与 --expected 比对（支持 electron-builder base64 sha512 / latest.yml）

```
dsh-toolbox sign hash --file <f> [--algo sha256\|all]... [--expected <hex\|base64\|sha512-base64>] [--feed <latest.yml>]
```
- 别名：`hash`
- 例：`dsh-toolbox sign hash --file setup.exe --algo all --json`
- 例：`dsh-toolbox sign hash --file setup.exe --feed latest.yml --json`

#### `sign.motw`

查看/移除文件的 Zone.Identifier（Mark of the Web 下载来源标记）

```
dsh-toolbox sign motw --file <f> [--remove --dry-run\|--yes]
```
- 别名：`motw`
- 例：`dsh-toolbox sign motw --file setup.exe --json`

#### `sign.verify`

Authenticode 校验：内嵌/目录签名、状态、签名者、有效期、时间戳、证书链、耗时

```
dsh-toolbox sign verify --file <exe> [--publisher <s>] [--revocation online\|cache\|none] [--no-revocation] [--digest] [--json]
```
- 别名：`authenticode`
- 例：`dsh-toolbox sign verify --file C:\Windows\System32\notepad.exe --json`
- 例：`dsh-toolbox sign verify --file setup.exe --publisher "DeepSeek"`
- 例：`dsh-toolbox sign verify --file setup.exe --no-revocation --json   # 不做吊销检查（快）`


### startup（1 条）

#### `startup.list`

启动项：Run/RunOnce 注册表键 + 启动文件夹

```
dsh-toolbox startup list [--all] [--json]
```
- 例：`dsh-toolbox startup list --json`


### svc（2 条）

#### `svc.control`

启停服务：start|stop|restart（需 --yes；--dry-run 只预览）

```
dsh-toolbox svc control --name <svc> [--name <svc>...] --action start\|stop\|restart [--timeout <dur>] [--dry-run\|--yes]
```
- 例：`dsh-toolbox svc control --name Spooler --action restart --dry-run --json`

#### `svc.list`

服务列表：状态/启动类型/可停止性（ServiceController + 注册表 Start）

```
dsh-toolbox svc list [--name <s\|glob>] [--state all\|running\|stopped] [--startup auto\|manual\|disabled\|boot\|system] [--with-pid]
```
- 别名：`services`
- 例：`dsh-toolbox svc list --state running --json`
- 例：`dsh-toolbox svc list --name WinDefend`

