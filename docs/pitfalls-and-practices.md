# 实践经验与踩坑记录

> 这份文档记录本项目开发过程中**实际遇到**的问题：现象、根因、解法，以及「下次怎么更快发现」。
>
> 与 [avalonia-markdown-client.md](avalonia-markdown-client.md) 的分工：那份是**选型与架构报告**（为什么这么做），这份是**问题清单与操作手册**（撞墙后怎么爬出来）。报告 §4 的坑位描述是这里的精简版。

## 怎么用这份文档

- **排查时**：按「现象」关键词搜索。每条都有可复现的判据，不是"注意一下"这种空话。
- **写新功能前**：翻 `F. 方法论` 一节。那里的教训适用于所有 C#/Avalonia 项目，也是最省时间的一节。
- **升级依赖后**：重点看 `D. NativeAOT` 与 `E. 安装包`，这两块最容易因版本变化悄悄回归。

**可信度标注**，每条都标了：

| 标注 | 含义 |
| --- | --- |
| 🔬 实测 | 本机/CI 跑出来的数据，附具体数值 |
| 📖 文档 | 来自官方文档或包元数据，未亲自复现 |
| ⚠️ 推断 | 合理推测但未严格验证，引用时请自行确认 |

### 环境基线

| 项 | 值 |
| --- | --- |
| OS | Windows 10.0.19045 x64 |
| .NET SDK | 10.0.400，目标框架 `net10.0` |
| Avalonia | 12.1.4 |
| AvaloniaEdit | 12.0.0（包名 `Avalonia.AvaloniaEdit`） |
| MarkView.Avalonia | 12.4.0 |
| Markdig | 1.5.0 |
| Inno Setup | 6.7.3 |
| 验证方式 | 本地手工 + GitHub Actions（`windows-latest`） |

---

## A. 依赖与包管理

### A1. AvaloniaEdit 有两个包名，引错会运行时崩溃

**现象** 🔬 实测

`dotnet add package AvaloniaEdit` 能成功，编译也可能过，但启动时挂掉或编辑器空白。

**根因**

NuGet 上存在两个包：

| 包名 | 最新版本 | 说明 |
| --- | --- | --- |
| `AvaloniaEdit` | **0.10.12** | 早期独立移植，只兼容 Avalonia 0.10 |
| `Avalonia.AvaloniaEdit` | **12.0.0** | 官方维护，跟随 Avalonia 12 |

包元数据显示 `AvaloniaEdit` 依赖 `Avalonia [0.10.12, )` 且长期未更新，而 Avalonia 12 同名程序集的版本号完全不同，绑定时对不上。

**解法**

```xml
<PackageReference Include="Avalonia.AvaloniaEdit" Version="12.0.0" />
<PackageReference Include="AvaloniaEdit.TextMate" Version="12.0.0" />
```

注意语法高亮包叫 `AvaloniaEdit.TextMate`（没有重复的 `Avalonia.` 前缀），命名不统一，容易混。

**怎么快速判断**：`dotnet list package --include-transitive | findstr -i avaloniaedit`，看解析出来的版本是不是 12.x。

---

### A2. Avalonia 12 的诊断工具换了包名

**现象** 🔬 实测

从模板或旧教程抄 `Avalonia.Diagnostics` 12.x，还原失败。

**根因** 📖 文档

`Avalonia.Diagnostics` 最新只到 **11.3.22**，没有 12.x 版本。Avalonia 12 的模板改用 `AvaloniaUI.DiagnosticsSupport`。

**解法**

```xml
<PackageReference Include="AvaloniaUI.DiagnosticsSupport" Version="2.2.3">
  <IncludeAssets Condition="'$(Configuration)' != 'Debug'">None</IncludeAssets>
  <PrivateAssets Condition="'$(Configuration)' != 'Debug'">All</PrivateAssets>
</PackageReference>
```

这个包默认会进 Release 产物，必须像上面这样按配置排除——否则 AOT 发布时会把设计器相关代码一起带进去（还会引入 `Avalonia.DesignerSupport` 的裁剪告警，见 D3）。

---

### A3. 三个 `StyleInclude` 缺一不可，缺最后一个的症状很有迷惑性

**现象** 🔬 实测

