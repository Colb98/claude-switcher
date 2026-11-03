using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClaudeSwitcher.Core;
using ClaudeSwitcher.UI;

namespace ClaudeSwitcher;

public partial class App : Application
{
    const string MutexName = @"Local\ClaudeSwitcher.SingleInstance";
    const string ShowEventName = @"Local\ClaudeSwitcher.Show";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    NotificationRouter? _notificationRouter;
    ShellFlashWatcher? _flashWatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        Loc.SetLanguage(ArgValue(args, "--lang") ?? ProfileStore.Load().Language);

        // Shortcut mode: open one profile and exit without showing the manager.
        if (ArgValue(args, "--launch") is { } launchId)
        {
            var profile = ProfileStore.Load().Profiles.FirstOrDefault(p => p.Id == launchId);
            if (profile != null) InstanceManager.Launch(profile);
            Shutdown();
            return;
        }

        // claude:// link handler (see LinkRouter): forward to the right profile and exit.
        if (ArgValue(args, LinkRouter.OpenUrlArg) is { } url)
        {
            try { LinkRouter.Open(url, ProfileStore.Load().Profiles); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Claude Switcher", MessageBoxButton.OK, MessageBoxImage.Error); }
            Shutdown();
            return;
        }

        if (ArgValue(args, "--stop") is { } stopId)
        {
            var profile = ProfileStore.Load().Profiles.FirstOrDefault(p => p.Id == stopId);
            Shutdown(profile != null && InstanceManager.Stop(profile) ? 0 : 1);
            return;
        }

        if (ArgValue(args, "--selftest") is { } reportPath)
        {
            var code = Task.Run(() => SelfTest.Run(reportPath)).GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }

        if (ArgValue(args, "--backup") is { } backupPath)
        {
            Shutdown(Task.Run(() => BackupCli.Backup(backupPath)).GetAwaiter().GetResult());
            return;
        }

        if (ArgValue(args, "--restore") is { } restorePath)
        {
            var to = ArgValue(args, "--to");
            Shutdown(Task.Run(() => BackupCli.Restore(restorePath, to)).GetAwaiter().GetResult());
            return;
        }

        // Screenshot runs are diagnostics and may run beside the user's own instance.
        var screenshot = ArgValue(args, "--screenshot");
        if (screenshot == null)
        {
            _mutex = new Mutex(true, MutexName, out var first);
            if (!first)
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing)) existing.Set();
                Shutdown();
                return;
            }
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, screenshot == null ? ShowEventName : null);

        var startup = args.Contains(StartupManager.StartupArg);
        var window = new MainWindow(startup, screenshot, ArgValue(args, "--theme"), ArgValue(args, "--lang"));
        MainWindow = window;
        if (!(startup && window.Settings.StartMinimized)) window.Show();
        if (screenshot == null)
        {
            _notificationRouter = new NotificationRouter(() => window.Settings);
            _flashWatcher = new ShellFlashWatcher(_notificationRouter.OnWindowFlashed);
        }

        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.Invoke(window.ShowFromTray);
        }) { IsBackground = true };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _flashWatcher?.Dispose();
        _notificationRouter?.Dispose();
        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }

    static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
