using IpadShare.Core.Hid;

namespace IpadShare.Core.Input;

/// <summary>Result of mapping one Windows key event.</summary>
public enum KeyMapKind
{
    /// <summary>Unknown key: forward nothing.</summary>
    Unmapped,
    /// <summary>An event Windows synthesizes that must be ignored (AltGr's fake Ctrl, NumLock fake Shifts).</summary>
    Ignore,
    /// <summary>A keyboard-page usage.</summary>
    Keyboard,
    /// <summary>A consumer-page usage (media/volume).</summary>
    Consumer,
}

public readonly record struct KeyMapping(KeyMapKind Kind, ushort Usage)
{
    public static readonly KeyMapping Unmapped = new(KeyMapKind.Unmapped, 0);
    public static readonly KeyMapping Ignore = new(KeyMapKind.Ignore, 0);
    public static KeyMapping Key(byte usage) => new(KeyMapKind.Keyboard, usage);
    public static KeyMapping Media(ushort usage) => new(KeyMapKind.Consumer, usage);
}

/// <summary>
/// Maps Windows low-level keyboard events (scan code set 1 + extended flag + virtual key) to
/// HID usages by PHYSICAL position. The iPad's own hardware-keyboard layout then decides
/// which character each key produces, exactly as with any external keyboard.
/// </summary>
public static class ScanCodeMap
{
    private static readonly byte[] s_normal = new byte[256];
    private static readonly byte[] s_extended = new byte[256];

    // Windows virtual-key codes used for special cases.
    public const int VK_PAUSE = 0x13, VK_SNAPSHOT = 0x2C, VK_NUMLOCK = 0x90,
        VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF,
        VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_STOP = 0xB2,
        VK_MEDIA_PLAY_PAUSE = 0xB3, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;

    static ScanCodeMap()
    {
        void N(int sc, byte u) => s_normal[sc] = u;
        void E(int sc, byte u) => s_extended[sc] = u;

        N(0x01, Keys.Escape);
        for (int i = 0; i < 10; i++) N(0x02 + i, (byte)(Keys.D1 + i)); // 1..9,0
        N(0x0C, Keys.Minus); N(0x0D, Keys.Equal); N(0x0E, Keys.Backspace); N(0x0F, Keys.Tab);
        byte[] qrow = { Keys.Q, Keys.W, Keys.E, Keys.R, Keys.T, Keys.Y, Keys.U, Keys.I, Keys.O, Keys.P };
        for (int i = 0; i < qrow.Length; i++) N(0x10 + i, qrow[i]);
        N(0x1A, Keys.LeftBracket); N(0x1B, Keys.RightBracket); N(0x1C, Keys.Enter); N(0x1D, Keys.LeftCtrl);
        byte[] arow = { Keys.A, Keys.S, Keys.D, Keys.F, Keys.G, Keys.H, Keys.J, Keys.K, Keys.L };
        for (int i = 0; i < arow.Length; i++) N(0x1E + i, arow[i]);
        N(0x27, Keys.Semicolon); N(0x28, Keys.Quote); N(0x29, Keys.Grave); N(0x2A, Keys.LeftShift);
        N(0x2B, Keys.Backslash);
        byte[] zrow = { Keys.Z, Keys.X, Keys.C, Keys.V, Keys.B, Keys.N, Keys.M };
        for (int i = 0; i < zrow.Length; i++) N(0x2C + i, zrow[i]);
        N(0x33, Keys.Comma); N(0x34, Keys.Period); N(0x35, Keys.Slash); N(0x36, Keys.RightShift);
        N(0x37, 0x55 /* KP * */); N(0x38, Keys.LeftAlt); N(0x39, Keys.Space); N(0x3A, Keys.CapsLock);
        for (int i = 0; i < 10; i++) N(0x3B + i, (byte)(Keys.F1 + i));  // F1..F10
        N(0x45, Keys.NumLock); N(0x46, Keys.ScrollLock);
        // Keypad (NumLock-independent physical keys)
        N(0x47, 0x5F); N(0x48, 0x60); N(0x49, 0x61); N(0x4A, 0x56);
        N(0x4B, 0x5C); N(0x4C, 0x5D); N(0x4D, 0x5E); N(0x4E, 0x57);
        N(0x4F, 0x59); N(0x50, 0x5A); N(0x51, 0x5B); N(0x52, 0x62); N(0x53, 0x63);
        N(0x54, Keys.PrintScreen);                // Alt+SysRq
        N(0x56, Keys.NonUsBackslash);             // ISO key left of Z (< > on Spanish)
        N(0x57, Keys.F11); N(0x58, Keys.F12);
        N(0x59, 0x67);                            // KP =
        for (int i = 0; i <= 10; i++) N(0x64 + i, (byte)(Keys.F13 + i)); // F13..F23
        N(0x70, 0x88); N(0x73, 0x87); N(0x76, Keys.F24); N(0x79, 0x8A); N(0x7B, 0x8B); N(0x7D, 0x89); N(0x7E, 0x85);

        E(0x1C, 0x58);                            // KP Enter
        E(0x1D, Keys.RightCtrl);
        E(0x35, 0x54);                            // KP /
        E(0x37, Keys.PrintScreen);
        E(0x38, Keys.RightAlt);                   // AltGr
        E(0x45, Keys.NumLock);
        E(0x46, Keys.Pause);                      // Ctrl+Break
        E(0x47, Keys.Home); E(0x48, Keys.Up); E(0x49, Keys.PageUp);
        E(0x4B, Keys.Left); E(0x4D, Keys.Right);
        E(0x4F, Keys.End); E(0x50, Keys.Down); E(0x51, Keys.PageDown);
        E(0x52, Keys.Insert); E(0x53, Keys.DeleteForward);
        E(0x5B, Keys.LeftGui); E(0x5C, Keys.RightGui); E(0x5D, Keys.Application);
    }

