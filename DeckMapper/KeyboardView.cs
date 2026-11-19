using System.Drawing.Drawing2D;

namespace DeckMapper;

/// <summary>Draws the keyboard and lets you click a key (or the dial). Shows each key's action on the current layer.</summary>
internal sealed class KeyboardView : Control
{
    public const byte DialId = 0; // selection id for the dial (no key has id 0)

    private static readonly Color Board = Color.FromArgb(24, 25, 28), Cap = Color.FromArgb(52, 54, 60), CapHover = Color.FromArgb(68, 71, 79),
        Remapped = Color.FromArgb(31, 111, 178), RemappedHover = Color.FromArgb(44, 130, 200), DisabledCap = Color.FromArgb(36, 37, 41),
        Text1 = Color.FromArgb(236, 237, 240), Text2 = Color.FromArgb(150, 154, 163), SelectColor = Color.FromArgb(255, 204, 0), Warn = Color.FromArgb(255, 153, 0);

    private KeyboardState? _state;
    private bool _fnLayer;
    private byte? _selected, _hover;
    private readonly ToolTip _tip = new() { InitialDelay = 400 };

    public event Action<byte>? KeySelected;

    public KeyboardView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Board;
    }

    public KeyboardState? State { get => _state; set { _state = value; Invalidate(); } }
    public bool FnLayer { get => _fnLayer; set { _fnLayer = value; Invalidate(); } }
    public byte? Selected { get => _selected; set { _selected = value; Invalidate(); } }

    // ---------------- geometry ----------------

    private (float scale, float ox, float oy) Geometry()
    {
        const float pad = 0.4f;
        float w = (float)KeyLayout.Width + pad * 2, h = (float)KeyLayout.Height + pad * 2;
        float scale = Math.Min(Width / w, Height / h);
        float ox = (Width - (float)KeyLayout.Width * scale) / 2, oy = (Height - (float)KeyLayout.Height * scale) / 2;
        return (scale, ox, oy);
    }

    private RectangleF Rect(PhysicalKey k)
    {
        var (s, ox, oy) = Geometry();
        float g = Math.Max(2f, s * 0.06f);
        return new RectangleF(ox + (float)k.X * s + g / 2, oy + (float)k.Y * s + g / 2, (float)k.W * s - g, (float)k.H * s - g);
    }

    private RectangleF DialRect()
    {
        var (s, ox, oy) = Geometry();
        var (x, y, d) = KeyLayout.Dial;
        return new RectangleF(ox + (float)x * s, oy + (float)y * s, (float)d * s, (float)d * s);
    }

    private byte? HitTest(Point p)
    {
        var dr = DialRect();
        using (var path = new GraphicsPath()) { path.AddEllipse(dr); if (path.IsVisible(p)) return DialId; }
        foreach (var k in KeyLayout.Keys)
            if (k.Id != KeyLayout.FnKey && Rect(k).Contains(p)) return k.Id;
        return null;
    }

    // ---------------- input ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitTest(e.Location);
        if (h != _hover)
        {
            _hover = h;
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            _tip.SetToolTip(this, h is { } id ? Tooltip(id) : "");
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = null; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (HitTest(e.Location) is { } id) { Selected = id; KeySelected?.Invoke(id); }
        base.OnMouseDown(e);
    }

    private string Tooltip(byte id)
    {
        if (id == DialId) return "Volume dial: click to edit its modes";
        var k = KeyLayout.Find(id);
        if (k == null || _state?.Keys.GetValueOrDefault(id) is not { } c) return "";
        string main = $"Main: {Describe(k, c.Main, false)}", fn = $"FN + key: {Describe(k, c.Fn, true)}";
        return c.ModifierGap ? $"{main}\n{fn}\n⚠ With Ctrl, Alt or Shift held this key does its default instead." : $"{main}\n{fn}";
    }

    public static string Describe(PhysicalKey k, KeyAction a, bool fn) => a.Kind == ActionKind.Default
        ? (fn ? k.FnText : k.MainText) + " (default)" : a.Describe();

    // ---------------- drawing ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Board);
        var (s, _, _) = Geometry();
        using var capFont = new Font("Segoe UI", Math.Max(6f, s * 0.17f), GraphicsUnit.Pixel);
        using var actFont = new Font("Segoe UI Semibold", Math.Max(6.5f, s * 0.2f), GraphicsUnit.Pixel);
        using var defFont = new Font("Segoe UI", Math.Max(6.5f, s * 0.19f), GraphicsUnit.Pixel);

        foreach (var k in KeyLayout.Keys)
        {
            var r = Rect(k);
            var c = _state?.Keys.GetValueOrDefault(k.Id);
            var act = c == null ? KeyAction.Default : _fnLayer ? c.Fn : c.Main;
            bool hover = _hover == k.Id, fnKey = k.Id == KeyLayout.FnKey;
            Color fill = fnKey ? DisabledCap
                : act.Kind == ActionKind.Disabled ? DisabledCap
                : act.Kind == ActionKind.Default ? (hover ? CapHover : Cap)
                : hover ? RemappedHover : Remapped;
            using var path = Round(r, Math.Max(3f, s * 0.12f));
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (_selected == k.Id) using (var p = new Pen(SelectColor, Math.Max(2f, s * 0.06f))) g.DrawPath(p, path);

            // Cap legend (top-left), action (centre).
            var inner = RectangleF.Inflate(r, -s * 0.08f, -s * 0.06f);
            Draw(g, k.Label, capFont, inner, act.Kind == ActionKind.Default ? Text2 : Color.FromArgb(190, 220, 245), StringAlignment.Near, StringAlignment.Near);
            string text = fnKey ? "" : act.Kind switch
            {
                ActionKind.Default => _fnLayer ? (k.FnDefault != null && k.FnDefault != k.Label ? k.FnDefault : "") : (k.MainDefault != null && k.Id is >= 58 and <= 69 ? k.MainDefault : ""),
                ActionKind.Disabled => "off",
                _ => act.Describe(),
            };
            if (text.Length > 0)
            {
                var tr = new RectangleF(inner.X, inner.Y + capFont.Height * 0.8f, inner.Width, inner.Height - capFont.Height * 0.8f);
                Draw(g, text, act.Kind == ActionKind.Default ? defFont : actFont, tr,
                    act.Kind == ActionKind.Default || act.Kind == ActionKind.Disabled ? Text2 : Text1, StringAlignment.Center, StringAlignment.Center);
            }
            if (!_fnLayer && c is { ModifierGap: true })
            {
                float t = s * 0.26f;
                using var warn = new SolidBrush(Warn);
                g.FillPolygon(warn, [new PointF(r.Right - t, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Top + t)]);
            }
        }

        // Dial.
        var dr = DialRect();
        bool dialCustom = _state?.Dial.Count > 0;
        using (var b = new LinearGradientBrush(dr, Color.FromArgb(90, 93, 102), Color.FromArgb(40, 42, 47), 60f)) g.FillEllipse(b, dr);
        var ringColor = _state?.Dial.FirstOrDefault() is { } m0 ? Color.FromArgb(m0.Color[0], m0.Color[1], m0.Color[2]) : Text2;
        using (var p = new Pen(_selected == DialId ? SelectColor : _hover == DialId ? Text1 : ringColor, Math.Max(2f, s * 0.07f))) g.DrawEllipse(p, dr);
        Draw(g, dialCustom ? $"Dial\n{_state!.Dial.Count} mode{(_state.Dial.Count == 1 ? "" : "s")}" : "Dial", capFont, dr, Text1,
            StringAlignment.Center, StringAlignment.Center);
    }

    private static void Draw(Graphics g, string text, Font f, RectangleF r, Color c, StringAlignment h, StringAlignment v)
    {
        using var b = new SolidBrush(c);
        using var sf = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, f, b, r, sf);
    }

    private static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
