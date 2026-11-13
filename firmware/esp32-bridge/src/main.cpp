// iPad Share USB bridge — ESP32-S3 firmware.
//
// The Windows app sends ready-made HID reports over USB serial; this board is the Bluetooth LE
// keyboard + mouse + trackpad the iPad pairs with. Use it when the laptop's own Bluetooth adapter
// can't act as a peripheral. Status LED: blue blink = waiting for the iPad, green = connected.

#include <Arduino.h>
#include <NimBLEDevice.h>
#include <NimBLEHIDDevice.h>
#include "protocol.h"
#include "report_map.h"

static const uint8_t FW_MAJOR = 1, FW_MINOR = 0;
static const uint16_t APPEARANCE_HID_GENERIC = 0x03C0;

static NimBLEServer* g_server = nullptr;
static NimBLEHIDDevice* g_hid = nullptr;
static NimBLECharacteristic* g_inputs[8] = {};
static NimBLECharacteristic* g_features[8] = {};
static volatile bool g_connected = false;
static volatile uint16_t g_mtu = 23, g_interval = 0;
static volatile bool g_statusDirty = true;
static bool g_advertiseWanted = true;
static bool g_hostSeen = false;

// ---------------------------------------------------------------- serial framing
static void sendFrame(uint8_t type, const uint8_t* payload, size_t len) {
    if (len + 2 > MAX_FRAME) return;
    uint8_t raw[MAX_FRAME];
    raw[0] = type;
    if (len) memcpy(raw + 1, payload, len);
    raw[len + 1] = crc8(raw, len + 1);
    uint8_t enc[MAX_FRAME + MAX_FRAME / 254 + 3];
    size_t n = cobs_encode(raw, len + 2, enc);
    enc[n++] = 0;
    Serial.write(enc, n);
}

static void sendLog(const char* text) { sendFrame(MSG_LOG, (const uint8_t*)text, strlen(text)); }

static void sendStatus() {
    uint8_t p[6] = {
        (uint8_t)(g_connected ? 1 : 0),
        (uint8_t)(g_mtu & 0xFF), (uint8_t)(g_mtu >> 8),
        (uint8_t)(g_interval & 0xFF), (uint8_t)(g_interval >> 8),
        (uint8_t)NimBLEDevice::getNumBonds(),
    };
    sendFrame(MSG_STATUS, p, sizeof(p));
}

// ---------------------------------------------------------------- BLE callbacks
class ServerCallbacks : public NimBLEServerCallbacks {
    void onConnect(NimBLEServer* s, ble_gap_conn_desc* desc) override {
        g_connected = true;
        g_interval = desc->conn_itvl;
        // Apple accessory guidelines for HID: min interval 11.25 ms, max ≥ min + 15 ms.
        s->updateConnParams(desc->conn_handle, 9, 24, 0, 300);
        g_statusDirty = true;
    }
    void onDisconnect(NimBLEServer* s, ble_gap_conn_desc* desc) override {
        g_connected = false;
        g_statusDirty = true;
        if (g_advertiseWanted) NimBLEDevice::startAdvertising();
    }
    void onMTUChange(uint16_t mtu, ble_gap_conn_desc* desc) override {
        g_mtu = mtu;
        g_statusDirty = true;
    }
    void onAuthenticationComplete(ble_gap_conn_desc* desc) override {
        if (!desc->sec_state.encrypted) NimBLEDevice::getServer()->disconnect(desc->conn_handle);
        g_statusDirty = true;
    }
};

class FeatureCallbacks : public NimBLECharacteristicCallbacks {
public:
    explicit FeatureCallbacks(uint8_t id) : _id(id) {}
    void onWrite(NimBLECharacteristic* c) override {
        std::string v = c->getValue();
        uint8_t p[64];
        size_t n = v.size() > 60 ? 60 : v.size();
        p[0] = _id;
        p[1] = 3;  // feature report
        memcpy(p + 2, v.data(), n);
        sendFrame(MSG_HOST_WRITE, p, n + 2);
    }
private:
    uint8_t _id;
};

static void startAdvertising() {
    NimBLEAdvertising* adv = NimBLEDevice::getAdvertising();
    adv->setAppearance(APPEARANCE_HID_GENERIC);
    adv->addServiceUUID(g_hid->hidService()->getUUID());
    adv->setScanResponse(true);
    adv->start();
}

