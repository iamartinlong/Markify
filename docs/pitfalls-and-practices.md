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

### B3. `Height=0` 的 Grid 行/列会被 `MinHeight`/`MinWidth` 撑开

**现象** 🔬 实测

左右分栏时，编辑器与预览下方多出一条 **120px 的空白带**，且随窗口宽度整幅延伸。

**根因**

```csharp
rows[2] = new RowDefinition(new GridLength(0))   // Height = 0 ✓
{
    MinHeight = 120,                              // ✗ 但最小高度是 120
};
```

Grid 布局里 **`MinHeight` 的优先级高于 `Height`**。即使把高度设成 0，只要 `MinHeight > 0`，这一行仍会占据 120px。

同一个方法里还写过一个无效的三元表达式，它没有暴露问题，但说明这段逻辑没被认真检查：

```csharp
MinHeight = vertical ? 120 : 120,   // 两个分支相同，等于常数
```

**解法**

`MinHeight` 必须和该轨道的可见性/存在性保持一致：

```csharp
rows[2] = new RowDefinition(
    !vertical && showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0))
{
    MinHeight = !vertical && showPreview ? 120 : 0,   // 一起归零
};
```

**XAML 里的初始定义也要同步**，否则 `ApplyPaneLayout()` 还没来得及执行时同样会显示空白：

```xml
<RowDefinition Height="*" MinHeight="120" />
<RowDefinition Height="0" MinHeight="0" />   <!-- 不能省 MinHeight="0" -->
<RowDefinition Height="0" MinHeight="0" />
```

**排查手法**：空白带如果是**单色、等宽、位置固定**，优先怀疑某个**空轨道被最小尺寸撑开**，而不是内边距或对齐问题。用 DevTools 的可视树选中那一行看实际尺寸最快。

**同类陷阱**：`MinWidth` / `MaxHeight` / `GridSplitter` 所在的像素轨道、以及 `Width=0` 但 `MinWidth>0` 的列，都是同一个模式。

---

### B4. 主题切换要分别处理「界面」和「编辑器」

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

### B5. 自定义 `Window` 的关闭确认里不能直接 `Close()`

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

### B6. 给 `TextEditor` 设 Padding 会让行号栏上下都内缩

**现象** 🔬 实测

编辑区顶部和底部的行号分隔线都**提前断掉**，露出"没有线也没有内容"的空白。像素测量：

```
底部：分隔线结束 y=756   编辑区底边 y=764    → 差 8px
顶部：编辑区顶边 y=108   分隔线起点 y=116    → 差 8px
```

**根因**

```xml
<ae:TextEditor Padding="8,8,0,8" />
```

`TextEditor` 的 `Padding` 会内缩**整个编辑器内容**，行号栏（`LineNumberMargin`）是编辑器的一部分，所以它也跟着少掉顶部和底部各 8px，无法贴合编辑区上下边缘。

**尝试过但无效的做法**：把 `Padding` 移到外层 `Border` 上——Border 的 padding 同样内缩整个子元素，问题一模一样。**换宿主不改变"内缩对象"这一事实。**

**解法**

```xml
<!-- 只保留左边距给正文留白；上/下/右归零，让行号槽贴合上下边缘 -->
<ae:TextEditor Padding="8,0,0,0" Background="Transparent" />
```

修复后实测：

| 项目 | 修复前 | 修复后 |
| --- | --- | --- |
| 分隔线距顶 | 8px | **0px** |
| 分隔线距底 | 8px | **0px** |
| 行号 1 文字距顶 | 12px | **4px**（AvaloniaEdit 自身行高留白） |

**取舍说明**：去掉上下 padding 后，首行文字和最后一行文字会更贴近边缘。若想要"留白但线不断"，需要给 `TextView` 加自定义背景渲染器——本项目选简单方案。

**排查手法**：行号/滚动条的"断口"类问题，量三个值就能定位——**线起点 / 内容区起点 / 容器起点**，以及底部的对应三值。差值等于某处 padding 或 margin 时，就是内缩导致的。**顶部和底部要分别检查**，同一个 `Padding` 的上下分量会各自造成一处断口（本项目就是先修了底部、后才发现顶部）。

