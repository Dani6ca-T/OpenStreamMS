// OsmsPadSetup.cpp — crea o quita el dispositivo raíz ROOT\OSMSPAD (lo que haría devcon).
//   OsmsPadSetup.exe install <ruta\OsmsPad.inf>   crea el nodo si no existe y le instala el driver
//   OsmsPadSetup.exe remove                       quita los nodos ROOT\OSMSPAD y el paquete del driver
// Códigos de salida: 0 bien, 3010 bien pero hay que reiniciar, otro = error de Windows.
#include <windows.h>
#include <setupapi.h>
#include <newdev.h>
#include <devguid.h>
#include <stdio.h>
#include <string>
#include <vector>

#ifndef MAX_CLASS_NAME_LEN
#define MAX_CLASS_NAME_LEN 32
#endif

static const wchar_t kHardwareId[] = L"ROOT\\OSMSPAD";

static bool HasHardwareId(HDEVINFO set, SP_DEVINFO_DATA& info)
{
    wchar_t ids[512]{};
    if (!SetupDiGetDeviceRegistryPropertyW(set, &info, SPDRP_HARDWAREID, nullptr,
                                           reinterpret_cast<BYTE*>(ids), sizeof ids - 2 * sizeof(wchar_t), nullptr))
        return false;
    for (const wchar_t* p = ids; *p; p += wcslen(p) + 1)
        if (_wcsicmp(p, kHardwareId) == 0) return true;
    return false;
}

// Recorre los nodos de la clase System con nuestro ID (presentes o no).
template <typename F> static void ForEachOsmsPad(F f)
{
    HDEVINFO set = SetupDiGetClassDevsW(&GUID_DEVCLASS_SYSTEM, L"ROOT", nullptr, 0);
    if (set == INVALID_HANDLE_VALUE) return;
    SP_DEVINFO_DATA info{ sizeof info };
    for (DWORD i = 0; SetupDiEnumDeviceInfo(set, i, &info); ++i)
        if (HasHardwareId(set, info)) f(set, info);
    SetupDiDestroyDeviceInfoList(set);
}

static int Install(const wchar_t* inf)
{
    wchar_t full[MAX_PATH];
    if (!GetFullPathNameW(inf, MAX_PATH, full, nullptr)) return static_cast<int>(GetLastError());

    bool exists = false;
    ForEachOsmsPad([&](HDEVINFO, SP_DEVINFO_DATA&) { exists = true; });
    if (!exists)
    {
        GUID cls;
        wchar_t className[MAX_CLASS_NAME_LEN];
        if (!SetupDiGetINFClassW(full, &cls, className, MAX_CLASS_NAME_LEN, nullptr))
            return static_cast<int>(GetLastError());
        HDEVINFO set = SetupDiCreateDeviceInfoList(&cls, nullptr);
        if (set == INVALID_HANDLE_VALUE) return static_cast<int>(GetLastError());
        SP_DEVINFO_DATA info{ sizeof info };
        wchar_t ids[] = L"ROOT\\OSMSPAD\0";
        bool ok = SetupDiCreateDeviceInfoW(set, className, &cls, nullptr, nullptr, DICD_GENERATE_ID, &info) &&
                  SetupDiSetDeviceRegistryPropertyW(set, &info, SPDRP_HARDWAREID, reinterpret_cast<BYTE*>(ids), sizeof ids) &&
                  SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, &info);
        DWORD err = ok ? 0 : GetLastError();
        SetupDiDestroyDeviceInfoList(set);
        if (!ok) { wprintf(L"No se pudo crear ROOT\\OSMSPAD: %lu\n", err); return static_cast<int>(err); }
        wprintf(L"Creado el dispositivo ROOT\\OSMSPAD\n");
    }

    BOOL reboot = FALSE;
    if (!UpdateDriverForPlugAndPlayDevicesW(nullptr, kHardwareId, full, INSTALLFLAG_FORCE, &reboot))
    {
        DWORD err = GetLastError();
        wprintf(L"No se pudo instalar el driver (%lu)%s\n", err,
                err == ERROR_AUTHENTICODE_TRUST_NOT_ESTABLISHED || err == static_cast<DWORD>(TRUST_E_NOSIGNATURE)
                    ? L": firma no aceptada (¿certificado en Root y TrustedPublisher?)" : L"");
        return static_cast<int>(err);
    }
    wprintf(L"Driver instalado%s\n", reboot ? L" (hace falta reiniciar)" : L"");
    return reboot ? 3010 : 0;
}

static int Remove()
{
    std::vector<std::wstring> infs;
    int removed = 0;
    ForEachOsmsPad([&](HDEVINFO set, SP_DEVINFO_DATA& info) {
        // INF instalado (oemNN.inf) para quitar luego el paquete del Driver Store.
        HKEY key = SetupDiOpenDevRegKey(set, &info, DICS_FLAG_GLOBAL, 0, DIREG_DRV, KEY_READ);
        if (key != INVALID_HANDLE_VALUE)
        {
            wchar_t inf[MAX_PATH]{};
            DWORD size = sizeof inf - sizeof(wchar_t);
            if (RegQueryValueExW(key, L"InfPath", nullptr, nullptr, reinterpret_cast<BYTE*>(inf), &size) == ERROR_SUCCESS)
                infs.emplace_back(inf);
            RegCloseKey(key);
        }
        BOOL reboot = FALSE;
        if (DiUninstallDevice(nullptr, set, &info, 0, &reboot)) ++removed;
    });
    for (const auto& inf : infs)
        if (SetupUninstallOEMInfW(inf.c_str(), SUOI_FORCEDELETE, nullptr))
            wprintf(L"Quitado el paquete %ls\n", inf.c_str());
    wprintf(L"%d dispositivo(s) ROOT\\OSMSPAD quitado(s)\n", removed);
    return 0;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 3 && _wcsicmp(argv[1], L"install") == 0) return Install(argv[2]);
    if (argc == 2 && _wcsicmp(argv[1], L"remove") == 0) return Remove();
    wprintf(L"Uso: OsmsPadSetup.exe install <OsmsPad.inf> | remove\n");
    return ERROR_INVALID_PARAMETER;
}
