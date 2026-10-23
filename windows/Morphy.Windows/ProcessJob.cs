using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Morphy;

// A worker crash/cancel must not leave LibreOffice running in the background.
internal sealed class ProcessJob : IDisposable
{
    private nint handle;
    internal ProcessJob(Process process)
    {
        handle = CreateJobObject(0, null);
        var limits = new Limits { Basic = new BasicLimits { Flags = 0x2000 | 0x200 }, JobMemory = (nuint)(2UL * 1024 * 1024 * 1024) };
        if (handle == 0 || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<Limits>()) || !AssignProcessToJobObject(handle, process.Handle))
        {
            int error = Marshal.GetLastWin32Error(); Dispose(); try { process.Kill(true); } catch (InvalidOperationException) { } throw new Win32Exception(error);
        }
    }
    public void Dispose() { if (handle != 0) { CloseHandle(handle); handle = 0; } }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct Limits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int infoClass, ref Limits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