**推广**：`Padding` 的四分量是独立的，修完一个边不代表其他边没问题。改 `Padding` 时应当把四个边**逐一对照设计意图**确认。

---

### B7. MarkView 的滚动条浮在内容之上，会压住表格边框

**现象** 🔬 实测

预览区右侧的表格右边框与滚动条**重叠**，视觉上像被截断。

```
滚动条滑块  x = 1252-1253
表格右边框  x = 1255        ← 越过了滚动条
```

**根因**

`MarkdownViewer` 的 `PART_ScrollViewer` 用的是 overlay 式滚动条，**不为自己预留布局宽度**。文档内容按 ScrollViewer 的完整宽度排版，滚动条随后画在内容之上，于是贴右边缘的元素（表格边框、代码块背景）被盖住。

**关键实验：改容器 padding 无效**

我先把 `PreviewHost` 的右 padding 从 16 加到 24，结果：

```
padding=16 → 滚动条 1252-1253, 表格 1255   （重合 2px）
padding=24 → 滚动条 1244-1245, 表格 1247   （重合 2px）
```

**两者一起左移，相对位置完全不变。** 因为容器 padding 同时内缩了 ScrollViewer 和它的内容，滚动条与内容的相对关系没变。

**解法**

要改的是**文档内容自身的宽度**，不是它的容器。`Grid.markdown-document` 是渲染文档的根元素（MarkView 的公开 style class），给它加右外边距即可：

```xml
<Style Selector="Grid.markdown-document">
    <Setter Property="Margin" Value="0,0,12,0" />
</Style>
```

同时把 `PreviewHost` 的右 padding 归零，避免右边距叠加两次：

```xml
<Border x:Name="PreviewHost" Padding="24,16,0,16" ... />
```

修复后实测：表格 `x=1259`，滚动条 `x=1268-1269`，**间隙 9px**。

**可复用的判断**：遇到"内容被浮层遮挡"（overlay 滚动条、悬浮按钮、粘性表头），**调容器的 padding 一定是无效的**——必须改内容层的尺寸或边距。判断方法：同时移动前后测量两者的相对距离，如果距离不变，就说明动错了层级。

**顺带**：这条也说明"加 padding"和"加 margin"在 WPF/Avalonia 里对布局的影响不同——`Padding` 内缩子元素，`Margin` 只影响元素自身在父容器中的位置。这里需要的是后者。

---

### B8. `ContextMenu` 不在视觉树里，相对绑定与 PlacementTarget 都拿不到目标

**现象** 🔬 实测

给标签加了右键菜单，绑定写成 `Command="{Binding $parent[ItemsControl].((vm:MainViewModel)DataContext).SaveCommand}"`。
结果：**菜单能弹出，但每一项都是灰的**，点击毫无反应。

**根因（两层，都实测确认）**

1. `ContextMenu` 是弹出层，**不属于视觉树**，`$parent[ItemsControl]` 解析不到祖先，绑定失败 → `Command` 为 null → 菜单项禁用。
2. 改用 `ContextMenu.PlacementTarget`（被右键的控件）看起来合理，但日志显示 **`Opening` 事件里它已经是 `null`**：

```
opening: placement=null tag=null
click:   menu=ok          tag=null
```

**解法**：在**右键按下时**就记住目标，菜单项点击时再取。

```csharp
// XAML: Border.PointerPressed="OnTabPointerPressed"
private DocumentViewModel? _contextMenuTarget;

private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
{
    if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
    if (sender is Control { DataContext: DocumentViewModel doc })
        _contextMenuTarget = doc;
}
```

菜单项的 `Click` 从 `_contextMenuTarget` 取值。实测通过：状态栏出现「已复制路径：…」。

**注意**：`MenuItem.DataContext` 也不可靠（菜单内 DataContext 不一定继承到外层标签），不要依赖它。

---

### B9. `async void` 事件处理器必须自带异常兜底

**问题** 🔬 实测

右键菜单 7 个处理器、文件树选择、拖放等都是 `async void`（事件签名要求）。
任何一个内部异常都会逃逸到 `SynchronizationContext`，**直接终结进程**——用户看到的是"点了菜单程序就没了"。

