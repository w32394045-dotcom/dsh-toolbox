# dsh-toolbox 架构（冻结版 v1）

> 本文档由 Lead 维护。**任何并行开发者必须先读本文档，不得修改本文件。**
> 契约变更只能由 Lead 在 `docs/CLI-CONTRACT.md` / 本文件中进行，并通过广播通知。

## 1. 目标

产出一个**单文件 Windows exe**：`dist\dsh-toolbox.exe`。

* 依赖：仅系统自带 **.NET Framework 4.8**（本机已装，Win10 默认自带）。无需 .NET SDK、无需 Python/Node、无第三方 DLL。
* 用途：给 DSH agent 当"手和脚" —— 通过 CLI 调用，输出稳定 JSON；并通过 stdio 通道保持长连接。
* 使用者是**程序（agent）**，其次才是人。所以：机器可读优先、退出码语义严格、永不静默成功。

## 2. 技术栈与构建

| 项 | 值 |
|---|---|
| 语言 | C#（`/langversion:latest`，由 Roslyn 4.14 编译） |
| 编译器 | `<repo-root>\.tools\roslyn-4.14.0\tasks\net472\csc.exe` |
| 目标运行时 | .NET Framework 4.8（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`） |
| 构建 | `pwsh -File build.ps1`（自动发现 csc、引用程序集、嵌入 manifest） |
| 产物 | `dist\dsh-toolbox.exe`（约 100–300 KB） |

### 2.1 C# 语言特性白名单（运行时是 .NET FW 4.8，不是 .NET 8！）

**可以用**：字符串插值 `$"..."`、元组 `(a, b)`、模式匹配 `is T x`、`switch` 表达式、
`??=`、局部函数、表达式体成员、目标类型 `new(...)`、`nameof`、`using static`、
`using` 声明、LINQ、`async/await`（`Task`）。

**禁止用**（缺运行时类型或会引入隐藏依赖）：
`record`、`init` 访问器、`required`、`Index`/`Range`（`a[^1]`、`a[1..2]`）、
`Span<T>`/`Memory<T>` 用于 IO 之外的复杂场景、顶层语句、`System.Text.Json`、
`HttpClient` 的高级特性（可用，但 `WebRequest`/`HttpWebRequest` 更稳）。

**禁止引入第三方 NuGet / DLL**。JSON 自己写（见 `Core/Json.cs`）。

## 3. 目录与写入范围（并行开发时**严禁越界写**）

```
DSH-Toolbox\
  build.ps1                     [Lead]
  docs\ARCHITECTURE.md          [Lead]
  docs\CLI-CONTRACT.md          [Lead]
  docs\ROADMAP.md               [feature-scout]
  docs\FEATURES.md              [feature-scout]
  src\app.manifest              [Lead]
  src\Program.cs                [Lead]
  src\Core\Json.cs              [Lead]
  src\Core\Cli.cs               [Lead]
  src\Core\Runtime.cs           [Lead]
  src\Core\Fs.cs                [Lead]
  src\Commands\CoreCommands.cs  [Lead]      doctor / sysinfo / env / manifest
  src\Commands\ScanCommands.cs  [scanner-dev]
  src\Commands\HashCommands.cs  [scanner-dev]
  src\Commands\ProcCommands.cs  [sysdev]
  src\Commands\SysCommands.cs   [sysdev]
  src\Commands\NetCommands.cs   [sysdev]
  src\Commands\SignCommands.cs  [sysdev]
  src\Commands\JobCommands.cs   [agentio-dev]
  src\Commands\LogCommands.cs   [agentio-dev]
  src\Commands\ServeCommand.cs  [agentio-dev]
  src\Core\JobStore.cs          [agentio-dev]
  dist\                         [build.ps1 产出]
  var\                          运行时数据（日志/任务/运行记录）
