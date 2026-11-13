# iPad Share — Architecture and Design Analysis

This document explains how iPad Share works, part by part: what each piece does,
why it is built that way, what the alternatives were, and what is still uncertain
until it runs on real hardware.

---

## 0. The one constraint that shapes everything

**Nothing may be installed on the iPad.** iPadOS gives an outside device only one
way to drive it without an app: **acting as a Bluetooth keyboard, mouse or
trackpad** (Bluetooth HID). iPadOS has accepted these natively since 13.4.

So the laptop has to *become* a Bluetooth keyboard + mouse + trackpad, and
every other feature is built on top of that:

| Wanted | How it is achieved without an iPad app |
| --- | --- |
| Connection | Laptop presents itself as a Bluetooth Low Energy HID device ("HID over GATT"). The iPad pairs with it like any keyboard and reconnects automatically. |
| Keyboard | Laptop key presses are re-encoded as HID keyboard reports. |
| Pointer / clicks / scroll | Re-encoded as HID mouse reports (with high-resolution wheel when the iPad accepts it). |
| Multi-touch gestures | Two modes: **iPad-native** (laptop presents a multi-touch trackpad and forwards raw finger positions) or **Translated** (the laptop recognises gestures itself and sends the matching iPadOS shortcuts). |
| Seamless screen switching | The Windows app decides which device "owns" the keyboard and pointer, using the screen edges, exactly like Universal Control. |

**Not possible without an iPad app** (and therefore not attempted): a shared
clipboard, dragging files between screens, showing the iPad screen on Windows.
These need Apple's private Continuity protocols.

**Wi‑Fi is not used for control.** iPadOS has no built-in way to accept input
over Wi‑Fi. Wi‑Fi is used only for the *optional* one-time calibration web page
(Section 6.4), opened in Safari, never installed.

---

## 1. System overview

```
 Lenovo laptop (Windows)                                         iPad Air (M1)
┌──────────────────────────────────────────────────────────┐   ┌──────────────┐
│ Input thread (real-time)                                 │   │              │
│  ├ Low-level keyboard + mouse hooks  ── block/pass ──┐   │   │   iPadOS     │
│  ├ Raw Input: mice (unaccelerated deltas)            │   │   │   built-in   │
│  └ Raw Input: Precision Touchpad (finger contacts)   │   │   │   HID host   │
│                                                      ▼   │   │              │
│  EdgeRouter ── which device owns input? (PC / iPad)      │   │  pointer,    │
│  KeyTranslator ── Windows keys → iPad keys               │   │  keyboard,   │
│  GestureEngine ── fingers → pointer/scroll/gestures      │   │  trackpad    │
│  IpadCursorModel ── where is the iPad pointer?           │   │  gestures    │
│                                                      │   │   │              │
│ Send thread: ReportScheduler (coalescing, ordered)   ▼   │   │              │
│  └ IHidTransport ─┬─ Built-in Bluetooth (WinRT GATT) ────────▶  BLE HID   │
│                   └─ USB bridge (ESP32-S3)  ─────────────────▶  BLE HID   │
│                                                          │   │              │
│ UI thread: tray icon, settings, onboarding, arrangement  │   │  Safari      │
│ Calibration server (optional, LAN) ◀───── WebSocket ─────────  (one time)  │
└──────────────────────────────────────────────────────────┘   └──────────────┘
```

The code is split into:

* `src/IpadShare.Core` — all logic that can be reasoned about and **unit-tested
  without Windows or an iPad**: HID descriptors and reports, key translation,
  the gesture engine, edge/handoff logic, the cursor model, calibration math,
  the report scheduler and the USB-bridge protocol.
* `src/IpadShare.Windows` — the Windows-specific shell: hooks, Raw Input,
  touchpad parsing via `hid.dll`, Bluetooth via WinRT, windows, tray, UI.
* `firmware/esp32-bridge` — optional USB dongle firmware for laptops whose
  Bluetooth adapter cannot act as a peripheral.
* `tests/IpadShare.Core.Tests` — the unit tests.

---

## 2. Bluetooth: turning the laptop into a Bluetooth HID device

### 2.1 Why Bluetooth Low Energy, and why through WinRT

* Windows **cannot** act as a *Classic* Bluetooth HID device; its stack has no
  HID device role.
