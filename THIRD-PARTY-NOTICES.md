# 第三方组件许可

Markify 自身以 MIT 许可发布（见 `LICENSE.txt`）。分发二进制时附带以下组件的许可声明。

| 组件 | 版本 | 许可 | 项目地址 |
| --- | --- | --- | --- |
| Avalonia | 12.1.4 | MIT | https://github.com/AvaloniaUI/Avalonia |
| AvaloniaEdit | 12.0.0 | MIT | https://github.com/AvaloniaUI/AvaloniaEdit |
| AvaloniaEdit.TextMate | 12.0.0 | MIT | https://github.com/AvaloniaUI/AvaloniaEdit |
| TextMateSharp / TextMateSharp.Grammars | 2.0.x | MIT | https://github.com/danipen/TextMateSharp |
| MarkView.Avalonia | 12.4.0 | MIT | https://github.com/Kryptos-FR/MarkView.Avalonia |
| Markdig | 1.5.0 | BSD-2-Clause | https://github.com/xoofx/markdig |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| SkiaSharp | 3.x | MIT | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp | 8.x | MIT | https://github.com/mono/SkiaSharp |
| Onigwrap (oniguruma) | 1.x | BSD-2-Clause | https://github.com/mono/oniguruma-sharp |

## .NET 运行时

发布包使用 NativeAOT 编译，包含 .NET 运行时的原生代码部分，遵循 [MIT 许可](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)。

## 说明

- TextMate 语法定义（grammars）随 `TextMateSharp.Grammars` 分发，各语法文件保留其原始许可（多为 MIT 或 MIT 兼容）。
- 完整许可文本见各组件仓库。若需在分发中附带全文，可从上述地址获取。
