using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Linq;

namespace Doodly;

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
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            var filePath = desktop.Args?.FirstOrDefault(argument => !argument.StartsWith('-'));
            if (filePath is not null)
                mainWindow.OpenImage(filePath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}