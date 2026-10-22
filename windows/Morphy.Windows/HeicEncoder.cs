using System;
using System.IO;
using System.Runtime.InteropServices;
using ImageMagick;

namespace Morphy;

// Only encode decoded RGBA pixels with WicHeic's libheif/kvazaar. Never register it as a system codec.
internal static class HeicEncoder
{
    [StructLayout(LayoutKind.Sequential)] private struct Error { public int Code, Subcode; public nint Message; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate Error Write(nint context, nint bytes, nuint size, nint user);
    [StructLayout(LayoutKind.Sequential)] private struct Writer { public int Version; public Write Callback; }
    internal static void Encode(MagickImage source, string target)
    {
        nint context = heif_context_alloc(), encoder = 0, image = 0, handle = 0;
        if (context == 0) throw new IOException("HEIC 인코더 메모리가 부족해요.");
        try
        {
            Check(heif_context_get_encoder_for_format(context, 1, out encoder)); Check(heif_encoder_set_lossy_quality(encoder, 92));
            int width = checked((int)source.Width), height = checked((int)source.Height);
            Check(heif_image_create(width, height, 1, 11, out image)); Check(heif_image_add_plane(image, 10, width, height, 8));
            nint plane = heif_image_get_plane(image, 10, out int stride);
            if (plane == 0 || stride < width * 4) throw new IOException("HEIC 이미지 버퍼를 만들지 못했어요.");
            byte[] rgba = source.ToByteArray(MagickFormat.Rgba);
            for (int y = 0; y < height; y++) Marshal.Copy(rgba, y * width * 4, plane + y * stride, width * 4);
            Check(heif_context_encode_image(context, image, encoder, 0, out handle));
            using var file = File.Create(target); Exception? failure = null;
            var writer = new Writer { Version = 1, Callback = (_, bytes, size, _) =>
            {
                try { var buffer = new byte[checked((int)size)]; Marshal.Copy(bytes, buffer, 0, buffer.Length); file.Write(buffer); return default; }
                catch (Exception e) { failure = e; return new Error { Code = 9 }; }
            } };
            Check(heif_context_write(context, ref writer, 0)); GC.KeepAlive(writer.Callback);
            if (failure != null) throw failure;
        }
        finally { if (handle != 0) heif_image_handle_release(handle); if (image != 0) heif_image_release(image); if (encoder != 0) heif_encoder_release(encoder); heif_context_free(context); }
    }
    private static void Check(Error error) { if (error.Code != 0) throw new IOException("HEIC 저장 오류: " + (Marshal.PtrToStringUTF8(error.Message) ?? error.Code.ToString())); }
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern nint heif_context_alloc();
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern void heif_context_free(nint ctx);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern Error heif_context_get_encoder_for_format(nint ctx, int format, out nint encoder);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern Error heif_encoder_set_lossy_quality(nint encoder, int quality);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern void heif_encoder_release(nint encoder);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern Error heif_image_create(int width, int height, int colorspace, int chroma, out nint image);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern Error heif_image_add_plane(nint image, int channel, int width, int height, int bits);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern nint heif_image_get_plane(nint image, int channel, out int stride);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern Error heif_context_encode_image(nint ctx, nint image, nint encoder, nint options, out nint handle);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern Error heif_context_write(nint ctx, ref Writer writer, nint user);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern void heif_image_handle_release(nint handle);
    [DllImport("wic_heic", CallingConvention = CallingConvention.Cdecl)] private static extern void heif_image_release(nint image);
}
