namespace DeckMapper;

/// <summary>Picks a <see cref="KeyAction"/>: kind (default, key, media, mouse, disabled) plus its target.</summary>
internal sealed class ActionEditor : UserControl
{
    private sealed record Kind(ActionKind Value, string Text) { public override string ToString() => Text; }
    private sealed record Item(ushort[] Codes, string Text) { public override string ToString() => Text; }

    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Margin = new Padding(0, 0, 6, 0) };
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, MaxDropDownItems = 20, Margin = new Padding(0, 0, 6, 0) };
    private readonly Button _record = new() { Text = "Record…", AutoSize = true, Margin = new Padding(0) };
    private readonly Button _macro = new() { Text = "Edit macro…", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
    private readonly Label _macroInfo = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0), ForeColor = SystemColors.GrayText };
    private readonly bool _forDial;
    private KeyAction _value = KeyAction.Default;
    private bool _loading;

    public event EventHandler? Changed;

    /// <param name="forDial">Dial actions: "Nothing" instead of Default/Disabled.</param>
    public ActionEditor(bool forDial = false)
    {
        _forDial = forDial;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0), MaximumSize = new Size(330, 0) };
        flow.Controls.AddRange([_kind, _target, _record, _macro, _macroInfo]);
        _macro.Click += (_, _) => EditMacro();
        Controls.Add(flow);
        new ToolTip().SetToolTip(_record, "Press a key or a combination on the keyboard (e.g. Ctrl + Shift + Esc)");

        _kind.SelectedIndexChanged += (_, _) => { if (!_loading) OnKindPicked(); };
        _target.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _target.SelectedItem is not Item it) return;
            var k = ((Kind)_kind.SelectedItem!).Value;
            Set(k switch { ActionKind.Media => KeyAction.Media(it.Codes[0]), ActionKind.Mouse => KeyAction.Mouse(it.Codes[0]), _ => KeyAction.Key(it.Codes) });
        };
        _record.Click += (_, _) =>
        {
            using var d = new RecordDialog();
            if (d.ShowDialog(FindForm()) == DialogResult.OK && d.Combo.Length > 0) { Value = KeyAction.Key(d.Combo); Changed?.Invoke(this, EventArgs.Empty); }
        };
        Value = KeyAction.Default;
    }

    /// <summary>Opens the macro editor. Returns false when cancelled.</summary>
    private bool EditMacro(bool open = false)
    {
        using var d = new MacroDialog(_value.Macro);
        if (d.ShowDialog(FindForm()) != DialogResult.OK) return false;
        _value = KeyAction.FromMacro(d.Result);
        Fill();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public KeyAction Value
    {
        get => _value;
        set { _value = value; Fill(); }
    }

    private void Set(KeyAction a)
    {
        _value = a;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnKindPicked()
    {
        var k = ((Kind)_kind.SelectedItem!).Value;
        _value = k switch
        {
            ActionKind.Default => KeyAction.Default,
            ActionKind.Disabled => KeyAction.Disabled,
            ActionKind.Key => _value.Kind == ActionKind.Key ? _value : KeyAction.Key(KeyCodes.Keys[0].Code),
            ActionKind.Media => KeyAction.Media(KeyCodes.Media[0].Code),
            ActionKind.Mouse => KeyAction.Mouse(KeyCodes.Mouse[0].Code),
            _ => _value,
        };
        if (k == ActionKind.Macro && _value.Kind != ActionKind.Macro)
        {
            var before = _value;
            if (!EditMacro(open: true)) { _value = before; Fill(); }
            return;
        }
        Fill();
        Changed?.Invoke(this, EventArgs.Empty);
        if (_target.Visible && k == ActionKind.Key) _target.Focus();
    }

    private void Fill()
    {
        _loading = true;
        try
        {
            _kind.Items.Clear();
            if (_forDial) _kind.Items.Add(new Kind(ActionKind.Default, "Nothing"));
            else _kind.Items.Add(new Kind(ActionKind.Default, "Default"));
            _kind.Items.Add(new Kind(ActionKind.Key, "Key / shortcut"));
            _kind.Items.Add(new Kind(ActionKind.Media, "Media / app"));
            _kind.Items.Add(new Kind(ActionKind.Mouse, "Mouse button"));
            if (!_forDial) _kind.Items.Add(new Kind(ActionKind.Macro, "Macro"));
            if (!_forDial) _kind.Items.Add(new Kind(ActionKind.Disabled, "Disabled"));
            if (_value.Kind == ActionKind.Kept) _kind.Items.Add(new Kind(ActionKind.Kept, _value.Describe()));
            _kind.SelectedItem = _kind.Items.Cast<Kind>().First(x => x.Value == _value.Kind);

            _target.Items.Clear();
            IEnumerable<KeyCodes.Named> list = _value.Kind switch
            {
                ActionKind.Key => KeyCodes.Keys,
                ActionKind.Media => KeyCodes.Media,
                ActionKind.Mouse => KeyCodes.Mouse,
                _ => [],
            };
            foreach (var n in list) _target.Items.Add(new Item([n.Code], n.Name));
            if (_value.Kind == ActionKind.Key && _value.Codes.Length > 1) _target.Items.Insert(0, new Item(_value.Codes, _value.Describe()));
            _target.SelectedItem = _target.Items.Cast<Item>().FirstOrDefault(i => i.Codes.SequenceEqual(_value.Codes));
            if (_target.SelectedItem == null && _value.Codes.Length == 1 && _value.Kind is ActionKind.Key or ActionKind.Media or ActionKind.Mouse)
            {
                var odd = new Item(_value.Codes, _value.Describe());
                _target.Items.Insert(0, odd);
                _target.SelectedItem = odd;
            }
            _target.Visible = _value.Kind is ActionKind.Key or ActionKind.Media or ActionKind.Mouse;
            _record.Visible = _value.Kind == ActionKind.Key;
            _macro.Visible = _macroInfo.Visible = _value.Kind == ActionKind.Macro;
            if (_value.Macro is { } m) _macroInfo.Text = $"{m.Steps.Length} step{(m.Steps.Length == 1 ? "" : "s")}, {(m.StopOnRelease ? "stops when let go" : "plays once")}";
        }
        finally { _loading = false; }
    }
}
