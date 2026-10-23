using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Morphy;

internal static class ConversionWorker
{
    private sealed record Request(string Source, string Format, string Staging);
    private sealed record Response(string[] Files, string? Error);
    internal static int Execute(string requestPath)
    {
        string responsePath = requestPath + ".result";
        try
        {
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath)) ?? throw new IOException("작업 요청이 없어요.");
            var format = Conversion.Formats.Single(f => f.Extension == request.Format);
            var results = Conversion.Generate(request.Source, format, request.Staging, CancellationToken.None);
            File.WriteAllText(responsePath, JsonSerializer.Serialize(new Response(results, null))); return 0;
        }
        catch (Exception e) { File.WriteAllText(responsePath, JsonSerializer.Serialize(new Response([], e.Message))); return 1; }
    }
    internal static async Task<string[]> Run(string source, OutputFormat format, string destination, CancellationToken token)
    {
        Conversion.CheckInput(source);
        string staging = Path.Combine(destination, ".morphy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            string requestPath = Path.Combine(staging, "request.json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(new Request(source, format.Extension, staging)));
            Exception? failure = null;
            try { await ProcessRunner.Run(Path.Combine(AppContext.BaseDirectory, "Morphy.exe"), ["--worker", requestPath], token, 300); }
            catch (IOException e) { failure = e; }
            token.ThrowIfCancellationRequested();
            if (!File.Exists(requestPath + ".result")) throw failure ?? new IOException("변환 작업이 예상치 못하게 종료됐어요.");
            var response = JsonSerializer.Deserialize<Response>(File.ReadAllText(requestPath + ".result")) ?? throw new IOException("변환 결과를 읽지 못했어요.");
            if (response.Error != null) throw new IOException(response.Error);
            if (failure != null) throw failure;
            foreach (string file in response.Files) if (!Path.GetFullPath(file).StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("변환 결과 경로가 올바르지 않아요.");
            return Conversion.CommitAll(response.Files, destination, token);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
