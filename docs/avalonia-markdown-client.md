# 用 Avalonia 开发轻量级 Markdown 编辑/预览客户端：实现方案调研

> 结论先行：**Avalonia 12 + AvaloniaEdit（编辑）+ MarkView.Avalonia（预览）+ Markdig（解析）** 是目前「原生控件、无 WebView、体积小、启动快」路线里最省事且仍在活跃维护的组合。本仓库 `src/Markify` 已按此方案实现出一个可运行的分栏编辑器原型，本文记录选型依据、架构、关键实现细节与踩过的坑。

---

## 1. 需求与约束

| 维度 | 目标 |
| --- | --- |
| 客户端形态 | 桌面（Windows 优先，兼容 macOS/Linux） |
| 编辑方式 | 源码编辑 + 实时预览（分栏），而非 WYSIWYG |
| 渲染方式 | 原生控件优先，不接受内嵌浏览器（体积、启动、依赖） |
| 轻量 | 单文件可分发，内存占用低，冷启动 < 1s |
| Markdown | CommonMark + GFM（表格、任务列表、围栏代码、删除线、自动链接） |
| 交互 | 打开/保存/另存为、编码保持、未保存提示、滚动同步、大纲导航、明暗主题 |

「轻量」在这里的定义很具体：**不引入 WebView2 / CEF，不打包 Node/Chromium，发布即单个自包含可执行文件**。这个约束基本决定了渲染层的选型。

---

## 2. 技术栈选型

### 2.1 版本基线（2026-10 实测）

本机环境：.NET SDK 10.0.400，目标框架 `net10.0`，Avalonia **12.1.4**。

| 包 | 版本 | 许可 | 作用 |
| --- | --- | --- | --- |
| `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` | 12.1.4 | MIT | UI 框架 |
| `Avalonia.AvaloniaEdit` | 12.0.0 | MIT | 文本编辑器（AvalonEdit 的 Avalonia 移植） |
| `AvaloniaEdit.TextMate` + `TextMateSharp.Grammars` | 12.0.0 / 2.0.4 | MIT | Markdown 源码语法高亮 |
| `MarkView.Avalonia` | 12.4.0 | MIT | Markdown → 原生控件渲染 |
| `MarkView.Avalonia.SyntaxHighlighting` | 12.4.0 | MIT | 预览区代码块高亮（可选） |
| `Markdig` | 1.5.0（传递依赖） | BSD-2 | Markdown 解析器 |
| `CommunityToolkit.Mvvm` | 8.4.2 | MIT | `[ObservableProperty]` / `[RelayCommand]` 源生成器 |

> ⚠️ 版本陷阱：`Avalonia.AvaloniaEdit` 的 NuGet 包名是 **`Avalonia.AvaloniaEdit`**，不是历史上有过的 `AvaloniaEdit`（后者停在 0.10.12，只支持 Avalonia 0.10）。引用错包会在运行时因程序集版本不匹配炸掉。
>
> 另一个陷阱：`Avalonia.Diagnostics` 官方包也停在 11.3.x；Avalonia 12 模板已改用 `AvaloniaUI.DiagnosticsSupport`。

### 2.2 渲染方案对比

这是整个选型的核心分歧点。四条路线的实测取舍：

| 方案 | 渲染 | 优点 | 缺点 | 结论 |
| --- | --- | --- | --- | --- |
| **MarkView.Avalonia** | 原生控件（Markdig → Avalonia 控件树） | MIT；GFM 完整；跨块文本选择；表格/任务列表/代码块工具栏；流式渲染；懒加载；内置主题与大量资源键 | 较新，Avalonia 12 专用 | ✅ **采用** |
| **Markdown.Avalonia** | 原生控件（MdXaml 移植） | 老牌（418★），API 简单，Wiki 完善；`Tight` 变体不含 AvaloniaEdit 依赖 | 强绑 AvaloniaEdit 版本；项目节奏偏慢；定制能力弱于前者 | 备选 |
| **Avalonia.Controls.Markdown**（官方 `Markdown` 控件） | 原生控件（FlowDocument） | 官方维护，与 `RichTextEditor` 共享文档模型，可做 WYSIWYG 延伸；支持流式 | **Avalonia Pro 商业授权**，需 license key | 有预算时首选 |
| **WebView + JS 渲染器**（markdown-it / marked + highlight.js） | 内嵌浏览器 | 渲染保真度最高（与 VS Code、Typora 同级），生态最全 | 体积 +100MB 级；冷启动慢；跨平台 WebView 行为差异；IPC 复杂度 | ❌ 与「轻量」冲突 |

