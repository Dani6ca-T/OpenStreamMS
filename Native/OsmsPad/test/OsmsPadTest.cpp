// OsmsPadTest.cpp — prueba de punta a punta con el driver instalado (como administrador):
//   1) abre la interfaz de OsmsPad y crea un mando Xbox Series;
//   2) espera a que XInput lo vea y comprueba botones, sticks y gatillos;
//   3) pide vibración con XInputSetState y comprueba que llega por IOCTL_OSMSPAD_FEEDBACK;
//   4) cierra el handle y comprueba que el mando desaparece.
// Uso: OsmsPadTest.exe [--keep N]   (--keep: deja el mando N segundos para probar un juego)
#include <windows.h>
#include <cfgmgr32.h>
#include <initguid.h>
#include <xinput.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>

#include "../OsmsPadIoctl.h"

static int g_failures = 0;
static void Check(bool ok, const char* what)
{
    printf("%s %s\n", ok ? "OK  " : "FALLO", what);
    if (!ok) ++g_failures;
}

static HANDLE OpenOsmsPad()
{
    ULONG len = 0;
    if (CM_Get_Device_Interface_List_SizeW(&len, const_cast<GUID*>(&GUID_DEVINTERFACE_OSMSPAD), nullptr,
                                           CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len <= 1)
        return INVALID_HANDLE_VALUE;
    std::vector<wchar_t> list(len);
    if (CM_Get_Device_Interface_ListW(const_cast<GUID*>(&GUID_DEVINTERFACE_OSMSPAD), nullptr, list.data(), len,
                                      CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
        return INVALID_HANDLE_VALUE;
    return CreateFileW(list.data(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                       FILE_FLAG_OVERLAPPED, nullptr);
}

static bool Ioctl(HANDLE h, DWORD code, const void* in, DWORD inLen, void* out, DWORD outLen, DWORD* got = nullptr)
{
    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    DWORD n = 0;
    BOOL ok = DeviceIoControl(h, code, const_cast<void*>(in), inLen, out, outLen, &n, &ov);
    if (!ok && GetLastError() == ERROR_IO_PENDING) ok = GetOverlappedResult(h, &ov, &n, TRUE);
    CloseHandle(ov.hEvent);
    if (got) *got = n;
    return ok != FALSE;
}

// XInput: índice cuyo estado coincide con lo que mandamos (puede haber mandos reales).
static int FindPad(WORD buttons, SHORT lx, DWORD timeoutMs)
{
    for (DWORD waited = 0; waited <= timeoutMs; waited += 50)
    {
        for (DWORD i = 0; i < XUSER_MAX_COUNT; ++i)
        {
            XINPUT_STATE s{};
            if (XInputGetState(i, &s) == ERROR_SUCCESS &&
                (s.Gamepad.wButtons & buttons) == buttons && abs(s.Gamepad.sThumbLX - lx) < 512)
                return static_cast<int>(i);
        }
        Sleep(50);
    }
    return -1;
}

int main(int argc, char** argv)
{
    DWORD keepSeconds = 0;
    if (argc == 3 && strcmp(argv[1], "--keep") == 0) keepSeconds = static_cast<DWORD>(atoi(argv[2]));

    HANDLE h = OpenOsmsPad();
    Check(h != INVALID_HANDLE_VALUE, "abrir la interfaz de OsmsPad (¿driver instalado? ¿administrador?)");
    if (h == INVALID_HANDLE_VALUE) return 1;

    OsmsPadCreateIn create{ OSMSPAD_PROTOCOL_VERSION, OSMSPAD_PROFILE_XBOX_SERIES, OSMSPAD_SESSION_CALLER };
    OsmsPadCreateOut created{};
    DWORD sessionId = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &sessionId);
    bool ok = Ioctl(h, IOCTL_OSMSPAD_CREATE, &create, sizeof create, &created, sizeof created);
    Check(ok, "crear mando");
    if (!ok) { printf("  error %lu\n", GetLastError()); return 1; }
    printf("     mando %u, %ls\n", created.Pad, created.InstanceId);
    Check(created.Session == sessionId, "la sesión del ID de instancia es la de quien lo crea");

    OsmsPadInput in{};
    in.Pad = created.Pad;
    in.Buttons = OSMSPAD_A | OSMSPAD_LEFT_SHOULDER;
    in.ThumbLX = 20000;
    in.RightTrigger = 200;
    int index = -1;
    for (int tries = 0; tries < 60 && index < 0; ++tries)   // hasta ~6 s: Windows tarda en montar el HID
    {
        Ioctl(h, IOCTL_OSMSPAD_INPUT, &in, sizeof in, nullptr, 0);
        index = FindPad(in.Buttons, in.ThumbLX, 100);
    }
    Check(index >= 0, "XInput ve el mando con A + LB y el stick izquierdo a la derecha");
    if (index >= 0)
    {
        XINPUT_STATE s{};
        XInputGetState(static_cast<DWORD>(index), &s);
        printf("     XInput %d: botones 0x%04X, LX %d, RT %u\n", index, s.Gamepad.wButtons, s.Gamepad.sThumbLX,
               s.Gamepad.bRightTrigger);
        Check(s.Gamepad.bRightTrigger > 150 && s.Gamepad.bRightTrigger < 250, "gatillo derecho ~200");

        // Vibración: la petición de feedback queda pendiente hasta que el "juego" la pida.
        OsmsPadFeedback fb{};
        OVERLAPPED ov{};
        ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        DWORD n = 0;
        BOOL pending = DeviceIoControl(h, IOCTL_OSMSPAD_FEEDBACK, nullptr, 0, &fb, sizeof fb, &n, &ov);
        if (!pending && GetLastError() != ERROR_IO_PENDING) printf("     feedback: error %lu\n", GetLastError());
        XINPUT_VIBRATION vib{ 65535, 32768 };
        XInputSetState(static_cast<DWORD>(index), &vib);
        bool got = WaitForSingleObject(ov.hEvent, 2000) == WAIT_OBJECT_0 && GetOverlappedResult(h, &ov, &n, FALSE);
        Check(got && fb.Pad == created.Pad && fb.LargeMotor > 60000 && fb.SmallMotor > 25000 && fb.SmallMotor < 40000,
              "la vibración que pide XInput llega al cliente");
        printf("     vibración: grande %u, pequeño %u\n", fb.LargeMotor, fb.SmallMotor);
        if (!got) CancelIoEx(h, &ov);
        CloseHandle(ov.hEvent);
        XINPUT_VIBRATION off{};
        XInputSetState(static_cast<DWORD>(index), &off);
    }

    if (keepSeconds)
    {
        printf("     mando creado durante %lu s: abre un juego o joy.cpl para probarlo\n", keepSeconds);
        Sleep(keepSeconds * 1000);
    }

    CloseHandle(h);
    if (index >= 0)
    {
        bool gone = false;
        for (int i = 0; i < 60 && !gone; ++i)
        {
            XINPUT_STATE s{};
            gone = XInputGetState(static_cast<DWORD>(index), &s) == ERROR_DEVICE_NOT_CONNECTED;
            if (!gone) Sleep(100);
        }
        Check(gone, "al cerrar el handle el mando desaparece");
    }

    printf(g_failures ? "\n%d fallos\n" : "\nTodo bien\n", g_failures);
    return g_failures ? 1 : 0;
}