预览区能渲染出内容，但**完全无法滚动**，`ScrollToAnchor()` 也无效。没有报错，没有任何异常。

**根因**

`MarkView.Avalonia` 把 `MarkdownViewer` 的 `ControlTemplate` 放在 `Themes/MarkdownTheme.axaml` 里，其中包含 `PART_ScrollViewer`。不引入这个文件，控件仍能用默认模板渲染内容，但**没有 ScrollViewer**——于是"能看不能滚"。

**解法**

```xml
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml" />   <!-- 编辑器模板 -->
  <StyleInclude Source="avares://MarkView.Avalonia/Themes/MarkdownTheme.axaml" />   <!-- 预览模板 + 滚动 -->
  <StyleInclude Source="avares://MarkView.Avalonia/Themes/GitHubTheme.axaml" />     <!-- 可选：外观预设 -->
</Application.Styles>
```

**怎么快速判断**：遍历可视树找 `ScrollViewer`，找不到就是模板没生效。本项目的 `--selftest` 里 `preview-scrollviewer` 这项检查就是为此设的。

---

## B. Avalonia 集成与 UI

### B1. `ToggleButton` 同时绑 `IsChecked` 和 `Command`，会触发两次导致"点了没反应"

**现象** 🔬 实测

点击工具栏「深色」按钮，主题没变，状态栏还显示「已切换到**浅色**主题」——方向反了。

**根因**

`ToggleButton` / `MenuItem(ToggleType=CheckBox)` 自己会把 `IsChecked` 取反一次，`Command` 里再 `IsDarkTheme = !IsDarkTheme` 又取反一次。两次抵消，实际"没变化"，而状态栏文案按最后一次赋值输出，于是显示错误的方向。

**解法**

**只绑 `IsChecked`**，把副作用移到属性变更回调：

```csharp
[ObservableProperty]
public partial bool IsDarkTheme { get; set; }

partial void OnIsDarkThemeChanged(bool value)
{
    if (Application.Current is { } app)
        app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
    StatusMessage = value ? "已切换到深色主题" : "已切换到浅色主题";
}
```

```xml
<ToggleButton Content="深色" IsChecked="{Binding IsDarkTheme}" />
```

**推广**：任何"控件自身会改状态"的场景（Toggle、CheckBox、Slider、Selection 类控件）都不要同时绑状态和命令。

---

### B2. 隐藏预览后，右侧仍占一半宽度

**现象** 🔬 实测

`IsVisible=false` 隐藏了预览控件，但 Grid 的 `*` 列还占着空间，编辑器只剩一半宽。

**根因**

`IsVisible=false` 只影响绘制与命中测试，**不改变布局中该元素的尺寸**。`GridLength.Star` 的列依然参与分配。

**解法**

不要只切可见性，要同时把对应轨道收成 0：

```csharp
columns[2] = new ColumnDefinition(
    showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0));
```

本项目用 `ApplyPaneLayout()` 统一处理"左右/上下分栏 + 是否显示预览"四种组合，且在 `Loaded` 时**必须调用一次**——否则初始状态（默认上下分栏）不会生效：

```csharp
Loaded += (_, _) => ApplyPaneLayout();
```

**踩到的次生问题**：我一开始只在 `PropertyChanged` 里调 `ApplyPaneLayout`，结果把 `IsVerticalSplit` 默认值改成 `false` 后界面仍是左右布局，因为没有任何属性变化事件。**布局代码要有一个"初始应用"入口。**

---

### B3. 主题切换要分别处理「界面」和「编辑器」

**现象** 🔬 实测

`RequestedThemeVariant = Dark` 之后，工具栏、预览、状态栏都变暗了，**编辑器还是亮色**。

**根因**

Avalonia 的 `DynamicResource` 会自动跟随主题，但 AvaloniaEdit 的语法着色来自 TextMate 主题（`ThemeName.LightPlus` / `DarkPlus`），它**不读 Avalonia 资源系统**。

**解法**

```csharp
private void ApplyEditorTheme(bool dark)
{
    _registryOptions = new RegistryOptions(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
    _textMate.SetTheme(_registryOptions.GetDefaultTheme());
    Editor.TextArea.TextView.Redraw();   // 必须重绘，否则配色不更新
}
```

