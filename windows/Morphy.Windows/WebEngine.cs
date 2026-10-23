using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Morphy;

// HTML → paginated A4 PDF or one full-page JPG, drawn by WebView2 (Edge's engine, built into Windows 11). LibreOffice
// reads HTML as a word-processor document and loses modern layout, so it is not used here (DECISIONS.md D11).
// Offline by design: every request that isn't a local file gets a 403, and navigation away from the file is cancelled.
internal static class WebEngine
{
    internal const int Width = 1024;            // CSS px, same as the Mac app
    internal const int MaxJpgHeight = 16_000;   // CSS px (32,000 px at 2x); longer pages should go to PDF
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    internal static void Convert(string source, OutputFormat format, string target, CancellationToken token)
    {
        string head = File.ReadAllText(source, Encoding.UTF8);
        if (!head.Contains('<')) throw new IOException("확장자와 실제 형식이 다르거나 손상된 HTML 파일이에요.");
        // WebView2 needs an STA thread with a message loop and a window handle; the worker process has neither by default.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            RenderAsync(source, format, target, token).ContinueWith(t =>
            {
                failure = t.Exception?.GetBaseException();
                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(Timeout + TimeSpan.FromSeconds(10))) throw new IOException("페이지를 그리는 데 시간이 너무 오래 걸려요.");
        if (failure is WebView2RuntimeNotFoundException) throw new IOException("Microsoft Edge WebView2 런타임이 필요해요. Windows 업데이트를 확인해 주세요.");
        if (failure != null) throw failure as IOException ?? new IOException("HTML을 변환하지 못했어요. " + failure.Message);
    }

    private static async Task RenderAsync(string source, OutputFormat format, string target, CancellationToken token)
    {
        string profile = Path.Combine(Path.GetTempPath(), "morphy-webview-" + Guid.NewGuid().ToString("N"));
        // A tool window far off screen: WebView2 renders into it, but it never shows, takes focus or reaches the taskbar.
        var window = new HwndSource(new HwndSourceParameters("Morphy")
        {
            WindowStyle = unchecked((int)0x90000000),   // WS_POPUP | WS_VISIBLE
            ExtendedWindowStyle = 0x08000080,           // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            PositionX = -32000, PositionY = -32000, Width = Width, Height = 800,
        });
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            controller = await environment.CreateCoreWebView2ControllerAsync(window.Handle);
            controller.Bounds = new System.Drawing.Rectangle(0, 0, Width, 800);
            controller.IsVisible = true;
            var web = controller.CoreWebView2;
            web.Settings.AreDefaultScriptDialogsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.IsWebMessageEnabled = false;
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.DownloadStarting += (_, e) => e.Cancel = true;
            web.NavigationStarting += (_, e) => { if (!IsLocal(e.Uri)) e.Cancel = true; };
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (_, e) =>
            {
                if (!IsLocal(e.Request.Uri)) e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked by Morphy", "");
            };
            // Same layout width on every screen and DPI.
            await web.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                JsonSerializer.Serialize(new { width = Width, height = 800, deviceScaleFactor = 1, mobile = false }));

            var loaded = new TaskCompletionSource<bool>();
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            web.Navigate(new Uri(source).AbsoluteUri);
            if (await Task.WhenAny(loaded.Task, Task.Delay(Timeout, token)) != loaded.Task || !loaded.Task.Result)
                throw new IOException("HTML 파일을 열 수 없어요. 원본 파일을 확인해 주세요.");
            await Task.Delay(500, token);  // let scripts and web fonts settle

            if (format.Extension == "pdf")
            {
                var settings = environment.CreatePrintSettings();
                settings.PageWidth = 8.27;   // A4, inches
                settings.PageHeight = 11.69;
                settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0.5;
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                if (!await web.PrintToPdfAsync(target, settings)) throw new IOException("PDF를 만들지 못했어요. 다시 시도할 수 있어요.");
                return;
            }
            using var metrics = JsonDocument.Parse(await web.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
            double height = Math.Max(100, metrics.RootElement.GetProperty("cssContentSize").GetProperty("height").GetDouble());
            if (height > MaxJpgHeight) throw new IOException("페이지가 너무 길어서 JPG 한 장에 담을 수 없어요. PDF로 바꿔 주세요.");
            string shot = await web.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", JsonSerializer.Serialize(new
            {
                format = "jpeg", quality = 90, captureBeyondViewport = true, fromSurface = true,
                clip = new { x = 0, y = 0, width = Width, height = Math.Ceiling(height), scale = 2 },   // 2x for sharp text
            }));
            using var image = JsonDocument.Parse(shot);
            File.WriteAllBytes(target, System.Convert.FromBase64String(image.RootElement.GetProperty("data").GetString()!));
        }
        finally
        {
            controller?.Close();
            window.Dispose();
            try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }  // browser may still hold it
        }
    }

    private static bool IsLocal(string uri) =>
        uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
}
