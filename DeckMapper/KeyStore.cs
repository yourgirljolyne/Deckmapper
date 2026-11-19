using System.Buffers.Binary;
using System.Text.Json;

namespace DeckMapper;

public enum ActionKind { Default, Disabled, Key, Media, Mouse, Macro, Kept }

/// <summary>What a key (or a dial turn) does. Key = one or more HID usages pressed together.</summary>
public sealed record KeyAction(ActionKind Kind, ushort[] Codes, string? KeptText = null)
{
    public static readonly KeyAction Default = new(ActionKind.Default, []);
    public static readonly KeyAction Disabled = new(ActionKind.Disabled, []);
    public static KeyAction Key(params ushort[] codes) => new(ActionKind.Key, codes);
    public static KeyAction Media(ushort code) => new(ActionKind.Media, [code]);
    public static KeyAction Mouse(ushort code) => new(ActionKind.Mouse, [code]);
    public static KeyAction FromMacro(Macro m) => new(ActionKind.Macro, []) { Macro = m };

    /// <summary>The steps, for <see cref="ActionKind.Macro"/>.</summary>
    public Macro? Macro { get; init; }

    /// <summary>True for actions that live in an action file on the keyboard.</summary>
    public bool NeedsFile => Kind is ActionKind.Key or ActionKind.Media or ActionKind.Mouse or ActionKind.Macro or ActionKind.Kept;

    public string Describe() => Kind switch
    {
        ActionKind.Default => "Default",
        ActionKind.Disabled => "Disabled",
        ActionKind.Key => Codes.Length == 1 ? KeyCodes.KeyName(Codes[0]) : KeyCodes.ComboName(Codes),
        ActionKind.Media => KeyCodes.MediaName(Codes[0]),
        ActionKind.Mouse => KeyCodes.MouseName(Codes[0]),
        ActionKind.Macro => Macro!.Summary,
        _ => KeptText ?? "Kept as is",
    };

    public bool Same(KeyAction o) => Kind == o.Kind && Codes.SequenceEqual(o.Codes) && KeptText == o.KeptText
        && (Macro == null ? o.Macro == null : o.Macro != null && Macro.Same(o.Macro));

    /// <summary>Assignment options to write: macros carry their own; keys keep what they had unless it was a macro's.</summary>
    public ushort OptionsFor(ushort loaded) => Kind switch
    {
        ActionKind.Kept => loaded,
        ActionKind.Macro => Macro!.Options,
        _ => (loaded & 16) != 0 || loaded == 0 ? KeyStore.KeyOptions : loaded,
    };
}

/// <summary>An assignment exactly as stored on the keyboard: layer flags, action file, trigger options.</summary>
public sealed record RawAssign(ushort Flags, ushort FileId, ushort Options);

/// <summary>Everything DeckMapper knows about one key's assignments.</summary>
public sealed class KeyConfig
{
    public KeyAction Main = KeyAction.Default;
    public KeyAction Fn = KeyAction.Default;
    /// <summary>Main-layer action also applies while Ctrl, Alt or Shift is held (so Alt + F4 still sends F4).</summary>
    public bool WithModifiers = true;
    /// <summary>Assignments DeckMapper doesn't edit (FN2, custom Ctrl/Alt/Shift layers). Written back unchanged.</summary>
    public List<RawAssign> Others = [];
    /// <summary>Action files these layers used when loaded (reused on save to avoid orphans).</summary>
    public ushort MainFile, FnFile;
    /// <summary>Options of the loaded assignment for Kept actions (macros, text).</summary>
    public ushort MainOptions, FnOptions;
    public byte OriginalMapByte;
    /// <summary>Layers (mapping bits) that were switched off with no assignment when loaded, i.e. really disabled.</summary>
    public byte DisabledMask;
    /// <summary>A key id that isn't on DeckMapper's layout: its assignments and mapping byte are kept exactly.</summary>
    public bool Foreign;
    /// <summary>Set from DeckMapper's settings: don't flag this key's Ctrl/Alt/Shift gap (kept on the PC, not the keyboard).</summary>
    public bool IgnoreGap;