**为什么没选 WYSIWYG（类 Typora）**：所见即所得要求在编辑器内部维护一份可编辑的富文本文档模型，并且要把「Markdown 源码偏移 ↔ 富文本节点」双向映射做好——这基本等于自己写一个 Markdown 感知的富文本编辑器。Avalonia 12 的 `RichTextEditor` 提供了文档模型底座，但仍在 Pro 层，且改造成本远高于分栏方案。**分栏方案能以 1/10 的成本拿到 90% 的日常价值**，且架构上留了升级口（见 §6）。

---

## 3. 整体架构

```
┌─────────────────────────────────────────────────────────────┐
│ Views/MainWindow.axaml                                      │
│  Menu / Toolbar / StatusBar                                 │
│  ┌──────────┬──────────────────────┬─────────────────────┐  │
│  │ Outline  │  TextEditor          │  MarkdownViewer     │  │
│  │ (ListBox)│  (AvaloniaEdit)      │  (MarkView)         │  │
│  │          │  TextMate 高亮       │  原生控件渲染        │  │
│  └──────────┴──────────────────────┴─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
        │ 事件/命令                    │ 绑定
        ▼                              ▼
┌───────────────────────────┐   ┌────────────────────────────┐
│ MainViewModel             │   │ MarkdownOutline (Markdig)  │
│ - SourceText / Preview    │   │ 源码行号 → 标题 → 锚点 slug │
│ - IsModified / FilePath   │   └────────────────────────────┘
│ - 防抖预览 (180ms)         │
│ - 大纲集合 / 光标状态       │   ┌────────────────────────────┐
└───────────────────────────┘   │ DocumentFileService        │
        │                       │ 编码探测 / BOM / 原子写入    │
        ▼                       └────────────────────────────┘
┌───────────────────────────┐
│ IDocumentDialogService    │  ← 由 MainWindow 实现，VM 不依赖窗口
│ 文件选择 / 保存 / 放弃确认  │
└───────────────────────────┘
```

### 3.1 分层职责

- **View（`MainWindow`）**：只做三件 View 该做的事——控件装配、像素级操作（滚动同步、光标定位）、平台 API（文件对话框、拖放）。
- **ViewModel**：文档状态机（脏标记、编码、统计）、防抖调度、大纲刷新、命令。
- **Services**：`DocumentFileService`（IO 与编码）、`MarkdownOutline`（Markdown AST → 大纲）、`DocumentFormats`（扩展名）。
- **不引入 DI 容器**：轻量客户端里 `new` 出来的依赖比容器注册表更易读。真需要时再换 `Microsoft.Extensions.DependencyInjection`（模板已可选）。

### 3.2 数据流（编辑 → 预览）

```
用户输入
  → TextEditor.TextChanged
  → vm.OnEditorTextChanged(text)          // 立刻更新 SourceText / 脏标记 / 字数
  → DispatcherTimer 重置 (180ms)
  → 定时器 Tick → PreviewMarkdown = SourceText   (仅当内容真的变了)
  → MarkdownViewer.Markdown 绑定触发整篇重渲染
  → RefreshOutline()   // 大纲变化时才重建集合
```

**为什么要防抖**：`MarkdownViewer.Markdown` 的 setter 会重解析 + 重建整棵控件树，并**把滚动位置归零**。对 30KB 的文档，逐键重渲染会造成明显卡顿和视觉跳动。180ms 是实测的主观流畅与「跟手感」的平衡点（可按文档长度自适应）。