```

新增文件请落在**自己拥有的文件名前缀**内。需要核心能力时**先读 `Core\*.cs`**，
不要各自实现第二套 glob/哈希/进程枚举。

## 4. 核心 API（已实现，直接调用）

```csharp
// 输出与结果
Ctx.Out.Result("scan.find", data);              // 统一信封（JSON 模式）或自动表格（人类模式）
Ctx.Out.BeginStream("scan.find");              // --jsonl 流式命令**开头**调用：保证 meta 是第一帧
Ctx.Out.Line("人类可读文本");                    // JSON 模式下写到 stderr，保证 stdout 纯 JSON
Ctx.Out.Row(params object[] cols);              // 追加一行表格（人类模式）
Ctx.Out.Warn("...");  Ctx.Out.Truncated = true;

// 数据构造（推荐用这两个，别手搓嵌套）
Json.Obj("count", 3, "items", Json.Arr(Json.Obj("path", p)));
Json.Arr(a, b, c);
Json.Parse(text);        // -> Dictionary<string,object> / object[] / string / 数值

// 参数
Ctx.Args.Get("--pattern");  Ctx.Args.GetAll("--include");  Ctx.Args.Flag("--hidden");
Ctx.Args.GetInt("--depth", 0);  Ctx.Args.GetLong("--max-size", 0);
Ctx.Args.GetSpan("--newer", TimeSpan.Zero);   // 支持 30s/5m/2h/3d/1w
Ctx.Args.Require("--path");                    // 缺失则抛 E_USAGE(exit 2)
Ctx.Args.Positional;                           // List<string>

// 文件系统（不要自己写 glob / 递归）
Fs.Enumerate(root, new FsScanOptions { ... })  // IEnumerable<FsEntry>，安全、可限深、可限数
Glob.ToRegex("**/*.{txt,log}")                 // -> Regex
Fs.HashFile(path, "sha256")                    // -> hex 字符串
Fs.ParseSize("10MB") / Fs.FormatSize(n)

// 错误
throw ToolException.Usage("缺少 --path");
throw ToolException.NotFound("文件不存在: x");
throw ToolException.Denied("拒绝访问");
throw new ToolException("E_LOCKED", "文件被占用", "先结束占用进程", ExitCodes.Error);

// 约定
Ctx.DryRun      // 破坏性命令必须支持：只报告将做什么
Ctx.Cancel      // CancellationToken，长循环里必须检查
```

## 5. 命令注册（每个模块唯一入口）

```csharp
namespace DshToolbox.Commands
{
    public static class ScanCommands
    {
        public static void Register()
        {
            Registry.Add("scan.find", "按条件递归扫描文件", "scan find --path <dir> [--include <glob>]...",
                RunFind, examples: new[] { "scan find --path . --include \"**/*.log\" --json" });
        }
        static int RunFind(Ctx ctx) { ... return ExitCodes.Ok; }
    }
}
```

`Program.cs` 已固定按顺序调用各模块的 `Register()`。**新增模块必须由 Lead 接入**，
或使用已预留的调用点（见 `Program.cs` 顶部注释）。

## 6. 质量要求

1. **一切结果走 `Out.Result`**：命令返回 `int` 退出码，不自己 `Console.WriteLine` JSON。
2. **绝不静默失败**：捕获异常 → `ToolException`，或让顶层兜底并返回非 0。
3. **破坏性操作**（删除/移动/覆盖/杀进程/改注册表/改 ACL）：
   * 必须支持 `--dry-run`，且 `--dry-run` 时**不得产生任何副作用**；
   * 真正执行需要 `--yes`（否则 exit 2 并提示加 `--yes`）。
4. **性能**：扫描类命令必须支持并行（`--parallel`，默认 CPU 核数，上限 32）与
   `--max-results` 提前终止；避免一次性把百万结果读进内存（`--jsonl` 流式）。
5. **可测**：每个命令都要能用 `--json` 跑通并在 `dist\dsh-toolbox.exe manifest --json` 中出现。
6. 中文输出必须能在控制台正常显示（`Program.cs` 已设置 UTF-8）。

## 7. 验收（Lead 负责）

* `build.ps1` 零错误（`-warnaserror` 不强制，但不得有 CS 错误）。
* `manifest --json` 列出全部命令且结构合法。
* 每个命令至少一条真实调用证据（写入 `docs\EVIDENCE.md`）。
* 破坏性命令的 `--dry-run` 无副作用（用文件 mtime/hash 前后对比证明）。
