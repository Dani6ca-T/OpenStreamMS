// OpenStreamMS NetHook — red propia por sesión (x64 y x86).
//
// Windows tiene una sola pila de red para todo el PC: las tarjetas, las IPs y las rutas son las
// mismas en todas las sesiones, y un programa que liga a "cualquier dirección" (0.0.0.0) sale por
// donde diga la tabla de rutas, nunca por sesión. Esta DLL se carga en los programas de una sesión
// (se pasa a cada proceso hijo) y hace que esa sesión parezca otro equipo:
//
//   - Los sockets IPv4 sin dirección (bind a 0.0.0.0, o connect/sendto sin bind) se ligan a la IP
//     de la tarjeta de la sesión. Con la IP de origen fija, Windows (modelo de host fuerte) envía
//     por esa tarjeta: también las difusiones.
//   - Con "ocultar" activo, el programa solo ve la tarjeta de la sesión, el loopback y las que se
//     permitan aparte (p. ej. una ZeroTier propia de la sesión): GetAdaptersAddresses,
//     GetAdaptersInfo, las tablas MIB y la resolución del nombre del equipo se filtran, y un bind a
//     la IP de una tarjeta oculta se pasa a la de la sesión.
//
// Es un aislamiento en modo usuario, no de kernel: un programa que no pase por estas funciones (o
// que no reciba la DLL: elevados por UAC, servicios, procesos que lanza COM) usa la red del PC.
//
// Configuración (la escribe el servicio por sesión de Windows):
//   HKLM\SOFTWARE\OpenStreamMS\NetSessions\<id de sesión>
//     Interface  REG_SZ        nombre o GUID de la tarjeta de la sesión ("vEthernet (OSMS 3)")
//     HideOthers REG_DWORD     1 = ocultar el resto de tarjetas (por defecto), 0 = verlas todas
//     AlsoShow   REG_MULTI_SZ  tarjetas que se ven y no se fuerzan (comodines; nombre o descripción)
//     Exclude    REG_MULTI_SZ  ejecutables que no reciben la DLL (comodines: juegos con antitrampas)
// Sin esa clave, para pruebas: OSMS_NETHOOK_INTERFACE, OSMS_NETHOOK_HIDE (0/1),
// OSMS_NETHOOK_ALSOSHOW y OSMS_NETHOOK_EXCLUDE (listas separadas por ';').

#define WIN32_LEAN_AND_MEAN
#define _WINSOCK_DEPRECATED_NO_WARNINGS
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <shlwapi.h>
#include <psapi.h>
#include <wchar.h>
#include <stdio.h>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "advapi32.lib")

#ifdef _WIN64
#define OTHER_ARCH L"x86"
#else
#define OTHER_ARCH L"x64"
#endif

// Siempre fuera: el streaming y el propio OpenStreamMS usan la red del PC.
static const WCHAR kAlwaysExcluded[] = L"sunshine*.exe;OpenStreamMS*.exe;osms-netinject.exe";

static HMODULE g_self;

// ── Configuración ─────────────────────────────────────────────────────────────
static struct {
    BOOL  active;
    WCHAR iface[256];
    BOOL  hide;
    WCHAR alsoShow[1024];   // patrones separados por ';'
    WCHAR exclude[1024];
} g_cfg;

static void Log(const char* fmt, ...)
{
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    int n = _snprintf_s(buf, sizeof buf, _TRUNCATE, "[osms-nethook %lu] ", GetCurrentProcessId());
    _vsnprintf_s(buf + n, sizeof buf - n, _TRUNCATE, fmt, ap);
    va_end(ap);
    OutputDebugStringA(buf);
}

// REG_MULTI_SZ → "a;b;c"
static void ReadMulti(HKEY key, LPCWSTR name, WCHAR* out, DWORD cch)
{
    WCHAR buf[1024];
    DWORD size = sizeof buf;
    out[0] = 0;
    if (RegGetValueW(key, NULL, name, RRF_RT_REG_MULTI_SZ | RRF_RT_REG_SZ, NULL, buf, &size) != ERROR_SUCCESS) return;
    for (WCHAR* p = buf; *p; p += wcslen(p) + 1)
    {
        if (out[0]) wcscat_s(out, cch, L";");
        wcscat_s(out, cch, p);
    }
}

static BOOL ReadConfig(void)
{
    DWORD session = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &session);
    WCHAR path[96];
    swprintf_s(path, ARRAYSIZE(path), L"SOFTWARE\\OpenStreamMS\\NetSessions\\%lu", session);

    HKEY key;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, path, 0, KEY_READ | KEY_WOW64_64KEY, &key) == ERROR_SUCCESS)
    {
        DWORD size = sizeof g_cfg.iface, hide = 1, cb = sizeof hide;
        RegGetValueW(key, NULL, L"Interface", RRF_RT_REG_SZ, NULL, g_cfg.iface, &size);
        if (RegGetValueW(key, NULL, L"HideOthers", RRF_RT_REG_DWORD, NULL, &hide, &cb) != ERROR_SUCCESS) hide = 1;
        g_cfg.hide = hide != 0;
        ReadMulti(key, L"AlsoShow", g_cfg.alsoShow, ARRAYSIZE(g_cfg.alsoShow));
        ReadMulti(key, L"Exclude", g_cfg.exclude, ARRAYSIZE(g_cfg.exclude));
        RegCloseKey(key);
    }
    else if (GetEnvironmentVariableW(L"OSMS_NETHOOK_INTERFACE", g_cfg.iface, ARRAYSIZE(g_cfg.iface)))
    {
        WCHAR hide[8] = L"1";
        GetEnvironmentVariableW(L"OSMS_NETHOOK_HIDE", hide, ARRAYSIZE(hide));
        g_cfg.hide = hide[0] != L'0';
        GetEnvironmentVariableW(L"OSMS_NETHOOK_ALSOSHOW", g_cfg.alsoShow, ARRAYSIZE(g_cfg.alsoShow));
        GetEnvironmentVariableW(L"OSMS_NETHOOK_EXCLUDE", g_cfg.exclude, ARRAYSIZE(g_cfg.exclude));
    }
    return g_cfg.iface[0] != 0;
}