---

## 4. 关键实现细节（含踩坑）

### 4.1 AvaloniaEdit 初始化

```csharp
// App.axaml —— 必须包含，否则编辑器没有模板，什么都不显示
<StyleInclude Source="avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml" />
```

```csharp
// MainWindow.axaml.cs
_registryOptions = new RegistryOptions(ThemeName.LightPlus);
_textMate = Editor.InstallTextMate(_registryOptions);

var language = _registryOptions.GetLanguageByExtension(".md");
_textMate.SetGrammar(_registryOptions.GetScopeByLanguageId(language.Id));
```

- 主题切换只需 `_textMate.SetTheme(...)` + `TextView.Redraw()`，无需重建编辑器。
- 设置 `Editor.Text` 会**清空撤销栈**（`Text` setter 内部调用 `UndoStack.ClearAll()`），且把光标置到 0。**打开文件必须走这条路径**，否则「新文档继承了上一个文档的撤销历史」会产生诡异的撤销行为。打开后还要 `UndoStack.MarkAsOriginalFile()`，否则 `IsModified` 立刻变脏。

### 4.2 滚动同步（本项目最大的坑）

三种导航同步方式，优先级递增：

| 方式 | 实现 | 表现 | 适用 |
| --- | --- | --- | --- |
| **按比例** | `offset / (extent - viewport)` | 长文档中源码行与预览内容会逐渐漂移（代码块高度差异） | 通用兜底 |
| **按标题锚点** | 源码行 → 最近标题 → `ScrollToAnchor(slug)` | 语义准确，但只在标题处跳，行内有阶梯感 | 长文档 |
| **双向精确映射** | 维护每个 Markdown block 在两侧的 Y 坐标表 | 完全同步 | 需要侵入渲染层，成本高 |

本项目采用「比例同步 + 大纲点击精确跳转」的组合。

**坑 1：不能用 `TextView.DocumentHeight` 算比例。**

`TextView.DocumentHeight` 只覆盖**已构建的可见行**（本机实测 698px，而真实可滚动高度 1377px），用它算比例恒为 0，表现为「编辑器滚了、预览不动」。正确来源是 `TextEditor` 暴露的外层 ScrollViewer：

```csharp
var ratio = Editor.VerticalOffset / (Editor.ExtentHeight - Editor.ViewportHeight);
```

**坑 2：反向同步（预览 → 编辑器）不能设 `ScrollViewer.Offset`。**

AvaloniaEdit 的 `TextView` 实现了 `ILogicalScrollable`，是**逻辑滚动源**。直接给外层 `ScrollViewer.Offset` 赋值会在下一次布局时被 TextView 的偏移覆盖回去（实测赋值 449 后读回 0）。必须走接口：

```csharp
var textView = Editor.TextArea.TextView;
((IScrollable)textView).Offset = new Vector(textView.ScrollOffset.X, target);
```

**坑 3：预览区的 `ScrollViewer` 要延迟挂接。**

`MarkdownViewer` 的 ScrollViewer（`PART_ScrollViewer`）在自己的 ControlTemplate 里，`Window` 构造函数执行时还取不到。稳妥做法是在 `LayoutUpdated` 里「取到且未挂接过就挂一次」：

```csharp
private void HookPreviewScroll()
{
    var preview = PreviewScroll;
    if (preview is null || ReferenceEquals(_hookedPreview, preview)) return;
    if (_hookedPreview is not null) _hookedPreview.ScrollChanged -= OnPreviewScrollChanged;
    _hookedPreview = preview;
    preview.ScrollChanged += OnPreviewScrollChanged;
}
```

**坑 4：重渲染后要恢复滚动位置。**

`Markdown` 赋值会把预览滚回顶部。在 `PreviewMarkdown` 变更时置 `_restoringPreviewScroll = true`，下一次 `LayoutUpdated`（此时 extent 已算好）按记录的编辑器比例恢复，并清标记。

