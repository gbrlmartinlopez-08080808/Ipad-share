using IpadShare.App.App;
using IpadShare.Core.Handoff;
using IpadShare.Core.Input;
using IpadShare.Core.Settings;
using IpadShare.Core.Transport;

namespace IpadShare.App.UI;

/// <summary>Settings and status: one scrolling page of cards.</summary>
internal sealed class MainWindow : Form
{
    private const int W = 524;
    private readonly AppController _app;
    private readonly TrayApp _tray;

    // header
    private readonly StatusPill _pill = new();
    private readonly Label _headline = new();
    private readonly Label _subline = new();
    private readonly PillButton _switchButton = new() { Primary = true, Text = "Switch to iPad" };
    private readonly PillButton _pairButton = new() { Text = "Pairing help" };

    // arrangement
    private readonly ArrangementView _arrangement = new();
    private readonly DropDown _model = new();
    private readonly Segmented _orientation = new() { Items = new[] { "Landscape", "Portrait" }, Width = 200 };

    // trackpad
    private readonly Segmented _gestures = new() { Items = new[] { "Translated", "iPad-native (beta)" }, Width = 290 };
    private readonly Slider _pointer = new() { Minimum = 0.25, Maximum = 3, Detent = 1, Width = 200 };
    private readonly Slider _scroll = new() { Minimum = 0.25, Maximum = 3, Detent = 1, Width = 200 };
    private readonly ToggleSwitch _reverse = new(), _momentum = new(), _tap = new(), _pinch = new(), _multi = new();

    // keyboard
    private readonly Segmented _style = new() { Items = new[] { "Windows-style", "Mac-style" }, Width = 250 };
    private readonly ToggleSwitch _winSpotlight = new(), _iso = new();

    // switching
    private readonly Segmented _resistance = new() { Items = new[] { "Light", "Medium", "Firm" }, Width = 220 };
    private readonly ToggleSwitch _glow = new(), _hotkey = new(), _shake = new();
    private readonly Slider _mouse = new() { Minimum = 0.25, Maximum = 3, Detent = 1, Width = 200 };
    private readonly PillButton _calibrate = new() { Text = "Calibrate…" };
    private SettingRow? _calibrationRow;
    private readonly Label _calibrationInfo = new();

    // general
    private readonly ToggleSwitch _sharing = new(), _startup = new();
    private readonly DropDown _transport = new() { Items = new[] { "Automatic", "Built-in Bluetooth", "USB bridge" }, Width = 200 };

    private bool _loading;

