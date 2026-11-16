// OsmsPadIoctl.h — contrato entre el driver OsmsPad y sus clientes (Sunshine, pruebas).
//
// Solo tipos de tamaño fijo y METHOD_BUFFERED: el mismo fichero lo incluyen el driver UMDF
// y los clientes de modo usuario, sin cabeceras del WDK. Cambiar un struct exige subir
// OSMSPAD_PROTOCOL_VERSION; el driver rechaza versiones que no conoce.
//
// Flujo de un cliente:
//   1) abrir la interfaz GUID_DEVINTERFACE_OSMSPAD (CM_Get_Device_Interface_List + CreateFile);
//   2) IOCTL_OSMSPAD_CREATE  -> número de mando (pad);
//   3) IOCTL_OSMSPAD_INPUT   con el estado completo del mando cada vez que cambie;
//   4) IOCTL_OSMSPAD_FEEDBACK pendiente (overlapped): se completa con la vibración que pide el juego;
//   5) IOCTL_OSMSPAD_DESTROY o cerrar el handle: cerrar el handle borra todos sus mandos.
#pragma once

#include <stdint.h>

#ifndef CTL_CODE
#include <windows.h>
#include <winioctl.h>
#endif

// {6B1A3C52-8E0F-4C7B-9D2A-5F3E1B7C9A40}
#ifdef DEFINE_GUID
DEFINE_GUID(GUID_DEVINTERFACE_OSMSPAD,
    0x6b1a3c52, 0x8e0f, 0x4c7b, 0x9d, 0x2a, 0x5f, 0x3e, 0x1b, 0x7c, 0x9a, 0x40);
#endif

#define OSMSPAD_PROTOCOL_VERSION 1u
#define OSMSPAD_MAX_PADS         16u
#define OSMSPAD_SESSION_CALLER   0xFFFFFFFFu   // sesión de Windows de quien crea el mando

#define OSMSPAD_DEVICE_TYPE 0x8A5Bu
#define IOCTL_OSMSPAD_CREATE   CTL_CODE(OSMSPAD_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)
#define IOCTL_OSMSPAD_INPUT    CTL_CODE(OSMSPAD_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_OSMSPAD_DESTROY  CTL_CODE(OSMSPAD_DEVICE_TYPE, 0x803, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_OSMSPAD_FEEDBACK CTL_CODE(OSMSPAD_DEVICE_TYPE, 0x804, METHOD_BUFFERED, FILE_READ_DATA)

enum OsmsPadProfile : uint32_t {
    OSMSPAD_PROFILE_XBOX_SERIES = 1,   // HID + xinputhid: lo ven XInput, GameInput, DirectInput y Steam
};

// Botones con los mismos bits que XINPUT_GAMEPAD / XUSB_REPORT (lo que ya usa Sunshine con ViGEm).
enum OsmsPadButton : uint16_t {
    OSMSPAD_DPAD_UP        = 0x0001,
    OSMSPAD_DPAD_DOWN      = 0x0002,
    OSMSPAD_DPAD_LEFT      = 0x0004,
    OSMSPAD_DPAD_RIGHT     = 0x0008,
    OSMSPAD_START          = 0x0010,
    OSMSPAD_BACK           = 0x0020,
    OSMSPAD_LEFT_THUMB     = 0x0040,
    OSMSPAD_RIGHT_THUMB    = 0x0080,
    OSMSPAD_LEFT_SHOULDER  = 0x0100,
    OSMSPAD_RIGHT_SHOULDER = 0x0200,
    OSMSPAD_GUIDE          = 0x0400,
    OSMSPAD_SHARE          = 0x0800,   // no existe en XUSB; aquí es el botón Compartir del mando Series
    OSMSPAD_A              = 0x1000,
    OSMSPAD_B              = 0x2000,
    OSMSPAD_X              = 0x4000,
    OSMSPAD_Y              = 0x8000,
};

#pragma pack(push, 1)

struct OsmsPadCreateIn {
    uint32_t Version;        // OSMSPAD_PROTOCOL_VERSION
    uint32_t Profile;        // OsmsPadProfile
    uint32_t TargetSession;  // sesión de Windows del mando, u OSMSPAD_SESSION_CALLER
};

struct OsmsPadCreateOut {
    uint32_t Pad;            // 0..OSMSPAD_MAX_PADS-1
    uint32_t Session;        // sesión que quedó en el ID de instancia
    wchar_t  InstanceId[48]; // ID de instancia del nodo VHF (OSMS_S<sesión>_P<pad>)
};

struct OsmsPadInput {
    uint32_t Pad;
    uint16_t Buttons;        // OsmsPadButton
    uint8_t  LeftTrigger;    // 0..255
    uint8_t  RightTrigger;
    int16_t  ThumbLX;        // como XInput: -32768..32767, positivo = derecha / arriba
    int16_t  ThumbLY;
    int16_t  ThumbRX;
    int16_t  ThumbRY;
};

struct OsmsPadDestroyIn {
    uint32_t Pad;
};

// Vibración que pidió el juego, 0..65535 por motor (como XINPUT_VIBRATION + gatillos).
struct OsmsPadFeedback {
    uint32_t Pad;
    uint16_t LargeMotor;     // izquierdo, baja frecuencia
    uint16_t SmallMotor;     // derecho, alta frecuencia
    uint16_t LeftTrigger;
    uint16_t RightTrigger;
};

#pragma pack(pop)

static_assert(sizeof(OsmsPadCreateIn) == 12, "OsmsPadCreateIn");
static_assert(sizeof(OsmsPadCreateOut) == 104, "OsmsPadCreateOut");
static_assert(sizeof(OsmsPadInput) == 16, "OsmsPadInput");
static_assert(sizeof(OsmsPadFeedback) == 12, "OsmsPadFeedback");
