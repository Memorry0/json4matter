using System.IO;
using System.Windows;

namespace JsonFormatter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "jsonformatter-crash.log"),
                $"UI: {args.Exception}");
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "jsonformatter-crash.log"),
                $"Domain: {args.ExceptionObject}");
        };

        Settings.Load();
        ThemeManager.Apply(Settings.Data.Theme);

        base.OnStartup(e);
    }
}