**坑 5：防重入。**

双向同步必须用一个 `_syncingScroll` 布尔量夹住写操作，否则 A 触发 B、B 又触发 A，出现滚动抖动/漂移。

### 4.3 大纲与锚点一致性

大纲点击要同时跳编辑器和预览，因此两侧的「标题标识」必须一致。GitHub 的锚点规则：

1. 转小写；
2. 空格 → `-`；
3. 移除其余标点（保留字母、数字、`-`、`_`，CJK 字符保留）；
4. 同名标题按出现顺序追加 `-1`、`-2`。

`MarkdownOutline` 用 Markdig 解析出 `HeadingBlock`（拿到**精确行号** `heading.Line + 1` 和标题纯文本），自行实现上述 slug 规则，从而与 MarkView 的 `ScrollToAnchor(slug)` 对齐。这样点击「第 5 节」时，编辑器跳到源码第 N 行，预览跳到对应标题，两边同屏。

同时 `RefreshOutline` 做了**集合差异短路**：标题数量、文本、层级、行号都没变就不重建 `ObservableCollection`，避免每次输入都刷新侧栏、丢失选中项。

### 4.4 编码与文件写入

| 场景 | 处理 |
| --- | --- |
| UTF-8 with BOM | 识别并**保留 BOM**（很多中文 Windows 工具依赖它） |
| UTF-8 no BOM | 默认，读/写都不写 BOM |
| UTF-16 LE / BE | 按 BOM 识别，保持原编码回写 |
| 换行符 | 原样保留（不做 LF/CRLF 归一化） |

写入采用**临时文件 + `File.Replace`** 的原子替换，避免写到一半崩溃污染原文件：

```csharp
var temp = path + ".markify-tmp";
await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
await using (var writer = new StreamWriter(stream, encoding))
    await writer.WriteAsync(text.AsMemory(), ct);

if (File.Exists(path)) File.Replace(temp, path, null);
else File.Move(temp, path);
```

### 4.5 未保存保护

三处入口都要拦：新建、打开（含拖放）、关闭窗口。

```
IsModified 由文本与「上次保存内容」比较得出（不是靠 UndoStack）
  → 关闭时 OnClosing 里 e.Cancel = true，弹确认框
  → 用户确认放弃后 vm.IsModified = false 再 Dispatcher.UIThread.Post(Close)
```

注意 `OnClosing` 里 **不能直接 `await` 后同步 `Close()`**——会重入。必须先把脏标记清掉，再通过 `Dispatcher.Post` 投递关闭。

### 4.6 绑定与 ToggleButton 的陷阱

`ToggleButton` / `MenuItem(ToggleType=CheckBox)` **同时绑定 `IsChecked` 和 `Command`** 会导致状态被切换两次（控件自己改一次，命令里再反转一次），表现为「点了没反应」。正确做法是**只绑定 `IsChecked`**，把副作用放到 `partial void OnXxxChanged(bool value)` 里：

```csharp
[ObservableProperty] public partial bool IsDarkTheme { get; set; }

partial void OnIsDarkThemeChanged(bool value)
{
    if (Application.Current is { } app)
        app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
}
```

这个 bug 在本项目里真实发生过，点击「深色」按钮状态栏显示「已切换到浅色主题」。

### 4.7 主题

- 应用级：`Application.RequestedThemeVariant` 切 `Light`/`Dark`，所有 `DynamicResource` 自动跟随。
- 自定义色：在 `Application.Resources` 的 `ThemeDictionaries` 里成对定义 `Light`/`Dark`（本项目定义了 `EditorBackground`、`ChromeBorder`、`PreviewBackground` 等）。
- 预览区随主题变色不需要重渲染——MarkView 的颜色全部是 `DynamicResource`，切主题时原地换色并保持滚动位置。
- 编辑器需要单独换 TextMate 主题（它不读 Avalonia 资源）。

### 4.8 其他可用能力（MarkView 12.4 已内置，本项目只开了部分）

