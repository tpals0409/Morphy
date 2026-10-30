using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Morphy;

internal static class SelfTest
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "Morphy-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            void Check(bool value, string why) { if (!value) throw new Exception(why); }
            foreach (var keys in new[] { (0xA2u, 0xA4u), (0xA4u, 0xA2u), (0xA3u, 0xA4u) })
            {
                var chord = new ShortcutChord();
                Check(chord.Update(keys.Item1, true, true) == 0, "One modifier does not open");
                Check(chord.Update(keys.Item2, true, true) == 1 && chord.Claimed, "Both modifier orders open");
                Check(chord.Update(keys.Item2, true, true) == 0, "Repeat must not reopen");
                Check(chord.Update(keys.Item1, false, true) == -1 && !chord.Claimed, "Release either modifier executes once");
                Check(chord.Update(keys.Item2, false, true) == 0, "Second release must not execute again");
                chord.Update(keys.Item1, true, false);
                Check(chord.Update(keys.Item2, true, false) == 0 && !chord.Claimed, "Ineligible chord passes through");
                Check(chord.Update(keys.Item2, true, true) == 0, "Focus change while held must not open");
                Check(chord.Update(keys.Item2, false, true) == 0, "Unclaimed release does not execute");
                Check(chord.Update(keys.Item2, true, true) == 1, "Next chord opens");
            }
            Check(RadialMenu.Hit(0, 0) == -1, "Center must cancel");
            Check(RadialMenu.Hit(0, -140) == -1, "Outside must cancel");
            Check(RadialMenu.Hit(0, -95) == 0, "Top slot");
            Check(RadialMenu.Hit(82, -48) == 1, "Upper-right slot");
            Check(RadialMenu.Hit(-82, -48) == 5, "Upper-left slot");
            Check(!Conversion.Outputs("test.JPEG").Any(f => f.Extension == "jpg"), "Alias exclusion");
            Check(Conversion.Outputs("test.exe").Length == 0, "Unsupported input");
            Check(OfficeEngine.SeparateTables("설명입니다.\n| 월 | 매출 |\n|---|---:|\n| 1월 | 100 |") == "설명입니다.\n\n| 월 | 매출 |\n|---|---:|\n| 1월 | 100 |", "Markdown table under text gets a blank line");
            Check(OfficeEngine.SeparateTables("글\r\n월 | 매출\r\n---|---\r\n1월 | 100\r\n").Contains("글\r\n\n월 | 매출"), "Markdown table without outer pipes, CRLF");
            foreach (string unchanged in new[] { "# 제목\n\n| a | b |\n|---|---|\n| 1 | 2 |", "```\ncode\n| a | b |\n|---|---|\n```", "a | b 는 표가 아니에요\n다음 줄" })
                Check(OfficeEngine.SeparateTables(unchanged) == unchanged, "Markdown left alone: " + unchanged);
            var frame = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 0 }, 8);
            string source = Path.Combine(root, "한글 원본.png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
            using (var file = File.Create(source)) encoder.Save(file);
            byte[] original = File.ReadAllBytes(source);
            foreach (var format in Conversion.Outputs(source))
            {
                string output = Conversion.Run(source, format, root);
                Check(File.Exists(output), $"Missing {format.Name}");
                if (format.Extension == "pdf") { using var pdf = new PdfEngine(output); Check(pdf.PageCount == 1, "PDF page"); }
                else
                {
                    using var result = Conversion.ReadImage(output);
                    Check(result.Width == 2 && result.Height == 2, "Dimensions");
                }
                string second = Conversion.Run(source, format, root);
                Check(second != output && second.Contains("(1)"), "Collision must not overwrite");
            }
            foreach (var pair in new[] { ("photo.jpg.png", "photo"), ("photo.PNG.PNG", "photo"), ("report.v1.png", "report.v1"), ("report.pdf.docx", "report"), ("photo.JPEG.png", "photo"), ("pdf.png", "pdf") })
                Check(Conversion.OutputStem(pair.Item1) == pair.Item2, "Output name: " + pair.Item1);
            string repeated = Path.Combine(root, "double.jpg.png"); File.WriteAllBytes(repeated, original);
            var jpg = Conversion.Formats.Single(f => f.Extension == "jpg");
            Check(Path.GetFileName(Conversion.Run(repeated, jpg, root)) == "double.jpg", "Repeated extension removed");
            Check(Path.GetFileName(Conversion.Run(repeated, jpg, root)) == "double (1).jpg", "Cleaned name collision preserved");
            Check(original.SequenceEqual(File.ReadAllBytes(repeated)), "Repeated-extension original unchanged");
            Check(original.SequenceEqual(File.ReadAllBytes(source)), "Original changed");
            string corrupt = Path.Combine(root, "bad.png"); File.WriteAllText(corrupt, "not an image");
            bool rejected = false; try { Conversion.Run(corrupt, Conversion.Formats[0], root); } catch { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(root, "bad.pdf")), "Corrupt input must not publish");
            string mismatch = Path.Combine(root, "wrong.jpg"); File.Copy(source, mismatch);
            rejected = false; try { Conversion.ReadImage(mismatch); } catch { rejected = true; }
            Check(rejected, "Mismatched extension must fail");
            Check(!Directory.GetFiles(root, ".morphy-*").Any(), "Temporary output leaked");
            foreach (var route in Conversion.OfficeRoutes)
            {
                var options = Conversion.Outputs("file." + route.Key);
                Check(options.Select(f => f.Slot).Where(s => s >= 0).Distinct().Count() == options.Count(f => f.Slot >= 0), "Duplicate ring slots: " + route.Key);
            }
            string svg = Path.Combine(root, "벡터.svg");
            File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"80\"><rect width=\"120\" height=\"80\" fill=\"red\"/><circle cx=\"50\" cy=\"40\" r=\"20\" fill=\"blue\"/></svg>");
            string vector = Conversion.Run(svg, Conversion.Formats.Single(f => f.Extension == "pdf"), root);
            using (var pdf = new PdfEngine(vector)) Check(pdf.PageCount == 1, "SVG vector PDF");
            Check(!File.ReadAllText(vector).Contains("/Subtype /Image"), "SVG must remain vector");
            string raster = Conversion.Run(vector, Conversion.Formats.Single(f => f.Extension == "png"), root);
            using (var bitmap = Conversion.ReadImage(raster)) Check(bitmap.Width == 240 && bitmap.Height == 160, "PDF raster dimensions");
            File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://example.com/secret.png\"/></svg>");
            rejected = false; try { Conversion.Run(svg, Conversion.Formats[0], root); } catch { rejected = true; }
            Check(rejected, "External SVG resources must be rejected");
            // HTML → PDF/JPG through WebView2: layout, scripts, local image, page break, no network (D11).
            string html = Path.Combine(root, "웹 페이지.html");
            File.Copy(source, Path.Combine(root, "local.png"), true);
            File.WriteAllText(html, "<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><style>.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:8px}.card{background:#f47b55;padding:16px}h2{break-before:page}</style></head><body><h1>웹 페이지 변환</h1><p id=\"js\">스크립트 전</p><p id=\"net\">확인 중</p><div class=\"grid\"><div class=\"card\">하나</div><div class=\"card\">둘</div><div class=\"card\">셋</div></div><img src=\"local.png\" width=\"64\"><img src=\"https://www.microsoft.com/favicon.ico\" width=\"1\" onload=\"document.getElementById('net').textContent='외부 연결됨'\" onerror=\"document.getElementById('net').textContent='외부 차단됨'\"><h2>둘째 쪽</h2><p>새 페이지에서 시작해요.</p><script>document.getElementById('js').textContent='스크립트가 바꾼 문장';</script></body></html>");
            Check(Conversion.Outputs(html).Select(f => f.Extension).SequenceEqual(new[] { "pdf", "jpg" }), "HTML offers PDF and JPG");
            Check(Conversion.Outputs("page.htm").Length == 2, ".htm is HTML too");
            string webPdf = Conversion.Run(html, Conversion.Formats.Single(f => f.Extension == "pdf"), root);
            using (var pdf = new PdfEngine(webPdf))
            {
                string text = pdf.Text();
                Check(pdf.PageCount == 2, "HTML PDF must keep the CSS page break, pages: " + pdf.PageCount);
                Check(text.Contains("스크립트가 바꾼 문장") && text.Contains("하나"), "HTML PDF must include script output and Korean text");
                Check(text.Contains("외부 차단됨"), "HTML PDF: network must be blocked: " + text);
            }
            string webJpg = Conversion.Run(html, Conversion.Formats.Single(f => f.Extension == "jpg"), root);
            using (var shot = Conversion.ReadImage(webJpg)) Check(shot.Width == 2 * WebEngine.Width && shot.Height > shot.Width / 2, $"HTML JPG must be the whole page at 2x, got {shot.Width}x{shot.Height}");
            string cp949 = Path.Combine(root, "cp949.txt");
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            File.WriteAllBytes(cp949, System.Text.Encoding.GetEncoding(949).GetBytes("한글 공백 검증"));
            Check(OfficeEngine.DecodeText(cp949) == "한글 공백 검증", "CP949 text");
            using var cancelled = new System.Threading.CancellationTokenSource(); cancelled.Cancel();
            rejected = false; try { ConversionWorker.Run(source, Conversion.Formats[0], root, cancelled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && !Directory.GetDirectories(root, ".morphy-*").Any(), "Worker cancellation cleanup");
            PackagingTests.Run(root, Check);
            using var stop = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            rejected = false;
            try { ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "ping.exe"), ["-t", "127.0.0.1"], stop.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "Running process cancellation");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest-result.txt"), "PASS: ring hit testing, all image encoders including HEIC/WEBP, PDF re-open/render, vector SVG, CP949, Korean paths, collision protection, originals, corrupt inputs, external SVG rejection, cancellation cleanup, update URL/digest/version rules, package traversal/hash rejection\n");
            return 0;
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest-result.txt"), e.ToString()); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
