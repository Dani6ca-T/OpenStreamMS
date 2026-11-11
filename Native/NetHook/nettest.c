// nettest — lo que ve un programa de la red del equipo (para probar osms-nethook).
//   nettest.exe [destino UDP, p. ej. 192.168.56.255] [puerto]
// Muestra las tarjetas (GetAdaptersAddresses y GetAdaptersInfo), las IPs del propio nombre y
// desde qué IP salen un bind a 0.0.0.0 y un sendto sin bind.

#define WIN32_LEAN_AND_MEAN
#define _WINSOCK_DEPRECATED_NO_WARNINGS
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <stdio.h>
#include <stdlib.h>
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "iphlpapi.lib")

static void PrintLocal(SOCKET s, const char* what)
{
    SOCKADDR_IN sa;
    int len = sizeof sa;
    char ip[32] = "?";
    if (getsockname(s, (SOCKADDR*)&sa, &len) == 0) inet_ntop(AF_INET, &sa.sin_addr, ip, sizeof ip);
    else sprintf_s(ip, sizeof ip, "sin ligar (%d)", WSAGetLastError());
    printf("  %-28s %s\n", what, ip);
}

int main(int argc, char** argv)
{
    WSADATA w;
    WSAStartup(MAKEWORD(2, 2), &w);
    printf("%s bits\n", sizeof(void*) == 8 ? "64" : "32");

    printf("GetAdaptersAddresses:\n");
    ULONG size = 64 * 1024;
    IP_ADAPTER_ADDRESSES* aa = (IP_ADAPTER_ADDRESSES*)malloc(size);
    if (GetAdaptersAddresses(AF_INET, 0, NULL, aa, &size) == NO_ERROR)
        for (IP_ADAPTER_ADDRESSES* a = aa; a; a = a->Next)
        {
            char ip[32] = "-";
            if (a->FirstUnicastAddress)
                inet_ntop(AF_INET, &((SOCKADDR_IN*)a->FirstUnicastAddress->Address.lpSockaddr)->sin_addr, ip, sizeof ip);
            printf("  %-34ls %s\n", a->FriendlyName, ip);
        }
    else printf("  (ninguna)\n");

    printf("GetAdaptersInfo:\n");
    size = 64 * 1024;
    IP_ADAPTER_INFO* ai = (IP_ADAPTER_INFO*)malloc(size);
    if (GetAdaptersInfo(ai, &size) == NO_ERROR)
        for (IP_ADAPTER_INFO* a = ai; a; a = a->Next) printf("  %-34.34s %s\n", a->Description, a->IpAddressList.IpAddress.String);
    else printf("  (ninguna)\n");

    char host[256];
    gethostname(host, sizeof host);
    printf("getaddrinfo(\"%s\"):\n", host);
    ADDRINFOA hints = { 0 }, *res = NULL;
    hints.ai_family = AF_INET;
    if (getaddrinfo(host, NULL, &hints, &res) == 0)
        for (ADDRINFOA* r = res; r; r = r->ai_next)
        {
            char ip[32];
            inet_ntop(AF_INET, &((SOCKADDR_IN*)r->ai_addr)->sin_addr, ip, sizeof ip);
            printf("  %s\n", ip);
        }

    printf("Sockets:\n");
    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    SOCKADDR_IN any = { 0 };
    any.sin_family = AF_INET;
    bind(s, (SOCKADDR*)&any, sizeof any);
    PrintLocal(s, "bind(0.0.0.0) ->");
    closesocket(s);

    const char* dest = argc > 1 ? argv[1] : "255.255.255.255";
    s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    BOOL on = TRUE;
    setsockopt(s, SOL_SOCKET, SO_BROADCAST, (char*)&on, sizeof on);
    SOCKADDR_IN to = { 0 };
    to.sin_family = AF_INET;
    to.sin_port = htons((u_short)(argc > 2 ? atoi(argv[2]) : 47998));
    inet_pton(AF_INET, dest, &to.sin_addr);
    sendto(s, "x", 1, 0, (SOCKADDR*)&to, sizeof to);
    char what[64];
    sprintf_s(what, sizeof what, "sendto(%s) sin bind ->", dest);
    PrintLocal(s, what);
    closesocket(s);
    return 0;
}
