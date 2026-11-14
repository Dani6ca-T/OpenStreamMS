// XboxSeries.cpp — ver XboxSeries.h. El descriptor y la forma de los informes vienen del
// perfil Xbox Series de libvirtualgamepad (Copyright (c) 2026 Chase Payne, MIT).
#include "XboxSeries.h"

#include <string.h>

namespace osmspad {
namespace {

constexpr uint8_t kDescriptor[] = {
    0x05, 0x01,              // Usage Page (Generic Desktop)
    0x09, 0x05,              // Usage (Game Pad)
    0xA1, 0x01,              // Collection (Application)
    0x85, 0x01,              //   Report ID (1)
    0x09, 0x01,              //   Usage (Pointer)
    0xA1, 0x00,              //   Collection (Physical)
    0x09, 0x30,              //     Usage (X)
    0x09, 0x31,              //     Usage (Y)
    0x15, 0x00,              //     Logical Minimum (0)
    0x27, 0xFF, 0xFF, 0x00, 0x00,  // Logical Maximum (65535)
    0x95, 0x02,              //     Report Count (2)
    0x75, 0x10,              //     Report Size (16)
    0x81, 0x02,              //     Input (Data, Variable, Absolute)
    0xC0,                    //   End Collection
    0x09, 0x01,              //   Usage (Pointer)
    0xA1, 0x00,              //   Collection (Physical)
    0x09, 0x32,              //     Usage (Z)
    0x09, 0x35,              //     Usage (Rz)
    0x15, 0x00,              //     Logical Minimum (0)
    0x27, 0xFF, 0xFF, 0x00, 0x00,  // Logical Maximum (65535)
    0x95, 0x02,              //     Report Count (2)
    0x75, 0x10,              //     Report Size (16)
    0x81, 0x02,              //     Input (Data, Variable, Absolute)
    0xC0,                    //   End Collection
    0x05, 0x02,              //   Usage Page (Simulation Controls)
    0x09, 0xC5,              //   Usage (Brake)
    0x15, 0x00,              //   Logical Minimum (0)
    0x26, 0xFF, 0x03,        //   Logical Maximum (1023)
    0x95, 0x01,              //   Report Count (1)
    0x75, 0x0A,              //   Report Size (10)
    0x81, 0x02,              //   Input (Data, Variable, Absolute)
    0x15, 0x00,              //   Logical Minimum (0)
    0x25, 0x00,              //   Logical Maximum (0)
    0x75, 0x06,              //   Report Size (6)
    0x95, 0x01,              //   Report Count (1)
    0x81, 0x03,              //   Input (Constant, Variable, Absolute)
    0x05, 0x02,              //   Usage Page (Simulation Controls)
    0x09, 0xC4,              //   Usage (Accelerator)
    0x15, 0x00,              //   Logical Minimum (0)
    0x26, 0xFF, 0x03,        //   Logical Maximum (1023)
    0x95, 0x01,              //   Report Count (1)
    0x75, 0x0A,              //   Report Size (10)
    0x81, 0x02,              //   Input (Data, Variable, Absolute)
    0x15, 0x00,              //   Logical Minimum (0)
    0x25, 0x00,              //   Logical Maximum (0)
    0x75, 0x06,              //   Report Size (6)
    0x95, 0x01,              //   Report Count (1)
    0x81, 0x03,              //   Input (Constant, Variable, Absolute)
    0x05, 0x01,              //   Usage Page (Generic Desktop)
    0x09, 0x39,              //   Usage (Hat switch)
    0x15, 0x01,              //   Logical Minimum (1)
    0x25, 0x08,              //   Logical Maximum (8)
    0x35, 0x00,              //   Physical Minimum (0)
    0x46, 0x3B, 0x01,        //   Physical Maximum (315)
    0x66, 0x14, 0x00,        //   Unit (English Rotation: Degrees)
    0x75, 0x04,              //   Report Size (4)
    0x95, 0x01,              //   Report Count (1)
    0x81, 0x42,              //   Input (Data, Variable, Absolute, Null state)
    0x75, 0x04,              //   Report Size (4)
    0x95, 0x01,              //   Report Count (1)
    0x15, 0x00,              //   Logical Minimum (0)
    0x25, 0x00,              //   Logical Maximum (0)
    0x35, 0x00,              //   Physical Minimum (0)
    0x45, 0x00,              //   Physical Maximum (0)
    0x65, 0x00,              //   Unit (None)
    0x81, 0x03,              //   Input (Constant, Variable, Absolute)
    0x05, 0x09,              //   Usage Page (Button)
    0x19, 0x01,              //   Usage Minimum (1)
    0x29, 0x0F,              //   Usage Maximum (15)
    0x15, 0x00,              //   Logical Minimum (0)
    0x25, 0x01,              //   Logical Maximum (1)
    0x75, 0x01,              //   Report Size (1)
    0x95, 0x0F,              //   Report Count (15)
    0x81, 0x02,              //   Input (Data, Variable, Absolute)
    0x15, 0x00,              //   Logical Minimum (0)
    0x25, 0x00,              //   Logical Maximum (0)
    0x75, 0x01,              //   Report Size (1)
    0x95, 0x01,              //   Report Count (1)
    0x81, 0x03,              //   Input (Constant, Variable, Absolute)
    0x05, 0x0C,              //   Usage Page (Consumer)
    0x0A, 0xB2, 0x00,        //   Usage (Record)
    0x15, 0x00,              //   Logical Minimum (0)
    0x25, 0x01,              //   Logical Maximum (1)
    0x95, 0x01,              //   Report Count (1)
    0x75, 0x01,              //   Report Size (1)
    0x81, 0x02,              //   Input (Data, Variable, Absolute)
    0x15, 0x00,              //   Logical Minimum (0)
    0x25, 0x00,              //   Logical Maximum (0)
    0x75, 0x07,              //   Report Size (7)
    0x95, 0x01,              //   Report Count (1)
    0x81, 0x03,              //   Input (Constant, Variable, Absolute)
    // Vibración: Physical Interface Device, máscara de actuadores y cuatro magnitudes.
    0x05, 0x0F,              //   Usage Page (Physical Interface Device)
    0x09, 0x21,              //   Usage (Set Effect Report)
    0x85, 0x03,              //   Report ID (3)
    0xA1, 0x02,              //   Collection (Logical)
    0x09, 0x97,              //     Usage (DC Enable Actuators)
    0x15, 0x00,              //     Logical Minimum (0)
    0x25, 0x01,              //     Logical Maximum (1)
    0x75, 0x04,              //     Report Size (4)
    0x95, 0x01,              //     Report Count (1)
    0x91, 0x02,              //     Output (Data, Variable, Absolute)
    0x15, 0x00,              //     Logical Minimum (0)
    0x25, 0x00,              //     Logical Maximum (0)
    0x75, 0x04,              //     Report Size (4)
    0x95, 0x01,              //     Report Count (1)
    0x91, 0x03,              //     Output (Constant, Variable, Absolute)
    0x09, 0x70,              //     Usage (Magnitude)
    0x15, 0x00,              //     Logical Minimum (0)
    0x25, 0x64,              //     Logical Maximum (100)
    0x75, 0x08,              //     Report Size (8)
    0x95, 0x04,              //     Report Count (4)
    0x91, 0x02,              //     Output (Data, Variable, Absolute)
    0x09, 0x50,              //     Usage (Duration)
    0x66, 0x01, 0x10,        //     Unit (SI Linear: Seconds)
    0x55, 0x0E,              //     Unit Exponent (-2)
    0x15, 0x00,              //     Logical Minimum (0)
    0x26, 0xFF, 0x00,        //     Logical Maximum (255)
    0x75, 0x08,              //     Report Size (8)
    0x95, 0x01,              //     Report Count (1)
    0x91, 0x02,              //     Output (Data, Variable, Absolute)
    0x09, 0xA7,              //     Usage (Start Delay)
    0x15, 0x00,              //     Logical Minimum (0)
    0x26, 0xFF, 0x00,        //     Logical Maximum (255)
    0x75, 0x08,              //     Report Size (8)
    0x95, 0x01,              //     Report Count (1)
    0x91, 0x02,              //     Output (Data, Variable, Absolute)
    0x65, 0x00,              //     Unit (None)
    0x55, 0x00,              //     Unit Exponent (0)
    0x09, 0x7C,              //     Usage (Loop Count)
    0x15, 0x00,              //     Logical Minimum (0)
    0x26, 0xFF, 0x00,        //     Logical Maximum (255)
    0x75, 0x08,              //     Report Size (8)
    0x95, 0x01,              //     Report Count (1)
    0x91, 0x02,              //     Output (Data, Variable, Absolute)
    0xC0,                    //   End Collection
    0xC0,                    // End Collection
};

// Primero el ID concreto del Series; detrás, el ID genérico de software GIP que también
// casa con xinputhid.inf, por si Microsoft retira el primero.
constexpr wchar_t kHardwareIds[] =
    L"HID\\VID_045E&PID_0B12&IG_00\0"
    L"HID\\VID_045E&PID_02FF&IG_00\0"
    L"\0";

// Bits del campo de botones del mando real (botones HID 1..15, con huecos en 3, 6, 9 y 10).
enum : uint16_t {
    kA = 1u << 0, kB = 1u << 1, kX = 1u << 3, kY = 1u << 4,
    kLB = 1u << 6, kRB = 1u << 7, kView = 1u << 10, kMenu = 1u << 11,
    kGuide = 1u << 12, kLS = 1u << 13, kRS = 1u << 14,
};

uint8_t EncodeHat(uint16_t b) noexcept {
    const bool up = b & OSMSPAD_DPAD_UP, down = b & OSMSPAD_DPAD_DOWN;
    const bool left = b & OSMSPAD_DPAD_LEFT, right = b & OSMSPAD_DPAD_RIGHT;
    if ((up && down) || (left && right)) return 0;
    if (up) return right ? 2 : (left ? 8 : 1);
    if (down) return right ? 4 : (left ? 6 : 5);
    return right ? 3 : (left ? 7 : 0);
}

uint16_t MapButtons(uint16_t b) noexcept {
    uint16_t m = 0;
    if (b & OSMSPAD_A) m |= kA;
    if (b & OSMSPAD_B) m |= kB;
    if (b & OSMSPAD_X) m |= kX;
    if (b & OSMSPAD_Y) m |= kY;
    if (b & OSMSPAD_LEFT_SHOULDER) m |= kLB;
    if (b & OSMSPAD_RIGHT_SHOULDER) m |= kRB;
    if (b & OSMSPAD_BACK) m |= kView;
    if (b & OSMSPAD_START) m |= kMenu;
    if (b & OSMSPAD_GUIDE) m |= kGuide;
    if (b & OSMSPAD_LEFT_THUMB) m |= kLS;
    if (b & OSMSPAD_RIGHT_THUMB) m |= kRS;
    return m;
}

// XInput: con signo y positivo hacia arriba. El mando: sin signo y, en vertical, hacia abajo.
uint16_t Horizontal(int16_t v) noexcept { return static_cast<uint16_t>(static_cast<int32_t>(v) + 32768); }
uint16_t Vertical(int16_t v) noexcept { return static_cast<uint16_t>(32767 - static_cast<int32_t>(v)); }
uint16_t Trigger(uint8_t v) noexcept { return static_cast<uint16_t>((v * uint32_t{kXboxTriggerMax}) / 255u); }

uint16_t FromPercent(uint8_t v) noexcept {
    const uint32_t c = v > kXboxRumbleMax ? kXboxRumbleMax : v;
    return static_cast<uint16_t>((c * 65535u) / kXboxRumbleMax);
}

}  // namespace

const uint8_t* XboxSeriesDescriptor(size_t* size) noexcept {
    if (size) *size = sizeof(kDescriptor);
    return kDescriptor;
}

const wchar_t* XboxSeriesHardwareIds(size_t* bytes) noexcept {
    if (bytes) *bytes = sizeof(kHardwareIds);
    return kHardwareIds;
}

XboxInputReport EncodeXboxInput(const OsmsPadInput& in) noexcept {
    XboxInputReport r{};
    r.ReportId = kXboxInputReportId;
    r.LeftX = Horizontal(in.ThumbLX);
    r.LeftY = Vertical(in.ThumbLY);
    r.RightX = Horizontal(in.ThumbRX);
    r.RightY = Vertical(in.ThumbRY);
    r.LeftTrigger = Trigger(in.LeftTrigger);
    r.RightTrigger = Trigger(in.RightTrigger);
    r.Hat = EncodeHat(in.Buttons);
    r.Buttons = MapButtons(in.Buttons);
    r.Share = (in.Buttons & OSMSPAD_SHARE) ? 1 : 0;
    return r;
}

bool DecodeXboxRumble(const uint8_t* data, size_t size, OsmsPadFeedback* out) noexcept {
    if (!data || !out || size < sizeof(XboxOutputReport)) return false;
    XboxOutputReport o;
    memcpy(&o, data, sizeof o);
    if (o.ReportId != kXboxOutputReportId) return false;
    // Un actuador sin su bit de la máscara conserva su estado en el mando real; aquí cuenta
    // como parado: mejor silencio que repetir una magnitud vieja.
    out->LargeMotor = (o.EnableMask & 0x08) ? FromPercent(o.LeftMotor) : 0;
    out->SmallMotor = (o.EnableMask & 0x04) ? FromPercent(o.RightMotor) : 0;
    out->LeftTrigger = (o.EnableMask & 0x02) ? FromPercent(o.LeftTriggerMotor) : 0;
    out->RightTrigger = (o.EnableMask & 0x01) ? FromPercent(o.RightTriggerMotor) : 0;
    return true;
}

}  // namespace osmspad
