using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace JsonFormatter;

public class App : Application
{
    public static string CrashLog =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JsonFormatter", "crash.log");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            WriteCrash($"[UI] {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash($"[Domain] {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            WriteCrash($"[Task] {e.Exception}");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Settings.Load();
            ThemeManager.Apply(Settings.Data.Theme, applyResources: true);
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void WriteCrash(string text)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(CrashLog)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(CrashLog, $"{DateTime.Now:HH:mm:ss} {text}\n");
        }
        catch { }
    }
}
