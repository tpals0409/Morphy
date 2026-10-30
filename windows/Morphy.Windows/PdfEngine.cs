using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ImageMagick;

namespace Morphy;

// PDFium is confined to the conversion worker's single thread.
internal sealed class PdfEngine : IDisposable
{
    private GCHandle pinned;
    private nint document;
    internal int PageCount { get; }
    static PdfEngine()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "pdfium.dll");
        if (File.Exists(local)) NativeLibrary.Load(local);
        FPDF_InitLibrary();
    }
    internal PdfEngine(string path)
    {
        byte[] data = File.ReadAllBytes(path); pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        document = FPDF_LoadMemDocument64(pinned.AddrOfPinnedObject(), (nuint)data.Length, null);
        if (document == 0) { pinned.Free(); throw new IOException(FPDF_GetLastError() == 4 ? "암호가 걸린 PDF는 지원하지 않아요." : "PDF를 읽을 수 없어요."); }
        if (FPDF_GetSecurityHandlerRevision(document) >= 0) { Dispose(); throw new IOException("암호화된 PDF는 지원하지 않아요."); }
        PageCount = FPDF_GetPageCount(document);
        if (PageCount is <= 0 or > 2000) { Dispose(); throw new IOException("PDF 페이지 수가 지원 범위를 벗어났어요."); }
    }
    internal void ValidatePages()
    {
        for (int i = 0; i < PageCount; i++) { nint page = FPDF_LoadPage(document, i); if (page == 0) throw new IOException("PDF 페이지 검증에 실패했어요."); FPDF_ClosePage(page); }
    }
    internal string Text()
    {
        var result = new StringBuilder();
        for (int i = 0; i < PageCount; i++)
        {
            nint page = FPDF_LoadPage(document, i); if (page == 0) throw new IOException("PDF 페이지를 읽지 못했어요.");
            try
            {
                nint text = FPDFText_LoadPage(page); if (text == 0) continue;
                try
                {
                    int count = FPDFText_CountChars(text); if (count < 0 || count > 10_000_000) throw new IOException("PDF 텍스트가 너무 커요.");
                    var buffer = new ushort[count + 1]; int read = FPDFText_GetText(text, 0, count, buffer);
                    if (read > 0) { var bytes = new byte[(read - 1) * 2]; Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length); result.Append(Encoding.Unicode.GetString(bytes)); }
                    result.AppendLine();
                }
                finally { FPDFText_ClosePage(text); }
            }
            finally { FPDF_ClosePage(page); }
        }
        return result.ToString();
    }
    private MagickImage Render(int index)
    {
        nint page = FPDF_LoadPage(document, index); if (page == 0) throw new IOException("PDF 페이지를 읽지 못했어요.");
        try
        {
            double pw = FPDF_GetPageWidth(page), ph = FPDF_GetPageHeight(page);
            if (!double.IsFinite(pw) || !double.IsFinite(ph) || pw <= 0 || ph <= 0 || pw > 40000 || ph > 40000) throw new IOException("PDF 페이지 크기를 처리할 수 없어요.");
            int w = (int)Math.Ceiling(pw * 2), h = (int)Math.Ceiling(ph * 2);
            if ((long)w * h > 80_000_000) throw new IOException("PDF 페이지 해상도가 너무 커요.");
            nint bitmap = FPDFBitmap_Create(w, h, 1); if (bitmap == 0) throw new IOException("PDF 렌더링 메모리가 부족해요.");
            try
            {
                FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF); FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, 1);
                int stride = FPDFBitmap_GetStride(bitmap); byte[] raw = new byte[checked(w * h * 4)]; nint buffer = FPDFBitmap_GetBuffer(bitmap);
                for (int y = 0; y < h; y++) Marshal.Copy(buffer + y * stride, raw, y * w * 4, w * 4);
                var image = new MagickImage(); image.ReadPixels(raw, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA)); return image;
            }
            finally { FPDFBitmap_Destroy(bitmap); }
        }
        finally { FPDF_ClosePage(page); }
    }
    internal static string[] Convert(string source, OutputFormat format, string staging, CancellationToken token)
    {
        using var pdf = new PdfEngine(source); string stem = Conversion.OutputStem(source);
        if (format.Extension == "txt") { string path = Path.Combine(staging, stem + ".txt"); File.WriteAllText(path, pdf.Text(), new UTF8Encoding(false)); return [path]; }
        var result = new List<string>();
        for (int i = 0; i < pdf.PageCount; i++)
        {
            token.ThrowIfCancellationRequested(); using var image = pdf.Render(i);
            string path = Path.Combine(staging, stem + (pdf.PageCount == 1 ? "" : $"-{i + 1:D3}") + "." + format.Extension);
            Conversion.WriteImage(image, format, path); result.Add(path);
        }
        return result.ToArray();
    }
    public void Dispose() { if (document != 0) { FPDF_CloseDocument(document); document = 0; } if (pinned.IsAllocated) pinned.Free(); }
    [DllImport("pdfium")] private static extern void FPDF_InitLibrary();
    [DllImport("pdfium")] private static extern nint FPDF_LoadMemDocument64(nint data, nuint length, string? password);
    [DllImport("pdfium")] private static extern uint FPDF_GetLastError();
    [DllImport("pdfium")] private static extern int FPDF_GetSecurityHandlerRevision(nint doc);
    [DllImport("pdfium")] private static extern int FPDF_GetPageCount(nint doc);
    [DllImport("pdfium")] private static extern nint FPDF_LoadPage(nint doc, int index);
    [DllImport("pdfium")] private static extern void FPDF_ClosePage(nint page);
    [DllImport("pdfium")] private static extern void FPDF_CloseDocument(nint doc);
    [DllImport("pdfium")] private static extern double FPDF_GetPageWidth(nint page);
    [DllImport("pdfium")] private static extern double FPDF_GetPageHeight(nint page);
    [DllImport("pdfium")] private static extern nint FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport("pdfium")] private static extern void FPDFBitmap_FillRect(nint bitmap, int x, int y, int w, int h, uint color);
    [DllImport("pdfium")] private static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int w, int h, int rotation, int flags);
    [DllImport("pdfium")] private static extern int FPDFBitmap_GetStride(nint bitmap);
    [DllImport("pdfium")] private static extern nint FPDFBitmap_GetBuffer(nint bitmap);
    [DllImport("pdfium")] private static extern void FPDFBitmap_Destroy(nint bitmap);
    [DllImport("pdfium")] private static extern nint FPDFText_LoadPage(nint page);
    [DllImport("pdfium")] private static extern int FPDFText_CountChars(nint page);
    [DllImport("pdfium")] private static extern int FPDFText_GetText(nint page, int start, int count, [Out] ushort[] result);
    [DllImport("pdfium")] private static extern void FPDFText_ClosePage(nint page);
}
