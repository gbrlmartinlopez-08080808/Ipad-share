namespace IpadShare.Core.Hid;

/// <summary>
/// Minimal, correct HID report-descriptor writer. Every item is emitted with the
/// smallest legal data size, and signed items (logical/physical extents, unit
/// exponent) are encoded as two's complement. This keeps the report map well
/// under the 512-byte BLE attribute limit.
/// </summary>
public sealed class HidDescriptorBuilder
{
    private readonly List<byte> _bytes = new();
    private int _collectionDepth;

    public int Length => _bytes.Count;
    public int OpenCollections => _collectionDepth;

    public byte[] ToArray()
    {
        if (_collectionDepth != 0)
            throw new InvalidOperationException($"{_collectionDepth} collection(s) left open.");
        return _bytes.ToArray();
    }

    // ---- item types (HID 1.11, 6.2.2.2) ----
    private const int Main = 0, Global = 1, Local = 2;

    private HidDescriptorBuilder ItemUnsigned(int tag, int type, uint value)
    {
        if (value <= 0xFF) return Emit(tag, type, 1, value);
        if (value <= 0xFFFF) return Emit(tag, type, 2, value);
        return Emit(tag, type, 4, value);
    }

    private HidDescriptorBuilder ItemSigned(int tag, int type, int value)
    {
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue) return Emit(tag, type, 1, (uint)(value & 0xFF));
        if (value >= short.MinValue && value <= short.MaxValue) return Emit(tag, type, 2, (uint)(value & 0xFFFF));
        return Emit(tag, type, 4, unchecked((uint)value));
    }

    private HidDescriptorBuilder Emit(int tag, int type, int size, uint value)
    {
        int sizeCode = size switch { 0 => 0, 1 => 1, 2 => 2, 4 => 3, _ => throw new ArgumentOutOfRangeException(nameof(size)) };
        _bytes.Add((byte)((tag << 4) | (type << 2) | sizeCode));
        for (int i = 0; i < size; i++) _bytes.Add((byte)(value >> (8 * i)));
        return this;
    }

    private HidDescriptorBuilder EmitNoData(int tag, int type)
    {
        _bytes.Add((byte)((tag << 4) | (type << 2)));
        return this;
    }

    // ---- Main items ----
    public HidDescriptorBuilder Input(HidMain flags) => ItemUnsigned(0x8, Main, (uint)flags);
    public HidDescriptorBuilder Output(HidMain flags) => ItemUnsigned(0x9, Main, (uint)flags);
    public HidDescriptorBuilder Feature(HidMain flags) => ItemUnsigned(0xB, Main, (uint)flags);

    public HidDescriptorBuilder Collection(HidCollection kind)
    {
        _collectionDepth++;
        return ItemUnsigned(0xA, Main, (uint)kind);
    }

    public HidDescriptorBuilder EndCollection()
    {
        if (_collectionDepth == 0) throw new InvalidOperationException("EndCollection without Collection.");
        _collectionDepth--;
        return EmitNoData(0xC, Main);
    }

    // ---- Global items ----
    public HidDescriptorBuilder UsagePage(ushort page) => ItemUnsigned(0x0, Global, page);
    public HidDescriptorBuilder LogicalMinimum(int v) => ItemSigned(0x1, Global, v);
    public HidDescriptorBuilder LogicalMaximum(int v) => ItemSigned(0x2, Global, v);
    public HidDescriptorBuilder PhysicalMinimum(int v) => ItemSigned(0x3, Global, v);
    public HidDescriptorBuilder PhysicalMaximum(int v) => ItemSigned(0x4, Global, v);
    /// <summary>Unit exponent is a 4-bit two's complement nibble (e.g. -2 → 0x0E).</summary>
    public HidDescriptorBuilder UnitExponent(int exp)
    {
        if (exp < -8 || exp > 7) throw new ArgumentOutOfRangeException(nameof(exp));
        return Emit(0x5, Global, 1, (uint)(exp & 0x0F));
    }
    public HidDescriptorBuilder Unit(uint unit) => unit == 0 ? Emit(0x6, Global, 1, 0) : ItemUnsigned(0x6, Global, unit);
    public HidDescriptorBuilder ReportSize(int bits) => ItemUnsigned(0x7, Global, (uint)bits);
    public HidDescriptorBuilder ReportId(byte id) => ItemUnsigned(0x8, Global, id);
    public HidDescriptorBuilder ReportCount(int count) => ItemUnsigned(0x9, Global, (uint)count);
    public HidDescriptorBuilder Push() => EmitNoData(0xA, Global);
    public HidDescriptorBuilder Pop() => EmitNoData(0xB, Global);

    // ---- Local items ----
    public HidDescriptorBuilder Usage(ushort usage) => ItemUnsigned(0x0, Local, usage);
    public HidDescriptorBuilder UsageMinimum(ushort usage) => ItemUnsigned(0x1, Local, usage);
    public HidDescriptorBuilder UsageMaximum(ushort usage) => ItemUnsigned(0x2, Local, usage);
}

[Flags]
public enum HidMain : uint
{
    Data = 0,
    Constant = 1 << 0,
    Array = 0,
    Variable = 1 << 1,
    Absolute = 0,
    Relative = 1 << 2,

    DataVarAbs = Data | Variable | Absolute,     // 0x02
    DataVarRel = Data | Variable | Relative,     // 0x06
    DataArrAbs = Data | Array | Absolute,        // 0x00
    ConstArrAbs = Constant,                      // 0x01 (padding)
    ConstVarAbs = Constant | Variable,           // 0x03 (padding)
}

public enum HidCollection : uint
{
    Physical = 0x00,
    Application = 0x01,
    Logical = 0x02,
}

public static class HidUnits
{
    /// <summary>SI linear, length in centimetres.</summary>
    public const uint Centimeter = 0x11;
    /// <summary>SI linear, time in seconds.</summary>
    public const uint Second = 0x1001;
}
