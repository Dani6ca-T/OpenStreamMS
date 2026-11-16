using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Gamepad;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Separación total de los ratones y teclados de un puesto, solo con drivers de Microsoft:
/// mientras el puesto está encendido, el dispositivo USB (o su interfaz, si es compuesto) pasa
/// del driver HID al genérico WinUSB (<c>winusb.inf</c>, incluido en Windows). Windows deja de
/// verlo como ratón/teclado, así que no mueve el cursor de la consola ni teclea en ella, y el
/// servicio lee sus informes USB directamente (<see cref="UsbHidReader"/>). Al apagar el puesto
/// vuelve a su driver (<c>input.inf</c>).
///
/// Cada cambio se anota en disco antes de hacerse, con la clase y el driver que tenía el
/// dispositivo: si el servicio se cae o se apaga el PC con un puesto encendido,
/// <see cref="RestoreAll"/> lo deshace en el siguiente arranque, y <see cref="WatchArrivals"/>
/// lo deshace en cuanto vuelva a conectarse uno que estaba desenchufado entonces.
/// </summary>
internal static class UsbCapture
{
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "usb-capture.json");
    private static readonly object _journalLock = new();

    /// <summary>Un nodo pasado a WinUSB y lo que tenía antes (null si no se pudo saber).</summary>
    internal sealed record CaptureRecord(string Id, string? ClassGuid = null, string? Inf = null);

    /// <summary>Nodos capturados por un puesto encendido en esta ejecución: no se restauran al llegar.</summary>
    private static readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Copia en memoria de los ids anotados, para el aviso de llegada (tiene que ser rápido).</summary>
    private static HashSet<string> _journalIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _restoring = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GUID de interfaz propio que se le da a WinUSB para poder abrir el dispositivo.</summary>
    public static readonly Guid InterfaceGuid = new("6b1e3c4a-3f2b-4d77-9c41-0a1b2c3d4e5f");

    private const string MouseClass    = "{4d36e96f-e325-11ce-bfc1-08002be10318}";
    private const string KeyboardClass = "{4d36e96b-e325-11ce-bfc1-08002be10318}";
    private const string UsbDeviceClass = "{88BAE032-5A81-49f0-BC3D-A4FF138216D6}";

    /// <summary>
    /// Nodos USB a pasar a WinUSB para el dispositivo de ese puerto: el propio dispositivo, o
    /// las interfaces (<c>&amp;MI_xx</c>) de un compuesto que tienen un ratón o teclado debajo.
    /// Así una interfaz de un teclado gaming que no es de entrada (RGB, audio…) no se toca.
    /// </summary>
    public static List<string> FindTargets(string usbPort)
    {
        var device = GamepadSessionIsolation.Cm.DeviceIds("USB")
            .FirstOrDefault(id => !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(DeviceTopology.UsbPortOf(id), usbPort, StringComparison.OrdinalIgnoreCase));
        if (device is null) return [];

        var children = GamepadSessionIsolation.Cm.Children(device);
        var interfaces = children.Where(c => c.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = interfaces.Count > 0 ? interfaces : [device];
        return candidates.Where(HasInputCollection).ToList();
    }

    private static bool HasInputCollection(string node) =>
        GamepadSessionIsolation.Cm.Children(node).Any(c =>
            GamepadSessionIsolation.Cm.ClassGuidOf(c) is { } g &&
            (g.Equals(MouseClass, StringComparison.OrdinalIgnoreCase) || g.Equals(KeyboardClass, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Pasa el nodo a WinUSB. Devuelve false si no se pudo (el puesto sigue con Raw Input).</summary>
    public static bool Capture(string instanceId)
    {
        // Lo que tenía antes, para devolvérselo al apagar. Si ya estaba anotado (quedó capturado
        // de una ejecución anterior) se conserva esa anotación: ahora mismo tendría WinUSB.
        var (cls, inf) = OriginalDriver(instanceId);
        bool fresh = Journal(add: new CaptureRecord(instanceId, cls, inf));
        if (!fresh)
            cls = ReadJournal().FirstOrDefault(r => r.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))?.ClassGuid ?? cls;
        // Activo antes de instalar: el cambio de driver dispara el aviso de llegada, y
        // WatchArrivals no debe tomarlo por uno que quedó capturado y devolverlo.
        lock (_journalLock) _active.Add(instanceId);
        try
        {
            // WinUSB lee DeviceInterfaceGUIDs al arrancar: sin él no expone interfaz que abrir.
            using (var k = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters"))
                k.SetValue("DeviceInterfaceGUIDs", new[] { InterfaceGuid.ToString("B") }, RegistryValueKind.MultiString);

            Setup.Install(instanceId, winUsb: true, rollbackClass: cls);
            return true;
        }
        catch (Exception ex)
        {
            lock (_journalLock) _active.Remove(instanceId);
            Logger.Warning($"[SeatUsb] No se pudo pasar {instanceId} a WinUSB: {ex.Message}");
            // Install deja la clase como estaba si falla: el nodo sigue con su driver. Solo se
            // olvida si la anotación era nueva; una anterior sigue pendiente de restaurar.
            if (fresh) Journal(remove: instanceId);
            return false;
        }
    }

    /// <summary>
    /// Devuelve el nodo a su driver de antes (el del fabricante si lo tenía), o al HID de
    /// Windows (<c>input.inf</c>) si aquel ya no está disponible.
    /// </summary>
    public static bool Release(string instanceId)
    {
        var record = ReadJournal().FirstOrDefault(r => r.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                     ?? new CaptureRecord(instanceId);
        bool ok = false;
        try
        {
            Setup.Install(instanceId, winUsb: false, preferInf: record.Inf);
            ok = true;
        }
        catch (Exception ex) { Logger.Warning($"[SeatUsb] No se pudo restaurar el driver de {instanceId}: {ex.Message}"); }
        lock (_journalLock) _active.Remove(instanceId);
        // Si falla queda anotado y se reintenta al volver a conectarse o en el próximo arranque.
        if (ok) Journal(remove: instanceId);
        return ok;
    }

    /// <summary>Restaura lo que quedara capturado de una ejecución anterior (caída, apagado brusco).</summary>
    public static void RestoreAll()
    {
        var pending = new List<string>();
        foreach (var r in ReadJournal())
        {
            if (!GamepadSessionIsolation.Cm.Exists(r.Id)) { pending.Add(r.Id); continue; }
            Logger.Log($"[SeatUsb] Restaurando el driver de {r.Id} (quedó capturado).");
            Release(r.Id);
        }
        if (pending.Count > 0)
            Logger.Log($"[SeatUsb] {pending.Count} dispositivo(s) capturados están desconectados; se restaurarán al conectarse: {string.Join(", ", pending)}");
    }

    /// <summary>
    /// Vigila la llegada de dispositivos: uno que quedó en WinUSB de una ejecución anterior y
    /// estaba desenchufado al arrancar vuelve a su driver en cuanto se conecta. Sin esto, la
    /// consola se quedaba sin ese teclado o ratón hasta el siguiente arranque del servicio.
    /// </summary>
    public static IDisposable? WatchArrivals() =>
        GamepadSessionIsolation.Cm.WatchEnumeration((id, _) =>
        {
            // Aviso en un hilo del sistema: aquí solo se decide; el cambio de driver va aparte.
            lock (_journalLock)
            {
                if (!_journalIds.Contains(id) || _active.Contains(id) || !_restoring.Add(id)) return;
            }
            Task.Run(() =>
            {
                try
                {
                    Logger.Log($"[SeatUsb] {id} se ha conectado y quedó capturado de antes: se devuelve a su driver.");
                    Release(id);
                }
                finally { lock (_journalLock) _restoring.Remove(id); }
            });
        });

    /// <summary>Clase y INF del driver actual del nodo (de su clave de driver), o null si no se sabe.</summary>
    private static (string? ClassGuid, string? Inf) OriginalDriver(string instanceId)
    {
        try
        {
            using var dev = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            string? cls = dev?.GetValue("ClassGUID") as string;
            string? inf = null;
            if (dev?.GetValue("Driver") is string driverKey)
            {
                using var drv = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
                inf = drv?.GetValue("InfPath") as string;
            }
            // Ya en WinUSB (quedó así por un fallo): no es el driver original.
            if (inf is not null && inf.Equals("winusb.inf", StringComparison.OrdinalIgnoreCase)) return (null, null);
            return (cls, inf);
        }
        catch { return (null, null); }
    }

    private static List<CaptureRecord> ReadJournal()
    {
        lock (_journalLock)
        {
            if (!File.Exists(JournalPath)) return [];
            string json;
            try { json = File.ReadAllText(JournalPath); }
            catch { return []; }
            try { return JsonSerializer.Deserialize<List<CaptureRecord>>(json) ?? []; }
            catch (JsonException)
            {
                // Formato anterior: solo los ids.
                try { return (JsonSerializer.Deserialize<List<string>>(json) ?? []).Select(id => new CaptureRecord(id)).ToList(); }
                catch { return []; }
            }
        }
    }

    /// <summary>Añade o quita una anotación. Devuelve true si <paramref name="add"/> no estaba ya.</summary>
    private static bool Journal(CaptureRecord? add = null, string? remove = null)
    {
        lock (_journalLock)
        {
            var list = ReadJournal();
            bool added = false;
            if (add is not null && !list.Any(r => r.Id.Equals(add.Id, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(add);
                added = true;
            }
            if (remove is not null) list.RemoveAll(r => r.Id.Equals(remove, StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            File.WriteAllText(JournalPath, JsonSerializer.Serialize(list));
            _journalIds = list.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return added;
        }
    }

    /// <summary>Carga la copia en memoria de las anotaciones (para <see cref="WatchArrivals"/>).</summary>
    public static void LoadJournal()
    {
        lock (_journalLock) _journalIds = ReadJournal().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ── SetupAPI ─────────────────────────────────────────────────────────────

    private static unsafe class Setup
    {
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(IntPtr g, string? e, IntPtr h, int f);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr s, int i, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiOpenDeviceInfoW(IntPtr s, string id, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr g, IntPtr hwnd);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiBuildDriverInfoList(IntPtr s, ref SP_DEVINFO_DATA d, int t);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiEnumDriverInfoW(IntPtr s, ref SP_DEVINFO_DATA d, int t, int i, byte* drv);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiGetDriverInfoDetailW(IntPtr s, ref SP_DEVINFO_DATA d, byte* drv, byte* det, int sz, out int req);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiGetDeviceInstallParamsW(IntPtr s, ref SP_DEVINFO_DATA d, byte* p);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiSetDeviceInstallParamsW(IntPtr s, ref SP_DEVINFO_DATA d, byte* p);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s, ref SP_DEVINFO_DATA d, int prop, byte* buf, int size);
        [DllImport("setupapi")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("newdev", SetLastError = true)] static extern bool DiInstallDevice(IntPtr hwnd, IntPtr s, ref SP_DEVINFO_DATA d, byte* drv, int flags, out bool reboot);

        const int SPDIT_CLASSDRIVER = 1, SPDIT_COMPATDRIVER = 2, SPDRP_CLASSGUID = 8;
        // Tamaños x64 de SP_DRVINFO_DATA_V2_W, SP_DRVINFO_DETAIL_DATA_W y SP_DEVINSTALL_PARAMS_W
        const int DRVINFO_SIZE = 1568, DETAIL_SIZE = 1584, DETAIL_INF_OFFSET = 544, DIP_SIZE = 584, DIP_PATH_OFFSET = 60;
        const int DI_ENUMSINGLEINF = 0x10000, DI_FLAGSEX_ALLOWEXCLUDEDDRVS = 0x800;

        /// <summary>
        /// winUsb: fuerza el modelo de <c>winusb.inf</c> (cambiando antes la clase a USBDevice, como
        /// hace el Administrador de dispositivos al elegir "Dispositivo WinUsb"); si falla después
        /// de cambiar la clase, la devuelve a <paramref name="rollbackClass"/> para no dejar el nodo
        /// sin driver. Si no, instala el driver compatible de <paramref name="preferInf"/> (el que
        /// tenía antes) o, si ya no está, el de <c>input.inf</c>, que vuelve a poner la clase HID.
        /// Lanza una excepción si no se pudo.
        /// </summary>
        public static void Install(string instanceId, bool winUsb, string? rollbackClass = null, string? preferInf = null)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            bool classChanged = false;
            try
            {
                if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref dev))
                    throw new InvalidOperationException($"dispositivo no encontrado ({Marshal.GetLastWin32Error()})");

                int listType = SPDIT_COMPATDRIVER;
                if (winUsb)
                {
                    SetClass(set, ref dev, UsbDeviceClass);
                    classChanged = true;

                    byte* dip = stackalloc byte[DIP_SIZE];
                    new Span<byte>(dip, DIP_SIZE).Clear();
                    *(int*)dip = DIP_SIZE;
                    SetupDiGetDeviceInstallParamsW(set, ref dev, dip);
                    *(int*)(dip + 4) |= DI_ENUMSINGLEINF;
                    *(int*)(dip + 8) |= DI_FLAGSEX_ALLOWEXCLUDEDDRVS;
                    string inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "winusb.inf") + "\0";
                    fixed (char* c = inf) Buffer.MemoryCopy(c, dip + DIP_PATH_OFFSET, 520, inf.Length * 2);
                    if (!SetupDiSetDeviceInstallParamsW(set, ref dev, dip))
                        throw new InvalidOperationException($"SetupDiSetDeviceInstallParams ({Marshal.GetLastWin32Error()})");
                    listType = SPDIT_CLASSDRIVER;
                }

                if (!SetupDiBuildDriverInfoList(set, ref dev, listType))
                    throw new InvalidOperationException($"SetupDiBuildDriverInfoList ({Marshal.GetLastWin32Error()})");

                // Por orden de preferencia: winusb.inf, o el driver de antes y si no input.inf.
                string[] wanted = winUsb ? ["winusb.inf"]
                    : new[] { preferInf, "input.inf" }
                        .Where(f => !string.IsNullOrEmpty(f) && !f.Equals("winusb.inf", StringComparison.OrdinalIgnoreCase))
                        .Select(f => f!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                byte* drv = stackalloc byte[DRVINFO_SIZE];
                byte* det = stackalloc byte[8192];
                int best = -1, bestRank = int.MaxValue;
                for (int i = 0; ; i++)
                {
                    new Span<byte>(drv, DRVINFO_SIZE).Clear();
                    *(int*)drv = DRVINFO_SIZE;
                    if (!SetupDiEnumDriverInfoW(set, ref dev, listType, i, drv)) break;
                    *(int*)det = DETAIL_SIZE;
                    SetupDiGetDriverInfoDetailW(set, ref dev, drv, det, 8192, out _);
                    string infName = Path.GetFileName(new string((char*)(det + DETAIL_INF_OFFSET)));
                    int rank = Array.FindIndex(wanted, w => w.Equals(infName, StringComparison.OrdinalIgnoreCase));
                    if (rank >= 0 && rank < bestRank) { best = i; bestRank = rank; }
                }
                if (best < 0)
                    throw new InvalidOperationException($"no hay driver {string.Join(" ni ", wanted)} para este dispositivo");

                new Span<byte>(drv, DRVINFO_SIZE).Clear();
                *(int*)drv = DRVINFO_SIZE;
                if (!SetupDiEnumDriverInfoW(set, ref dev, listType, best, drv))
                    throw new InvalidOperationException($"SetupDiEnumDriverInfo ({Marshal.GetLastWin32Error()})");
                if (!DiInstallDevice(IntPtr.Zero, set, ref dev, drv, 0, out _))
                    throw new InvalidOperationException($"DiInstallDevice ({Marshal.GetLastWin32Error():X})");
                classChanged = false;
                Logger.Log($"[SeatUsb] {instanceId} → {new string((char*)(drv + 16))} ({wanted[bestRank]}).");
            }
            catch when (classChanged && rollbackClass is not null)
            {
                // La clase cambió pero WinUSB no se instaló: el nodo quedaría como "USBDevice" sin
                // driver y nadie lo restauraría. Se devuelve su clase; el driver anterior sigue.
                try { SetClass(set, ref dev, rollbackClass); }
                catch (Exception ex) { Logger.Warning($"[SeatUsb] {instanceId}: no se pudo devolver su clase ({ex.Message})."); }
                throw;
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static void SetClass(IntPtr set, ref SP_DEVINFO_DATA dev, string classGuid)
        {
            string cls = classGuid + "\0";
            fixed (char* pc = cls)
                if (!SetupDiSetDeviceRegistryPropertyW(set, ref dev, SPDRP_CLASSGUID, (byte*)pc, cls.Length * 2))
                    throw new InvalidOperationException($"no se pudo cambiar la clase ({Marshal.GetLastWin32Error()})");
            dev.ClassGuid = new Guid(classGuid);
        }
    }
}

/// <summary>
/// Lee un ratón/teclado puesto en WinUSB y traduce sus informes HID a <see cref="SeatInputEvent"/>.
/// Si se desconecta, reintenta abrirlo cada segundo (vuelve con el mismo driver WinUSB).
/// </summary>
internal sealed unsafe class UsbHidReader : IDisposable
{
    private readonly string _instanceId;
    private readonly Action<SeatInputEvent> _emit;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private SafeFileHandle? _file;
    private IntPtr _winusb;
    private readonly object _handleLock = new();

    public UsbHidReader(string instanceId, Action<SeatInputEvent> emit)
    {
        _instanceId = instanceId;
        _emit = emit;
        // Prioridad alta: con el juego cargando la CPU, cada informe sale sin esperar turno.
        _thread = new Thread(Run) { IsBackground = true, Name = $"SeatUsb {instanceId}", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        string? lastError = null;
        while (!_stopping)
        {
            try
            {
                if (Open(out byte pipe, out int packet, out var layout) is { } error)
                {
                    if (error != lastError) Logger.Warning($"[SeatUsb] Esperando a {_instanceId}: {error}");
                    lastError = error;
                    Close();
                    Thread.Sleep(1000);
                    continue;
                }
                lastError = null;
                Logger.Log($"[SeatUsb] Leyendo {_instanceId} ({layout.Summary}).");
                var state = new HidTranslator(layout, _emit);
                var managed = new byte[packet];
                int traced = 0;
                fixed (byte* buf = managed)
                    while (!_stopping && WinUsb_ReadPipe(_winusb, pipe, buf, packet, out int read, IntPtr.Zero))
                    {
                        if (read <= 0) continue;
                        // Diagnóstico: los primeros informes en crudo, para ver el formato real del aparato.
                        if (traced < 8) { traced++; Logger.Log($"[SeatUsb] informe: {Convert.ToHexString(managed, 0, read)}"); }
                        state.OnReport(new ReadOnlySpan<byte>(buf, read));
                    }
                state.ReleaseAll();
                if (!_stopping) Logger.Warning($"[SeatUsb] {_instanceId} desconectado ({Marshal.GetLastWin32Error()}).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[SeatUsb] {_instanceId}: {ex.Message}");
                Thread.Sleep(1000);
            }
            finally { Close(); }
        }
    }

    /// <summary>Abre el dispositivo; devuelve null si está listo para leer o el motivo por el que no.</summary>
    private string? Open(out byte pipe, out int packet, out HidReportLayout layout)
    {
        pipe = 0; packet = 8; layout = HidReportLayout.Empty;
        var g = UsbCapture.InterfaceGuid;
        int cr = CM_Get_Device_Interface_List_SizeW(out int len, ref g, _instanceId, 0);
        if (cr != 0 || len <= 1) return $"sin interfaz WinUSB (CM {cr})";
        var list = new char[len];
        if ((cr = CM_Get_Device_Interface_ListW(ref g, _instanceId, list, len, 0)) != 0) return $"sin interfaz WinUSB (CM {cr})";
        string path = new string(list).TrimEnd('\0').Split('\0')[0];
        if (path.Length == 0) return "sin interfaz WinUSB";

        var file = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero);
        if (file.IsInvalid) return $"CreateFile {Marshal.GetLastWin32Error()}";
        if (!WinUsb_Initialize(file, out var winusb)) { int e = Marshal.GetLastWin32Error(); file.Dispose(); return $"WinUsb_Initialize {e}"; }
        lock (_handleLock) { _file = file; _winusb = winusb; }

        byte* ifd = stackalloc byte[9];
        if (!WinUsb_QueryInterfaceSettings(winusb, 0, ifd)) return $"WinUsb_QueryInterfaceSettings {Marshal.GetLastWin32Error()}";
        byte iface = ifd[2], protocol = ifd[7];
        byte* pi = stackalloc byte[12];
        for (byte e = 0; e < ifd[4]; e++)
        {
            if (!WinUsb_QueryPipe(winusb, 0, e, pi)) continue;
            if (*(int*)pi == 3 /*interrupt*/ && (pi[4] & 0x80) != 0) { pipe = pi[4]; packet = *(ushort*)(pi + 6); break; }
        }
        if (pipe == 0) return $"sin endpoint de interrupción ({ifd[4]} endpoints)";

        // Descriptor de informe HID: GET_DESCRIPTOR(0x22) dirigido a la interfaz, pidiendo justo la
        // longitud que anuncia su descriptor HID (algunos ratones no responden si se pide de más).
        int want = ReportDescriptorLength(winusb, iface);
        int err = 0;
        byte* rd = stackalloc byte[1024];
        for (int attempt = 0; attempt < 3 && layout.Fields.Count == 0 && layout.Arrays.Count == 0; attempt++)
        {
            int n = attempt == 2 ? 1024 : (want is > 0 and <= 1024 ? want : 1024);
            ulong setup = 0x81UL | (6UL << 8) | (0x2200UL << 16) | ((ulong)iface << 32) | ((ulong)n << 48);
            if (WinUsb_ControlTransfer(winusb, setup, rd, n, out int rdl, IntPtr.Zero) && rdl > 0)
                layout = HidReportLayout.Parse(new ReadOnlySpan<byte>(rd, rdl));
            else err = Marshal.GetLastWin32Error();
        }

        if (layout.Fields.Count == 0 && layout.Arrays.Count == 0)
        {
            // Sin descriptor utilizable: protocolo "boot" (formato fijo) si es un ratón o teclado boot.
            if (HidReportLayout.Boot(protocol) is not { } boot)
                return $"descriptor HID no disponible (err {err}, longitud {want}) y no es un dispositivo boot (protocolo {protocol})";
            ulong setProtocol = 0x21UL | (0x0BUL << 8) | (0UL << 16) | ((ulong)iface << 32);   // SET_PROTOCOL(boot)
            bool protoOk = WinUsb_ControlTransfer(winusb, setProtocol, null, 0, out _, IntPtr.Zero);
            layout = boot;
            Logger.Warning($"[SeatUsb] {_instanceId}: sin descriptor HID (err {err}, longitud {want}); protocolo boot {(protoOk ? "activado" : $"rechazado ({Marshal.GetLastWin32Error()})")}.");
        }
        return null;
    }

    /// <summary>wDescriptorLength del descriptor de informe, sacado del descriptor HID (0x21) de la interfaz.</summary>
    private static int ReportDescriptorLength(IntPtr winusb, byte iface)
    {
        byte* cfg = stackalloc byte[1024];
        if (!WinUsb_GetDescriptor(winusb, 2 /*configuración*/, 0, 0, cfg, 1024, out int len)) return 0;
        bool inIface = false;
        for (int i = 0; i + 1 < len && cfg[i] > 0; i += cfg[i])
        {
            byte type = cfg[i + 1];
            if (type == 4) inIface = i + 2 < len && cfg[i + 2] == iface;
            else if (type == 0x21 && inIface && i + 8 < len)
                for (int k = 0; k < cfg[i + 5] && i + 8 + k * 3 < len; k++)
                    if (cfg[i + 6 + k * 3] == 0x22) return cfg[i + 7 + k * 3] | (cfg[i + 8 + k * 3] << 8);
        }
        return 0;
    }

    private void Close()
    {
        lock (_handleLock)
        {
            if (_winusb != IntPtr.Zero) { WinUsb_Free(_winusb); _winusb = IntPtr.Zero; }
            _file?.Dispose();
            _file = null;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        lock (_handleLock)
        {
            // Desbloquea el ReadPipe en curso.
            if (_winusb != IntPtr.Zero)
                for (byte p = 0x81; p < 0x90; p++) WinUsb_AbortPipe(_winusb, p);
            _file?.Dispose();
        }
        _thread.Join(2000);
    }

    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid g, string inst, int f);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_ListW(ref Guid g, string inst, char[] buf, int len, int f);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle h, out IntPtr w);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Free(IntPtr w);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_QueryInterfaceSettings(IntPtr w, byte alt, byte* desc);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_QueryPipe(IntPtr w, byte alt, byte idx, byte* info);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_ReadPipe(IntPtr w, byte pipe, byte* buf, int len, out int read, IntPtr ov);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_AbortPipe(IntPtr w, byte pipe);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_GetDescriptor(IntPtr w, byte type, byte index, ushort lang, byte* buf, int len, out int read);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_ControlTransfer(IntPtr w, ulong setup, byte* buf, int len, out int read, IntPtr ov);
}
