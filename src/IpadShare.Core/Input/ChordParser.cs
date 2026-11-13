using IpadShare.Core.Hid;

namespace IpadShare.Core.Input;

/// <summary>Parses shortcut strings such as "Cmd+H", "Globe+Up", "Option+Cmd+D", "Cmd+Shift+3".</summary>
public static class ChordParser
{
    private static readonly Dictionary<string, byte> s_keys = BuildKeys();
    private static readonly Dictionary<byte, string> s_names = BuildNames();

    private static Dictionary<byte, string> BuildNames()
    {
        // canonical names: first spelling wins
        string[] canonical =
        {
            "Enter", "Escape", "Backspace", "ForwardDelete", "Tab", "Space", "Minus", "Equal",
            "LeftBracket", "RightBracket", "Comma", "Period", "Slash", "Up", "Down", "Left", "Right",
            "Home", "End", "PageUp", "PageDown",
        };
        var d = new Dictionary<byte, string>();
        foreach (var name in canonical) d[s_keys[name]] = name;
        foreach (var kv in s_keys) d.TryAdd(kv.Value, kv.Key);
        return d;
    }

    private static Dictionary<string, byte> BuildKeys()
    {
        var d = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 26; i++) d[((char)('A' + i)).ToString()] = (byte)(Keys.A + i);
        for (int i = 1; i <= 9; i++) d[i.ToString()] = (byte)(Keys.D1 + i - 1);
        d["0"] = Keys.D0;
        for (int i = 1; i <= 12; i++) d["F" + i] = (byte)(Keys.F1 + i - 1);
        d["Enter"] = d["Return"] = Keys.Enter;
        d["Esc"] = d["Escape"] = Keys.Escape;
        d["Backspace"] = d["Delete"] = Keys.Backspace;
        d["ForwardDelete"] = Keys.DeleteForward;
        d["Tab"] = Keys.Tab;
        d["Space"] = Keys.Space;
        d["Minus"] = d["-"] = Keys.Minus;
        d["Equal"] = d["Plus"] = d["="] = Keys.Equal;
        d["LeftBracket"] = d["["] = Keys.LeftBracket;
        d["RightBracket"] = d["]"] = Keys.RightBracket;
        d["Comma"] = Keys.Comma;
        d["Period"] = Keys.Period;
        d["Slash"] = Keys.Slash;
        d["Up"] = Keys.Up; d["Down"] = Keys.Down; d["Left"] = Keys.Left; d["Right"] = Keys.Right;
        d["Home"] = Keys.Home; d["End"] = Keys.End; d["PageUp"] = Keys.PageUp; d["PageDown"] = Keys.PageDown;
        return d;
    }

    public static bool TryParse(string? text, out Chord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (text.EndsWith("++")) parts = parts.Append("+").ToArray();
        Mods mods = Mods.None;
        byte key = 0;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "cmd": case "command": case "⌘": mods |= Mods.Cmd; continue;
                case "shift": case "⇧": mods |= Mods.LShift; continue;
                case "option": case "opt": case "alt": case "⌥": mods |= Mods.Option; continue;
                case "ctrl": case "control": case "⌃": mods |= Mods.Ctrl; continue;
                case "globe": case "fn": case "🌐": mods |= Mods.Globe; continue;
            }
            if (key != 0) return false;                     // two non-modifier keys
            if (!s_keys.TryGetValue(p, out key)) return false;
        }
        if (key == 0) return false;
        chord = new Chord(mods, key);
        return true;
    }

    public static string Format(Chord c)
    {
        var parts = new List<string>();
        if ((c.Mods & Mods.Globe) != 0) parts.Add("Globe");
        if (c.Mods.HasCtrl()) parts.Add("Ctrl");
        if (c.Mods.HasAlt()) parts.Add("Option");
        if (c.Mods.HasShift()) parts.Add("Shift");
        if (c.Mods.HasGui()) parts.Add("Cmd");
        var name = s_names.TryGetValue(c.Key, out var n) ? n : $"0x{c.Key:X2}";
        parts.Add(name);
        return string.Join("+", parts);
    }
}
