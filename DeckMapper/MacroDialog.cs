using System.Diagnostics;

namespace DeckMapper;

/// <summary>Edits a macro: record keys, or add key presses, waits, clicks (optionally at a screen position), moves and scrolls by hand.</summary>
internal sealed class MacroDialog : Form
{
    private readonly List<MacroStep> _steps;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, GridLines = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly RadioButton _once = new() { Text = "Play the whole macro once per press", AutoSize = true, Checked = true };
    private readonly RadioButton _held = new() { Text = "Run while the key is held, stop when you let go", AutoSize = true };
    private readonly Label _total = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0) };
    private readonly Button _edit = Side("Edit…"), _delete = Side("Delete"), _up = Side("Move up"), _down = Side("Move down");

    public Macro Result => new([.. _steps], _held.Checked);

    public MacroDialog(Macro? start)
    {
        _steps = start?.Steps.ToList() ?? [];
        Text = "Macro";
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(700, 540);
        MinimumSize = new Size(620, 440);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        if (start?.StopOnRelease == true) _held.Checked = true;

        _list.Columns.Add("#", 44, HorizontalAlignment.Right);
        _list.Columns.Add("Step", 420);

        Button record = Side("● Record keys…"), key = Side("Key press…"), wait = Side("Wait…"), more = Side("More ▾"), clear = Side("Clear all");
        record.Font = new Font(Font, FontStyle.Bold);
        var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 170, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8, 0, 0, 0) };
        side.Controls.AddRange([record, Gap(), key, wait, more, Gap(), _edit, _delete, _up, _down, clear]);

        var moreMenu = new ContextMenuStrip();
        moreMenu.Items.Add("Key down only…", null, (_, _) => AddOne(StepKind.KeyDown));
        moreMenu.Items.Add("Key up only…", null, (_, _) => AddOne(StepKind.KeyUp));
        more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height));

        var help = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(0, 0, 0, 6), ForeColor = SystemColors.GrayText,
            Text = "The keyboard plays these steps in order. New steps go after the selected one. Double-click a step to change it. " +
                "This keyboard only plays keys and waits in macros (mouse clicks, scrolling and mouse moves don't work).",
        };
        var mode = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 8, 0, 0) };
        mode.Controls.AddRange([_once, _held, _total]);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 0) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);
        AcceptButton = ok;
        CancelButton = cancel;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        body.Controls.Add(_list);
        body.Controls.Add(side);
        body.Controls.Add(help);
        body.Controls.Add(mode);
        body.Controls.Add(buttons);
        Controls.Add(body);

        record.Click += (_, _) => Record();
        key.Click += (_, _) => AddKeyPress();
        wait.Click += (_, _) => AddOne(StepKind.Wait);
        _edit.Click += (_, _) => EditSelected();
        _list.DoubleClick += (_, _) => EditSelected();
        _delete.Click += (_, _) => DeleteSelected();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) DeleteSelected(); };
        _up.Click += (_, _) => MoveSelected(-1);
        _down.Click += (_, _) => MoveSelected(+1);
        clear.Click += (_, _) =>
        {
            if (_steps.Count > 0 && MessageBox.Show(this, "Remove every step?", "Macro", MessageBoxButtons.OKCancel) == DialogResult.OK) { _steps.Clear(); Fill(-1); }
        };
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        ok.Click += (_, e) =>
        {
            if (_steps.Count == 0) { MessageBox.Show(this, "Add at least one step.", "Macro"); DialogResult = DialogResult.None; }
            else if (Unbalanced() is { } warn && MessageBox.Show(this, warn + "\n\nKeep it like this?", "Macro", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                DialogResult = DialogResult.None;
        };
        Fill(-1);
    }

    private static Button Side(string t) => new() { Text = t, Width = 160, Height = 30, Margin = new Padding(0, 0, 0, 4), TextAlign = ContentAlignment.MiddleLeft };
    private static Control Gap() => new Panel { Height = 8, Width = 1, Margin = new Padding(0) };

    // ---------------- list ----------------

    private void Fill(int select)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        for (int i = 0; i < _steps.Count; i++) _list.Items.Add(new ListViewItem([(i + 1).ToString(), _steps[i].Describe()]));
        _list.EndUpdate();
        if (select >= 0 && select < _steps.Count)
        {
            _list.Items[select].Selected = true;
            _list.Items[select].Focused = true;
            _list.EnsureVisible(select);
        }
        int ms = _steps.Where(s => s.Kind == StepKind.Wait).Sum(s => s.A);
        _total.Text = $"{_steps.Count} step{(_steps.Count == 1 ? "" : "s")}, about {(ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.##} s")} of waiting" +
            (_steps.Count > Macro.MaxSteps ? $"  (too long: keep it under {Macro.MaxSteps} steps)" : "");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        int n = _list.SelectedIndices.Count;
        _edit.Enabled = n == 1;
        _delete.Enabled = n > 0;
        _up.Enabled = n > 0 && _list.SelectedIndices[0] > 0;
        _down.Enabled = n > 0 && _list.SelectedIndices[n - 1] < _steps.Count - 1;
    }

    private int InsertAt => _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[_list.SelectedIndices.Count - 1] + 1 : _steps.Count;

    private void Insert(IEnumerable<MacroStep> steps)
    {
        int at = InsertAt;
        var list = steps.ToList();
        _steps.InsertRange(at, list);
        Fill(at + list.Count - 1);
    }

    private void DeleteSelected()
    {
        var idx = _list.SelectedIndices.Cast<int>().OrderDescending().ToList();
        if (idx.Count == 0) return;
        foreach (var i in idx) _steps.RemoveAt(i);
        Fill(Math.Min(idx[^1], _steps.Count - 1));
    }

    private void MoveSelected(int dir)
    {
        var idx = _list.SelectedIndices.Cast<int>().ToList();
        if (idx.Count == 0) return;
        int first = idx[0], last = idx[^1];
        if (dir < 0 && first == 0 || dir > 0 && last == _steps.Count - 1) return;
        var block = _steps.GetRange(first, last - first + 1);
        _steps.RemoveRange(first, block.Count);
        _steps.InsertRange(first + dir, block);
        Fill(-1);
        for (int i = 0; i < block.Count; i++) _list.Items[first + dir + i].Selected = true;
    }

    private void EditSelected()
    {
        if (_list.SelectedIndices.Count != 1) return;
        int i = _list.SelectedIndices[0];
        if (StepDialog.Ask(this, _steps[i].Kind, _steps[i]) is { } s) { _steps[i] = s; Fill(i); }
    }

    /// <summary>A key pressed and released unevenly would leave it stuck down on the computer.</summary>
    private string? Unbalanced()
    {
        var down = new HashSet<(bool mouse, int code)>();
        foreach (var s in _steps)
        {
            if (s.Kind is StepKind.KeyDown or StepKind.MouseDown) down.Add((s.Kind == StepKind.MouseDown, s.A));
            if (s.Kind is StepKind.KeyUp or StepKind.MouseUp) down.Remove((s.Kind == StepKind.MouseUp, s.A));
        }
        if (down.Count == 0) return null;
        var names = down.Select(d => d.mouse ? KeyCodes.MouseName((ushort)d.code) : KeyCodes.KeyName((ushort)d.code));
        return $"These are pressed but never released, so they'd stay held down after the macro: {string.Join(", ", names)}.";
    }

    // ---------------- adding ----------------

    private void AddOne(StepKind kind)
    {
        if (StepDialog.Ask(this, kind, null) is { } s) Insert([s]);
    }

    private void AddKeyPress()
    {
        using var d = new RecordDialog();
        if (d.ShowDialog(this) != DialogResult.OK || d.Combo.Length == 0) return;
        var steps = d.Combo.Select(c => new MacroStep(StepKind.KeyDown, c)).ToList();
        steps.Add(new(StepKind.Wait, 30));
        steps.AddRange(d.Combo.Reverse().Select(c => new MacroStep(StepKind.KeyUp, c)));
        Insert(steps);
    }

    private void Record()
    {
        using var d = new MacroRecorder();
        if (d.ShowDialog(this) == DialogResult.OK && d.Steps.Count > 0) Insert(d.Steps);
    }
}

