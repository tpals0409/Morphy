using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Morphy;

internal static class ExplorerSelection
{
    internal static bool IsExplorer(nint hwnd) => Native.ClassName(hwnd) is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";

    // Called from the dispatcher, never inside the low-level keyboard callback.
    internal static string? Read(nint foreground)
    {
        if (!IsExplorer(foreground)) return null;
        object? shell = null, windows = null, window = null, document = null, selection = null, item = null;
        try
        {
            var focus = AutomationElement.FocusedElement;
            if (focus != null && (focus.Current.ControlType == ControlType.Edit || focus.Current.ControlType == ControlType.Document)) return null;
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            windows = ((dynamic)shell!).Windows();
            if (Native.ClassName(foreground) is "Progman" or "WorkerW")
            {
                object location = 0, root = 0;
                int hwnd = 0;
                window = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out hwnd, 1);
            }
            else
            {
                int count = ((dynamic)windows).Count;
                for (int i = 0; i < count; i++)
                {
                    object candidate = ((dynamic)windows).Item(i);
                    bool match = (nint)(long)((dynamic)candidate).HWND == foreground && IsVisibleTab(candidate);
                    if (match) { window = candidate; break; }
                    Release(candidate);
                }
            }
            if (window == null) return null;
            document = ((dynamic)window).Document;
            selection = ((dynamic)document).SelectedItems();
            if ((int)((dynamic)selection).Count != 1) return null;
            item = ((dynamic)selection).Item(0);
            string path = ((dynamic)item).Path;
            return File.Exists(path) && !Directory.Exists(path) ? path : null;
        }
        catch (Exception e) { Settings.Log($"Selection: {e.Message}"); return null; }
        finally { Release(item); Release(selection); Release(document); Release(window); Release(windows); Release(shell); }
    }

    private static bool IsVisibleTab(object window)
    {
        object? browser = null;
        try
        {
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iid = new Guid("000214E2-0000-0000-C000-000000000046");
            int hr = ((IServiceProvider)window).QueryService(ref service, ref iid, out browser);
            if (hr != 0 || browser == null) return false;
            return ((IOleWindow)browser).GetWindow(out nint hwnd) == 0 && Native.IsWindowVisible(hwnd);
        }
        finally { Release(browser); }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value); }
    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow { [PreserveSig] int GetWindow(out nint hwnd); [PreserveSig] int ContextSensitiveHelp(bool enter); }
}
