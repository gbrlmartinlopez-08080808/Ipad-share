using IpadShare.Core.Hid;

namespace IpadShare.Core.Tests;

/// <summary>
/// Independent HID report-descriptor parser used only to validate the descriptor we build:
/// it computes every field's bit position so the tests can check encoders against it.
/// </summary>
internal sealed class HidDescriptorParser
{
    public sealed record Field(
        byte ReportId, HidReportType Type, int BitOffset, int BitSize, int Count,
        ushort UsagePage, IReadOnlyList<uint> Usages, int LogicalMin, int LogicalMax,
        uint Flags, uint Unit, int UnitExponent, int Depth)
    {
        public bool IsConstant => (Flags & 1) != 0;
        public bool IsVariable => (Flags & 2) != 0;
        public bool IsRelative => (Flags & 4) != 0;
        public uint UsageAt(int i) => Usages.Count == 0 ? 0 : Usages[Math.Min(i, Usages.Count - 1)];
    }

    private struct Globals
    {
        public ushort UsagePage;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public int UnitExponent;
        public uint Unit;
        public int ReportSize, ReportCount;
        public byte ReportId;
    }

    public List<Field> Fields { get; } = new();
    public Dictionary<(byte, HidReportType), int> ReportBits { get; } = new();
    public int MaxDepth { get; private set; }
    public int ApplicationCollections { get; private set; }

    public static HidDescriptorParser Parse(ReadOnlySpan<byte> d)
    {
        var p = new HidDescriptorParser();
        p.Run(d);
        return p;
    }

    private void Run(ReadOnlySpan<byte> d)
    {
        var g = new Globals();
        var stack = new Stack<Globals>();
        var usages = new List<uint>();
        uint? usageMin = null, usageMax = null;
        int depth = 0;
        int i = 0;
        while (i < d.Length)
        {
            byte prefix = d[i++];
            if (prefix == 0xFE) throw new InvalidDataException("long items not supported");
            int size = (prefix & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
            int type = (prefix >> 2) & 3;
            int tag = prefix >> 4;
            if (i + size > d.Length) throw new InvalidDataException($"item at {i - 1} overruns descriptor");
            uint u = 0;
            for (int k = 0; k < size; k++) u |= (uint)d[i + k] << (8 * k);
            int s = size switch
            {
                0 => 0,
                1 => (sbyte)u,
                2 => (short)u,
                _ => (int)u,
            };
            i += size;

            switch (type)
            {
                case 0: // main
                    switch (tag)
                    {
                        case 0x8: AddField(g, HidReportType.Input, u, usages, usageMin, usageMax, depth); break;
                        case 0x9: AddField(g, HidReportType.Output, u, usages, usageMin, usageMax, depth); break;
                        case 0xB: AddField(g, HidReportType.Feature, u, usages, usageMin, usageMax, depth); break;
                        case 0xA:
                            depth++;
                            MaxDepth = Math.Max(MaxDepth, depth);
                            if (u == 1) ApplicationCollections++;
                            break;
                        case 0xC:
                            if (depth == 0) throw new InvalidDataException("unbalanced End Collection");
                            depth--;
                            break;
                        default: throw new InvalidDataException($"unknown main tag {tag:X}");
                    }
                    usages.Clear(); usageMin = usageMax = null;
                    break;
                case 1: // global
                    switch (tag)
                    {
                        case 0x0: g.UsagePage = (ushort)u; break;
                        case 0x1: g.LogicalMin = s; break;
                        case 0x2: g.LogicalMax = s; break;
                        case 0x3: g.PhysicalMin = s; break;
                        case 0x4: g.PhysicalMax = s; break;
                        case 0x5: g.UnitExponent = (int)(u & 0xF) >= 8 ? (int)(u & 0xF) - 16 : (int)(u & 0xF); break;
                        case 0x6: g.Unit = u; break;
                        case 0x7: g.ReportSize = (int)u; break;
                        case 0x8:
                            if (u == 0) throw new InvalidDataException("report id 0 is reserved");
                            g.ReportId = (byte)u; break;
                        case 0x9: g.ReportCount = (int)u; break;
                        case 0xA: stack.Push(g); break;
                        case 0xB:
                            if (stack.Count == 0) throw new InvalidDataException("Pop without Push");
                            var popped = stack.Pop();
                            // Report ID is not affected by push/pop in practice, but keep spec semantics.
                            g = popped; break;
                        default: throw new InvalidDataException($"unknown global tag {tag:X}");
                    }
                    break;
                case 2: // local
                    switch (tag)
                    {
                        case 0x0: usages.Add(size == 4 ? u : ((uint)g.UsagePage << 16) | u); break;
                        case 0x1: usageMin = u; break;
                        case 0x2: usageMax = u; break;
                        default: throw new InvalidDataException($"unsupported local tag {tag:X}");
                    }
                    break;
                default:
                    throw new InvalidDataException("reserved item type");
            }
        }
        if (depth != 0) throw new InvalidDataException("unbalanced collections");
        if (stack.Count != 0) throw new InvalidDataException("unbalanced push/pop");
    }

    private void AddField(Globals g, HidReportType type, uint flags, List<uint> usages, uint? umin, uint? umax, int depth)
    {
        if (g.ReportSize <= 0 || g.ReportCount <= 0) throw new InvalidDataException("field without size/count");
        var key = (g.ReportId, type);
        ReportBits.TryGetValue(key, out int offset);
        var list = new List<uint>();
        foreach (var x in usages) list.Add(x);
        if (umin is uint a && umax is uint b)
            for (uint x = a; x <= b; x++) list.Add(((uint)g.UsagePage << 16) | x);
        // Normalise: usages captured with the page at the time of the Usage item.
        Fields.Add(new Field(g.ReportId, type, offset, g.ReportSize, g.ReportCount, g.UsagePage, list,
            g.LogicalMin, g.LogicalMax, flags, g.Unit, g.UnitExponent, depth));
        ReportBits[key] = offset + g.ReportSize * g.ReportCount;
    }

    public static long ReadBits(ReadOnlySpan<byte> report, int bitOffset, int bitSize, bool signed)
    {
        ulong v = 0;
        for (int b = 0; b < bitSize; b++)
        {
            int bit = bitOffset + b;
            if (((report[bit >> 3] >> (bit & 7)) & 1) != 0) v |= 1UL << b;
        }
        if (signed && bitSize < 64 && (v & (1UL << (bitSize - 1))) != 0) v |= ~0UL << bitSize;
        return (long)v;
    }

    public static uint FullUsage(ushort page, ushort usage) => ((uint)page << 16) | usage;
}
