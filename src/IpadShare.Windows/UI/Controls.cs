using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace IpadShare.App.UI;

/// <summary>Base for owner-drawn controls: double-buffered, transparent-aware, DPI helper.</summary>
internal abstract class DrawnControl : Control
{
    protected DrawnControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        Font = Theme.Body();
    }

    protected float S(float v) => v * DeviceDpi / 96f;
    protected bool Hover { get; private set; }

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }
}

/// <summary>Windows 11-style toggle switch.</summary>
internal sealed class ToggleSwitch : DrawnControl
{
    private bool _checked;

    public ToggleSwitch()
    {
        Size = new Size(44, 22);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public event EventHandler? CheckedChanged;

    /// <summary>Set without raising CheckedChanged (for loading settings).</summary>
    public void SetSilently(bool v) { _checked = v; Invalidate(); }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) Checked = !Checked;
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        using var path = Theme.RoundRect(r, r.Height / 2);
        if (_checked)
        {
            using var b = new SolidBrush(Enabled ? Theme.Accent : Theme.Track);
            g.FillPath(b, path);
        }
        else
        {
            using var b = new SolidBrush(Hover ? Theme.ControlHover : Theme.Control);
            g.FillPath(b, path);
            using var pen = new Pen(Theme.SubText, S(1.2f));
            g.DrawPath(pen, path);
        }
        float d = r.Height - S(8);
        float x = _checked ? r.Right - d - S(4) : r.X + S(4);
        using var knob = new SolidBrush(_checked ? Color.White : Theme.SubText);
        g.FillEllipse(knob, x, r.Y + (r.Height - d) / 2, d, d);
        if (Focused && ShowFocusCues)
        {
            using var fp = new Pen(Theme.Text, 1) { DashStyle = DashStyle.Dot };
            g.DrawPath(fp, path);
        }
    }
}

/// <summary>Segmented picker (like iOS UISegmentedControl).</summary>
internal sealed class Segmented : DrawnControl
{
    private string[] _items = Array.Empty<string>();
    private int _selected;
    private int _hoverIndex = -1;

