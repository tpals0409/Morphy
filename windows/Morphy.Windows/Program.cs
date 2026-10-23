using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Morphy;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) { Settings.Log(e.ToString()); Console.Error.WriteLine(e); if (args.Length == 0 || args.Contains("--check-updates")) MessageBox.Show(e.Message, "Morphy", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
    }
    private static int Run(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--convert")
        {
            var format = Conversion.Formats.Single(f => f.Extension == args[2].ToLowerInvariant());
            string folder = args.Length > 3 ? args[3] : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(args[1]))!;
            foreach (string file in ConversionWorker.Run(System.IO.Path.GetFullPath(args[1]), format, folder, CancellationToken.None).GetAwaiter().GetResult()) Console.WriteLine(file);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--install-engine")
        {
            using var cancellation = new CancellationTokenSource();
            _ = System.Threading.Tasks.Task.Run(async () => { if (await Console.In.ReadLineAsync() == "cancel") cancellation.Cancel(); });
            EngineInstaller.Install(new EngineProgress(), cancellation.Token).GetAwaiter().GetResult(); return 0;
        }
        if (args.Length == 2 && args[0] == "--worker") return ConversionWorker.Execute(args[1]);
        if (args.Length == 3 && args[0] == "--extract-engine")
        {
            EngineInstaller.VerifySignature(args[1], CancellationToken.None).GetAwaiter().GetResult();
            using var archive = new MsiExtractor(args[1]); archive.Extract(args[2], CancellationToken.None);
            string executable = System.IO.Directory.GetFiles(args[2], "soffice.com", System.IO.SearchOption.AllDirectories).Single();
            EngineInstaller.CopyRuntime(args[2], System.IO.Path.GetDirectoryName(executable)!); return 0;
        }
        if (args.Contains("--selftest")) return SelfTest.Run();
        if (args.Length == 2 && args[0] == "--render-menu") { RadialMenu.RenderPreview(args[1]); return 0; }
        using var singleton = new Mutex(true, @"Local\Morphy.Windows", out bool created);
        bool checkUpdates = args.Contains("--check-updates");
        if (!created)
        {
            if (checkUpdates) ControlServer.RequestUpdateCheck().GetAwaiter().GetResult();
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Settings.Log(e.Exception.ToString());
            MessageBox.Show(e.Exception.Message, "Morphy", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        bool smoke = args.Contains("--smoke-test");
        using var tray = new TrayApp(smoke, checkUpdates);
        if (smoke)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); app.Shutdown(); };
            timer.Start();
        }
        app.Run();
        return 0;
    }
    private sealed class EngineProgress : IProgress<(string Stage, double Fraction)>
    {
        public void Report((string Stage, double Fraction) value)
        {
            lock (Console.Out) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { stage = value.Stage, fraction = value.Fraction }));
        }
    }
}