    public KeyConfig Clone()
    {
        var c = (KeyConfig)MemberwiseClone();
        c.Others = [.. Others];
        return c;
    }

    /// <summary>Main layer remapped but Ctrl/Alt/Shift still fall back to the default (Web Hub's behaviour).</summary>
    public bool ModifierGap => HasModifierGap && !IgnoreGap;

    /// <summary>The gap itself, whether or not the warning is ignored for this key.</summary>
    public bool HasModifierGap => Main.Kind is not (ActionKind.Default or ActionKind.Disabled) && !WithModifiers
        && !Others.Any(o => o.Flags is Flags.Ctrl or Flags.Alt or Flags.Shift);
}

/// <summary>Dial mode: a built-in behaviour or custom actions per turn/press, plus its ring colour.</summary>
public sealed class DialMode
{
    public byte Type;
    public byte[] Color = [255, 255, 255, 255]; // R, G, B, A
    public List<DialAction> Actions = [];
    public DialMode Clone() => new() { Type = Type, Color = [.. Color], Actions = Actions.Select(a => a with { }).ToList() };
}

public sealed record DialAction(byte Trigger, ushort Options, KeyAction Action, ushort FileId);

internal static class Flags
{
    public const ushort Main = 0, Fn = 1, Fn2 = 2, Ctrl = 4, Alt = 8, Shift = 16;
    /// <summary>Key-mapping bit for a layer flag (Web Hub: KEY, FN, FN2, CTRL, ALT, SHIFT _FUNCTION_ENABLED).</summary>
    public static int Bit(ushort flags) => flags switch { Main => 0, Fn => 1, Fn2 => 2, Ctrl => 3, Alt => 4, Shift => 5, _ => -1 };
}

/// <summary>A snapshot of the keyboard's key setup, as files plus the parsed model.</summary>
public sealed class KeyboardState
{
    public ushort ProfileId, AssignFile, MapFile, DialFile;
    public ushort AssignHeader = 45, DialHeader = 22;
    public byte[] MapBytes = [];
    public Dictionary<byte, KeyConfig> Keys = [];
    public List<DialMode> Dial = [];
    /// <summary>Every file read while loading (id → bytes), for backups and change detection.</summary>
    public Dictionary<ushort, byte[]> Files = [];
    public string ProfileName = "";
    /// <summary>Key order in the loaded assignments file (kept on save so an unchanged layout writes nothing).</summary>
    public List<byte> AssignOrder = [];

    public KeyboardState Clone()
    {
        var s = (KeyboardState)MemberwiseClone();
        s.Keys = Keys.ToDictionary(k => k.Key, k => k.Value.Clone());
        s.Dial = Dial.Select(d => d.Clone()).ToList();
        return s;
    }
}

/// <summary>Reads and writes key assignments, key mappings and dial modes in the format Corsair Web Hub uses.</summary>
public sealed class KeyStore : IDisposable
{
    private const ushort RemapHeader = 1902, MacroHeader = 12460, TextHeader = 940;
    public const ushort KeyOptions = 0x41;   // TRIGGER_ON_PRESS_PRIMARY | STOP_ON_RELEASE_PRIMARY (what Web Hub writes)
    private const ushort DialOptions = 1;     // TRIGGER_ON_EACH_DIAL_TICK
    /// <summary>DeckMapper takes new action file ids from here downwards (Web Hub counts up from 28000, SlotDeck down from 32699).</summary>
    public const ushort OwnTop = 31999, OwnBottom = 31000;

    private Bragi? _b;
    public Action<string>? Log { get; set; }

    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeckMapper", "backups");

