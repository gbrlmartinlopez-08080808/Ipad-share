using IpadShare.Core.Hid;

namespace IpadShare.Core.Input;

public enum KeyboardStyle
{
    /// <summary>Ctrl→⌘, Alt→⌥, Win→🌐 plus Windows-habit translations (Ctrl+← = word jump, Home = line start...).</summary>
    Windows,
    /// <summary>Physical mapping: Win→⌘, Alt→⌥, Ctrl→⌃. No translations.</summary>
    Mac,
}

public sealed class KeyTranslatorOptions
{
    public KeyboardStyle Style { get; set; } = KeyboardStyle.Windows;
    /// <summary>Swap the ISO keys ` (left of 1) and &lt; (left of Z). Some iPadOS layouts swap them for non-Apple keyboards.</summary>
    public bool SwapIsoKeys { get; set; }
    /// <summary>Windows style: tapping Win alone opens Spotlight (⌘Space).</summary>
    public bool WinTapOpensSpotlight { get; set; } = true;
}

/// <summary>A key chord sent to the iPad: modifiers + one key.</summary>
public readonly record struct Chord(Mods Mods, byte Key)
{
    public override string ToString() => $"{Mods.Describe()}{Key:X2}";
}

/// <summary>
/// Converts physical key presses (HID usages by position) into the keyboard reports the iPad
/// should receive. Keeps per-key translations stable while a key is held, so modifier changes
/// never corrupt a held key, and emits modifier-only reports before chords so iPadOS always
/// recognises shortcuts.
/// </summary>
public sealed class KeyTranslator
{
    private readonly KeyTranslatorOptions _opt;

    // Physical state
    private Mods _physMods;                     // physical modifiers (LGui = Win key)
    private readonly List<HeldKey> _held = new(); // non-modifier keys in press order

    // Deferred Win (Globe) handling
    private bool _winUsedInChord;               // Win+<key> or Win+click happened during this hold
    private bool _globeLatched;                 // a 🌐 chord happened: keep 🌐 held until Win is released
    private bool _altTabActive;                 // Alt+Tab pressed: Alt acts as ⌘ until released

    private KeyboardReport _lastSent;

    private readonly record struct HeldKey(byte Physical, byte Output, Mods Mods);

    public KeyTranslator(KeyTranslatorOptions? options = null) => _opt = options ?? new KeyTranslatorOptions();

    public KeyTranslatorOptions Options => _opt;
    public KeyboardReport Current => _lastSent;
    public Mods PhysicalModifiers => _physMods;
    public bool AnyKeyDown => _physMods != Mods.None || _held.Count > 0;

    /// <summary>Handles a key press. Repeated presses of an already-held key are ignored (the iPad does auto-repeat).</summary>
    public void KeyDown(byte usage, List<KeyboardReport> output)
    {
        usage = ApplyIsoSwap(usage);
        if (Keys.IsModifier(usage))
        {
            var bit = ModsExtensions.FromModifierUsage(usage);
            if ((_physMods & bit) != 0) return; // auto-repeat
            _physMods |= bit;
            if (IsWin(bit)) { _winUsedInChord = false; _globeLatched = false; }
            EmitState(output, preferModsFirst: false);
            return;
        }

        foreach (var h in _held) if (h.Physical == usage) return; // auto-repeat

        var chord = Translate(usage, _physMods);
        if (_physMods.HasGui() && _opt.Style == KeyboardStyle.Windows)
        {
            _winUsedInChord = true;
            if ((chord.Mods & Mods.Globe) != 0) _globeLatched = true;
        }
        if (usage == Keys.Tab && _opt.Style == KeyboardStyle.Windows && IsAltOnly(_physMods)) _altTabActive = true;

        if (_held.Count >= KeyboardReport.MaxKeys) _held.RemoveAt(0); // rollover: drop the oldest
        _held.Add(new HeldKey(usage, chord.Key, chord.Mods));
        EmitState(output, preferModsFirst: true);
    }

    public void KeyUp(byte usage, List<KeyboardReport> output)
    {
        usage = ApplyIsoSwap(usage);
        if (Keys.IsModifier(usage))
        {
            var bit = ModsExtensions.FromModifierUsage(usage);
            if ((_physMods & bit) == 0) return;
            bool wasWinTap = IsWin(bit) && !_winUsedInChord && _held.Count == 0
                             && _opt.Style == KeyboardStyle.Windows && _opt.WinTapOpensSpotlight
                             && (_physMods & ~bit) == Mods.None;
            _physMods &= ~bit;
            if (!_physMods.HasAlt()) _altTabActive = false;
            if (!_physMods.HasGui()) { _winUsedInChord = false; _globeLatched = false; }
            EmitState(output, preferModsFirst: false);
            if (wasWinTap) Tap(new Chord(Mods.Cmd, Keys.Space), output);
            return;
        }

        int idx = _held.FindIndex(h => h.Physical == usage);
        if (idx < 0) return;
        _held.RemoveAt(idx);
        EmitState(output, preferModsFirst: false);
    }