**易被忽略的原因**：这些方法调用的 VM 方法内部大多有 try/catch，看起来是安全的；
但链路中间还夹着**弹窗、批量关闭、文件 IO**，任何一环抛异常都到顶了。

**解法**：统一包一层。

```csharp
private async void RunMenuAction(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) { _viewModel.StatusMessage = $"操作失败：{ex.Message}"; }
}
```

**要点**：不要静默吞掉——把原因写到状态栏，用户至少知道发生了什么。

---

### B10. 不设 `Window.Background` 时，菜单栏在深色主题下会渲染成纯黑

**现象** 🔬 实测

深色主题下截图取色，得到三段互不相关的颜色：

| 区域 | 实测 |
| --- | --- |
| 标题栏（非客户区） | `#FFFFFF` |
| 菜单栏 | `#000000` ← 纯黑 |
| 工具栏 / 标签栏 / 状态栏 | `#252526` |

菜单栏像被"挖掉"了一样，与周围的 `#252526` 明显割裂。浅色主题下完全看不出问题。

**根因**

`MainWindow.axaml` 从未设置 `Window.Background`。`Menu` 自己不带背景，于是露出**下层的窗口底色**；而窗口底色未设置时取主题默认值——浅色恰好是白，跟菜单栏想要的浅色一样，所以问题不暴露。

**解法**：给 Window 设背景，并把 Menu 设为透明，颜色交给外层 `Border`。

```xml
<Window ... Background="{DynamicResource SurfaceChrome}">
  <Border Classes="chrome">
    <Menu Background="Transparent"> ... </Menu>
  </Border>
</Window>
```

**排查手法**：深色主题出现"某个横条颜色不对"，先逐区域取色。若某条纯黑或纯白，多半是**没人给它背景**，而不是样式写错。

---

### B11. 设计令牌缺键时 Avalonia 不报错，只是悄悄回退

**现象** 🔬 实测

给明暗两套主题字典补令牌时，漏掉 Dark 里的一个键。结果是：

- 编译通过，0 警告
- 单元测试全过
- 运行不崩溃
- 只是那个属性用了默认值 —— 深色主题下某个面板"还是浅色的"

**根因**：`{DynamicResource Key}` 找不到键时返回 `UnsetValue`，控件回退到模板默认值，整个过程没有异常也没有日志。

**解法**：在自检里加一项，把两套主题 × 全部令牌都查一遍。

```csharp
foreach (var key in required)
    foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        if (!app.TryGetResource(key, variant, out _))
            missing.Add($"{variant.Key}:{key}");
```

这条检查**必须做负向验证**才有意义：故意删掉 `Dark:StateSelected` 再跑，确认它报 `missing tokens: Dark:StateSelected` 且退出码为 1。第一次写完不验证，很可能写出一条永远通过的假检查（同类教训见 F12）。

---

### B12. 改 XAML 后 `dotnet build` 可能不重编资源，跑的是旧 XAML

**现象** 🔬 实测

做 B11 的负向验证：临时删掉一个令牌 → 自检正确报错 → 恢复文件 → 再次构建并运行，**仍然报同样的错**。

`App.axaml` 里那个键明明已经回来了（`Select-String` 确认存在）。

**根因**：XAML 被编译成 `AvaloniaResource` 嵌进程序集，增量构建认为输入没变（时间戳精度 / 中间文件缓存），没重新打包资源。

**解法**：改完 XAML 后做验证性运行，用 `-t:Rebuild`，不要用普通 build。

```powershell
dotnet build src/Markify -c Release -t:Rebuild
```

**要点**：`dotnet build` 成功不等于**你刚才改的 XAML 被编进去了**。凡是要"以运行结果为准"的验证（自检、截图取色），先 Rebuild。

---

### B13. 改 UI 后截图验证：鼠标停在哪，截图里就有它

**现象** 🔬 实测

改完深色主题，逐列扫描截图，发现 x=641 有一条 351px 长的 `#858585` 亮线，怀疑是 `GridSplitter` 的抓握指示太亮。给 `GridSplitter` 设了背景色，重新截图，**亮线还在**。

