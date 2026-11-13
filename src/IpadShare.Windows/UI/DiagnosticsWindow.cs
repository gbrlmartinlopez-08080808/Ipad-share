using System.Diagnostics;
using IpadShare.App.App;

namespace IpadShare.App.UI;

/// <summary>Everything needed to troubleshoot remotely, one click to copy.</summary>
internal sealed class DiagnosticsWindow : Form
{
    private readonly AppController _app;
    private readonly TextBox _text = new();

    public DiagnosticsWindow(AppController app)
    {
        _app = app;
        Text = "iPad Share diagnostics";
        Icon = TrayApp.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(760, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body();

        _text.Multiline = true;
        _text.ReadOnly = true;
        _text.ScrollBars = ScrollBars.Both;
        _text.WordWrap = false;
        _text.Font = new Font("Consolas", 9f);
        _text.BackColor = Theme.Card;
        _text.ForeColor = Theme.Text;
        _text.BorderStyle = BorderStyle.None;
        _text.Dock = DockStyle.Fill;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 10, 12, 8), BackColor = Theme.Background };
        var copy = new PillButton { Text = "Copy", Primary = true, Margin = new Padding(0, 0, 8, 0) };
        var refresh = new PillButton { Text = "Refresh", Margin = new Padding(0, 0, 8, 0) };
        var logs = new PillButton { Text = "Open log folder" };
        copy.FitToText(); refresh.FitToText(); logs.FitToText();
        copy.Click += (_, _) => { try { Clipboard.SetText(_text.Text); copy.Text = "Copied"; } catch { } };
        refresh.Click += (_, _) => Reload();
        logs.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Directory}\"") { UseShellExecute = true }); } catch { }
        };
        bar.Controls.AddRange(new Control[] { copy, refresh, logs });

        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0), BackColor = Theme.Background };
        pad.Controls.Add(_text);
        Controls.Add(pad);
        Controls.Add(bar);
        Reload();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyToWindow(this);
    }

    private void Reload()
    {
        try { _text.Text = _app.Diagnostics().Replace("\r\n", "\n").Replace("\n", "\r\n"); }
        catch (Exception ex) { _text.Text = "Diagnostics failed: " + ex; }
        _text.SelectionStart = 0;
    }
}