* Windows **can** publish GATT services as an LE *peripheral* through WinRT's
  `GattServiceProvider` (Windows 10 2004+). Microsoft's reserved-service list
  (GAP, GATT, Device Information, Scan Parameters) does **not** include HID
  (0x1812), and open-source projects (e.g. `windows-ble-hid`) report this
  working on built-in Intel and other radios.
* The adapter must support the **LE peripheral role**. The app checks
  `BluetoothAdapter.IsPeripheralRoleSupported` at startup and tells you
  plainly if it doesn't. If it doesn't, the USB bridge (Section 2.6) is the
  fallback.

### 2.2 GATT layout (HID over GATT Profile)

Service `0x1812` (Human Interface Device):

| Characteristic | UUID | Properties | Notes |
| --- | --- | --- | --- |
| HID Information | 0x2A4A | Read | bcdHID 1.11, country 0, flags: normally connectable |
| Report Map | 0x2A4B | Read | The HID report descriptor (Section 3), ≤ 512 bytes |
| HID Control Point | 0x2A4C | Write w/o response | Suspend / exit suspend |
| Protocol Mode | 0x2A4E | Read, Write w/o response | Always "report protocol" |
| Report (keyboard) | 0x2A4D | Read, Notify | Report Reference `[1, input]` |
| Report (consumer) | 0x2A4D | Read, Notify | `[2, input]` |
| Report (mouse) | 0x2A4D | Read, Notify | `[3, input]` |
| Report (wheel resolution) | 0x2A4D | Read, Write | `[4, feature]` |
| Report (trackpad) | 0x2A4D | Read, Notify | `[5, input]` |
| Report (trackpad caps) | 0x2A4D | Read, Write | `[6, feature]` |

Plus a **Battery Service** (0x180F) reporting the *laptop's* battery, so it shows
in the iPad's Batteries widget like any Apple accessory.

Every HID characteristic requires an **encrypted link**. That is what makes the
iPad pair and bond: on first access iOS starts pairing, Windows accepts it
(you may see a Windows "pair device?" prompt once), and afterwards both sides
remember the bond and reconnect automatically.

### 2.3 Advertising and reconnection

* While no iPad is subscribed, the app advertises the HID service (connectable,
  discoverable). The iPad lists the laptop under *Settings → Bluetooth* using
  the Windows computer's Bluetooth name.
* After bonding, iOS reconnects on its own whenever the laptop advertises, so
  you never pair again. The app keeps advertising whenever it is running and
  not connected, and it starts with Windows (tray), so this is automatic.
* "Connected" is detected when the iPad subscribes to the input-report
  notifications (`SubscribedClientsChanged`). The iPad's name is read from the
  session and shown in the UI ("Connected to Gabriel's iPad").

### 2.4 Latency

* BLE HID latency is that of any Bluetooth mouse: one connection interval
  (iOS typically uses 11.25–15 ms for HID) plus processing.
* On Windows 11 the app asks for a low-latency connection
  (`RequestPreferredConnectionParameters(ThroughputOptimized)`); failures are
  ignored.
* The `ReportScheduler` (Section 7) never lets a queue build up. Pointer motion
  is *coalesced* (summed) while a report is in flight, so the pointer always
  shows the latest position instead of replaying stale motion.

### 2.5 MTU and report sizes

Notifications carry `ATT_MTU − 3` bytes. iPads negotiate large MTUs (≥ 185), but
the trackpad report (29 bytes) needs MTU ≥ 32. The app checks the negotiated
`MaxPduSize`. If it is too small, iPad-native trackpad mode is disabled for
that session and Translated mode is used. Keyboard (8 B), consumer (2 B) and
mouse (9 B) reports always fit the 20-byte minimum.

### 2.6 USB bridge fallback (ESP32-S3)

If the laptop's Bluetooth can't be a peripheral, a ~US$8 ESP32-S3 board plugged
into USB does the radio work:

* Windows sends ready-made HID reports over USB serial (COBS-framed packets
  with CRC-8, see `BridgeProtocol`).
* The firmware (NimBLE) exposes **the same report map** as the built-in path.
  A unit test checks that the firmware's byte array matches the C# descriptor.
* The bridge keeps the bond in its own flash. Your laptop's Bluetooth stays free
  for headphones.
* The firmware can be flashed from a browser (esptool-js), with no tools to install.

Transport selection is automatic: a responding bridge is preferred (most
controllable), otherwise built-in Bluetooth is used.

---

## 3. The HID report map: what the iPad sees