// ¿name encaja con algún patrón de la lista "a;b;c"?
static BOOL MatchesList(LPCWSTR name, LPCWSTR list)
{
    if (!name || !*name || !list || !*list) return FALSE;
    WCHAR buf[1024];
    wcscpy_s(buf, ARRAYSIZE(buf), list);
    WCHAR* ctx = NULL;
    for (WCHAR* tok = wcstok_s(buf, L";", &ctx); tok; tok = wcstok_s(NULL, L";", &ctx))
    {
        while (*tok == L' ') tok++;
        if (*tok && PathMatchSpecW(name, tok)) return TRUE;
    }
    return FALSE;
}

static BOOL IsExcludedExe(LPCWSTR exePath)
{
    LPCWSTR slash = wcsrchr(exePath, L'\\');
    LPCWSTR name = slash ? slash + 1 : exePath;
    return MatchesList(name, kAlwaysExcluded) || MatchesList(name, g_cfg.exclude);
}

// ── Estado de las tarjetas ────────────────────────────────────────────────────
#define MAX_IFS   64
#define MAX_ADDRS 128

typedef struct { ULONG64 luid; IF_INDEX index; BOOL visible; BOOL forced; } IfState;   // forced: el tráfico va por la sesión
typedef struct { int family; IN_ADDR v4; IN6_ADDR v6; IF_INDEX index; } LocalAddr;

static SRWLOCK   g_lock = SRWLOCK_INIT;
static DWORD     g_stamp;
static BOOL      g_loaded;
static IfState   g_ifs[MAX_IFS];
static int       g_nifs;
static LocalAddr g_addrs[MAX_ADDRS];
static int       g_naddrs;
static BOOL      g_haveIp;
static IN_ADDR   g_sessionIp;
static IF_INDEX  g_sessionIndex;

static void Refresh(void)
{
    ULONG size = 32 * 1024;
    IP_ADAPTER_ADDRESSES* list = NULL;
    ULONG rc = ERROR_BUFFER_OVERFLOW;
    for (int tries = 0; tries < 3 && rc == ERROR_BUFFER_OVERFLOW; tries++)
    {
        HeapFree(GetProcessHeap(), 0, list);
        list = (IP_ADAPTER_ADDRESSES*)HeapAlloc(GetProcessHeap(), 0, size);
        if (!list) return;
        rc = GetAdaptersAddresses(AF_UNSPEC, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
                                  NULL, list, &size);
    }

    IfState ifs[MAX_IFS];
    LocalAddr addrs[MAX_ADDRS];
    int nifs = 0, naddrs = 0;
    BOOL haveIp = FALSE;
    IN_ADDR ip = { 0 };
    IF_INDEX sessionIndex = 0;

    if (rc == NO_ERROR)
    {
        for (IP_ADAPTER_ADDRESSES* a = list; a && nifs < MAX_IFS; a = a->Next)
        {
            WCHAR guid[MAX_ADAPTER_NAME_LENGTH + 4];
            MultiByteToWideChar(CP_ACP, 0, a->AdapterName, -1, guid, ARRAYSIZE(guid));
            BOOL session = _wcsicmp(a->FriendlyName, g_cfg.iface) == 0 || _wcsicmp(guid, g_cfg.iface) == 0;
            BOOL loopback = a->IfType == IF_TYPE_SOFTWARE_LOOPBACK;
            BOOL also = MatchesList(a->FriendlyName, g_cfg.alsoShow) || MatchesList(a->Description, g_cfg.alsoShow);

            IfState* s = &ifs[nifs++];
            s->luid = a->Luid.Value;
            s->index = a->IfIndex ? a->IfIndex : a->Ipv6IfIndex;
            s->visible = session || loopback || also || !g_cfg.hide;
            s->forced = !(loopback || also || session);

            for (IP_ADAPTER_UNICAST_ADDRESS* u = a->FirstUnicastAddress; u && naddrs < MAX_ADDRS; u = u->Next)
            {
                LocalAddr* l = &addrs[naddrs++];
                l->family = u->Address.lpSockaddr->sa_family;
                l->index = s->index;
                if (l->family == AF_INET)
                {
                    l->v4 = ((SOCKADDR_IN*)u->Address.lpSockaddr)->sin_addr;
                    if (session && !haveIp && u->DadState == IpDadStatePreferred) { ip = l->v4; haveIp = TRUE; }
                }
                else if (l->family == AF_INET6)
                    l->v6 = ((SOCKADDR_IN6*)u->Address.lpSockaddr)->sin6_addr;
            }
            if (session) sessionIndex = s->index;
        }
    }
    HeapFree(GetProcessHeap(), 0, list);
    if (rc != NO_ERROR) return;   // se mantiene lo último conocido

    AcquireSRWLockExclusive(&g_lock);
    memcpy(g_ifs, ifs, nifs * sizeof *ifs);
    g_nifs = nifs;
    memcpy(g_addrs, addrs, naddrs * sizeof *addrs);
    g_naddrs = naddrs;
    if (haveIp != g_haveIp || ip.s_addr != g_sessionIp.s_addr)
    {
        char txt[32];
        inet_ntop(AF_INET, &ip, txt, sizeof txt);
        Log("tarjeta \"%ls\": %s", g_cfg.iface, haveIp ? txt : "sin IPv4");
    }
    g_haveIp = haveIp;
    g_sessionIp = ip;
    g_sessionIndex = sessionIndex;
    g_loaded = TRUE;
    ReleaseSRWLockExclusive(&g_lock);
}

