# dsh-toolbox

**English** | [简体中文](README.zh-CN.md)

[![build](https://github.com/w32394045-dotcom/dsh-toolbox/actions/workflows/build.yml/badge.svg)](https://github.com/w32394045-dotcom/dsh-toolbox/actions/workflows/build.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![platform](https://img.shields.io/badge/platform-Windows%207%2B%20%7C%20x64%20%7C%20ARM64-0078D4)

A **single-file Windows toolbox** that acts as hands and feet for DSH agents: file scanning, hashing,
processes and services, system and network diagnostics, signature and integrity verification, background
jobs and log management — all drivable by an agent through a stable JSON contract.

* Output: `dsh-toolbox.exe` (~543 KB, **depends only on the bundled .NET Framework 4.8**)
* No Python / Node / .NET SDK required, and the tool itself runs without admin rights (only installs ask for elevation); zero startup delay (built to be called often)
* Machine-readable first: one JSON envelope with `--json`, streaming `--jsonl`, strict exit codes, never silently "succeeds"
* Long-lived channel: `serve --stdio` (JSON-RPC 2.0 over NDJSON) with log and job events
* Acceptance: `verify.ps1` — **44/44 green** (envelope / exit codes / clean stdout / verdict semantics / destructive gate / streaming contract / channel)
* **GUI + CLI in one exe**: double-click for the graphical window, pass arguments for the CLI

![dsh-toolbox GUI (English, light theme)](docs/screenshots/gui-en-overview.png)

## Why this exists

It started with a real failure: the DSH desktop app's auto-update broke with
`Command failed: set "PSModulePath=" & chcp 65001 >NUL & powershell.exe ... Get-AuthenticodeSignature ...`.
The cause: electron-updater 6.8.9 hard-codes a **20-second timeout** for the PowerShell signature check,
while this machine (spinning disk + Defender) measurably needs 32–45 s — so the check is killed and the
failure is reported as "invalid signature".

The conclusion was blunt: **agents are missing a reliable, programmable, JSON-speaking tool for a lot of
system-level work.** Hence this toolbox — and `sign.verify` is its flagship command (no timeout, reports
elapsed milliseconds).

## Features

* **68 commands across 20 groups** — see the table below
* **Two-way distribution**: official desktop app or the CLI (`@deepseek-ai/dsh`), built-in installer with
  size + SHA-512 + Authenticode verification, silent install, version check, launch
* **Environment check** (`compat.check`): 13 checks for the known failure modes — signature-check timeouts,
  missing CPU instruction sets, restricted PowerShell, long paths, Defender exclusions, disk space, TLS, VC++ runtime
* **Host operations** (`host.status`, `maint.*`): restart the host to reload plugins, clean caches, kill leftovers,
  re-pull updates, rebuild the toolbox itself
* **Light / dark themes** and **Chinese / English UI**, both following the system by default
* **Elevation on demand**: click the user badge in the sidebar → confirmation → UAC → the app restarts elevated
* **Real progress** for long tasks (install / upgrade / re-pull) via streaming `--jsonl` frames
* **Activity log** pops out into a separate window; selectable, exportable, live
* **Redaction mode** (`DSH_TOOLBOX_REDACT=1`): the UI hides your user name and home paths, so screenshots and
  logs are safe to attach to an issue — the CLI contract is untouched

## Install & dependencies — fully automatic

`install.cli` does not assume the machine has anything. It **checks first, installs what is missing, then
installs the CLI via npm** — all through the CLI, non-interactively:

```powershell
dsh-toolbox install.prereq --json          # 12–13 pre-flight checks (13 with network probes; 12 with `--fast`) + an executable plan
dsh-toolbox install.cli --dry-run --json   # show the whole plan without touching anything
dsh-toolbox install.cli --user-level --yes    # per-user install (no admin needed)
dsh-toolbox elevate.run -- install.cli --yes  # default path: administrator mode (raises UAC)
```

* **No Microsoft Store, no winget, no git required.** Node is installed from the **official zip** into the
  user directory — no MSI/EXE installer, so no UI, no licence pages, no admin needed for that step.
* **Integrity first**: the zip is checked against the official `SHASUMS256.txt`, and the result is verified by
  running `node --version` / `npm --version`. If it cannot be verified, the install fails loudly.
* **Interactive prompts are handled, not hoped for** (`Proc` in `src/Core/Proc.cs`): the child's stdin is closed
  immediately (a prompt gets EOF instead of hanging your automation), tool-specific non-interactive
  environment variables are injected (`npm_config_yes`/`CI=1`, `GIT_TERMINAL_PROMPT=0`/`GIT_ASKPASS=echo`/
  ssh `BatchMode=yes`, winget agreements, MSI `/qn`), the output is scanned for "waiting for input" patterns,
  and on a hit it **retries with non-interactive flags** — both attempts are recorded in `interactiveTrace`.
  The same hardening is available for any command:
  `dsh-toolbox run --non-interactive --tool npm --retry-args "--yes" -- npm install -g x`
* **Runs in administrator (sudo) mode by default** — installs are gated behind elevation
  (`E_ELEVATION_REQUIRED`, exit 4). Use `--user-level` for a per-user install, `--machine` for a system-wide
  one. Checks and `--dry-run` are never gated, so you can always inspect the plan first.

## Download

Grab the prebuilt exe from [**Releases**](https://github.com/w32394045-dotcom/dsh-toolbox/releases/latest) —
no installer, no runtime to install. Just run it.

## Quick start

```powershell
$exe = '.\dsh-toolbox.exe'

& $exe                             # no arguments → the GUI (double-click works too)
& $exe help                        # command overview
& $exe manifest --json             # machine-readable catalog (for agent self-discovery)
& $exe doctor --json               # environment self-check
& $exe host.status --json          # desktop app / CLI / web port / privileges
& $exe compat.check --json         # 13 environment checks
& $exe install.check --json        # latest official build: version / size / SHA-512
& $exe scan.find --path . --ext .cs --newer 7d
& $exe proc.list --sort mem --top 15
& $exe sign.verify --path C:\Windows\System32\notepad.exe --json
& $exe serve --stdio               # JSON-RPC channel for long-lived agent sessions
```

Full integration guide (JSON contract, exit codes, log/job layout, serve protocol, safety rules):
[docs/AGENT-GUIDE.md](docs/AGENT-GUIDE.md) *(Chinese)*.
Complete command catalog, generated from `manifest --json`: [docs/COMMANDS.md](docs/COMMANDS.md) *(Chinese)*.

## The GUI

![light and dark, Chinese and English](docs/screenshots/gui-theme-lang.png)

* Seven pages: Overview / Install & upgrade / Maintenance / Environment check / Jobs & logs / About / **Terminal**
* **Web URL on the Overview page** (`http://127.0.0.1:<port>/`) with one-click *Open web UI* / *Copy URL*
* **Terminal page** — a colour-coded console that runs commands through the same CLI channel, with quick-command
  chips and one-click **Launch dsh CLI**. By design it is available in administrator mode only (same boundary as sudo).
* Same implementation as the CLI — the GUI calls its own CLI as a child process, so **if it works in the
  window, it works over the channel**
* Frameless custom-drawn WinForms, DPI-aware, rounded corners, `TextRenderer` for correct CJK rendering
* Click the activity log to pop it out into a separate window in the same style:

![activity log window](docs/screenshots/gui-log-window.png)

## Commands

| Group | Commands |
|---|---|
| Core | `doctor` `sysinfo` `env` `manifest` `version` `help` |
| Files | `scan.find` `scan.size` `scan.tree` `scan.dup` `scan.snapshot` `scan.verify` `scan.recent` `scan.empty-dirs` |
| Hashing | `hash.file` `hash.dir` `hash.compare` |
| Processes | `proc.list` `proc.tree` `proc.find` `proc.kill` `proc.wait` `proc.port` |
| Services & system | `svc.list` `svc.control` `disk.space` `disk.health` `eventlog.list` `eventlog.query` `installed.list` `defender.status` `startup.list` |
| Network | `net.ports` `net.tcp` `net.http` `net.dns` `net.ip` `net.download` |
| Signature & integrity | `sign.verify` `sign.chain` `sign.hash` `sign.motw` |
| Jobs & logs | `run` `job.start` `job.list` `job.status` `job.output` `job.kill` `log.append` `log.tail` `log.search` `log.runs` |
| Host & maintenance | `host.status` `maint.restart-host` `maint.kill-leftovers` `maint.clean-cache` `maint.pull-update` `maint.rebuild-self` |
| Environment & install | `compat.check` `compat.fix` `install.prereq` `install.node` `install.check` `install.desktop` `install.cli` `install.verify` `elevate.run` |
| Settings | `config.get` `config.set` |
| Channel | `serve --stdio` (JSON-RPC 2.0) |

Everything listed is implemented and verified by real invocations (44/44 acceptance).

## Build from source

```powershell
powershell -File tools\fetch-roslyn.ps1    # download Roslyn (needed once; ~21 MB, ~81 MB extracted, from NuGet)
powershell -File build.ps1                 # → dist\dsh-toolbox.exe
powershell -File verify.ps1                # 44 acceptance checks against the built exe
powershell -File build.ps1 -Out mine.exe   # separate output name for parallel work
```

The compiler is **Roslyn 4.14** (fetched into `.tools\`), the target runtime is the bundled
**.NET Framework 4.8** — that is why the exe is small and needs no SDK. Nothing is installed system-wide.

CI (`.github/workflows/build.yml`) does exactly this on clean `windows-latest` **and `windows-11-arm`** runners for every push to `main` and every pull request:
fetch Roslyn → build → run the 44 acceptance checks → upload the exe as an artifact.

## Architecture

```
src\Program.cs            entry point: global options, dispatch, timeouts, run log, error fallback, help
src\Core\Json.cs          zero-dependency JSON (ordered writer + system parser for reading)
src\Core\Cli.cs           option parsing (--k v / --k=v / -k v, repeatable, no comma splitting)
src\Core\Runtime.cs       Ctx / Output (envelope) / Log (JSONL) / Paths / Registry / ExitCodes / exceptions
src\Core\Fs.cs            glob→regex, safe traversal, size/duration parsing, hashing, parallel map
src\Core\L10n.cs          settings + bilingual string selection (L.T("中文","English"))
src\Commands\*.cs         command modules, each with a single Register()
src\Gui\*.cs              WinForms GUI: MainForm, custom-drawn controls, bridge, log window, redaction
```

The contracts are **frozen**: [docs/CLI-CONTRACT.md](docs/CLI-CONTRACT.md) (external promises) and
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (internal conventions). They are the single source of truth
for implementation and parallel development.

## Settings

```
%LOCALAPPDATA%\dsh-toolbox\settings.json   { "lang": "auto", "theme": "auto" }
```

Both default to `auto` — **language follows the Windows display language, theme follows the Windows
app mode**. Precedence: `--lang` / `--theme` > `DSH_TOOLBOX_LANG` > `settings.json` > system.

```powershell
& $exe config.get --json                     # settings, detected system values, paths
& $exe config.set --lang en-US --json
& $exe config.set --theme dark --json
& $exe config.set --lang auto --json         # back to following the system
```

## Runtime data

```
%LOCALAPPDATA%\dsh-toolbox\
  settings.json                 language / theme
  logs\toolbox-YYYYMMDD.jsonl   structured log
  runs\runs.jsonl               one record per invocation (cmd/argv/exit/ms/cwd/pid)
  runs\<runid>.out.json         large results spilled to disk
  jobs\<jobid>\                 background jobs (cmd.json / stdout.log / stderr.log / status.json)
```

Override with `--home <dir>` or the `DSH_TOOLBOX_HOME` environment variable.

## Safety rules

* Destructive operations are refused by default (exit code 2): preview with `--dry-run`, confirm with `--yes`.
* `--dry-run` guarantees zero side effects.
* `env` redacts secret-looking values unless `--show-secrets` is given.
* Processes are only killed when they match the target exactly (name / executable path) — never by accident.
* Elevation never happens silently: the GUI warns first, then raises UAC only after you confirm.

## Icon

Hand-authored SVG ([icon/app-icon.svg](icon/app-icon.svg), plus a small-size variant and a monochrome one)
→ 12 rendered PNG sizes → a 9-frame `.ico` embedded at build time. See [icon/README.md](icon/README.md).

## Documentation

| Document | Language | Contents |
|---|---|---|
| [docs/AGENT-GUIDE.md](docs/AGENT-GUIDE.md) | Chinese | how an agent should call this tool, safety rules |
| [docs/CLI-CONTRACT.md](docs/CLI-CONTRACT.md) | Chinese | frozen JSON contract, exit codes, verdict semantics |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Chinese | internal conventions and layout |
| [docs/COMMANDS.md](docs/COMMANDS.md) | Chinese | generated command catalog |
| [docs/EVIDENCE.md](docs/EVIDENCE.md) | Chinese | measured evidence and reproductions |

> The deeper documents are currently Chinese-only. The README and the whole UI are bilingual; translating
> the remaining docs is on the list.

## License

[MIT](LICENSE)
