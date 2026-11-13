using IpadShare.Core.Hid;
using IpadShare.Core.Input;

namespace IpadShare.Core.Tests;

public class KeyboardTests
{
    private readonly List<KeyboardReport> _out = new();
    private KeyTranslator T(KeyboardStyle style = KeyboardStyle.Windows) =>
        new(new KeyTranslatorOptions { Style = style });

    private static KeyboardReport R(Mods m, params byte[] keys) => new(m, keys);

    [Theory]
    [InlineData(0x1E, false, 0x41, Keys.A)]
    [InlineData(0x10, false, 0x51, Keys.Q)]
    [InlineData(0x1C, false, 0x0D, Keys.Enter)]
    [InlineData(0x1C, true, 0x0D, 0x58)]               // keypad enter
    [InlineData(0x4B, true, 0x25, Keys.Left)]
    [InlineData(0x4B, false, 0x64, 0x5C)]              // keypad 4
    [InlineData(0x38, true, 0xA5, Keys.RightAlt)]      // AltGr
    [InlineData(0x5B, true, 0x5B, Keys.LeftGui)]
    [InlineData(0x56, false, 0xE2, Keys.NonUsBackslash)]
    [InlineData(0x57, false, 0x7A, Keys.F11)]
    [InlineData(0x6E, false, 0x86, 0x72)]              // F23 (Copilot key)
    [InlineData(0x45, false, 0x13, Keys.Pause)]        // Pause arrives as scan 0x45 + VK_PAUSE
    [InlineData(0x45, true, 0x90, Keys.NumLock)]
    public void Scan_codes_map_to_physical_usages(int sc, bool ext, int vk, int usage)
    {
        var m = ScanCodeMap.Map(sc, ext, vk);
        Assert.Equal(KeyMapKind.Keyboard, m.Kind);
        Assert.Equal(usage, m.Usage);
    }

    [Fact]
    public void AltGr_fake_ctrl_and_numlock_fake_shift_are_ignored()
    {
        Assert.Equal(KeyMapKind.Ignore, ScanCodeMap.Map(0x21D, false, 0xA2).Kind);
        Assert.Equal(KeyMapKind.Ignore, ScanCodeMap.Map(0x2A, true, 0xA0).Kind);
    }

    [Fact]
    public void Media_keys_map_to_consumer_usages()
    {
        var m = ScanCodeMap.Map(0x30, true, ScanCodeMap.VK_VOLUME_UP);
        Assert.Equal(KeyMapKind.Consumer, m.Kind);
        Assert.Equal(ConsumerUsages.VolumeUp, m.Usage);
    }

    [Fact]
    public void Ctrl_C_becomes_command_C()
    {
        var t = T();
        t.KeyDown(Keys.LeftCtrl, _out);
        t.KeyDown(Keys.C, _out);
        t.KeyUp(Keys.C, _out);
        t.KeyUp(Keys.LeftCtrl, _out);
        Assert.Equal(new[] { R(Mods.Cmd), R(Mods.Cmd, Keys.C), R(Mods.Cmd), R(Mods.None) }, _out);
    }

    [Fact]
    public void Ctrl_left_is_word_jump_without_command_leaking()
    {
        var t = T();
        t.KeyDown(Keys.LeftCtrl, _out);
        _out.Clear();
        t.KeyDown(Keys.Left, _out);
        Assert.Equal(new[] { R(Mods.Option), R(Mods.Option, Keys.Left) }, _out);
        _out.Clear();
        t.KeyUp(Keys.Left, _out);
        // key lifted before ⌘ comes back, so the iPad never sees ⌘←
        Assert.Equal(new[] { R(Mods.Option), R(Mods.Cmd) }, _out);
        Assert.DoesNotContain(_out, r => r.Modifiers == Mods.Cmd && r.Contains(Keys.Left));
    }

    [Fact]
    public void Shift_home_selects_to_line_start()
    {
        var t = T();
        t.KeyDown(Keys.LeftShift, _out);
        _out.Clear();
        t.KeyDown(Keys.Home, _out);
        Assert.Equal(R(Mods.Cmd | Mods.LShift, Keys.Left), _out[^1]);
    }

    [Fact]
    public void Win_tap_opens_spotlight_but_win_chords_do_not()
    {
        var t = T();
        t.KeyDown(Keys.LeftGui, _out);
        Assert.Empty(_out); // deferred: a lone 🌐 would switch keyboards on iPad
        t.KeyUp(Keys.LeftGui, _out);
        Assert.Contains(R(Mods.Cmd, Keys.Space), _out);
        Assert.Equal(KeyboardReport.Empty, _out[^1]);

        _out.Clear();
        t.KeyDown(Keys.LeftGui, _out);
        t.KeyDown(Keys.D, _out);
        t.KeyUp(Keys.D, _out);
        t.KeyUp(Keys.LeftGui, _out);
        Assert.Contains(R(Mods.Cmd, Keys.H), _out);                 // Win+D → Home
        Assert.DoesNotContain(_out, r => r.Contains(Keys.Space));
        Assert.DoesNotContain(_out, r => (r.Modifiers & Mods.Globe) != 0); // no stray 🌐 tap
    }