// La IP de la tarjeta puede cambiar (DHCP): se relee como mucho cada 2 s.
static void EnsureFresh(void)
{
    DWORD now = GetTickCount();
    if (g_loaded && now - g_stamp < 2000) return;
    g_stamp = now;
    DWORD err = GetLastError();
    int wsa = WSAGetLastError();
    Refresh();
    WSASetLastError(wsa);
    SetLastError(err);
}

static BOOL SessionIp(IN_ADDR* out)
{
    EnsureFresh();
    AcquireSRWLockShared(&g_lock);
    BOOL ok = g_haveIp;
    if (ok) *out = g_sessionIp;
    ReleaseSRWLockShared(&g_lock);
    return ok;
}

static const IfState* FindIf(ULONG64 luid, IF_INDEX index)   // con el candado tomado
{
    for (int i = 0; i < g_nifs; i++)
        if ((luid && g_ifs[i].luid == luid) || (!luid && index && g_ifs[i].index == index)) return &g_ifs[i];
    return NULL;
}

// Interfaces que no salen en GetAdaptersAddresses (filtros, pseudo-interfaces): ocultas.
static BOOL VisibleIf(ULONG64 luid, IF_INDEX index)
{
    if (!g_cfg.hide) return TRUE;
    EnsureFresh();
    AcquireSRWLockShared(&g_lock);
    const IfState* s = FindIf(luid, index);
    BOOL v = s && s->visible;
    ReleaseSRWLockShared(&g_lock);
    return v;
}

static BOOL VisibleLuid(NET_LUID luid) { return VisibleIf(luid.Value, 0); }
static BOOL VisibleIndex(IF_INDEX index) { return VisibleIf(0, index); }

// ¿addr es una IP local de una tarjeta oculta?
static BOOL IsHiddenLocal(const SOCKADDR* sa)
{
    if (!g_cfg.hide || !sa) return FALSE;
    EnsureFresh();
    BOOL hidden = FALSE;
    AcquireSRWLockShared(&g_lock);
    for (int i = 0; i < g_naddrs && !hidden; i++)
    {
        const LocalAddr* l = &g_addrs[i];
        if (l->family != sa->sa_family) continue;
        BOOL same = sa->sa_family == AF_INET
            ? l->v4.s_addr == ((const SOCKADDR_IN*)sa)->sin_addr.s_addr
            : memcmp(&l->v6, &((const SOCKADDR_IN6*)sa)->sin6_addr, sizeof l->v6) == 0;
        if (same)
        {
            const IfState* s = FindIf(0, l->index);
            hidden = !s || !s->visible;
        }
    }
    ReleaseSRWLockShared(&g_lock);
    return hidden;
}

// ── Sockets ───────────────────────────────────────────────────────────────────
static BOOL IsUnbound(SOCKET s)
{
    SOCKADDR_STORAGE ss;
    int len = sizeof ss;
    return getsockname(s, (SOCKADDR*)&ss, &len) == SOCKET_ERROR && WSAGetLastError() == WSAEINVAL;
}

static BOOL IsDatagram(SOCKET s)
{
    int type = 0, len = sizeof type;
    return getsockopt(s, SOL_SOCKET, SO_TYPE, (char*)&type, &len) == 0 && type == SOCK_DGRAM;
}

// Tras ligar un socket UDP a la IP de la sesión, la multidifusión sale también por su tarjeta.
static void SetMulticastIf(SOCKET s, IN_ADDR ip)
{
    if (IsDatagram(s)) setsockopt(s, IPPROTO_IP, IP_MULTICAST_IF, (const char*)&ip, sizeof ip);
}

// El tráfico a una tarjeta permitida aparte (ZeroTier propia...) o al loopback no se fuerza.
static BOOL ShouldForce(const SOCKADDR* dest)
{
    if (!dest || dest->sa_family != AF_INET) return FALSE;
    const SOCKADDR_IN* d = (const SOCKADDR_IN*)dest;
    if ((ntohl(d->sin_addr.s_addr) >> 24) == 127) return FALSE;
    DWORD best = 0;
    if (GetBestInterfaceEx((SOCKADDR*)dest, &best) != NO_ERROR) return TRUE;
    EnsureFresh();
    AcquireSRWLockShared(&g_lock);
    const IfState* s = FindIf(0, best);
    BOOL force = !s || s->forced || best == g_sessionIndex;
    ReleaseSRWLockShared(&g_lock);
    return force;
}

// connect/sendto sin bind previo: Windows elegiría la IP por la tabla de rutas.
static void ImplicitBind(SOCKET s, const SOCKADDR* dest)
{
    IN_ADDR ip;
    if (!dest || dest->sa_family != AF_INET || !SessionIp(&ip)) return;
    int wsa = WSAGetLastError();
    if (IsUnbound(s) && ShouldForce(dest))
    {
        SOCKADDR_IN local = { 0 };
        local.sin_family = AF_INET;
        local.sin_addr = ip;
        if (bind(s, (SOCKADDR*)&local, sizeof local) == 0) SetMulticastIf(s, ip);
    }
    WSASetLastError(wsa);
}

// Un servidor de la sesión queda ligado a la IP de la sesión, no a 0.0.0.0: si el propio juego se
// conecta a 127.0.0.1:puerto no lo encontraría. Si nadie escucha en el loopback ni en 0.0.0.0 con
// ese puerto pero sí en la IP de la sesión, el destino pasa a ser la IP de la sesión.
static BOOL ListeningOn(BOOL tcp, DWORD addr, USHORT port)
{
    DWORD size = 0;
    void* table = NULL;
    DWORD rc;
    for (int tries = 0; tries < 3; tries++)
    {
        rc = tcp ? GetExtendedTcpTable(table, &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0)
                 : GetExtendedUdpTable(table, &size, FALSE, AF_INET, UDP_TABLE_OWNER_PID, 0);
        if (rc != ERROR_INSUFFICIENT_BUFFER) break;
        HeapFree(GetProcessHeap(), 0, table);
        table = HeapAlloc(GetProcessHeap(), 0, size);
        if (!table) return FALSE;
    }
    BOOL found = FALSE;
    if (rc == NO_ERROR && table)
    {
        if (tcp)
        {
            MIB_TCPTABLE_OWNER_PID* t = (MIB_TCPTABLE_OWNER_PID*)table;
            for (DWORD i = 0; i < t->dwNumEntries && !found; i++)
                found = (USHORT)t->table[i].dwLocalPort == port && t->table[i].dwLocalAddr == addr;
        }
        else
        {
            MIB_UDPTABLE_OWNER_PID* t = (MIB_UDPTABLE_OWNER_PID*)table;
            for (DWORD i = 0; i < t->dwNumEntries && !found; i++)
                found = (USHORT)t->table[i].dwLocalPort == port && t->table[i].dwLocalAddr == addr;
        }
    }
    HeapFree(GetProcessHeap(), 0, table);
    return found;
}