    /// <summary>
    /// Call before a mouse click/scroll on the iPad so held modifiers apply to it
    /// (Shift-click, ⌘-click). Also marks Win as used so a Win+click doesn't open Spotlight.
    /// </summary>
    public void OnPointerAction(List<KeyboardReport> output)
    {
        if (_physMods.HasGui()) _winUsedInChord = true;
        EmitState(output, preferModsFirst: false);
    }

    /// <summary>Sends a full press + release of a chord, restoring the current state afterwards.</summary>
    public void Tap(Chord chord, List<KeyboardReport> output)
    {
        var baseMods = BaseMods();
        Send(new KeyboardReport(chord.Mods, ReadOnlySpan<byte>.Empty), output);
        Send(new KeyboardReport(chord.Mods, new[] { chord.Key }), output);
        Send(new KeyboardReport(chord.Mods, ReadOnlySpan<byte>.Empty), output);
        Send(new KeyboardReport(baseMods, HeldOutputs()), output);
    }

    /// <summary>Releases everything on the iPad and forgets all state.</summary>
    public void ReleaseAll(List<KeyboardReport> output)
    {
        _physMods = Mods.None;
        _held.Clear();
        _winUsedInChord = false;
        _globeLatched = false;
        _altTabActive = false;
        Send(KeyboardReport.Empty, output);
    }

    /// <summary>Forget state without emitting (used when the iPad link was reset).</summary>
    public void Reset()
    {
        _physMods = Mods.None;
        _held.Clear();
        _winUsedInChord = false;
        _globeLatched = false;
        _altTabActive = false;
        _lastSent = KeyboardReport.Empty;
    }

    // ------------------------------------------------------------------

    private void EmitState(List<KeyboardReport> output, bool preferModsFirst)
    {
        Mods mods = _held.Count > 0 ? _held[^1].Mods : BaseMods();
        var keys = HeldOutputs();
        var target = new KeyboardReport(mods, keys);
        if (target.Equals(_lastSent)) return;

        if (preferModsFirst && mods != _lastSent.Modifiers)
        {
            // 1) change modifiers while keeping only keys that were already down,
            // 2) then press the new key. iPadOS sees a clean modifier transition.
            var stillDown = new List<byte>();
            for (int i = 0; i < KeyboardReport.MaxKeys; i++)
            {
                byte k = _lastSent.Key(i);
                if (k != 0 && Array.IndexOf(keys, k) >= 0 && k != _held[^1].Output) stillDown.Add(k);
            }
            Send(new KeyboardReport(mods, stillDown.ToArray()), output);
        }
        else if (!preferModsFirst && mods != _lastSent.Modifiers && _lastSent.KeyCount > target.KeyCount)
        {
            // Releasing a translated key: lift the key before restoring modifiers
            // (so ⌥← doesn't briefly become ⌘←).
            Send(new KeyboardReport(_lastSent.Modifiers, keys), output);
        }
        Send(target, output);
    }

    private void Send(KeyboardReport r, List<KeyboardReport> output)
    {
        if (r.Equals(_lastSent)) return;
        output.Add(r);
        _lastSent = r;
    }

    private byte[] HeldOutputs()
    {
        var keys = new byte[_held.Count];
        for (int i = 0; i < _held.Count; i++) keys[i] = _held[i].Output;
        return keys;
    }

    /// <summary>Modifiers sent while no translated key is held.</summary>
    private Mods BaseMods()
    {
        var p = _physMods;
        if (_opt.Style == KeyboardStyle.Mac) return p & ~Mods.Globe;

        Mods m = Mods.None;
        if ((p & Mods.LShift) != 0) m |= Mods.LShift;
        if ((p & Mods.RShift) != 0) m |= Mods.RShift;
        if ((p & Mods.LCtrl) != 0) m |= Mods.LGui;   // Ctrl → ⌘
        if ((p & Mods.RCtrl) != 0) m |= Mods.RGui;
        if (_altTabActive)
        {
            if (p.HasAlt()) m |= Mods.LGui;            // Alt held after Alt+Tab → ⌘ (keeps switcher open)
        }
        else
        {
            if ((p & Mods.LAlt) != 0) m |= Mods.LAlt;  // Alt → ⌥
            if ((p & Mods.RAlt) != 0) m |= Mods.RAlt;  // AltGr → right ⌥
        }
        // Win → 🌐, but only after a 🌐 chord: a lone 🌐 press/release would make
        // iPadOS switch keyboard language / open the emoji picker.
        if (p.HasGui() && _globeLatched) m |= Mods.Globe;
        return m;
    }

