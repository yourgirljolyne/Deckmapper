using System.Drawing.Drawing2D;

namespace DeckMapper;

/// <summary>
/// Owns the background key service (<see cref="KeyEngine"/>) and the window. No tray icon: with the service on,
/// closing the window leaves DeckMapper running invisibly; starting DeckMapper again opens the window.
/// With the service off, closing the window exits.
/// </summary>
internal sealed class BackgroundApp : ApplicationContext
{
    public const string ShowEventName = "DeckMapper.ShowWindow";

    private readonly KeyEngine _engine = new();
    private readonly SynchronizationContext _ui;
    private MainForm? _window;
    private bool _exiting;

    public static Icon AppIcon { get; } = MakeIcon();

    public BackgroundApp(bool showWindow)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        if (Settings.PlayKeysInSoftwareMode) _engine.Start();
        ListenForShowRequests();
        if (showWindow || !Settings.PlayKeysInSoftwareMode) ShowWindow();
    }

    public bool ServiceOn => Settings.PlayKeysInSoftwareMode;

    /// <summary>Turns the background key service on or off (and remembers it).</summary>
    public async Task SetServiceAsync(bool on)
    {
        Settings.PlayKeysInSoftwareMode = on;
        if (on) _engine.Start();
        else
        {
            await _engine.StopAsync();
            // Starting with Windows only makes sense for the service.
            if (Settings.StartWithWindows) Settings.StartWithWindows = false;
        }
    }

    public void ShowWindow()
    {
        if (_exiting) return;
        if (_window == null || _window.IsDisposed)
        {
            _window = new MainForm(_engine, this) { Icon = AppIcon };
            _window.FormClosed += (_, _) =>
            {
                _window = null;
                // Nothing to do in the background without the service, or while Admiral plays the keys.
                if (!Settings.PlayKeysInSoftwareMode || KeyEngine.AdmiralRunning) Exit();
            };
            _window.Show();
        }
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private async void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        await _engine.StopAsync();
        _engine.Dispose();
        ExitThread();
    }

    /// <summary>A second DeckMapper start signals this event instead of opening another copy.</summary>
    private void ListenForShowRequests()
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => _ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>A small keycap: drawn here so the app needs no icon file.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            var r = new RectangleF(2, 5, 28, 22);
            const float rad = 6;
            path.AddArc(r.X, r.Y, rad, rad, 180, 90);
            path.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
            path.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
            path.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
            path.CloseFigure();
            using (var b = new SolidBrush(Color.FromArgb(31, 111, 178))) g.FillPath(b, path);
            using (var b = new SolidBrush(Color.White))
            {
                g.FillRectangle(b, 7, 10, 4, 4); g.FillRectangle(b, 14, 10, 4, 4); g.FillRectangle(b, 21, 10, 4, 4);
                g.FillRectangle(b, 7, 18, 18, 4);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
