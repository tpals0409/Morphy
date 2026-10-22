using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Morphy;

internal sealed class Settings
{
    internal static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morphy");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public bool OnboardingShown { get; set; }
    public bool TriggerEnabled { get; set; } = true;
    internal static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    internal void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
    internal static void Log(string message)
    {
        try { Directory.CreateDirectory(DirectoryPath); File.AppendAllText(Path.Combine(DirectoryPath, "morphy.log"), $"{DateTimeOffset.Now:O} {message}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    internal static bool AutoStart
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("Morphy") is string; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue("Morphy", $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue("Morphy", false);
        }
    }
}
