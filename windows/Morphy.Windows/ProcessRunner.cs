using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Morphy;

internal static class ProcessRunner
{
    internal static async Task<string> Run(string executable, string[] args, CancellationToken token, int timeoutSeconds = 180)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileName(executable).StartsWith("soffice", StringComparison.OrdinalIgnoreCase))
        {
            info.Environment["SAL_DISABLE_OPENCL"] = "1";
            info.Environment["SAL_DISABLESKIA"] = "1";
        }
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        if (!process.Start()) throw new IOException("변환 프로세스를 시작할 수 없어요.");
        using var job = new ProcessJob(process);
        Task<string> stdout = ReadOutput(process.StandardOutput), stderr = ReadOutput(process.StandardError);
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("작업 시간이 초과됐어요. 파일을 확인한 뒤 다시 시도해 주세요.");
        }
        job.Dispose();
        string output = await stdout.ConfigureAwait(false), error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException($"작업이 실패했어요 ({process.ExitCode}). {error} {output}".Trim());
        return output;
    }
    private static async Task<string> ReadOutput(StreamReader reader)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0) if (text.Length < 32768) text.Append(buffer, 0, Math.Min(count, 32768 - text.Length));
        return text.ToString();
    }
}
