// Prototipo: ¿restringe DEVPKEY_Device_SessionId un dispositivo a una sesión de Windows?
//
// Fija o quita la propiedad en un nodo, y desde cualquier sesión comprueba si el nodo se ve
// (CM_Locate_DevNode, SetupDiGetClassDevs) y si se pueden abrir sus interfaces (CreateFile).
// "spawn" lanza la comprobación dentro de otra sesión, con el token del usuario de esa sesión
// o como SYSTEM en ella (hace falta correr como SYSTEM).
//
//   DeviceSessionProbe list [texto]
//   DeviceSessionProbe get <instanceId>
//   DeviceSessionProbe set <instanceId> <sesión> [--tree] [--restart]
//   DeviceSessionProbe clear <instanceId> [--tree] [--restart]
//   DeviceSessionProbe restart <instanceId>
//   DeviceSessionProbe probe <instanceId>
//   DeviceSessionProbe spawn <user|system> <sesión> <fichero> <orden...>
//   --out <fichero>  (cualquier orden) escribe la salida en ese fichero
//
// --tree aplica a todo el subárbol del nodo (p. ej. el dispositivo USB y sus colecciones HID).

using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

var argv = args.ToList();
int outIdx = argv.IndexOf("--out");
StreamWriter? outFile = null;
if (outIdx >= 0 && outIdx + 1 < argv.Count)
{
    outFile = new StreamWriter(argv[outIdx + 1], append: false, Encoding.UTF8) { AutoFlush = true };
    Console.SetOut(outFile);
    argv.RemoveRange(outIdx, 2);
}
bool tree = argv.Remove("--tree");
bool restart = argv.Remove("--restart");
string? pathsFile = null;
int pathsIdx = argv.IndexOf("--paths");
if (pathsIdx >= 0 && pathsIdx + 1 < argv.Count)
{
    pathsFile = argv[pathsIdx + 1];
    argv.RemoveRange(pathsIdx, 2);
}

try
{
    switch (argv.FirstOrDefault())
    {
        case "list":    List(argv.ElementAtOrDefault(1) ?? ""); break;
        case "get":     foreach (var id in Nodes(argv[1], true)) Console.WriteLine($"{id}  SessionId={Cm.SessionId(id)?.ToString() ?? "(no existe)"}"); break;
        case "set":     Set(argv[1], uint.Parse(argv[2])); break;
        case "clear":   Set(argv[1], null); break;
        case "restart": Restart(argv[1]); break;
        case "disable": DevSetup.Change(argv[1], 2); Console.WriteLine($"{argv[1]}: deshabilitado"); break;
        case "enable":  DevSetup.Change(argv[1], 1); Console.WriteLine($"{argv[1]}: habilitado"); break;
        case "probe":   Probe(argv[1]); break;
        case "paths":   Paths(argv[1], argv[2]); break;
        case "acl":
            // acl <id> <SDDL|clear|get>: descriptor de seguridad del dispositivo (SPDRP_SECURITY_SDS)
            foreach (var n in Nodes(argv[1], tree))
            {
                if (argv[2] != "get") DevSetup.SetSecurity(n, argv[2] == "clear" ? null : argv[2]);
                Console.WriteLine($"{n}: seguridad = {DevSetup.GetSecurity(n) ?? "(por defecto)"}");
            }
            if (restart) Restart(argv[1]);
            break;
        case "usersid": Console.WriteLine(SessionUserSid(uint.Parse(argv[1]))); break;
        case "spawn":   Spawn(argv[1], uint.Parse(argv[2]), argv[3], argv.Skip(4).ToArray()); break;
        default:
            Console.WriteLine("Uso: list [texto] | get <id> | set <id> <sesión> [--tree] [--restart] | clear <id> [--tree] [--restart] | restart <id> | probe <id> | spawn <user|system> <sesión> <fichero> <orden...>");
            return 1;
    }
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    return 2;
}
finally { outFile?.Dispose(); }

IEnumerable<string> Nodes(string id, bool all) => all ? Cm.Descendants(id).Prepend(id) : [id];