再查：`#858585` 的像素其实散布在 x=641..1287、y=145..597 的整个区域共 1226 个 —— 那是编辑器里的**语法高亮文字**（TextMate DarkPlus 的注释色），不是线。

**根因**：按"列"扫描时，一条列上连续的相同颜色会被误认为一条竖线，实际可能只是该列恰好穿过若干同色文字。

**解法**：判断"某个区域是什么颜色"用**区域众数**，不要用单列/单行扫描。

```js
function mode(im, x0, x1, y0, y1) { /* 统计矩形内出现最多的颜色 */ }
```

**另一半教训**：截图时鼠标的位置会被截进去。要验证"静态外观"先把鼠标移开；要验证"悬停态"则有意停上去。两者不能用同一张图。

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

### E4. 验证卸载不能用"前后值相同"来判断，且 `-Wait` 并不真的等到结束

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

### E5. Inno Setup 6 不自带简体中文

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

### E6. Windows 10+ 的默认程序受哈希保护，程序无法强制设置

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
加确定性验证          → 才发现"卸载完成"的时机判断有误（E4）
```

**所以**：验证步骤不是"额外工作"，它是**唯一能暴露问题的东西**。而且是滚雪球式的——每加一层，前一层的问题就浮出来。

### F8. 「撤销/还原」类逻辑必须用哨兵值验证

**通用教训**（详见 E4 的实例）：

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

### F9. 测试工具自身的缺陷会伪装成产品缺陷

**现象** 🔬 实测

审计时发现 `Ctrl+H`「没反应」。按之前的经验（`Ctrl+F` 曾因命令路由失效）加了窗口级隧道处理器，仍然没反应。日志显示 `ShowSearchPanel` 完全没被调用。

于是做对照实验：**此时 `Ctrl+F` 也不工作了**——但它几天前验证过是好的。

**根因**

问题在模拟按键的方式，不在应用代码：

| 发送方式 | 结果 |
| --- | --- |
| `keybd_event` 发 Ctrl | Avalonia 收不到 `KeyModifiers`，事件不触发 |
| `SendInput` 发 Ctrl | 正常，日志立刻出现 `key=H mods=Control` |

`keybd_event` 是旧 API，对修饰键状态的处理在新版应用里不可靠。而单独发送字母键（无修饰键）却能成功——这正是误导之处：**能输入文字让人误判「键盘模拟是好的」**。

**解法**：UI 自动化一律用 `SendInput`。

```csharp
[StructLayout(LayoutKind.Sequential)]
struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1, pad2; }

