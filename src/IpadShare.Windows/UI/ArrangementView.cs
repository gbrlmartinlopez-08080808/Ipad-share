using IpadShare.App.App;
using IpadShare.Core.Handoff;

namespace IpadShare.App.UI;

/// <summary>
/// Drag-to-arrange view, like the Displays settings on a Mac: the PC monitors and the iPad are drawn
/// to physical scale; dragging the iPad snaps it to the nearest free monitor edge.
/// </summary>
internal sealed class ArrangementView : DrawnControl
{
    private List<MonitorInfo> _monitors = new();
    private IpadModel _model = IpadModel.IpadAirM1;
    private bool _portrait;
    private string? _device;
    private EdgeSide _side = EdgeSide.Right;
    private double _offset;            // fraction of the monitor edge
    private bool _dragging;
    private PointF _dragDelta;
    private RectangleF _ipadView;
    private bool _onIpad;

    public ArrangementView()
    {
        Size = new Size(524, 210);
        Cursor = Cursors.Default;
    }

    /// <summary>(monitor device, side, offset fraction) after the user drops the iPad.</summary>
    public event Action<string, EdgeSide, double>? ArrangementChanged;

    public void SetState(List<MonitorInfo> monitors, string? device, EdgeSide side, double? offset, IpadModel model, bool portrait)
    {
        _monitors = monitors;
        _model = model;
        _portrait = portrait;
        var mon = Monitor(device);
        _device = mon?.Device;
        _side = side;
        _offset = mon == null ? 0 : offset ?? ScreenLayout.CenteredOffset(mon.Bounds, mon.PhysicalMm, side, model, portrait);
        Invalidate();
    }

    public void SetOnIpad(bool onIpad) { if (_onIpad != onIpad) { _onIpad = onIpad; Invalidate(); } }

    private MonitorInfo? Monitor(string? device) =>
        _monitors.FirstOrDefault(m => m.Device == device) ?? _monitors.FirstOrDefault(m => m.Primary) ?? _monitors.FirstOrDefault();

    /// <summary>iPad rectangle in virtual-screen pixels for an arrangement.</summary>
    private RectD IpadWorldRect(MonitorInfo mon, EdgeSide side, double offset)
    {
        var l = ScreenLayout.FromArrangement(mon.Bounds, mon.PhysicalMm, side, offset, _model, _portrait);
        var mm = _portrait ? _model.LandscapeMm.Rotated : _model.LandscapeMm;
        double along = l.IpadLengthPx;
        double across = side.IsHorizontalNeighbour() ? along * mm.Width / mm.Height : along * mm.Height / mm.Width;
        var b = mon.Bounds;
        return side switch
        {
            EdgeSide.Right => new RectD(b.Right, l.IpadStart, across, along),
            EdgeSide.Left => new RectD(b.Left - across, l.IpadStart, across, along),
            EdgeSide.Bottom => new RectD(l.IpadStart, b.Bottom, along, across),
            _ => new RectD(l.IpadStart, b.Top - across, along, across),
        };
    }

    private (float scale, float ox, float oy) Transform()
    {
        var mon = Monitor(_device);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Inc(RectD r) { minX = Math.Min(minX, r.Left); minY = Math.Min(minY, r.Top); maxX = Math.Max(maxX, r.Right); maxY = Math.Max(maxY, r.Bottom); }
        foreach (var m in _monitors) Inc(m.Bounds);
        if (mon != null)
        {
            // keep room for the iPad on every side so dragging around doesn't rescale the view
            double pad = Math.Max(mon.Bounds.Width, mon.Bounds.Height) * 0.5;
            Inc(new RectD(minX - pad, minY - pad * 0.5, (maxX - minX) + 2 * pad, (maxY - minY) + pad));
        }
        if (minX == double.MaxValue) return (1, 0, 0);
        float m2 = S(10);
        float scale = (float)Math.Min((Width - 2 * m2) / (maxX - minX), (Height - 2 * m2) / (maxY - minY));
        float ox = (float)(Width / 2f - (minX + maxX) / 2 * scale);
        float oy = (float)(Height / 2f - (minY + maxY) / 2 * scale);
        return (scale, ox, oy);
    }

