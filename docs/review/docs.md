# 文档与发布面审查（task-5 / docs-auditor）

仓库 `C:\Users\ptfm\DSH-Toolbox` ↔ GitHub `w32394045-dotcom/dsh-toolbox`（public）。只读审查；
未改任何 md、未动 release/topics、未跑构建、未启动 GUI。二进制：`dist\dsh-toolbox.exe`（556032 B）。

## 一、核对通过（"承诺"成立）

| # | 承诺 | 验证方式 → 结果 |
|---|---|---|
| V1 | `version`/`protocol` | `dist\dsh-toolbox.exe version --json` → `0.2.0` / `protocol=1` |
| V2 | 68 条命令 / 20 组（二进制事实） | `manifest --json` → `data.count=68`；`group` 去重 = 20（含 6 条核心命令的空组） |
| V3 | Release latest = v0.2.0 且资产=本地 dist | API `/releases` → asset `size=556032`、`digest=sha256:7710E714…24F53`；本地 `Get-FileHash` = 完全相同 |
| V4 | v0.1.0 已标注被取代 | v0.1.0 title = "v0.1.0 — 首发（已被 v0.2.0 取代）" |
| V5 | 截图/文件链接存在 | 全部 md 的相对链接扫描（含 README×2、icon/README、docs/*.md）→ **0 断链**；`curl` 匿名访问 raw.githubusercontent…/main/* → README、README.zh-CN、LICENSE、icon/app-icon.svg、docs 5 篇、3 张截图全部 **HTTP 200** |
| V6 | 徽章指向真实工作流 | `badge.svg` → 200，内容 `<title>build - passing</title>`；`build.yml` 存在 |
| V7 | CI 双平台 | Actions run #15 → 两个 job：`build-and-verify (windows-latest)`、`(windows-11-arm)` 均 success |
| V8 | README 举例的 flag 真实存在 | `scan.find --path/--ext/--newer`、`proc.list --sort mem --top`、`env --show-secrets`、`run --non-interactive --tool npm --retry-args`、`config.set --lang/--theme`、`install.cli --dry-run/--yes/--user-level/--machine` 均在 manifest usage 中；`sign.verify --path`（README 写法）实测可用（`--file` 的别名） |
| V9 | compat.check 13 项 / install.check 有 SHA-512 | `compat.check --fast --json` → 13 items；`install.check --json` → 含 `sha512`、`size`、`latest` |
| V10 | .NET Framework 4.8 / Roslyn 4.14 / 图标 | compat.check `dotnet` 判据 `Release>=528040`(4.8)+基线文案"Windows 7 SP1 + .NET Framework 4.8"；`fetch-roslyn.ps1` 默认 `4.14.0`；`icon/png` 12 个尺寸、`app.ico` 头 `frames=9` |
| V11 | `--dry-run` 不被闸门拦 | 源码顺序：`InstallCommands.cs:228-234`、`ProvisionCommands.cs:535-541` 先返回 dry-run；实测 `install.cli --dry-run` exit 0 |
| V12 | 环境变量名 | `DSH_TOOLBOX_LANG`(L10n.cs:103)、`DSH_TOOLBOX_HOME`(Runtime.cs:67)、`DSH_TOOLBOX_REDACT`(Redact.cs:26) 存在；`config.get --lang en-US` 输出英文条目 |
| V13 | 不需要商店/winget/git | `install.prereq --json` → `needsStore=false needsGit=false needsNpm=false needsNode=false`（本机其实装了 git/商店，工具仍不依赖） |
| V14 | 中英一致 | h2 15/15、代码块 14/14、表格行 21/21、列表项 30/30、图片 6/6；语言切换链接互指正确（README.md↔README.zh-CN.md） |

## 二、问题清单（严重度 / 证据 / 建议）

**P1 [高] 命令总数写错：66 ≠ 68。** README.md:36 "**66 commands across 20 groups**"、README.zh-CN.md:33 "**66 条命令 / 20 组**"。
证据：`manifest --json` → `count=68`；`docs/COMMANDS.md`（自动生成）头部写 "命令总数 **68**　分组 20 个"；v0.2.0 release body 也写 "68 commands / 20 groups"；仓库 About 却写 "66 条命令"。
另外 README 命令表里 `version` `help` **不在** manifest 的 68 条内（内置命令，`version --json` 可用、`help` 输出 105 行），表内共 70 个名字、按展示需要分了 12 行。
建议：两处改为 "**68 commands across 20 groups**" / "**68 条命令 / 20 组**"，并说明表格是展示分组（`version`/`help` 为内置项）。

**P2 [高] 验收数字过期：36 ≠ 44。** README.md:17、README.md:135、README.zh-CN.md:16、README.zh-CN.md:126 均写 "36/36"；`build.yml:27` 步骤名 "自检与验收（36 项）"；`docs/EVIDENCE.md:20,27` 写 "PASS=36"；v0.2.0 release body 写 "36/36"。
证据：实跑 `powershell -File verify.ps1` → 输出 44 行，末行 **`PASS=44  FAIL=0  SKIP=0`**，exit 0（25 条命令抽查 + 12 条语义/闸门/通道 + 7 条 substantive）。
建议：改为 44/44（或把 harness 里新增项归类后同步更新），并同步 `build.yml` 步骤名与 EVIDENCE.md §2 表。

**P3 [高] "不需要管理员"与 sudo 闸门自相矛盾。** README.md:14 "No Python / Node / .NET SDK / admin rights required"、README.md:62 "no admin needed for that step"（中文 README.zh-CN.md:13、:58 同义）；而 README.md:58 快速开始直接给 `dsh-toolbox install.cli --yes`。
证据：以受限令牌（非管理员）实跑 `runas /trustlevel:0x20000 "… install.cli --yes --json --quiet"` →
`{"error":{"code":"E_ELEVATION_REQUIRED","message":"Installs run in administrator mode by default (more reliable)"}}`，`EXIT=4`。
同理由 `install.node --file <不存在> --yes` 复现同一错误码。更矛盾的是：非管理员下 `install.prereq --json` 给出 `verdict=true, blockCount=0, canAutoInstall=true`，`admin` 项文案 "当前是普通用户 —— 够用（全部装到用户目录）"，`userpath` 修复项也写"不需要管理员"，但下一步就 exit 4（源码 `ProvisionCommands.cs:293-305`）。
建议：README 快速开始改为 `dsh-toolbox elevate.run -- install.cli --yes` 或 `install.cli --user-level --yes`，并把第 13/14 行改为"exe 本身不需要管理员；默认安装走提权闸门，`--user-level` 可免提权"。

**P4 [中] `install.prereq` 项数：12 ≠ 13。** README.md:56 "12 pre-flight checks"、README.zh-CN.md:52 "12 项前置探测"、docs/AGENT-GUIDE.md:126 "12 项前置探测"。
证据：`install.prereq --json` → `count=13`（arch/disk/node/npm/proxy/git/store/powershell/userpath/dsh/admin/net-nodejs/net-npm）；只有加 `--fast` 才 `count=12`（网络两项合并为 1 个 net 项）。
建议：无 `--fast` 的命令写 13，或注明"`--fast` 为 12 项"。

**P5 [中] exe 体积：~490 KB ≠ 543 KB。** README.md:13 / README.zh-CN.md:12。
证据：`Get-Item dist\dsh-toolbox.exe` → 556032 B = **543.0 KB**；v0.2.0 release 资产同为 556032 B（v0.1.0 为 550912 B），两版都不是 490 KB。
建议：改为 "~543 KB"（或 "~0.53 MB"）。

**P6 [中] CI 描述漏了 ARM64 平台。** README.md:149 "on a clean `windows-latest` runner for every push"、README.zh-CN.md:140 同。
证据：`build.yml:4-6` 仅 `on.push.branches:[main]` + PR + dispatch（不是"every push"）；`build.yml:14` 矩阵 `[windows-latest, windows-11-arm]`；Actions run #15 两个 job 均 success；release body 已正确写 "both `windows-latest` and `windows-11-arm`"。
建议：改为"每次推送到 main / PR 时在 `windows-latest` 与 `windows-11-arm` 两个 runner 上".

**P7 [中] `docs/EVIDENCE.md` 是过期且不可复现的"实测记录"。** 行 18 "51 条 / 14 组"、行 26 "已注册命令: 51 个"、行 27 "PASS=36"，§2 分类表合计 36（缺 6→7 号的"破坏性操作正向对照"与 7 条 substantive 项）。
证据：README.md:219 把它作为"measured evidence and reproductions"；当前实际 68 条 / 20 组 / PASS=44。
建议：重跑 harness 并整段替换 §2 输出与分类表；否则 README 不该引用它为可复现实测。

**P8 [中] 仓库 About 描述仍写 66 条。** GitHub API `/repos/…` → `description: "…（66 条命令：…）"`，与 release/manifest 的 68 冲突。（仅报告，未修改。）

**P9 [低] `--theme` 的全局优先级承诺未实现。** README.md:176 / README.zh-CN.md:166 "Precedence: `--lang` / `--theme` > `DSH_TOOLBOX_LANG` > `settings.json` > 系统"。
证据：`config.get --theme dark --json` → `theme=auto, effectiveTheme=light`（未变）；`config.get --theme contrast` 同样无效；`manifest.globalOptions` 无 `--theme`；源码只在 `ConfigCommands.cs:24`（config.set）解析 `--theme`，Program.cs:42 只全局提取 `--lang`。
建议：优先级行删掉 `--theme`，或实现全局 `--theme`。

**P10 [低] 平台徽章与能力不符。** README.md:7 / README.zh-CN.md:7 徽章 "Windows 10+ x64"。
证据：`src/app.manifest` 声明 supportedOS 含 Windows 7（{35138b9a…}）与 Win8/8.1；`compat.check` 基线文案 "Windows 7 SP1 + .NET Framework 4.8"；v0.2.0 release 写 "Windows 7 SP1 → 11"、"ARM64 supported … verified on a real ARM64 CI runner"；`.github/workflows/build.yml:14` 有 ARM64 runner。README 全文未提 ARM64。
建议：徽章改 "Windows 7 SP1+ / ARM64"，并在功能列表补 ARM64 与 OS 基线。

**P11 [低] release notes 的 "9 CPU instruction-set checks" 无依据。** v0.2.0 body 原文如此；`compat.check` 只检测 SSE4.2/AVX/AVX2 三项（`HostCommands.cs:276-284`，`--fast` 输出 "SSE4.2=✗ AVX=✗ AVX2=✗"）。（仅报告。）

**P12 [低] Roslyn 体积：~40 MB ≠ 21 MB（下载）/ 81 MB（展开）。** README.md:140 / README.zh-CN.md:131 写 "~40 MB"；`.gitignore:7` 注释写 "约 80 MB"。
证据：NuGet flat-container HEAD `microsoft.net.compilers.toolset.4.14.0.nupkg` → `Content-Length: 21771766`（≈20.8 MB）；本地 `.tools` 展开后 80.9 MB。建议写 "~21 MB 下载 / ~81 MB 展开"。

**P13 [低] 仓库卫生。** `src/Gui/MainForm.cs.bak` 被提交进仓库（`git ls-files` 可见）——多余备份产物，且 MainForm.cs.bak:901 硬编码个人路径 `C:\Users\ptfm\DSH-Toolbox`。`docs/FEATURES.md:1900,1916,1931` 的示例记录里也出现 `C:\Users\ptfm\…`（示意性，但泄露作者路径）。CI 步骤 `build.yml:55` 会在根目录生成 `manifest.json`，而 `.gitignore` 未覆盖它（`git check-ignore manifest.json` 无输出）。已覆盖项：`dist/*.exe`、`.tools/`、`var/`、`icon/.render/` ✓。凭据扫描（`ghp_`/`github_pat_`/`AKIA`/PRIVATE KEY/password=）在 tracked 文件中 **0 命中**；无 tracked 二进制 exe/dll（最大 tracked 文本为 docs/FEATURES.md 227 KB）。
建议：`git rm src/Gui/MainForm.cs.bak`，`.gitignore` 加 `*.bak`、`manifest.json`；FEATURES.md 示例路径改成 `C:\path\to\…`。

**P14 [提示，供 lead/安全审查] `sign.verify` 对目录签名文件返回 `valid=true`，但 `integrity.digestMatches=false`、`computedDigestHex=null`。**
证据：`sign.verify --path C:\Windows\System32\notepad.exe --json` → `valid:true, status:"Valid", signatureKind:"catalog"`，同时 `integrity:{"pkcs7Verified":true,"digestMatches":false,"digestSkipped":false,"computedDigestHex":null}`。README 把 `sign.verify` 当"招牌命令"，文档层面没说清 catalog 签名下 digest 字段的语义，容易被误读为"摘要不匹配"。

## 三、未覆盖 / 限制

* 未做：截图与当前 GUI 画面对比（gui-auditor 范围）、`docs/FEATURES.md`（227 KB）与 `docs/ROADMAP.md` 逐行核对（仅抽查；ROADMAP.md:27 的 "51 条命令" 属历史叙述）。
* 非管理员场景用 `runas /trustlevel:0x20000` 模拟（受限令牌），非真实标准用户会话；`EXIT=4` 用 `cmd /v:on … echo EXIT=!errorlevel!` 取得。
* release/topics/仓库 About 只读未改（红线）；P8/P11 需仓库管理员手动修正。
