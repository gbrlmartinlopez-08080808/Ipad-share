using System.Buffers.Binary;

namespace IpadShare.Core.Hid;

/// <summary>Keyboard input report (ID 1): modifiers, Globe bit, 6-key array.</summary>
public readonly struct KeyboardReport : IEquatable<KeyboardReport>
{
    public const int MaxKeys = 6;
    public readonly Mods Modifiers;
    private readonly ulong _keys; // up to 6 usages packed little-endian

    public KeyboardReport(Mods modifiers, ReadOnlySpan<byte> keys)
    {
        Modifiers = modifiers;
        ulong packed = 0;
        int n = 0;
        foreach (var k in keys)
        {
            if (k == 0) continue;
            if (n == MaxKeys) break;
            packed |= (ulong)k << (8 * n++);
        }
        _keys = packed;
    }

    public static KeyboardReport Empty => default;

    public int KeyCount
    {
        get { int n = 0; for (int i = 0; i < MaxKeys; i++) if (Key(i) != 0) n++; return n; }
    }

    public byte Key(int index) => (byte)(_keys >> (8 * index));

    public bool Contains(byte usage)
    {
        for (int i = 0; i < MaxKeys; i++) if (Key(i) == usage) return true;
        return false;
    }

    public void WriteTo(Span<byte> dst)
    {
        if (dst.Length < ReportMap.KeyboardReportLength) throw new ArgumentException("buffer too small");
        dst[0] = (byte)((ushort)Modifiers & 0xFF);
        dst[1] = (byte)(((Modifiers & Mods.Globe) != 0) ? 0x01 : 0x00);
        for (int i = 0; i < MaxKeys; i++) dst[2 + i] = Key(i);
    }

    public byte[] ToBytes() { var b = new byte[ReportMap.KeyboardReportLength]; WriteTo(b); return b; }

    public bool Equals(KeyboardReport o) => Modifiers == o.Modifiers && _keys == o._keys;
    public override bool Equals(object? obj) => obj is KeyboardReport k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(Modifiers, _keys);
    public override string ToString()
    {
        var keys = new List<string>();
        for (int i = 0; i < MaxKeys; i++) if (Key(i) != 0) keys.Add($"0x{Key(i):X2}");
        return $"{Modifiers.Describe()}[{string.Join(",", keys)}]";
    }
}

/// <summary>Consumer control report (ID 2): one 16-bit usage, 0 = released.</summary>
public static class ConsumerReport
{
    public static byte[] ToBytes(ushort usage)
    {
        var b = new byte[ReportMap.ConsumerReportLength];
        BinaryPrimitives.WriteUInt16LittleEndian(b, usage);
        return b;
    }
}

[Flags]
public enum MouseButtons : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4,
    Back = 8,
    Forward = 16,
}

/// <summary>Mouse input report (ID 3).</summary>
public readonly record struct MouseReport(MouseButtons Buttons, short Dx, short Dy, short Wheel, short Pan)
{
    public const short Limit = 32767;

    public void WriteTo(Span<byte> dst)
    {
        if (dst.Length < ReportMap.MouseReportLength) throw new ArgumentException("buffer too small");
        dst[0] = (byte)Buttons;
        BinaryPrimitives.WriteInt16LittleEndian(dst[1..], Dx);
        BinaryPrimitives.WriteInt16LittleEndian(dst[3..], Dy);
        BinaryPrimitives.WriteInt16LittleEndian(dst[5..], Wheel);
        BinaryPrimitives.WriteInt16LittleEndian(dst[7..], Pan);
    }

    public byte[] ToBytes() { var b = new byte[ReportMap.MouseReportLength]; WriteTo(b); return b; }
}

/// <summary>One finger in a trackpad report, already in virtual-trackpad logical units.</summary>
public readonly record struct TouchContact(byte Id, bool Tip, bool Confidence, ushort X, ushort Y);

/// <summary>Trackpad input report (ID 5).</summary>
public sealed class TouchpadReport
{
    public readonly TouchContact[] Contacts;
    public readonly ushort ScanTime;   // 100 µs units, wrapping
    public readonly bool Button;

    public TouchpadReport(IReadOnlyList<TouchContact> contacts, ushort scanTime, bool button)
    {
        if (contacts.Count > ReportMap.TouchContacts)
            throw new ArgumentException($"at most {ReportMap.TouchContacts} contacts");
        Contacts = contacts.ToArray();
        ScanTime = scanTime;
        Button = button;
    }

    public void WriteTo(Span<byte> dst)
    {
        if (dst.Length < ReportMap.TouchpadReportLength) throw new ArgumentException("buffer too small");
        dst[..ReportMap.TouchpadReportLength].Clear();
        for (int i = 0; i < Contacts.Length; i++)
        {
            var c = Contacts[i];
            int o = i * 5;
            // bits0..5 contact id, bit6 confidence, bit7 tip (descriptor order)
            dst[o] = (byte)((c.Id & 0x3F) | (c.Confidence ? 0x40 : 0) | (c.Tip ? 0x80 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(dst[(o + 1)..], Math.Min(c.X, (ushort)ReportMap.TouchLogicalMax));
            BinaryPrimitives.WriteUInt16LittleEndian(dst[(o + 3)..], Math.Min(c.Y, (ushort)ReportMap.TouchLogicalMax));
        }
        int t = ReportMap.TouchContacts * 5;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[t..], ScanTime);
        dst[t + 2] = (byte)Contacts.Length;
        dst[t + 3] = (byte)(Button ? 1 : 0);
    }

    public byte[] ToBytes() { var b = new byte[ReportMap.TouchpadReportLength]; WriteTo(b); return b; }
}
