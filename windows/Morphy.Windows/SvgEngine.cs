using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ImageMagick;
using SkiaSharp;
using Svg.Skia;

namespace Morphy;

internal static class SvgEngine
{
    internal static void Convert(string source, OutputFormat format, string target)
    {
        using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
        var xml = XDocument.Load(reader);
        if (xml.Root?.Name.LocalName != "svg") throw new IOException("SVG 형식이 아니에요.");
        foreach (var node in xml.Descendants())
        {
            if (node.Name.LocalName is "script" or "foreignObject") throw new IOException("스크립트가 포함된 SVG는 지원하지 않아요.");
            foreach (var attr in node.Attributes())
            {
                if (attr.Name.LocalName == "base" || attr.Name.LocalName == "style" && attr.Value.Contains('\\')) throw new IOException("외부 참조를 포함할 수 있는 SVG 스타일은 지원하지 않아요.");
                if (attr.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)) throw new IOException("실행 가능한 SVG는 지원하지 않아요.");
                if (attr.Name.LocalName is "href" or "src" && !attr.Value.StartsWith('#')) throw new IOException("외부 이미지를 참조하는 SVG는 먼저 단일 파일로 저장해 주세요.");
            }
            if (node.Name.LocalName == "style" && node.Value.Contains('\\')) throw new IOException("이스케이프된 SVG 스타일은 지원하지 않아요.");
        }
        string text = xml.ToString();
        if (Regex.IsMatch(text, "@import|@font-face", RegexOptions.IgnoreCase)) throw new IOException("외부 글꼴을 참조하는 SVG는 지원하지 않아요.");
        foreach (Match match in Regex.Matches(text, @"url\(([^)]*)\)", RegexOptions.IgnoreCase))
            if (!match.Groups[1].Value.Trim().Trim('\'', '"').StartsWith('#')) throw new IOException("외부 리소스를 참조하는 SVG는 지원하지 않아요.");
        using var svg = new SKSvg(); using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var picture = svg.Load(input) ?? throw new IOException("SVG를 읽을 수 없어요."); var bounds = picture.CullRect;
        if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width * bounds.Height > 80_000_000) throw new IOException("SVG 크기를 처리할 수 없어요.");
        if (format.Extension == "pdf")
        {
            using var pdf = SKDocument.CreatePdf(target); var canvas = pdf.BeginPage(bounds.Width, bounds.Height);
            canvas.Translate(-bounds.Left, -bounds.Top); canvas.DrawPicture(picture); pdf.EndPage(); pdf.Close();
        }
        else
        {
            using var bitmap = new SKBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
            using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(SKColors.Transparent); canvas.Translate(-bounds.Left, -bounds.Top); canvas.DrawPicture(picture); }
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100); using var image = new MagickImage(encoded.ToArray()); Conversion.WriteImage(image, format, target);
        }
    }
}