`IpadShare.Core/Hid/ReportMap.cs` builds the descriptor with a small builder
that picks the minimal item encoding. A test parses it back and verifies the
size of every report. The descriptor is fixed (it doesn't depend on the
laptop), because iOS caches it for bonded devices.

| ID | Type | Layout | Purpose |
| --- | --- | --- | --- |
| 1 | Input | modifiers(8b) · Apple *Globe* bit + 7 pad · 6 keys | Keyboard. The Globe bit uses Apple's vendor usage (page 0xFF, usage 0x03), the same one Magic Keyboards use for 🌐. |
| 2 | Input | 16-bit consumer usage | Media keys, volume, play/pause |
| 3 | Input | 5 buttons · X16 · Y16 · Wheel16 · Pan16 | Mouse with 16-bit motion (no splitting) and horizontal scroll (AC Pan) |
| 4 | Feature | wheel resolution multiplier | If the iPad turns on high-resolution scrolling, the app sends scroll in 1/120 notches. Otherwise it accumulates whole notches. It adapts to whatever the host does. |
| 5 | Input | 5 × (confidence, tip, contact id, X16, Y16) · scan time · contact count · button | Multi-touch trackpad in the standard digitizer layout used by Windows Precision Touchpads. Logical size 3000 × 3000 over 150 × 150 mm (0.05 mm resolution). |
| 6 | Feature | max contacts (5) · pad type (clickpad) | Trackpad capabilities |

The whole map is ≤ 512 bytes (the ATT attribute limit). This is enforced by a test.

### 3.1 The open question: will iPadOS use the trackpad collection?

iPadOS gives full gestures to the Magic Trackpad and to some third-party
trackpads (Brydge added "native multi-touch" by firmware for iPadOS 14.5;
Logitech's Combo Touch has it). Apple does not document what a third-party
trackpad must report. The report above follows the industry-standard digitizer
layout. Whether iPadOS treats it as a gesture-capable trackpad **can only be
learned by trying**, so:

* The device always exposes **both** a mouse and a trackpad, so switching modes
  needs no re-pairing.
* **Translated** mode (guaranteed to work) is the default. Onboarding has a
  10-second test: "swipe up with three fingers — did your iPad go Home?" If yes,
  iPad-native mode stays on.

---

## 4. Capturing input on Windows

All capture runs on **one dedicated high-priority thread** with its own message
loop (`InputThread`). Hooks must return in well under a millisecond, or Windows
silently removes them. Running everything on one thread also means the engine
needs no locks.

### 4.1 Low-level hooks (keyboard + mouse)

`WH_KEYBOARD_LL` and `WH_MOUSE_LL` let the app see every key and mouse event and
decide, per event, to **pass** it to Windows or **block** it.

* **PC mode:** everything passes. The mouse hook watches the cursor for an edge
  push.
* **iPad mode:** everything is blocked and forwarded to the iPad, with these
  exceptions:
  * A key that was *pressed on Windows* before switching still delivers its
    key-up to Windows (no stuck Shift/Ctrl).
  * Events injected by *other* software are dropped, not forwarded. This also
    neutralises Windows touchpad gestures that are implemented as injected
    shortcuts (e.g. Win+Tab).
* **Ctrl+Alt+Del** cannot be blocked (by design of Windows). When a secure
  desktop appears (lock screen, UAC), the app returns to PC mode instantly.

### 4.2 Raw Input: unaccelerated mouse deltas

Hook coordinates already include Windows' pointer acceleration, and iPadOS
applies its own. Double acceleration feels "floaty". So in iPad mode, mouse motion
comes from **Raw Input** (`WM_INPUT`, `RIDEV_INPUTSINK`), which delivers the
mouse's true counts. Blocking in the low-level hook does not stop Raw Input.
iPadOS then applies its own acceleration exactly as if the mouse were paired
directly.

### 4.3 Precision Touchpad: raw finger contacts

The Lenovo's touchpad is a Windows Precision Touchpad (HID digitizer
0x0D/0x05). Through Raw Input the app receives its **raw HID reports** and
parses them with `hid.dll` (`HidP_GetUsageValue`/`HidP_GetUsages` per finger
link collection):

* Per finger: contact ID, tip, confidence (palm rejection), X, Y.
* Per frame: contact count, scan time, physical button.
* **Hybrid reporting** (fingers split across several reports) is reassembled.
* Physical size in mm is computed from the descriptor's physical extents and
  units, so gestures are tuned to **your** touchpad's real size.
