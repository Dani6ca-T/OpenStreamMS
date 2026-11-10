using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Gamepad;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Reserva un dispositivo USB para el usuario de un puesto con el descriptor de seguridad del
/// propio dispositivo (<c>SPDRP_SECURITY_SDS</c>): solo SYSTEM y ese usuario pueden abrirlo, así
/// que la consola y las demás sesiones reciben "acceso denegado". Sin drivers de terceros y para
/// cualquier clase de dispositivo (ver <c>docs/asignacion-dispositivos.md</c>).
/// <list type="bullet">
/// <item>Windows aplica la ACL al crear el objeto de dispositivo: tras cambiarla se reinicia el
/// nodo, lo que además cierra los handles que tuviera abiertos otra sesión.</item>
/// <item>Persiste en el registro: cada cambio se anota en disco con el descriptor anterior, y
/// <see cref="RestoreAll"/> lo deshace si el servicio se cayó con un puesto encendido (o en
/// cuanto el dispositivo vuelva a conectarse, ver <see cref="OnDeviceArrived"/>).</item>
/// <item>No aísla lo que abre un servicio del sistema por la sesión (audio, cámaras, unidades
/// montadas): <see cref="SystemOpenedKind"/> lo detecta para avisar en el panel.</item>
/// </list>
/// </summary>
internal static class SeatDeviceAccess
{
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "device-access.json");
    private static readonly object _lock = new();

    /// <summary>Un nodo con la ACL de un puesto y el descriptor que tenía antes (null: el de su clase).</summary>
    internal sealed record AccessRecord(string Id, string? OriginalSds);

    /// <summary>Puertos reservados ahora mismo → SID del usuario del puesto.</summary>
    private static readonly Dictionary<string, string> _ports = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Nodos con ACL de puesto (copia en memoria del registro de cambios).</summary>
    private static HashSet<string> _journalIds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Nodos que se están cambiando: su propio reinicio dispara avisos de llegada.</summary>
    private static readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SDDL que deja abrir el dispositivo solo a SYSTEM y al usuario <paramref name="userSid"/>.</summary>
    public static string SddlFor(string userSid) => $"D:P(A;;GA;;;SY)(A;;GA;;;{userSid})";

    /// <summary>
    /// Reserva el dispositivo de ese puerto (el nodo USB y todos sus hijos) para
    /// <paramref name="userSid"/>. Devuelve false si no hay nada conectado o no se pudo.
    /// </summary>
    public static bool Reserve(string usbPort, string userSid, string seatName)
    {
        lock (_lock) _ports[usbPort] = userSid;
        if (UsbDeviceAt(usbPort) is not { } device)
        {
            Logger.Log($"[SeatAccess] '{seatName}': nada conectado en {usbPort}; se reservará al conectarse.");
            return false;
        }
        return Apply(device, userSid, seatName);
    }

    /// <summary>Devuelve el dispositivo de ese puerto a todas las sesiones.</summary>
    public static void Free(string usbPort)
    {
        lock (_lock) _ports.Remove(usbPort);
        if (UsbDeviceAt(usbPort) is { } device) Restore(device);
    }

    /// <summary>Deshace lo que quedara de una ejecución anterior (dispositivos presentes).</summary>
    public static void RestoreAll()
    {
        LoadJournal();
        var records = ReadJournal();
        var roots = records.Select(r => r.Id).Where(GamepadSessionIsolation.Cm.Exists)
                           .Select(id => UsbRootOf(id) ?? id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var root in roots)
        {
            Logger.Log($"[SeatAccess] {root} quedó reservado para un puesto: se devuelve a todas las sesiones.");
            Restore(root);
        }
        int pending = ReadJournal().Count;
        if (pending > 0)
            Logger.Log($"[SeatAccess] {pending} nodo(s) reservados están desconectados; se liberarán al conectarse.");
    }

    /// <summary>
    /// Aviso de llegada o arranque de un nodo (hilo del sistema: solo se decide aquí). Si cuelga
    /// de un puerto reservado y no tiene la ACL, se le pone (dispositivo enchufado con el puesto
    /// encendido); si tiene una ACL de puesto que ya no corresponde, se quita.
    /// </summary>
    public static void OnDeviceArrived(string instanceId)
    {
        string? sid = null;
        bool stale;
        lock (_lock)
        {
            if (_busy.Contains(instanceId)) return;
            stale = _journalIds.Contains(instanceId);
            if (_ports.Count == 0 && !stale) return;
        }
        // Fuera del candado: subir por el árbol consulta PnP.
        string? root = UsbRootOf(instanceId);
        if (root is null) return;
        string? port = DeviceTopology.UsbPortOf(root);
        lock (_lock)
        {
            if (_busy.Contains(root)) return;
            if (port is not null) _ports.TryGetValue(port, out sid);
            if (sid is null && !stale) return;
            _busy.Add(root);
        }
        string target = root;
        string? userSid = sid;
        Task.Run(() =>
        {
            try
            {
                if (userSid is not null)
                {
                    if (!HasSds(target, SddlFor(userSid))) Apply(target, userSid, "puesto");
                }
                else Restore(target);
            }
            catch (Exception ex) { Logger.Warning($"[SeatAccess] {target}: {ex.Message}"); }
            finally { lock (_lock) _busy.Remove(target); }
        });
    }

    /// <summary>
    /// Qué abre un servicio del sistema en ese puerto, y por tanto la ACL no aísla: "storage",
    /// "camera" o "audio"; null si no hay nada de eso.
    /// </summary>
    public static string? SystemOpenedKind(string usbPort)
    {
        if (UsbDeviceAt(usbPort) is not { } device) return null;
        foreach (var n in GamepadSessionIsolation.Cm.Descendants(device).Prepend(device))
        {
            switch (GamepadSessionIsolation.Cm.ClassGuidOf(n)?.ToLowerInvariant())
            {
                case "{4d36e967-e325-11ce-bfc1-08002be10318}":   // DiskDrive
                case "{71a27cdd-812a-11d0-bec7-08002be2092f}":   // Volume
                case "{eec5ad98-8080-425f-922a-dabf3de3f69a}":   // WPD (teléfonos, MTP)
                    return "storage";
                case "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}":   // Camera
                case "{6bdd1fc6-810f-11d0-bec7-08002be2092f}":   // Image
                    return "camera";
                case "{4d36e96c-e325-11ce-bfc1-08002be10318}":   // MEDIA (audio USB)
                    return "audio";
            }
        }
        return null;
    }

    // ── Aplicar y restaurar ─────────────────────────────────────────────────

    private static bool Apply(string device, string userSid, string seatName)
    {
        string sddl = SddlFor(userSid);
        lock (_lock) _busy.Add(device);
        try
        {
            var nodes = GamepadSessionIsolation.Cm.Descendants(device).Prepend(device).ToList();
            foreach (var n in nodes)
            {
                // Se anota antes de tocarlo, con lo que tenía (si ya estaba anotado se conserva lo de antes).
                Journal(add: new AccessRecord(n, Security.Get(n)));
                Security.Set(n, sddl);
            }
            // La ACL solo cuenta para el objeto de dispositivo nuevo: reiniciar el nodo raíz rehace
            // el árbol y cierra lo que tuviera abierto la consola u otra sesión.
            GamepadSessionIsolation.DevSetup.Restart(device);
            // Los hijos que aparezcan con otro id al reiniciar (raro) los cubre OnDeviceArrived.
            Logger.Log($"[SeatAccess] '{seatName}': {device} reservado ({nodes.Count} nodo(s)); las demás sesiones no pueden abrirlo.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SeatAccess] '{seatName}': no se pudo reservar {device}: {ex.Message}");
            return false;
        }
        finally { lock (_lock) _busy.Remove(device); }
    }

    private static void Restore(string device)
    {
        lock (_lock) _busy.Add(device);
        try
        {
            var byId = ReadJournal().ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            var nodes = GamepadSessionIsolation.Cm.Descendants(device).Prepend(device).Where(byId.ContainsKey).ToList();
            if (nodes.Count == 0) return;
            foreach (var n in nodes)
            {
                if (!string.Equals(Security.Get(n), byId[n].OriginalSds, StringComparison.OrdinalIgnoreCase))
                    Security.Set(n, byId[n].OriginalSds);
                Journal(remove: n);
            }
            GamepadSessionIsolation.DevSetup.Restart(device);
            Logger.Log($"[SeatAccess] {device} vuelve a estar disponible para todas las sesiones.");
        }
        catch (Exception ex)
        {
            // Queda anotado: se reintenta al reconectarse o en el próximo arranque del servicio.
            Logger.Warning($"[SeatAccess] No se pudo liberar {device}: {ex.Message}");
        }
        finally { lock (_lock) _busy.Remove(device); }
    }

    private static bool HasSds(string id, string sddl)
    {
        try { return string.Equals(Security.Get(id), sddl, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ── Topología ───────────────────────────────────────────────────────────

    /// <summary>Nodo USB (no interfaz <c>&amp;MI_xx</c>) conectado en ese puerto, o null.</summary>
    private static string? UsbDeviceAt(string usbPort) =>
        GamepadSessionIsolation.Cm.DeviceIds("USB")
            .FirstOrDefault(id => !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(DeviceTopology.UsbPortOf(id), usbPort, StringComparison.OrdinalIgnoreCase));

    /// <summary>Primer antecesor USB que no es interfaz de un compuesto (el que tiene puerto).</summary>
    private static string? UsbRootOf(string instanceId)
    {
        for (string? id = instanceId; id is not null; id = GamepadSessionIsolation.Cm.Parent(id))
            if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                return id;
        return null;
    }

    // ── Registro de cambios ─────────────────────────────────────────────────

    private static List<AccessRecord> ReadJournal()
    {
        lock (_lock)
        {
            try { return File.Exists(JournalPath) ? JsonSerializer.Deserialize<List<AccessRecord>>(File.ReadAllText(JournalPath)) ?? [] : []; }
            catch { return []; }
        }
    }

    private static void LoadJournal()
    {
        lock (_lock) _journalIds = ReadJournal().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void Journal(AccessRecord? add = null, string? remove = null)
    {
        lock (_lock)
        {
            var list = ReadJournal();
            if (add is not null && !list.Any(r => r.Id.Equals(add.Id, StringComparison.OrdinalIgnoreCase))) list.Add(add);
            if (remove is not null) list.RemoveAll(r => r.Id.Equals(remove, StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            File.WriteAllText(JournalPath, JsonSerializer.Serialize(list));
            _journalIds = list.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ── Usuario de una sesión ───────────────────────────────────────────────

    /// <summary>SID del usuario conectado a esa sesión de Windows, o null (el servicio corre como SYSTEM).</summary>
    public static string? UserSidOfSession(uint windowsSession)
    {
        if (!WTSQueryUserToken(windowsSession, out var token)) return null;
        try
        {
            GetTokenInformation(token, 1 /*TokenUser*/, IntPtr.Zero, 0, out int len);
            if (len <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(token, 1, buf, len, out _)) return null;
                return ConvertSidToStringSidW(Marshal.ReadIntPtr(buf), out var sid) ? sid : null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(token); }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int ret);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertSidToStringSidW(IntPtr sid, out string str);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    // ── SetupAPI ────────────────────────────────────────────────────────────

    private static class Security
    {
        [StructLayout(LayoutKind.Sequential)] private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr g, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string id, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA d, int prop, byte[]? buf, int size);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA d, int prop, out int type, byte[]? buf, int size, out int req);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        private const int SPDRP_SECURITY_SDS = 0x18;

        /// <summary>Descriptor propio del nodo en SDDL, o null si usa el de su clase.</summary>
        public static string? Get(string id)
        {
            string? result = null;
            With(id, (IntPtr set, ref SP_DEVINFO_DATA d) =>
            {
                var buf = new byte[4096];
                if (SetupDiGetDeviceRegistryPropertyW(set, ref d, SPDRP_SECURITY_SDS, out _, buf, buf.Length, out int req) && req > 2)
                    result = Encoding.Unicode.GetString(buf, 0, req).TrimEnd('\0');
            });
            return result;
        }

        /// <summary>Fija el descriptor (SDDL), o lo quita con null.</summary>
        public static void Set(string id, string? sddl)
        {
            With(id, (IntPtr set, ref SP_DEVINFO_DATA d) =>
            {
                byte[]? buf = sddl is null ? null : Encoding.Unicode.GetBytes(sddl + "\0");
                if (!SetupDiSetDeviceRegistryPropertyW(set, ref d, SPDRP_SECURITY_SDS, buf, buf?.Length ?? 0))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (sddl is null && err == 13 /*ERROR_INVALID_DATA: no tenía*/) return;
                    throw new Win32Exception(err, $"{id}: no se pudo cambiar su descriptor de seguridad");
                }
            });
        }

        private delegate void DeviceAction(IntPtr set, ref SP_DEVINFO_DATA d);

        private static void With(string id, DeviceAction action)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == new IntPtr(-1)) throw new Win32Exception();
            try
            {
                var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(set, id, IntPtr.Zero, 0, ref d))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"{id}: no se encuentra");
                action(set, ref d);
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }
}
