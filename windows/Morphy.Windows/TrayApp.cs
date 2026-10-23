using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Morphy;

internal sealed class TrayApp : IDisposable
{
    private readonly Settings settings = Settings.Load();
    private readonly Forms.NotifyIcon tray;
    private readonly ShortcutTrigger trigger;
    private readonly List<Job> jobs = [];
    private bool running;
    private bool maintenance;
    private bool checkingUpdate;
    private CancellationTokenSource? currentCancellation;
    private Task<bool>? engineTask;
    private readonly ControlServer control;
    private Window? guide;
    private sealed class Job(string source, OutputFormat format, string destination)
    {
        internal readonly string Source = source, Destination = destination;
        internal readonly OutputFormat Format = format;
        internal string State = "대기", Error = "";
        internal string[] Results = [];
    }
    internal TrayApp(bool smoke = false, bool checkUpdates = false)
    {
        tray = new Forms.NotifyIcon { Text = "Morphy " + Updates.Current, Icon = CreateIcon(), Visible = true };
        trigger = new ShortcutTrigger(() => !maintenance) { Convert = Enqueue };
        control = new ControlServer(() => !running && !maintenance && engineTask == null, () => _ = CheckUpdates(true));
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowGuide(); };
        Refresh();
        if (!smoke && !settings.OnboardingShown) Application.Current.Dispatcher.BeginInvoke(ShowGuide);
        if (!smoke)
        {
            ConfigureTaskbar();
            _ = CheckUpdates(checkUpdates);
        }
    }
    private static void ConfigureTaskbar()
    {
        try
        {
            var list = new System.Windows.Shell.JumpList();
            list.JumpItems.Add(new System.Windows.Shell.JumpTask
            {
                Title = "업데이트 확인…", Description = "Morphy의 새 버전을 확인합니다.",
                ApplicationPath = Environment.ProcessPath!, Arguments = "--check-updates",
                WorkingDirectory = AppContext.BaseDirectory,
                IconResourcePath = Environment.ProcessPath!, IconResourceIndex = 0
            });
            System.Windows.Shell.JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch (Exception e) { Settings.Log("Taskbar menu: " + e); }
    }
    private static System.Drawing.Icon CreateIcon()
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Morphy.Brand.ico")!;
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }
    private void ShowGuide()
    {
        if (guide != null) { guide.Activate(); return; }
        var panel = new StackPanel { Margin = new Thickness(32) };
        panel.Children.Add(Brand.LogoView(72));
        panel.Children.Add(new TextBlock { Text = "파일 변환, 손끝에서.", FontSize = 28, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 24) });
        foreach (string line in new[] { "1   탐색기에서 파일 하나를 선택하세요.", "2   Ctrl+Alt를 누른 채 원하는 형식으로 이동하세요.", "3   Ctrl 또는 Alt에서 손을 떼면 변환돼요." }) panel.Children.Add(new TextBlock { Text = line, FontSize = 16, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = "가운데나 바깥에서 놓으면 취소돼요. Esc로도 취소할 수 있어요.\n클릭이나 방향키·Enter로도 실행할 수 있어요.\n원본은 그대로, 결과는 같은 폴더에 저장돼요.", FontSize = 13, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "이미지 · PDF · Word · Excel · PowerPoint를 변환해요.\n설치가 끝나면 모든 변환 기능을 바로 사용할 수 있어요.\n파일은 외부로 보내지 않아요.", FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        var logs = new Button { Content = "진단 로그 열기", Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Left };
        logs.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(Settings.DirectoryPath); Launch(Settings.DirectoryPath); }
            catch (Exception e) { MessageBox.Show(e.Message, "Morphy"); }
        };
        panel.Children.Add(logs);
        var button = new Button { Content = "시작하기", Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 24, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) => { settings.OnboardingShown = true; settings.Save(); guide?.Close(); };
        panel.Children.Add(button);
        guide = new Window { Title = "Morphy", Width = 490, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel, Background = Brushes.White };
        Brand.Apply(guide);
        guide.Closed += (_, _) => guide = null;
        guide.Show();
    }
    private async void Enqueue(string source, OutputFormat format)
    {
        if (maintenance) return;
        try
        {
        Conversion.CheckInput(source);
        if (Conversion.NeedsEngine(source, format) && EngineInstaller.Find() == null && !await EnsureEngine()) return;
        string folder = Path.GetDirectoryName(source)!;
        try
        {
            string probe = Path.Combine(folder, $".morphy-probe-{Guid.NewGuid():N}");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            using var picker = new Forms.FolderBrowserDialog { Description = "원본 폴더에 저장할 수 없어요. 결과를 저장할 폴더를 선택해 주세요.", UseDescriptionForTitle = true };
            if (picker.ShowDialog() != Forms.DialogResult.OK) return;
            folder = picker.SelectedPath;
        }
        jobs.Add(new Job(source, format, folder));
        Refresh(); _ = Pump();
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Morphy", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async Task Pump()
    {
        if (running) return;
        running = true;
        try
        {
            while (jobs.FirstOrDefault(j => j.State == "대기") is { } job)
            {
                job.State = "변환 중"; Refresh();
                currentCancellation = new CancellationTokenSource();
                try
                {
                    job.Results = await ConversionWorker.Run(job.Source, job.Format, job.Destination, currentCancellation.Token);
                    job.State = "완료";
                    tray.ShowBalloonTip(4000, "변환 완료", job.Results.Length == 1 ? Path.GetFileName(job.Results[0]) : $"{job.Results.Length}개 파일을 저장했어요.", Forms.ToolTipIcon.Info);
                }
                catch (OperationCanceledException) { job.State = "취소됨"; }
                catch (Exception e)
                {
                    job.State = "실패"; job.Error = e.Message; Settings.Log(e.ToString());
                    tray.ShowBalloonTip(6000, "파일을 변환하지 못했어요", e.Message, Forms.ToolTipIcon.Error);
                }
                finally { currentCancellation.Dispose(); currentCancellation = null; }
                Refresh();
            }
        }
        finally { running = false; Refresh(); }
    }
    private static Task<T> RunSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() => { try { completion.SetResult(action()); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(System.Threading.ApartmentState.STA); thread.IsBackground = true; thread.Start();
        return completion.Task;
    }
    private void Refresh()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Morphy " + Updates.Current).Enabled = false;
        menu.Items.Add("사용 방법", null, (_, _) => ShowGuide());
        var update = menu.Items.Add("업데이트 확인…", null, async (_, _) => await CheckUpdates(true)); update.Enabled = !checkingUpdate && !running && !maintenance && engineTask == null;
        menu.Items.Add(new Forms.ToolStripSeparator());
        var startup = new Forms.ToolStripMenuItem("로그인 시 실행") { Checked = Settings.AutoStart };
        startup.Click += (_, _) => { try { Settings.AutoStart = !Settings.AutoStart; Refresh(); } catch (Exception e) { MessageBox.Show(e.Message, "Morphy"); } };
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = menu.Items.Add("Morphy 종료", null, (_, _) => Application.Current.Shutdown());
        exit.Enabled = !running && !maintenance && engineTask == null;
        var previous = tray.ContextMenuStrip; tray.ContextMenuStrip = menu;
        if (previous != null) Application.Current.Dispatcher.BeginInvoke(previous.Dispose, DispatcherPriority.Background);
    }
    private static void Launch(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { MessageBox.Show(e.Message, "Morphy"); } }
    private async Task<bool> EnsureEngine()
    {
        if (engineTask != null) return await engineTask;
        if (MessageBox.Show("문서 변환 기능을 추가할까요? 약 375MB를 내려받고 2GB의 여유 공간이 필요해요.\n\nLibreOffice (The Document Foundation, MPL-2.0)를 공식 서버에서 받아 사용해요. 파일은 업로드하지 않아요.", "Morphy", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;
        engineTask = InstallEngine(); Refresh();
        try { return await engineTask; } finally { engineTask = null; Refresh(); }
    }
    private async Task<bool> InstallEngine()
    {
        using var cancel = new CancellationTokenSource(); var window = new OperationWindow("문서 변환 기능 추가", cancel); window.Show();
        try { await EngineInstaller.Install(new Progress<(string Stage, double Fraction)>(p => window.Update(p.Stage, p.Fraction)), cancel.Token); return true; }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) { Settings.Log(e.ToString()); MessageBox.Show(e.Message, "Morphy", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        finally { window.Finish(); }
    }
    private async Task CheckUpdates(bool interactive)
    {
        if (interactive && (running || maintenance || engineTask != null))
        {
            MessageBox.Show("진행 중인 작업이 끝난 뒤 업데이트를 확인해 주세요.", "Morphy 업데이트");
            return;
        }
        if (interactive && checkingUpdate) return;
        if (interactive) { checkingUpdate = true; trigger.Close(); Refresh(); }
        try
        {
            var update = await Updates.Check(CancellationToken.None);
            if (update == null) { if (interactive) MessageBox.Show("현재 사용할 수 있는 새 Windows 버전이 없어요.", "Morphy"); return; }
            if (!interactive) { tray.ShowBalloonTip(5000, "Morphy 업데이트", $"{update.Version} 버전이 있어요. 트레이 메뉴에서 업데이트할 수 있어요.", Forms.ToolTipIcon.Info); return; }
            if (running || maintenance || engineTask != null) return;
            if (MessageBox.Show($"Morphy {update.Version} 버전을 내려받고 설치할까요? 설치가 끝나면 앱을 다시 열어요.", "Morphy 업데이트", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            maintenance = true; trigger.Close(); Refresh(); using var cancel = new CancellationTokenSource();
            var window = new OperationWindow("업데이트 다운로드", cancel); window.Show();
            try
            {
                string setup = await Updates.Download(update, new Progress<double>(p => window.Update("내려받는 중", p)), cancel.Token);
                window.Finish(); Updates.Install(setup); Application.Current.Shutdown();
            }
            finally { window.Finish(); maintenance = false; Refresh(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Settings.Log("Update: " + e); if (interactive) MessageBox.Show("업데이트를 확인하거나 설치하지 못했어요.\n" + e.Message, "Morphy"); }
        finally { if (interactive) { checkingUpdate = false; Refresh(); } }
    }
    public void Dispose() { control.Dispose(); trigger.Dispose(); guide?.Close(); tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Icon?.Dispose(); tray.Dispose(); }
}
