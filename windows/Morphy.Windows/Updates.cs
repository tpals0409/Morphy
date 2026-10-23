using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Morphy;

internal sealed record AppUpdate(Version Version, Uri Url, long Size, string Hash);
internal static class Updates
{
    internal const string Origin = "https://morphy.tpals0409dev.workers.dev";
    internal static Version Current => new(Assembly.GetExecutingAssembly().GetName().Version!.ToString(3));
    internal static async Task<AppUpdate?> Check(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("Morphy/" + Current);
        using var response = await client.GetAsync(Origin + "/api/release/windows", token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return Parse(json.RootElement, Current);
    }
    internal static AppUpdate? Parse(JsonElement release, Version current)
    {
        string? Text(string key) => release.ValueKind == JsonValueKind.Object && release.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string? number = Text("version"), path = Text("path"), digest = Text("sha256");
        if (number == null || !Regex.IsMatch(number, @"^\d+\.\d+\.\d+$") || !Version.TryParse(number, out var version) || version <= current) return null;
        if (path != $"/releases/Morphy-{version}-win-x64-Setup.exe") return null;
        if (digest == null || !Regex.IsMatch(digest, @"^[0-9a-fA-F]{64}$")) return null;
        if (!release.TryGetProperty("bytes", out var bytes) || bytes.ValueKind != JsonValueKind.Number || !bytes.TryGetInt64(out long size) || size <= 0 || size > 1_000_000_000) return null;
        return new AppUpdate(version, new Uri(Origin + path), size, digest);
    }
    internal static async Task<string> Download(AppUpdate update, IProgress<double> progress, CancellationToken token)
    {
        string folder = Path.Combine(Settings.DirectoryPath, "Updates"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"Morphy-{update.Version}-Setup.exe");
        string part = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await EngineInstaller.Download(update.Url, part, update.Size, update.Hash, progress, token);
            // A signed installed build must never be replaced by an unsigned or differently signed build.
            string? subject = null;
            try { subject = Authenticode.Verify(Path.Combine(AppContext.BaseDirectory, "Morphy.exe")); } catch (IOException) { }
            if (subject != null && Authenticode.Verify(part) != subject) throw new IOException("업데이트의 게시자가 현재 앱과 달라요.");
            token.ThrowIfCancellationRequested(); File.Move(part, path, true); return path;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
    internal static void Install(string setup)
    {
        Process.Start(new ProcessStartInfo(setup, "--update")
        {
            UseShellExecute = false, CreateNoWindow = true,
            // The installer renames the app directory. Its working directory must not lock it.
            WorkingDirectory = Path.GetTempPath()
        });
    }
}
