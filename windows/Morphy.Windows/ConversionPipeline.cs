using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using ImageMagick;
using SkiaSharp;

namespace Morphy;

internal sealed record OutputFormat(string Name, string Extension, int Slot);
internal static class Conversion
{
    static Conversion()
    {
        ResourceLimits.Memory = 512UL * 1024 * 1024; ResourceLimits.Disk = 1024UL * 1024 * 1024;
        ResourceLimits.Width = 100000; ResourceLimits.Height = 100000; ResourceLimits.ListLength = 2000; ResourceLimits.Thread = 2;
    }
    internal static readonly OutputFormat[] Formats = [new("PDF", "pdf", 0), new("JPG", "jpg", 1), new("HEIC", "heic", 2), new("GIF", "gif", 3), new("PNG", "png", 5), new("TIFF", "tiff", -1), new("WEBP", "webp", -1), new("TXT", "txt", 0), new("DOCX", "docx", 3), new("XLSX", "xlsx", 3), new("PPTX", "pptx", 3), new("CSV", "csv", 2), new("HTML", "html", 2)];
    private static readonly string[] ImageExtensions = ["png", "jpg", "heic", "gif", "tiff", "webp", "bmp", "svg"];
    internal static readonly string[] ImageOutputs = ["pdf", "jpg", "heic", "gif", "png", "tiff", "webp"];
    internal static readonly Dictionary<string, string[]> OfficeRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["docx"] = ["pdf"], ["doc"] = ["pdf", "docx"], ["odt"] = ["pdf", "docx"], ["rtf"] = ["pdf", "docx"],
        ["txt"] = ["pdf", "docx"], ["md"] = ["pdf", "docx", "html"],
        ["xlsx"] = ["pdf", "csv"], ["xls"] = ["pdf", "xlsx", "csv"], ["ods"] = ["pdf", "xlsx", "csv"], ["csv"] = ["pdf", "xlsx"],
        ["pptx"] = ["pdf"], ["ppt"] = ["pdf", "pptx"], ["odp"] = ["pdf", "pptx"]
    };
    internal static readonly string[] WebOutputs = ["pdf", "jpg"];   // HTML through WebView2, offline (WebEngine, D11)
    internal static string Normalize(string ext) => ext.ToLowerInvariant() switch { "jpeg" or "jpe" or "jfif" => "jpg", "tif" => "tiff", "heif" => "heic", "markdown" => "md", "htm" => "html", var s => s };
    internal static string Extension(string path) => Normalize(Path.GetExtension(path).TrimStart('.'));
    internal static OutputFormat[] Outputs(string path)
    {
        string ext = Extension(path);
        string[] outputs = ImageExtensions.Contains(ext) ? ImageOutputs : ext == "pdf" ? [.. ImageOutputs.Where(x => x != "pdf"), "txt"] : ext == "html" ? WebOutputs : OfficeRoutes.GetValueOrDefault(ext) ?? [];
        return Formats.Where(f => outputs.Contains(f.Extension) && f.Extension != ext).ToArray();
    }
    internal static bool NeedsEngine(string source, OutputFormat format) => OfficeRoutes.ContainsKey(Extension(source)) && format.Extension != "html";
    internal static void CheckInput(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException("원본 파일을 찾을 수 없어요.");
        if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) throw new IOException("링크나 아직 내려받지 않은 클라우드 파일은 지원하지 않아요.");
        if (file.Length == 0 || file.Length > 100_000_000) throw new IOException("비어 있거나 100MB보다 큰 파일은 지원하지 않아요.");
        if (Outputs(path).Length == 0) throw new IOException("지원하지 않는 형식이에요.");
    }
    internal static string[] Generate(string source, OutputFormat format, string staging, CancellationToken token)
    {
        CheckInput(source);
        if (!Outputs(source).Contains(format)) throw new IOException("지원하지 않는 변환이에요.");
        token.ThrowIfCancellationRequested();
        string ext = Extension(source), stem = Path.GetFileNameWithoutExtension(source);
        string target = Path.Combine(staging, stem + "." + format.Extension);
        string[] outputs;
        if (ext == "pdf") outputs = PdfEngine.Convert(source, format, staging, token);
        else if (ext == "html") { WebEngine.Convert(source, format, target, token); outputs = [target]; }
        else if (OfficeRoutes.ContainsKey(ext)) outputs = OfficeEngine.Convert(source, format, staging, token);
        else if (ext == "svg") { SvgEngine.Convert(source, format, target); outputs = [target]; }
        else { using var image = ReadImage(source); WriteImage(image, format, target); outputs = [target]; }
        foreach (string output in outputs) { token.ThrowIfCancellationRequested(); Validate(output, format); }
        if (outputs.Length == 0) throw new IOException("변환 결과가 없어요.");
        return outputs;
    }
    internal static MagickImage ReadImage(string source)
    {
        byte[] data = File.ReadAllBytes(source);
        string expected = Extension(source);
        bool magic = expected switch
        {
            "png" => data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "jpg" => data.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
            "gif" => data.AsSpan().StartsWith("GIF87a"u8) || data.AsSpan().StartsWith("GIF89a"u8),
            "bmp" => data.AsSpan().StartsWith("BM"u8),
            "tiff" => data.AsSpan().StartsWith(new byte[] { 73, 73, 42, 0 }) || data.AsSpan().StartsWith(new byte[] { 77, 77, 0, 42 }),
            "webp" => data.Length > 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            "heic" => data.Length > 12 && data.AsSpan(4, 4).SequenceEqual("ftyp"u8), _ => false
        };
        if (!magic) throw new IOException("확장자와 실제 이미지 형식이 달라요.");
        using var ping = new MagickImageCollection(); ping.Ping(data);
        if (ping.Count == 0) throw new IOException("이미지를 읽을 수 없어요.");
        string actual = Normalize(ping[0].Format.ToString().ToLowerInvariant());
        if (actual != expected) throw new IOException("확장자와 실제 이미지 형식이 달라요.");
        if (expected is "gif" or "webp" && ping.Count > 1) throw new IOException("움직이는 이미지는 지원하지 않아요.");
        if ((long)ping[0].Width * ping[0].Height > 80_000_000) throw new IOException("이미지 해상도가 너무 커요.");
        var image = new MagickImage(data, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 }); image.AutoOrient(); return image;
    }
    internal static void WriteImage(MagickImage image, OutputFormat format, string target)
    {
        if (format.Extension == "heic") { HeicEncoder.Encode(image, target); return; }
        if (format.Extension == "pdf")
        {
            using var decoded = SKImage.FromEncodedData(image.ToByteArray(MagickFormat.Png));
            using var pdf = SKDocument.CreatePdf(target);
            float scale = Math.Min(1f, 14400f / Math.Max(decoded.Width, decoded.Height));
            var canvas = pdf.BeginPage(decoded.Width * scale, decoded.Height * scale);
            canvas.Clear(SKColors.White); canvas.DrawImage(decoded, new SKRect(0, 0, decoded.Width * scale, decoded.Height * scale), new SKSamplingOptions(SKFilterMode.Linear));
            pdf.EndPage(); pdf.Close(); return;
        }
        if (format.Extension == "jpg") { image.BackgroundColor = MagickColors.White; image.Alpha(AlphaAction.Remove); }
        image.Quality = 92; image.Write(target, ToMagick(format.Extension));
    }
    private static MagickFormat ToMagick(string ext) => ext switch { "jpg" => MagickFormat.Jpeg, "png" => MagickFormat.Png, "gif" => MagickFormat.Gif, "tiff" => MagickFormat.Tiff, "heic" => MagickFormat.Heic, "webp" => MagickFormat.WebP, _ => throw new NotSupportedException(ext) };
    internal static void Validate(string path, OutputFormat format)
    {
        if (!File.Exists(path)) throw new IOException("변환 결과가 생성되지 않았어요.");
        if (format.Extension == "pdf") { using var pdf = new PdfEngine(path); pdf.ValidatePages(); }
        else if (format.Extension is "docx" or "xlsx" or "pptx")
        {
            using var zip = ZipFile.OpenRead(path);
            string part = format.Extension switch { "docx" => "word/document.xml", "xlsx" => "xl/workbook.xml", _ => "ppt/presentation.xml" };
            if (zip.GetEntry("[Content_Types].xml") == null || zip.GetEntry(part) == null) throw new IOException("문서 검증에 실패했어요.");
            using var stream = zip.GetEntry(part)!.Open(); using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null }); while (reader.Read()) { }
        }
        else if (format.Extension is "txt" or "csv" or "html") { _ = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)); }
        else { using var image = ReadImage(path); if (image.Width == 0 || image.Height == 0) throw new IOException("이미지 검증에 실패했어요."); }
    }
    internal static string[] CommitAll(string[] generated, string destination, CancellationToken token)
    {
        var published = new List<string>();
        try { token.ThrowIfCancellationRequested(); foreach (string file in generated) published.Add(Commit(file, destination, Path.GetFileNameWithoutExtension(file), Extension(file))); return published.ToArray(); }
        catch { foreach (string file in published) File.Delete(file); throw; }
    }
    internal static string Commit(string temp, string folder, string stem, string extension)
    {
        for (int n = 0; n < 10000; n++)
        {
            string path = Path.Combine(folder, $"{stem}{(n == 0 ? "" : $" ({n})")}.{extension}");
            try { File.Move(temp, path, false); return path; } catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("같은 이름의 파일이 너무 많아요.");
    }
    internal static string Run(string source, OutputFormat format, string destination)
    {
        string staging = Path.Combine(destination, ".morphy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try { return CommitAll(Generate(source, format, staging, CancellationToken.None), destination, CancellationToken.None)[0]; }
        finally { Directory.Delete(staging, true); }
    }
}
