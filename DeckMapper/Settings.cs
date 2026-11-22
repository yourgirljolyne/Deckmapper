using System.Text.Json;

namespace DeckMapper;

/// <summary>DeckMapper's own preferences, kept on this PC (never on the keyboard).</summary>
internal static class Settings
{
    private sealed class Data
    {
        /// <summary>Key ids whose Ctrl/Alt/Shift gap shouldn't be flagged.</summary>
        public SortedSet<byte> IgnoredGapKeys { get; set; } = [];
        /// <summary>Background key service: play custom keys from the PC while the keyboard is in software mode (SignalRGB).</summary>
        public bool PlayKeysInSoftwareMode { get; set; } = true;
    }

    public static bool PlayKeysInSoftwareMode { get => Current.PlayKeysInSoftwareMode; set { Current.PlayKeysInSoftwareMode = value; Save(); } }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Starts DeckMapper in the tray at sign-in (HKCU Run key).</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("DeckMapper") is string;
        }
        set
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("DeckMapper", $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue("DeckMapper", throwOnMissingValue: false);
        }
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeckMapper", "settings.json");

    private static Data? _data;

    private static Data Current
    {
        get
        {
            if (_data != null) return _data;
            try { _data = File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) : null; }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { _data = null; }
            return _data ??= new Data();
        }
    }

    public static bool IsIgnored(byte key) => Current.IgnoredGapKeys.Contains(key);

    public static void SetIgnored(byte key, bool ignored)
    {
        if (ignored) Current.IgnoredGapKeys.Add(key); else Current.IgnoredGapKeys.Remove(key);
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* stays for this session */ }
    }
}