- 表格、任务列表（`AreTaskListsInteractive`）、alert 块、脚注、`<details>`
- 代码块工具栏：语言标签（`ShowCodeBlockLanguage`）、复制按钮（`ShowCodeBlockCopyButton`）、行号、折行
- 图片：自适应缩放、lightbox 缩放、保存、缓存
- 公式（`MarkView.Avalonia.Math`）、Mermaid（`.Mermaid`）、SVG（`.Svg`）—— 均为独立包，按需引入
- 懒渲染（`IsLazyRenderingEnabled`）：图片/公式/代码着色滚动到附近才构建，长文档首屏秒开
- 文本选择/复制（含复制为 Markdown/HTML）、文档内查找、目录树 `TableOfContents`、标题折叠

---

## 5. 原型已实现清单

| 功能 | 状态 | 位置 |
| --- | --- | --- |
| 分栏编辑 + 实时预览（180ms 防抖） | ✅ | `MainViewModel.FlushPreview` |
| Markdown 语法高亮（TextMate） | ✅ | `MainWindow.ApplyMarkdownGrammar` |
| 双向滚动同步 | ✅ | `OnEditorScrollOffsetChanged` / `OnPreviewScrollChanged` |
| 大纲导航（源码行 ↔ 预览锚点） | ✅ | `MarkdownOutline` + `NavigateToHeading` |
| 打开/保存/另存为 + 编码保持 + 原子写入 | ✅ | `DocumentFileService` |
| 脏标记 + 三处未保存确认 | ✅ | `ConfirmDiscardAsync` |
| 拖放打开、命令行 `Markify doc.md` | ✅ | `OnDrop` / `App.axaml.cs` |
| 光标行列、字数（含 CJK 计数）、字符数 | ✅ | `MainViewModel.CountWords` |
| 明暗主题（编辑器 + 预览 + 界面联动） | ✅ | `OnIsDarkThemeChanged` |
| 左右/上下分栏切换、专注预览、自动换行、行号 | ✅ | `ApplySplitOrientation` |
| 快捷键（Ctrl+B/I/K/`、Ctrl+S/O/N） | ✅ | `OnEditorKeyDown` |
| 大纲导航、导出 PDF/HTML、多标签页 | ⬜ | 见 §6 |

---

## 6. 后续演进路线

按「投入产出比」排序：

1. **多标签页 / 工作区**（1–2 天）
   把 `MainViewModel` 拆成 `DocumentViewModel` + `WorkspaceViewModel`，`TabControl` 绑定集合。注意每篇文档要有独立的 `TextDocument`（共享会串撤销栈）。

2. **导出 HTML / PDF**（2–3 天）
   - HTML：`Markdig` 转 HTML + 内嵌 CSS 模板，一行 `File.WriteAllText`。零依赖，最容易。
   - PDF：无原生 .NET 方案。可选 QuestPDF（社区版有限制）或走「HTML → 系统浏览器打印」。若要纯原生，可用 MarkView 离屏渲染到 `RenderTargetBitmap` 后分页——需要自己处理分页，建议先不做。

3. **精确滚动同步**（3–5 天）
   目前是比例同步。要做「行级」精确同步，需要在渲染后遍历预览控件树，记录每个 block 的 `Bounds.Y`，建立「源码行 → Y」的分段线性映射。MarkView 的 `MarkdownViewer` 没直接暴露 block 边界，可行路径是：拿 Markdig AST 的 `Block.Line` + 遍历可视树里带 `markdown-*` style class 的控件，按顺序配对。难度中等，收益是长文档体验明显提升。

4. **文件监听 + 外部修改提示**（半天）
   `FileSystemWatcher` 监听当前文件，变更时提示「文件已被外部修改，是否重新加载」。编辑器和外部工具并用的场景很常见。

5. **搜索替换**（1 天）
   AvaloniaEdit 自带 `SearchPanel`（`Editor.SearchPanel`），Ctrl+F 基本开箱即用；预览侧用 `MarkdownViewer.Find(...)`。

6. **WYSIWYG 模式**（评估后决定）
   如果确实要做，路线是：升级到 `Avalonia.Controls.Markdown` + `RichTextEditor`（Pro 授权），复用其共享的 `FlowDocument` 模型。**不要**自己拿 AvaloniaEdit 硬做富文本——那条路上没有可用的文档模型底座。

---

## 7. 性能与体积

| 指标 | 实测/预期 |
| --- | --- |
| 冷启动 | ~0.6–0.9s（Debug，`dotnet run`） |
| 内存（30KB 文档） | 约 90–130MB（Avalonia + Skia 基线） |
| 逐键输入延迟 | 无感（防抖后仅每 180ms 一次重解析） |
| 发布体积（自包含 win-x64） | 约 60–80MB；开 trimming 后约 45–55MB |
| **NativeAOT（win-x64）** | **`Markify.exe` 34.21MB，运行文件合计 52.72MB，zip 包 19.91MB**（见 §7.1） |

**体积优化建议**：

```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<PublishTrimmed>true</PublishTrimmed>
<TrimMode>partial</TrimMode>          <!-- full 会裁掉 XAML 反射路径 -->
<InvariantGlobalization>true</InvariantGlobalization>
```

### 7.1 NativeAOT 实测结论（已用 CI 验证）

初版本文曾建议「不要开 `PublishAot`」，理由是 Avalonia 的 XAML、Markdig 泛型、TextMate 正则都有反射路径。**该结论经实测被推翻**，故更正如下。

CI（`.github/workflows/aot.yml`）在 `windows-latest` 上跑通了完整链路：

| 项目 | 结果 |
| --- | --- |
| `PublishAot=true` 编译 | ✅ 成功（约 60s） |
| AOT 自检 9 项 | ✅ 9/9 通过 |
| 确认为原生编译 | ✅ 报告 `isAot: yes`，`Assembly.Location` 为空 |
| `Markify.exe` | 34.2 MB |
| 运行文件合计（不含 pdb） | 52.7 MB（5 个文件） |
| 符号文件（不随发布分发） | 200.2 MB（3 个 pdb） |
| 裁剪分析器 IL 警告 | 0（`TrimmerSingleWarn=false` 展开后） |

**关键点：裁剪/编译通过不等于能跑，必须实际执行。** 本项目为此加了 `--selftest` 模式，在**真实原生二进制**里验证最易被裁剪破坏的 9 条路径：XAML 资源装载、AvaloniaEdit 文档与 TextMate transformer、Markdig 解析、MarkView 控件树构建（146 个可视后代 / 39 个 TextBlock）、主题模板里的 `PART_ScrollViewer`、主题切换、CJK 字数、文件 IO 往返。报告末尾的 `isAot` 标记用来防止「CI 悄悄跑了 JIT 版本」这种假通过。

**AOT 产物的形态**（不要用「有没有 .dll」来判断）：

```
Markify.exe            34.21 MB  ← 应用本身（含运行时）
libSkiaSharp.dll       11.09 MB  ← 原生库（2D 渲染）
av_libglesv2.dll        5.14 MB  ← 原生库（GLES）
libHarfBuzzSharp.dll    1.73 MB  ← 原生库（文本整形）
libonigwrap.dll         0.52 MB  ← 原生库（TextMate 正则）
                     ─────────
                        52.7 MB  (5 个文件，不含 pdb)
