using System.Runtime.InteropServices;
using IpadShare.App.App;
using static IpadShare.App.Native.NativeMethods;

namespace IpadShare.App.Input;

/// <summary>
/// Two Win32 windows owned by the input thread:
/// <list type="bullet">
/// <item><b>Capture</b>: an invisible (alpha 1/255) topmost window over the whole desktop while the iPad is
/// controlled. Its cursor is empty, so the Windows pointer disappears, and it absorbs touchpad gestures that
/// apps would otherwise read directly (DirectManipulation) and that hooks can't block.</item>
/// <item><b>Glow</b>: a thin click-through bar on the shared edge showing push-through progress.</item>
/// </list>
/// </summary>
internal sealed class Overlays : IDisposable
{
    private IntPtr _capture, _glow;
    private WndProc? _captureProc, _glowProc;
    private IntPtr _glowBrush;
    private uint _glowColor = 0xFF840A; // BGR of #0A84FF
    private double _glowLevel, _flashStart = -1;
    private bool _glowVisible;
    private RECT _glowRect;

    public bool CaptureVisible { get; private set; }

    public void Create()
    {
        IntPtr inst = GetModuleHandle(null);
        _captureProc = CaptureWndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_captureProc),
            hInstance = inst,
            lpszClassName = "iPadShareCapture",
        };
        RegisterClassEx(ref wc);
        _capture = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
            wc.lpszClassName, "iPad Share", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (_capture != IntPtr.Zero) SetLayeredWindowAttributes(_capture, 0, 1, LWA_ALPHA);
        else Log.Warn($"Capture window failed ({Marshal.GetLastWin32Error()})");

        _glowProc = GlowWndProc;
        var wg = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_glowProc),
            hInstance = inst,
            lpszClassName = "iPadShareGlow",
        };
        RegisterClassEx(ref wg);
        _glow = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
            wg.lpszClassName, "iPad Share edge", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (_glow != IntPtr.Zero) SetLayeredWindowAttributes(_glow, 0, 0, LWA_ALPHA);
        _glowBrush = CreateSolidBrush(_glowColor);
    }

    public void SetAccent(Color c)
    {
        uint bgr = (uint)(c.R | (c.G << 8) | (c.B << 16));
        if (bgr == _glowColor) return;
        _glowColor = bgr;
        if (_glowBrush != IntPtr.Zero) DeleteObject(_glowBrush);
        _glowBrush = CreateSolidBrush(bgr);
        if (_glow != IntPtr.Zero) InvalidateRect(_glow, IntPtr.Zero, true);
    }

    // ---------------- capture ----------------
    public void ShowCapture()
    {
        if (_capture == IntPtr.Zero) return;
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        SetWindowPos(_capture, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        CaptureVisible = true;
    }

    public void HideCapture()
    {
        if (_capture == IntPtr.Zero) return;
        ShowWindow(_capture, SW_HIDE);
        CaptureVisible = false;
    }

    private IntPtr CaptureWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                SetCursor(IntPtr.Zero);        // no pointer while the iPad is controlled
                return new IntPtr(1);
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);
            case WM_NCHITTEST:
                return new IntPtr(HTCLIENT);
            case WM_ERASEBKGND:
                return new IntPtr(1);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ---------------- glow ----------------
    /// <summary>Place the glow bar on a screen-edge segment (physical pixels).</summary>
    public void PlaceGlow(int x, int y, int w, int h)
    {
        _glowRect = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        if (_glow == IntPtr.Zero) return;
        SetWindowPos(_glow, HWND_TOPMOST, x, y, Math.Max(1, w), Math.Max(1, h), SWP_NOACTIVATE | (_glowVisible ? SWP_SHOWWINDOW : 0));
    }

    /// <summary>Show push progress 0…1 (0 hides).</summary>
    public void SetGlow(double progress)
    {
        _glowLevel = Math.Clamp(progress, 0, 1);
        if (_flashStart < 0) ApplyGlowAlpha(_glowLevel);
    }

    /// <summary>A short bright flash when the pointer crosses.</summary>
    public void Flash(double now) => _flashStart = now;

    /// <summary>Animate the flash; returns true while animating.</summary>
    public bool Animate(double now)
    {
        if (_flashStart < 0) return false;
        double t = (now - _flashStart) / 0.35;
        if (t >= 1)
        {
            _flashStart = -1;
            ApplyGlowAlpha(_glowLevel);
            return false;
        }
        ApplyGlowAlpha(1 - t);
        return true;
    }

    private byte _lastAlpha;

    private void ApplyGlowAlpha(double level)
    {
        if (_glow == IntPtr.Zero) return;
        byte alpha = (byte)Math.Round(Math.Clamp(level, 0, 1) * 230);
        if (alpha == _lastAlpha && (alpha == 0) == !_glowVisible) return;   // unchanged: no Win32 calls on the hot path
        _lastAlpha = alpha;
        if (alpha == 0)
        {
            if (_glowVisible) { ShowWindow(_glow, SW_HIDE); _glowVisible = false; }
            return;
        }
        SetLayeredWindowAttributes(_glow, 0, alpha, LWA_ALPHA);
        if (!_glowVisible)
        {
            SetWindowPos(_glow, HWND_TOPMOST, _glowRect.Left, _glowRect.Top,
                Math.Max(1, _glowRect.Right - _glowRect.Left), Math.Max(1, _glowRect.Bottom - _glowRect.Top),
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            ShowWindow(_glow, SW_SHOWNOACTIVATE);
            _glowVisible = true;
        }
    }

    private IntPtr GlowWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                var hdc = BeginPaint(hWnd, out var ps);
                GetClientRect(hWnd, out var rc);
                FillRect(hdc, ref rc, _glowBrush);
                EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_capture != IntPtr.Zero) DestroyWindow(_capture);
        if (_glow != IntPtr.Zero) DestroyWindow(_glow);
        if (_glowBrush != IntPtr.Zero) DeleteObject(_glowBrush);
        _capture = _glow = _glowBrush = IntPtr.Zero;
    }
}
