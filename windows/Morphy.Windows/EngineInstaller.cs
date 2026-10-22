using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Morphy;

internal static class EngineInstaller
{
    internal const string Version = "26.8.1";
    internal const long DownloadBytes = 374870016;
    internal const string Hash = "c6298b10cfa1748bcbedad94c62ce771e240e5021bfd6d7a967cd571b04b5312";
    internal static string Root => Path.Combine(Settings.DirectoryPath, "Engines", "LibreOffice");
    internal static string Managed => Path.Combine(Root, Version);
    internal static string? Find()
    {
        string? test = Environment.GetEnvironmentVariable("MORPHY_ENGINE_PATH");
        if (!string.IsNullOrWhiteSpace(test) && File.Exists(test)) return test;
        foreach (string program in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string exe = Path.Combine(program, "LibreOffice", "program", "soffice.com");
            if (File.Exists(exe)) { var v = FileVersionInfo.GetVersionInfo(exe); if (v.FileMajorPart > 26 || v.FileMajorPart == 26 && v.FileMinorPart >= 8) return exe; }
        }
        string managed = Path.Combine(Managed, "program", "soffice.com"); return File.Exists(managed) ? managed : null;
    }
    internal static async Task Install(IProgress<(string Stage, double Fraction)> progress, CancellationToken token)
    {
        if (Find() != null) return;
        Directory.CreateDirectory(Root);
        var drive = new DriveInfo(Path.GetPathRoot(Root)!); if (drive.AvailableFreeSpace < 2_000_000_000) throw new IOException("문서 변환 기능을 추가하려면 2GB의 여유 공간이 필요해요.");
        string work = Path.Combine(Root, ".install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            string msi = Path.Combine(work, "engine.msi");
            string[] urls = [$"https://download.documentfoundation.org/libreoffice/stable/{Version}/win/x86_64/LibreOffice_{Version}_Win_x86-64.msi", $"https://downloadarchive.documentfoundation.org/libreoffice/old/{Version}.1/win/x86_64/LibreOffice_{Version}.1_Win_x86-64.msi"];
            Exception? last = null;
            foreach (string url in urls)
            {
                try { await Download(new Uri(url), msi, DownloadBytes, Hash, new Progress<double>(p => progress.Report(("내려받는 중", p))), token); last = null; break; }
                catch (Exception e) when (e is HttpRequestException or IOException) { last = e; }
            }
            if (last != null) throw last;
            progress.Report(("서명 확인 중", 0)); await VerifySignature(msi, token);
            string extracted = Path.Combine(work, "extracted");
            await Task.Run(() => { using var archive = new MsiExtractor(msi); archive.Extract(extracted, token, p => progress.Report(("추가하는 중", p))); }, token);
            string executable = Directory.GetFiles(extracted, "soffice.com", SearchOption.AllDirectories).Single();
            string appRoot = Directory.GetParent(Path.GetDirectoryName(executable)!)!.FullName;
            CopyRuntime(extracted, Path.GetDirectoryName(executable)!);
            string test = await ProcessRunner.Run(executable, ["--headless", "--version"], token, 30);
            if (!test.Contains(Version, StringComparison.Ordinal)) throw new IOException("설치한 문서 변환 기능의 버전이 올바르지 않아요.");
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(Managed)) throw new IOException("기존 엔진 폴더가 있어요. 문서 변환 기능 삭제 후 다시 추가해 주세요.");
            Directory.Move(appRoot, Managed);
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }
    internal static void CopyRuntime(string extracted, string program)
    {
        string folder = Path.Combine(extracted, "System64");
        if (Directory.Exists(folder)) foreach (string dll in Directory.GetFiles(folder, "*.dll")) File.Copy(dll, Path.Combine(program, Path.GetFileName(dll)), true);
    }
    internal static async Task Download(Uri url, string target, long size, string expectedHash, IProgress<double>? progress, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Morphy/0.1.0");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new IOException("안전하지 않은 다운로드 주소예요.");
        if (response.Content.Headers.ContentLength is long actual && actual != size) throw new IOException("다운로드 파일 크기가 일치하지 않아요.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            var buffer = new byte[128 * 1024]; long total = 0; int read; var clock = Stopwatch.StartNew();
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                total += read; if (total > size) throw new IOException("다운로드 파일이 예상보다 커요."); hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token);
                if (clock.ElapsedMilliseconds >= 100 || total == size) { progress?.Report((double)total / size); clock.Restart(); }
            }
            if (total != size) throw new IOException("파일 다운로드가 완료되지 않았어요.");
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("다운로드 무결성 검증에 실패했어요.");
    }
    internal static Task VerifySignature(string msi, CancellationToken token)
    {
        return Task.Run(() => { token.ThrowIfCancellationRequested(); string subject = Authenticode.Verify(msi); if (!subject.Contains("O=The Document Foundation", StringComparison.Ordinal)) throw new IOException("문서 변환 기능의 게시자가 일치하지 않아요."); }, token);
    }
    internal static void Remove()
    {
        if (!Directory.Exists(Managed)) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(Managed, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }
}
