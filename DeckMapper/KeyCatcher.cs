namespace DeckMapper;

/// <summary>
/// A focusable box that swallows every key press (Tab, Enter, Esc, Alt, even Alt + F4) and reports them as HID usages,
/// so key combinations can be recorded and tested without Windows acting on them.
/// </summary>
internal sealed class KeyCatcher : Control
{
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private readonly List<ushort> _down = [];

    /// <summary>A key went down (code, or 0 for a key with no HID equivalent) with these keys held, in press order.</summary>
    public event Action<ushort, IReadOnlyList<ushort>, string>? KeyPressed;
    /// <summary>A key that was down went up.</summary>
    public event Action<ushort>? KeyReleased;
    /// <summary>Every key is up again.</summary>
    public event Action? AllReleased;

    public string Hint { get; set; } = "Click here, then press keys";

    public KeyCatcher()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public string Shown { get; set; } = "";

    protected override bool IsInputKey(Keys keyData) => true;
    public override bool PreProcessMessage(ref Message msg) => msg.Msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP ? false : base.PreProcessMessage(ref msg);

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { _down.Clear(); Invalidate(); base.OnLostFocus(e); }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN:
            {
                int vk = (int)m.WParam;
                long lp = (long)m.LParam;
                bool repeat = (lp & (1L << 30)) != 0;
                ushort code = KeyCodes.FromWindows(vk, lp);
                if (code != 0 && !_down.Contains(code)) _down.Add(code);
                if (!repeat || code == 0) KeyPressed?.Invoke(code, _down, code == 0 ? WindowsOnlyName(vk) : "");
                return; // never to DefWindowProc: no menu, no Alt+F4
            }
            case WM_KEYUP or WM_SYSKEYUP:
            {
                int vk = (int)m.WParam;
                ushort code = KeyCodes.FromWindows(vk, (long)m.LParam);
                if (vk == 0x2C && !_down.Contains(70)) { KeyPressed?.Invoke(70, [70], ""); KeyReleased?.Invoke(70); } // Print Screen only sends key-up
                if (_down.Remove(code)) KeyReleased?.Invoke(code);
                if (vk is 0x10 or 0x11 or 0x12) // generic Shift/Ctrl/Alt up: drop both sides
                    foreach (var c in _down.Where(c => vk == 0x10 ? c is 225 or 229 : vk == 0x11 ? c is 224 or 228 : c is 226 or 230).ToList())
                    {
                        _down.Remove(c);
                        KeyReleased?.Invoke(c);
                    }
                if (_down.Count == 0) AllReleased?.Invoke();
                return;
            }
        }
        base.WndProc(ref m);
    }

    private static string WindowsOnlyName(int vk) => vk switch
    {
        0xAD => "Mute", 0xAE => "Volume down", 0xAF => "Volume up", 0xB0 => "Next track", 0xB1 => "Previous track",
        0xB2 => "Stop", 0xB3 => "Play / Pause", 0xB6 => "Calculator / App 2", 0xB4 => "Mail", 0xAC => "Browser home",
        0xA6 => "Browser back", 0xA7 => "Browser forward", 0xA8 => "Browser refresh", 0xAA => "Search",
        _ => $"Windows key code 0x{vk:X2}",
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Focused ? SystemColors.Window : SystemColors.Control);
        var r = ClientRectangle;
        r.Width--; r.Height--;
        using (var pen = new Pen(Focused ? SystemColors.Highlight : SystemColors.ControlDark, Focused ? 2 : 1) { DashStyle = Focused ? System.Drawing.Drawing2D.DashStyle.Solid : System.Drawing.Drawing2D.DashStyle.Dash })
            g.DrawRectangle(pen, r);
        string text = Shown.Length > 0 ? Shown : Focused ? "Listening… press keys" : Hint;
        TextRenderer.DrawText(g, text, Font, ClientRectangle, Shown.Length > 0 ? ForeColor : SystemColors.GrayText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
}

/// <summary>Records one key or a key combination (e.g. Ctrl + Shift + Esc).</summary>
internal sealed class RecordDialog : Form
{
    private readonly KeyCatcher _catcher = new() { Dock = DockStyle.Fill, Hint = "Press the key or combination", Font = new Font("Segoe UI", 14f) };
    private ushort[] _combo = [];
    private readonly Button _ok = new() { Text = "Use this", DialogResult = DialogResult.OK, Enabled = false, AutoSize = true };

    public ushort[] Combo => _combo;

    public RecordDialog()
    {
        Text = "Record a key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 200);
        Font = new Font("Segoe UI", 9.5f);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var note = new Label
        {
            Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 0),
            Text = "Press it on the keyboard. Combinations work too: hold Ctrl, Shift, Alt or Win and press a key. Use the mouse to confirm.",
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        buttons.Controls.AddRange([cancel, _ok]);
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4) };
        host.Controls.Add(_catcher);
        Controls.Add(host);
        Controls.Add(note);
        Controls.Add(buttons);
        _catcher.KeyPressed += (code, held, _) =>
        {
            if (code == 0) return;
            // Modifiers first (in HID order), then the other keys in the order they were pressed.
            _combo = [.. held.Where(KeyCodes.IsModifier).Order(), .. held.Where(c => !KeyCodes.IsModifier(c))];
            if (_combo.Length > 6) _combo = _combo[..6];
            _catcher.Shown = KeyAction.Key(_combo).Describe();
            _catcher.Invalidate();
            _ok.Enabled = true;
        };
        Shown += (_, _) => _catcher.Focus();
    }
}