不需要重建编辑器，`SetTheme` + `Redraw()` 即可。

---

### B4. 自定义 `Window` 的关闭确认里不能直接 `Close()`

**现象** ⚠️ 推断（按此写法会重入，已在实现中规避）

`OnClosing` 是 `async void`，如果 `await` 之后再调 `Close()`，会重新进入 `OnClosing`，脏标记仍在 → 再次弹窗 → 死循环。

**解法**

先清标记，再通过 Dispatcher 投递关闭：

```csharp
protected override async void OnClosing(WindowClosingEventArgs e)
{
    if (_viewModel is { IsModified: true } vm)
    {
        e.Cancel = true;                        // 先取消，避免窗口真关掉
        if (!await vm.ConfirmDiscardAsync())
            return;                             // 用户选了取消，保持打开

        vm.IsModified = false;                  // 关键：先清标记
        Dispatcher.UIThread.Post(Close);        // 下一轮消息循环再关
        return;
    }

    base.OnClosing(e);
}
```

---

## C. 编辑器与预览的滚动同步

这是本项目**最费时**的一块，连踩三个坑。三个坑彼此独立，任何一个没解决都会让同步完全失效。

### C1. 不能用 `TextView.DocumentHeight` 算滚动比例

**现象** 🔬 实测

滚动编辑器，预览完全不动。

**根因**

`TextView.DocumentHeight` 只覆盖**已经构建出来的可视行**（AvaloniaEdit 是虚拟化的）。诊断日志实测：

```
tvDocH=698.3   tvBounds=700.0        → 698-700 < 0 → ratio 恒为 0
editorExtent=1377.7  editorViewport=700.0
```

用 `698.3 - 700 = -1.7`，`max <= 0` 直接返回 0，所以预览永远停在顶部。

**解法**

用 `TextEditor` 暴露的外层 ScrollViewer 数据：

```csharp
var ratio = Ratio(Editor.VerticalOffset, Editor.ExtentHeight, Editor.ViewportHeight);

static double Ratio(double offset, double extent, double viewport)
{
    var max = extent - viewport;
    return max <= 0 ? 0 : Math.Clamp(offset / max, 0, 1);
}
```

**教训**：用第三方控件的属性前，先打印实际值确认语义。名字像"文档高度"不代表是"可滚动内容总高度"。

---

### C2. 反向同步（预览 → 编辑器）不能设 `ScrollViewer.Offset`

**现象** 🔬 实测

给 `Editor` 的 ScrollViewer 赋 `Offset.Y = 449.4`，立刻读回 `0.0`，界面无变化。

**根因**

AvaloniaEdit 的 `TextView` 实现了 `ILogicalScrollable`，是**逻辑滚动源**。外层 `ScrollViewer` 只是宿主，直接改它的 Offset 会在下一次布局时被 TextView 自己的偏移覆盖回去。

**解法**

必须走接口，设置 TextView 自身的偏移：

```csharp
var textView = Editor.TextArea.TextView;
((IScrollable)textView).Offset =
    new Vector(textView.ScrollOffset.X, Math.Max(0, target));
```

`--selftest` 里没有覆盖这条（它需要真实交互），回归时请手工验证：**滚动预览，编辑器应跟着动**。

---

### C3. 预览的 `ScrollViewer` 要延迟挂接，且防重入

**现象** 🔬 实测

`ScrollChanged` 事件绑不上，反向同步一开始完全无响应。

**根因（两件事叠加）**

1. `MarkdownViewer` 的 ScrollViewer 在**自己的 ControlTemplate 里**，`Window` 构造函数执行时模板还没应用，此时 `GetVisualDescendants()` 找不到它。
2. 双向同步必须防重入，否则 A 触发 B、B 又触发 A，出现抖动或位置漂移。

**解法**

在 `LayoutUpdated` 里"取到且未挂接过就挂一次"，并记录已挂接实例避免重复订阅：

```csharp
private ScrollViewer? _hookedPreview;

private void HookPreviewScroll()
{
    var preview = PreviewScroll;                 // 内部带失效重查
    if (preview is null || ReferenceEquals(_hookedPreview, preview))
        return;

    if (_hookedPreview is not null)
        _hookedPreview.ScrollChanged -= OnPreviewScrollChanged;

    _hookedPreview = preview;
    preview.ScrollChanged += OnPreviewScrollChanged;
}
```

