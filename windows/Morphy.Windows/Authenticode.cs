using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Morphy;

internal static class Authenticode
{
    internal static string Verify(string path)
    {
        var file = new FileInfoData { Size = (uint)Marshal.SizeOf<FileInfoData>(), Path = path };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Union = 1, File = pointer, StateAction = 1, Flags = 0x1000 };
        Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            int result = WinVerifyTrust(new nint(-1), ref action, ref data);
            if (result != 0) throw new IOException($"파일 서명을 확인하지 못했어요 (0x{result:X8}).");
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        finally { data.StateAction = 2; _ = WinVerifyTrust(new nint(-1), ref action, ref data); Marshal.DestroyStructure<FileInfoData>(pointer); Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileInfoData { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public nint Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData { public uint Size; public nint Policy, Sip; public uint Ui, Revocation, Union; public nint File; public uint StateAction; public nint State, Url; public uint Flags, Context; public nint SignatureSettings; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