* The user's Windows touchpad preferences (tap to click, two-finger tap, right
  click zone, tap-and-drag) are read from the registry and honoured on the iPad.

If no Precision Touchpad is found (or parsing fails), the app falls back to
using the hook's synthesized pointer/wheel events, which is less rich but still
works.

### 4.4 The capture window: hiding the Windows cursor and stopping leaks

While controlling the iPad, a **transparent, click-through-proof topmost window**
covers the Windows desktop (alpha 1/255, invisible):

* Its cursor is empty, so the Windows pointer disappears.
* It keeps touchpad gestures from reaching apps that read the touchpad directly
  (DirectManipulation: Edge, Explorer), which hooks cannot block.
* The Windows cursor is parked under it. A watchdog warps it back and
  reinstalls the hooks if Windows ever removed them.

---

## 5. Gestures and the trackpad feel

### 5.1 iPad-native mode

Finger contacts are forwarded as trackpad reports with real millimetre
geometry. iPadOS does everything else: inertial scrolling, pinch, rotate,
3-finger Home/App Switcher, tap-to-click (per the iPad's own settings). This is
the most "Apple" experience. It depends on Section 3.1.

### 5.2 Translated mode (`GestureEngine`)

The engine is a complete touchpad driver that runs on the laptop:

| Gesture on the Lenovo | Sent to the iPad |
| --- | --- |
| 1-finger move | Pointer motion (mm → counts; iPadOS applies its acceleration curve) |
| Tap / 2-finger tap / 3-finger tap | Click / right-click / Spotlight (⌘Space) |
| Tap then touch and move | Drag (tap-and-drag) |
| Physical click | Left. Right if 2 fingers or in the bottom-right zone (like Windows) |
| Click and move | Drag. A resting thumb is ignored; the moving finger drives. |
| 2-finger scroll | Smooth vertical + horizontal scroll with **momentum** after lift-off (exponential decay, τ ≈ 0.33 s, like iOS). New touch stops it. |
| 2-finger pinch | Zoom (⌘+ / ⌘−) |
| 3-finger swipe up, quick | Home (⌘H) |
| 3-finger swipe up and hold | App Switcher (🌐↑) |
| 3-finger swipe left/right | Switch to last app (⌘Tab) |
| 3-finger swipe down | Notification Center (🌐N) |
| 4-finger swipe up / down | App Switcher / Home |
| 4-finger tap | Control Center (🌐C) |

Gesture classification is direction-locked (it doesn't flip mid-gesture) and
uses thresholds in **millimetres**, so it behaves the same on any touchpad.
All actions are configurable in `settings.json`.

**Scroll direction:** fingers up → wheel +, fingers right → pan +. With the
iPad's default *Natural Scrolling*, content follows your fingers, which matches
the Windows default. "Reverse scrolling" flips it.

---

## 6. Seamless screen switching (`EdgeRouter` + `IpadCursorModel`)

### 6.1 Arrangement

You drag the iPad around a picture of your screen(s), as in the Displays
settings on a Mac. The iPad is drawn at its **real physical size** relative to
the laptop panel (laptop size from the monitor's physical DPI, iPad Air =
1180 × 820 pt landscape). The overlap between the two facing edges is the
*crossing segment*. Positions along it map linearly, so the pointer leaves the
laptop and enters the iPad at the height you'd expect.

### 6.2 PC → iPad

* Crossing requires a deliberate **push**: the cursor must sit on the edge and
  keep moving outward ~1.5 cm (measured from raw motion), within a short time
  window. A thin glow at the edge shows the resistance building, like Universal
  Control's.
* **Corner guards:** the top and bottom ~2.5 % of the edge never cross, so
  flinging to a maximized window's ✕ button doesn't throw you onto the iPad.
* No crossing while a mouse button is held.

### 6.3 Where is the iPad's pointer? (the hard part)

A mouse is relative: the laptop can't *place* the iPad pointer, and nothing on
the iPad reports where it is. The app solves this in three layers:

1. **Edge snap on entry.** When crossing, the app sends a large burst of motion
   toward the shared edge. iPadOS clamps the pointer at its screen edge, so its
   horizontal position becomes **exactly known**, and it is exactly where it
   should appear. It then moves vertically to the matching height.
2. **Dead reckoning.** Every count sent is integrated through a model of iPadOS
   pointer acceleration (gain as a function of speed), clamped to the screen.
3. **Self-correction at edges.** Pushing past an iPad edge pins the real
   pointer there, so whenever the model reaches an edge and you keep pushing,
   model and reality converge again.

**Returning to Windows:** when the modelled pointer is at the shared edge *and*
you keep pushing past it by a margin, control returns to Windows at the
matching height. With calibration the margin is small. Without it the margin is
larger, so the return is never *early*: an early return would steal clicks near
the iPad's edge, such as the Back button.

Fallback: **Ctrl+Alt+Space** toggles between devices at any time.

### 6.4 One-time calibration (optional, no install)

The iPad's acceleration curve depends on its *Tracking Speed* setting. To learn
it exactly:

1. The laptop starts a tiny local web server and shows a QR code.
2. You open it in Safari on the iPad (camera → tap link). It's a web page, not an app.
3. The laptop sends controlled strokes at 8 speeds. The page reports where
   the pointer actually went (pointer events give its position in points).
4. A gain curve is fitted and saved. Dead reckoning becomes accurate, and the
   return margin shrinks.

---

## 7. Sending reports efficiently (`ReportScheduler`)

* **Keyboard and consumer reports:** strict FIFO, never dropped or merged. A
  key press must arrive exactly once and in order.
* **Mouse:** a queue of *segments* (button state + accumulated motion/scroll).
  Motion merges into the latest segment. A button change starts a new segment,
  so clicks land exactly where the pointer was.
* **Trackpad frames:** latest-frame-wins under back-pressure, while preserving
  finger-lift events, so the iPad never sees a "stuck" finger.
* At most 2 notifications are in flight. Everything else coalesces. Latency
  stays at one connection interval even if the radio stalls.

---

## 8. Keyboard (`KeyTranslator`)

Keys are sent by **physical position** (scan code → HID usage). The iPad's
*Hardware Keyboard* layout setting decides the characters, exactly as with any
external keyboard. **Set it to your laptop's layout** (e.g. Spanish – ISO).

Two shortcut styles:

* **Windows-style (default):** your Windows muscle memory works on the iPad.
  * Ctrl → ⌘ (Ctrl+C/V/X/Z/A/T/W/F/S… just work), Alt → ⌥, Win → 🌐 (system
    shortcuts, like Win on Windows).
  * Win tapped alone → Spotlight (⌘Space). Win+D → Home. Win+Tab → App Switcher.
  * Alt+Tab → ⌘Tab (hold Alt to keep the switcher open).
  * Ctrl+←/→ → ⌥←/→ (word jump), Ctrl+Backspace/Delete → delete word.
  * Home/End → ⌘←/⌘→ (line start/end), Ctrl+Home/End → ⌘↑/⌘↓.
  * Ctrl+Y → ⌘⇧Z (redo), F5 → ⌘R (reload), PrintScreen → ⌘⇧3 (screenshot).
* **Mac-style:** physical mapping (Win → ⌘, Alt → ⌥, Ctrl → ⌃), no translations.

Media/volume keys go to the iPad as consumer-control usages. Key auto-repeat is
done by the iPad (Windows repeat events are ignored), like a real keyboard.

---

## 9. Reliability and safety

* **No stuck input:** per-key ownership on switch; every button and key is
  released to the iPad on return; hooks re-armed by a watchdog.
* **Secure desktop / lock / sleep:** instantly returns to PC mode.
* **Crash safety:** nothing system-wide is modified, so there is nothing to
  restore if the app dies. No system cursor replacement, no registry gesture
  hacks. If the app exits, Windows input is simply normal.
* **Diagnostics:** one click copies a full report (adapter capabilities,
  touchpad geometry, connection, MTU, settings, recent log), so problems can be
  fixed remotely.

---

## 10. What is verified where

| Part | Verified by |
| --- | --- |
| HID descriptor validity and report sizes | Unit tests (descriptor parser) |
| Report encoding, key translation, gesture engine, edge router, cursor model, calibration fit, scheduler, bridge framing | Unit tests |
| Builds into a self-contained Windows `.exe` (x64 + ARM64) | CI and local cross-build |
| Windows hooks, Raw Input, touchpad parsing, WinRT Bluetooth, UI | **Needs your laptop**. Written defensively, with every subsystem failing soft and logging to Diagnostics. |
| iPadOS acceptance of the trackpad collection, Globe key, scroll multiplier | **Needs your iPad**. Designed so each one degrades gracefully if the iPad says no. |
