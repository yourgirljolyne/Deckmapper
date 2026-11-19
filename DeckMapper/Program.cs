using System.Runtime.InteropServices;

namespace DeckMapper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (cmd is "dump" or "check" or "restore")
        {
            AttachConsole(-1);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            try
            {
                return cmd switch
                {
                    "dump" => Dump().GetAwaiter().GetResult(),
                    "check" => Check(args.Length > 1 ? byte.Parse(args[1]) : null).GetAwaiter().GetResult(),
                    _ => Restore(args[1]).GetAwaiter().GetResult(),
                };
            }
            catch (BragiException e) { Console.WriteLine(e.Message); return 1; }
        }

        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "DeckMapper.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("DeckMapper is already open.", "DeckMapper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        Application.Run(new MainForm());
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
