# dsh-toolbox 图标包

手写 SVG 设计稿 → 逐尺寸光栅化 → 多帧 ICO → 嵌入 exe。

## 文件

| 文件 | 说明 |
|---|---|
| `app-icon.svg` | 主图标（256×256 设计稿，渐变圆角方砖 + 白色工具箱） |
| `app-icon-small.svg` | **小尺寸专用**：去掉提手细节、加粗箱体、圆角收小，16–24px 下仍可辨认 |
| `app-icon-mono.svg` | 单色剪影版（颜色继承 `currentColor`），用于文档/通知等场合 |
| `png/icon-<size>.png` | 12 个尺寸的位图：16 20 24 32 40 48 64 96 128 192 256 512（带透明通道） |
| `app.ico` | 多帧 Windows 图标，9 帧：16 20 24 32 40 48 64 128 256（PNG 压缩帧，Vista+ 标准） |
| `preview.png` | 预览对照图：浅底 + 深底两行，确认两种背景下都清晰 |
| `build_ico.py` | 由 `png/` 组装 `.ico`（纯标准库，无需 Pillow） |
| `render.ps1` | 由 SVG 渲染全部尺寸（Chrome headless，透明背景） |

`src/app.ico` 是构建时实际嵌入 exe 的那一份（`build.ps1` 的 `/win32icon`）。

## 重新生成

```powershell
# 1) 改完 SVG 后重新渲染所有尺寸（Chrome headless，透明背景）
powershell -File icon\render.ps1

# 2) 重新组装 ICO（会自动同步到 src\app.ico）
python icon\build_ico.py icon\app.ico
Copy-Item icon\app.ico src\app.ico -Force

# 3) 重新构建 exe（图标在此时嵌入）
powershell -File build.ps1
```

## 设计说明

- **配色**跟随界面主题主色：`#5C9BFF` → `#2F6FED` → `#7A5AF0` 对角渐变，与 GUI 的强调色一致。
- **造型**：工具箱（盖 + 体 + 提手 + 锁扣），锁扣用底色渐变"挖空"表现，避免在小尺寸下变成糊点。
- **尺寸自适应**：≤24px 使用 `app-icon-small.svg`。这不是简单缩放——小图去掉了提手细描边、把箱体加粗、圆角按比例收小，否则 16px 下细节会糊成一团。
- **双背景可用**：预览图第二行在深色 `#1B1E23` 底上验证（对应深色主题与深色任务栏），边缘与白色箱体对比度足够。

## 使用位置

- exe 图标：`build.ps1` → `/win32icon`（资源管理器、任务栏、Alt+Tab）
- 窗体图标：`MainForm` / `LogWindow` 构造时 `Icon.ExtractAssociatedIcon(exe)` 读取自身
- 界面品牌位：标题栏左上角直接绘制该图标（`Ui.AppIcon`），保证界面内与系统里同一个图标