static const SOCKADDR* LoopbackToSession(SOCKET s, const SOCKADDR* dest, int len, SOCKADDR_IN* tmp)
{
    IN_ADDR ip;
    if (!dest || len < (int)sizeof(SOCKADDR_IN) || dest->sa_family != AF_INET) return dest;
    const SOCKADDR_IN* d = (const SOCKADDR_IN*)dest;
    if ((ntohl(d->sin_addr.s_addr) >> 24) != 127 || !SessionIp(&ip)) return dest;
    int wsa = WSAGetLastError();
    BOOL tcp = !IsDatagram(s);
    USHORT port = d->sin_port;   // en orden de red, igual que dwLocalPort
    BOOL redirect = !ListeningOn(tcp, d->sin_addr.s_addr, port) && !ListeningOn(tcp, INADDR_ANY, port)
                    && ListeningOn(tcp, ip.s_addr, port);
    WSASetLastError(wsa);
    if (!redirect) return dest;
    *tmp = *d;
    tmp->sin_addr = ip;
    return (const SOCKADDR*)tmp;
}

static int WSAAPI h_bind(SOCKET s, const struct sockaddr* name, int len)
{
    IN_ADDR ip;
    if (name && len >= (int)sizeof(SOCKADDR_IN) && name->sa_family == AF_INET)
    {
        const SOCKADDR_IN* in = (const SOCKADDR_IN*)name;
        if ((in->sin_addr.s_addr == INADDR_ANY || IsHiddenLocal(name)) && SessionIp(&ip))
        {
            SOCKADDR_IN local = *in;
            local.sin_addr = ip;
            int rc = bind(s, (SOCKADDR*)&local, sizeof local);
            if (rc == 0) SetMulticastIf(s, ip);
            return rc;
        }
    }
    return bind(s, name, len);
}

static int WSAAPI h_connect(SOCKET s, const struct sockaddr* name, int len)
{
    SOCKADDR_IN tmp;
    name = LoopbackToSession(s, name, len, &tmp);
    ImplicitBind(s, name);
    return connect(s, name, len);
}

static int WSAAPI h_WSAConnect(SOCKET s, const struct sockaddr* name, int len, LPWSABUF caller, LPWSABUF callee,
                               LPQOS sqos, LPQOS gqos)
{
    SOCKADDR_IN tmp;
    name = LoopbackToSession(s, name, len, &tmp);
    ImplicitBind(s, name);
    return WSAConnect(s, name, len, caller, callee, sqos, gqos);
}

static int WSAAPI h_sendto(SOCKET s, const char* buf, int n, int flags, const struct sockaddr* to, int tolen)
{
    SOCKADDR_IN tmp;
    to = LoopbackToSession(s, to, tolen, &tmp);
    ImplicitBind(s, to);
    return sendto(s, buf, n, flags, to, tolen);
}

static int WSAAPI h_WSASendTo(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD sent, DWORD flags, const struct sockaddr* to,
                              int tolen, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cr)
{
    SOCKADDR_IN tmp;
    to = LoopbackToSession(s, to, tolen, &tmp);
    ImplicitBind(s, to);
    return WSASendTo(s, bufs, count, sent, flags, to, tolen, ov, cr);
}

// Unirse a un grupo de multidifusión "en cualquier tarjeta" → en la de la sesión.
static int WSAAPI h_setsockopt(SOCKET s, int level, int opt, const char* val, int len)
{
    IN_ADDR ip;
    if (level == IPPROTO_IP && val && SessionIp(&ip))
    {
        if ((opt == IP_ADD_MEMBERSHIP || opt == IP_DROP_MEMBERSHIP) && len >= (int)sizeof(struct ip_mreq))
        {
            struct ip_mreq m = *(const struct ip_mreq*)val;
            if (m.imr_interface.s_addr == INADDR_ANY) m.imr_interface = ip;
            return setsockopt(s, level, opt, (const char*)&m, sizeof m);
        }
        if (opt == IP_MULTICAST_IF && len >= (int)sizeof(IN_ADDR) && ((const IN_ADDR*)val)->s_addr == INADDR_ANY)
            return setsockopt(s, level, opt, (const char*)&ip, sizeof ip);
    }
    return setsockopt(s, level, opt, val, len);
}

// ── Tarjetas ──────────────────────────────────────────────────────────────────
static ULONG WINAPI h_GetAdaptersAddresses(ULONG family, ULONG flags, PVOID reserved, PIP_ADAPTER_ADDRESSES buf, PULONG size)
{
    ULONG rc = GetAdaptersAddresses(family, flags, reserved, buf, size);
    if (rc != NO_ERROR || !g_cfg.hide || !buf) return rc;
    // Primero se encadenan las visibles; luego, si la cabeza (el inicio del búfer, que el
    // programa usa como lista) no es visible, se intercambia su contenido con la primera visible.
    PIP_ADAPTER_ADDRESSES first = NULL, prev = NULL;
    for (PIP_ADAPTER_ADDRESSES a = buf; a; a = a->Next)
    {
        if (!VisibleLuid(a->Luid)) continue;
        if (prev) prev->Next = a; else first = a;
        prev = a;
    }
    if (prev) prev->Next = NULL;
    if (!first)
    {   // nada visible: lista vacía
        *size = 0;
        return ERROR_NO_DATA;
    }
    if (first != buf)
    {
        // La cabeza oculta se sustituye por la primera visible (copia del registro; los punteros
        // internos siguen apuntando a datos del mismo búfer, válidos).
        PIP_ADAPTER_ADDRESSES next = first->Next;
        ULONG len = buf->Length < first->Length ? buf->Length : first->Length;
        memcpy(buf, first, len);
        buf->Next = next;
    }
    return rc;
}

