# 待办与路线图

> 最后更新：2026-10-28（v0.5.1 发布后）
>
> 这份文档记录**尚未实现的想法**（§2，均未排期）与**接手所需现状**（§1）。已完成的功能见 [工作日志](worklog.md)，踩过的坑见 [pitfalls-and-practices.md](pitfalls-and-practices.md)。

## 怎么用这份文档

- **接手项目时**：先读 §1 现状，再读 §2 路线图——但**先确认要不要开工**，§2 里的条目都未排期。
- 每完成一项，把条目移到 [worklog.md](worklog.md)，**不要直接删除**——保留决策记录对后来者有参考价值。
- 优先级定义：**P0** 阻断使用 / **P1** 明显影响日常 / **P2** 体验优化 / **P3** 锦上添花。
  优先级只表示**如果要做，先做哪个**，不代表已经排期。

---

## 1. 项目现状（交接必读）

| 项 | 值 |
| --- | --- |
| 最新版本 | **v0.5.1**（纯修复版） |
| 源码 | 16 个 `.cs` 文件（不含 `obj/`），约 3100 行 |
| 测试 | **86 个测试方法 / 105 个用例**（xunit.v3），全部通过；时序相关用例已改为轮询，连跑 10 次稳定 |
| 自检 | **10 项**，JIT 与 NativeAOT 均通过 |
| 发布物 | 便携 zip（20 MB）+ 安装包（15 MB），均含 SHA-256 |
| 技术栈 | .NET 10 / Avalonia 12.1.4 / AvaloniaEdit 12.0.0 / MarkView 12.4.0 |
| 构建警告 | 0 |

### 已具备的能力

分栏实时预览、双向滚动同步、大纲导航、多标签页、文件夹目录树、标签右键菜单、查找替换、外部修改检测、导出 HTML、导出 PDF、明暗主题、NativeAOT 发布、Inno Setup 安装包（含文件关联）。

### 关键约束（改动前必须知道）

1. **三个 `StyleInclude` 缺一不可**（`App.axaml`）。缺 MarkView 的 `MarkdownTheme.axaml` 时预览**能渲染但不能滚动**，没有报错。
2. **所有标签共用一个 `TextEditor`**，切换时替换 `TextDocument`。这保证了撤销栈隔离，也意味着标签间共享语法高亮配置与查找面板状态。
3. **AOT 是发布路径**。任何引入反射、动态代码的新依赖都必须重跑 `--selftest`，单元测试在 JIT 下跑，证明不了 AOT 可用。
4. **`ContextMenu` 不在视觉树里**，相对绑定与 `PlacementTarget` 都拿不到目标（详见 pitfalls B8）。

---

## 2. 路线图（均未排期）

> **状态：全部为待评估的想法，当前没有进行中的开发。**
> v0.5.1 之后本项目处于功能完整、仅维护的状态。下列条目保留是为了记录"还差什么"，
> 不是承诺要做。开工前请先确认优先级是否仍成立——当时的判断可能已经过时。

### P1 — 明显影响日常使用

> 新增 **P1-0：UI 视觉与交互优化**，详见 [ui-optimization.md](ui-optimization.md)；
> 风格选型讨论见 [vscode-style-discussion.md](vscode-style-discussion.md)。
> 这条比下面的 P1-1~P1-3 更显眼——"界面简陋"是用户打开就看到的，而文件夹记忆只是操作繁琐。

#### P1-0. UI 视觉与交互优化（**方向已定，未实施**）

