# 审查报告：验收台本身（verify.ps1）

> 审查人：Lead（关键路径）　日期：2026-10-01
> 动机：其他四个领域的审查都会引用"36/36 全绿"作为证据，所以**这套台子值不值得信**必须单独查。
> 方法：变异测试（拿"什么都不干、只回合法信封的假 exe"去喂它）+ 逐条核对断言的实质强度 + 覆盖度统计。

## 结论（一句话）

**原来的 36 项里，只有约 10 项具备实质鉴别力；一个空壳程序能通过 26 项。** 已补 8 项实质断言与 1 项正向对照，现在 **44 项**，并且把"不能被假 exe 骗过"变成了可复跑的检查 `tools/verify-mutation.ps1`。

## 发现

### H-1（高）逐命令检查只验形状，不验实质

25 个逐命令检查的断言只有三条：退出码符合期望、stdout 恰好一行、信封含 `ok/cmd/version`。
只要一个程序能回合法信封，`scan.find` 返回**空结果**也照样 PASS。

证据（变异测试）：桩子伪造 manifest（68 条命令"已注册"）+ 一律 `exit 0` + 空 `data`：

```
假 exe 成绩（升级前 36 项）: PASS=26  FAIL=9  SKIP=1
它骗过的项: doctor, sysinfo, env, scan.find, scan.size, scan.tree, scan.recent, scan.dup,
            scan.empty-dirs, hash.file, hash.dir, proc.list, proc.find, proc.port, svc.list,
            disk.space, installed.list, startup.list, net.ports, net.tcp, net.ip,
            log.append, log.tail, log.runs, job.list, dry-run 零副作用
它没骗过: sign.verify×2, hash.compare 结论语义, scan.verify 结论语义, jsonl meta 前置,
          未知命令, 缺必填参数, proc.kill 目标不存在, 破坏性操作未确认
```

即：真正有鉴别力的是那 9-10 项**语义**检查（结论语义、错误码、闸门、jsonl 帧序）。

### H-2（高）"dry-run 零副作用"是自证的

原检查只验证：跑 `--dry-run` 后目标进程仍存活。一个**什么都不做**的程序天然满足这一点。
缺的是**正向对照**——同一目标加 `--yes` 必须真的被杀掉，否则无法区分"闸门有效"与"命令根本没实现"。

### H-3（中）覆盖度：68 条命令里只有 25 条被直接验收

未被任何检查直接触及的 43 条（含若干高风险命令）：
`sign.verify`/`sign.chain`/`sign.hash`/`sign.motw`、`scan.snapshot`/`scan.verify`、`hash.compare`、
`proc.kill`/`proc.tree`/`proc.wait`、`svc.control`、`eventlog.*`、`defender.status`、`disk.health`、
`net.http`/`net.dns`/`net.download`、`job.start/status/output/kill`、`log.search`、
`maint.*`（5 条）、`compat.check`/`compat.fix`、`install.*`（5 条）、`config.*`、`host.status`、
`elevate.run`、`run`、`serve`、`manifest`。
（其中 `sign.verify`/`hash.compare`/`scan.verify`/`serve` 有间接或语义检查覆盖，其余基本没有。）

### H-4（低，但值得记）验收台的**防伪**设计是对的

`$available` 是**从被测 exe 自己的 `manifest --json`** 推导的，所以一个不实现 manifest 的桩子会被
判为"命令未注册"而全部 SKIP（实测 PASS=0 FAIL=1 SKIP=28）。这一点应当保留。

### H-5（低）机器相关性残留

原实现写死端口 19387（只有在装了 DSH 的机器上成立，CI 上必挂），已改为**自备监听端口**（起一个隐藏
PowerShell 持有本地空闲端口作为探测目标，跑完杀掉，起不来则 SKIP）。当前剩余的机器相关性：
`proc.find --name explorer`、`sign.verify` 依赖 Windows 自带签名文件、`disk.space` 阈值——这些在
windows-latest 与 windows-11-arm 上都已实测通过，属可接受范围。

## 已做的修复（本轮）

1. **新增 7 项实质断言**：`hash.file` 的 sha256 必须与 PowerShell 独立计算一致；`scan.find` 必须找到
   ≥20 个 `.cs` 且包含 `Program.cs`；`host.status` 的 webPort 必须为正且 dshHome 非空；`config.get`
   的生效语言必须是合法 locale 且可选语言 ≥2；`compat.check --fast` 必须 ≥10 项且有 blockCount；
   `proc.list` 必须包含当前 PID；`disk.space` 容量字段必须为正数。
2. **新增 1 项正向对照**：`proc.kill --yes` 必须真的把目标杀掉（与 `--dry-run` 存活、无 `--yes` 被拒形成三段闭环）。
3. **新增 `tools/verify-mutation.ps1`**：把"不能被假 exe 骗过"变成退出码可断言的检查（桩子通过项 > 阈值即失败）。
4. 结果：真 exe **44 PASS / 0 FAIL / 0 SKIP**；同一个桩子现在只骗过 26 项（新增的 8 项全部挡住它）。

## 局限与未覆盖

* 实质断言只覆盖 7 条命令；另外 ~36 条命令仍只有形状检查（尤其 `sign.*`、`maint.*`、`install.*`）。
  建议后续按"最高风险优先"逐步补（`sign.verify` 对已知签名文件、`install.verify` 对已知包、
  `maint.kill-leftovers --dry-run` 的零副作用、`job.*` 的生命周期）。
* 变异测试的阈值（30）是经验值；真正的判据是"新增实质断言后桩子分数不上升"。
* 本轮未验证 `serve` 通道（变异测试里用 `-SkipServe` 跳过），它由常规验收覆盖（6 帧合法 JSON）。
