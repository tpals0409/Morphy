using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Morphy.Packaging;

namespace Morphy.Setup;

internal static class Program
{
    private static string InstallPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Morphy");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Morphy";
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Morphy.lnk");
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--selftest")) return Test();
            if (args.Contains("--uninstall"))
            {
                if (MessageBox.Show("Morphy를 제거할까요? 개인 설정과 문서 변환 기능은 보관돼요.", "Morphy 제거", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return 0;
                string copy = Path.Combine(Path.GetTempPath(), "Morphy-remove-" + Guid.NewGuid().ToString("N") + ".exe"); File.Copy(Environment.ProcessPath!, copy);
                Process.Start(new ProcessStartInfo(copy, "--remove") { UseShellExecute = false, CreateNoWindow = true }); return 0;
            }
            bool remove = args.Contains("--remove");
            if (!remove && !args.Contains("--update") && MessageBox.Show("Morphy와 문서 변환 기능을 함께 설치할까요?\n\n문서 변환 기능이 없으면 LibreOffice를 공식 서버에서 약 375MB 내려받아요. 인터넷 연결과 2GB 이상의 여유 공간이 필요해요. 관리자 권한은 필요하지 않아요.", "Morphy 설치", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return 0;
            var app = new Application(); var cancel = new CancellationTokenSource();
            var label = new TextBlock { Text = remove ? "Morphy를 제거하는 중…" : "Morphy를 설치하는 중…", FontSize = 18, Margin = new Thickness(0, 0, 0, 20) };
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 12 };
            var button = new Button { Content = "취소", Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Right };
            button.Click += (_, _) => cancel.Cancel(); var content = new StackPanel { Margin = new Thickness(28) }; content.Children.Add(Brand.LogoView()); content.Children.Add(label); content.Children.Add(bar); content.Children.Add(button);
            var window = new Window { Title = "Morphy", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = content };
            Brand.Apply(window);
            bool finished = false; window.Closing += (_, e) => { if (!finished) { cancel.Cancel(); e.Cancel = true; } };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await StopApp(cancel.Token);
                    if (remove) await Task.Run(Remove);
                    else
                    {
                        await Task.Run(() => Install(cancel.Token, (stage, p) => app.Dispatcher.Invoke(() => { label.Text = stage; bar.Value = p * 100; })));
                        Process.Start(new ProcessStartInfo(Path.Combine(InstallPath, "Morphy.exe")) { UseShellExecute = true });
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { MessageBox.Show(e.Message, "Morphy", MessageBoxButton.OK, MessageBoxImage.Error); Environment.ExitCode = 1; }
                finally { finished = true; window.Close(); }
            };
            app.Run(window); return Environment.ExitCode;
        }
        catch (Exception e) { Console.Error.WriteLine(e); if (!args.Contains("--selftest")) MessageBox.Show(e.Message, "Morphy 설치 오류"); return 1; }
    }
    private static async Task StopApp(CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", "Morphy.Control." + Environment.UserName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(1500, token);
            string? reply = await RequestStop(pipe, token);
            if (reply != "ok") throw new IOException("Morphy에서 진행 중인 작업을 마친 뒤 다시 설치해 주세요.");
        }
        catch (TimeoutException) { }
        using var mutex = new Mutex(false, @"Local\Morphy.Windows");
        bool acquired; try { acquired = mutex.WaitOne(10000); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Morphy를 종료한 뒤 다시 시도해 주세요."); mutex.ReleaseMutex();
    }
    private static async Task<string?> RequestStop(Stream pipe, CancellationToken token)
    {
        // The pipe owns its lifetime. Disposing the reader must not close it
        // before the writer performs its final flush.
        using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await writer.WriteLineAsync("quit");
        return await reader.ReadLineAsync(deadline.Token);
    }
    private static void Install(CancellationToken token, Action<string, double> progress)
    {
        string staging = InstallPath + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new IOException("설치 자료가 없어요.");
            Bundle.Extract(payload, staging, token, p => progress("Morphy를 준비하는 중…", p)); token.ThrowIfCancellationRequested();
            PrepareEngine(Path.Combine(staging, "Morphy.exe"), token, progress).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested(); progress("설치를 마치는 중…", 1);
            File.Copy(Environment.ProcessPath!, Path.Combine(staging, "MorphySetup.exe"));
            File.WriteAllText(Path.Combine(staging, ".morphy-install"), "Morphy Windows");
            Bundle.Activate(staging, InstallPath, Register);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static async Task PrepareEngine(string app, CancellationToken token, Action<string, double> progress)
    {
        token.ThrowIfCancellationRequested(); progress("문서 변환 기능을 준비하는 중…", 0);
        var info = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--install-engine");
        using var child = Process.Start(info) ?? throw new IOException("문서 변환 기능 설치를 시작하지 못했어요.");
        var errors = new System.Text.StringBuilder();
        Task stderr = Task.Run(async () => { while (await child.StandardError.ReadLineAsync() is { } line) if (errors.Length < 8192) errors.AppendLine(line); });
        Task stdout = Task.Run(async () =>
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                using var json = System.Text.Json.JsonDocument.Parse(line);
                progress("문서 변환 · " + json.RootElement.GetProperty("stage").GetString(), Math.Clamp(json.RootElement.GetProperty("fraction").GetDouble(), 0, 1));
            }
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(45));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            // Ask the downloader to clean up its temporary files before forcing exit.
            try { await child.StandardInput.WriteLineAsync("cancel"); await child.StandardInput.FlushAsync(); } catch (IOException) { }
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await child.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException) { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested(); throw new TimeoutException("문서 변환 기능 설치 시간이 초과됐어요. 인터넷 연결을 확인한 뒤 다시 설치해 주세요.");
        }
        await Task.WhenAll(stdout, stderr); token.ThrowIfCancellationRequested();
        if (child.ExitCode != 0) throw new IOException("문서 변환 기능을 준비하지 못해 앱 설치를 완료하지 않았어요. 다시 실행하면 재시도할 수 있어요.\n" + errors);
    }
    private static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Morphy"); key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version!.ToString(3));
        key.SetValue("InstallLocation", InstallPath); key.SetValue("Publisher", "Morphy"); key.SetValue("DisplayIcon", Path.Combine(InstallPath, "Morphy.exe"));
        key.SetValue("UninstallString", "\"" + Path.Combine(InstallPath, "MorphySetup.exe") + "\" --uninstall"); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        object? shell = null, link = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); link = ((dynamic)shell!).CreateShortcut(Shortcut); ((dynamic)link).TargetPath = Path.Combine(InstallPath, "Morphy.exe"); ((dynamic)link).WorkingDirectory = InstallPath; ((dynamic)link).Save(); }
        finally { if (link != null) Marshal.ReleaseComObject(link); if (shell != null) Marshal.ReleaseComObject(shell); }
        using var startup = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (startup?.GetValue("Morphy") != null) startup.SetValue("Morphy", "\"" + Path.Combine(InstallPath, "Morphy.exe") + "\" --background");
    }
    private static void Remove()
    {
        string resolved = Path.GetFullPath(InstallPath);
        if (!File.Exists(Path.Combine(resolved, ".morphy-install"))) throw new IOException("Morphy 설치 폴더를 확인할 수 없어 제거를 중단했어요.");
        Directory.Delete(resolved, true); if (File.Exists(Shortcut)) File.Delete(Shortcut); Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        using var startup = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); startup?.DeleteValue("Morphy", false);
    }
    private static int Test()
    {
        TestStopRequest().GetAwaiter().GetResult();
        string root = Path.Combine(Path.GetTempPath(), "Morphy-setup-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string staging = Path.Combine(root, "staging"), target = Path.Combine(root, "installed"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "old.txt"), "old");
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")!; Bundle.Extract(payload, staging, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MORPHY_ENGINE_PATH")))
                PrepareEngine(Path.Combine(staging, "Morphy.exe"), CancellationToken.None, (_, _) => { }).GetAwaiter().GetResult();
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool rejected = false;
                try { PrepareEngine(Path.Combine(staging, "Morphy.exe"), cancelled.Token, (_, _) => { }).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { rejected = true; }
                if (!rejected) throw new IOException("Engine setup ignored cancellation");
            }
            bool rolled = false; try { Bundle.Activate(staging, target, () => throw new IOException("simulated registration failure")); } catch (IOException) { rolled = File.Exists(Path.Combine(target, "old.txt")); }
            if (!rolled) throw new IOException("Installer rollback failed");
            using var again = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")!; Bundle.Extract(again, staging, CancellationToken.None); Bundle.Activate(staging, target, () => { });
            if (!File.Exists(Path.Combine(target, "Morphy.exe")) || File.Exists(Path.Combine(target, "old.txt"))) throw new IOException("Installer activation failed");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "setup-selftest.txt"), "PASS: duplex pipe shutdown/disposal, payload hashes, extraction, rollback, atomic activation"); return 0;
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task TestStopRequest()
    {
        foreach (string reply in new[] { "ok", "busy" })
        {
            string name = "Morphy-setup-test-" + Guid.NewGuid().ToString("N");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var serving = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(server, System.Text.Encoding.UTF8, true, 1024, true);
                using var writer = new StreamWriter(server, new System.Text.UTF8Encoding(false), 1024, true) { AutoFlush = true };
                if (await reader.ReadLineAsync(timeout.Token) != "quit") throw new IOException("Unexpected shutdown command");
                await writer.WriteLineAsync(reply);
            });
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            if (await RequestStop(client, timeout.Token) != reply) throw new IOException("Shutdown response mismatch");
            if (!client.CanRead || !client.CanWrite) throw new IOException("Text wrappers closed the owning pipe");
            await serving;
        }
    }
}
