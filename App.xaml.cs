using System.Threading;
using System.Windows;

namespace LanChat;

public partial class App : Application
{
    private static int _crashRecorded;
    private static string _lastUiErrorKey = string.Empty;
    private static DateTime _lastUiErrorAt = DateTime.MinValue;

    public App()
    {
        AppLog.Write("程序启动。");
        AttachmentStore.PurgeStale();

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Window_Loaded));

        DispatcherUnhandledException += (_, e) =>
        {
            var exception = e.Exception;
            AppLog.Write($"未处理的 UI 异常：{exception.GetType().Name}: {exception.Message}");

            // 内存耗尽、访问冲突这类异常继续执行只会放大问题，记录后照常退出。
            if (exception is OutOfMemoryException or AccessViolationException or BadImageFormatException or InsufficientExecutionStackException)
            {
                ReportCrash(exception, "UI 线程致命异常");
                e.Handled = false;
                return;
            }

            // 其余异常尽量保住程序：写入日志并恢复，避免用户聊到一半直接闪退。
            var key = exception.GetType().FullName + "|" + exception.Message;
            var now = DateTime.UtcNow;
            var repeated = key == _lastUiErrorKey && (now - _lastUiErrorAt).TotalSeconds < 5;
            _lastUiErrorKey = key;
            _lastUiErrorAt = now;
            if (!repeated) AppLog.SaveCrashLog(exception, "UI 线程未处理异常（已恢复运行）", fatal: false);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            AppLog.Write($"未处理的运行时异常：{exception?.Message ?? e.ExceptionObject?.ToString() ?? "未知"}");
            ReportCrash(exception, $"非 UI 线程未处理异常（IsTerminating={e.IsTerminating}）");
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.SaveRuntimeLog();

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"未观察到的后台任务异常：{e.Exception.GetBaseException().Message}");
            AppLog.SaveCrashLog(e.Exception, "未观察到的后台任务异常（已忽略）", fatal: false);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Apply(AppSettings.DarkMode);
        base.OnStartup(e);
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) ThemeManager.ApplyWindowChrome(window);
    }

    /// <summary>
    /// UI 线程异常会再触发一次 AppDomain 处理器，这里只记录第一次，
    /// 避免同一崩溃生成两份报告并把来源标错。
    /// </summary>
    private static void ReportCrash(Exception? exception, string source)
    {
        if (Interlocked.Exchange(ref _crashRecorded, 1) == 0) AppLog.SaveCrashLog(exception, source);
        else AppLog.Write($"崩溃处理补充：{source}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.SaveRuntimeLog();
        base.OnExit(e);
    }
}