    /// <summary>Translate a key pressed with the given physical modifiers.</summary>
    internal Chord Translate(byte key, Mods phys)
    {
        if (_opt.Style == KeyboardStyle.Mac) return new Chord(phys & ~Mods.Globe, key);

        bool ctrl = phys.HasCtrl(), alt = phys.HasAlt(), shift = phys.HasShift(), win = phys.HasGui();
        Mods sh = shift ? Mods.LShift : Mods.None;

        // ---- Win (system) shortcuts ----
        if (win && !ctrl && !alt)
        {
            if (shift && key == Keys.S) return new Chord(Mods.Cmd | Mods.LShift, Keys.D4); // Snip → screenshot+markup
            switch (key)
            {
                case Keys.D: return new Chord(Mods.Cmd, Keys.H);                       // show desktop → Home
                case Keys.Tab: return new Chord(Mods.Globe, Keys.Up);                  // Task View → App Switcher
                case Keys.A: return new Chord(Mods.Globe, Keys.C);                     // Quick Settings → Control Center
                case Keys.S: case Keys.Q: return new Chord(Mods.Cmd, Keys.Space);       // Search → Spotlight
                case 0x6E /* F23: Copilot key (Win+Shift+F23) */: return new Chord(Mods.Cmd, Keys.Space);
                case Keys.D3 when shift: return new Chord(Mods.Cmd | Mods.LShift, Keys.D3);
            }
            return new Chord(Mods.Globe | sh, key);                                   // Win+X → 🌐X
        }

        // ---- Alt shortcuts ----
        if (alt && !ctrl && !win)
        {
            switch (key)
            {
                case Keys.Tab: return new Chord(Mods.Cmd | sh, Keys.Tab);              // Alt+Tab → ⌘Tab
                case Keys.Left when !shift: return new Chord(Mods.Cmd, Keys.LeftBracket);   // back
                case Keys.Right when !shift: return new Chord(Mods.Cmd, Keys.RightBracket); // forward
            }
        }

        // ---- Ctrl text navigation (word-wise on Windows = ⌥ on iPad) ----
        if (ctrl && !alt && !win)
        {
            switch (key)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Backspace: case Keys.DeleteForward:
                    return new Chord(Mods.Option | sh, key);
                case Keys.Home: return new Chord(Mods.Cmd | sh, Keys.Up);                 // document start
                case Keys.End: return new Chord(Mods.Cmd | sh, Keys.Down);                // document end
                case Keys.Tab: return new Chord(Mods.Ctrl | sh, Keys.Tab);               // next/prev tab (⌃Tab)
                case Keys.Y when !shift: return new Chord(Mods.Cmd | Mods.LShift, Keys.Z); // redo
                case Keys.Insert when !shift: return new Chord(Mods.Cmd, Keys.C);         // legacy copy
                case Keys.F5: return new Chord(Mods.Cmd, Keys.R);
            }
        }

        // ---- Plain navigation / function keys ----
        if (!ctrl && !alt && !win)
        {
            switch (key)
            {
                case Keys.Home: return new Chord(Mods.Cmd | sh, Keys.Left);               // line start
                case Keys.End: return new Chord(Mods.Cmd | sh, Keys.Right);               // line end
                case Keys.F5: return new Chord(Mods.Cmd, Keys.R);                         // reload
                case Keys.PrintScreen: return new Chord(Mods.Cmd | Mods.LShift, Keys.D3); // screenshot
                case Keys.Insert when shift: return new Chord(Mods.Cmd, Keys.V);          // legacy paste
                case Keys.DeleteForward when shift: return new Chord(Mods.Cmd, Keys.X);   // legacy cut
            }
        }

        // ---- Default: base modifier mapping ----
        Mods m = Mods.None;
        if ((phys & Mods.LShift) != 0) m |= Mods.LShift;
        if ((phys & Mods.RShift) != 0) m |= Mods.RShift;
        if ((phys & Mods.LCtrl) != 0) m |= Mods.LGui;
        if ((phys & Mods.RCtrl) != 0) m |= Mods.RGui;
        if ((phys & Mods.LAlt) != 0) m |= Mods.LAlt;
        if ((phys & Mods.RAlt) != 0) m |= Mods.RAlt;
        if (win) m |= Mods.Globe;
        return new Chord(m, key);
    }

    private byte ApplyIsoSwap(byte usage)
    {
        if (!_opt.SwapIsoKeys) return usage;
        return usage switch
        {
            Keys.Grave => Keys.NonUsBackslash,
            Keys.NonUsBackslash => Keys.Grave,
            _ => usage,
        };
    }

    private static bool IsWin(Mods bit) => (bit & (Mods.LGui | Mods.RGui)) != 0;
    private static bool IsAltOnly(Mods m) => m.HasAlt() && !m.HasCtrl() && !m.HasGui();
}