双向同步统一用一个布尔量夹住写操作：

```csharp
if (_syncingScroll) return;
try { _syncingScroll = true; /* 写对方滚动位置 */ }
finally { _syncingScroll = false; }
```

**另一个细节**：`Markdown` 赋值会重建文档并把滚动归零。需要在 `PreviewMarkdown` 变更时置标记，在下次 `LayoutUpdated`（此时 extent 已算好）恢复：

```csharp
if (e.ExtentDelta != default) return;   // extent 变化中不参与反向同步
```

### C4. 已知限制：按比例同步在长文档会漂移

**说明**（设计取舍，非 bug）

目前是**按比例**同步：`ratio = offset / (extent - viewport)`。源码里一个 20 行代码块在预览中可能只占 3 行，比例映射会逐渐失配。

**现有缓解**：大纲导航走**锚点精确跳转**（`Preview.ScrollToAnchor(slug)`），不依赖比例。

**精确方案的代价**（评估后未实施）：需要遍历预览可视树，把带 `markdown-*` style class 的控件按顺序与 Markdig AST 的 `Block.Line` 配对，建立「源码行 → Y」分段映射。约 3–5 天工作量。

---

## D. NativeAOT

### D1. 裁剪/编译通过 ≠ 能运行，必须真实执行

**这是本项目最重要的一条经验。**

**现象** 🔬 实测

`dotnet publish -p:PublishAot=true` 成功，裁剪分析器 0 告警，产物看起来正常。但"能编译"完全不能证明"能跑"——AOT 崩溃通常是启动瞬间的 `MissingMetadataException` / `NullReferenceException`，且**只在特定代码路径触发**。

**解法：给应用内置冒烟自检**

`--selftest` 在真实原生二进制里跑 9 条最易被裁剪破坏的路径：

| 检查 | 为什么容易坏 |
| --- | --- |
| `xaml-app-resources` | XAML 编译产物用反射查资源 |
| `editor-loaded` | AvaloniaEdit 文档/模板初始化 |
| `textmate-grammar` | TextMate 用正则 + 反射加载语法 |
| `markdig-parse` | Markdig 有大量泛型 |
| `markview-render` | 大量控件构建（实测 146 个可视后代 / 39 个 TextBlock） |
| `preview-scrollviewer` | 主题模板查找 |
| `theme-switch` | `DynamicResource` 解析 |
| `word-count-cjk` | 字符分类逻辑 |
| `file-io-roundtrip` | 编码/BOM 处理 |

**关键细节**：报告里必须带 `isAot: yes/no`，否则 CI 可能悄悄跑了 JIT 版本而"假通过"。

```csharp
// NativeAOT 下 IsDynamicCodeSupported 为 false
return RuntimeFeature.IsDynamicCodeSupported ? "no" : "yes";
```

---

### D2. 用 `IOException`/`Dll` 之外的信号判断 AOT 是否生效

**可用信号** 🔬 实测

| 信号 | 非 AOT | AOT |
| --- | --- | --- |
| `Assembly.Location` | 有路径（dll） | **空字符串** |
| `RuntimeFeature.IsDynamicCodeSupported` | `true` | `false` |
| 产物里有没有 `Markify.dll` | 有 | 无 |

`Assembly.Location` 为空是最直观的判据。

---

### D3. 裁剪告警默认被折叠，要显式展开

**现象** 🔬 实测

第一次裁剪发布只报 **1 条** `IL2104`（"Assembly 'Avalonia.DesignerSupport' produced trim warnings"），看起来几乎没问题。

**根因**

裁剪器默认 `TrimmerSingleWarn=true`，每个程序集只报一条汇总告警。

**解法**

```powershell
dotnet publish src/Markify -c Release -r win-x64 `
  -p:PublishTrimmed=true -p:TrimMode=partial -p:TrimmerSingleWarn=false
