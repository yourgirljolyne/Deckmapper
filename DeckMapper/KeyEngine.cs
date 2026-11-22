namespace DeckMapper;

/// <summary>
/// Plays the keyboard's own key assignments from the PC while the keyboard is in host-controlled (software) mode,
/// e.g. while SignalRGB streams lighting. In that mode the firmware stops running remaps and macros: remapped keys
/// send nothing, and every key's up/down state is reported on the notification channel (usage page 0xFF42, usage 2)
/// as a bitmap indexed by key id. That's what iCUE reads; this does the same with DeckMapper's assignments.
/// In self-operated mode the engine does nothing, because the keyboard runs its assignments itself.
/// </summary>
internal sealed class KeyEngine : IDisposable
{
    private const ushort NotifyUsage = 0x02;
    private const byte KeyStateReport = 0x02;
    private const byte FnKey = KeyLayout.FnKey;
    private static readonly TimeSpan ModePoll = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _run;
    private volatile bool _reload = true;

    private Dictionary<byte, KeyConfig> _keys = [];
    private bool[] _down = new bool[256];
    /// <summary>What we pressed for each physical key, so its release undoes exactly that.</summary>
    private readonly Dictionary<byte, Held> _held = [];

    private sealed class Held
    {
        public List<ushort> Keys = [];
        public List<ushort> Media = [];
        public List<int> Mouse = [];
        public CancellationTokenSource? Macro;
    }

    /// <summary>True while the keyboard is in software mode and the engine is playing assignments.</summary>
    public bool Active { get; private set; }
    public string Status { get; private set; } = "Starting";
    public event Action? StatusChanged;
    public Action<string>? Log { get; set; }