    private async Task<Bragi> DeviceAsync()
    {
        if (_b != null) return _b;
        var b = Bragi.Open();
        if (b == null)
        {
            bool receiver = Bragi.ReceiverIds.Any(pid => HidDevice.Describe(Bragi.Vid, pid).Any());
            throw new BragiException(receiver
                ? "The keyboard is on the wireless receiver. Plug in the USB cable: key settings can only be changed over the cable."
                : "Keyboard not found. Plug the VANGUARD AIR 99 in with the USB cable and close iCUE.");
        }
        b.Log = Log;
        await b.StartSessionAsync();
        return _b = b;
    }

    public async Task CloseAsync()
    {
        if (_b == null) return;
        await _b.StopSessionAsync();
        _b.Dispose();
        _b = null;
    }

    public void Dispose() => _b?.Dispose();

    private async Task<Bragi> RetryAsync()
    {
        _b?.Dispose();
        _b = null;
        return await DeviceAsync();
    }

    private async Task<byte[]?> ReadAsync(ushort id, Dictionary<ushort, byte[]> files)
    {
        if (id == 0) return null;
        if (files.TryGetValue(id, out var cached)) return cached;
        var b = await DeviceAsync();
        var d = await b.ReadFileAsync(id);
        if (d != null) files[id] = d;
        return d;
    }

    // ---------------- loading ----------------

    public async Task<KeyboardState> LoadAsync()
    {
        try { return await LoadOnceAsync(); }
        catch (BragiException) { await RetryAsync(); return await LoadOnceAsync(); }
    }

