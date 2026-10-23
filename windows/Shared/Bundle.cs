using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Morphy.Packaging;

public static class Bundle
{
    public static void Extract(Stream payload, string destination, CancellationToken token, Action<double>? progress = null)
    {
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        var manifest = zip.GetEntry("bundle-manifest.json") ?? throw new IOException("설치 파일 목록이 없어요.");
        using var input = manifest.Open();
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(input) ?? throw new IOException("설치 파일 목록이 잘못됐어요.");
        if (hashes.Count == 0 || hashes.Count > 10000) throw new IOException("설치 파일 수가 올바르지 않아요.");
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        Directory.CreateDirectory(destination);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested(); if (entry.FullName == "bundle-manifest.json") continue;
            string name = entry.FullName.Replace('\\', '/');
            if (name.Contains(':') || name.Split('/').Any(p => p is ".." or "." or "") || !seen.Add(name) || !hashes.TryGetValue(name, out string? expected)) throw new IOException("설치 파일 경로가 잘못됐어요.");
            total += entry.Length; if (total > 2_000_000_000) throw new IOException("설치 파일 크기가 너무 커요.");
            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("설치 경로를 벗어난 파일이에요.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[128 * 1024]; int count;
            while ((count = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("설치 파일 검증에 실패했어요: " + name);
            progress?.Invoke((double)seen.Count / hashes.Count);
        }
        if (seen.Count != hashes.Count || !File.Exists(Path.Combine(destination, "Morphy.exe"))) throw new IOException("설치 파일이 누락됐어요.");
    }
    public static void Activate(string staging, string target, Action register)
    {
        string backup = target + ".previous-" + Guid.NewGuid().ToString("N");
        bool previous = Directory.Exists(target), installed = false;
        try
        {
            if (previous) Directory.Move(target, backup);
            Directory.Move(staging, target); installed = true; register();
        }
        catch
        {
            if (installed && Directory.Exists(target)) Directory.Delete(target, true);
            if (previous && Directory.Exists(backup)) Directory.Move(backup, target);
            throw;
        }
        if (Directory.Exists(backup)) { try { Directory.Delete(backup, true); } catch (IOException) { } }
    }
}
