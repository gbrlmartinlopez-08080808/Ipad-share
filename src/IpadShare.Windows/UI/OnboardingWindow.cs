using IpadShare.App.App;
using IpadShare.App.Bluetooth;
using IpadShare.Core.Handoff;
using IpadShare.Core.Settings;
using IpadShare.Core.Transport;

namespace IpadShare.App.UI;

/// <summary>First-run guide: checks → pair → arrange → try it → gestures → done.</summary>
internal sealed class OnboardingWindow : Form
{
    private const int W = 500;
    private readonly AppController _app;
    private readonly TrayApp _tray;
    private readonly Panel _host = new();
    private readonly PillButton _back = new() { Text = "Back" };
    private readonly PillButton _next = new() { Text = "Next", Primary = true };
    private readonly StepDots _dots = new();
    private readonly List<Func<Control>> _pages;
    private int _page;

    // live-updated widgets on the current page
    private StatusPill? _pairPill;
    private Label? _pairDetail;
    private CheckLine? _tryLabel;
    private bool _wasOnIpad, _triedCrossing;
    private CheckLine? _btLabel;
    private ArrangementView? _arr;

    public OnboardingWindow(AppController app, TrayApp tray, int startPage)
    {
        _app = app;
        _tray = tray;
        Text = "Set up iPad Share";
        Icon = TrayApp.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(W + 60, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body();

        _pages = new() { Welcome, Pair, Arrange, TryIt, Gestures, Done };

        _host.SetBounds(30, 24, W, 450);
        _host.BackColor = Theme.Background;
        Controls.Add(_host);

        _dots.SetBounds(30, 505, 160, 20);
        Controls.Add(_dots);

        _back.SetBounds(W + 30 - 230, 498, 100, 34);
        _next.SetBounds(W + 30 - 120, 498, 120, 34);
        _back.Click += (_, _) => Show(_page - 1);
        _next.Click += (_, _) => NextClicked();
        Controls.Add(_back);
        Controls.Add(_next);

        _app.StatusChanged += OnStatus;
        FormClosed += (_, _) => _app.StatusChanged -= OnStatus;
        Show(Math.Clamp(startPage, 0, _pages.Count - 1));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyToWindow(this);
    }

    private void OnStatus()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(UpdateLive);
    }

    private void Show(int page)
    {
        if (page < 0 || page >= _pages.Count) return;
        _page = page;
        _pairPill = null; _pairDetail = null; _tryLabel = null; _btLabel = null; _arr = null;
        _host.SuspendLayout();
        foreach (Control c in _host.Controls) c.Dispose();
        _host.Controls.Clear();
        var content = _pages[page]();
        content.Dock = DockStyle.Fill;
        _host.Controls.Add(content);
        _host.ResumeLayout();
        _back.Visible = page > 0;
        _next.Text = page == _pages.Count - 1 ? "Finish" : "Next";
        _dots.Set(_pages.Count, page);
        UpdateLive();
    }

    private void NextClicked()
    {
        if (_page == _pages.Count - 1)
        {
            _app.UpdateSettings(s => s.FirstRunCompleted = true);
            Close();
            return;
        }
        Show(_page + 1);
    }

