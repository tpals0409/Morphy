using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Morphy;

internal sealed class ControlServer : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    internal ControlServer(Func<bool> canExit, Action checkUpdates) { _ = Serve(canExit, checkUpdates); }
    private async Task Serve(Func<bool> canExit, Action checkUpdates)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream("Morphy.Control." + Environment.UserName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                string? command = await reader.ReadLineAsync(deadline.Token);
                bool exit = command == "quit" && await Application.Current.Dispatcher.InvokeAsync(canExit);
                await writer.WriteLineAsync(exit || command == "check-updates" ? "ok" : "busy");
                if (command == "check-updates") _ = Application.Current.Dispatcher.BeginInvoke(checkUpdates);
                if (exit) { _ = Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown()); return; }
            }
            catch (OperationCanceledException) { }
            catch (IOException e) { Settings.Log("Control pipe: " + e.Message); }
        }
    }
    internal static async Task RequestUpdateCheck()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", "Morphy.Control." + Environment.UserName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("check-updates".AsMemory(), deadline.Token);
        if (await reader.ReadLineAsync(deadline.Token) != "ok") throw new IOException("실행 중인 Morphy에서 업데이트 확인을 열지 못했어요.");
    }
    public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); }
}