```

这四个 `.dll` 是**原生**动态库，AOT 应用本来就需要它们，发布时必须一起分发。真正需要断言的是「`Markify.dll` 这类托管程序集不存在」。

**仍需注意**：

- PDB 体积很大（合计 200MB：`Markify.pdb` 100MB、`libSkiaSharp.pdb` 80MB、`libHarfBuzzSharp.pdb` 20MB），但**不随发布分发**；`StripSymbols=true` 只剥离 exe 自身符号，第三方 PDB 仍在，做发布包时需显式排除 `*.pdb`。
- AOT 不是「单文件」：exe + 4 个原生 dll（合计 5 个文件）。若要求严格单文件需额外打包，与 AOT 无直接关系。
- 只在 `win-x64` 验证过。`osx-arm64` / `linux-x64` 未验证，跨平台 AOT 需各自跑一遍。
- 应用使用 `Process`/反射的动态场景若以后引入（如插件加载），AOT 下会失效，需回来重跑自检。

**自检的复现方式**：

```powershell
dotnet publish src/Markify -c Release -r win-x64 -p:PublishAot=true -p:StripSymbols=true -o artifacts/aot
artifacts/aot/Markify.exe --selftest --selftest-output artifacts/selftest-aot.txt
```

首次贡献或升级 Avalonia/MarkView 后建议重跑，这是发现「AOT 回归」成本最低的手段。

**发布流程（CD）**：`.github/workflows/release.yml` 由 `v*` tag 触发，链路为：构建 → JIT 自检 → AOT 发布 → AOT 自检（并断言 `isAot=yes`）→ 校验产物只含原生库 → 打包（排除 pdb）→ **解包后运行一次**（防止压缩破坏目录结构）→ 生成 SHA-256 → 创建 GitHub Release。

v0.1.0 实测发布数据：zip 19.91 MB，解包后 52.72 MB / 5 个文件，本机运行 9/9 自检通过，GUI 正常启动，内存约 133 MB。

---

## 8. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| `MarkView.Avalonia` 较新（2026 年） | API 可能在小版本变动 | 用浮动版本 `12.4.*`（其主版本号跟随 Avalonia，语义清晰）；把预览控件封装成 `MarkdownPreviewView`，便于替换 |
| 官方 `Markdown` 控件在 Pro 层 | 想用官方方案需付费 | 本项目已用 MIT 替代品，功能覆盖足够；商业化后可在封装层无缝切换 |
| 逐键重渲染在大文档上偏慢 | 输入卡顿 | 已用防抖；进一步可加「仅重渲染可见区块」（MarkView 懒渲染已部分覆盖） |
| 比例滚动同步在长文档漂移 | 体验瑕疵 | 已用大纲锚点补偿；后续见 §6.3 |
| AvaloniaEdit 未实现 `Save(string)` / `Load(string)` | 只能用 Stream API | 本项目直接用 `DocumentFileService` 自管 IO，绕开该缺口 |
| 中文输入法（IME） | 组合输入事件可能异常 | 实测 IME 正常；若异常可监听 `TextInput` 而非 `TextChanged` |

---

## 9. 复现步骤

```powershell
# 环境：.NET SDK 10+
dotnet new install Avalonia.Templates          # 若未安装
dotnet new avalonia.mvvm -n Markify -o src/Markify -f net10.0 -m CommunityToolkit -rvl true

