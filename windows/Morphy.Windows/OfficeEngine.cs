using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Markdig;

namespace Morphy;

internal static class OfficeEngine
{
    internal static string DecodeText(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length >= 2 && (data[0] == 0xFF && data[1] == 0xFE || data[0] == 0xFE && data[1] == 0xFF)) return File.ReadAllText(path);
        try { return new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(data); }
    }
    // A table right under a line of text is a table on GitHub, but not to stricter Markdown parsers. Add the blank
    // line before a table header (outside fenced code) so it always converts as a table, as the Mac app does.
    internal static string SeparateTables(string text)
    {
        var delimiter = new Regex(@"^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?$");
        string[] lines = text.Split('\n');
        var output = new System.Collections.Generic.List<string>();
        bool fenced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")) fenced = !fenced;
            if (!fenced && trimmed.Contains('|') && i + 1 < lines.Length && output.Count > 0 && output[output.Count - 1].Trim().Length > 0)
            {
                string next = lines[i + 1].Trim();
                if (next.Contains('|') && delimiter.IsMatch(next)) output.Add("");
            }
            output.Add(lines[i]);
        }
        return string.Join("\n", output);
    }
    private static string MarkdownHtml(string text)
    {
        text = SeparateTables(text);
        string html = Markdown.ToHtml(text, new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build());
        html = Regex.Replace(html, "<img\\b[^>]*>", "", RegexOptions.IgnoreCase);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><style>body{font-family:'Malgun Gothic',sans-serif;line-height:1.6}table{border-collapse:collapse}td,th{border:1px solid #aaa;padding:6px}pre{white-space:pre-wrap}</style></head><body>" + html + "</body></html>";
    }
    internal static string[] Convert(string source, OutputFormat format, string staging, CancellationToken token)
    {
        string ext = Conversion.Extension(source), stem = Path.GetFileNameWithoutExtension(source);
        if (ext == "md" && format.Extension == "html") { string html = Path.Combine(staging, stem + ".html"); File.WriteAllText(html, MarkdownHtml(DecodeText(source)), new UTF8Encoding(false)); return [html]; }
        string engine = EngineInstaller.Find() ?? throw new IOException("문서 변환 기능을 먼저 추가해 주세요.");
        string work = Path.Combine(staging, "office-work"); Directory.CreateDirectory(work);
        string input = source;
        if (ext is "txt" or "md" or "csv")
        {
            input = Path.Combine(work, stem + (ext == "md" ? ".html" : "." + ext));
            File.WriteAllText(input, ext == "md" ? MarkdownHtml(DecodeText(source)) : DecodeText(source), new UTF8Encoding(true));
        }
        else ValidateInput(source, ext);
        string profile = Path.Combine(work, "profile"); Directory.CreateDirectory(Path.Combine(profile, "user"));
        File.WriteAllText(Path.Combine(profile, "user", "registrymodifications.xcu"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <oor:items xmlns:oor="http://openoffice.org/2001/registry">
              <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop><prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop></item>
              <item oor:path="/org.openoffice.Office.Common/Load"><prop oor:name="UpdateDocMode" oor:op="fuse"><value>0</value></prop></item>
              <item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>2</value></prop></item>
              <item oor:path="/org.openoffice.Office.Calc/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>2</value></prop></item>
            </oor:items>
            """);
        string filter = format.Extension switch
        {
            "pdf" => ext is "xlsx" or "xls" or "ods" or "csv" ? "pdf:calc_pdf_Export" : ext is "ppt" or "pptx" or "odp" ? "pdf:impress_pdf_Export" : "pdf:writer_pdf_Export",
            "docx" => "docx:Office Open XML Text", "xlsx" => "xlsx:Calc MS Excel 2007 XML", "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
            "csv" => "csv:Text - txt - csv (StarCalc):44,34,76,1,,0,false,true,true,false,false,-1", _ => throw new NotSupportedException()
        };
        var args = new System.Collections.Generic.List<string> { "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri, "--headless", "--nologo", "--nodefault", "--nolockcheck", "--norestore" };
        if (ext == "csv") args.Add("--infilter=Text - txt - csv (StarCalc):44,34,76,1,,0,false,true");
        if (ext == "txt") args.Add("--infilter=Text (encoded):UTF8,LF,Malgun Gothic,ko-KR");
        args.AddRange(["--convert-to", filter, "--outdir", staging, input]);
        string log = ProcessRunner.Run(engine, args.ToArray(), token).GetAwaiter().GetResult();
        string[] outputs = Directory.GetFiles(staging, "*." + format.Extension).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (outputs.Length == 0) throw new IOException("문서를 변환하지 못했어요. 암호·손상 여부를 확인해 주세요. " + log);
        if (format.Extension == "csv" && outputs.Length == 1)
        {
            string canonical = Path.Combine(staging, stem + ".csv"); if (!string.Equals(outputs[0], canonical, StringComparison.OrdinalIgnoreCase)) { File.Move(outputs[0], canonical); outputs[0] = canonical; }
        }
        // UTF-8 BOM on every sheet's CSV: Korean-locale Excel otherwise reads it as CP949 and garbles Hangul (the Mac app does the same).
        if (format.Extension == "csv")
        {
            byte[] bom = Encoding.UTF8.GetPreamble();
            foreach (string csv in outputs) { byte[] data = File.ReadAllBytes(csv); if (!data.AsSpan().StartsWith(bom)) File.WriteAllBytes(csv, bom.Concat(data).ToArray()); }
        }
        return outputs;
    }
    private static void ValidateInput(string source, string ext)
    {
        byte[] magic = new byte[8]; using (var file = File.OpenRead(source)) _ = file.Read(magic);
        bool zip = magic[0] == 0x50 && magic[1] == 0x4B, ole = magic[0] == 0xD0 && magic[1] == 0xCF;
        if (ext is "docx" or "xlsx" or "pptx" or "odt" or "ods" or "odp")
        {
            if (ole) throw new IOException("암호가 걸린 문서는 지원하지 않아요.");
            if (!zip) throw new IOException("문서 확장자와 실제 형식이 달라요.");
            using var archive = ZipFile.OpenRead(source);
            if (archive.Entries.Sum(e => e.Length) > 1_000_000_000L) throw new IOException("문서 압축 해제 크기가 너무 커요.");
            // Reject remote relationships rather than fetching linked documents/images during conversion.
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open(); using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                while (reader.Read()) if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.GetAttribute("TargetMode") == "External" && !(reader.GetAttribute("Type") ?? "").EndsWith("/hyperlink", StringComparison.Ordinal)) throw new IOException("외부 파일에 연결된 문서는 연결을 해제한 뒤 변환해 주세요.");
            }
        }
        else if (ext is "doc" or "xls" or "ppt" && !ole) throw new IOException("문서 확장자와 실제 형식이 달라요.");
        else if (ext == "rtf" && !Encoding.ASCII.GetString(magic).StartsWith("{\\rtf")) throw new IOException("RTF 형식이 아니에요.");
    }
}
