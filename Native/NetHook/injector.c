// OpenStreamMS NetInject — carga osms-nethook.dll (de su misma carpeta) en un proceso.
//
//   osms-netinject.exe <pid>             inyecta en ese proceso (misma arquitectura que este exe;
//                                        lo usa la DLL para los hijos de la otra arquitectura)
//   osms-netinject.exe --run <orden...>  arranca la orden con la DLL cargada y espera a que acabe
//                                        (devuelve su código de salida); para pruebas
//
// Códigos: 0 bien, 1 fallo al inyectar, 2 uso, 3 no se pudo abrir/arrancar el proceso.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>
#include <stdlib.h>

typedef BOOL (WINAPI *OsmsNetInject_t)(HANDLE, LPCWSTR);
typedef BOOL (WINAPI *OsmsNetInjectAny_t)(HANDLE);

static HMODULE LoadHook(WCHAR* dll)
{
    GetModuleFileNameW(NULL, dll, MAX_PATH);
    WCHAR* slash = wcsrchr(dll, L'\\');
    wcscpy_s(slash ? slash + 1 : dll, MAX_PATH - (slash ? (slash + 1 - dll) : 0), L"osms-nethook.dll");
    return LoadLibraryW(dll);   // en este exe no engancha nada (está excluido): solo exporta el inyector
}

static LPWSTR ArgsAfterRun(void)
{
    LPWSTR p = wcsstr(GetCommandLineW(), L"--run");
    if (!p) return NULL;
    p += 5;
    while (*p == L' ' || *p == L'\t') p++;
    return *p ? p : NULL;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2) return 2;
    WCHAR dll[MAX_PATH];
    HMODULE hook = LoadHook(dll);
    if (!hook) return 1;

    if (_wcsicmp(argv[1], L"--run") == 0)
    {
        LPWSTR args = ArgsAfterRun();
        if (!args) return 2;
        static WCHAR cmd[32768];
        wcscpy_s(cmd, ARRAYSIZE(cmd), args);
        STARTUPINFOW si = { sizeof si };
        PROCESS_INFORMATION pi;
        if (!CreateProcessW(NULL, cmd, NULL, NULL, TRUE, CREATE_SUSPENDED, NULL, NULL, &si, &pi)) return 3;
        OsmsNetInjectAny_t injectAny = (OsmsNetInjectAny_t)GetProcAddress(hook, "OsmsNetInjectAny");
        if (!injectAny || !injectAny(pi.hProcess))
        {
            TerminateProcess(pi.hProcess, 1);
            return 1;
        }
        ResumeThread(pi.hThread);
        WaitForSingleObject(pi.hProcess, INFINITE);
        DWORD code = 0;
        GetExitCodeProcess(pi.hProcess, &code);
        return (int)code;
    }

    DWORD pid = wcstoul(argv[1], NULL, 10);
    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION
                                 | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!process) return 3;
    OsmsNetInject_t inject = (OsmsNetInject_t)GetProcAddress(hook, "OsmsNetInject");
    BOOL ok = inject && inject(process, dll);
    CloseHandle(process);
    return ok ? 0 : 1;
}