cd src/Markify
dotnet add package Avalonia.AvaloniaEdit --version 12.0.0
dotnet add package AvaloniaEdit.TextMate --version 12.0.0
dotnet add package MarkView.Avalonia --version 12.4.0

dotnet run                                     # 或：dotnet run -- path\to\doc.md
```

`App.axaml` 里**三个 `StyleInclude` 缺一不可**：FluentTheme、AvaloniaEdit 的 Fluent 主题、MarkView 的 `MarkdownTheme.axaml`（缺最后一个时预览能渲染但无法滚动）。

---

## 10. 参考资料

- Avalonia 官方文档 · Markdown 控件（Pro）：https://docs.avaloniaui.net/controls/documents/markdown
- MarkView.Avalonia 文档：https://kryptos-fr.github.io/MarkView.Avalonia/
- MarkView.Avalonia 仓库（MIT）：https://github.com/Kryptos-FR/MarkView.Avalonia
- Markdown.Avalonia（备选，MIT）：https://github.com/whistyun/Markdown.Avalonia
- AvaloniaEdit 仓库：https://github.com/AvaloniaUI/AvaloniaEdit
- Markdig：https://github.com/xoofx/markdig
- GitHub 标题锚点规则（`github-slugger`）：https://github.com/Flet/github-slugger