/// <summary>Asks for the details of one step.</summary>
internal static class StepDialog
{
    public static MacroStep? Ask(IWin32Window owner, StepKind kind, MacroStep? current)
    {
        using var f = new Form
        {
            Text = kind switch
            {
                StepKind.KeyDown => "Key down", StepKind.KeyUp => "Key up", StepKind.MouseDown => "Mouse button down",
                StepKind.MouseUp => "Mouse button up", StepKind.Wait => "Wait", StepKind.MoveTo => "Move mouse to",
                StepKind.MoveBy => "Move mouse by", StepKind.Scroll => "Scroll", _ => "Scroll sideways",
            },
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = new Font("Segoe UI", 9.5f), Padding = new Padding(12),
        };
        var flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        f.Controls.Add(flow);
        Func<MacroStep?> read;

        switch (kind)
        {
            case StepKind.KeyDown or StepKind.KeyUp:
            {
                var box = Combo(KeyCodes.Keys, current?.A ?? 4);
                var rec = new Button { Text = "Record…", AutoSize = true };
                rec.Click += (_, _) =>
                {
                    using var d = new RecordDialog();
                    if (d.ShowDialog(f) == DialogResult.OK && d.Combo.Length > 0) Select(box, d.Combo[^1]);
                };
                flow.Controls.Add(Row(new Label { Text = "Key", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, box, rec));
                read = () => box.SelectedItem is Item it ? new(kind, it.Code) : null;
                break;
            }
            case StepKind.MouseDown or StepKind.MouseUp:
            {
                var box = Combo(KeyCodes.Mouse, current?.A ?? 1);
                flow.Controls.Add(Row(new Label { Text = "Button", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, box));
                read = () => box.SelectedItem is Item it ? new(kind, it.Code) : null;
                break;
            }
            case StepKind.Wait:
            {
                var ms = Num(1, Macro.MaxWaitMs, current?.A ?? 100);
                flow.Controls.Add(Row(new Label { Text = "Wait", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, ms,
                    new Label { Text = "milliseconds (1000 = 1 second)", AutoSize = true, Margin = new Padding(6, 6, 0, 0) }));
                read = () => new(kind, (int)ms.Value);
                break;
            }
            default:
            {
                var n = Num(-127, 127, current?.A ?? (kind == StepKind.Scroll ? -3 : 3));
                flow.Controls.Add(Row(new Label { Text = "Clicks", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, n));
                flow.Controls.Add(Note(kind == StepKind.Scroll ? "Positive scrolls up, negative scrolls down." : "Positive scrolls right, negative scrolls left."));
                read = () => (int)n.Value == 0 ? null : new(kind, (int)n.Value);
                break;
            }
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
        flow.Controls.Add(Row(ok, cancel));
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? read() : null;
    }

    internal sealed record Item(ushort Code, string Name) { public override string ToString() => Name; }

    internal static ComboBox Combo(IEnumerable<KeyCodes.Named> list, int code)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, MaxDropDownItems = 20 };
        foreach (var n in list) box.Items.Add(new Item(n.Code, n.Name));
        Select(box, code);
        return box;
    }

    internal static void Select(ComboBox box, int code)
    {
        var it = box.Items.Cast<Item>().FirstOrDefault(i => i.Code == code);
        if (it == null) { it = new Item((ushort)code, KeyCodes.KeyName((ushort)code)); box.Items.Insert(0, it); }
        box.SelectedItem = it;
    }

    internal static NumericUpDown Num(int min, int max, int value) => new()
    {
        Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 90, ThousandsSeparator = false,
    };

    internal static FlowLayoutPanel Row(params Control[] c)
    {
        var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        r.Controls.AddRange(c);
        return r;
    }

    internal static Label Note(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 8) };
}

/// <summary>Records key presses and releases with the time between them.</summary>
internal sealed class MacroRecorder : Form
{
    private readonly KeyCatcher _catcher = new() { Dock = DockStyle.Fill, Hint = "Click here, then type. Use the mouse to stop.", Font = new Font("Segoe UI", 12f) };
    private readonly CheckBox _waits = new() { Text = "Record the time between keys", Checked = true, AutoSize = true };
    private readonly Stopwatch _clock = new();
    private readonly List<(long ms, MacroStep step)> _events = [];

    public List<MacroStep> Steps { get; } = [];

    public MacroRecorder()
    {
        Text = "Record keys";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 260);
        Font = new Font("Segoe UI", 9.5f);
        var note = new Label
        {
            Dock = DockStyle.Top, Height = 48, Padding = new Padding(10, 8, 10, 0),
            Text = "Every press and release is recorded, including Ctrl, Alt, Shift and Win. Click Done (with the mouse) when finished.",
        };
        var done = new Button { Text = "Done", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 0) };
        var restart = new Button { Text = "Start over", AutoSize = true };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        bottom.Controls.AddRange([cancel, done, restart, _waits]);
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4) };
        host.Controls.Add(_catcher);
        Controls.Add(host);
        Controls.Add(note);
        Controls.Add(bottom);

        _catcher.KeyPressed += (code, _, _) => { if (code != 0) Add(new(StepKind.KeyDown, code)); };
        _catcher.KeyReleased += code => Add(new(StepKind.KeyUp, code));
        restart.Click += (_, _) => { _events.Clear(); _clock.Reset(); ShowProgress(); _catcher.Focus(); };
        done.Click += (_, _) => Build();
        Shown += (_, _) => _catcher.Focus();
        ShowProgress();
    }

    private void Add(MacroStep s)
    {
        if (!_clock.IsRunning) _clock.Start();
        _events.Add((_clock.ElapsedMilliseconds, s));
        ShowProgress();
    }

    private void ShowProgress()
    {
        var last = _events.TakeLast(6).Select(e => e.step.Describe());
        _catcher.Shown = _events.Count == 0 ? "" : $"{_events.Count} event{(_events.Count == 1 ? "" : "s")}\n" + string.Join("  ·  ", last);
        _catcher.Invalidate();
    }

    private void Build()
    {
        long prev = -1;
        foreach (var (ms, step) in _events)
        {
            if (prev >= 0)
            {
                int gap = (int)(ms - prev);
                if (_waits.Checked) { gap = (int)Math.Round(gap / 10.0) * 10; if (gap > 0) Steps.Add(new(StepKind.Wait, gap)); }
                else Steps.Add(new(StepKind.Wait, 20));
            }
            Steps.Add(step);
            prev = ms;
        }
    }
}
