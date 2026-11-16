// XboxSeries.h — forma HID de un mando Xbox Series: descriptor, IDs y conversión de informes.
//
// Basado en el perfil Xbox Series de libvirtualgamepad (Copyright (c) 2026 Chase Payne, MIT;
// ver THIRD-PARTY.md): el descriptor reproduce el de un mando real por HID y el ID de
// hardware HID\VID_045E&PID_0B12&IG_00 es el que hace que Windows le ponga su filtro
// xinputhid.sys, que es lo que lo convierte en un mando XInput. Sin cabeceras del WDK:
// lo compilan el driver y la prueba unitaria.
#pragma once

#include <stddef.h>
#include <stdint.h>

#include "OsmsPadIoctl.h"

namespace osmspad {

constexpr uint16_t kXboxVendorId = 0x045E;
constexpr uint16_t kXboxSeriesProductId = 0x0B12;
constexpr uint16_t kXboxSeriesVersion = 0x0509;
constexpr uint8_t kXboxInputReportId = 1;
constexpr uint8_t kXboxOutputReportId = 3;
constexpr uint16_t kXboxTriggerMax = 1023;   // gatillos de 10 bits
constexpr uint8_t kXboxRumbleMax = 100;      // la vibración va en porcentaje

#pragma pack(push, 1)

struct XboxInputReport {
    uint8_t ReportId;
    uint16_t LeftX, LeftY, RightX, RightY;   // sin signo; los verticales, positivo hacia abajo
    uint16_t LeftTrigger, RightTrigger;      // 10 bits útiles
    uint8_t Hat;                             // 0 = centro, 1..8 en sentido horario desde arriba
    uint16_t Buttons;                        // 15 bits con los huecos del mando real
    uint8_t Share;                           // Consumer "Record"
};

struct XboxOutputReport {
    uint8_t ReportId;
    uint8_t EnableMask;                      // 1 gatillo dcho, 2 gatillo izq, 4 motor dcho, 8 motor izq
    uint8_t LeftTriggerMotor, RightTriggerMotor, LeftMotor, RightMotor;  // 0..100
    uint8_t Duration, StartDelay, LoopCount;
};

#pragma pack(pop)

static_assert(sizeof(XboxInputReport) == 17, "XboxInputReport");
static_assert(sizeof(XboxOutputReport) == 9, "XboxOutputReport");

const uint8_t* XboxSeriesDescriptor(size_t* size) noexcept;

// REG_MULTI_SZ para VHF_CONFIG.HardwareIDs; bytes incluye los dos terminadores.
const wchar_t* XboxSeriesHardwareIds(size_t* bytes) noexcept;

XboxInputReport EncodeXboxInput(const OsmsPadInput& in) noexcept;

// false si el informe no es una orden de vibración de este perfil.
bool DecodeXboxRumble(const uint8_t* data, size_t size, OsmsPadFeedback* out) noexcept;

}  // namespace osmspad
