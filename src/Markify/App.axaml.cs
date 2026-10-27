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
            var initial = desktop.Args?.FirstOrDefault(DocumentFormats.IsSupported);
            if (initial is not null && window.DataContext is MainViewModel vm)
                window.Opened += async (_, _) => await vm.LoadAsync(Path.GetFullPath(initial));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