- **现状**：深色主题存在三处视觉断裂（标题栏仍白、菜单栏纯黑、编辑器与预览两套深色）；次要文字靠 `Opacity` 压暗，浅色主题下 2 处对比度不达标；工具栏 11 个按钮无图标无悬停态；目录树用彩色 emoji 与 Fluent 线性风格冲突。
- **根因**：`App.axaml` 只有 5 个语义色键，缺少层级色 / 次级文字色 / 状态色，只好用 `Opacity`、边框线和硬编码色值（`#40FF0000`）替代。
- **风格已定**（2026-10-28）：**"精致 Markdown 工具"而非 VS Code 插件**——采用 VS Code 的骨架（两层底色 + 细线分区 + 半透明状态）与自有强调色，**不做活动栏、不做 IDE 式布局**。定稿令牌与实测值见 [vscode-style-discussion.md](vscode-style-discussion.md) §7。
- **方案**：[ui-optimization.md](ui-optimization.md) 给出问题清单（含实测数值）、实施计划与不建议做的事。
- **成本**：视觉层约 1 天（修断裂 + 建令牌），命令面板约 1 天，标签栏紧凑化约 0.5 天；合计约 2.5 天，分两期。
- **注意**：不要动 MarkView 包内的 Markdown 样式；不要引入图标字体包（AOT 风险）；**次级文字不可用在交互行上**（会与半透明状态色冲突，详见讨论稿 §7）；改前先截基线图（项目无 UI 自动化测试）。

#### P1-1. 打开文件夹后不记忆，重启需重新打开

- **现状**：每次启动都要 `Ctrl+Shift+O` 重选文件夹。
- **建议**：在退出时把 `FolderPath` 和最近打开的文件列表写入本地配置，启动时恢复。
- **落点**：新增 `Services/AppSettingsService.cs`，配置放 `%LOCALAPPDATA%\Markify\settings.json`。
- **注意**：配置损坏不能导致启动失败，读取失败就当作空配置。

#### P1-2. 单击目录树文件即打开，缺少"预览态"

- **现状**：单击直接开标签，浏览大量文件时容易堆积标签。
- **建议**：引入"临时标签"概念（斜体显示），单击复用同一个临时标签，双击转为常驻。与 VS Code 一致。
- **落点**：`DocumentViewModel` 加 `IsPreview`，`MainViewModel.OpenPathAsync` 增加 `preview: bool` 参数。
- **成本**：约 1 天，涉及标签状态机，改动面较大。

#### P1-3. 没有"最近打开的文件"

- **现状**：文件菜单只有"打开…"，需要从对话框里翻目录。
- **建议**：文件菜单加"最近打开"子菜单，保留最近 10 个；同时支持把文件拖到任务栏图标打开（Windows JumpList，可选）。
- **依赖**：与 P1-1 共用配置存储，建议一起做。

### P2 — 体验优化

#### P2-1. 精确滚动同步

- **现状**：按比例同步，长文档中源码行与预览内容会逐渐漂移（代码块高度差异大时明显）。
- **建议**：解析后建立「源码行 → 预览 Y 坐标」分段映射。需要遍历预览可视树里带 `markdown-*` class 的控件，与 Markdig AST 的 `Block.Line` 按顺序配对。
- **成本**：3–5 天，是本项目剩余项里最难的。
- **备注**：目前靠大纲锚点跳转补偿，日常够用，因此不是 P1。

#### P2-2. 导出时不做代码语法高亮

- **现状**：导出的 HTML / PDF 里代码块是朴素等宽样式，而预览有 TextMate 着色。
- **原因**：Highlight.js 需要 CDN（离线失效）或内嵌约 100KB 脚本。
- **建议**：如果要做，选择**内嵌精简版**（只打包常用语言）而不是 CDN——导出文件的卖点就是离线可用。
- **成本**：1–2 天（含体积验证）。

#### P2-3. 导出 HTML 的 `<details>` 不支持嵌套

- **原因**：Markdig 的 HTML 渲染器不认 MarkView 的 `DetailsBlock`，当前用正则抽取还原，无法处理嵌套。
- **建议**：改为遍历 AST 自行生成 HTML，而不是正则替换。
- **成本**：半天。触发概率低（嵌套 details 少见），优先级不高。

#### P2-4. 预览区不支持查找

- **现状**：`Ctrl+F` 只作用于源码编辑器。
- **建议**：MarkView 提供 `MarkdownViewer.Find(...)`，可接到同一个查找框。
- **成本**：半天。

### P3 — 锦上添花

#### P3-1. 真实 Logo

- **现状**：当前是占位设计（深靛蓝圆角方块 + 白色 M + 琥珀圆点）。
- **替换方法**：见 [logo.md](logo.md)，覆盖 `src/Markify/Assets/` 下三个文件即可，然后删除 `tools/LogoGen` 与 CI 里的 `Verify logo assets` 步骤。

