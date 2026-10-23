using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Morphy;

// Track physical modifier events: hook callbacks run before Windows updates key state.
internal sealed class ShortcutChord
{
    private readonly System.Collections.Generic.HashSet<uint> held = [];
    private bool active;
    internal bool Claimed { get; private set; }
    internal static bool IsModifier(uint key) => key is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
    internal void Seed(uint key) => held.Add(key);
    // 1: open, -1: release selection, 0: no action.
    internal int Update(uint key, bool down, bool eligible)
    {
        if (down) held.Add(key); else held.Remove(key);
        bool chord = (held.Contains(0x11) || held.Contains(0xA2) || held.Contains(0xA3)) &&
            (held.Contains(0x12) || held.Contains(0xA4) || held.Contains(0xA5));
        if (chord == active) return 0;
        active = chord;
        if (chord) { Claimed = eligible; return eligible ? 1 : 0; }
        bool claimed = Claimed; Claimed = false; return claimed ? -1 : 0;
    }
}

internal sealed class ShortcutTrigger : IDisposable
{
    private readonly Native.HookProc keyboardCallback, mouseCallback;
    private readonly nint keyboardHook, mouseHook;
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Func<bool> enabled;
    private readonly ShortcutChord shortcut = new();
    private readonly System.Collections.Generic.HashSet<uint> swallowedKeys = [];
    private int generation, swallowMouseUp;
    private bool pending;
    private nint foreground;
    private RadialMenu? menu;
    internal Action<string, OutputFormat>? Convert;

    internal ShortcutTrigger(Func<bool> enabled)
    {
        this.enabled = enabled;
        foreach (uint key in new uint[] { 0xA2, 0xA3, 0xA4, 0xA5 })
            if (Native.Down((int)key)) shortcut.Seed(key);
        keyboardCallback = Keyboard; mouseCallback = Mouse;
        keyboardHook = Native.SetWindowsHookEx(13, keyboardCallback, Native.GetModuleHandle(null), 0);
        mouseHook = Native.SetWindowsHookEx(14, mouseCallback, Native.GetModuleHandle(null), 0);
        if (keyboardHook == 0 || mouseHook == 0) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        watch.Tick += (_, _) => { if ((menu != null || pending) && Native.GetForegroundWindow() != foreground) Close(); };
        watch.Start();
    }
    private nint Keyboard(int code, nint message, nint data)
    {
        if (code < 0) return Native.CallNextHookEx(keyboardHook, code, message, data);
        var key = Marshal.PtrToStructure<Native.Keyboard>(data);
        if ((key.Flags & 0x10) != 0) return Native.CallNextHookEx(keyboardHook, code, message, data);
        bool down = (int)message is 0x100 or 0x104;
        if (ShortcutChord.IsModifier(key.Key))
        {
            nint window = Native.GetForegroundWindow();
            bool eligible = enabled() && ExplorerSelection.IsExplorer(window) &&
                !Native.Down(0x10) && !Native.Down(0x5B) && !Native.Down(0x5C);
            int action = shortcut.Update(key.Key, down, eligible);
            if (action == 1 && menu == null && !pending) Capture(window);
            else if (action == -1)
            {
                CancelCapture(); // A late lookup must not open a menu after release.
                menu?.ExecuteSelection();
            }
            // Forward both modifier down and up to avoid stuck Ctrl/Alt and preserve system chords.
            return Native.CallNextHookEx(keyboardHook, code, message, data);
        }
        if (!down && swallowedKeys.Remove(key.Key)) return 1;
        if (down)
        {
            CancelCapture();
            if (menu != null)
            {
                switch (key.Key)
                {
                    case 0x1B: swallowedKeys.Add(key.Key); Close(); return 1;
                    case 0x0D: swallowedKeys.Add(key.Key); menu.ExecuteSelection(); return 1;
                    case 0x25: case 0x26: swallowedKeys.Add(key.Key); menu.Navigate(-1); return 1;
                    case 0x27: case 0x28: swallowedKeys.Add(key.Key); menu.Navigate(1); return 1;
                    default: Close(); break; // Preserve all system shortcuts, including Alt.
                }
            }
        }
        return Native.CallNextHookEx(keyboardHook, code, message, data);
    }
    private void Capture(nint window)
    {
        foreground = window; pending = true;
        int capture = ++generation; var dispatcher = Dispatcher.CurrentDispatcher;
        var thread = new System.Threading.Thread(() =>
        {
            string? selection = ExplorerSelection.Read(window);
            dispatcher.BeginInvoke(() =>
            {
                if (!pending || capture != generation) return;
                CancelCapture();
                if (!shortcut.Claimed || !enabled() || selection == null || Native.GetForegroundWindow() != window) return;
                var outputs = Conversion.Outputs(selection); if (outputs.Length == 0) return;
                menu = new RadialMenu(selection, outputs)
                {
                    Pick = format => dispatcher.BeginInvoke(() => Convert?.Invoke(selection, format)),
                    Dismiss = Close
                };
                menu.Show();
            });
        });
        thread.IsBackground = true; thread.SetApartmentState(System.Threading.ApartmentState.STA); thread.Start();
    }
    private nint Mouse(int code, nint message, nint data)
    {
        if (code < 0) return Native.CallNextHookEx(mouseHook, code, message, data);
        int msg = (int)message;
        var point = Marshal.PtrToStructure<Native.Mouse>(data).Position;
        if (msg == swallowMouseUp) { swallowMouseUp = 0; return 1; }
        if (msg == 0x200) menu?.Move(point);
        else
        {
            CancelCapture();
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
    private void CancelCapture() { pending = false; generation++; }
    internal void Close() { CancelCapture(); var old = menu; menu = null; old?.Close(); }
    public void Dispose() { Close(); watch.Stop(); Native.UnhookWindowsHookEx(keyboardHook); Native.UnhookWindowsHookEx(mouseHook); }
}
