using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClaudeSwitcher.Core;

namespace ClaudeSwitcher;

public partial class App : Application
{
    const string MutexName = @"Local\ClaudeSwitcher.SingleInstance";
    const string ShowEventName = @"Local\ClaudeSwitcher.Show";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // Shortcut mode: open one profile and exit without showing the manager.
        if (ArgValue(args, "--launch") is { } launchId)
        {
            var profile = ProfileStore.Load().Profiles.FirstOrDefault(p => p.Id == launchId);
            if (profile != null) InstanceManager.Launch(profile);
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
        var window = new MainWindow(startup, screenshot, ArgValue(args, "--theme"));
        MainWindow = window;
        if (!(startup && window.Settings.StartMinimized)) window.Show();

        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.Invoke(window.ShowFromTray);
        }) { IsBackground = true };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
