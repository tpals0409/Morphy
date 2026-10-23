using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Morphy;

// Read MSI tables and embedded CABs without running Windows Installer or any MSI custom action.
internal sealed class MsiExtractor : IDisposable
{
    private uint database;
    internal MsiExtractor(string path) { Check(MsiOpenDatabase(path, 0, out database)); }
    internal void Extract(string destination, CancellationToken token, Action<double>? progress = null)
    {
        Directory.CreateDirectory(destination);
        string raw = Path.Combine(destination, ".cab-files"); Directory.CreateDirectory(raw);
        foreach (var media in Query("SELECT `Cabinet` FROM `Media`", 1))
        {
            token.ThrowIfCancellationRequested(); string name = media[0];
            if (!name.StartsWith('#')) throw new IOException("외부 CAB가 필요한 MSI는 지원하지 않아요.");
            string streamName = name[1..]; string cabinet = Path.Combine(destination, Guid.NewGuid().ToString("N") + ".cab");
            uint view = 0, record = 0;
            try
            {
                Check(MsiDatabaseOpenView(database, "SELECT `Data` FROM `_Streams` WHERE `Name`='" + streamName.Replace("'", "''") + "'", out view)); Check(MsiViewExecute(view, 0)); Check(MsiViewFetch(view, out record));
                using var output = File.Create(cabinet); var buffer = new byte[1024 * 1024];
                while (true) { token.ThrowIfCancellationRequested(); uint length = (uint)buffer.Length; Check(MsiRecordReadStream(record, 1, buffer, ref length)); if (length == 0) break; output.Write(buffer, 0, (int)length); }
            }
            finally { if (record != 0) MsiCloseHandle(record); if (view != 0) MsiCloseHandle(view); }
            ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "expand.exe"), ["-F:*", cabinet, raw], token, 300).GetAwaiter().GetResult(); File.Delete(cabinet);
        }
        var directories = new Dictionary<string, string[]>(); foreach (var row in Query("SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`", 3)) directories[row[0]] = row;
        var components = new Dictionary<string, string>(); foreach (var row in Query("SELECT `Component`,`Directory_` FROM `Component`", 2)) components[row[0]] = row[1];
        string Resolve(string key, int depth = 0)
        {
            if (depth > 100) throw new IOException("MSI 디렉터리 구조가 잘못됐어요.");
            if (!directories.TryGetValue(key, out var row)) return "";
            string name = row[2].Split(':')[0]; if (name.Contains('|')) name = name.Split('|')[1];
            if (name is "." or "SourceDir" || string.IsNullOrEmpty(row[1])) name = "";
            return Path.Combine(string.IsNullOrEmpty(row[1]) ? "" : Resolve(row[1], depth + 1), name);
        }
        var files = Query("SELECT `File`,`Component_`,`FileName`,`FileSize` FROM `File`", 4); int done = 0;
        foreach (var row in files)
        {
            token.ThrowIfCancellationRequested(); string name = row[2].Contains('|') ? row[2].Split('|')[1] : row[2];
            string target = Path.GetFullPath(Path.Combine(destination, Resolve(components[row[1]]), name));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("MSI 경로가 설치 폴더를 벗어났어요.");
            string source = Path.Combine(raw, row[0]);
            if (!File.Exists(source) || new FileInfo(source).Length != long.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture)) throw new IOException("MSI 압축 파일 검증에 실패했어요: " + name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                using var a = File.OpenRead(source); using var b = File.OpenRead(target);
                bool same = System.Security.Cryptography.SHA256.HashData(a).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
                a.Dispose(); b.Dispose();
                if (!same && Path.GetFileName(Path.GetDirectoryName(target)) is "System" or "System64")
                {
                    var old = System.Diagnostics.FileVersionInfo.GetVersionInfo(target); var next = System.Diagnostics.FileVersionInfo.GetVersionInfo(source);
                    var oldVersion = new Version(old.FileMajorPart, old.FileMinorPart, old.FileBuildPart, old.FilePrivatePart);
                    var nextVersion = new Version(next.FileMajorPart, next.FileMinorPart, next.FileBuildPart, next.FilePrivatePart);
                    if (nextVersion > oldVersion) File.Move(source, target, true); else File.Delete(source);
                }
                else if (!same) throw new IOException("MSI 파일 경로가 충돌했어요: " + target);
                else File.Delete(source);
            }
            else File.Move(source, target);
            if (++done % 32 == 0 || done == files.Count) progress?.Invoke((double)done / files.Count);
        }
        Directory.Delete(raw, true);
    }
    private List<string[]> Query(string sql, int columns)
    {
        var result = new List<string[]>(); Check(MsiDatabaseOpenView(database, sql, out uint view));
        try
        {
            Check(MsiViewExecute(view, 0)); uint code;
            while ((code = MsiViewFetch(view, out uint record)) == 0)
            {
                try { var row = new string[columns]; for (uint i = 1; i <= columns; i++) { uint size = 0; _ = MsiRecordGetString(record, i, null, ref size); var text = new StringBuilder((int)size + 1); size++; Check(MsiRecordGetString(record, i, text, ref size)); row[i - 1] = text.ToString(); } result.Add(row); }
                finally { MsiCloseHandle(record); }
            }
            if (code != 259) Check(code);
        }
        finally { MsiCloseHandle(view); }
        return result;
    }
    private static void Check(uint code) { if (code != 0) throw new IOException("MSI 읽기 오류: " + code); }
    public void Dispose() { if (database != 0) { MsiCloseHandle(database); database = 0; } }
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabase(string path, nint persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder? value, ref uint length);
    [DllImport("msi.dll")] private static extern uint MsiRecordReadStream(uint record, uint field, byte[] buffer, ref uint size);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
}