    public Segmented()
    {
        Size = new Size(240, 30);
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Items { get => _items; set { _items = value; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, _items.Length - 1));
            if (_selected == value) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectedIndexChanged;
    public void SetSilently(int i) { _selected = Math.Clamp(i, 0, Math.Max(0, _items.Length - 1)); Invalidate(); }

    private int IndexAt(int x) => _items.Length == 0 ? -1 : Math.Clamp(x * _items.Length / Math.Max(1, Width), 0, _items.Length - 1);

    protected override void OnMouseMove(MouseEventArgs e) { int i = IndexAt(e.X); if (i != _hoverIndex) { _hoverIndex = i; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hoverIndex = -1; base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == System.Windows.Forms.MouseButtons.Left) SelectedIndex = IndexAt(e.X); base.OnMouseDown(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Theme.RoundRect(r, S(6)))
        using (var b = new SolidBrush(Theme.Control))
        using (var pen = new Pen(Theme.CardBorder))
        {
            g.FillPath(b, path);
            g.DrawPath(pen, path);
        }
        if (_items.Length == 0) return;
        float w = r.Width / _items.Length;
        for (int i = 0; i < _items.Length; i++)
        {
            var cell = new RectangleF(r.X + i * w + S(2), r.Y + S(2), w - S(4), r.Height - S(4));
            if (i == _selected)
            {
                using var path = Theme.RoundRect(cell, S(5));
                using var b = new SolidBrush(Theme.Accent);
                g.FillPath(b, path);
            }
            else if (i == _hoverIndex)
            {
                using var path = Theme.RoundRect(cell, S(5));
                using var b = new SolidBrush(Theme.ControlHover);
                g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, _items[i], Font, Rectangle.Round(cell), i == _selected ? Theme.AccentText : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>Flat slider with accent fill.</summary>
internal sealed class Slider : DrawnControl
{
    private double _value = 1, _min = 0, _max = 1;
    private bool _dragging;

    public Slider()
    {
        Size = new Size(200, 24);
        Cursor = Cursors.Hand;
    }

    public double Minimum { get => _min; set { _min = value; Invalidate(); } }
    public double Maximum { get => _max; set { _max = value; Invalidate(); } }
    /// <summary>Optional value that snaps when the thumb is near it (e.g. 1.0 = default).</summary>
    public double? Detent { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, _min, _max);
            if (Math.Abs(value - _value) < 1e-9) return;
            _value = value;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;
    /// <summary>Raised when the user releases the thumb (persist here).</summary>
    public event EventHandler? ValueCommitted;
    public void SetSilently(double v) { _value = Math.Clamp(v, _min, _max); Invalidate(); }

    private RectangleF TrackRect => new(S(9), Height / 2f - S(2), Width - S(18), S(4));

    private void SetFromX(int x)
    {
        var t = TrackRect;
        double f = Math.Clamp((x - t.X) / t.Width, 0, 1);
        double v = _min + f * (_max - _min);
        if (Detent is double d && Math.Abs(v - d) < (_max - _min) * 0.03) v = d;
        Value = Math.Round(v, 2);
    }

    protected override void OnMouseDown(MouseEventArgs e) { _dragging = true; Capture = true; SetFromX(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging) { _dragging = false; Capture = false; ValueCommitted?.Invoke(this, EventArgs.Empty); }
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var t = TrackRect;
        double f = _max > _min ? (_value - _min) / (_max - _min) : 0;
        using (var path = Theme.RoundRect(t, t.Height / 2))
        using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, path);
        var filled = new RectangleF(t.X, t.Y, (float)(t.Width * f), t.Height);
        if (filled.Width > 1)
        {
            using var path = Theme.RoundRect(filled, t.Height / 2);
            using var b = new SolidBrush(Theme.Accent);
            g.FillPath(b, path);
        }
        if (Detent is double d && _max > _min)
        {
            float dx = t.X + (float)(t.Width * (d - _min) / (_max - _min));
            using var p = new Pen(Theme.SubText, S(1));
            g.DrawLine(p, dx, t.Bottom + S(3), dx, t.Bottom + S(6));
        }
        float cx = t.X + (float)(t.Width * f), r = S(_dragging || Hover ? 8 : 7);
        using (var b = new SolidBrush(Theme.Card)) g.FillEllipse(b, cx - r, Height / 2f - r, 2 * r, 2 * r);
        using (var pen = new Pen(Theme.CardBorder, S(1))) g.DrawEllipse(pen, cx - r, Height / 2f - r, 2 * r, 2 * r);
        using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, cx - r * 0.55f, Height / 2f - r * 0.55f, r * 1.1f, r * 1.1f);
    }
}

/// <summary>Rounded button: accent (primary) or subtle (secondary).</summary>
internal sealed class PillButton : DrawnControl
{
    private bool _pressed;
    public bool Primary { get; set; }

    public PillButton()
    {
        Size = new Size(120, 32);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    public void FitToText()
    {
        var sz = TextRenderer.MeasureText(Text, Font);
        Width = sz.Width + (int)S(32);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, S(6));
        Color fill = Primary ? Theme.Accent : (Hover ? Theme.ControlHover : Theme.Control);
        if (_pressed) fill = Theme.Blend(fill, Theme.Dark ? Color.White : Color.Black, 0.12);
        if (!Enabled) fill = Theme.Blend(fill, Theme.Background, 0.5);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!Primary) using (var pen = new Pen(Theme.CardBorder)) g.DrawPath(pen, path);
        var tc = Primary ? Theme.AccentText : Theme.Text;
        if (!Enabled) tc = Theme.SubText;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, tc,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Picker button that opens a menu of choices.</summary>
internal sealed class DropDown : DrawnControl
{
    private string[] _items = Array.Empty<string>();
    private int _selected;

    public DropDown()
    {
        Size = new Size(220, 30);
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Items { get => _items; set { _items = value; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set { if (value == _selected) return; _selected = value; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }

    public event EventHandler? SelectedIndexChanged;
    public void SetSilently(int i) { _selected = i; Invalidate(); }

    protected override void OnClick(EventArgs e)
    {
        var menu = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false, Font = Font };
        for (int i = 0; i < _items.Length; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem(_items[i]) { Checked = i == _selected };
            item.Click += (_, _) => SelectedIndex = idx;
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, new Point(0, Height));
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Theme.RoundRect(r, S(6)))
        {
            using var b = new SolidBrush(Hover ? Theme.ControlHover : Theme.Control);
            g.FillPath(b, path);
            using var pen = new Pen(Theme.CardBorder);
            g.DrawPath(pen, path);
        }
        string text = _selected >= 0 && _selected < _items.Length ? _items[_selected] : "";
        var tr = new Rectangle((int)S(10), 0, Width - (int)S(34), Height);
        TextRenderer.DrawText(g, text, Font, tr, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        float cx = Width - S(16), cy = Height / 2f;
        using var p = new Pen(Theme.SubText, S(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(p, new[] { new PointF(cx - S(4), cy - S(2)), new PointF(cx, cy + S(2)), new PointF(cx + S(4), cy - S(2)) });
    }
}

/// <summary>Context-menu renderer matching the theme.</summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer() : base(new MenuColors()) { RoundedEdges = true; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.SubText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text;
        base.OnRenderArrow(e);
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color MenuItemSelected => Theme.ControlHover;
        public override Color MenuItemSelectedGradientBegin => Theme.ControlHover;
        public override Color MenuItemSelectedGradientEnd => Theme.ControlHover;
        public override Color MenuItemBorder => Theme.ControlHover;
        public override Color MenuBorder => Theme.CardBorder;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color SeparatorDark => Theme.CardBorder;
        public override Color SeparatorLight => Theme.CardBorder;
        public override Color CheckBackground => Theme.Accent;
        public override Color CheckSelectedBackground => Theme.Accent;
        public override Color CheckPressedBackground => Theme.Accent;
    }
}

/// <summary>Rounded card containing a vertical stack of rows.</summary>
internal sealed class Card : Panel
{
    private readonly FlowLayoutPanel _stack;

    public Card(string? title = null, string? subtitle = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        Padding = new Padding(18, 14, 18, 14);
        Margin = new Padding(0, 0, 0, 12);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Card,
            Location = new Point(Padding.Left, Padding.Top),
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        Controls.Add(_stack);
        if (title != null)
        {
            _stack.Controls.Add(new Label
            {
                Text = title, Font = Theme.Semibold(11.5f), ForeColor = Theme.Text, AutoSize = true,
                Margin = new Padding(0, 0, 0, subtitle != null ? 2 : 8), BackColor = Theme.Card,
            });
        }
        if (subtitle != null)
        {
            _stack.Controls.Add(new Label
            {
                Text = subtitle, ForeColor = Theme.SubText, AutoSize = true, MaximumSize = new Size(500, 0),
                Margin = new Padding(0, 0, 0, 10), BackColor = Theme.Card,
            });
        }
    }

    public int ContentWidth { get; set; } = 524;

    public T Add<T>(T c) where T : Control
    {
        c.Margin = c.Margin == Padding.Empty || c.Margin == new Padding(3) ? new Padding(0, 4, 0, 4) : c.Margin;
        _stack.Controls.Add(c);
        return c;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        g.Clear(Theme.Background);
        float s = DeviceDpi / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, 8 * s);
        using var b = new SolidBrush(Theme.Card);
        using var pen = new Pen(Theme.CardBorder);
        g.FillPath(b, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>A setting line: title + optional description on the left, a control on the right.</summary>
internal sealed class SettingRow : TableLayoutPanel
{
    public SettingRow(string title, string? description, Control control, int width = 524)
    {
        BackColor = Theme.Card;
        ColumnCount = 2;
        RowCount = 1;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Width = width;
        MinimumSize = new Size(width, 0);
        Margin = new Padding(0, 6, 0, 6);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var text = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Card,
            Margin = new Padding(0, 0, 12, 0), Anchor = AnchorStyles.Left,
        };
        text.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 0) });
        if (description != null)
            text.Controls.Add(new Label
            {
                Text = description, AutoSize = true, ForeColor = Theme.SubText, BackColor = Theme.Card,
                Font = Theme.Body(9f), MaximumSize = new Size(width - control.Width - 24, 0), Margin = new Padding(0, 1, 0, 0),
            });
        Controls.Add(text, 0, 0);
        control.Anchor = AnchorStyles.Right;
        control.Margin = new Padding(0);
        Controls.Add(control, 1, 0);
    }
}

/// <summary>Small coloured status dot + text.</summary>
internal sealed class StatusPill : DrawnControl
{
    private Color _dot = Theme.SubText;

    public StatusPill() { Size = new Size(200, 26); }

    public void Set(string text, Color dot)
    {
        Text = text;
        _dot = dot;
        var sz = TextRenderer.MeasureText(text, Font);
        Width = sz.Width + (int)S(34);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Theme.RoundRect(r, r.Height / 2))
        using (var b = new SolidBrush(Theme.Blend(Theme.Card, _dot, 0.14)))
            g.FillPath(b, path);
        float d = S(8);
        using (var b = new SolidBrush(_dot)) g.FillEllipse(b, S(11), (Height - d) / 2, d, d);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)S(24), 0, Width - (int)S(26), Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

internal enum CheckKind { Ok, Warn, Bad, Bullet, Pending }

/// <summary>A line of text with a drawn status icon (no reliance on font glyphs like ✓).</summary>
internal sealed class CheckLine : DrawnControl
{
    private CheckKind _kind;
    private readonly int _maxWidth;

    public CheckLine(CheckKind kind, string text, int maxWidth)
    {
        _kind = kind;
        _maxWidth = maxWidth;
        Text = text;
        Margin = new Padding(0, 2, 0, 10);
        Relayout();
    }

    public void Set(CheckKind kind, string text)
    {
        _kind = kind;
        Text = text;
        Relayout();
        Invalidate();
    }

    private int Indent => (int)S(28);

    private void Relayout()
    {
        var sz = TextRenderer.MeasureText(Text, Font, new Size(_maxWidth - Indent, int.MaxValue), TextFormatFlags.WordBreak);
        Size = new Size(_maxWidth, Math.Max(sz.Height, (int)S(20)) + 2);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Relayout(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Relayout(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        float d = S(18), y = S(1);
        var circle = new RectangleF(0, y, d, d);
        Color c = _kind switch
        {
            CheckKind.Ok => Theme.Good,
            CheckKind.Warn => Theme.Warn,
            CheckKind.Bad => Theme.Bad,
            CheckKind.Pending => Theme.SubText,
            _ => Theme.SubText,
        };
        if (_kind == CheckKind.Bullet)
        {
            float b = S(6);
            using var br = new SolidBrush(Theme.SubText);
            g.FillEllipse(br, d / 2 - b / 2, y + d / 2 - b / 2, b, b);
        }
        else
        {
            using (var br = new SolidBrush(c)) g.FillEllipse(br, circle);
            using var pen = new Pen(Color.White, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float cx = d / 2, cy = y + d / 2;
            switch (_kind)
            {
                case CheckKind.Ok:
                    g.DrawLines(pen, new[] { new PointF(cx - S(4), cy), new PointF(cx - S(1), cy + S(3)), new PointF(cx + S(4.5f), cy - S(3.5f)) });
                    break;
                case CheckKind.Bad:
                    g.DrawLine(pen, cx - S(3.5f), cy - S(3.5f), cx + S(3.5f), cy + S(3.5f));
                    g.DrawLine(pen, cx + S(3.5f), cy - S(3.5f), cx - S(3.5f), cy + S(3.5f));
                    break;
                case CheckKind.Warn:
                    g.DrawLine(pen, cx, cy - S(4), cx, cy + S(1));
                    using (var dot = new SolidBrush(Color.White)) g.FillEllipse(dot, cx - S(1.2f), cy + S(3), S(2.4f), S(2.4f));
                    break;
                case CheckKind.Pending:
                    using (var dot = new SolidBrush(Color.White))
                        for (int i = -1; i <= 1; i++) g.FillEllipse(dot, cx + i * S(4) - S(1.2f), cy - S(1.2f), S(2.4f), S(2.4f));
                    break;
            }
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(Indent, 0, Width - Indent, Height), ForeColor,
            TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
    }
}

/// <summary>Page indicator dots.</summary>
internal sealed class StepDots : DrawnControl
{
    private int _count, _index;
    public void Set(int count, int index) { _count = count; _index = index; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        float d = S(7), gap = S(8), y = (Height - d) / 2;
        for (int i = 0; i < _count; i++)
        {
            float w = i == _index ? d * 2.6f : d;
            float x = i * (d + gap) + (i > _index ? d * 1.6f : 0);
            using var p = Theme.RoundRect(new RectangleF(x, y, w, d), d / 2);
            using var b = new SolidBrush(i == _index ? Theme.Accent : Theme.Track);
            g.FillPath(b, p);
        }
    }
}
