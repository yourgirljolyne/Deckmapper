using System.Diagnostics;

namespace DeckMapper;

/// <summary>Keyboard grid on the left, editor for the selected key (or the dial) on the right, key tester at the bottom.</summary>
internal sealed class MainForm : Form
{
    private readonly KeyStore _store = new();
    private KeyboardState? _loaded, _edited;
    private bool _busy, _dirty, _closing;

    private readonly KeyboardView _view = new() { Dock = DockStyle.Fill, MinimumSize = new Size(600, 200) };
    private readonly RadioButton _mainLayer = Tab("Main layer"), _fnLayer = Tab("FN layer");
    private readonly Button _apply = Btn("Apply to keyboard"), _reload = Btn("Reload"), _undo = Btn("Undo changes"), _backups = Btn("Backups…"),
        _fixAll = Btn("Fix them all"), _fPreset = Btn("F-keys first preset…");
    private readonly Panel _banner = new() { Dock = DockStyle.Top, Height = 40, Visible = false, BackColor = Color.FromArgb(255, 244, 214), Padding = new Padding(10, 6, 10, 6) };
    private readonly Label _bannerText = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(90, 60, 0) };
    private readonly Panel _side = new() { Dock = DockStyle.Right, Width = 360, Padding = new Padding(14, 10, 10, 10), AutoScroll = true };
    private readonly KeyCatcher _tester = new() { Dock = DockStyle.Fill, Hint = "Key tester: click here and press keys to see what Windows receives (Alt + F4 won't close anything here)" };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolTip _tips = new();

    private byte? _selected;

    public MainForm()
    {
        Text = "DeckMapper";
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(1380, 680);
        MinimumSize = new Size(1100, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 2), WrapContents = false };
        top.Controls.AddRange([_mainLayer, _fnLayer, Spacer(24), _apply, _undo, _reload, Spacer(24), _fPreset, _backups]);
        _mainLayer.Checked = true;
        _apply.Font = new Font(Font, FontStyle.Bold);

        _banner.Controls.Add(_bannerText);
        _banner.Controls.Add(_fixAll);
        _fixAll.Dock = DockStyle.Right;
        _fixAll.Margin = new Padding(0);

        var testerHost = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 6, 10, 6) };
        testerHost.Controls.Add(_tester);
        var status = new StatusStrip();
        status.Items.Add(_status);

        Controls.Add(_view);
        Controls.Add(_side);
        Controls.Add(_banner);
        Controls.Add(top);
        Controls.Add(testerHost);
        Controls.Add(status);

        _tips.SetToolTip(_mainLayer, "What each key does when pressed on its own");
        _tips.SetToolTip(_fnLayer, "What each key does while FN is held");
        _tips.SetToolTip(_apply, "Write the changes to the keyboard (a backup of the current setup is saved first)");
        _tips.SetToolTip(_undo, "Forget the changes you haven't applied");
        _tips.SetToolTip(_reload, "Read the keys from the keyboard again");
        _tips.SetToolTip(_backups, "Open or restore the backups DeckMapper saved before each change");
        _tips.SetToolTip(_fPreset, "F1-F12 send F1-F12 (with Ctrl/Alt/Shift too); FN + F7-F12 control media");

        _mainLayer.CheckedChanged += (_, _) => { _view.FnLayer = _fnLayer.Checked; ShowEditor(); };
        _view.KeySelected += id => { _selected = id; ShowEditor(); };
        _apply.Click += async (_, _) => await ApplyAsync();
        _reload.Click += async (_, _) => { if (ConfirmDiscard()) await LoadAsync(); };
        _undo.Click += (_, _) => { if (_loaded != null) { SetState(_loaded.Clone(), dirty: false); SetStatus("Changes undone."); } };
        _fixAll.Click += (_, _) => FixAll();
        _fPreset.Click += (_, _) => FKeysFirst();
        _backups.Click += async (_, _) => await BackupsAsync();
        _tester.KeyPressed += (code, held, name) =>
        {
            _tester.Shown = "Windows received:  " + (code == 0 ? name : held.Count > 1 ? KeyCodes.ComboName(held) : KeyCodes.KeyName(code));
            _tester.Invalidate();
        };

        Shown += async (_, _) => await LoadAsync();
        FormClosing += OnClosing;
        UpdateUi();
        ShowEditor();
    }

    private static RadioButton Tab(string text) => new()
    {
        Text = text, Appearance = Appearance.Button, AutoSize = true, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(0, 0, 4, 0),
        TextAlign = ContentAlignment.MiddleCenter,
    };
    private static Button Btn(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(0, 0, 6, 0) };
    private static Control Spacer(int w) => new Panel { Width = w, Height = 1 };

    // ---------------- keyboard I/O ----------------

    private async Task RunAsync(string what, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateUi();
        SetStatus(what + "…");
        Cursor = Cursors.WaitCursor;
        try { await action(); }
        catch (Exception e) when (e is BragiException or IOException or InvalidOperationException or InvalidDataException)
        {
            SetStatus(e.Message);
            if (what.StartsWith("Writing")) MessageBox.Show(this, e.Message, "DeckMapper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = Cursors.Default;
            _busy = false;
            UpdateUi();
        }
    }

    private Task LoadAsync() => RunAsync("Reading the keys from the keyboard", async () =>
    {
        _loaded = await _store.LoadAsync();
        SetState(_loaded.Clone(), dirty: false);
        int remapped = _loaded.Keys.Values.Count(c => !c.Foreign && (c.Main.Kind != ActionKind.Default || c.Fn.Kind != ActionKind.Default));
        SetStatus($"Profile \"{_loaded.ProfileName}\": {remapped} key{(remapped == 1 ? "" : "s")} changed from default, {_loaded.Dial.Count} dial mode{(_loaded.Dial.Count == 1 ? "" : "s")}.");
    });

    private Task ApplyAsync() => RunAsync("Writing to the keyboard", async () =>
    {
        if (_loaded == null || _edited == null) return;
        var r = await _store.SaveAsync(_loaded, _edited, new Progress<string>(SetStatus));
        _loaded = await _store.LoadAsync();
        SetState(_loaded.Clone(), dirty: false);
        SetStatus(r.Writes.Count == 0 && r.Deleted.Count == 0 ? "Nothing to change."
            : $"Done: wrote {r.Writes.Count} file{(r.Writes.Count == 1 ? "" : "s")}. Try it in the key tester below.");
    });

    private async Task BackupsAsync()
    {
        Directory.CreateDirectory(KeyStore.BackupRoot);
        using var d = new FolderBrowserDialog
        {
            Description = "Pick a backup to put back on the keyboard (Cancel to just look)",
            InitialDirectory = KeyStore.BackupRoot, UseDescriptionForTitle = true, ShowNewFolderButton = false,
        };
        if (d.ShowDialog(this) != DialogResult.OK)
        {
            if (MessageBox.Show(this, "Open the backups folder?", "DeckMapper", MessageBoxButtons.YesNo) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", KeyStore.BackupRoot) { UseShellExecute = true });
            return;
        }
        if (!File.Exists(Path.Combine(d.SelectedPath, "backup.json")))
        {
            MessageBox.Show(this, "That folder isn't a DeckMapper backup (no backup.json).", "DeckMapper");
            return;
        }
        if (MessageBox.Show(this, $"Put the keys, FN layer and dial from\n{Path.GetFileName(d.SelectedPath)}\nback on the keyboard?\n\nWhat's on it now is backed up first.",
            "Restore backup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunAsync("Writing the backup to the keyboard", async () =>
        {
            int n = await _store.RestoreAsync(d.SelectedPath, new Progress<string>(SetStatus));
            _loaded = await _store.LoadAsync();
            SetState(_loaded.Clone(), dirty: false);
            SetStatus($"Backup restored ({n} file{(n == 1 ? "" : "s")} written).");
        });
    }

    // ---------------- editing ----------------

    private void SetState(KeyboardState s, bool dirty)
    {
        _edited = s;
        _dirty = dirty;
        foreach (var (key, c) in s.Keys) c.IgnoreGap = Settings.IsIgnored(key);
        _view.State = s;
        ShowEditor();
        UpdateUi();
    }

    private void Changed()
    {
        _dirty = true;
        _view.Invalidate();
        UpdateUi();
    }

    private void FixAll()
    {
        if (_edited == null) return;
        foreach (var c in _edited.Keys.Values.Where(c => c.ModifierGap)) c.WithModifiers = true;
        Changed();
        ShowEditor();
        SetStatus("Ctrl, Alt and Shift now work with every remapped key. Press Apply to keyboard.");
    }

    private void FKeysFirst()
    {
        if (_edited == null) return;
        if (MessageBox.Show(this,
            "F1-F12 will send F1-F12, also with Ctrl, Alt or Shift held (so Alt + F4 works with one hand).\n\n" +
            "FN + F7 … F12 become Previous, Play/Pause, Next, Mute, Volume down, Volume up. FN + F1 … F6 stay as they are.\n\n" +
            "Nothing is written until you press Apply to keyboard.", "F-keys first", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        ushort[] media = [182, 205, 181, 226, 234, 233];
        for (byte i = 0; i < 12; i++)
        {
            if (!_edited.Keys.TryGetValue((byte)(58 + i), out var c)) continue;
            c.Main = KeyAction.Key((ushort)(58 + i));
            c.WithModifiers = true;
            if (i >= 6) c.Fn = KeyAction.Media(media[i - 6]);
        }
        Changed();
        ShowEditor();
        SetStatus("F-keys first: set up. Press Apply to keyboard to write it.");
    }

    // ---------------- side panel ----------------

    private void ShowEditor()
    {
        _side.SuspendLayout();
        foreach (Control c in _side.Controls) c.Dispose();
        _side.Controls.Clear();
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top };
        _side.Controls.Add(flow);

        if (_edited == null || _selected == null)
        {
            flow.Controls.Add(Para(_edited == null ? "Reading the keyboard…" :
                "Click a key to change what it does.\n\nBlue keys are changed from the default. An orange corner means Ctrl, Alt or Shift + that key still does the key's default (that's why Alt + F4 needed FN). You can turn that warning off per key.\n\nChanges are only written when you press Apply to keyboard.", 320));
        }
        else if (_selected == KeyboardView.DialId) DialEditor(flow);
        else KeyEditor(flow, _selected.Value);
        _side.ResumeLayout();
    }

    private static Label Title(string t) => new() { Text = t, AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0, 0, 0, 6) };
    private static Label Para(string t, int w = 320) => new() { Text = t, AutoSize = true, MaximumSize = new Size(w, 0), Margin = new Padding(0, 4, 0, 8), ForeColor = SystemColors.GrayText };
    private static Label Head(string t) => new() { Text = t, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 10, 0, 4) };

    private void KeyEditor(FlowLayoutPanel flow, byte id)
    {
        var k = KeyLayout.Find(id)!;
        var c = _edited!.Keys[id];
        bool fn = _fnLayer.Checked;
        flow.Controls.Add(Title(fn ? $"FN + {k.Label}" : k.Label == k.MainText ? k.Label : $"{k.Label}  ·  {k.MainText}"));
        flow.Controls.Add(Para(fn
            ? $"While FN is held. Default: {k.FnText}."
            : $"Pressed on its own. Default: {k.MainText}."));

        flow.Controls.Add(Head("Does"));
        var ed = new ActionEditor { Value = fn ? c.Fn : c.Main };
        flow.Controls.Add(ed);

        var mods = new CheckBox
        {
            Text = "Also when Ctrl, Alt or Shift is held", AutoSize = true, Checked = c.WithModifiers, Margin = new Padding(0, 10, 0, 0),
            Visible = !fn && c.Main.Kind != ActionKind.Default,
        };
        var modsNote = Para("Without it, Ctrl/Alt/Shift + this key do the built-in function instead (how Web Hub remaps work).");
        modsNote.Visible = mods.Visible;
        flow.Controls.Add(mods);
        flow.Controls.Add(modsNote);

        var ignore = new CheckBox
        {
            Text = "Don't warn about Ctrl / Alt / Shift for this key", AutoSize = true, Checked = c.IgnoreGap, Margin = new Padding(0, 8, 0, 0),
        };
        var ignoreNote = Para("No orange corner, and Fix them all skips this key. Saved on this PC.");
        flow.Controls.Add(ignore);
        flow.Controls.Add(ignoreNote);
        void RefreshIgnore() => ignore.Visible = ignoreNote.Visible = !fn && (c.HasModifierGap || c.IgnoreGap);
        RefreshIgnore();

        ed.Changed += (_, _) =>
        {
            if (fn) c.Fn = ed.Value; else c.Main = ed.Value;
            mods.Visible = modsNote.Visible = !fn && c.Main.Kind != ActionKind.Default;
            Changed();
            RefreshIgnore();
        };
        mods.CheckedChanged += (_, _) => { c.WithModifiers = mods.Checked; Changed(); RefreshIgnore(); };
        ignore.CheckedChanged += (_, _) =>
        {
            c.IgnoreGap = ignore.Checked;
            Settings.SetIgnored(id, ignore.Checked);
            _view.Invalidate();
            UpdateUi();
        };

        flow.Controls.Add(Head("Other layer"));
        var otherLink = new LinkLabel
        {
            Text = (fn ? "Alone: " : "FN + key: ") + KeyboardView.Describe(k, fn ? c.Main : c.Fn, !fn), AutoSize = true, MaximumSize = new Size(320, 0),
        };
        otherLink.LinkClicked += (_, _) => { (fn ? _mainLayer : _fnLayer).Checked = true; };
        flow.Controls.Add(otherLink);

        if (c.Others.Count > 0)
            flow.Controls.Add(Para($"This key also has {c.Others.Count} other assignment{(c.Others.Count == 1 ? "" : "s")} (FN2 or a custom Ctrl/Alt/Shift layer from Web Hub). DeckMapper keeps {(c.Others.Count == 1 ? "it" : "them")} as is."));

        var reset = Btn("Reset this key");
        reset.Margin = new Padding(0, 16, 0, 0);
        reset.Click += (_, _) =>
        {
            c.Main = KeyAction.Default; c.Fn = KeyAction.Default; c.WithModifiers = true;
            Changed(); ShowEditor();
        };
        flow.Controls.Add(reset);
        _tips.SetToolTip(reset, "Both layers back to the keyboard's own function");
    }

    private int _dialIndex;

    private void DialEditor(FlowLayoutPanel flow)
    {
        var modes = _edited!.Dial;
        flow.Controls.Add(Title("Volume dial"));
        flow.Controls.Add(Para("The dial switches between these modes on the keyboard; the ring shows the mode's colour. Custom actions let a turn or a press send any key, media key or mouse button."));
        if (_edited.DialFile == 0) { flow.Controls.Add(Para("This profile has no dial file, so the dial can't be changed here.")); return; }

        var list = new ListBox { Width = 320, Height = 120, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 24 };
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            var m = modes[e.Index];
            using (var b = new SolidBrush(Color.FromArgb(m.Color[0], m.Color[1], m.Color[2]))) e.Graphics.FillEllipse(b, e.Bounds.X + 6, e.Bounds.Y + 5, 14, 14);
            e.Graphics.DrawEllipse(SystemPens.ControlDark, e.Bounds.X + 6, e.Bounds.Y + 5, 14, 14);
            TextRenderer.DrawText(e.Graphics, $"{e.Index + 1}.  {DialNames.Name(m.Type)}", e.Font, new Point(e.Bounds.X + 28, e.Bounds.Y + 3), e.ForeColor);
        };
        foreach (var m in modes) list.Items.Add(m);
        flow.Controls.Add(list);

        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        Button add = Btn("Add"), remove = Btn("Remove"), up = Btn("▲"), down = Btn("▼");
        row.Controls.AddRange([add, remove, up, down]);
        flow.Controls.Add(row);

        var detail = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        flow.Controls.Add(detail);

        void Refresh(int sel)
        {
            list.Items.Clear();
            foreach (var m in modes) list.Items.Add(m);
            _dialIndex = Math.Clamp(sel, -1, modes.Count - 1);
            list.SelectedIndex = _dialIndex;
            remove.Enabled = modes.Count > 1 && _dialIndex >= 0;
            up.Enabled = _dialIndex > 0;
            down.Enabled = _dialIndex >= 0 && _dialIndex < modes.Count - 1;
            add.Enabled = modes.Count < 6;
            ShowMode();
        }

        void ShowMode()
        {
            foreach (Control c in detail.Controls) c.Dispose();
            detail.Controls.Clear();
            if (_dialIndex < 0 || _dialIndex >= modes.Count) return;
            var m = modes[_dialIndex];
            detail.Controls.Add(Head($"Mode {_dialIndex + 1}"));
            var type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            foreach (var (t, n) in DialNames.Modes) type.Items.Add(new DialChoice(t, n));
            if (!DialNames.Modes.Any(x => x.Type == m.Type)) type.Items.Add(new DialChoice(m.Type, DialNames.Name(m.Type)));
            type.SelectedItem = type.Items.Cast<DialChoice>().First(x => x.Type == m.Type);
            var color = Btn("Ring colour…");
            var trow = new FlowLayoutPanel { AutoSize = true };
            trow.Controls.AddRange([type, color]);
            detail.Controls.Add(trow);
            type.SelectedIndexChanged += (_, _) =>
            {
                m.Type = ((DialChoice)type.SelectedItem!).Type;
                Changed(); list.Invalidate(); ShowMode();
            };
            color.Click += (_, _) =>
            {
                using var cd = new ColorDialog { Color = Color.FromArgb(m.Color[0], m.Color[1], m.Color[2]), FullOpen = true };
                if (cd.ShowDialog(this) != DialogResult.OK) return;
                m.Color = [cd.Color.R, cd.Color.G, cd.Color.B, 255];
                Changed(); list.Invalidate();
            };

            if (m.Type != DialNames.Custom) return;
            foreach (var trig in new[] { DialNames.TurnRight, DialNames.TurnLeft, DialNames.Press })
            {
                detail.Controls.Add(Head(trig switch { DialNames.TurnRight => "Turn right", DialNames.TurnLeft => "Turn left", _ => "Press" }));
                var existing = m.Actions.FirstOrDefault(a => a.Trigger == trig);
                var ed = new ActionEditor(forDial: true) { Value = existing?.Action ?? KeyAction.Default };
                ed.Changed += (_, _) =>
                {
                    var old = m.Actions.FirstOrDefault(a => a.Trigger == trig);
                    m.Actions.RemoveAll(a => a.Trigger == trig);
                    if (ed.Value.Kind != ActionKind.Default) m.Actions.Add(new(trig, 1, ed.Value, old?.FileId ?? 0));
                    m.Actions.Sort((a, b) => a.Trigger.CompareTo(b.Trigger));
                    Changed();
                };
                detail.Controls.Add(ed);
            }
        }

        list.SelectedIndexChanged += (_, _) => { if (list.SelectedIndex != _dialIndex && list.SelectedIndex >= 0) { _dialIndex = list.SelectedIndex; Refresh(_dialIndex); } };
        add.Click += (_, _) => { modes.Add(new DialMode { Type = 4, Color = [0, 170, 255, 255] }); Changed(); Refresh(modes.Count - 1); };
        remove.Click += (_, _) => { modes.RemoveAt(_dialIndex); Changed(); Refresh(_dialIndex - 1 < 0 ? 0 : _dialIndex - 1); };
        up.Click += (_, _) => { (modes[_dialIndex - 1], modes[_dialIndex]) = (modes[_dialIndex], modes[_dialIndex - 1]); Changed(); Refresh(_dialIndex - 1); };
        down.Click += (_, _) => { (modes[_dialIndex + 1], modes[_dialIndex]) = (modes[_dialIndex], modes[_dialIndex + 1]); Changed(); Refresh(_dialIndex + 1); };
        Refresh(Math.Min(_dialIndex, modes.Count - 1) is var i && i < 0 ? 0 : i);
    }

    private sealed record DialChoice(byte Type, string Name) { public override string ToString() => Name; }

    // ---------------- misc ----------------

    private void UpdateUi()
    {
        bool ready = !_busy && _edited != null;
        _apply.Enabled = ready && _dirty;
        _undo.Enabled = ready && _dirty;
        _reload.Enabled = !_busy;
        _backups.Enabled = !_busy;
        _fPreset.Enabled = ready;
        _view.Enabled = ready;
        _side.Enabled = ready;
        Text = _dirty ? "DeckMapper • not applied yet" : "DeckMapper";

        int gaps = _edited?.Keys.Values.Count(c => c.ModifierGap && c.Main.Kind != ActionKind.Disabled) ?? 0;
        _banner.Visible = gaps > 0;
        _bannerText.Text = $"⚠  {gaps} remapped key{(gaps == 1 ? "" : "s")} still do{(gaps == 1 ? "es" : "")} the old default when Ctrl, Alt or Shift is held (like Alt + F4 needing FN). Orange corners show which.";
        _fixAll.Enabled = ready;
    }

    private void SetStatus(string s) => _status.Text = s;

    private bool ConfirmDiscard() => !_dirty || MessageBox.Show(this, "Forget the changes you haven't applied?", "DeckMapper",
        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        e.Cancel = true;
        _closing = true;
        while (_busy) await Task.Delay(50);
        try { await _store.CloseAsync(); } catch { /* keyboard gone */ }
        _store.Dispose();
        Close();
    }
}