static void setupBle() {
    NimBLEDevice::init("iPad Share");
    NimBLEDevice::setPower(ESP_PWR_LVL_P9);
    NimBLEDevice::setSecurityAuth(true /*bond*/, false /*mitm*/, true /*secure connections*/);
    NimBLEDevice::setSecurityIOCap(BLE_HS_IO_NO_INPUT_OUTPUT);
    NimBLEDevice::setMTU(185);

    g_server = NimBLEDevice::createServer();
    g_server->setCallbacks(new ServerCallbacks());

    g_hid = new NimBLEHIDDevice(g_server);
    g_hid->manufacturer("iPad Share");
    g_hid->pnp(0x02, 0x303A, 0x1D5A, 0x0100);   // USB-IF vendor source, Espressif VID
    g_hid->hidInfo(0x00, 0x03);                  // country 0, remote wake + normally connectable
    g_hid->reportMap((uint8_t*)REPORT_MAP, REPORT_MAP_LEN);

    g_inputs[RID_KEYBOARD] = g_hid->inputReport(RID_KEYBOARD);
    g_inputs[RID_CONSUMER] = g_hid->inputReport(RID_CONSUMER);
    g_inputs[RID_MOUSE] = g_hid->inputReport(RID_MOUSE);
    g_inputs[RID_TOUCHPAD] = g_hid->inputReport(RID_TOUCHPAD);

    g_features[RID_MOUSE_RES] = g_hid->featureReport(RID_MOUSE_RES);
    g_features[RID_TOUCHPAD_CAPS] = g_hid->featureReport(RID_TOUCHPAD_CAPS);
    uint8_t zero = 0, caps = TOUCHPAD_CAPS_VALUE;
    g_features[RID_MOUSE_RES]->setValue(&zero, 1);
    g_features[RID_TOUCHPAD_CAPS]->setValue(&caps, 1);
    g_features[RID_MOUSE_RES]->setCallbacks(new FeatureCallbacks(RID_MOUSE_RES));
    g_features[RID_TOUCHPAD_CAPS]->setCallbacks(new FeatureCallbacks(RID_TOUCHPAD_CAPS));

    g_hid->setBatteryLevel(100);
    g_hid->startServices();
    startAdvertising();
}

// ---------------------------------------------------------------- host messages
static void handleFrame(const uint8_t* f, size_t n) {
    if (n < 2 || crc8(f, n - 1) != f[n - 1]) return;
    uint8_t type = f[0];
    const uint8_t* p = f + 1;
    size_t len = n - 2;
    switch (type) {
        case MSG_HELLO: {
            g_hostSeen = true;
            uint8_t ack[5] = { PROTOCOL_VERSION, FW_MAJOR, FW_MINOR,
                               (uint8_t)(REPORT_MAP_CRC16 & 0xFF), (uint8_t)(REPORT_MAP_CRC16 >> 8) };
            sendFrame(MSG_HELLO_ACK, ack, sizeof(ack));
            g_statusDirty = true;
            break;
        }
        case MSG_INPUT_REPORT:
            if (len >= 1 && p[0] < 8 && g_inputs[p[0]] && g_connected) {
                g_inputs[p[0]]->setValue(p + 1, len - 1);
                g_inputs[p[0]]->notify();
            }
            break;
        case MSG_BATTERY:
            if (len >= 1) g_hid->setBatteryLevel(p[0]);
            break;
        case MSG_ADVERTISE:
            g_advertiseWanted = len >= 1 && p[0] != 0;
            if (g_advertiseWanted && !g_connected) NimBLEDevice::startAdvertising();
            if (!g_advertiseWanted) NimBLEDevice::stopAdvertising();
            break;
        case MSG_FORGET_BONDS:
            NimBLEDevice::deleteAllBonds();
            sendLog("bonds cleared");
            if (g_connected) g_server->disconnect(g_server->getPeerInfo(0).getConnHandle());
            g_statusDirty = true;
            break;
        case MSG_PING:
            sendFrame(MSG_PONG, nullptr, 0);
            break;
    }
}

static uint8_t g_rx[MAX_FRAME + 8];
static size_t g_rxLen = 0;
static bool g_rxOverflow = false;

static void pollSerial() {
    while (Serial.available() > 0) {
        int c = Serial.read();
        if (c < 0) break;
        if (c != 0) {
            if (g_rxLen < sizeof(g_rx)) g_rx[g_rxLen++] = (uint8_t)c; else g_rxOverflow = true;
            continue;
        }
        if (g_rxLen > 0 && !g_rxOverflow) {
            uint8_t raw[MAX_FRAME + 8];
            size_t n = cobs_decode(g_rx, g_rxLen, raw);
            if (n > 0) handleFrame(raw, n);
        }
        g_rxLen = 0;
        g_rxOverflow = false;
    }
}

// ---------------------------------------------------------------- status LED
static void updateLed() {
#ifdef RGB_BUILTIN
    static uint32_t last = 0;
    uint32_t now = millis();
    if (now - last < 50) return;
    last = now;
    if (g_connected) neopixelWrite(RGB_BUILTIN, 0, 24, 0);
    else if ((now / 500) % 2) neopixelWrite(RGB_BUILTIN, 0, 0, 32);
    else neopixelWrite(RGB_BUILTIN, 0, 0, 0);
#endif
}

void setup() {
    Serial.begin(921600);
    setupBle();
}

void loop() {
    pollSerial();
    static uint32_t lastStatus = 0;
    if (g_statusDirty || millis() - lastStatus > 2000) {
        g_statusDirty = false;
        lastStatus = millis();
        sendStatus();
    }
    updateLed();
    delay(1);
}