    public MainWindow(AppController app, TrayApp tray)
    {
        _app = app;
        _tray = tray;
        Text = "iPad Share";
        Icon = TrayApp.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(W + 104, 760);          // cards (W + 36) + margins + vertical scrollbar
        MinimumSize = new Size(W + 120, 420);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body();

        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background, Padding = new Padding(20, 16, 0, 16) };
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Background, Location = new Point(20, 16), Margin = Padding.Empty,
        };
        scroller.Controls.Add(stack);
        Controls.Add(scroller);

        stack.Controls.Add(BuildHeader());
        stack.Controls.Add(BuildArrangement());
        stack.Controls.Add(BuildTrackpad());
        stack.Controls.Add(BuildKeyboard());
        stack.Controls.Add(BuildSwitching());
        stack.Controls.Add(BuildGeneral());
        stack.Controls.Add(new Label
        {
            Text = $"iPad Share {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · nothing is installed on your iPad",
            ForeColor = Theme.SubText, AutoSize = true, Font = Theme.Body(8.5f), Margin = new Padding(4, 2, 0, 8),
        });

        LoadSettings();
        WireEvents();
        RefreshStatus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyToWindow(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window keeps iPad Share running in the tray.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------
    private Control BuildHeader()
    {
        var card = new Card();
        var top = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Card, Margin = new Padding(0), WrapContents = false };
        top.Controls.Add(new PictureBox
        {
            Image = TrayApp.AppIcon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44),
            Margin = new Padding(0, 0, 12, 0), BackColor = Theme.Card,
        });
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Theme.Card, Margin = new Padding(0), WrapContents = false };
        _headline.AutoSize = true; _headline.Font = Theme.Title(16f); _headline.BackColor = Theme.Card; _headline.Margin = new Padding(0, 0, 0, 2);
        titles.Controls.Add(_headline);
        titles.Controls.Add(_pill);
        _pill.Margin = new Padding(0, 2, 0, 0);
        top.Controls.Add(titles);
        card.Add(top);

        _subline.AutoSize = true; _subline.MaximumSize = new Size(W, 0); _subline.ForeColor = Theme.SubText; _subline.BackColor = Theme.Card;
        card.Add(_subline).Margin = new Padding(0, 10, 0, 6);

        var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Card, Margin = new Padding(0, 4, 0, 0) };
        _switchButton.Margin = new Padding(0, 0, 8, 0);
        _pairButton.Margin = new Padding(0);
        buttons.Controls.Add(_switchButton);
        buttons.Controls.Add(_pairButton);
        card.Add(buttons);
        return card;
    }

    private Control BuildArrangement()
    {
        var card = new Card("Arrangement", "Drag the iPad to where it sits next to your laptop. Move the pointer across the blue edge to switch.");
        _arrangement.Width = W;
        card.Add(_arrangement);
        _model.Items = IpadModel.Presets.Select(p => p.Name).ToArray();
        _model.Width = 230;
        card.Add(new SettingRow("iPad model", null, _model, W));
        card.Add(new SettingRow("Orientation", null, _orientation, W));
        return card;
    }

    private Control BuildTrackpad()
    {
        var card = new Card("Trackpad");
        card.Add(new SettingRow("Gestures", "Translated always works. iPad-native passes your fingers straight to iPadOS.", _gestures, W));
        card.Add(new SettingRow("Pointer speed", null, _pointer, W));
        card.Add(new SettingRow("Scroll speed", null, _scroll, W));
        card.Add(new SettingRow("Scroll momentum", "Content keeps gliding after a flick, like on iPad.", _momentum, W));
        card.Add(new SettingRow("Reverse scrolling", "Use if the iPad scrolls the opposite way to your fingers.", _reverse, W));
        card.Add(new SettingRow("Tap to click", "Follows your Windows touchpad setting until changed here.", _tap, W));
        card.Add(new SettingRow("Pinch to zoom", null, _pinch, W));
        card.Add(new SettingRow("Three- and four-finger gestures",
            "Swipe up: Home · up and hold: App Switcher · sideways: switch apps · tap: Spotlight", _multi, W));
        return card;
    }

    private Control BuildKeyboard()
    {
        var card = new Card("Keyboard");
        card.Add(new SettingRow("Shortcut style", "Windows-style: Ctrl+C copies, Ctrl+← jumps words, Alt+Tab switches apps.", _style, W));
        card.Add(new SettingRow("Win key opens Spotlight", "Tap the Windows key to search the iPad.", _winSpotlight, W));
        card.Add(new SettingRow("Swap the ` and < keys", "Only if these two keys come out swapped on the iPad.", _iso, W));
        card.Add(new Label
        {
            Text = "Tip: match the iPad's layout to your laptop's — on the iPad open Settings › General › Keyboard › Hardware Keyboard.",
            ForeColor = Theme.SubText, AutoSize = true, MaximumSize = new Size(W, 0), BackColor = Theme.Card, Font = Theme.Body(9f),
            Margin = new Padding(0, 6, 0, 0),
        });
        return card;
    }

    private Control BuildSwitching()
    {
        var card = new Card("Switching");
        card.Add(new SettingRow("Crossing", "Light: just move past the edge. Firm: push a little harder first.", _resistance, W));
        card.Add(new SettingRow("Shake the pointer to switch", "Shake left-right quickly to jump to the other screen, from either side.", _shake, W));
        card.Add(new SettingRow("Edge glow", "Shows the push building up, like Universal Control.", _glow, W));
        card.Add(new SettingRow("Ctrl+Alt+Space switches devices", null, _hotkey, W));
        card.Add(new SettingRow("Mouse speed on iPad", "For a USB or Bluetooth mouse plugged into the laptop.", _mouse, W));
        _calibrationRow = new SettingRow("Pointer calibration", null, _calibrate, W);
        card.Add(_calibrationRow);
        _calibrationInfo.AutoSize = true; _calibrationInfo.ForeColor = Theme.SubText; _calibrationInfo.BackColor = Theme.Card;
        _calibrationInfo.MaximumSize = new Size(W, 0); _calibrationInfo.Font = Theme.Body(9f);
        card.Add(_calibrationInfo).Margin = new Padding(0, 0, 0, 4);
        return card;
    }

    private Control BuildGeneral()
    {
        var card = new Card("General");
        card.Add(new SettingRow("Share keyboard and trackpad", "Turn off to keep the pointer on Windows.", _sharing, W));
        card.Add(new SettingRow("Start with Windows", "Runs quietly in the tray so the iPad reconnects by itself.", _startup, W));
        card.Add(new SettingRow("Connection", "Automatic uses the USB bridge when it's plugged in.", _transport, W));
        var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Card, Margin = new Padding(0, 8, 0, 0) };
        var diag = new PillButton { Text = "Diagnostics…" };
        diag.Click += (_, _) => _tray.ShowDiagnostics();
        var tour = new PillButton { Text = "Setup guide…" };
        tour.Click += (_, _) => _tray.ShowOnboarding(0);
        diag.Margin = new Padding(0, 0, 8, 0);
        buttons.Controls.Add(diag);
        buttons.Controls.Add(tour);
        card.Add(buttons);
        return card;
    }

    // ------------------------------------------------------------------
    private void LoadSettings()
    {
        _loading = true;
        var s = _app.Settings;
        var a = s.Arrangement;
        _arrangement.SetState(SystemInfo.Monitors(), a.MonitorDevice, a.Side, a.Offset, IpadModel.ByName(a.IpadModel), a.Portrait);
        _model.SetSilently(Math.Max(0, IpadModel.Presets.ToList().FindIndex(p => p.Name == a.IpadModel)));
        _orientation.SetSilently(a.Portrait ? 1 : 0);

        _gestures.SetSilently(s.TrackpadMode == TrackpadMode.Native ? 1 : 0);
        _pointer.SetSilently(s.PointerSpeed);
        _scroll.SetSilently(s.ScrollSpeed);
        _momentum.SetSilently(s.Momentum);
        _reverse.SetSilently(s.ReverseScroll);
        _tap.SetSilently(s.TapToClick ?? SystemInfo.ReadTouchpadPrefs().Taps ?? true);
        _pinch.SetSilently(s.PinchToZoom);
        _multi.SetSilently(s.MultiFingerGestures);

        _style.SetSilently(s.KeyboardStyle == KeyboardStyle.Mac ? 1 : 0);
        _winSpotlight.SetSilently(s.WinTapOpensSpotlight);
        _iso.SetSilently(s.SwapIsoKeys);

        _resistance.SetSilently((int)s.Resistance);
        _glow.SetSilently(s.ShowEdgeGlow);
        _hotkey.SetSilently(s.HotkeyEnabled);
        _shake.SetSilently(s.ShakeToSwitch);
        _mouse.SetSilently(s.MouseSpeed);
        _calibrationInfo.Text = s.Calibration.HasMouse
            ? $"Calibrated {s.Calibration.CalibratedAt:d}. Recalibrate if you change the iPad's Tracking Speed."
            : "Not calibrated yet. It takes 20 seconds in Safari, nothing to install, and makes returning to Windows precise.";

        _sharing.SetSilently(s.SharingEnabled);
        _startup.SetSilently(s.StartWithWindows);
        _transport.SetSilently((int)s.Transport);
        _loading = false;
    }

    private void Change(Action<AppSettings> change)
    {
        if (_loading) return;
        _app.UpdateSettings(change);
    }

    private void WireEvents()
    {
        _switchButton.Click += (_, _) =>
        {
            if (_app.Status.OnIpad) _app.SwitchToPc(); else _app.SwitchToIpad();
        };
        _pairButton.Click += (_, _) => _tray.ShowOnboarding(1);

        _arrangement.ArrangementChanged += (device, side, offset) => Change(s =>
        {
            s.Arrangement.MonitorDevice = device;
            s.Arrangement.Side = side;
            s.Arrangement.Offset = offset;
        });
        _model.SelectedIndexChanged += (_, _) =>
        {
            Change(s => s.Arrangement.IpadModel = IpadModel.Presets[_model.SelectedIndex].Name);
            ReloadArrangement();
        };
        _orientation.SelectedIndexChanged += (_, _) =>
        {
            Change(s => { s.Arrangement.Portrait = _orientation.SelectedIndex == 1; s.Arrangement.Offset = null; });
            ReloadArrangement();
        };

        _gestures.SelectedIndexChanged += (_, _) => Change(s => s.TrackpadMode = _gestures.SelectedIndex == 1 ? TrackpadMode.Native : TrackpadMode.Translated);
        _pointer.ValueCommitted += (_, _) => Change(s => s.PointerSpeed = _pointer.Value);
        _scroll.ValueCommitted += (_, _) => Change(s => s.ScrollSpeed = _scroll.Value);
        _momentum.CheckedChanged += (_, _) => Change(s => s.Momentum = _momentum.Checked);
        _reverse.CheckedChanged += (_, _) => Change(s => s.ReverseScroll = _reverse.Checked);
        _tap.CheckedChanged += (_, _) => Change(s => s.TapToClick = _tap.Checked);
        _pinch.CheckedChanged += (_, _) => Change(s => s.PinchToZoom = _pinch.Checked);
        _multi.CheckedChanged += (_, _) => Change(s => s.MultiFingerGestures = _multi.Checked);

        _style.SelectedIndexChanged += (_, _) => Change(s => s.KeyboardStyle = _style.SelectedIndex == 1 ? KeyboardStyle.Mac : KeyboardStyle.Windows);
        _winSpotlight.CheckedChanged += (_, _) => Change(s => s.WinTapOpensSpotlight = _winSpotlight.Checked);
        _iso.CheckedChanged += (_, _) => Change(s => s.SwapIsoKeys = _iso.Checked);

        _resistance.SelectedIndexChanged += (_, _) => Change(s => s.Resistance = (CrossingResistance)_resistance.SelectedIndex);
        _glow.CheckedChanged += (_, _) => Change(s => s.ShowEdgeGlow = _glow.Checked);
        _hotkey.CheckedChanged += (_, _) => Change(s => s.HotkeyEnabled = _hotkey.Checked);
        _shake.CheckedChanged += (_, _) => Change(s => s.ShakeToSwitch = _shake.Checked);
        _mouse.ValueCommitted += (_, _) => Change(s => s.MouseSpeed = _mouse.Value);
        _calibrate.Click += (_, _) => _tray.ShowCalibration();

        _sharing.CheckedChanged += (_, _) => Change(s => s.SharingEnabled = _sharing.Checked);
        _startup.CheckedChanged += (_, _) => Change(s => s.StartWithWindows = _startup.Checked);
        _transport.SelectedIndexChanged += (_, _) => Change(s => s.Transport = (TransportPreference)_transport.SelectedIndex);
    }

    private void ReloadArrangement()
    {
        var a = _app.Settings.Arrangement;
        _arrangement.SetState(SystemInfo.Monitors(), a.MonitorDevice, a.Side, a.Offset, IpadModel.ByName(a.IpadModel), a.Portrait);
    }

    /// <summary>Call on the UI thread when status or settings changed elsewhere.</summary>
    public void RefreshStatus()
    {
        var st = _app.Status;
        string peer = st.Peer ?? "your iPad";
        _headline.Text = "iPad Share";
        switch (st.Transport)
        {
            case TransportState.Connected when st.OnIpad:
                _pill.Set($"Controlling {peer}", Theme.Accent);
                _subline.Text = "Your keyboard and trackpad are driving the iPad. Move back past the edge, shake the pointer, or press Ctrl+Alt+Space to return.";
                break;
            case TransportState.Connected:
                _pill.Set($"Connected to {peer}", Theme.Good);
                _subline.Text = st.SharingEnabled
                    ? $"Move the pointer past the {_app.Settings.Arrangement.Side.ToString().ToLowerInvariant()} edge of your screen (or shake it) to use the iPad."
                    : "Sharing is paused. Turn it on under General.";
                break;
            case TransportState.Advertising:
                _pill.Set("Waiting for your iPad", Theme.Warn);
                _subline.Text = $"On the iPad, open Settings › Bluetooth and tap “{Environment.MachineName}”. After the first time it reconnects by itself.";
                break;
            case TransportState.Starting:
                _pill.Set("Starting Bluetooth…", Theme.SubText);
                _subline.Text = "";
                break;
            default:
                _pill.Set(st.Transport == TransportState.Unavailable ? "Not available" : "Problem", Theme.Bad);
                _subline.Text = st.Detail ?? "Bluetooth isn't ready.";
                break;
        }
        _switchButton.Text = st.OnIpad ? "Back to PC" : "Switch to iPad";
        _switchButton.Enabled = st.Transport == TransportState.Connected && st.SharingEnabled;
        _switchButton.FitToText();
        _pairButton.FitToText();
        _calibrate.FitToText();
        _calibrate.Enabled = st.Transport == TransportState.Connected;
        _arrangement.SetOnIpad(st.OnIpad);
        _calibrationInfo.Text = _app.Settings.Calibration.HasMouse
            ? $"Calibrated {_app.Settings.Calibration.CalibratedAt:d}. Recalibrate if you change the iPad's Tracking Speed."
            : "Not calibrated yet. It takes 20 seconds in Safari, nothing to install, and makes returning to Windows precise.";
    }

    public void ReloadAll()
    {
        LoadSettings();
        RefreshStatus();
    }
}
