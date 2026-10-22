using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using Morphy.Packaging;

namespace Morphy;

internal static class PackagingTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        string hash = new('a', 64);
        string release = $$"""
            {"version":"0.2.0","path":"/releases/Morphy-0.2.0-win-x64-Setup.exe","bytes":1234,"sha256":"{{hash}}"}
            """;
        using (var json = JsonDocument.Parse(release))
        {
            check(Updates.Parse(json.RootElement, new Version(0, 1, 0))?.Version == new Version(0, 2, 0), "Valid update");
            check(Updates.Parse(json.RootElement, new Version(0, 3, 0)) == null, "No downgrade");
        }
        foreach (string invalid in new[] { release.Replace("/releases/", "https://example.com/releases/"), release.Replace(hash, "bad"), release.Replace("1234", "0"), release.Replace("1234", "\"bad\""), release.Replace("0.2.0", "0.2.0-beta.1"), "{}", "[]", release.Replace("win-x64-Setup.exe", "dmg") })
            using (var json = JsonDocument.Parse(invalid)) check(Updates.Parse(json.RootElement, new Version(0, 1, 0)) == null, "Reject invalid Worker release");
        foreach (string name in new[] { "../escape.exe", "Morphy.exe" })
        {
            using var payload = new MemoryStream();
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("bundle-manifest.json").Open())) writer.Write(JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string> { [name] = hash }));
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("corrupt");
            }
            payload.Position = 0; bool rejected = false;
            string stage = Path.Combine(root, "package-" + Guid.NewGuid().ToString("N"));
            try { Bundle.Extract(payload, stage, CancellationToken.None); } catch (IOException) { rejected = true; }
            check(rejected && !File.Exists(Path.Combine(root, "escape.exe")), "Reject zip traversal/hash mismatch");
        }
    }
}
