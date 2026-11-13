namespace IpadShare.Core.Hid;

/// <summary>HID usage pages and usages used by iPad Share (HID Usage Tables 1.4 + Apple vendor).</summary>
public static class HidUsages
{
    // Pages
    public const ushort PageGenericDesktop = 0x01;
    public const ushort PageKeyboard = 0x07;
    public const ushort PageLed = 0x08;
    public const ushort PageButton = 0x09;
    public const ushort PageConsumer = 0x0C;
    public const ushort PageDigitizer = 0x0D;
    /// <summary>Apple vendor "Top Case" page (kHIDPage_AppleVendorTopCase).</summary>
    public const ushort PageAppleVendorTopCase = 0xFF;

    // Generic desktop
    public const ushort GdPointer = 0x01;
    public const ushort GdMouse = 0x02;
    public const ushort GdKeyboard = 0x06;
    public const ushort GdX = 0x30;
    public const ushort GdY = 0x31;
    public const ushort GdWheel = 0x38;
    public const ushort GdResolutionMultiplier = 0x48;

    // Apple vendor top case
    /// <summary>kHIDUsage_AV_TopCase_KeyboardFn — the fn / 🌐 Globe key.</summary>
    public const ushort AppleTopCaseKeyboardFn = 0x03;

    // Consumer
    public const ushort ConsumerControl = 0x01;
    public const ushort ConsumerAcPan = 0x238;

    // Digitizer
    public const ushort DigTouchPad = 0x05;
    public const ushort DigFinger = 0x22;
    public const ushort DigTipSwitch = 0x42;
    public const ushort DigConfidence = 0x47;
    public const ushort DigContactIdentifier = 0x51;
    public const ushort DigContactCount = 0x54;
    public const ushort DigContactCountMaximum = 0x55;
    public const ushort DigScanTime = 0x56;
    public const ushort DigPadType = 0x59;
}

/// <summary>Consumer-page usages for media and system keys.</summary>
public static class ConsumerUsages
{
    public const ushort ScanNext = 0xB5;
    public const ushort ScanPrevious = 0xB6;
    public const ushort Stop = 0xB7;
    public const ushort PlayPause = 0xCD;
    public const ushort Mute = 0xE2;
    public const ushort VolumeUp = 0xE9;
    public const ushort VolumeDown = 0xEA;
    public const ushort BrightnessUp = 0x6F;
    public const ushort BrightnessDown = 0x70;
    /// <summary>AL Keyboard Layout — iOS toggles the on-screen keyboard.</summary>
    public const ushort KeyboardLayout = 0x1AE;
    public const ushort AcSearch = 0x221;
    public const ushort AcHome = 0x223;
}

/// <summary>Keyboard-page usages (subset used directly by name in the code).</summary>
public static class Keys
{
    public const byte None = 0x00;
    public const byte A = 0x04, B = 0x05, C = 0x06, D = 0x07, E = 0x08, F = 0x09, G = 0x0A, H = 0x0B,
        I = 0x0C, J = 0x0D, K = 0x0E, L = 0x0F, M = 0x10, N = 0x11, O = 0x12, P = 0x13, Q = 0x14,
        R = 0x15, S = 0x16, T = 0x17, U = 0x18, V = 0x19, W = 0x1A, X = 0x1B, Y = 0x1C, Z = 0x1D;
    public const byte D1 = 0x1E, D2 = 0x1F, D3 = 0x20, D4 = 0x21, D5 = 0x22, D6 = 0x23, D7 = 0x24,
        D8 = 0x25, D9 = 0x26, D0 = 0x27;
    public const byte Enter = 0x28, Escape = 0x29, Backspace = 0x2A, Tab = 0x2B, Space = 0x2C,
        Minus = 0x2D, Equal = 0x2E, LeftBracket = 0x2F, RightBracket = 0x30, Backslash = 0x31,
        NonUsHash = 0x32, Semicolon = 0x33, Quote = 0x34, Grave = 0x35, Comma = 0x36, Period = 0x37,
        Slash = 0x38, CapsLock = 0x39;
    public const byte F1 = 0x3A, F2 = 0x3B, F3 = 0x3C, F4 = 0x3D, F5 = 0x3E, F6 = 0x3F, F7 = 0x40,
        F8 = 0x41, F9 = 0x42, F10 = 0x43, F11 = 0x44, F12 = 0x45;
    public const byte PrintScreen = 0x46, ScrollLock = 0x47, Pause = 0x48, Insert = 0x49, Home = 0x4A,
        PageUp = 0x4B, DeleteForward = 0x4C, End = 0x4D, PageDown = 0x4E, Right = 0x4F, Left = 0x50,
        Down = 0x51, Up = 0x52, NumLock = 0x53;
    public const byte NonUsBackslash = 0x64, Application = 0x65;
    public const byte F13 = 0x68, F24 = 0x73;

    // Modifiers (usage IDs; bit = usage - 0xE0)
    public const byte LeftCtrl = 0xE0, LeftShift = 0xE1, LeftAlt = 0xE2, LeftGui = 0xE3,
        RightCtrl = 0xE4, RightShift = 0xE5, RightAlt = 0xE6, RightGui = 0xE7;

    public static bool IsModifier(byte usage) => usage >= 0xE0 && usage <= 0xE7;
}

/// <summary>Modifier bit mask as sent in byte 0 of the keyboard report (plus a Globe pseudo-bit).</summary>
[Flags]
public enum Mods : ushort
{
    None = 0,
    LCtrl = 1 << 0,
    LShift = 1 << 1,
    LAlt = 1 << 2,   // ⌥ Option
    LGui = 1 << 3,   // ⌘ Command
    RCtrl = 1 << 4,
    RShift = 1 << 5,
    RAlt = 1 << 6,
    RGui = 1 << 7,
    /// <summary>🌐 Globe (sent in byte 1 of the keyboard report).</summary>
    Globe = 1 << 8,

    Ctrl = LCtrl, Shift = LShift, Option = LAlt, Cmd = LGui,
}

public static class ModsExtensions
{
    public static bool HasShift(this Mods m) => (m & (Mods.LShift | Mods.RShift)) != 0;
    public static bool HasCtrl(this Mods m) => (m & (Mods.LCtrl | Mods.RCtrl)) != 0;
    public static bool HasAlt(this Mods m) => (m & (Mods.LAlt | Mods.RAlt)) != 0;
    public static bool HasGui(this Mods m) => (m & (Mods.LGui | Mods.RGui)) != 0;
    public static Mods FromModifierUsage(byte usage) => Keys.IsModifier(usage) ? (Mods)(1 << (usage - 0xE0)) : Mods.None;

    public static string Describe(this Mods m)
    {
        var parts = new List<string>();
        if (m.HasCtrl()) parts.Add("⌃");
        if (m.HasAlt()) parts.Add("⌥");
        if (m.HasShift()) parts.Add("⇧");
        if (m.HasGui()) parts.Add("⌘");
        if ((m & Mods.Globe) != 0) parts.Add("🌐");
        return string.Concat(parts);
    }
}