    private async Task<KeyboardState> LoadOnceAsync()
    {
        var s = new KeyboardState();
        var list = await ReadAsync(15, s.Files) ?? throw new BragiException("The keyboard has no profile list.");
        if (list.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(2)) == 0)
            throw new BragiException("The keyboard has no profiles.");
        s.ProfileId = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(4));
        var p = await ReadAsync(s.ProfileId, s.Files) ?? throw new BragiException($"Profile file {s.ProfileId} is missing.");
        ushort U(int o) => o + 2 <= p.Length ? BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o)) : (ushort)0;
        s.AssignFile = U(6);
        s.MapFile = U(14);
        s.DialFile = U(18);
        int nameLen = U(30);
        if (32 + nameLen <= p.Length) s.ProfileName = System.Text.Encoding.UTF8.GetString(p, 32, nameLen).TrimEnd('\0');
        if (s.MapFile == 0 || s.AssignFile == 0)
            throw new BragiException("This profile has no key assignment files yet. Remap any key once in Corsair Web Hub, then try again.");

        s.MapBytes = await ReadAsync(s.MapFile, s.Files) ?? throw new BragiException("The key mapping file is missing.");
        var assign = await ReadAsync(s.AssignFile, s.Files) ?? [];

        // Parse assignments (version 2: header, 2 bytes, then key, count, count × (flags, file, options)).
        var raw = new Dictionary<byte, List<RawAssign>>();
        if (assign.Length >= 4)
        {
            s.AssignHeader = BinaryPrimitives.ReadUInt16LittleEndian(assign);
            if (s.AssignHeader != 45)
                throw new BragiException($"Unknown key assignment format ({s.AssignHeader}). DeckMapper only knows version 45.");
            int a = 4;
            while (a + 2 <= assign.Length)
            {
                byte key = assign[a];
                int n = assign[a + 1];
                if (!s.AssignOrder.Contains(key)) s.AssignOrder.Add(key);
                a += 2;
                var lst = raw.TryGetValue(key, out var e) ? e : raw[key] = [];
                for (int j = 0; j < n && a + 6 <= assign.Length; j++, a += 6)
                    lst.Add(new(BinaryPrimitives.ReadUInt16LittleEndian(assign.AsSpan(a)),
                        BinaryPrimitives.ReadUInt16LittleEndian(assign.AsSpan(a + 2)),
                        BinaryPrimitives.ReadUInt16LittleEndian(assign.AsSpan(a + 4))));
            }
        }

        foreach (var pk in KeyLayout.Keys)
        {
            if (pk.Id == KeyLayout.FnKey) continue;
            var c = new KeyConfig { OriginalMapByte = MapByte(s.MapBytes, pk.Id) };
            var list2 = raw.TryGetValue(pk.Id, out var r) ? r : [];
            RawAssign? Get(ushort f) => list2.FirstOrDefault(x => x.Flags == f);
            bool Cleared(ushort f) => c.OriginalMapByte != 0 && (c.OriginalMapByte & (1 << Flags.Bit(f))) == 0;

            async Task<KeyAction> LayerAsync(ushort f)
            {
                var ra = Get(f);
                if (ra != null)
                {
                    var act = await ReadActionAsync(ra.FileId, s.Files);
                    return act.Macro is { } m ? KeyAction.FromMacro(m with { StopOnRelease = (ra.Options & 64) != 0 }) : act;
                }
                return Cleared(f) ? KeyAction.Disabled : KeyAction.Default;
            }

            foreach (var f in new[] { Flags.Main, Flags.Fn, Flags.Fn2, Flags.Ctrl, Flags.Alt, Flags.Shift })
                if (Cleared(f) && Get(f) == null) c.DisabledMask |= (byte)(1 << Flags.Bit(f));
            c.Main = await LayerAsync(Flags.Main);
            c.Fn = await LayerAsync(Flags.Fn);
            c.MainFile = Get(Flags.Main)?.FileId ?? 0;
            c.FnFile = Get(Flags.Fn)?.FileId ?? 0;
            c.MainOptions = Get(Flags.Main)?.Options ?? KeyOptions;
            c.FnOptions = Get(Flags.Fn)?.Options ?? KeyOptions;

            // Ctrl/Alt/Shift layers that repeat the main action are folded into WithModifiers.
            var mods = new[] { Flags.Ctrl, Flags.Alt, Flags.Shift };
            bool mirrored = true;
            foreach (var f in mods)
            {
                var act = await LayerAsync(f);
                mirrored &= c.Main.Kind != ActionKind.Default && act.Same(c.Main);
            }
            c.WithModifiers = c.Main.Kind == ActionKind.Default || mirrored;
            c.Others = list2.Where(x => x.Flags is not (Flags.Main or Flags.Fn)
                && !(mirrored && x.Flags is Flags.Ctrl or Flags.Alt or Flags.Shift)).ToList();
            s.Keys[pk.Id] = c;
        }
        foreach (var (key, list2) in raw)
            if (!s.Keys.ContainsKey(key))
                s.Keys[key] = new KeyConfig { Foreign = true, Others = list2, OriginalMapByte = MapByte(s.MapBytes, key) };

        // Dial modes: header, count, then per mode: type, RGBA, n, n × (trigger, 0, options, file).
        var dial = await ReadAsync(s.DialFile, s.Files);
        if (dial is { Length: >= 4 })
        {
            s.DialHeader = BinaryPrimitives.ReadUInt16LittleEndian(dial);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(dial.AsSpan(2));
            int d = 4;
            for (int i = 0; i < count && d + 6 <= dial.Length; i++)
            {
                var m = new DialMode { Type = dial[d], Color = dial.AsSpan(d + 1, 4).ToArray() };
                int n = dial[d + 5];
                d += 6;
                for (int j = 0; j < n && d + 6 <= dial.Length; j++, d += 6)
                {
                    ushort file = BinaryPrimitives.ReadUInt16LittleEndian(dial.AsSpan(d + 4));
                    m.Actions.Add(new(dial[d], BinaryPrimitives.ReadUInt16LittleEndian(dial.AsSpan(d + 2)),
                        await ReadActionAsync(file, s.Files), file));
                }
                s.Dial.Add(m);
            }
        }
        return s;
    }

    private static byte MapByte(byte[] map, byte key) => 2 + key < map.Length ? map[2 + key] : (byte)0;

    private async Task<KeyAction> ReadActionAsync(ushort fileId, Dictionary<ushort, byte[]> files)
    {
        var d = await ReadAsync(fileId, files);
        if (d == null || d.Length < 2) return new(ActionKind.Kept, [], $"Missing action file {fileId}");
        return DecodeAction(d);
    }

    public static KeyAction DecodeAction(byte[] d)
    {
        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(d);
        if (header == MacroHeader)
            return Macro.Decode(d, false) is { } m ? KeyAction.FromMacro(m) : new(ActionKind.Kept, [], "Macro (made in Web Hub)");
        if (header == TextHeader) return new(ActionKind.Kept, [], "Text (made in Web Hub)");
        if (header != RemapHeader || d.Length < 10) return new(ActionKind.Kept, [], "Unknown action");
        switch (d[2])
        {
            case 1:
                int n = d[8];
                var codes = d.AsSpan(9, Math.Min(n, d.Length - 9)).ToArray().Select(x => (ushort)x).ToArray();
                return KeyAction.Key(codes);
            case 2: return KeyAction.Mouse(d[8]);
            case 3: return d.Length >= 10 ? KeyAction.Media(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8))) : KeyAction.Default;
            case 4: return new(ActionKind.Kept, [], "Internal function");
            case 5: return new(ActionKind.Kept, [], "Gamepad button");
            default: return new(ActionKind.Kept, [], "Unknown action");
        }
    }

    /// <summary>Remap action file: u16 1902, type, u16 duration, 3 zero bytes, then the target(s).</summary>
    public static byte[] EncodeAction(KeyAction a)
    {
        if (a.Kind == ActionKind.Macro) return a.Macro!.Encode();
        var o = new List<byte> { RemapHeader & 0xFF, RemapHeader >> 8, 0, 0, 0, 0, 0, 0 };
        switch (a.Kind)
        {
            case ActionKind.Key:
                o[2] = 1;
                o.Add((byte)a.Codes.Length);
                o.AddRange(a.Codes.Select(c => (byte)c));
                break;
            case ActionKind.Mouse:
                o[2] = 2;
                o.Add((byte)a.Codes[0]);
                break;
            case ActionKind.Media:
                o[2] = 3;
                o.Add((byte)(a.Codes[0] & 0xFF));
                o.Add((byte)(a.Codes[0] >> 8));
                break;
            default: throw new InvalidOperationException("No file for " + a.Kind);
        }
        return [.. o];
    }

    // ---------------- saving ----------------

    /// <summary>The files a state turns into, plus which ids it newly needs.</summary>
    private sealed class Plan
    {
        public Dictionary<ushort, byte[]> Writes = [];
        public HashSet<ushort> Referenced = [];
        public List<string> Notes = [];
    }

    /// <summary>
    /// Writes <paramref name="edited"/> to the keyboard. <paramref name="loaded"/> must be the state it was edited from:
    /// the keyboard is re-read first and nothing is written if it changed in the meantime. A backup is saved before writing.
    /// Returns the backup folder.
    /// </summary>
    public async Task<SaveResult> SaveAsync(KeyboardState loaded, KeyboardState edited, IProgress<string>? status = null, bool dryRun = false)
    {
        status?.Report("Checking the keyboard");
        var fresh = await LoadAsync();
        foreach (var id in new[] { loaded.AssignFile, loaded.MapFile, loaded.DialFile })
        {
            if (id == 0) continue;
            if (!fresh.Files.TryGetValue(id, out var now) || !loaded.Files.TryGetValue(id, out var was) || !now.SequenceEqual(was))
                throw new BragiException("The keys changed on the keyboard since DeckMapper loaded them (Web Hub?). Press Reload, then make your changes again.");
        }

        string? backup = dryRun ? null : SaveBackup(fresh, "before-apply");
        var b = await DeviceAsync();
        var plan = new Plan();

        // ---- action files ----
        var used = new HashSet<ushort>();      // ids taken in the new layout
        var owner = new Dictionary<ushort, byte[]>();
        foreach (var c in edited.Keys.Values)
            foreach (var o in c.Others) used.Add(o.FileId);
        foreach (var c in edited.Keys.Values)
        {
            if (c.Main.Kind == ActionKind.Kept && c.MainFile != 0) used.Add(c.MainFile);
            if (c.Fn.Kind == ActionKind.Kept && c.FnFile != 0) used.Add(c.FnFile);
        }
        foreach (var m in edited.Dial)
            foreach (var a in m.Actions.Where(a => a.Action.Kind == ActionKind.Kept)) used.Add(a.FileId);

        var freshIds = new Queue<ushort>();
        async Task<ushort> NewIdAsync()
        {
            for (ushort id = freshIds.Count > 0 ? freshIds.Dequeue() : OwnTop; id >= OwnBottom; id--)
            {
                if (used.Contains(id) || fresh.Files.ContainsKey(id)) continue;
                if (await b.FileExistsAsync(id)) continue;
                used.Add(id);
                freshIds.Enqueue((ushort)(id - 1));
                return id;
            }
            throw new BragiException("No free file ids left for DeckMapper.");
        }

        // Use the file the layer had before if nothing else claims it; otherwise a new id.
        async Task<ushort> FileForAsync(KeyAction a, ushort previous)
        {
            var bytes = EncodeAction(a);
            if (previous != 0 && !used.Contains(previous)) { used.Add(previous); owner[previous] = bytes; return previous; }
            var id = await NewIdAsync();
            owner[id] = bytes;
            return id;
        }

        var newAssign = new List<byte>();
        newAssign.AddRange(Le16(edited.AssignHeader));
        newAssign.AddRange(new byte[2]);
        var map = (byte[])fresh.MapBytes.Clone();

        int Rank(byte k) { int i = fresh.AssignOrder.IndexOf(k); return i < 0 ? 1000 + k : i; }
        foreach (var (key, c) in edited.Keys.OrderBy(k => Rank(k.Key)))
        {
            var entries = new List<RawAssign>();
            byte orig = MapByte(fresh.MapBytes, key);
            if (c.Foreign)
            {
                newAssign.Add(key);
                newAssign.Add((byte)c.Others.Count);
                foreach (var e in c.Others) { newAssign.AddRange([.. Le16(e.Flags), .. Le16(e.FileId), .. Le16(e.Options)]); plan.Referenced.Add(e.FileId); }
                continue;
            }
            int bits = orig == 0 ? 0 : (orig & 0xC4) | 0x3B; // keep FN2 and unknown bits, reset the layers we manage
            void Clear(ushort f) { int bit = Flags.Bit(f); if (bit >= 0) bits &= ~(1 << bit); }

            if (c.Main.NeedsFile)
            {
                ushort file = c.Main.Kind == ActionKind.Kept ? c.MainFile : await FileForAsync(c.Main, c.MainFile);
                ushort opt = c.Main.OptionsFor(c.MainOptions);
                entries.Add(new(Flags.Main, file, opt));
                Clear(Flags.Main);
                if (c.WithModifiers)
                    foreach (var f in new[] { Flags.Ctrl, Flags.Alt, Flags.Shift }) { entries.Add(new(f, file, opt)); Clear(f); }
            }
            else if (c.Main.Kind == ActionKind.Disabled)
            {
                Clear(Flags.Main);
                if (c.WithModifiers) { Clear(Flags.Ctrl); Clear(Flags.Alt); Clear(Flags.Shift); }
            }

            if (c.Fn.NeedsFile)
            {
                ushort file = c.Fn.Kind == ActionKind.Kept ? c.FnFile : await FileForAsync(c.Fn, c.FnFile);
                entries.Add(new(Flags.Fn, file, c.Fn.OptionsFor(c.FnOptions)));
                Clear(Flags.Fn);
            }
            else if (c.Fn.Kind == ActionKind.Disabled) Clear(Flags.Fn);

            foreach (var o in c.Others)
            {
                if (c.WithModifiers && c.Main.Kind != ActionKind.Default && o.Flags is Flags.Ctrl or Flags.Alt or Flags.Shift) continue;
                entries.Add(o);
                Clear(o.Flags);
            }
            // Ctrl/Alt/Shift layers disabled in the original without an assignment stay disabled.
            if (!c.WithModifiers)
                foreach (var f in new[] { Flags.Ctrl, Flags.Alt, Flags.Shift })
                    if ((c.DisabledMask & (1 << Flags.Bit(f))) != 0 && !entries.Any(e => e.Flags == f)) Clear(f);

            if (orig != 0 && 2 + key < map.Length) map[2 + key] = (byte)bits;
            else if (entries.Count > 0) plan.Notes.Add($"key {key} is not in the key mapping file; skipped");
            if (entries.Count == 0 || orig == 0) continue;

            newAssign.Add(key);
            newAssign.Add((byte)entries.Count);
            foreach (var e in entries)
            {
                newAssign.AddRange(Le16(e.Flags));
                newAssign.AddRange(Le16(e.FileId));
                newAssign.AddRange(Le16(e.Options));
                plan.Referenced.Add(e.FileId);
            }
        }

        // ---- dial ----
        byte[]? newDial = null;
        if (edited.DialFile != 0)
        {
            var d = new List<byte>();
            d.AddRange(Le16(edited.DialHeader));
            d.AddRange(Le16((ushort)edited.Dial.Count));
            foreach (var m in edited.Dial)
            {
                var acts = new List<(byte trig, ushort opt, ushort file)>();
                foreach (var a in m.Actions)
                {
                    if (a.Action.Kind == ActionKind.Kept) { acts.Add((a.Trigger, a.Options, a.FileId)); continue; }
                    if (!a.Action.NeedsFile) continue;
                    acts.Add((a.Trigger, DialOptions, await FileForAsync(a.Action, a.FileId)));
                }
                d.Add(m.Type);
                d.AddRange(m.Color);
                d.Add((byte)acts.Count);
                foreach (var (trig, opt, file) in acts)
                {
                    d.Add(trig);
                    d.Add(0);
                    d.AddRange(Le16(opt));
                    d.AddRange(Le16(file));
                    plan.Referenced.Add(file);
                }
            }
            newDial = [.. d];
        }

        foreach (var (id, bytes) in owner)
            if (!fresh.Files.TryGetValue(id, out var old) || !old.SequenceEqual(bytes)) plan.Writes[id] = bytes;
        byte[] assignBytes = [.. newAssign];
        if (!assignBytes.SequenceEqual(fresh.Files.GetValueOrDefault(fresh.AssignFile) ?? [])) plan.Writes[fresh.AssignFile] = assignBytes;
        if (!map.SequenceEqual(fresh.MapBytes)) plan.Writes[fresh.MapFile] = map;
        if (newDial != null && !newDial.SequenceEqual(fresh.Files.GetValueOrDefault(fresh.DialFile) ?? [])) plan.Writes[fresh.DialFile] = newDial;

        // DeckMapper's own action files that nothing points at any more.
        var stale = fresh.Files.Keys.Where(id => id is >= OwnBottom and <= OwnTop && !plan.Referenced.Contains(id)).ToList();

        var result = new SaveResult(backup, order0(), stale);
        List<ushort> order0() => plan.Writes.Keys.OrderBy(id => id == fresh.AssignFile || id == fresh.MapFile || id == fresh.DialFile ? 1 : 0)
            .ThenBy(id => id == fresh.MapFile ? 0 : 1).ToList();
        result.Bytes = plan.Writes;
        if (dryRun || (plan.Writes.Count == 0 && stale.Count == 0)) return result;

        // Action files first, then the files that point at them.
        var order = result.Writes;
        await b.SetPropertyAsync(Bragi.PropOperatingMode, Bragi.ModeHostControlled);
        try
        {
            int i = 0;
            foreach (var id in order)
            {
                status?.Report($"Writing {++i} of {order.Count}");
                Log?.Invoke($"write {id}: {Convert.ToHexString(plan.Writes[id])}");
                await b.WriteFileAsync(id, plan.Writes[id]);
            }
            foreach (var id in stale)
            {
                Log?.Invoke($"delete unused {id}");
                await b.DeleteFileAsync(id);
            }
        }
        finally { await b.SetPropertyAsync(Bragi.PropOperatingMode, Bragi.ModeSelfOperated); }
        foreach (var n in plan.Notes) Log?.Invoke(n);
        return result;
    }

    /// <summary>What a save wrote (or would write): file ids in write order, and DeckMapper files it deleted.</summary>
    public sealed record SaveResult(string? Backup, List<ushort> Writes, List<ushort> Deleted)
    {
        public Dictionary<ushort, byte[]> Bytes { get; set; } = [];
    }

    private static byte[] Le16(ushort v) => [(byte)v, (byte)(v >> 8)];

    // ---------------- backups ----------------

    private sealed record Manifest(string Profile, ushort ProfileId, ushort AssignFile, ushort MapFile, ushort DialFile, ushort[] Files, string Saved);

    /// <summary>Saves every file of a loaded state to a new folder under <see cref="BackupRoot"/>.</summary>
    public static string SaveBackup(KeyboardState s, string label)
    {
        string dir = Path.Combine(BackupRoot, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{label}");
        Directory.CreateDirectory(dir);
        foreach (var (id, bytes) in s.Files) File.WriteAllBytes(Path.Combine(dir, $"{id}.bin"), bytes);
        var m = new Manifest(s.ProfileName, s.ProfileId, s.AssignFile, s.MapFile, s.DialFile, [.. s.Files.Keys.Order()], DateTime.Now.ToString("s"));
        File.WriteAllText(Path.Combine(dir, "backup.json"), JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        return dir;
    }

    /// <summary>
    /// Puts a backup back: action files, then key mappings, assignments and dial modes. The profile file and the
    /// profile list are never written. Returns the number of files written.
    /// </summary>
    public async Task<int> RestoreAsync(string dir, IProgress<string>? status = null)
    {
        var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(dir, "backup.json")))
            ?? throw new InvalidDataException("backup.json is empty");
        var current = await LoadAsync();
        if (current.ProfileId != m.ProfileId || current.AssignFile != m.AssignFile || current.MapFile != m.MapFile)
            throw new BragiException("That backup is from a different profile layout than the keyboard has now.");
        SaveBackup(current, "before-restore");

        var skip = new HashSet<ushort> { 15, m.ProfileId };
        var pointers = new[] { m.MapFile, m.AssignFile, m.DialFile };
        var ids = m.Files.Where(id => !skip.Contains(id)).OrderBy(id => pointers.Contains(id) ? 1 : 0).ToList();
        var b = await DeviceAsync();
        int written = 0;
        await b.SetPropertyAsync(Bragi.PropOperatingMode, Bragi.ModeHostControlled);
        try
        {
            foreach (var id in ids)
            {
                var bytes = File.ReadAllBytes(Path.Combine(dir, $"{id}.bin"));
                if (current.Files.TryGetValue(id, out var now) && now.SequenceEqual(bytes)) continue;
                status?.Report($"Restoring file {id}");
                await b.WriteFileAsync(id, bytes);
                written++;
            }
        }
        finally { await b.SetPropertyAsync(Bragi.PropOperatingMode, Bragi.ModeSelfOperated); }
        return written;
    }
}