static ULONG WINAPI h_GetAdaptersInfo(PIP_ADAPTER_INFO buf, PULONG size)
{
    ULONG rc = GetAdaptersInfo(buf, size);
    if (rc != NO_ERROR || !g_cfg.hide || !buf) return rc;
    PIP_ADAPTER_INFO first = NULL, prev = NULL;
    for (PIP_ADAPTER_INFO a = buf; a; a = a->Next)
    {
        if (!VisibleIndex(a->Index)) continue;
        if (prev) prev->Next = a; else first = a;
        prev = a;
    }
    if (prev) prev->Next = NULL;
    if (!first) return ERROR_NO_DATA;
    if (first != buf)
    {
        PIP_ADAPTER_INFO next = first->Next;
        *buf = *first;
        buf->Next = next;
    }
    return rc;
}

// Compacta un array MIB quitando las filas de tarjetas ocultas.
#define COMPACT(table, count, rowType, visibleExpr)                     \
    do {                                                                \
        ULONG kept_ = 0;                                                \
        for (ULONG i_ = 0; i_ < (ULONG)(count); i_++)                   \
        {                                                               \
            rowType* row = &(table)[i_];                                \
            if (visibleExpr) { if (kept_ != i_) (table)[kept_] = *row; kept_++; } \
        }                                                               \
        (count) = kept_;                                                \
    } while (0)

static DWORD WINAPI h_GetIpAddrTable(PMIB_IPADDRTABLE t, PULONG size, BOOL order)
{
    DWORD rc = GetIpAddrTable(t, size, order);
    if (rc == NO_ERROR && g_cfg.hide && t) COMPACT(t->table, t->dwNumEntries, MIB_IPADDRROW, VisibleIndex(row->dwIndex));
    return rc;
}

static DWORD WINAPI h_GetIfTable(PMIB_IFTABLE t, PULONG size, BOOL order)
{
    DWORD rc = GetIfTable(t, size, order);
    if (rc == NO_ERROR && g_cfg.hide && t) COMPACT(t->table, t->dwNumEntries, MIB_IFROW, VisibleIndex(row->dwIndex));
    return rc;
}

static DWORD WINAPI h_GetIpForwardTable(PMIB_IPFORWARDTABLE t, PULONG size, BOOL order)
{
    DWORD rc = GetIpForwardTable(t, size, order);
    if (rc == NO_ERROR && g_cfg.hide && t)
        COMPACT(t->table, t->dwNumEntries, MIB_IPFORWARDROW, VisibleIndex(row->dwForwardIfIndex));
    return rc;
}

static DWORD WINAPI h_GetInterfaceInfo(PIP_INTERFACE_INFO t, PULONG size)
{
    DWORD rc = GetInterfaceInfo(t, size);
    if (rc == NO_ERROR && g_cfg.hide && t) COMPACT(t->Adapter, t->NumAdapters, IP_ADAPTER_INDEX_MAP, VisibleIndex(row->Index));
    return rc;
}

static NETIO_STATUS WINAPI h_GetIfTable2(PMIB_IF_TABLE2* t)
{
    NETIO_STATUS rc = GetIfTable2(t);
    if (rc == NO_ERROR && g_cfg.hide && *t) COMPACT((*t)->Table, (*t)->NumEntries, MIB_IF_ROW2, VisibleLuid(row->InterfaceLuid));
    return rc;
}

static NETIO_STATUS WINAPI h_GetIfTable2Ex(MIB_IF_TABLE_LEVEL level, PMIB_IF_TABLE2* t)
{
    NETIO_STATUS rc = GetIfTable2Ex(level, t);
    if (rc == NO_ERROR && g_cfg.hide && *t) COMPACT((*t)->Table, (*t)->NumEntries, MIB_IF_ROW2, VisibleLuid(row->InterfaceLuid));
    return rc;
}

static NETIO_STATUS WINAPI h_GetUnicastIpAddressTable(ADDRESS_FAMILY family, PMIB_UNICASTIPADDRESS_TABLE* t)
{
    NETIO_STATUS rc = GetUnicastIpAddressTable(family, t);
    if (rc == NO_ERROR && g_cfg.hide && *t)
        COMPACT((*t)->Table, (*t)->NumEntries, MIB_UNICASTIPADDRESS_ROW, VisibleLuid(row->InterfaceLuid));
    return rc;
}

static NETIO_STATUS WINAPI h_GetIpForwardTable2(ADDRESS_FAMILY family, PMIB_IPFORWARD_TABLE2* t)
{
    NETIO_STATUS rc = GetIpForwardTable2(family, t);
    if (rc == NO_ERROR && g_cfg.hide && *t)
        COMPACT((*t)->Table, (*t)->NumEntries, MIB_IPFORWARD_ROW2, VisibleLuid(row->InterfaceLuid));
    return rc;
}

static NETIO_STATUS WINAPI h_GetIpInterfaceTable(ADDRESS_FAMILY family, PMIB_IPINTERFACE_TABLE* t)
{
    NETIO_STATUS rc = GetIpInterfaceTable(family, t);
    if (rc == NO_ERROR && g_cfg.hide && *t)
        COMPACT((*t)->Table, (*t)->NumEntries, MIB_IPINTERFACE_ROW, VisibleLuid(row->InterfaceLuid));
    return rc;
}