```

展开后本项目仍是 0 条具体告警（`Avalonia.DesignerSupport` 只是设计期组件，见 A2 的排除配置）。

**注意 `TrimMode`**：`full` 会裁掉 XAML 的反射路径，用 `partial`。

---

### D4. 判断 AOT 产物形态：不能"有 .dll 就失败"

**现象** 🔬 实测（CI 第一次就失败在这）

我最初写的 CI 断言是"输出目录不能有 `.dll`"，结果 AOT 发布"失败"——但应用其实完全正常。

**根因**

AOT 应用**本来就需要**若干原生动态库：

```
Markify.exe            34.21 MB   ← 应用 + 运行时（原生）
libSkiaSharp.dll       11.09 MB   ← 2D 渲染
av_libglesv2.dll        5.14 MB   ← GLES
libHarfBuzzSharp.dll    1.73 MB   ← 文本整形
libonigwrap.dll         0.52 MB   ← TextMate 正则引擎
                     ─────────
                        52.72 MB  (5 个文件，不含 pdb)
```

它们是**原生** DLL，不是托管程序集。

**正确判据**

```powershell
$nativeAllow = @('libSkiaSharp.dll','libHarfBuzzSharp.dll','libonigwrap.dll','av_libglesv2.dll')
$unexpected = Get-ChildItem artifacts/aot -Filter *.dll -File |
              Where-Object { $nativeAllow -notcontains $_.Name }
if ($unexpected) { throw "非原生 DLL 泄漏：$($unexpected.Name -join ', ')" }
if (Test-Path artifacts/aot/Markify.dll) { throw "托管 Markify.dll 不应存在" }
```

**顺带**：PDB 合计约 **200 MB**（`Markify.pdb` 100 MB + `libSkiaSharp.pdb` 80 MB + `libHarfBuzzSharp.pdb` 20 MB），**不随发布分发**。统计体积时务必排除，否则会把 252.9 MB 误读成"发布包很大"。

---

## E. 安装包与文件关联

目标场景：双击 `.md` 直接用 Markify 打开。便携 zip 做不到，所以引入 Inno Setup。

### E1. 安装源必须用通配符，不能硬编码文件名

**现象** ⚠️ 推断（按 AOT 产物结构直接推得，已用通配符规避）

漏装任一原生库，尤其是 `libSkiaSharp.dll`，程序会在**启动阶段**抛：

```
System.TypeInitializationException: The type initializer for 'SkiaSharp.SKImageInfo' threw an exception.
 ---> System.DllNotFoundException: Unable to load DLL 'libSkiaSharp' or one of its dependencies
