// USB-serial framing shared with the Windows app (IpadShare.Core.Transport.BridgeProtocol).
// Frame: [type][payload...][crc8] → COBS → 0x00 terminator.
#pragma once
#include <stdint.h>
#include <stddef.h>

enum BridgeMessage : uint8_t {
    MSG_HELLO = 0x01,
    MSG_INPUT_REPORT = 0x02,
    MSG_BATTERY = 0x03,
    MSG_ADVERTISE = 0x04,
    MSG_FORGET_BONDS = 0x05,
    MSG_PING = 0x06,

    MSG_HELLO_ACK = 0x81,
    MSG_STATUS = 0x82,
    MSG_HOST_WRITE = 0x83,
    MSG_LOG = 0x84,
    MSG_PONG = 0x86,
};

static const uint8_t PROTOCOL_VERSION = 1;
static const size_t MAX_FRAME = 256;

static inline uint8_t crc8(const uint8_t* d, size_t n) {
    uint8_t crc = 0;
    for (size_t i = 0; i < n; i++) {
        crc ^= d[i];
        for (int k = 0; k < 8; k++) crc = (crc & 0x80) ? (uint8_t)((crc << 1) ^ 0x07) : (uint8_t)(crc << 1);
    }
    return crc;
}

static inline uint16_t crc16(const uint8_t* d, size_t n) {
    uint16_t crc = 0xFFFF;
    for (size_t i = 0; i < n; i++) {
        crc ^= (uint16_t)d[i] << 8;
        for (int k = 0; k < 8; k++) crc = (crc & 0x8000) ? (uint16_t)((crc << 1) ^ 0x1021) : (uint16_t)(crc << 1);
    }
    return crc;
}

// COBS encode `n` bytes into `out` (capacity n + n/254 + 2). Returns encoded length (no terminator).
static inline size_t cobs_encode(const uint8_t* in, size_t n, uint8_t* out) {
    size_t code_idx = 0, o = 1;
    uint8_t code = 1;
    for (size_t i = 0; i < n; i++) {
        if (in[i] == 0) {
            out[code_idx] = code; code_idx = o++; code = 1;
        } else {
            out[o++] = in[i];
            if (++code == 0xFF) { out[code_idx] = code; code_idx = o++; code = 1; }
        }
    }
    out[code_idx] = code;
    return o;
}

// COBS decode. Returns decoded length, or 0 on malformed input.
static inline size_t cobs_decode(const uint8_t* in, size_t n, uint8_t* out) {
    size_t i = 0, o = 0;
    while (i < n) {
        uint8_t code = in[i++];
        if (code == 0) return 0;
        for (uint8_t j = 1; j < code; j++) {
            if (i >= n || in[i] == 0) return 0;
            out[o++] = in[i++];
        }
        if (code != 0xFF && i < n) out[o++] = 0;
    }
    return o;
}