void List(string filter)
{
    foreach (var id in Cm.DeviceIds().Where(i => i.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"{id}\n    clase={Cm.Class(id) ?? "?"}  SessionId={Cm.SessionId(id)?.ToString() ?? "-"}  puerto={Cm.LocationPath(id) ?? "-"}");
}

void Set(string id, uint? session)
{
    foreach (var n in Nodes(id, tree))
    {
        Cm.SetSessionId(n, session);
        Console.WriteLine($"{n}: SessionId {(session is null ? "quitado" : $"= {session}")} (ahora: {Cm.SessionId(n)?.ToString() ?? "-"})");
    }
    if (restart) Restart(id);
}

void Restart(string id)
{
    DevSetup.Restart(id);
    Console.WriteLine($"{id}: reiniciado");
}

void Probe(string id)
{
    ProcessIdToSessionId((uint)Environment.ProcessId, out uint mySession);
    Console.WriteLine($"Proceso: sesión {mySession}, usuario {Environment.UserDomainName}\\{Environment.UserName}, {DateTime.Now:HH:mm:ss}");
    var visible = DevSetup.VisibleInstanceIds();
    Console.WriteLine($"SetupDiGetClassDevs(PRESENT|ALLCLASSES): {visible.Count} dispositivos");
    var guids = InterfaceClassGuids();

    // El árbol se recorre desde un nodo que se pueda localizar (si el propio nodo no se ve, no hay árbol).
    var nodes = Cm.Locate(id) == 0 ? Cm.Descendants(id).Prepend(id).ToList() : [id];
    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var n in nodes)
    {
        int cr = Cm.Locate(n);
        Console.WriteLine($"\n{n}");
        Console.WriteLine($"  clase={Safe(() => Cm.Class(n)) ?? "?"}  SessionId={Safe(() => Cm.SessionId(n)?.ToString()) ?? "-"}  estado={Cm.Status(n)}");
        Console.WriteLine($"  CM_Locate_DevNode: {(cr == 0 ? "OK" : $"CR {cr}")}   SetupDiGetClassDevs: {(visible.Contains(n) ? "lo ve" : "NO lo ve")}");
        foreach (var g in guids)
            foreach (var path in Cm.Interfaces(g, n))
            {
                seenPaths.Add(path);
                Console.WriteLine($"  interfaz {g:B}\n    {path}\n    {OpenAll(path)}");
            }
    }

    // Rutas conocidas de antemano (sacadas con "paths" desde SYSTEM): se prueban aunque el nodo
    // no se pueda localizar ni enumerar desde esta sesión. Abrir por ruta es lo que hace un juego.
    if (pathsFile is not null && File.Exists(pathsFile))
    {
        Console.WriteLine("\nRutas conocidas (abiertas directamente):");
        foreach (var path in File.ReadAllLines(pathsFile).Where(l => l.Length > 0))
            Console.WriteLine($"  {path}\n    {OpenAll(path)}   (enumerada aquí: {(seenPaths.Contains(path) ? "sí" : "no")})");
    }
}

void Paths(string id, string file)
{
    var guids = InterfaceClassGuids();
    var list = Cm.Descendants(id).Prepend(id).SelectMany(n => guids.SelectMany(g => Cm.Interfaces(g, n))).Distinct().ToList();
    File.WriteAllLines(file, list);
    Console.WriteLine($"{list.Count} rutas de interfaz en {file}");
}

