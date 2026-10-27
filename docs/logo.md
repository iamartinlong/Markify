# 图标（Logo）

> **当前是临时占位设计**，用于开发期与早期版本。正式品牌 Logo 到位后按本文 §4 替换。

## 1. 设计说明

### 构成

| 元素 | 说明 |
| --- | --- |
| 底色 | 深靛蓝渐变（`#2B3A67` → `#1B2440`），圆角 22% |
| 主体 | 白色 **M** 折线（Markdown / Markify 首字母） |
| 强调 | 右上角琥珀色圆点 `#FFB33D`，呼应"标记/标注"语义 |

### 为什么这样设计

- **不用字体渲染 M**。字体依赖会带来两个问题：不同系统字形不一致；16px 下细节糊成一团。用折线 + `round join` 描边，几何在任何尺寸都可控。
- **左右竖笔同起同止**（86 → 170）。这是"看起来像 M"的关键——第一版两条竖笔长度不齐，视觉上像两根独立的柱子。
- **中段 V 下探到 140**，与基线留 30 的差，给 M 纵向比例；探得太浅会显得扁平。
- **圆角而非 squircle**。squircle 在大尺寸更精致，但小尺寸抗锯齿容易糊；纯圆角在所有尺寸都稳。
- **≤32px 时给圆点加底色光晕**，否则琥珀会糊进白色笔画里消失。

### 配色依据

与 `App.axaml` 的 chrome 配色同族（深蓝灰），但独立定义——图标不应随应用主题变化而变色，否则在浅色任务栏上可能失去对比度。

## 2. 生成方式

图标由 `tools/LogoGen` **从一份几何定义生成三种格式**：

```powershell
dotnet run --project tools/LogoGen -- src/Markify/Assets
```

| 输出 | 用途 |
| --- | --- |
| `markify-logo.svg` | 矢量源，可读可改 |
| `markify-logo.png` | 1024px，README / 文档 / 商店素材 |
| `markify-logo.ico` | 16/24/32/48/64/128/256，窗口 + exe + 安装包 |

**为什么不手工导出多份文件**：三份文件如果各自维护，改一次设计就要同步导出三次，漏一次就会出现"标题栏是新图标、exe 还是旧的"这种问题。程序生成保证永远一致。

ICO 的编码细节：≥256 用内嵌 PNG（Vista+ 支持，体积小），小尺寸用 BMP/DIB（兼容性最好）。

## 3. 接入位置

三个地方**必须分别设置**，它们互不覆盖：

| 位置 | 文件 | 影响 |
| --- | --- | --- |
| 运行时窗口 | `Views/MainWindow.axaml` 的 `Icon=` | 标题栏、任务栏、Alt+Tab |
| exe 内嵌 | `Markify.csproj` 的 `<ApplicationIcon>` | 资源管理器、文件关联图标 |
| 安装包 | `installer/markify.iss` 的 `SetupIconFile` | setup.exe 与卸载项 |

> **易漏点**：`Window.Icon` 只影响运行时窗口，**不会写入 exe**。本项目最初就缺 `<ApplicationIcon>`，导致 exe 用的是 .NET 默认图标，而窗口标题栏是 Avalonia logo——两者不一致。

`installer/markify.iss` 里的 `UninstallDisplayIcon` 与 ProgID 的 `DefaultIcon` 都指向 `{app}\Markify.exe`，因此只要 exe 内嵌图标正确，文件关联图标自动跟着对。

## 4. 换正式 Logo 的步骤

### 方案 A：直接替换图片（推荐，最简单）

1. 准备两份素材：
   - **PNG**，1024×1024，透明背景
   - **ICO**，至少包含 16×16 与 256×256
2. 覆盖 `src/Markify/Assets/` 下的 `markify-logo.png` / `.ico` / `.svg`
3. 删除 `tools/LogoGen`，并移除 `.github/workflows/aot.yml` 里的 `Verify logo assets are up to date` 步骤（否则 CI 会因产物与生成器不符而失败）
4. 更新本文与 README 的"临时占位"说明

### 方案 B：改生成器（仍想程序化生成）

修改 `tools/LogoGen/Program.cs` 里的几何常量或配色，然后重新生成：

```powershell
dotnet run --project tools/LogoGen -- src/Markify/Assets
```

**注意**：`Render()`（位图）和 `BuildSvg()`（矢量）是两处独立的几何定义，改一处必须同步改另一处。CI 只校验"产物与生成器一致"，不能发现"位图与 SVG 构图不一致"——这是当前设计的一个已知限制。

## 5. CI 校验

`.github/workflows/aot.yml` 的 `Verify logo assets are up to date` 步骤会：

1. 在临时目录重新生成三份文件；
2. 与仓库中的文件做 SHA-256 比对；
3. 不一致则失败，并提示重新生成的命令。

**目的**：防止有人改了 `Program.cs` 却忘记提交重新生成的产物，导致源码与图片不同步。

该检查经过负向测试验证（人为改一个字节能被正确检出），不是摆设。

## 6. 已知限制

- **几何定义有两处**（`Render` 与 `BuildSvg`），需要手工保持一致。若要根治，可让 SVG 也由 SkiaSharp 的 `SKPicture` 导出，但会牺牲可读性——当前选择保留手写 SVG 的可读性，用注释提醒同步。
- **SVG 没有小尺寸的圆点光晕**。位图在 ≤32px 时给琥珀圆点加了底色光晕（否则会糊进白笔画），SVG 未加——因为 SVG 通常用于 48px 以上的场景。**这是位图与 SVG 唯一的有意差异**，不是遗漏。
- **没有 512/1024 的 ICO 条目**。Windows 最大用到 256，如需应用商店素材请用 PNG。
- **未制作浅色版**。如果将来上深色任务栏主题，白色 M 与深蓝底仍有足够对比度（≥ 4.5:1），但正式 Logo 建议同时验证浅色/深色两种背景。
