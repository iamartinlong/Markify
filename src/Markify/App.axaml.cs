using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Markify.Services;
using Markify.ViewModels;
using Markify.Views;

namespace Markify;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            desktop.MainWindow = window;

            // 支持 `Markify path/to/doc.md` 直接打开文档。
            // 注意：自检模式下不解析命令行文档，因为 --selftest-output 的值本身就是个 .txt 路径，
            // 会被误判为要打开的文档（进而把输出报告当成内容读进来）。
            var initial = SelfTest.Requested
                ? null
                : desktop.Args?.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)
                                                     && DocumentFormats.IsSupported(a));

            if (initial is not null && window.DataContext is MainViewModel vm)
                window.Opened += async (_, _) => await vm.LoadAsync(Path.GetFullPath(initial));

            // CI 用：--selftest 时在真实原生二进制里做冒烟验证，然后按结果退出。
            if (SelfTest.Requested)
                SelfTest.Attach(window, desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
