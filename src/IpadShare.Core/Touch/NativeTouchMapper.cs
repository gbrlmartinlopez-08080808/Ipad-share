using IpadShare.Core.Hid;

namespace IpadShare.Core.Touch;

/// <summary>
/// iPad-native mode: converts laptop touchpad frames (mm) into virtual-trackpad reports.
/// Geometry is preserved 1:1 in millimetres, centred on the 150 × 150 mm virtual pad, so
/// iPadOS gesture recognition sees real finger distances and speeds. Contact IDs are remapped
/// to stable slots, and every lift is reported once with Tip = 0 as the PTP protocol requires.
/// </summary>
public sealed class NativeTouchMapper
{
    private readonly Dictionary<int, byte> _slots = new();     // laptop contact id → report id
    private readonly bool[] _slotUsed = new bool[64];
    private readonly Dictionary<int, TouchContact> _last = new();

    public TouchpadGeometry Geometry { get; set; } = TouchpadGeometry.Default;

    /// <summary>Map one frame. Returns null when there is nothing to report (no fingers before or now).</summary>
    public TouchpadReport? Map(TouchFrame frame)
    {
        var contacts = new List<TouchContact>(ReportMap.TouchContacts);
        var present = new HashSet<int>();

        foreach (var p in frame.Contacts)
        {
            if (!p.Tip) continue;
            if (contacts.Count == ReportMap.TouchContacts) break;
            if (!present.Add(p.Id)) continue;
            byte slot = SlotFor(p.Id);
            var c = new TouchContact(slot, true, p.Confidence, ToUnitsX(p.X), ToUnitsY(p.Y));
            contacts.Add(c);
            _last[p.Id] = c;
        }

        // Fingers that disappeared: report once with Tip = 0 at their last position.
        var lifted = new List<int>();
        foreach (var id in _last.Keys) if (!present.Contains(id)) lifted.Add(id);
        foreach (var id in lifted)
        {
            var c = _last[id];
            if (contacts.Count < ReportMap.TouchContacts)
                contacts.Add(c with { Tip = false });
            _last.Remove(id);
            ReleaseSlot(id);
        }

        if (contacts.Count == 0 && !frame.Button && lifted.Count == 0 && !_buttonWasDown) return null;
        _buttonWasDown = frame.Button;
        return new TouchpadReport(contacts, ScanTime(frame.Time), frame.Button);
    }

    private bool _buttonWasDown;

    /// <summary>A report that lifts every finger still down (used when leaving the iPad).</summary>
    public TouchpadReport? LiftAll(double time)
    {
        if (_last.Count == 0 && !_buttonWasDown) return null;
        var contacts = new List<TouchContact>();
        foreach (var c in _last.Values)
            if (contacts.Count < ReportMap.TouchContacts) contacts.Add(c with { Tip = false });
        _last.Clear();
        _slots.Clear();
        Array.Clear(_slotUsed);
        _buttonWasDown = false;
        return new TouchpadReport(contacts, ScanTime(time), false);
    }

    public void Reset()
    {
        _last.Clear();
        _slots.Clear();
        Array.Clear(_slotUsed);
        _buttonWasDown = false;
    }

    public static ushort ScanTime(double seconds) => (ushort)((long)Math.Round(seconds * 10000) & 0xFFFF);

    private ushort ToUnitsX(double mm) => ToUnits(mm, Geometry.WidthMm);
    private ushort ToUnitsY(double mm) => ToUnits(mm, Geometry.HeightMm);

    private static ushort ToUnits(double mm, double padSize)
    {
        double offset = (ReportMap.TouchPhysicalSizeMm - Math.Min(padSize, ReportMap.TouchPhysicalSizeMm)) / 2;
        double v = (offset + mm) * ReportMap.TouchUnitsPerMm;
        return (ushort)Math.Clamp(Math.Round(v), 0, ReportMap.TouchLogicalMax);
    }

    private byte SlotFor(int id)
    {
        if (_slots.TryGetValue(id, out var s)) return s;
        for (byte i = 0; i < _slotUsed.Length; i++)
        {
            if (_slotUsed[i]) continue;
            _slotUsed[i] = true;
            _slots[id] = i;
            return i;
        }
        return 0;
    }

    private void ReleaseSlot(int id)
    {
        if (_slots.Remove(id, out var s)) _slotUsed[s] = false;
    }
}
