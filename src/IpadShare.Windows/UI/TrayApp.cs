using System.Drawing.Drawing2D;
using System.Reflection;
using IpadShare.App.App;
using IpadShare.Core.Transport;

namespace IpadShare.App.UI;

/// <summary>
/// The app lives in the notification area: the icon shows the connection state at a glance,
/// the menu offers the few actions people need, and windows open on demand.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private static Icon? s_appIcon;
    public static Icon AppIcon => s_appIcon ??= LoadAppIcon();
    public static TrayApp? Current { get; private set; }

    private readonly AppController _app;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem, _switchItem, _pauseItem;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<string, Icon> _iconCache = new();
    private MainWindow? _main;
    private OnboardingWindow? _onboarding;
    private CalibrationWindow? _calibration;
    private DiagnosticsWindow? _diagnostics;
    private TransportState _lastState = TransportState.Stopped;
    private bool _quitting;

    public TrayApp(AppController app, bool background)
    {
        Current = this;
        _app = app;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _switchItem = new ToolStripMenuItem("Switch to iPad", null, (_, _) => { if (_app.Status.OnIpad) _app.SwitchToPc(); else _app.SwitchToIpad(); })
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+Space",
        };
        _pauseItem = new ToolStripMenuItem("Pause sharing", null, (_, _) => _app.UpdateSettings(s => s.SharingEnabled = !s.SharingEnabled));
        _menu = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false, Font = Theme.Body() };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _switchItem,
            _pauseItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open iPad Share", null, (_, _) => ShowMain()),
            new ToolStripMenuItem("Calibrate pointer…", null, (_, _) => ShowCalibration()),
            new ToolStripMenuItem("Diagnostics…", null, (_, _) => ShowDiagnostics()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, (_, _) => Quit()),
        });

        _icon = new NotifyIcon
        {
            Icon = StatusIcon(TransportState.Starting, false),
            Text = "iPad Share",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowMain(); };

        _app.StatusChanged += () => _ui.Post(_ => UpdateStatus(), null);
        _app.PushedWhileDisconnected += () => _ui.Post(_ => ShowDisconnectedHint(), null);

        _ = StartAsync(background);
    }

    private async Task StartAsync(bool background)
    {
        try
        {
            await _app.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show("iPad Share couldn't start: " + ex.Message, "iPad Share", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        if (!_app.Settings.FirstRunCompleted) ShowOnboarding(0);
        else if (!background) ShowMain();
        UpdateStatus();
    }

    // ------------------------------------------------------------------
    public void ShowMain()
    {
        if (_main == null || _main.IsDisposed) _main = new MainWindow(_app, this);
        _main.ReloadAll();
        Activate(_main);
    }

    /// <summary>Thread-safe: used when a second copy of the app is launched.</summary>
    public void ShowMainFromAnyThread() => _ui.Post(_ => ShowMain(), null);

    public void ShowOnboarding(int page)
    {
        _onboarding?.Close();
        _onboarding = new OnboardingWindow(_app, this, page);
        _onboarding.FormClosed += (_, _) => { _onboarding = null; _main?.ReloadAll(); };
        Activate(_onboarding);
    }

    public void ShowCalibration()
    {
        if (_calibration == null || _calibration.IsDisposed)
        {
            _calibration = new CalibrationWindow(_app);
            _calibration.FormClosed += (_, _) => { _calibration = null; _main?.ReloadAll(); };
        }
        Activate(_calibration);
    }

    public void ShowDiagnostics()
    {
        if (_diagnostics == null || _diagnostics.IsDisposed) _diagnostics = new DiagnosticsWindow(_app);
        Activate(_diagnostics);
    }

    private static void Activate(Form f)
    {
        if (!f.Visible) f.Show();
        if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
        f.Activate();
        f.BringToFront();
    }

    private void ShowDisconnectedHint()
    {
        _icon.ShowBalloonTip(6000, "Your iPad isn't connected",
            $"On the iPad, open Settings › Bluetooth and tap “{Environment.MachineName}”.", ToolTipIcon.Info);
    }

    private void UpdateStatus()
    {
        if (_quitting) return;
        var st = _app.Status;
        string peer = st.Peer ?? "iPad";
        string text = st.Transport switch
        {
            TransportState.Connected when st.OnIpad => $"Controlling {peer}",
            TransportState.Connected when !st.SharingEnabled => $"Connected to {peer} (paused)",
            TransportState.Connected => $"Connected to {peer}",
            TransportState.Advertising => "Waiting for your iPad",
            TransportState.Starting => "Starting…",
            _ => st.Detail ?? "Bluetooth not available",
        };
        _statusItem.Text = text;
        _switchItem.Text = st.OnIpad ? "Back to PC" : "Switch to iPad";
        _switchItem.Enabled = st.Transport == TransportState.Connected && st.SharingEnabled;
        _pauseItem.Checked = !st.SharingEnabled;
        _icon.Icon = StatusIcon(st.Transport, st.OnIpad);
        string tip = "iPad Share — " + text;
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        if (st.Transport == TransportState.Connected && _lastState != TransportState.Connected && _app.Settings.FirstRunCompleted)
            _icon.ShowBalloonTip(2500, "iPad connected", $"{peer} is ready. Move the pointer across the edge to use it.", ToolTipIcon.None);
        _lastState = st.Transport;

        _main?.RefreshStatus();
    }

    /// <summary>App icon with a small status badge.</summary>
    private Icon StatusIcon(TransportState state, bool onIpad)
    {
        string key = onIpad ? "ipad" : state switch
        {
            TransportState.Connected => "ok",
            TransportState.Advertising or TransportState.Starting => "wait",
            _ => "bad",
        };
        if (_iconCache.TryGetValue(key, out var cached)) return cached;

        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var src = new Icon(AppIcon, size, size);
            g.DrawIcon(src, new Rectangle(0, 0, size, size));
            Color? badge = key switch
            {
                "ok" => Theme.Good,
                "wait" => Color.FromArgb(160, 160, 160),
                "bad" => Theme.Bad,
                _ => null,
            };
            if (badge is Color c)
            {
                float d = size * 0.42f;
                var r = new RectangleF(size - d, size - d, d, d);
                using var ring = new SolidBrush(Color.White);
                g.FillEllipse(ring, RectangleF.Inflate(r, size * 0.06f, size * 0.06f));
                using var b = new SolidBrush(c);
                g.FillEllipse(b, r);
            }
        }
        var icon = Icon.FromHandle(bmp.GetHicon());
        _iconCache[key] = icon;
        return icon;
    }

    private static Icon LoadAppIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _icon.Visible = false;
        try { await _app.DisposeAsync(); } catch (Exception ex) { Log.Error("Shutdown", ex); }
        foreach (var f in Application.OpenForms.Cast<Form>().ToList()) f.Dispose();
        _icon.Dispose();
        Log.Info("Exit");
        Log.Flush();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
