using System.Text;

namespace IpadShare.Core.Transport;

/// <summary>Message types on the USB-serial link between Windows and the ESP32-S3 bridge.</summary>
public enum BridgeMessage : byte
{
    // host → bridge
    Hello = 0x01,          // [protocol version]
    InputReport = 0x02,    // [report id, data…]
    Battery = 0x03,        // [percent]
    Advertise = 0x04,      // [0/1]
    ForgetBonds = 0x05,    // []
    Ping = 0x06,           // []

    // bridge → host
    HelloAck = 0x81,       // [protocol, fw major, fw minor, descriptor crc16 LE]
    Status = 0x82,         // [connected, mtu LE16, interval (1.25 ms units) LE16, bonds]
    HostWrite = 0x83,      // [report id, report type, data…]
    Log = 0x84,            // [utf-8 text]
    Pong = 0x86,           // []
}

public readonly record struct BridgeFrame(BridgeMessage Type, byte[] Payload)
{
    public string PayloadText => Encoding.UTF8.GetString(Payload);
}

/// <summary>
/// Framing: [type][payload…][crc8] → COBS-encoded → terminated by 0x00.
/// COBS guarantees the payload contains no zero bytes, so a lost byte can only corrupt one frame
/// and the receiver resynchronises at the next 0x00. CRC-8 (poly 0x07) rejects corrupted frames.
/// The firmware implements the exact same algorithm (firmware/esp32-bridge/src/protocol.h).
/// </summary>
public static class BridgeProtocol
{
    public const byte ProtocolVersion = 1;
    public const int MaxFrame = 256;

    public static byte[] Encode(BridgeMessage type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length + 2 > MaxFrame) throw new ArgumentException("payload too large");
        var raw = new byte[payload.Length + 2];
        raw[0] = (byte)type;
        payload.CopyTo(raw.AsSpan(1));
        raw[^1] = Crc8(raw.AsSpan(0, raw.Length - 1));
        var enc = CobsEncode(raw);
        var frame = new byte[enc.Length + 1];
        enc.CopyTo(frame, 0);
        frame[^1] = 0;
        return frame;
    }

    public static byte[] CobsEncode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + data.Length / 254 + 2);
        int codeIndex = output.Count;
        output.Add(0);
        byte code = 1;
        foreach (byte b in data)
        {
            if (b == 0)
            {
                output[codeIndex] = code;
                codeIndex = output.Count;
                output.Add(0);
                code = 1;
            }
            else
            {
                output.Add(b);
                code++;
                if (code == 0xFF)
                {
                    output[codeIndex] = code;
                    codeIndex = output.Count;
                    output.Add(0);
                    code = 1;
                }
            }
        }
        output[codeIndex] = code;
        return output.ToArray();
    }

    /// <summary>Decodes one COBS block (without the trailing 0x00). Returns null if malformed.</summary>
    public static byte[]? CobsDecode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        int i = 0;
        while (i < data.Length)
        {
            byte code = data[i++];
            if (code == 0) return null;
            for (int j = 1; j < code; j++)
            {
                if (i >= data.Length) return null;
                byte b = data[i++];
                if (b == 0) return null;
                output.Add(b);
            }
            if (code != 0xFF && i < data.Length) output.Add(0);
        }
        return output.ToArray();
    }

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }
        return crc;
    }

    /// <summary>CRC-16/CCITT-FALSE, used to confirm the bridge firmware carries the same report map.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int k = 0; k < 8; k++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
}

/// <summary>Incremental frame decoder for a byte stream.</summary>
public sealed class BridgeFrameDecoder
{
    private readonly List<byte> _buf = new(BridgeProtocol.MaxFrame + 8);
    private bool _overflow;

    public long BadFrames { get; private set; }

    public IEnumerable<BridgeFrame> Push(ReadOnlyMemory<byte> bytes)
    {
        var frames = new List<BridgeFrame>();
        foreach (byte b in bytes.Span)
        {
            if (b != 0)
            {
                if (_buf.Count < BridgeProtocol.MaxFrame + 8) _buf.Add(b); else _overflow = true;
                continue;
            }
            if (_buf.Count == 0) continue;
            if (!_overflow)
            {
                var raw = BridgeProtocol.CobsDecode(_buf.ToArray());
                if (raw != null && raw.Length >= 2 && BridgeProtocol.Crc8(raw.AsSpan(0, raw.Length - 1)) == raw[^1])
                    frames.Add(new BridgeFrame((BridgeMessage)raw[0], raw.AsSpan(1, raw.Length - 2).ToArray()));
                else BadFrames++;
            }
            else BadFrames++;
            _buf.Clear();
            _overflow = false;
        }
        return frames;
    }
}