    public bool Running => _run is { IsCompleted: false };

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _run = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_run != null) try { await _run; } catch { /* stopping */ }
        ReleaseAll();
        SetStatus("Off", false);
    }

    /// <summary>Re-read the assignments (after DeckMapper applied changes).</summary>
    public void Reload() => _reload = true;

    private void SetStatus(string s, bool active)
    {
        if (s == Status && active == Active) return;
        Status = s;
        Active = active;
        Log?.Invoke(s);
        StatusChanged?.Invoke();
    }

    // ---------------- main loop ----------------

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await SessionAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is BragiException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                Log?.Invoke("engine: " + e.Message);
            }
            ReleaseAll();
            SetStatus("Waiting for the keyboard (USB cable)", false);
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One connection to the keyboard: polls the mode, reads reports while in software mode.</summary>
    private async Task SessionAsync(CancellationToken ct)
    {
        using var bragi = Bragi.Open() ?? throw new IOException("keyboard not found");
        using var notify = HidDevice.Open(Bragi.Vid, Bragi.PidAir99Wired, Bragi.UsagePage, NotifyUsage)
            ?? throw new IOException("notification channel not found");
        await bragi.StartSessionAsync();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reader = ReadReportsAsync(notify, linked.Token);
            try
            {
                while (!ct.IsCancellationRequested && !reader.IsCompleted)
                {
                    uint mode = await bragi.GetPropertyAsync(Bragi.PropOperatingMode);
                    if (mode == Bragi.ModeHostControlled)
                    {
                        if (_reload || !Active)
                        {
                            await LoadAsync();
                            _reload = false;
                        }
                        SetStatus("Keyboard in software mode (SignalRGB?): DeckMapper is playing your custom keys", true);
                    }
                    else
                    {
                        if (Active) ReleaseAll();
                        SetStatus("Keyboard runs your custom keys itself", false);
                    }
                    await Task.WhenAny(reader, Task.Delay(ModePoll, ct));
                }
            }
            finally
            {
                linked.Cancel();
                try { await reader; } catch { /* closing */ }
            }
            if (reader.IsFaulted) throw reader.Exception!.InnerException ?? reader.Exception;
        }
        finally { await bragi.StopSessionAsync(); }
    }

    private async Task LoadAsync()
    {
        using var store = new KeyStore { Log = Log };
        try
        {
            var s = await store.LoadAsync();
            lock (_gate) _keys = s.Keys;
            Log?.Invoke($"engine: loaded {s.Keys.Values.Count(c => c.Main.Kind != ActionKind.Default || c.Fn.Kind != ActionKind.Default)} assigned keys");
        }
        finally { await store.CloseAsync(); }
    }

    private async Task ReadReportsAsync(HidDevice notify, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] r;
            try { r = await notify.ReadAsync(ct); }
            catch (OperationCanceledException) { return; }
            // [0, 0, 0, type, bitmap...]: bit n of the bitmap = key id n is down.
            if (r.Length < 5 || r[3] != KeyStateReport) continue;
            if (!Active) { SyncWithoutActing(r); continue; }
            lock (_gate) OnKeyState(r.AsSpan(4));
        }
    }

    /// <summary>Tracks key state while inactive, so a key held across a mode switch isn't seen as a new press.</summary>
    private void SyncWithoutActing(byte[] r)
    {
        lock (_gate)
        {
            var now = new bool[256];
            for (int i = 0; i < r.Length - 4 && i < 32; i++)
                for (int bit = 0; bit < 8; bit++) now[i * 8 + bit] = (r[4 + i] & (1 << bit)) != 0;
            _down = now;
        }
    }

    // ---------------- key state -> actions ----------------

    private void OnKeyState(ReadOnlySpan<byte> bitmap)
    {
        var now = new bool[256];
        for (int i = 0; i < bitmap.Length && i < 32; i++)
            for (int bit = 0; bit < 8; bit++) now[i * 8 + bit] = (bitmap[i] & (1 << bit)) != 0;

        for (int id = 0; id < 256; id++)
        {
            if (now[id] == _down[id]) continue;
            if (now[id]) Press((byte)id, now);
            else Release((byte)id);
        }
        _down = now;
    }

    private void Press(byte id, bool[] now)
    {
        if (id == FnKey || !_keys.TryGetValue(id, out var c) || c.Foreign) return;
        byte map = c.OriginalMapByte;
        if (map == 0) return;

        KeyAction action;
        if (now[FnKey])
        {
            if ((map & 0b10) != 0) return;      // FN layer not remapped: the keyboard handles it
            action = c.Fn;
        }
        else
        {
            if ((map & 0b1) != 0) return;       // not remapped: the keyboard sends the key itself
            // A Ctrl/Alt/Shift layer left at its default (Web Hub style, not mirrored) is the keyboard's job too.
            bool ctrl = now[105] || now[109], alt = now[107] || now[111], shift = now[106] || now[110];
            if ((ctrl && (map & 0b1000) != 0) || (alt && (map & 0b10000) != 0) || (shift && (map & 0b100000) != 0)) return;
            action = c.Main;
        }

        var held = new Held();
        switch (action.Kind)
        {
            case ActionKind.Key:
                foreach (var k in action.Codes) { InputSender.KeyDown(k); held.Keys.Add(k); }
                break;
            case ActionKind.Media:
                InputSender.Media(action.Codes[0], up: false);
                held.Media.Add(action.Codes[0]);
                break;
            case ActionKind.Mouse:
                InputSender.MouseButton(action.Codes[0], up: false);
                held.Mouse.Add(action.Codes[0]);
                break;
            case ActionKind.Macro when action.Macro is { } m:
                held.Macro = new CancellationTokenSource();
                _ = PlayMacroAsync(m, held.Macro.Token);
                if (!m.StopOnRelease) held.Macro = null; // play once: runs to the end on its own
                break;
            default:
                return; // Disabled, Default, Kept (Web Hub text actions etc.): nothing to send
        }
        _held[id] = held;
    }

    private void Release(byte id)
    {
        if (!_held.Remove(id, out var h)) return;
        for (int i = h.Keys.Count - 1; i >= 0; i--) InputSender.KeyUp(h.Keys[i]);
        foreach (var m in h.Media) InputSender.Media(m, up: true);
        foreach (var b in h.Mouse) InputSender.MouseButton(b, up: true);
        h.Macro?.Cancel();
    }

    private void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var id in _held.Keys.ToList()) Release(id);
            Array.Clear(_down);
        }
    }

    private static async Task PlayMacroAsync(Macro m, CancellationToken ct)
    {
        var keys = new List<ushort>();
        var buttons = new List<int>();
        try
        {
            foreach (var s in m.Steps)
            {
                ct.ThrowIfCancellationRequested();
                switch (s.Kind)
                {
                    case StepKind.KeyDown: InputSender.KeyDown((ushort)s.A); keys.Add((ushort)s.A); break;
                    case StepKind.KeyUp: InputSender.KeyUp((ushort)s.A); keys.Remove((ushort)s.A); break;
                    case StepKind.MouseDown: InputSender.MouseButton(s.A, false); buttons.Add(s.A); break;
                    case StepKind.MouseUp: InputSender.MouseButton(s.A, true); buttons.Remove(s.A); break;
                    case StepKind.Wait: await Task.Delay(s.A, ct); break;
                    case StepKind.MoveTo: InputSender.MoveTo(s.A, s.B); break;
                    case StepKind.MoveBy: InputSender.MoveBy(s.A, s.B); break;
                    case StepKind.Scroll: InputSender.Scroll(s.A, false); break;
                    case StepKind.ScrollSideways: InputSender.Scroll(s.A, true); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Never leave anything stuck down when a macro is stopped early.
            foreach (var k in keys) InputSender.KeyUp(k);
            foreach (var b in buttons) InputSender.MouseButton(b, true);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        ReleaseAll();
    }
}
