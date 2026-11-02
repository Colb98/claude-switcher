using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClaudeSwitcher.UI;

/// <summary>Reports windows whose taskbar button starts flashing, through a hidden window that gets shell messages.</summary>
public sealed class ShellFlashWatcher : IDisposable
{
    const int WsPopup = unchecked((int)0x80000000);
    /// <summary>HSHELL_FLASH.</summary>
    const long ShellWindowFlashed = 0x8006;

    readonly Action<IntPtr> _onFlash;
    readonly HwndSource _window;
    readonly uint _shellMessage;

    public ShellFlashWatcher(Action<IntPtr> onFlash)
    {
        _onFlash = onFlash;
        _shellMessage = RegisterWindowMessage("SHELLHOOK");
        _window = new HwndSource(new HwndSourceParameters("ClaudeSwitcher.ShellHook") { WindowStyle = WsPopup });
        _window.AddHook(OnMessage);
        RegisterShellHookWindow(_window.Handle);
    }

    public void Dispose()
    {
        DeregisterShellHookWindow(_window.Handle);
        _window.Dispose();
    }

    IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == _shellMessage && wParam.ToInt64() == ShellWindowFlashed) _onFlash(lParam);
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr hwnd);
}
