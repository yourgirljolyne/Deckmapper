using System.Runtime.InteropServices;

namespace DeckMapper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (cmd is "dump" or "check" or "restore" or "import")
        {
            AttachConsole(-1);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            try
            {
                return cmd switch
                {
                    "dump" => Dump().GetAwaiter().GetResult(),
                    "check" => Check(args.Length > 1 ? byte.Parse(args[1]) : null).GetAwaiter().GetResult(),
                    "import" => Import(args[1], args.Contains("--apply")).GetAwaiter().GetResult(),
                    _ => Restore(args[1]).GetAwaiter().GetResult(),
                };
            }
            catch (BragiException e) { Console.WriteLine(e.Message); return 1; }
        }

        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "DeckMapper.SingleInstance", out bool first);
        if (!first)
        {
            // Already running (maybe just the background service): ask it to show its window.
            if (EventWaitHandle.TryOpenExisting(BackgroundApp.ShowEventName, out var show)) { show.Set(); show.Dispose(); }
            else MessageBox.Show("DeckMapper is already open.", "DeckMapper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        // --background: start without the window (used by "Start with Windows").
        Application.Run(new BackgroundApp(showWindow: !args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase))));
        return 0;
    }

    /// <summary>Prints every key and dial assignment. Read-only.</summary>
    private static async Task<int> Dump()
    {
        using var store = new KeyStore();
        var s = await store.LoadAsync();
        Console.WriteLine($"profile {s.ProfileId} \"{s.ProfileName}\": assignments {s.AssignFile}, key mappings {s.MapFile}, dial {s.DialFile}");
        foreach (var (id, c) in s.Keys.OrderBy(k => k.Key))
        {
            if (c.Foreign) { Console.WriteLine($"  key {id,3} (not on layout): {c.Others.Count} assignment(s) kept"); continue; }
            if (c.Main.Kind == ActionKind.Default && c.Fn.Kind == ActionKind.Default && c.Others.Count == 0) continue;
            var name = KeyLayout.Find(id)?.MainText ?? id.ToString();
            Console.WriteLine($"  {name,-16} main: {c.Main.Describe(),-14} fn: {c.Fn.Describe(),-14}" +
                (c.Main.Kind == ActionKind.Default ? "" : c.WithModifiers ? " (also with Ctrl/Alt/Shift)" : " (Ctrl/Alt/Shift fall back to default!)") +
                (c.Others.Count > 0 ? $" +{c.Others.Count} other" : ""));
        }
        for (int i = 0; i < s.Dial.Count; i++)
        {
            var m = s.Dial[i];
            Console.WriteLine($"  dial mode {i + 1}: {DialNames.Name(m.Type)} #{Convert.ToHexString(m.Color)}" +
                string.Concat(m.Actions.Select(a => $" [{DialNames.Trigger(a.Trigger)}: {a.Action.Describe()}]")));
        }
        await store.CloseAsync();
        return 0;
    }

    /// <summary>
    /// Loads, then plans a save with no edits: should write nothing. With a key id, plans turning off that key's
    /// "also with Ctrl/Alt/Shift" instead and shows what would be written. Read-only either way.
    /// </summary>
    private static async Task<int> Check(byte? unmirror)
    {
        using var store = new KeyStore();
        var s = await store.LoadAsync();
        var edited = s.Clone();
        if (unmirror is { } k) edited.Keys[k].WithModifiers = false;
        var r = await store.SaveAsync(s, edited, dryRun: true);
        Console.WriteLine(r.Writes.Count == 0 && r.Deleted.Count == 0 ? "OK: an unchanged layout writes nothing." : "Unchanged layout would still write:");
        foreach (var id in r.Writes)
        {
            Console.WriteLine($"  {id}: now  {Convert.ToHexString(s.Files.GetValueOrDefault(id) ?? [])}");
            Console.WriteLine($"  {id}: new  {Convert.ToHexString(r.Bytes[id])}");
        }
        foreach (var id in r.Deleted) Console.WriteLine($"  delete {id}");
        await store.CloseAsync();
        return r.Writes.Count == 0 ? 0 : 2;
    }

    /// <summary>
    /// Copies the keys, macros and dial modes of a backup into the profile the keyboard has now, through the normal save.
    /// For when the old profile is gone and its file ids belong to a new profile (2026-11-23: iCUE recreated the profile),
    /// so a plain restore no longer fits. Reads the old profile list, profile, assignments, key mappings and dial file
    /// from the backup, and the action/macro files from the keyboard (they're where the old assignments point). Deletes
    /// nothing. Without --apply only prints what it would write.
    /// </summary>
    private static async Task<int> Import(string dir, bool apply)
    {
        byte[] Bak(ushort id) => File.ReadAllBytes(Path.Combine(dir, $"{id}.bin"));
        var list = Bak(15);
        ushort profileId = BitConverter.ToUInt16(list, 4);
        var profile = Bak(profileId);
        ushort U(int o) => BitConverter.ToUInt16(profile, o);
        var snapshot = new Dictionary<ushort, byte[]> { [15] = list, [profileId] = profile };
        foreach (var id in new[] { U(6), U(14), U(18) }) if (id != 0) snapshot[id] = Bak(id);

        using var store = new KeyStore { Log = s => Console.WriteLine("  . " + s) };
        var current = await store.LoadAsync();
        Console.WriteLine($"Keyboard now: profile {current.ProfileId} \"{current.ProfileName}\": assignments {current.AssignFile}, key mappings {current.MapFile}, dial {current.DialFile}");

        using var old = new KeyStore { Snapshot = snapshot };
        var imported = await old.LoadAsync();
        await old.CloseAsync();
        Console.WriteLine($"Backup:       profile {imported.ProfileId} \"{imported.ProfileName}\": assignments {imported.AssignFile}, key mappings {imported.MapFile}, dial {imported.DialFile}");
        foreach (var (id, c) in imported.Keys.OrderBy(k => k.Key))
        {
            if (c.Foreign) { Console.WriteLine($"  key {id,3} (not on layout): {c.Others.Count} assignment(s) kept"); continue; }
            if (c.Main.Kind == ActionKind.Default && c.Fn.Kind == ActionKind.Default && c.Others.Count == 0) continue;
            var name = KeyLayout.Find(id)?.MainText ?? id.ToString();
            string how = c.Main.Macro is { } m ? (m.StopOnRelease ? " (while held)" : " (play once)") : "";
            Console.WriteLine($"  {name,-16} main: {c.Main.Describe(),-14}{how}" +
                $" fn: {c.Fn.Describe(),-14}{(c.WithModifiers || c.Main.Kind == ActionKind.Default ? "" : " (Ctrl/Alt/Shift keep the default)")}" +
                (c.Others.Count > 0 ? $" +{c.Others.Count} other" : ""));
        }
        for (int i = 0; i < imported.Dial.Count; i++)
            Console.WriteLine($"  dial mode {i + 1}: {DialNames.Name(imported.Dial[i].Type)}" +
                string.Concat(imported.Dial[i].Actions.Select(a => $" [{DialNames.Trigger(a.Trigger)}: {a.Action.Describe()}]")));

        // The old key files go in as they are, into the slots the new profile uses. Every file they point to must
        // still be on the keyboard (checked by MoveKeyFilesAsync); the action and macro files themselves aren't touched.
        var moves = new Dictionary<ushort, byte[]> { [current.AssignFile] = snapshot[U(6)], [current.MapFile] = snapshot[U(14)] };
        if (U(18) != 0 && current.DialFile != 0) moves[current.DialFile] = snapshot[U(18)];
        var r = await store.MoveKeyFilesAsync(current, moves, imported, apply);
        Console.WriteLine(apply ? "Written:" : "Would write (nothing written yet; add --apply):");
        foreach (var id in r.Writes)
        {
            string what = id == current.AssignFile ? $"assignments (old {U(6)})" : id == current.MapFile ? $"key mappings (old {U(14)})" : $"dial modes (old {U(18)})";
            Console.WriteLine($"  {id}: {what}, {moves[id].Length} bytes, as they were");
        }
        if (r.Writes.Count == 0) Console.WriteLine("  nothing: the keyboard already matches");
        else Console.WriteLine($"  then a new timestamp in profile {current.ProfileId}, so the keyboard reloads its keys");
        if (r.Backup != null) Console.WriteLine($"Backup of what was there before: {r.Backup}");
        await store.CloseAsync();
        return 0;
    }

    private static async Task<int> Restore(string dir)
    {
        using var store = new KeyStore();
        int n = await store.RestoreAsync(dir, new Progress<string>(Console.WriteLine));
        Console.WriteLine($"Restored {n} file(s).");
        await store.CloseAsync();
        return 0;
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
}