// ── Nombre del equipo ─────────────────────────────────────────────────────────
// Resolver el propio nombre devuelve todas las IPs del PC: se quitan las de tarjetas ocultas y,
// si no queda ninguna IPv4, la primera quitada pasa a ser la IP de la sesión.
static void FilterAddrInfoA(PADDRINFOA* head)
{
    if (!g_cfg.hide || !head) return;
    IN_ADDR ip;
    BOOL haveIp = SessionIp(&ip), haveV4 = FALSE;
    for (PADDRINFOA a = *head; a; a = a->ai_next)
        if (a->ai_family == AF_INET && !IsHiddenLocal(a->ai_addr)) haveV4 = TRUE;
    PADDRINFOA* link = head;
    while (*link)
    {
        PADDRINFOA a = *link;
        if (!IsHiddenLocal(a->ai_addr)) { link = &a->ai_next; continue; }
        if (a->ai_family == AF_INET && haveIp && !haveV4)
        {
            ((SOCKADDR_IN*)a->ai_addr)->sin_addr = ip;
            haveV4 = TRUE;
            link = &a->ai_next;
            continue;
        }
        *link = a->ai_next;
        a->ai_next = NULL;
        freeaddrinfo(a);
    }
}

static void FilterAddrInfoW(PADDRINFOW* head)
{
    if (!g_cfg.hide || !head) return;
    IN_ADDR ip;
    BOOL haveIp = SessionIp(&ip), haveV4 = FALSE;
    for (PADDRINFOW a = *head; a; a = a->ai_next)
        if (a->ai_family == AF_INET && !IsHiddenLocal(a->ai_addr)) haveV4 = TRUE;
    PADDRINFOW* link = head;
    while (*link)
    {
        PADDRINFOW a = *link;
        if (!IsHiddenLocal(a->ai_addr)) { link = &a->ai_next; continue; }
        if (a->ai_family == AF_INET && haveIp && !haveV4)
        {
            ((SOCKADDR_IN*)a->ai_addr)->sin_addr = ip;
            haveV4 = TRUE;
            link = &a->ai_next;
            continue;
        }
        *link = a->ai_next;
        a->ai_next = NULL;
        FreeAddrInfoW(a);
    }
}

static INT WSAAPI h_getaddrinfo(PCSTR node, PCSTR service, const ADDRINFOA* hints, PADDRINFOA* result)
{
    INT rc = getaddrinfo(node, service, hints, result);
    if (rc == 0) FilterAddrInfoA(result);
    return rc;
}

static INT WSAAPI h_GetAddrInfoW(PCWSTR node, PCWSTR service, const ADDRINFOW* hints, PADDRINFOW* result)
{
    INT rc = GetAddrInfoW(node, service, hints, result);
    if (rc == 0) FilterAddrInfoW(result);
    return rc;
}

static struct hostent* WSAAPI h_gethostbyname(const char* name)
{
    struct hostent* h = gethostbyname(name);
    if (!h || !g_cfg.hide || h->h_addrtype != AF_INET || !h->h_addr_list) return h;
    IN_ADDR ip;
    BOOL haveIp = SessionIp(&ip), haveV4 = FALSE;
    int kept = 0;
    for (int i = 0; h->h_addr_list[i]; i++)
    {
        SOCKADDR_IN sa = { 0 };
        sa.sin_family = AF_INET;
        memcpy(&sa.sin_addr, h->h_addr_list[i], sizeof sa.sin_addr);
        if (IsHiddenLocal((SOCKADDR*)&sa))
        {
            if (!haveIp || haveV4) continue;
            memcpy(h->h_addr_list[i], &ip, sizeof ip);
        }
        haveV4 = TRUE;
        h->h_addr_list[kept++] = h->h_addr_list[i];
    }
    h->h_addr_list[kept] = NULL;
    return h;
}