```

不是降级渲染，是直接崩溃。

**解法**

```ini
[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
```

AOT 产物将来多一个原生库，脚本无需改动。

**这个错误我真的犯过**：本地模拟打包时用错了产物目录（框架依赖的 `bin/Release/net10.0` 里没有 `libSkiaSharp.dll`），解包后一启动就崩溃，退出码 `-532462766`（`0xE0434352`）。做打包验证要用 **RID 特定的自包含/AOT 产物**。

---

### E2. 卸载必须**还原**关联，不能只删除

**现象** 🔬 实测（这是我引入的真实缺陷）

装完卸载，`.md` 的默认值变成**空字符串**。这台机器原本是 `VSCode.md`，用户的关联被我弄丢了。

**根因**

`Flags: uninsdeletevalue` 的语义是"卸载时删除该值"，不是"恢复原值"。删掉默认值后 Windows 会把它当成"未知应用"。

**解法**

安装时把原值备份，卸载时写回：

```ini
; 备份到 HKCU\Software\Markify\Backup
[Code]
const BackupRoot = 'Software\Markify\Backup';

procedure BackupAssociations();
var Existing: String;
begin
  Existing := ReadAssoc('.md');
  { 只备份非 Markify 的关联，避免升级时把 Markify 自己当成"原值" }
  if (Existing <> '') and (CompareText(Existing, '{#MyProgId}') <> 0) then
    RegWriteStringValue(HKA, BackupRoot, '.md', Existing);
end;
```

```ini
; [Registry] 里设置默认关联的条目不能带 uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.md"; ValueType: string; ValueName: ""; ValueData: "{#MyProgId}"; Tasks: associate
```

**验证结果**（本机，原始值 `VSCode.md`）：

```
BEFORE        : VSCode.md
AFTER INSTALL : Markify.Markdown
AFTER UNINSTL : VSCode.md      ← 精确还原
```

**注意**：`OpenWithProgids` 下的候选登记可以正常用 `uninsdeletevalue`，因为那里只是"候选项"，删掉不影响用户默认选择。

---

### E3. 清理备份键：阶段和 API 都要对

**现象** 🔬 实测

卸载后 `.md` 已经正确还原，但 `HKCU\Software\Markify\Backup` 残留。

**根因（两个独立错误）**

1. 在 `CurUninstallStepChanged` 的 `usUninstall` 阶段清理——此时卸载程序还在读写该键，删不掉。
2. 用 `RegDeleteKeyIfEmpty`——备份键里有扩展名子值，**不是空键**，该函数直接失败。

**解法**

```pascal
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then   // 用 Post，不是 usUninstall
    RestoreAssociations();
end;

// 内部清理
RegDeleteKeyIncludingSubkeys(HKA, BackupRoot);   // 不是 RegDeleteKeyIfEmpty
RegDeleteKeyIncludingSubkeys(HKA, 'Software\Markify');
```

**验证后**：`ProgID` / `Applications` 项 / 备份键 / 安装目录全部干净。

---

### E6. 验证卸载不能用"前后值相同"来判断，且 `-Wait` 并不真的等到结束

**现象** 🔬 实测

验证脚本打印 `BEFORE : Markify.Markdown` / `AFTER UNINSTALL: VSCode.md`，看起来"还原成功"，但 **BEFORE 就已经是 Markify 了**——说明上一轮卸载还没完成，测的根本不是这一轮。

**根因（两层）**

1. **Inno 的卸载器会把自己复制到临时目录再运行**（`_iu*.tmp`）。`Start-Process -Wait` 等到的是"启动器"退出，真正的卸载进程还在后台跑。紧接着读注册表，拿到的是**旧值**。
2. 用"卸载后等于安装前"作判据，在前后值恰好相同（或残留状态）时会**静默通过**，掩盖问题。

**解法**

用**哨兵值**，让"还原"这件事在数值上可区分：

```powershell
# 1. 先写入一个不可能自然出现的值
reg add "HKCU\Software\Classes\.md" /ve /d "Sentinel.TestHandler" /f

# 2. 安装 → 确认被改写
# 3. 卸载，并等到真正的临时进程结束
Start-Process "$target\unins000.exe" -ArgumentList "/VERYSILENT" -Wait
for ($i=0; $i -lt 30; $i++) {
    if (-not (Get-Process -Name "_iu*" -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 1
}
# 4. 断言恢复成哨兵值
```

实测输出（确定性证据）：

```
INSTALL: .md = Markify.Markdown ; backup = Sentinel.TestHandler
UNINSTL: .md = Sentinel.TestHandler
backup残留: False ; 安装目录残留: False
```

**推广**：验证"撤销/还原"类逻辑，**必须制造可区分的初始状态**。用真实值的后果是：初始值和期望值可能相同，测试永远绿。

---

### E4. Inno Setup 6 不自带简体中文

**现象** 🔬 实测

```
Error: Couldn't open include file "...\Languages\ChineseSimplified.isl"
```

**根因**

Inno Setup 6.7.3 的 `Languages\` 目录只有 29 个语言文件，**没有简体中文**（官方列为 Unofficial）。

**解法**

从官方仓库取对应版本的翻译文件，**放进仓库**而不是依赖本机安装：

```powershell
# 注意用对应版本标签，master 上已无此路径
curl -o installer/ChineseSimplified.isl `
  https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl
```

```ini
[Languages]
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
```

**踩到的次级问题**：`master` 分支的 `Files/Languages/Unofficial/` 已经改结构，路径 404。要用**版本标签**（`is-6_7_3`）。

---

### E5. Windows 10+ 的默认程序受哈希保护，程序无法强制设置

**说明** 📖 文档

即使写了 `HKCU\Software\Classes\.md` 默认值，Windows 10/11 的 `UserChoice` 有哈希校验，系统仍可能提示用户手动确认一次。这是设计上的限制，**任何安装程序都绕不过**。

**因此的产品决策**：把「设为默认」做成**可选任务**（默认勾选，用户可取消），并且始终注册 `OpenWithProgids` 让 Markify 出现在「打开方式」列表里。不篡改用户现有选择。

---

## F. 方法论（最值得复盘的一节）

前面是具体技术坑，这一节是**让这些坑被发现的机制**。没有这些，上面的问题至少有一半会带进发布版。

### F1. 先建立 JIT 基线，再验 AOT

CI 里 `jit-baseline` job 先于 `aot-build` 跑。这样 AOT 失败时，可以确定是 **AOT 的问题**，而不是代码本来就坏的。

**反例**：如果只跑 AOT，一个普通的逻辑错误会被误判成"裁剪把代码裁坏了"，浪费大量时间往错误方向排查。

### F2. 测试要跑在**真实产物**上，不是跑在"能编译"上

| 环节 | 只验证编译 | 验证真实产物（本项目做法） |
| --- | --- | --- |
| AOT | 编译成功就发布 | 跑 `--selftest` 9 项 |
| 安装包 | ISCC 返回 0 就发布 | 实际安装 → 运行自检 → 卸载 |
| zip 包 | 压缩成功就发布 | 解包 → 运行解包后的 exe |

CD 里 `Smoke-test installer` 这步就是干这个：真实静默安装、跑自检、再卸载，全部要成功才发版。

### F3. 断言要防"假通过"

CI 里三个刻意的"反向"检查：

1. **`isAot=yes` 断言**——防止 CI 实际跑的是 JIT 版本却报告成功。
2. **`Markify.dll` 不存在**——防止托管程序集混进 AOT 产物。
3. **解包后再运行一次**——防止压缩/解压破坏目录结构（这类问题压缩工具不会报错）。

### F4. 我的断言本身也会错，失败要读日志而不是改断言

**实例**：CI 第一次 AOT"失败"，我的第一反应是 AOT 有问题。读日志后才发现是**我写的断言错了**（把原生 DLL 当成托管程序集，D4）。

**教训**：CI 报错时，先确认"是产品坏了还是断言坏了"。判断方法——看日志里失败的具体内容，如果失败信息与"预期行为"矛盾，八成是断言写错了。

### F5. 在 UI 线程上同步等待异步 IO 会死锁

**现象** 🔬 实测

自检在 `file-io-roundtrip` 这步**永久挂起**，进程不退出，CI 只能等超时。

**根因**

```csharp
service.WriteAsync(path, content, encoding).GetAwaiter().GetResult();   // ✗ 死锁
```

Avalonia 有 SynchronizationContext，UI 线程上同步阻塞等待异步操作，续体要回到 UI 线程才能完成，而 UI 线程正被阻塞——互相等待。

**解法**

放到线程池上跑，用真正的 `await`：

```csharp
return Task.Run(async () =>
{
    await service.WriteAsync(path, content, encoding);
    var (text, encoding) = await service.ReadAsync(path);
    return $"bytes={...}";
}).GetAwaiter().GetResult();     // 这里阻塞是安全的：不依赖 UI 线程
```

**规律**：`.GetAwaiter().GetResult()` 在有 SynchronizationContext 的线程上 = 死锁风险。要么全程 `await`，要么 `Task.Run` 隔离。

### F6. 命令行参数解析要考虑"参数值也是文件路径"

**现象** 🔬 实测

自检的 `markview-render` 检查失败，预览渲染出 1 个 TextBlock（应该是 39 个）。

**根因**

```
Markify.exe --selftest --selftest-output D:\...\selftest-aot.txt
```

`App` 里查找"要打开的文档"用的是"第一个扩展名受支持的参数"，而 `DocumentFormats` 支持 `.txt`，于是**输出报告文件被当作文档打开**，真实内容被覆盖。

**解法**

跳过以 `--` 开头的参数，且自检模式下不解析文档参数：

```csharp
var initial = SelfTest.Requested
    ? null
    : desktop.Args?.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)
                                         && DocumentFormats.IsSupported(a));
```

**推广**：任何 CLI 都要区分"选项"与"位置参数"。用 `System.CommandLine` 一类库能避免这类问题。

### F7. 每加一层验证，都会立刻发现前一层的 bug

实际发生的顺序：

```
加 --selftest         → 立刻发现"参数被当文档打开"（F6）+ UI 线程死锁（F5）
加 AOT CI            → 立刻发现断言写错（D4）
加 installer-script  → 提前发现 .iss 语法问题（Pos/Copy 用法、RegDeleteKey 返回值）
加 Smoke-test        → 才能在发布前确认"装得上、跑得起来、卸得干净"
加卸载还原验证        → 才发现 uninsdeletevalue 会毁掉用户关联（E2）
加确定性验证          → 才发现"卸载完成"的时机判断有误（E6）
```

**所以**：验证步骤不是"额外工作"，它是**唯一能暴露问题的东西**。而且是滚雪球式的——每加一层，前一层的问题就浮出来。

### F8. 「撤销/还原」类逻辑必须用哨兵值验证

**通用教训**（详见 E6 的实例）：

如果测试是"执行 → 撤销 → 断言等于初始值"，而初始值本来就可能等于期望值，这个测试会**永远通过**。

**正确做法**：先写入一个不可能自然出现的哨兵值，再断言它能被还原回来。

```powershell
# ✗ 不可靠：初始值可能就是 VSCode.md，看起来"测试通过"但什么都没验证到
$before = $current; DoUndo(); assert($current -eq $before)

# ✓ 可靠：哨兵值让"还原"可区分
SetValue('Sentinel.TestHandler'); DoWork(); DoUndo()
assert($current -eq 'Sentinel.TestHandler')
```

同类场景：默认值还原、配置回滚、迁移脚本的 downgrade 路径、缓存失效。

---

## G. 被推翻的判断（诚实记录）

写下来是为了提醒：**没验证过的结论不要写成定论**。

| 初稿判断 | 实测结果 | 处理 |
| --- | --- | --- |
| 「**不要开 `PublishAot`**，Avalonia/Markdig/TextMate 的反射路径需要大量 `DynamicDependency`」 | AOT 编译成功，9/9 自检通过，裁剪 0 告警，exe 34.21MB | 已在报告 §7.1 更正为「可行」 |
| 「AOT 产物应该只有 exe 一个文件」 | 是 exe + 4 个原生库（共 5 文件，52.72MB） | 已在报告与 README 更正 |
| 「发布包约 45–55MB（trimming）」 | AOT 运行文件 52.72MB，zip 19.91MB，安装包 15.04MB | 已补充实测数据 |

**方法论价值**：这三条都是"听起来合理但没有实测"的推断。报告里现在对未验证内容标注了性质，避免下次把推测当结论用。

---

## H. 排查手法速查

| 场景 | 手法 |
| --- | --- |
| 控件找不到了 | `control.GetVisualDescendants().GroupBy(x => x.GetType().Name)` 打印类型直方图 |
| 事件没触发 | 在回调里先写一行日志到文件（WinExe 无控制台，`Console` 不可见） |
| 布局不生效 | 检查是不是只改了 `IsVisible` 而没改 Grid 轨道；确认有"初始应用"入口 |
| 滚动不同步 | 分别打印两侧的 `Offset / Extent / Viewport`，先确认 ratio 不是恒 0 |
| AOT 崩溃 | 先手工跑 `--selftest`；再确认 `Assembly.Location` 是否为空（是否真 AOT） |
| 安装包启动崩 | 检查 `{app}` 下原生库是否齐全（缺 Skia 必崩） |
| 注册表没生效 | `reg query HKCR\<ext>` 看合并视图；注意 `assoc` 只查 HKLM，查不到按用户安装的关联 |
| 卸载验证不稳定 | 卸载器会把自身复制到 `_iu*.tmp` 再运行，`-Wait` 不等于卸载完成；要轮询该进程结束后再读注册表（E6） |
| CI 失败但本地正常 | 先判断是产品坏了还是断言坏了（F4） |

---

## I. 参考

- 完整选型与架构：[avalonia-markdown-client.md](avalonia-markdown-client.md)
- 自检模式与发布流程：根目录 `README.md`
- AOT 官方前置条件：https://aka.ms/nativeaot-prerequisites
- Inno Setup 文档：https://jrsoftware.org/ishelp/
- Inno Setup 语言文件（含简中）：https://jrsoftware.org/files/istrans/