#### P3-2. WYSIWYG 模式

- **评估**：需要 Avalonia Pro 的 `RichTextEditor` + `Avalonia.Controls.Markdown`（商业授权）。
- **不建议自研**：那等于写一个 Markdown 感知的富文本编辑器，成本远超收益。
- 若确实要做，**新建分支**试验，不要动主干的分栏模式。

#### P3-3. 跨平台验证

- **现状**：只在 `win-x64` 验证过，CI 也只跑 Windows。
- **建议**：如需 macOS / Linux，`release.yml` 加 `matrix`（`osx-arm64` / `linux-x64`），AOT 需各自验证。
- **注意**：`PdfExporter` 依赖 Edge/Chrome，Linux 上通常没有预装，需要探测 `google-chrome` / `chromium`。

#### P3-4. 多语言界面

- **现状**：界面文案硬编码中文。
- **建议**：如要做，用 `.resx` 或 Avalonia 的资源字典；`Settings` 里存语言偏好。

---

## 3. 已知技术债

> 与 §2 不同：这里记的是**已经存在**的不足，不是想法。若哪天要动相关代码，这些是绕不开的前提。

| 项 | 说明 | 影响 |
| --- | --- | --- |
| `MarkdownOutline` 用 `UseAdvancedExtensions` | 与预览的 `UseGitHubExtensions` 不完全一致，某些扩展语法下大纲行号可能与预览锚点不符 | 低，实测常见语法无问题 |
| `MarkdownHtmlExporter` 的手写 SVG/CSS 与位图双份定义 | 位图和 SVG 的几何是两处（`Render` 与 `BuildSvg`），改一处要手工同步 | 低，CI 能校验产物与生成器一致，但校验不了"位图与 SVG 构图一致" |
| 主窗口代码后置较大 | `MainWindow.axaml.cs` 约 1000 行，包含滚动同步、文件树、菜单、对话框 | 中，继续加功能前建议拆分 |
| 无 UI 自动化测试 | 交互靠人工验证，历史上多次因工具缺陷误判（见 pitfalls F9/F10） | 中，UI 回归全靠自觉 |
| `FileChangeWatcher` 的 500ms 宽限期 | 抑制自身写入用的是时间戳窗口（v0.5.1 修复）。保存后 500ms 内到达的外部修改仍会被当作自身写入忽略 | 低，但极快的"保存即被格式化"场景可能漏报。若要彻底解决，需按写入内容/大小比对而非纯时间判断 |

---

## 4. 接手检查清单

第一次接手时按顺序跑一遍，确认环境可用：

```powershell
# 1. 构建（应 0 警告）
dotnet build src/Markify -c Release

# 2. 单元测试（应 105 个用例全部通过）
dotnet build tests/Markify.Tests
dotnet run --project tests/Markify.Tests

# 3. 自检（应 10 通过）
src/Markify/bin/Release/net10.0/Markify.exe --selftest --selftest-output selftest.txt
Get-Content selftest.txt

# 4. 安装包脚本能编译（需要 Inno Setup 6）
#    见 .github/workflows/aot.yml 的 installer-script job
```

发布流程（详见 README）：

```powershell
git tag -a v0.6.0 -m "..."
git push origin v0.6.0      # 触发 release.yml：构建 → 自检 → 打包 → 建 Release
```

---

## 5. 相关文档

| 文档 | 内容 |
| --- | --- |
| [worklog.md](worklog.md) | 已完成工作与决策记录（按版本） |
| [ui-optimization.md](ui-optimization.md) | **UI 优化方案**（问题实测 + 设计令牌 + 三期计划，未实施） |
| [vscode-style-discussion.md](vscode-style-discussion.md) | **是否做成 VS Code 风格**（三层拆解 + 空间账 + 决策建议，讨论稿） |
| [pitfalls-and-practices.md](pitfalls-and-practices.md) | 40 条踩坑与排查手法，**改代码前建议先翻** |
| [avalonia-markdown-client.md](avalonia-markdown-client.md) | 选型依据、架构、四种渲染方案对比 |
| [logo.md](logo.md) | 图标设计与替换步骤 |