    /// <param name="scanCode">KBDLLHOOKSTRUCT.scanCode (may exceed 0xFF for synthesized keys).</param>
    /// <param name="extended">LLKHF_EXTENDED.</param>
    /// <param name="vk">KBDLLHOOKSTRUCT.vkCode.</param>
    public static KeyMapping Map(int scanCode, bool extended, int vk)
    {
        // Media keys are identified by virtual key, independent of keyboard firmware.
        switch (vk)
        {
            case VK_VOLUME_MUTE: return KeyMapping.Media(ConsumerUsages.Mute);
            case VK_VOLUME_DOWN: return KeyMapping.Media(ConsumerUsages.VolumeDown);
            case VK_VOLUME_UP: return KeyMapping.Media(ConsumerUsages.VolumeUp);
            case VK_MEDIA_NEXT_TRACK: return KeyMapping.Media(ConsumerUsages.ScanNext);
            case VK_MEDIA_PREV_TRACK: return KeyMapping.Media(ConsumerUsages.ScanPrevious);
            case VK_MEDIA_STOP: return KeyMapping.Media(ConsumerUsages.Stop);
            case VK_MEDIA_PLAY_PAUSE: return KeyMapping.Media(ConsumerUsages.PlayPause);
            case VK_PAUSE: return KeyMapping.Key(Keys.Pause);
            case VK_NUMLOCK: return KeyMapping.Key(Keys.NumLock);
            case VK_SNAPSHOT: return KeyMapping.Key(Keys.PrintScreen);
        }

        // AltGr on non-US layouts makes Windows inject a fake Left Ctrl with scan code 0x21D.
        if (scanCode > 0xFF) return KeyMapping.Ignore;
        // With NumLock on, navigation keys are wrapped in fake (extended) Shift presses.
        if (extended && (scanCode == 0x2A || scanCode == 0x36)) return KeyMapping.Ignore;
        if (scanCode == 0) return KeyMapping.Unmapped;

        byte u = extended ? s_extended[scanCode] : s_normal[scanCode];
        if (u == 0 && extended) u = s_normal[scanCode]; // a few keyboards set E0 on plain keys
        return u == 0 ? KeyMapping.Unmapped : KeyMapping.Key(u);
    }
}
