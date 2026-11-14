// XboxSeriesTest.cpp — prueba de XboxSeries.cpp (conversión de informes), sin driver ni WDK.
#include <stdio.h>
#include <string.h>

#include "../XboxSeries.h"

static int g_failures = 0;
static void Check(bool ok, const char* what)
{
    printf("%s %s\n", ok ? "OK  " : "FALLO", what);
    if (!ok) ++g_failures;
}

int main()
{
    using namespace osmspad;

    size_t size = 0;
    const uint8_t* d = XboxSeriesDescriptor(&size);
    Check(d && size > 100 && d[0] == 0x05 && d[size - 1] == 0xC0, "descriptor empieza y acaba bien");

    size_t bytes = 0;
    const wchar_t* ids = XboxSeriesHardwareIds(&bytes);
    Check(wcscmp(ids, L"HID\\VID_045E&PID_0B12&IG_00") == 0, "primer ID: el que casa con xinputhid.inf");
    Check(bytes % sizeof(wchar_t) == 0 && ids[bytes / sizeof(wchar_t) - 1] == 0 && ids[bytes / sizeof(wchar_t) - 2] == 0,
          "IDs en REG_MULTI_SZ con doble terminador");

    OsmsPadInput idle{};
    XboxInputReport r = EncodeXboxInput(idle);
    Check(r.ReportId == 1 && r.LeftX == 32768 && r.LeftY == 32767 && r.Hat == 0 && r.Buttons == 0,
          "en reposo: sticks al centro, cruceta centrada, sin botones");

    OsmsPadInput in{};
    in.ThumbLX = 32767; in.ThumbLY = 32767;       // derecha y arriba
    in.ThumbRX = -32768; in.ThumbRY = -32768;     // izquierda y abajo
    in.LeftTrigger = 255; in.RightTrigger = 0;
    in.Buttons = OSMSPAD_A | OSMSPAD_Y | OSMSPAD_GUIDE | OSMSPAD_DPAD_UP | OSMSPAD_DPAD_RIGHT | OSMSPAD_SHARE;
    r = EncodeXboxInput(in);
    Check(r.LeftX == 65535 && r.LeftY == 0, "stick izquierdo arriba-derecha (vertical invertido)");
    Check(r.RightX == 0 && r.RightY == 65535, "stick derecho abajo-izquierda");
    Check(r.LeftTrigger == 1023 && r.RightTrigger == 0, "gatillos a 10 bits");
    Check(r.Hat == 2, "cruceta arriba+derecha = 2");
    Check(r.Buttons == ((1u << 0) | (1u << 4) | (1u << 12)), "A, Y y Guía en sus bits del mando real");
    Check(r.Share == 1, "Compartir");

    in = {}; in.Buttons = OSMSPAD_DPAD_UP | OSMSPAD_DPAD_DOWN;
    Check(EncodeXboxInput(in).Hat == 0, "arriba+abajo a la vez = centro");

    uint8_t rumble[9] = { 3, 0x0C, 0, 0, 100, 50, 0, 0, 0 };
    OsmsPadFeedback fb{};
    Check(DecodeXboxRumble(rumble, sizeof rumble, &fb), "vibración: informe 3 reconocido");
    Check(fb.LargeMotor == 65535 && fb.SmallMotor == 32767 && fb.LeftTrigger == 0 && fb.RightTrigger == 0,
          "vibración: motores al 100 % y 50 %, gatillos sin su bit = 0");
    rumble[0] = 2;
    Check(!DecodeXboxRumble(rumble, sizeof rumble, &fb), "otro informe: se ignora");
    Check(!DecodeXboxRumble(rumble, 4, &fb), "informe corto: se ignora");

    printf(g_failures ? "\n%d fallos\n" : "\nTodo bien\n", g_failures);
    return g_failures ? 1 : 0;
}
