# Markify

<img src="src/Markify/Assets/markify-logo.png" alt="Markify logo" width="96" align="right">

一个用 [Avalonia 12](https://avaloniaui.net) 构建的轻量级 Markdown 编辑/预览客户端：左侧源码编辑，右侧原生控件实时预览，**不使用 WebView**。

> 当前图标为**临时占位设计**，后续会替换为正式品牌 Logo。

![布局](docs/screenshot.png)

## 特性

- **分栏实时预览** — 180ms 防抖，输入即所见；也可切换上下分栏或隐藏预览专注写作
- **双向滚动同步** — 滚动编辑器，预览跟随；滚动预览，编辑器跟随
- **大纲导航** — 左侧标题树，点击同时跳转源码行与预览锚点
- **Markdown 语法高亮** — AvaloniaEdit + TextMate（跟随明暗主题）
- **完整 GFM 渲染** — 表格、任务列表、围栏代码（带语言标签与复制按钮）、脚注、alert 块
- **文件处理** — 打开/保存/另存为，UTF-8(BOM)/UTF-16 编码保持，原子写入防中断损坏
- **未保存保护** — 新建、打开、关闭窗口三处均会确认
- **明暗主题** — 界面、编辑器、预览三者联动
- **拖放打开** / 命令行 `Markify doc.md`
- **状态栏** — 光标行列、字数（中文按字计）、字符数、编码

## 运行

需要 .NET SDK 10 或更高版本。

```powershell
cd src/Markify
dotnet run
```

打开指定文件：

```powershell
dotnet run -- ..\..\samples\demo.md
```

## 快捷键

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl+N` / `Ctrl+O` / `Ctrl+S` / `Ctrl+Shift+S` | 新建 / 打开 / 保存 / 另存为 |
| `Ctrl+B` / `Ctrl+I` / `Ctrl+K` / `` Ctrl+` `` | 加粗 / 斜体 / 链接 / 行内代码 |
| `Ctrl+F` / `Ctrl+H` | 查找 / 查找并替换（编辑器内） |
| `Ctrl+Shift+P` | 显示/隐藏预览 |

## 项目结构

```
src/Markify/
├─ App.axaml                     主题与样式引入（AvaloniaEdit / MarkView 必需）
├─ Assets/
│  ├─ markify-logo.svg           图标源（矢量，可读）
│  ├─ markify-logo.png           1024px 位图（README / 文档用）
│  └─ markify-logo.ico           多尺寸图标（窗口 + exe + 安装包）
├─ ViewModels/
│  └─ MainViewModel.cs           文档状态、防抖预览、大纲、命令
├─ Views/
│  ├─ MainWindow.axaml           布局：大纲 + 编辑器 + 预览 + 状态栏
│  ├─ MainWindow.axaml.cs        滚动同步、大纲导航、文件对话框、拖放
│  └─ HeadingLevelToMarginConverter.cs
├─ Services/
│  ├─ DocumentFileService.cs     编码探测、BOM 保持、原子写入
│  ├─ DocumentFormats.cs         支持的扩展名
│  ├─ IDocumentDialogService.cs  对话框抽象（VM 不依赖窗口）
│  └─ MarkdownOutline.cs         Markdig AST → 标题大纲 + GitHub 锚点
├─ docs/
│  └─ avalonia-markdown-client.md   完整技术调研与选型报告
tools/LogoGen/                    图标生成器（SVG / PNG / ICO 同源输出）
```

## 图标

当前为**临时占位图标**，用于开发期和早期版本。正式 Logo 到位后按下面步骤替换。

图标由 `tools/LogoGen` 从一份几何定义生成三种格式，避免手工导出多份文件后不同步：

```powershell
dotnet run --project tools/LogoGen -- src/Markify/Assets
```

输出 `markify-logo.svg`（矢量源）、`markify-logo.png`（1024px）、`markify-logo.ico`（16/24/32/48/64/128/256 多尺寸）。

**接入位置**（替换时需一并检查）：

| 位置 | 文件 |
| --- | --- |
| 窗口标题栏 / 任务栏 | `Views/MainWindow.axaml` 的 `Icon=` |
| exe 内嵌图标（资源管理器、文件关联） | `Markify.csproj` 的 `<ApplicationIcon>` |
| 安装包与卸载项 | `installer/markify.iss` 的 `SetupIconFile` |

**换成正式 Logo 时**：

1. 直接把新图覆盖上面三个文件（提供 1024px PNG + 含 16/256 的 ICO 即可）；
2. 若不再需要程序生成，删除 `tools/LogoGen` 与 `.github/workflows/aot.yml` 里的 `Verify logo assets` 步骤；
3. 保留 SVG 便于日后修改。

CI 会重新生成图标并与仓库比对，防止改了生成器却忘记提交产物。

## 技术选型

| 层 | 选择 |
| --- | --- |
| UI | Avalonia 12.1.4 |
| 编辑 | AvaloniaEdit 12.0.0 + TextMate 高亮 |
| 渲染 | MarkView.Avalonia 12.4.0（Markdig → 原生 Avalonia 控件） |
| MVVM | CommunityToolkit.Mvvm 8.4.2 |

发布：支持 NativeAOT（已实测，`Markify.exe` 34.2MB）。

选型依据、与备选方案（Markdown.Avalonia、官方 Pro 控件、WebView）的对比，见 [docs/avalonia-markdown-client.md](docs/avalonia-markdown-client.md)。

开发中实际踩的坑与排查手法（滚动同步、AOT、安装包、文件关联等 20+ 条），见 [docs/pitfalls-and-practices.md](docs/pitfalls-and-practices.md)。

## 测试

```powershell
dotnet build tests/Markify.Tests
dotnet run --project tests/Markify.Tests
```

46 个单元测试，覆盖大纲锚点生成、中英文混排字数统计、文件编码/BOM 往返、外部修改监听（防抖与自身写入抑制）。

> 注意：xunit.v3 使用 Microsoft.Testing.Platform 作为宿主。.NET 10 的 `dotnet test` 对 MTP 的集成尚不稳定（会报「运行了零个测试」），因此用 `dotnet run --project` 运行，CI 亦如此。

## 已知限制

- 滚动同步当前为「按比例」，超长文档中源码行与预览内容会有轻微漂移（大纲导航可精确跳转）
- 暂不支持多标签页、导出 PDF/HTML、所见即所得模式
- 替换功能依赖 AvaloniaEdit 的 SearchPanel，仅在源码编辑区生效（预览区暂不支持查找）

## NativeAOT

本项目**已通过 NativeAOT 实测验证**，并通过 CD 流程真正发布（[v0.1.0](https://github.com/iamartinlong/Markify/releases/tag/v0.1.0)）。

| 项目 | 实测 |
| --- | --- |
| `Markify.exe` | 34.21 MB |
| 运行文件合计（不含 pdb） | 52.72 MB / 5 个文件 |
| 发布包（zip） | 19.91 MB |
| AOT 自检 | 9/9 通过 |
| 裁剪 IL 警告 | 0 |
| 运行时内存 | 约 133 MB |

```powershell
dotnet publish src/Markify -c Release -r win-x64 -p:PublishAot=true -p:StripSymbols=true -o artifacts/aot
```

产物是 `Markify.exe` + 4 个原生库（Skia / HarfBuzz / onig / GLES），**不是严格单文件**。PDB 约 200MB，发布包已排除。

### 自检模式

编译通过不代表 AOT 能跑。应用内置 `--selftest`，在真实原生二进制里验证最易被裁剪破坏的 9 条路径（XAML 装载、AvaloniaEdit + TextMate、Markdig 解析、MarkView 控件树、主题模板 ScrollViewer、主题切换、CJK 字数、文件 IO）：

```powershell
artifacts/aot/Markify.exe --selftest --selftest-output artifacts/selftest-aot.txt
```

报告含 `isAot: yes/no`，用于确认跑的是 AOT 产物而非回落到 JIT。升级 Avalonia / MarkView 后建议重跑。

### 发布流程

| 工作流 | 触发 | 作用 |
| --- | --- | --- |
| `aot.yml` | push / PR | 验证 JIT 与 AOT 均可构建、自检通过；编译安装脚本 |
| `release.yml` | `v*` tag | 构建 → 自检 → 打包 zip → 构建安装包 → **实际安装/运行/卸载** → 校验和 → 创建 Release |

发布新版本：

```powershell
git tag -a v0.2.0 -m "Markify v0.2.0"
git push origin v0.2.0
```

详见 [docs/avalonia-markdown-client.md §7.1](docs/avalonia-markdown-client.md)。

## 下载

从 [Releases](https://github.com/iamartinlong/Markify/releases) 下载，两种形式：

| 形式 | 文件 | 体积 | 说明 |
| --- | --- | --- | --- |
| 安装版（推荐） | `Markify-<version>-setup.exe` | 15.0 MB | 开始菜单项 + Markdown 文件关联，卸载时恢复原关联 |
| 便携版 | `Markify-<version>-win-x64.zip` | 19.9 MB | 解压即用，不写注册表 |

均附带 `.sha256` 校验文件。

- 需要 Windows 10/11 x64
- **无需安装 .NET 运行时**（NativeAOT 自包含）
- 安装版默认按用户安装，不弹 UAC；可在安装界面切换为全局安装

### 安装包做了什么

- 注册 `Markify.Markdown` ProgID，并加入 `.md` / `.markdown` 的「打开方式」候选
- 「设为默认」是**可选**任务（默认勾选）；不选则只登记候选，不抢占现有默认程序
- **卸载会把 `.md` 关联还原成安装前的值**，而不是留空
- 安装程序语言：简体中文 / English

```powershell
# 静默安装（按用户、不建桌面图标）
Markify-0.2.0-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NOICONS
```

### 安装包怎么构建

`installer/markify.iss` 由 Inno Setup 6 编译，CI 通过 `choco install innosetup` 获取：

```powershell
dotnet publish src/Markify -c Release -r win-x64 -p:PublishAot=true -p:StripSymbols=true -o artifacts/aot

# 组织 staging（排除 pdb）
# ...

& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
  "/DMyAppVersion=0.2.0" "/DStageDir=<staging 绝对路径>" "installer/markify.iss"
```

发布新版本：

```powershell
git tag -a v0.3.0 -m "Markify v0.3.0"
git push origin v0.3.0
```

## 许可

示例代码可自由使用。依赖库均为 MIT（Markdig 为 BSD-2-Clause）。
