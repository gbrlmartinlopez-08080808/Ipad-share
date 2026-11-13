using System.Runtime.InteropServices;

namespace IpadShare.App.Native;

/// <summary>Win32 interop. Struct layouts follow the Windows SDK headers for both x64 and ARM64.</summary>
internal static class NativeMethods
{
    // ---------------- messages ----------------
    public const int WM_DESTROY = 0x0002;
    public const int WM_PAINT = 0x000F;
    public const int WM_CLOSE = 0x0010;
    public const int WM_QUIT = 0x0012;
    public const int WM_ERASEBKGND = 0x0014;
    public const int WM_SETCURSOR = 0x0020;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    public const int WM_INPUT = 0x00FF;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_APP = 0x8000;
    public const int MA_NOACTIVATE = 3;
    public const int HTCLIENT = 1;

    // ---------------- hooks ----------------
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const uint LLKHF_EXTENDED = 0x01;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_UP = 0x80;
    public const uint LLMHF_INJECTED = 0x01;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---------------- message loop ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    public const uint PM_REMOVE = 0x0001;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;
    public const uint INFINITE = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[]? pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---------------- windows ----------------
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOOWNERZORDER = 0x0200;
    public const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    // ---------------- cursor ----------------
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    // ---------------- raw input ----------------
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_PREPARSEDDATA = 0x20000005;
    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RIDI_DEVICEINFO = 0x2000000b;
    public const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_DEVNOTIFY = 0x00002000;
    public const ushort MOUSE_MOVE_ABSOLUTE = 0x01;
    public const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001, RI_MOUSE_LEFT_BUTTON_UP = 0x0002,
        RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004, RI_MOUSE_RIGHT_BUTTON_UP = 0x0008,
        RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010, RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020,
        RI_MOUSE_BUTTON_4_DOWN = 0x0040, RI_MOUSE_BUTTON_4_UP = 0x0080,
        RI_MOUSE_BUTTON_5_DOWN = 0x0100, RI_MOUSE_BUTTON_5_UP = 0x0200,
        RI_MOUSE_WHEEL = 0x0400, RI_MOUSE_HWHEEL = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    /// <summary>RID_DEVICE_INFO with the HID member of the union (largest member is 24 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public RID_DEVICE_INFO_HID hid;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    // ---------------- hid.dll ----------------
    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public enum HIDP_REPORT_TYPE { Input = 0, Output = 1, Feature = 2 }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>HIDP_VALUE_CAPS (72 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        [FieldOffset(56)] public ushort UsageMin;   // NotRange.Usage when IsRange == 0
        [FieldOffset(58)] public ushort UsageMax;
    }

    /// <summary>HIDP_BUTTON_CAPS (72 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HIDP_BUTTON_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
    }

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);
    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(HIDP_REPORT_TYPE reportType, [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);
    [DllImport("hid.dll")]
    public static extern int HidP_GetButtonCaps(HIDP_REPORT_TYPE reportType, [Out] HIDP_BUTTON_CAPS[] buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);
    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(HIDP_REPORT_TYPE reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, IntPtr report, uint reportLength);
    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(HIDP_REPORT_TYPE reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsedData, IntPtr report, uint reportLength);

    // ---------------- monitors / DPI ----------------
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint MONITOR_DEFAULTTONULL = 0;
    public const int MDT_EFFECTIVE_DPI = 0, MDT_RAW_DPI = 2;

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    // ---------------- desktop / session ----------------
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, [Out] char[] pvInfo, uint nLength, out uint lpnLengthNeeded);
    public const int UOI_NAME = 2;
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetThreadDesktop(uint dwThreadId);
    public const uint DESKTOP_READOBJECTS = 0x0001;

    // ---------------- timers ----------------
    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint uPeriod);

    // ---------------- DWM ----------------
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    // ---------------- misc ----------------
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    public static ushort LoWord(IntPtr v) => (ushort)((long)v & 0xFFFF);
    public static short HiWordSigned(uint v) => (short)(v >> 16);
}