// ── Propagación a los procesos hijos ──────────────────────────────────────────
BOOL WINAPI OsmsNetInject(HANDLE process, LPCWSTR dllPath)
{
    SIZE_T size = (wcslen(dllPath) + 1) * sizeof(WCHAR);
    LPVOID remote = VirtualAllocEx(process, NULL, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) return FALSE;
    BOOL ok = FALSE;
    if (WriteProcessMemory(process, remote, dllPath, size, NULL))
    {
        LPTHREAD_START_ROUTINE loadLibrary =
            (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
        HANDLE thread = CreateRemoteThread(process, NULL, 0, loadLibrary, remote, 0, NULL);
        if (thread)
        {
            DWORD code = 0;
            ok = WaitForSingleObject(thread, 15000) == WAIT_OBJECT_0 && GetExitCodeThread(thread, &code) && code != 0;
            CloseHandle(thread);
        }
    }
    if (ok) VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    return ok;
}

static BOOL SameArch(HANDLE process)
{
    BOOL wow = FALSE;
    IsWow64Process(process, &wow);
#ifdef _WIN64
    return !wow;
#else
    BOOL selfWow = FALSE;
    IsWow64Process(GetCurrentProcess(), &selfWow);
    return wow == selfWow;
#endif
}

// Inyecta en un proceso (suspendido) de cualquier arquitectura: el de la otra arquitectura lo
// hace osms-netinject.exe de la carpeta hermana (..\x86 o ..\x64).
BOOL WINAPI OsmsNetInjectAny(HANDLE process)
{
    WCHAR dll[MAX_PATH];
    GetModuleFileNameW(g_self, dll, MAX_PATH);
    if (SameArch(process)) return OsmsNetInject(process, dll);

    WCHAR helper[MAX_PATH];
    wcscpy_s(helper, MAX_PATH, dll);
    WCHAR* slash = wcsrchr(helper, L'\\');
    if (!slash) return FALSE;
    *slash = 0;
    slash = wcsrchr(helper, L'\\');
    if (!slash) return FALSE;
    swprintf_s(slash + 1, MAX_PATH - (slash + 1 - helper), OTHER_ARCH L"\\osms-netinject.exe");

    WCHAR cmd[MAX_PATH + 32];
    swprintf_s(cmd, ARRAYSIZE(cmd), L"\"%s\" %lu", helper, GetProcessId(process));
    STARTUPINFOW si = { sizeof si };
    PROCESS_INFORMATION pi;
    if (!CreateProcessW(helper, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) return FALSE;
    DWORD code = 1;
    if (WaitForSingleObject(pi.hProcess, 20000) == WAIT_OBJECT_0) GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return code == 0;
}

static BOOL ChildPath(HANDLE process, WCHAR* out, DWORD cch)
{
    return QueryFullProcessImageNameW(process, 0, out, &cch);
}

// Si la inyección falla el hijo arranca igual (con la red del PC): mejor que no arrancar.
static void AfterCreate(BOOL ok, DWORD flags, LPPROCESS_INFORMATION pi)
{
    if (!ok) return;
    WCHAR path[MAX_PATH];
    if (ChildPath(pi->hProcess, path, MAX_PATH) && !IsExcludedExe(path) && !OsmsNetInjectAny(pi->hProcess))
        Log("no se pudo cargar en %ls", path);
    if (!(flags & CREATE_SUSPENDED)) ResumeThread(pi->hThread);
}

static BOOL WINAPI h_CreateProcessW(LPCWSTR app, LPWSTR cmd, LPSECURITY_ATTRIBUTES pa, LPSECURITY_ATTRIBUTES ta,
                                    BOOL inherit, DWORD flags, LPVOID env, LPCWSTR dir,
                                    LPSTARTUPINFOW si, LPPROCESS_INFORMATION pi)
{
    BOOL ok = CreateProcessW(app, cmd, pa, ta, inherit, flags | CREATE_SUSPENDED, env, dir, si, pi);
    DWORD err = GetLastError();
    AfterCreate(ok, flags, pi);
    SetLastError(err);
    return ok;
}

static BOOL WINAPI h_CreateProcessA(LPCSTR app, LPSTR cmd, LPSECURITY_ATTRIBUTES pa, LPSECURITY_ATTRIBUTES ta,
                                    BOOL inherit, DWORD flags, LPVOID env, LPCSTR dir,
                                    LPSTARTUPINFOA si, LPPROCESS_INFORMATION pi)
{
    BOOL ok = CreateProcessA(app, cmd, pa, ta, inherit, flags | CREATE_SUSPENDED, env, dir, si, pi);
    DWORD err = GetLastError();
    AfterCreate(ok, flags, pi);
    SetLastError(err);
    return ok;
}

// ── Parcheo de las IAT ────────────────────────────────────────────────────────
typedef struct { const char* name; void* hook; } Hook;

static FARPROC WINAPI h_GetProcAddress(HMODULE module, LPCSTR name);
static HMODULE WINAPI h_LoadLibraryW(LPCWSTR name);
static HMODULE WINAPI h_LoadLibraryA(LPCSTR name);
static HMODULE WINAPI h_LoadLibraryExW(LPCWSTR name, HANDLE f, DWORD fl);
static HMODULE WINAPI h_LoadLibraryExA(LPCSTR name, HANDLE f, DWORD fl);

static const Hook kHooks[] = {
    { "bind",                     (void*)h_bind },
    { "connect",                  (void*)h_connect },
    { "WSAConnect",               (void*)h_WSAConnect },
    { "sendto",                   (void*)h_sendto },
    { "WSASendTo",                (void*)h_WSASendTo },
    { "setsockopt",               (void*)h_setsockopt },
    { "getaddrinfo",              (void*)h_getaddrinfo },
    { "GetAddrInfoW",             (void*)h_GetAddrInfoW },
    { "gethostbyname",            (void*)h_gethostbyname },
    { "GetAdaptersAddresses",     (void*)h_GetAdaptersAddresses },
    { "GetAdaptersInfo",          (void*)h_GetAdaptersInfo },
    { "GetIpAddrTable",           (void*)h_GetIpAddrTable },
    { "GetIfTable",               (void*)h_GetIfTable },
    { "GetIpForwardTable",        (void*)h_GetIpForwardTable },
    { "GetInterfaceInfo",         (void*)h_GetInterfaceInfo },
    { "GetIfTable2",              (void*)h_GetIfTable2 },
    { "GetIfTable2Ex",            (void*)h_GetIfTable2Ex },
    { "GetUnicastIpAddressTable", (void*)h_GetUnicastIpAddressTable },
    { "GetIpForwardTable2",       (void*)h_GetIpForwardTable2 },
    { "GetIpInterfaceTable",      (void*)h_GetIpInterfaceTable },
    { "CreateProcessW",           (void*)h_CreateProcessW },
    { "CreateProcessA",           (void*)h_CreateProcessA },
    { "GetProcAddress",           (void*)h_GetProcAddress },
    { "LoadLibraryW",             (void*)h_LoadLibraryW },
    { "LoadLibraryA",             (void*)h_LoadLibraryA },
    { "LoadLibraryExW",           (void*)h_LoadLibraryExW },
    { "LoadLibraryExA",           (void*)h_LoadLibraryExA },
};

static void* HookFor(const char* name)
{
    for (int i = 0; i < ARRAYSIZE(kHooks); i++)
        if (strcmp(name, kHooks[i].name) == 0) return kHooks[i].hook;
    return NULL;
}

// ws2_32 exporta bind, connect, sendto... también por ordinal y su biblioteca de importación los
// enlaza así: esas entradas no traen nombre. Se reconocen por la dirección ya resuelta, comparada
// con la real de cada función en los módulos que la implementan.
#define MAX_REAL 4
static void* g_real[ARRAYSIZE(kHooks)][MAX_REAL];

static void ResolveReal(void)
{
    static const WCHAR* const kModules[MAX_REAL] = { L"ws2_32.dll", L"iphlpapi.dll", L"kernel32.dll", L"kernelbase.dll" };
    for (int m = 0; m < MAX_REAL; m++)
    {
        HMODULE mod = GetModuleHandleW(kModules[m]);
        if (!mod) continue;
        for (int i = 0; i < ARRAYSIZE(kHooks); i++)
            g_real[i][m] = (void*)GetProcAddress(mod, kHooks[i].name);
    }
}

static void* HookForAddress(ULONG_PTR address)
{
    if (!address) return NULL;
    for (int i = 0; i < ARRAYSIZE(kHooks); i++)
        for (int m = 0; m < MAX_REAL; m++)
            if (g_real[i][m] == (void*)address) return kHooks[i].hook;
    return NULL;
}

static void PatchImports(HMODULE module)
{
    BYTE* base = (BYTE*)module;
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return;
    for (IMAGE_IMPORT_DESCRIPTOR* imp = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); imp->Name; imp++)
    {
        if (!imp->OriginalFirstThunk) continue;
        IMAGE_THUNK_DATA* names = (IMAGE_THUNK_DATA*)(base + imp->OriginalFirstThunk);
        IMAGE_THUNK_DATA* iat   = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk);
        for (; names->u1.AddressOfData; names++, iat++)
        {
            void* hook = IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)
                ? HookForAddress(iat->u1.Function)
                : HookFor((const char*)((IMAGE_IMPORT_BY_NAME*)(base + names->u1.AddressOfData))->Name);
            if (!hook || iat->u1.Function == (ULONG_PTR)hook) continue;
            DWORD old;
            if (VirtualProtect(&iat->u1.Function, sizeof(void*), PAGE_READWRITE, &old))
            {
                iat->u1.Function = (ULONG_PTR)hook;
                VirtualProtect(&iat->u1.Function, sizeof(void*), old, &old);
            }
        }
    }
}