    private static RectangleF ToView(RectD r, (float s, float ox, float oy) t) =>
        new((float)(r.X * t.s + t.ox), (float)(r.Y * t.s + t.oy), (float)(r.Width * t.s), (float)(r.Height * t.s));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var t = Transform();
        var font = Theme.Body(8.5f);

        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            var r = ToView(m.Bounds, t);
            r.Inflate(-S(1.5f), -S(1.5f));
            bool attached = m.Device == _device;
            using var path = Theme.RoundRect(r, S(5));
            using (var b = new SolidBrush(Theme.Control)) g.FillPath(b, path);
            using (var p = new Pen(attached ? Theme.Text : Theme.CardBorder, S(attached ? 1.5f : 1f))) g.DrawPath(p, path);
            TextRenderer.DrawText(g, m.Label(i), font, Rectangle.Round(r), Theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        var mon = Monitor(_device);
        if (mon == null) return;
        var world = IpadWorldRect(mon, _side, _offset);
        _ipadView = ToView(world, t);
        if (_dragging) _ipadView.Location = new PointF(_lastMouse.X - _dragDelta.X, _lastMouse.Y - _dragDelta.Y);
        var ip = _ipadView;
        ip.Inflate(-S(1.5f), -S(1.5f));

        using (var path = Theme.RoundRect(ip, S(7)))
        {
            var fill = Theme.Blend(Theme.Card, Theme.Accent, _onIpad ? 0.35 : 0.18);
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            using var p = new Pen(Theme.Accent, S(_dragging ? 2.2f : 1.6f));
            g.DrawPath(p, path);
        }
        // crossing segment highlight
        if (!_dragging)
        {
            var layout = ScreenLayout.FromArrangement(mon.Bounds, mon.PhysicalMm, _side, _offset, _model, _portrait);
            var (s0, s1) = layout.CrossingSegment;
            float a0 = (float)(s0 * t.scale), a1 = (float)(s1 * t.scale);
            using var hp = new Pen(Theme.Accent, S(3)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            var mb = ToView(mon.Bounds, t);
            switch (_side)
            {
                case EdgeSide.Right: g.DrawLine(hp, mb.Right - S(3), a0 + t.oy, mb.Right - S(3), a1 + t.oy); break;
                case EdgeSide.Left: g.DrawLine(hp, mb.Left + S(3), a0 + t.oy, mb.Left + S(3), a1 + t.oy); break;
                case EdgeSide.Bottom: g.DrawLine(hp, a0 + t.ox, mb.Bottom - S(3), a1 + t.ox, mb.Bottom - S(3)); break;
                default: g.DrawLine(hp, a0 + t.ox, mb.Top + S(3), a1 + t.ox, mb.Top + S(3)); break;
            }
        }

        TextRenderer.DrawText(g, "iPad", Theme.Semibold(9f), Rectangle.Round(ip), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private PointF _lastMouse;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _lastMouse = e.Location;
        Cursor = _dragging || _ipadView.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
        if (_dragging) Invalidate();
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left && _ipadView.Contains(e.Location))
        {
            _dragging = true;
            _dragDelta = new PointF(e.X - _ipadView.X, e.Y - _ipadView.Y);
            _lastMouse = e.Location;
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            Snap(new PointF(e.X - _dragDelta.X + _ipadView.Width / 2, e.Y - _dragDelta.Y + _ipadView.Height / 2));
            Invalidate();
            if (_device != null) ArrangementChanged?.Invoke(_device, _side, _offset);
        }
        base.OnMouseUp(e);
    }

    /// <summary>Snap the dragged iPad (view-space centre) to the nearest free monitor edge.</summary>
    private void Snap(PointF viewCenter)
    {
        var t = Transform();
        double wx = (viewCenter.X - t.ox) / t.scale, wy = (viewCenter.Y - t.oy) / t.scale;
        double best = double.MaxValue;
        foreach (var m in _monitors)
        {
            foreach (EdgeSide side in Enum.GetValues<EdgeSide>())
            {
                if (EdgeBlocked(m, side)) continue;
                var probe = IpadWorldRect(m, side, 0);
                double along = side.IsHorizontalNeighbour() ? probe.Height : probe.Width;
                double monStart = side.IsHorizontalNeighbour() ? m.Bounds.Top : m.Bounds.Left;
                double monLen = side.IsHorizontalNeighbour() ? m.Bounds.Height : m.Bounds.Width;
                double centerAlong = side.IsHorizontalNeighbour() ? wy : wx;
                double minOverlap = Math.Min(along, monLen) * 0.25;
                double start = Math.Clamp(centerAlong - along / 2, monStart - along + minOverlap, monStart + monLen - minOverlap);
                double offset = (start - monStart) / monLen;
                var r = IpadWorldRect(m, side, offset);
                double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                double d = (cx - wx) * (cx - wx) + (cy - wy) * (cy - wy);
                if (d < best) { best = d; _device = m.Device; _side = side; _offset = offset; }
            }
        }
    }

    /// <summary>An edge touching another monitor can't host the iPad.</summary>
    private bool EdgeBlocked(MonitorInfo m, EdgeSide side)
    {
        foreach (var o in _monitors)
        {
            if (o == m) continue;
            var a = m.Bounds; var b = o.Bounds;
            bool vOverlap = a.Top < b.Bottom && b.Top < a.Bottom;
            bool hOverlap = a.Left < b.Right && b.Left < a.Right;
            switch (side)
            {
                case EdgeSide.Right when vOverlap && Math.Abs(b.Left - a.Right) < 2: return true;
                case EdgeSide.Left when vOverlap && Math.Abs(b.Right - a.Left) < 2: return true;
                case EdgeSide.Bottom when hOverlap && Math.Abs(b.Top - a.Bottom) < 2: return true;
                case EdgeSide.Top when hOverlap && Math.Abs(b.Bottom - a.Top) < 2: return true;
            }
        }
        return false;
    }
}