    [Fact]
    public void Win_plus_letter_uses_globe_and_keeps_it_held_until_release()
    {
        var t = T();
        t.KeyDown(Keys.LeftGui, _out);
        t.KeyDown(Keys.N, _out);
        Assert.Equal(R(Mods.Globe, Keys.N), _out[^1]);
        t.KeyUp(Keys.N, _out);
        Assert.Equal(R(Mods.Globe), _out[^1]);
        t.KeyUp(Keys.LeftGui, _out);
        Assert.Equal(KeyboardReport.Empty, _out[^1]);
        Assert.DoesNotContain(_out, r => r.Contains(Keys.Space));
    }

    [Fact]
    public void Win_click_does_not_open_spotlight()
    {
        var t = T();
        t.KeyDown(Keys.LeftGui, _out);
        t.OnPointerAction(_out);
        t.KeyUp(Keys.LeftGui, _out);
        Assert.DoesNotContain(_out, r => r.Contains(Keys.Space));
    }

    [Fact]
    public void Alt_tab_holds_command_until_alt_released()
    {
        var t = T();
        t.KeyDown(Keys.LeftAlt, _out);
        Assert.Equal(R(Mods.Option), _out[^1]);
        t.KeyDown(Keys.Tab, _out);
        Assert.Equal(R(Mods.Cmd, Keys.Tab), _out[^1]);
        t.KeyUp(Keys.Tab, _out);
        Assert.Equal(R(Mods.Cmd), _out[^1]);   // switcher stays open
        t.KeyDown(Keys.Tab, _out);
        Assert.Equal(R(Mods.Cmd, Keys.Tab), _out[^1]);
        t.KeyUp(Keys.Tab, _out);
        t.KeyUp(Keys.LeftAlt, _out);
        Assert.Equal(KeyboardReport.Empty, _out[^1]);
    }

    [Fact]
    public void AltGr_stays_right_option_for_international_characters()
    {
        var t = T();
        t.KeyDown(Keys.RightAlt, _out);
        t.KeyDown(Keys.D2, _out);
        Assert.Equal(R(Mods.RAlt, Keys.D2), _out[^1]);
    }

    [Fact]
    public void Ctrl_tab_stays_control_tab_for_browser_tabs()
    {
        var t = T();
        t.KeyDown(Keys.LeftCtrl, _out);
        t.KeyDown(Keys.Tab, _out);
        Assert.Equal(R(Mods.Ctrl, Keys.Tab), _out[^1]);
    }

    [Fact]
    public void Ctrl_y_is_redo()
    {
        var t = T();
        t.KeyDown(Keys.LeftCtrl, _out);
        t.KeyDown(Keys.Y, _out);
        Assert.Equal(R(Mods.Cmd | Mods.LShift, Keys.Z), _out[^1]);
    }

    [Fact]
    public void Auto_repeat_is_left_to_the_ipad()
    {
        var t = T();
        t.KeyDown(Keys.A, _out);
        t.KeyDown(Keys.A, _out);
        t.KeyDown(Keys.A, _out);
        Assert.Single(_out);
    }

    [Fact]
    public void Mac_style_is_physical()
    {
        var t = T(KeyboardStyle.Mac);
        t.KeyDown(Keys.LeftGui, _out);
        t.KeyDown(Keys.C, _out);
        Assert.Equal(R(Mods.Cmd, Keys.C), _out[^1]);
        t.KeyUp(Keys.C, _out);
        t.KeyUp(Keys.LeftGui, _out);
        t.KeyDown(Keys.LeftCtrl, _out);
        t.KeyDown(Keys.Left, _out);
        Assert.Equal(R(Mods.Ctrl, Keys.Left), _out[^1]);
    }

    [Fact]
    public void Release_all_clears_everything()
    {
        var t = T();
        t.KeyDown(Keys.LeftShift, _out);
        t.KeyDown(Keys.A, _out);
        t.ReleaseAll(_out);
        Assert.Equal(KeyboardReport.Empty, _out[^1]);
        Assert.False(t.AnyKeyDown);
    }

    [Fact]
    public void Iso_swap_option_swaps_the_two_iso_keys()
    {
        var t = new KeyTranslator(new KeyTranslatorOptions { SwapIsoKeys = true });
        t.KeyDown(Keys.Grave, _out);
        Assert.Equal(R(Mods.None, Keys.NonUsBackslash), _out[^1]);
    }

    [Fact]
    public void Seven_keys_roll_over_oldest()
    {
        var t = T();
        byte[] ks = { Keys.A, Keys.B, Keys.C, Keys.D, Keys.E, Keys.F, Keys.G };
        foreach (var k in ks) t.KeyDown(k, _out);
        Assert.Equal(6, _out[^1].KeyCount);
        Assert.False(_out[^1].Contains(Keys.A));
        Assert.True(_out[^1].Contains(Keys.G));
    }

    [Fact]
    public void Win_shift_s_is_screenshot_and_markup_not_search()
    {
        var t = T();
        t.KeyDown(Keys.LeftGui, _out);
        t.KeyDown(Keys.LeftShift, _out);
        t.KeyDown(Keys.S, _out);
        Assert.Equal(R(Mods.Cmd | Mods.LShift, Keys.D4), _out[^1]);
    }

    [Fact]
    public void Win_s_is_spotlight()
    {
        var t = T();
        t.KeyDown(Keys.LeftGui, _out);
        t.KeyDown(Keys.S, _out);
        Assert.Equal(R(Mods.Cmd, Keys.Space), _out[^1]);
    }
}