    // ------------------------------------------------------------------
    private static FlowLayoutPanel Stack()
    {
        return new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Background, AutoScroll = false,
            Padding = new Padding(0),
        };
    }

    private static Label H1(string text) => new()
    {
        Text = text, Font = Theme.Title(18f), AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 10),
    };

    private static Label P(string text, bool sub = true) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(W - 24, 0), ForeColor = sub ? Theme.SubText : Theme.Text,
        Margin = new Padding(0, 0, 0, 12),
    };

    private static Label Step(string n, string text) => new()
    {
        Text = $"{n}   {text}", AutoSize = true, MaximumSize = new Size(W - 24, 0), ForeColor = Theme.Text,
        Font = Theme.Body(10.5f), Margin = new Padding(0, 4, 0, 8),
    };

    private Control Welcome()
    {
        var s = Stack();
        s.Controls.Add(new PictureBox { Image = TrayApp.AppIcon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64), Margin = new Padding(0, 0, 0, 12) });
        s.Controls.Add(H1("Use your laptop with your iPad"));
        s.Controls.Add(P("Move the pointer past the edge of your screen and your laptop's keyboard and trackpad control the iPad. " +
                         "Nothing is installed on the iPad: it sees your laptop as a Bluetooth keyboard and trackpad."));
        _btLabel = new CheckLine(CheckKind.Pending, "Checking Bluetooth…", W - 24);
        s.Controls.Add(_btLabel);
        var pad = _app.Touchpads.FirstOrDefault();
        s.Controls.Add(pad != null
            ? new CheckLine(CheckKind.Ok, $"Precision touchpad found ({pad.Geometry.WidthMm:0} × {pad.Geometry.HeightMm:0} mm). Gestures will be tuned to it.", W)
            : new CheckLine(CheckKind.Warn, "No Precision Touchpad was found. Pointer and scrolling still work; multi-finger gestures need a precision touchpad.", W - 24));
        s.Controls.Add(new CheckLine(CheckKind.Ok, "Keyboard ready.", W - 24));
        _ = ProbeBluetooth();
        return s;
    }

    private async Task ProbeBluetooth()
    {
        var label = _btLabel;
        var (ok, message) = await BleHidPeripheral.ProbeAsync();
        bool bridge = BridgeTransport.FindBridgePorts().Count > 0;
        if (label == null || label.IsDisposed) return;
        if (ok) label.Set(CheckKind.Ok, message);
        else if (bridge) label.Set(CheckKind.Ok, "USB bridge found. It will be used for the connection.");
        else label.Set(CheckKind.Bad, message + " See the README for the US$8 USB bridge option.");
    }

    private Control Pair()
    {
        var s = Stack();
        s.Controls.Add(H1("Pair your iPad"));
        s.Controls.Add(Step("1", "On your iPad, open Settings › Bluetooth."));
        s.Controls.Add(Step("2", $"Under Other Devices, tap “{Environment.MachineName}”."));
        s.Controls.Add(Step("3", "If Windows asks to pair or allow the device, choose Allow."));
        s.Controls.Add(P("You only do this once. Afterwards the iPad reconnects by itself whenever iPad Share is running."));
        _pairPill = new StatusPill { Margin = new Padding(0, 6, 0, 4) };
        s.Controls.Add(_pairPill);
        _pairDetail = P("", sub: true);
        _pairDetail.Margin = new Padding(0, 4, 0, 12);
        s.Controls.Add(_pairDetail);
        s.Controls.Add(P("Paired before but it won't connect? On the iPad tap the (i) next to the laptop › Forget This Device, then pair again."));
        return s;
    }

    private Control Arrange()
    {
        var s = Stack();
        s.Controls.Add(H1("Where is your iPad?"));
        s.Controls.Add(P("Drag the iPad to the side of your screen where it actually sits. The pointer crosses at the blue edge."));
        var a = _app.Settings.Arrangement;
        _arr = new ArrangementView { Width = W, Height = 230, Margin = new Padding(0, 0, 0, 10) };
        _arr.SetState(SystemInfo.Monitors(), a.MonitorDevice, a.Side, a.Offset, IpadModel.ByName(a.IpadModel), a.Portrait);
        _arr.ArrangementChanged += (dev, side, off) => _app.UpdateSettings(x =>
        {
            x.Arrangement.MonitorDevice = dev; x.Arrangement.Side = side; x.Arrangement.Offset = off;
        });
        s.Controls.Add(_arr);
        var orient = new Segmented { Items = new[] { "Landscape", "Portrait" }, Width = 220 };
        orient.SetSilently(a.Portrait ? 1 : 0);
        orient.SelectedIndexChanged += (_, _) =>
        {
            _app.UpdateSettings(x => { x.Arrangement.Portrait = orient.SelectedIndex == 1; x.Arrangement.Offset = null; });
            var na = _app.Settings.Arrangement;
            _arr.SetState(SystemInfo.Monitors(), na.MonitorDevice, na.Side, na.Offset, IpadModel.ByName(na.IpadModel), na.Portrait);
        };
        s.Controls.Add(orient);
        return s;
    }

    private Control TryIt()
    {
        var s = Stack();
        s.Controls.Add(H1("Try it"));
        string side = _app.Settings.Arrangement.Side.ToString().ToLowerInvariant();
        s.Controls.Add(P($"Move the pointer to the {side} edge of your screen and keep pushing a little. A blue glow builds up, then the pointer appears on your iPad."));
        s.Controls.Add(P("To come back, move the pointer back past the same edge of the iPad. Ctrl+Alt+Space also switches."));
        _tryLabel = new CheckLine(CheckKind.Pending, "Waiting for you to cross the edge…", W - 24) { Margin = new Padding(0, 10, 0, 0) };
        s.Controls.Add(_tryLabel);
        _wasOnIpad = _app.Status.OnIpad;
        _triedCrossing = false;
        return s;
    }

    private Control Gestures()
    {
        var s = Stack();
        s.Controls.Add(H1("iPad gestures"));
        var st = _app.Status;
        bool possible = st.TouchpadFound && (st.Transport != TransportState.Connected || st.MaxPayload >= 29);
        if (!possible)
        {
            s.Controls.Add(P("Your trackpad gestures will be translated into iPad shortcuts: three-finger swipe up for Home, " +
                             "up and hold for the App Switcher, sideways to switch apps."));
            return s;
        }
        s.Controls.Add(P("Your laptop can also pass your fingers straight to iPadOS, for the real iPad trackpad feel. " +
                         "It depends on the iPad accepting it, so let's test it:"));
        var test = new PillButton { Text = "Test iPad-native gestures", Primary = true, Margin = new Padding(0, 4, 0, 12) };
        test.FitToText();
        var question = P("Move onto the iPad, then swipe up with three fingers. Did it go to the Home Screen?", sub: false);
        question.Visible = false;
        var answers = new FlowLayoutPanel { AutoSize = true, Visible = false, BackColor = Theme.Background };
        var yes = new PillButton { Text = "Yes, keep them", Primary = true, Margin = new Padding(0, 0, 8, 0) };
        var no = new PillButton { Text = "No, use translated" };
        yes.FitToText(); no.FitToText();
        answers.Controls.Add(yes); answers.Controls.Add(no);
        var result = new CheckLine(CheckKind.Ok, "", W - 24) { Visible = false };
        test.Click += (_, _) =>
        {
            _app.UpdateSettings(x => x.TrackpadMode = TrackpadMode.Native);
            question.Visible = answers.Visible = true;
        };
        yes.Click += (_, _) =>
        {
            _app.UpdateSettings(x => { x.TrackpadMode = TrackpadMode.Native; x.NativeModeVerified = true; });
            result.Set(CheckKind.Ok, "iPad-native gestures are on.");
            result.Visible = true;
            answers.Visible = false;
        };
        no.Click += (_, _) =>
        {
            _app.UpdateSettings(x => { x.TrackpadMode = TrackpadMode.Translated; x.NativeModeVerified = false; });
            result.Set(CheckKind.Ok, "Using translated gestures. They work on every iPad.");
            result.Visible = true;
            answers.Visible = false;
        };
        s.Controls.Add(test);
        s.Controls.Add(question);
        s.Controls.Add(answers);
        s.Controls.Add(result);
        s.Controls.Add(P("You can change this any time under Trackpad › Gestures."));
        return s;
    }

    private Control Done()
    {
        var s = Stack();
        s.Controls.Add(H1("You're all set"));
        s.Controls.Add(new CheckLine(CheckKind.Bullet, "Ctrl+Alt+Space jumps between your laptop and the iPad.", W - 24));
        s.Controls.Add(new CheckLine(CheckKind.Bullet, "Set the iPad's keyboard layout to match your laptop: on the iPad, Settings › General › Keyboard › Hardware Keyboard.", W - 24));
        s.Controls.Add(new CheckLine(CheckKind.Bullet, "For the most precise handoff, run Calibrate under Switching (20 seconds in Safari, nothing to install).", W - 24));
        s.Controls.Add(new CheckLine(CheckKind.Bullet, "iPad Share lives in the tray (^) and starts with Windows, so your iPad reconnects by itself.", W - 24));
        var cal = new PillButton { Text = "Calibrate now", Margin = new Padding(0, 8, 0, 0) };
        cal.FitToText();
        cal.Click += (_, _) => _tray.ShowCalibration();
        s.Controls.Add(cal);
        return s;
    }

    // ------------------------------------------------------------------
    private void UpdateLive()
    {
        var st = _app.Status;
        if (_pairPill != null)
        {
            switch (st.Transport)
            {
                case TransportState.Connected:
                    _pairPill.Set($"Connected to {st.Peer ?? "your iPad"}", Theme.Good);
                    break;
                case TransportState.Advertising:
                    _pairPill.Set("Waiting for your iPad…", Theme.Warn);
                    break;
                case TransportState.Starting:
                    _pairPill.Set("Starting Bluetooth…", Theme.SubText);
                    break;
                default:
                    _pairPill.Set("Bluetooth not available", Theme.Bad);
                    break;
            }
            if (_pairDetail != null)
                _pairDetail.Text = st.Transport is TransportState.Unavailable or TransportState.Error ? st.Detail ?? "" : "";
        }
        if (_tryLabel != null)
        {
            if (st.Transport != TransportState.Connected)
                _tryLabel.Set(CheckKind.Warn, "Connect your iPad first (previous step).");
            else if (st.OnIpad)
            {
                _triedCrossing = true;
                _tryLabel.Set(CheckKind.Ok, "You're on the iPad! Now move back the same way.");
            }
            else if (_triedCrossing)
                _tryLabel.Set(CheckKind.Ok, "Perfect. That's all there is to it.");
            else
                _tryLabel.Set(CheckKind.Pending, "Waiting for you to cross the edge…");
            _wasOnIpad = st.OnIpad;
        }
        _arr?.SetOnIpad(st.OnIpad);
    }
}