// Módulos que implementan lo enganchado (o por los que pasa: kernel32 salta a kernelbase por su
// IAT): engancharlos haría que el gancho se llamara a sí mismo.
static BOOL IsImplementer(LPCWSTR path)
{
    static const WCHAR* const kSkip[] = {
        L"ntdll.dll", L"kernel32.dll", L"kernelbase.dll", L"ws2_32.dll", L"mswsock.dll", L"iphlpapi.dll",
        L"wow64.dll", L"wow64win.dll", L"wow64cpu.dll", L"wow64base.dll", L"wow64con.dll", L"xtajit.dll",
    };
    LPCWSTR slash = wcsrchr(path, L'\\');
    LPCWSTR name = slash ? slash + 1 : path;
    for (int i = 0; i < ARRAYSIZE(kSkip); i++)
        if (_wcsicmp(name, kSkip[i]) == 0) return TRUE;
    return FALSE;
}

static SRWLOCK g_patchLock = SRWLOCK_INIT;

static void PatchAllModules(void)
{
    AcquireSRWLockExclusive(&g_patchLock);
    HMODULE modules[1024];
    DWORD needed = 0;
    if (K32EnumProcessModules(GetCurrentProcess(), modules, sizeof modules, &needed))
    {
        DWORD count = needed / sizeof(HMODULE);
        if (count > ARRAYSIZE(modules)) count = ARRAYSIZE(modules);
        for (DWORD i = 0; i < count; i++)
        {
            WCHAR path[MAX_PATH];
            if (modules[i] == g_self || !GetModuleFileNameW(modules[i], path, MAX_PATH) || IsImplementer(path)) continue;
            PatchImports(modules[i]);
        }
    }
    ReleaseSRWLockExclusive(&g_patchLock);
}

static HMODULE WINAPI h_LoadLibraryW(LPCWSTR name)                       { HMODULE m = LoadLibraryW(name);          if (m) PatchAllModules(); return m; }
static HMODULE WINAPI h_LoadLibraryA(LPCSTR name)                        { HMODULE m = LoadLibraryA(name);          if (m) PatchAllModules(); return m; }
static HMODULE WINAPI h_LoadLibraryExW(LPCWSTR name, HANDLE f, DWORD fl) { HMODULE m = LoadLibraryExW(name, f, fl); if (m && !(fl & (LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE))) PatchAllModules(); return m; }
static HMODULE WINAPI h_LoadLibraryExA(LPCSTR name, HANDLE f, DWORD fl)  { HMODULE m = LoadLibraryExA(name, f, fl); if (m && !(fl & (LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE))) PatchAllModules(); return m; }

// Quien pide la dirección a mano (GetProcAddress, carga diferida) también recibe el gancho.
static FARPROC WINAPI h_GetProcAddress(HMODULE module, LPCSTR name)
{
    FARPROC real = GetProcAddress(module, name);
    if (!real) return real;
    WCHAR path[MAX_PATH];
    if (!GetModuleFileNameW(module, path, MAX_PATH) || !IsImplementer(path)) return real;
    void* hook = IS_INTRESOURCE(name) ? HookForAddress((ULONG_PTR)real) : HookFor(name);
    // LoadLibrary*/GetProcAddress pedidos a mano no hace falta desviarlos.
    if (!hook || hook == (void*)h_GetProcAddress || hook == (void*)h_LoadLibraryW || hook == (void*)h_LoadLibraryA
        || hook == (void*)h_LoadLibraryExW || hook == (void*)h_LoadLibraryExA)
        return real;
    return (FARPROC)hook;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    g_self = instance;
    DisableThreadLibraryCalls(instance);

    WCHAR exe[MAX_PATH];
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    if (!ReadConfig() || IsExcludedExe(exe)) return TRUE;   // queda cargada sin hacer nada

    g_cfg.active = TRUE;
    ResolveReal();
    PatchAllModules();
    Log("activa en %ls (tarjeta \"%ls\", ocultar=%d)", exe, g_cfg.iface, g_cfg.hide);
    return TRUE;
}
