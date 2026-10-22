using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Morphy;

internal sealed class AltTrigger : IDisposable
{
    private readonly Native.HookProc keyboardCallback, mouseCallback;
    private readonly nint keyboardHook, mouseHook;
    private readonly DispatcherTimer hold = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Func<bool> enabled;
    private bool armed, altDown;
    private bool holdElapsed;
    private int generation;
    private int swallowMouseUp;
    private readonly System.Collections.Generic.HashSet<uint> swallowedKeys = [];
    private string? snapshot;
    private nint foreground;
    private Native.Point initial;
    private RadialMenu? menu;
    internal Action<string, OutputFormat>? Convert;

    internal AltTrigger(Func<bool> enabled)
    {
        this.enabled = enabled;
        keyboardCallback = Keyboard; mouseCallback = Mouse;
        keyboardHook = Native.SetWindowsHookEx(13, keyboardCallback, Native.GetModuleHandle(null), 0);
        mouseHook = Native.SetWindowsHookEx(14, mouseCallback, Native.GetModuleHandle(null), 0);
        if (keyboardHook == 0 || mouseHook == 0) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        hold.Tick += (_, _) => { holdElapsed = true; Open(); };
        watch.Tick += (_, _) => { if (menu != null && Native.GetForegroundWindow() != foreground) Close(); };
        watch.Start();
    }
    private nint Keyboard(int code, nint message, nint data)
    {
        if (code < 0) return Native.CallNextHookEx(keyboardHook, code, message, data);
        var key = Marshal.PtrToStructure<Native.Keyboard>(data);
        if ((key.Flags & 0x10) != 0) return Native.CallNextHookEx(keyboardHook, code, message, data);
        bool down = (int)message is 0x100 or 0x104;
        if (!down && swallowedKeys.Remove(key.Key)) return 1;
        if (key.Key == 0xA4)
        {
            if (down && !altDown)
            {
                altDown = true;
                if (menu == null && enabled() && !Native.Down(0x11) && !Native.Down(0x10) && !Native.Down(0x5B) && !Native.Down(0x5C) && !Native.Down(0xA5))
                {
                    foreground = Native.GetForegroundWindow();
                    if (ExplorerSelection.IsExplorer(foreground))
                    {
                        Native.GetCursorPos(out initial); armed = true; snapshot = null; holdElapsed = false;
                        int capture = ++generation; nint window = foreground; var dispatcher = Dispatcher.CurrentDispatcher;
                        var thread = new System.Threading.Thread(() =>
                        {
                            string? selection = ExplorerSelection.Read(window);
                            dispatcher.BeginInvoke(() => { if (!armed || capture != generation || Native.GetForegroundWindow() != window) return; snapshot = selection; if (selection == null) CancelHold(); else if (holdElapsed) Open(); });
                        });
                        thread.IsBackground = true; thread.SetApartmentState(System.Threading.ApartmentState.STA); thread.Start();
                        hold.Start();
                    }
                }
            }
            if (!down)
            {
                altDown = false; CancelHold();
                if (menu != null) menu.ExecuteSelection();
                // The initial Alt-down was passed to Windows. Always pass its
                // matching up, even when ExecuteSelection leaves a menu open.
                // Swallowing only the up leaves Explorer treating clicks as Alt+click.
                return Native.CallNextHookEx(keyboardHook, code, message, data);
            }
            if (menu != null) return 1;
        }
        else if (down)
        {
            CancelHold();
            if (menu != null)
            {
                switch (key.Key)
                {
                    case 0x1B: swallowedKeys.Add(key.Key); Close(); return 1;
                    case 0x0D: swallowedKeys.Add(key.Key); menu.ExecuteSelection(); return 1;
                    case 0x25: case 0x26: swallowedKeys.Add(key.Key); menu.Navigate(-1); return 1;
                    case 0x27: case 0x28: swallowedKeys.Add(key.Key); menu.Navigate(1); return 1;
                    default: Close(); break; // Let Alt+Tab and other system shortcuts continue.
                }
            }
        }
        return Native.CallNextHookEx(keyboardHook, code, message, data);
    }
    private nint Mouse(int code, nint message, nint data)
    {
        if (code < 0) return Native.CallNextHookEx(mouseHook, code, message, data);
        int msg = (int)message;
        var point = Marshal.PtrToStructure<Native.Mouse>(data).Position;
        if (msg == swallowMouseUp) { swallowMouseUp = 0; return 1; }
        if (msg == 0x200)
        {
            if (armed && (Math.Abs(point.X - initial.X) > 24 * Native.ScaleAt(initial) || Math.Abs(point.Y - initial.Y) > 24 * Native.ScaleAt(initial))) CancelHold();
            menu?.Move(point);
        }
        else
        {
            CancelHold();
            if (menu != null)
            {
                if (msg is 0x201 or 0x204 or 0x207 or 0x20B)
                {
                    swallowMouseUp = msg + 1;
                    if (msg == 0x201) menu.Click(point); else Close();
                }
                return 1;
            }
        }
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }
    private void Open()
    {
        hold.Stop();
        if (!armed || !altDown || Native.GetForegroundWindow() != foreground) { CancelHold(); return; }
        if (snapshot == null) return;
        string source = snapshot;
        CancelHold();
        var outputs = Conversion.Outputs(source);
        if (outputs.Length == 0) return;
        menu = new RadialMenu(source, outputs)
        {
            Pick = format => Dispatcher.CurrentDispatcher.BeginInvoke(() => Convert?.Invoke(source, format)),
            Dismiss = Close
        };
        menu.Show();
    }
    private void CancelHold() { hold.Stop(); armed = false; snapshot = null; generation++; }
    internal void Close() { CancelHold(); var old = menu; menu = null; old?.Close(); }
    public void Dispose() { Close(); watch.Stop(); Native.UnhookWindowsHookEx(keyboardHook); Native.UnhookWindowsHookEx(mouseHook); }
}
