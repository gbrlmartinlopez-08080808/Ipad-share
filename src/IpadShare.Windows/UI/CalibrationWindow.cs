using IpadShare.App.App;
using IpadShare.App.Calibration;
using QRCoder;

namespace IpadShare.App.UI;

/// <summary>One-time pointer calibration through a Safari page (QR code), fully automatic.</summary>
internal sealed class CalibrationWindow : Form
{
    private readonly AppController _app;
    private readonly CalibrationServer _server = new();
    private CalibrationRunner? _runner;
    private CancellationTokenSource? _cts;
    private readonly PictureBox _qr = new();
    private readonly Label _url = new();
    private readonly Label _status = new();
    private readonly ProgressBarFlat _progress = new();
    private readonly PillButton _action = new() { Primary = true, Text = "Start" };
    private bool _running, _done;

    public CalibrationWindow(AppController app)
    {
        _app = app;
        Text = "Calibrate the pointer";
        Icon = TrayApp.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(520, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body();

        var title = new Label { Text = "Calibrate the pointer", Font = Theme.Title(17f), AutoSize = true, Location = new Point(28, 22) };
        var lead = new Label
        {
            Text = "On your iPad, open the Camera and point it at this code, then tap the link. Keep the page open and " +
                   "don't touch the iPad or laptop for about 20 seconds.",
            AutoSize = true, MaximumSize = new Size(464, 0), ForeColor = Theme.SubText, Location = new Point(28, 64),
        };
        _qr.SetBounds(160, 120, 200, 200);
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.BackColor = Color.White;
        _url.SetBounds(28, 328, 464, 24);
        _url.TextAlign = ContentAlignment.MiddleCenter;
        _url.ForeColor = Theme.SubText;
        _url.Font = Theme.Body(9f);
        _status.SetBounds(28, 370, 464, 48);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _progress.SetBounds(60, 424, 400, 8);
        var note = new Label
        {
            Text = "If Windows asks whether iPad Share may use the network, allow it on private networks. The iPad and laptop must be on the same Wi-Fi.",
            AutoSize = true, MaximumSize = new Size(464, 0), ForeColor = Theme.SubText, Font = Theme.Body(8.5f), Location = new Point(28, 446),
        };
        _action.SetBounds(200, 500, 120, 34);
        _action.Click += (_, _) => OnAction();
        Controls.AddRange(new Control[] { title, lead, _qr, _url, _status, _progress, note, _action });
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.ApplyToWindow(this);
        try
        {
            _server.Start();
            _url.Text = _server.Url;
            _qr.Image = RenderQr(_server.Url!);
            _runner = new CalibrationRunner(_app.CreateCalibrationHost(), _server);
            _runner.Progress += (msg, v) => Ui(() => { _status.Text = msg; if (v >= 0) _progress.Value = v; });
            _app.StatusChanged += OnAppStatus;
            _server.ClientChanged += () => Ui(UpdateWaiting);
            _app.CancelRequested += OnCancelRequested;
            UpdateWaiting();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _status.ForeColor = Theme.Bad;
            _action.Text = "Close";
            _done = true;
        }
    }

    private void OnCancelRequested() => _cts?.Cancel();

    private void OnAppStatus() => Ui(UpdateWaiting);

    /// <summary>Marshal to the UI thread, ignoring calls that arrive after the window closed.</summary>
    private void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }   // includes ObjectDisposedException
    }

    private async void UpdateWaiting()
    {
        if (_running || _done) return;
        if (!_app.Status.Transport.Equals(Core.Transport.TransportState.Connected))
        {
            _status.Text = "Waiting for your iPad to connect over Bluetooth…";
            _action.Enabled = false;
            return;
        }
        if (_server.ClientConnected)
        {
            _status.Text = "Page connected. Starting…";
            _action.Enabled = true;
            await Task.Delay(1200);
            if (!_running && !_done && _server.ClientConnected) await RunAsync();
        }
        else
        {
            _status.Text = "Waiting for the page to open on the iPad…";
            _action.Enabled = false;
        }
    }

    private async void OnAction()
    {
        if (_done) { Close(); return; }
        if (_running) { _cts?.Cancel(); return; }
        await RunAsync();
    }

    private async Task RunAsync()
    {
        if (_runner == null || _running) return;
        _running = true;
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _action.Text = "Cancel";
        _action.Primary = false;
        _action.Enabled = true;
        _status.ForeColor = Theme.Text;
        try
        {
            var curve = await _runner.RunAsync(_cts.Token);
            if (curve != null)
            {
                _app.SaveCalibration(curve);
                _status.Text = "Calibrated. Switching back to Windows is now precise.";
                _status.ForeColor = Theme.Good;
                _progress.Value = 1;
                _done = true;
                _action.Text = "Done";
                _action.Primary = true;
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Calibration cancelled.";
            _action.Text = "Try again";
            _action.Primary = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Calibration failed: " + ex.Message);
            _status.Text = ex.Message;
            _status.ForeColor = Theme.Bad;
            _action.Text = "Try again";
            _action.Primary = true;
        }
        finally
        {
            _running = false;
            _action.Invalidate();
        }
    }

    private static Bitmap RenderQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix;
        int n = m.Count, scale = 8;
        var bmp = new Bitmap(n * scale, n * scale);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (m[y][x]) g.FillRectangle(Brushes.Black, x * scale, y * scale, scale, scale);
        return bmp;
    }

    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        _app.CancelRequested -= OnCancelRequested;
        _app.StatusChanged -= OnAppStatus;
        _cts?.Cancel();
        base.OnFormClosed(e);
        await _server.DisposeAsync();
    }
}

/// <summary>Thin accent progress bar.</summary>
internal sealed class ProgressBarFlat : DrawnControl
{
    private double _value;
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 1); Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(0, 0, Width, Height);
        using (var p = Theme.RoundRect(r, Height / 2f)) using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);
        if (_value > 0)
        {
            var f = new RectangleF(0, 0, Math.Max(Height, (float)(Width * _value)), Height);
            using var p = Theme.RoundRect(f, Height / 2f);
            using var b = new SolidBrush(Theme.Accent);
            g.FillPath(b, p);
        }
    }
}