// SID del usuario conectado en una sesión (hace falta SYSTEM para WTSQueryUserToken).
static string SessionUserSid(uint session)
{
    if (!WTSQueryUserToken(session, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken");
    try
    {
        GetTokenInformation(token, 1 /*TokenUser*/, IntPtr.Zero, 0, out int len);
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetTokenInformation(token, 1, buf, len, out _)) throw new Win32Exception();
            if (!ConvertSidToStringSidW(Marshal.ReadIntPtr(buf), out var str)) throw new Win32Exception();
            return str;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
    finally { CloseHandle(token); }
}

[DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int ret);
[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertSidToStringSidW(IntPtr sid, out string str);

static string OpenAll(string path) =>
    $"abrir(consulta)={TryOpen(path, 0)}  abrir(lectura)={TryOpen(path, 0x80000000)}  abrir(lectura+escritura)={TryOpen(path, 0xC0000000)}";

static string? Safe(Func<string?> f) { try { return f(); } catch (Exception ex) { return $"({ex.Message})"; } }

static string TryOpen(string path, uint access)
{
    using var h = CreateFileW(path, access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
    return h.IsInvalid ? $"error {Marshal.GetLastWin32Error()}" : "OK";
}

static List<Guid> InterfaceClassGuids()
{
    using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceClasses");
    return k?.GetSubKeyNames().Select(n => Guid.TryParse(n, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList() ?? [];
}

void Spawn(string who, uint session, string outPath, string[] command)
{
    IntPtr primary = IntPtr.Zero, token = IntPtr.Zero;
    try
    {
        if (who == "user")
        {
            if (!WTSQueryUserToken(session, out token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken (¿no es SYSTEM o no hay usuario en esa sesión?)");
        }
        else
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0xF01FF, out token)) throw new Win32Exception();
        }
        if (!DuplicateTokenEx(token, 0xF01FF, IntPtr.Zero, 2 /*Impersonation*/, 1 /*Primary*/, out primary)) throw new Win32Exception();
        if (who != "user")
        {
            uint s = session;
            if (!SetTokenInformation(primary, 12 /*TokenSessionId*/, ref s, 4)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(TokenSessionId)");
        }

        string exe = Environment.ProcessPath!;
        string extra = pathsFile is null ? "" : $" --paths {Quote(pathsFile)}";
        string cmdLine = $"\"{exe}\" {string.Join(' ', command.Select(Quote))}{extra} --out {Quote(outPath)}";
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = "winsta0\\default" };
        if (!CreateProcessAsUserW(primary, null, new StringBuilder(cmdLine), IntPtr.Zero, IntPtr.Zero, false,
                0x08000000 /*CREATE_NO_WINDOW*/, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");
        WaitForSingleObject(pi.hProcess, 60000);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        Console.WriteLine($"Lanzado como {who} en la sesión {session}: {outPath}");
    }
    finally
    {
        if (primary != IntPtr.Zero) CloseHandle(primary);
        if (token != IntPtr.Zero) CloseHandle(token);
    }

    static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}

[DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid, out uint session);
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
[DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out IntPtr token);
[DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
[DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
[DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr sa, int level, int type, out IntPtr newToken);
[DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int cls, ref uint info, int len);
[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool CreateProcessAsUserW(IntPtr token, string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
[DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
[DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct STARTUPINFO
{
    public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
}
[StructLayout(LayoutKind.Sequential)]
struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

static class Cm
{
    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    static DEVPROPKEY SessionKey => new() { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = 6 };
    static DEVPROPKEY LocationKey => new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 37 };
    const uint DEVPROP_TYPE_UINT32 = 7, DEVPROP_TYPE_EMPTY = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out int inst, string id, int flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Child(out int child, int inst, int flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Sibling(out int sib, int inst, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_IDW(int inst, char[] buf, int len, int flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out uint status, out uint problem, int inst, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_PropertyW(int inst, ref DEVPROPKEY key, out uint type, byte[]? buf, ref int size, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Set_DevNode_PropertyW(int inst, ref DEVPROPKEY key, uint type, byte[]? buf, int size, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_Registry_PropertyW(int inst, int prop, out int type, byte[]? buf, ref int len, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out int len, string? filter, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string? filter, char[] buf, int len, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid g, string? id, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_ListW(ref Guid g, string? id, char[] buf, int len, int flags);

    public static int Locate(string id) => CM_Locate_DevNodeW(out _, id, 0);

    static int Inst(string id)
    {
        // Si no se localiza normal, se prueba como fantasma: así se puede fijar o leer la propiedad igualmente.
        if (CM_Locate_DevNodeW(out int inst, id, 0) == 0) return inst;
        int cr = CM_Locate_DevNodeW(out inst, id, 1);
        if (cr != 0) throw new InvalidOperationException($"{id}: no se encuentra (CR {cr})");
        return inst;
    }

    public static uint? SessionId(string id)
    {
        var key = SessionKey;
        int size = 4;
        var buf = new byte[4];
        int cr = CM_Get_DevNode_PropertyW(Inst(id), ref key, out uint type, buf, ref size, 0);
        return cr == 0 && type == DEVPROP_TYPE_UINT32 ? BitConverter.ToUInt32(buf) : null;
    }

    public static void SetSessionId(string id, uint? session)
    {
        var key = SessionKey;
        int cr = session is { } s
            ? CM_Set_DevNode_PropertyW(Inst(id), ref key, DEVPROP_TYPE_UINT32, BitConverter.GetBytes(s), 4, 0)
            : CM_Set_DevNode_PropertyW(Inst(id), ref key, DEVPROP_TYPE_EMPTY, null, 0, 0);
        if (session is null && cr == 37 /*CR_NO_SUCH_VALUE: ya no estaba*/) return;
        if (cr != 0) throw new InvalidOperationException($"{id}: CM_Set_DevNode_Property CR {cr} (¿hace falta administrador?)");
    }

    public static string? LocationPath(string id)
    {
        var key = LocationKey;
        int size = 0;
        CM_Get_DevNode_PropertyW(Inst(id), ref key, out _, null, ref size, 0);
        if (size <= 2) return null;
        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(Inst(id), ref key, out _, buf, ref size, 0) == 0
            ? Encoding.Unicode.GetString(buf, 0, size).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
    }

    public static string? Class(string id)
    {
        int len = 0;
        int inst = Inst(id);
        CM_Get_DevNode_Registry_PropertyW(inst, 8 /*CM_DRP_CLASS*/, out _, null, ref len, 0);
        if (len == 0) return null;
        var buf = new byte[len];
        return CM_Get_DevNode_Registry_PropertyW(inst, 8, out _, buf, ref len, 0) == 0
            ? Encoding.Unicode.GetString(buf, 0, len).TrimEnd('\0') : null;
    }

    public static string Status(string id)
    {
        if (CM_Locate_DevNodeW(out int inst, id, 0) != 0) return "no localizable";
        return CM_Get_DevNode_Status(out uint st, out uint prob, inst, 0) == 0
            ? ((st & 0x8) != 0 ? "arrancado" : $"parado (problema {prob})") : "?";
    }

    public static IEnumerable<string> DeviceIds()
    {
        if (CM_Get_Device_ID_List_SizeW(out int len, null, 0x100) != 0) return [];
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(null, buf, len, 0x100) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public static List<string> Interfaces(Guid g, string id)
    {
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref g, id, 0) != 0 || len <= 1) return [];
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref g, id, buf, len, 0) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static List<string> Descendants(string id)
    {
        var result = new List<string>();
        if (CM_Locate_DevNodeW(out int inst, id, 0) != 0 || CM_Get_Child(out int child, inst, 0) != 0) return result;
        do
        {
            var buf = new char[512];
            if (CM_Get_Device_IDW(child, buf, buf.Length, 0) != 0) continue;
            string c = new string(buf).TrimEnd('\0');
            result.Add(c);
            result.AddRange(Descendants(c));
        }
        while (CM_Get_Sibling(out child, child, 0) == 0);
        return result;
    }
}

static class DevSetup
{
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct SP_PROPCHANGE_PARAMS { public int HeaderSize, InstallFunction, StateChange, Scope, HwProfile; }

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr g, IntPtr hwnd);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(IntPtr g, string? e, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int i, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA d, char[] buf, int len, out int req);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string id, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA d, ref SP_PROPCHANGE_PARAMS p, int size);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiCallClassInstaller(int f, IntPtr set, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA d, int prop, byte[]? buf, int size);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA d, int prop, out int type, byte[]? buf, int size, out int req);
    const int SPDRP_SECURITY_SDS = 0x18;

    /// <summary>Descriptor de seguridad del dispositivo en SDDL; null lo quita (vuelve el de la clase o el por defecto).</summary>
    public static void SetSecurity(string id, string? sddl)
    {
        WithDevice(id, (set, d) =>
        {
            byte[]? buf = sddl is null ? null : Encoding.Unicode.GetBytes(sddl + "\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref d, SPDRP_SECURITY_SDS, buf, buf?.Length ?? 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"{id}: SPDRP_SECURITY_SDS");
        });
    }

    public static string? GetSecurity(string id)
    {
        string? result = null;
        WithDevice(id, (set, d) =>
        {
            var buf = new byte[4096];
            if (SetupDiGetDeviceRegistryPropertyW(set, ref d, SPDRP_SECURITY_SDS, out _, buf, buf.Length, out int req))
                result = Encoding.Unicode.GetString(buf, 0, req).TrimEnd('\0');
        });
        return result;
    }

    delegate void DeviceAction(IntPtr set, SP_DEVINFO_DATA d);

    static void WithDevice(string id, DeviceAction action)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, id, IntPtr.Zero, 0, ref d)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"{id}: no se abre");
            action(set, d);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static HashSet<string> VisibleInstanceIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x2 /*PRESENT*/ | 0x4 /*ALLCLASSES*/);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buf = new char[512];
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                if (SetupDiGetDeviceInstanceIdW(set, ref d, buf, buf.Length, out _))
                    result.Add(new string(buf, 0, Math.Max(0, Array.IndexOf(buf, '\0'))));   // el búfer se reutiliza: cortar en el primer NUL
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public static void Restart(string id) => Change(id, 3);

    /// <summary>DIF_PROPERTYCHANGE: 1 habilitar, 2 deshabilitar, 3 reiniciar.</summary>
    public static void Change(string id, int state)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, id, IntPtr.Zero, 0, ref d)) throw new Win32Exception();
            var p = new SP_PROPCHANGE_PARAMS { HeaderSize = 8, InstallFunction = 0x12, StateChange = state, Scope = 1 };
            if (!SetupDiSetClassInstallParamsW(set, ref d, ref p, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()) ||
                !SetupDiCallClassInstaller(0x12, set, ref d))
                throw new Win32Exception();
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