uint sent = SendInput(4, new[] {
    Key(0x11, false),  // Ctrl down
    Key(vk,   false),  // key down
    Key(vk,   true),   // key up
    Key(0x11, true)    // Ctrl up
}, Marshal.SizeOf(typeof(INPUT)));
// 返回 4 表示四个事件全部投递成功
```

**方法论教训**：

1. **对照组**比单次观察可靠。发现 `Ctrl+H` 失败时，顺手测一下「已知是好的」`Ctrl+F`，立刻能区分「改坏了」还是「测试方法失效」。
2. **「部分成功」最容易误导**。字母能输入就以为键盘模拟没问题，实际上修饰键是单独一条路径。
3. **加日志 > 猜代码**。先怀疑命令路由、又怀疑 IME（确实查了 `ImmGetOpenStatus` 并排除），最后靠一行日志定位。
4. 审计过程中我**为不存在的缺陷改过代码**（加了窗口级 handler）。修复本身无害，但要意识到：**没有失败证据就动手改，是在引入风险**。

**推广**：任何「自动化测试通过/失败」的结论，先确认工具本身可信。UI 自动化尤其如此——它静默失败时，看起来和「功能没实现」一模一样。

---

### F10. 自动化验证失败时，先排除环境干扰

**踩坑过程** 🔬 实测

审计右键菜单时连续三次"失败"，每次都得出不同结论，全部是误判：

| 现象 | 我的判断 | 真相 |
| --- | --- | --- |
| 菜单项灰显 | 绑定写错了 | ✅ 这个判断对了 |
| 修复后仍不执行 | 绑定还是错 | 点击坐标落在**另一个程序的窗口**上 |
| 键盘 Enter 也不执行 | 处理器没接上 | **剪贴板被其他程序占用**，`Get-Clipboard` 直接抛异常 |

最后一次用**状态栏文本**而不是剪贴板验证，才得到确定结论。

**教训**：

1. **验证指标要选不受环境影响的**。剪贴板是全局共享资源，别的程序随时会占用；状态栏、日志文件才是本进程私有的。
2. **窗口位置会漂移**。自动化点击前要 `SetWindowPos` + `SetForegroundWindow`，否则可能点到别处。
3. 截图能证伪：`SuperPost` 窗口盖住了目标区域，一眼就能看出点击没打中。
4. 与 F9 一起看：**F9 是工具静默失效，F10 是环境干扰**，两者都伪装成"功能没实现"。

---

### F11. 用固定 `Task.Delay` 等异步事件，测试必然偶发失败

**现象** 🔬 实测

`FileChangeWatcherTests` 用 `await Task.Delay(150)` 等 `FileSystemWatcher` 就绪、
`await Task.Delay(1500)` 等防抖窗口过去。表现是**偶发失败**：105 个用例里偶见 1 个失败，重跑即过。

**根因**

文件系统事件是异步的，到达时间取决于系统负载。固定 sleep 只是"多数时候够用"，
负载高时事件还在路上，断言就提前执行了。

**更糟的后果**：这类偶发失败会训练人养成"重跑一次就好"的习惯，
从而掩盖真正的间歇性缺陷。审计时看到过一次失败又无法复现，**必须查清而不是跳过**。

**解法**：轮询到条件成立，而不是睡固定时长。

```csharp
private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition()) return true;
        await Task.Delay(25, TestContext.Current.CancellationToken);
    }
    return condition();
}
}
```

慢机器上只是慢一点，不会误判失败。修复后连跑 **10 次全通过**（此前 8 次里有 1–2 次失败）。

**同一类问题**：`Thread.Sleep`、`WaitForExit(固定时间)`、UI 自动化里的"点完等 1 秒"——都是同一个模式。

---

### F12. 「没有任何反应」的断言必须配一个正向对照

**背景** 🔬 实测

`SuppressOwnWrite_DoesNotRaise` 原本只断言"抑制期间事件数为 0"。
这条断言在 **watcher 完全没工作** 时也会通过——测试是绿的，功能是坏的。

**解法**：加正向对照。先确认 watcher 能检测到探测写入，再断言抑制期间为 0，
最后确认宽限期后能恢复检测。

```csharp
await EnsureWatchingAsync(watcher, path, () => Volatile.Read(ref raised));  // 先证明能用
var baseline = Volatile.Read(ref raised);
using (watcher.SuppressOwnWrite()) { /* 写入 */ }
Assert.Equal(0, Volatile.Read(ref raised) - baseline);                     // 抑制有效
await Task.Delay(700);                                                      // 等宽限期结束
await File.WriteAllTextAsync(path, "external");
Assert.True(await WaitUntilAsync(() => Volatile.Read(ref raised) > before)); // 能恢复
```

**加上正向对照后，这个测试立刻暴露了一个真实产品缺陷**（见 F13）。

**推广**：凡是断言"不发生 X"的测试，都要先证明"在正常条件下 X 会发生"。
否则它只能证明"代码没抛异常"，不能证明行为正确。

---

### F13. 用「布尔开关 + 延迟解除」抑制事件，会吞掉宽限期内的真实事件

**现象** 🔬 实测（由 F12 的正向对照发现）

自身保存时需要抑制文件监听，避免误报"文件被外部修改"。原实现是：

```csharp
// 抑制开始 → 置 true
_owner._ownWriteInProgress = true;

// Dispose 时延迟 500ms 再置 false
_ = Task.Delay(500).ContinueWith(_ => _owner._ownWriteInProgress = false, ...);
```

**缺陷**：这 500ms 内发生的一切文件事件都被当作"自身写入"丢弃。
真实场景——**保存后立刻被外部工具（如 git、格式化器）修改**——那次修改会被静默忽略，
用户以为文件是最新的，实际不是。

**解法**：改用时间戳窗口。

```csharp
private long _ownWriteTicks;   // 最近一次自身写入的时刻
private static readonly TimeSpan OwnWriteGrace = TimeSpan.FromMilliseconds(500);

