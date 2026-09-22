using System.IO;
using System.Text;
using System.Windows;

namespace JsonFormatter;

public partial class App : Application
{
    private static readonly string CrashLog =
        Path.Combine(Path.GetTempPath(), "jsonformatter-crash.log");

    private static void AppendCrash(string tag, string text)
    {
        try
        {
            File.AppendAllText(CrashLog,
                $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {tag} =====\n{text}\n",
                Encoding.UTF8);
        }
        catch { /* 记录失败也不能阻断 */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppendCrash("UI", args.Exception.ToString());
            // 单个控件模板损坏不拖垮整个应用（已记录日志）
            args.Handled = args.Exception is System.Windows.Markup.XamlParseException;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppendCrash("Domain", args.ExceptionObject.ToString());
        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppendCrash("Task", args.Exception.ToString());

        Settings.Load();
        ThemeManager.Apply(Settings.Data.Theme);

        base.OnStartup(e);
    }
}