private bool InOwnWriteGrace
{
    get
    {
        var ticks = Interlocked.Read(ref _ownWriteTicks);
        return ticks != 0 && DateTime.UtcNow.Ticks - ticks < OwnWriteGrace.Ticks;
    }
}
```

写入时刷新时间戳，事件到达时比较**距上次写入多久**。
仍然是 500ms 的宽限，但语义从"这段时间内一律忽略"变成"忽略紧跟着自身写入的事件"——
宽限期结束后到达的事件照常上报。

**要点**：抑制"自己的操作"时，优先用**时间窗口**而不是**全局开关**。
开关的问题在于它的作用域是"时间上的一个区间"，而真实事件并不遵守你划的边界。

---

### F14. 脚本改文档改完后，必须比对"非空行多重集"

**现象** 🔬 实测

`pitfalls-and-practices.md` 的一级标题跑到了第 194 行，B8/B9/F10 悬在文件最开头，F9 被夹在 H 与 I 之间。连续两个版本都没人发现——因为**文档结构损坏不影响编译、不影响测试，读正文也看不出**。

**根因**：用脚本按**行号**插入内容，而行号是在另一份副本上算出来的。文件稍有一点长度差异，插入点就落到别处。

```powershell
# ✗ 行号在别的副本上算的，插进去就错位
$lines.InsertRange($lineNumberFromAnotherCopy, $newContent)
```

**解法**：按标题定位边界，而不是按行号。

```powershell
$iC = FindHeading $L '^## C\.'     # 用正则找章节标题
$at = $iC; while ($L[$at-1] -match '^\s*$') { $at-- }   # 回退到分隔线
```

**关键的一步是改完的校验**——比对非空行的**多重集**：

```powershell
$before = $original | Where-Object { $_ -notmatch '^\s*$' } | Sort-Object
$after  = $updated  | Where-Object { $_ -notmatch '^\s*$' } | Sort-Object
if (Compare-Object $before $after) { throw "内容变了，放弃写入" }
```

它同时能抓住两类破坏：**丢了内容**（少行）和**重复了内容**（多行）。实测中它确实拦住了一次——重排脚本多删了一个 `---`（那是上一节的结束分隔线），多重集立刻报出 786 对 785。

**推广**：凡是用脚本批量改结构化文本（Markdown、配置、SQL 迁移），都用「改完比对内容集合」兜底。它比"再看一眼"可靠，也比写完整解析器便宜。

---

### F15. 断言"恰好发生 1 次"的测试，把机器速度当成了被测行为

**现象** 🔬 实测（CI 上失败，本机连跑 8 次全过）

`MultipleRapidWrites_AreDebounced` 连续写文件 5 次（间隔 30ms），断言防抖后的通知数 `== 1`。

- 本机（负载低）：8/8 通过
- GitHub Actions（共享 runner）：**失败，实际得到 2**

**根因**

`FileSystemWatcher` 的事件由操作系统分发，到达时间取决于系统负载。负载高时，5 次写入产生的事件可能被拖延、打散，跨越 250ms 防抖窗口，于是**合法地**触发 2 次通知。

用简化模型验证（事件到达间隔 → 通知数）：

| 场景 | 事件间隔 | 通知数 | 旧断言 |
| --- | --- | --- | --- |
| 理想 | `[20,20,20,20,20]` | 1 | 通过 |
| 高负载：间隔被拉长 | `[200,200,200,200,200]` | 3 | **失败** |
| 高负载：堆积后分批 | `[20,20,300,20,20]` | 2 | **失败** |
| 极端：几乎不合并 | `[300,300,100,300,100]` | 4 | **失败** |

**关键认识**：`== 1` 等价于要求"5 次写入的事件必须在 250ms 内全部到齐且紧密"——这是**外部环境属性**，不是 `FileChangeWatcher` 能控制的东西。断言它，等于断言"机器足够快"。

**解法**：断言防抖的**定义**，而不是某个特定环境的产物。

```csharp
// 防抖 = 把 N 次合并成更少的次数
Assert.True(total >= 1, "应至少通知一次");
Assert.True(total < writes, $"通知数应少于写入数 {writes}，实际 {total}——防抖没生效");
```

新断言在同样 4 种场景下 0 失败，且**仍然能抓到真正的缺陷**：把防抖关掉（`_timer.Change(TimeSpan.Zero, ...)`）再跑，它确实失败。

**推广**：凡是断言精确次数的测试，先问一句——**这个次数是谁保证的？**

- 被测代码保证 → 可以断言精确值
- 操作系统 / 调度器 / 网络保证 → 只能断言范围或单调关系

同类陷阱：断言"日志恰好 1 条"、"重试恰好 3 次"、"回调恰好调用 1 次"（异步场景）。

---



**现象** 🔬 实测

`pitfalls-and-practices.md` 的一级标题跑到了第 194 行，B8/B9/F10 悬在文件最开头，F9 被夹在 H 与 I 之间。连续两个版本都没人发现——因为**文档结构损坏不影响编译、不影响测试，读正文也看不出**。

**根因**：用脚本按**行号**插入内容，而行号是在另一份副本上算出来的。文件稍有一点长度差异，插入点就落到别处。

```powershell
# ✗ 行号在别的副本上算的，插进去就错位
$lines.InsertRange($lineNumberFromAnotherCopy, $newContent)
```

**解法**：按标题定位边界，而不是按行号。

```powershell
$iC = FindHeading $L '^## C\.'     # 用正则找章节标题
$at = $iC; while ($L[$at-1] -match '^\s*$') { $at-- }   # 回退到分隔线
```

**关键的一步是改完的校验**——比对非空行的**多重集**：

```powershell
$before = $original | Where-Object { $_ -notmatch '^\s*$' } | Sort-Object
$after  = $updated  | Where-Object { $_ -notmatch '^\s*$' } | Sort-Object
if (Compare-Object $before $after) { throw "内容变了，放弃写入" }
```

它同时能抓住两类破坏：**丢了内容**（少行）和**重复了内容**（多行）。实测中它确实拦住了一次——重排脚本多删了一个 `---`（那是上一节的结束分隔线），多重集立刻报出 786 对 785。

**推广**：凡是用脚本批量改结构化文本（Markdown、配置、SQL 迁移），都用「改完比对内容集合」兜底。它比"再看一眼"可靠，也比写完整解析器便宜。

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
| 底部/右侧多出固定宽度空白 | 某个 `Height=0` 的轨道仍带 `MinHeight>0`，Grid 会按最小尺寸撑开（B3） |
| 行号/滚动条线提前断开 | 量「线起点 / 内容区起点 / 容器起点」（上下各测一次），差值是 padding 或 margin（B6） |
| 内容被滚动条/浮层压住 | 调容器 padding 无效（会连滚动条一起移）；改内容层 margin（B7） |
| 滚动不同步 | 分别打印两侧的 `Offset / Extent / Viewport`，先确认 ratio 不是恒 0 |
| AOT 崩溃 | 先手工跑 `--selftest`；再确认 `Assembly.Location` 是否为空（是否真 AOT） |
| 安装包启动崩 | 检查 `{app}` 下原生库是否齐全（缺 Skia 必崩） |
| 注册表没生效 | `reg query HKCR\<ext>` 看合并视图；注意 `assoc` 只查 HKLM，查不到按用户安装的关联 |
| 卸载验证不稳定 | 卸载器会把自身复制到 `_iu*.tmp` 再运行，`-Wait` 不等于卸载完成；要轮询该进程结束后再读注册表（E4） |
| CI 失败但本地正常 | 先判断是产品坏了还是断言坏了（F4） |

---

## I. 参考

- 完整选型与架构：[avalonia-markdown-client.md](avalonia-markdown-client.md)
- 自检模式与发布流程：根目录 `README.md`
- AOT 官方前置条件：https://aka.ms/nativeaot-prerequisites
- Inno Setup 文档：https://jrsoftware.org/ishelp/
- Inno Setup 语言文件（含简中）：https://jrsoftware.org/files/istrans/

