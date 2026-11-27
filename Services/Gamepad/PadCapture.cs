using Microsoft.Win32.SafeHandles;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Localization;
using OpenStreamMS.Services.Seats;
using System.Runtime.InteropServices;

namespace OpenStreamMS.Services.Gamepad;

/// <summary>
/// Aísla un mando físico sacándolo de Windows, igual que el teclado y el ratón de un puesto:
/// todas las interfaces de su USB pasan a WinUSB (<see cref="UsbCapture"/>), este servicio lee
/// sus informes y los entrega a un mando X360 virtual de ViGEmBus (<see cref="ViGEmPads"/>)
/// reclamado para la sesión destino. En Windows
/// no queda ningún mando, HID ni XUSB que abrir, así que ninguna otra sesión lo puede leer, por
/// ningún camino (HidHide solo filtra los nodos HID y Xbox, y Steam abría también el USB).
/// <para>
/// Primera versión: mandos con protocolo Xbox 360 (interfaz USB clase FF, subclase 5D,
/// protocolo 01), que es el modo XInput de la mayoría de mandos de terceros. Los demás siguen
/// aislados con HidHide y la ACL del dispositivo.
/// </para>
/// </summary>
internal static class PadCapture
{
    /// <summary>Identificador compatible de la interfaz de mando de un Xbox 360 (con cable o modo XInput).</summary>
    private const string Xbox360Interface = @"USB\Class_FF&SubClass_5D&Prot_01";

    /// <summary>
    /// Interfaces a capturar para el mando de ese puerto: las de mando Xbox 360 (se leen) y el
    /// resto del compuesto (teclado del botón de captura, HID del fabricante…; solo se apartan).
    /// Null si en ese puerto no hay un mando que se sepa leer.
    /// </summary>
    public static (List<string> Pads, List<string> Others)? FindTargets(string usbPort)
    {
        // Ni interfaces (&MI_) ni el USB\..&IG_ que xusb22 cuelga del XUSB: también están en el
        // puerto y, si salían antes en la lista, el puerto parecía no tener mando (el receptor
        // inalámbrico se quedaba en Windows y Steam lo leía desde la consola).
        var device = GamepadSessionIsolation.Cm.DeviceIds("USB")
            .FirstOrDefault(id => !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                               && !id.Contains("&IG_", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(DeviceTopology.UsbPortOf(id), usbPort, StringComparison.OrdinalIgnoreCase));
        if (device is null) return null;
        var interfaces = GamepadSessionIsolation.Cm.Children(device)
            .Where(c => c.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).ToList();
        var pads = interfaces.Where(IsXbox360Interface).ToList();
        if (pads.Count == 0) return null;
        return (pads, interfaces.Except(pads, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool IsXbox360Interface(string node) =>
        GamepadSessionIsolation.Cm.CompatibleIds(node)
            .Any(id => id.Equals(Xbox360Interface, StringComparison.OrdinalIgnoreCase));

    /// <summary>True si ViGEmBus está instalado: sin él no hay dónde entregar el mando capturado.</summary>
    public static bool Available => ViGEmPads.IsInstalled();
}

/// <summary>
/// Lee un mando Xbox 360 puesto en WinUSB y lo reproduce en un mando ViGEm de la sesión
/// destino; la vibración que pide el juego vuelve al mando físico. Si el mando se desconecta (un
/// inalámbrico que se duerme) se borra el virtual y se vuelve a crear al reaparecer.
/// </summary>
internal sealed unsafe class Xbox360PadReader : IDisposable
{
    private readonly string _instanceId;
    private readonly uint _session;
    private readonly string _owner;
    private readonly ViGEmPads _pads;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private readonly object _handleLock = new();
    private SafeFileHandle? _file;
    private IntPtr _winusb;
    private byte _outPipe;
    private uint? _pad;

    public string InstanceId => _instanceId;

    public Xbox360PadReader(string instanceId, uint windowsSession, string owner, ViGEmPads pads)
    {
        _instanceId = instanceId;
        _session = windowsSession;
        _owner = owner;
        _pads = pads;
        _pads.Feedback += OnFeedback;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Pad {instanceId}", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        string? lastError = null;
        while (!_stopping)
        {
            try
            {
                if (Open(out byte inPipe) is { } error)
                {
                    if (error != lastError) Logger.Log(BackendText.Get("padCapture.waiting", _instanceId, error));
                    lastError = error;
                    Close();
                    Thread.Sleep(1000);
                    continue;
                }
                lastError = null;

                var pad = _pads.Create(_session, _owner);
                string virtualId = $"ViGEm X360 #{pad}";
                lock (_handleLock) _pad = pad;
                Logger.Log(BackendText.Get("padCapture.virtualCreated", _owner, _instanceId, virtualId, _session));
                SetLed(0);

                var report = new byte[64];
                var last = default(PadState);
                bool first = true;
                int traced = 0, sent = 0;
                long lastSent = 0;
                fixed (byte* buf = report)
                    while (!_stopping && WinUsb_ReadPipe(_winusb, inPipe, buf, report.Length, out int read, IntPtr.Zero))
                    {
                        // Diagnóstico: los primeros informes en crudo, para ver el formato real del mando.
                        if (traced < 6) { traced++; Logger.Log(BackendText.Get("padCapture.rawReport", read, Convert.ToHexString(report, 0, Math.Min(read, 32)))); }
                        // Informe de estado: tipo 0x00, longitud 0x14 (20 bytes). Hay otros (LED, auriculares).
                        if (read < 14 || report[0] != 0x00 || report[1] < 0x0E) continue;
                        var s = new PadState(
                            Buttons: (ushort)(report[2] | (report[3] << 8)),
                            LeftTrigger: report[4], RightTrigger: report[5],
                            ThumbLX: BitConverter.ToInt16(report, 6), ThumbLY: BitConverter.ToInt16(report, 8),
                            ThumbRX: BitConverter.ToInt16(report, 10), ThumbRY: BitConverter.ToInt16(report, 12));
                        // También sin cambios, cada 100 ms: el primer informe puede llegar antes de que
                        // Windows termine de montar el mando virtual y perderse.
                        long now = Environment.TickCount64;
                        if (!first && s == last && now - lastSent < 100) continue;
                        first = false;
                        last = s;
                        lastSent = now;
                        _pads.Input(pad, s);
                        if (++sent is 1 or 50 or 500)
                            Logger.Log(BackendText.Get("padCapture.statesSent", sent, virtualId, s.Buttons, s.ThumbLX, s.ThumbLY));
                    }
                if (!_stopping) Logger.Log(BackendText.Get("padCapture.readerDisconnected", _instanceId, Marshal.GetLastWin32Error(), sent));
            }
            catch (Exception ex)
            {
                Logger.Warning(BackendText.Get("padCapture.readerError", _instanceId, ex.Message));
                Thread.Sleep(1000);
            }
            finally
            {
                DropPad();
                Close();
            }
        }
    }

    /// <summary>Abre la interfaz WinUSB; null si está lista o el motivo por el que no.</summary>
    private string? Open(out byte inPipe)
    {
        inPipe = 0;
        var g = UsbCapture.InterfaceGuid;
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref g, _instanceId, 0) != 0 || len <= 1) return BackendText.Get("padCapture.winUsbInterfaceUnavailable");
        var list = new char[len];
        if (CM_Get_Device_Interface_ListW(ref g, _instanceId, list, len, 0) != 0) return BackendText.Get("padCapture.winUsbInterfaceUnavailable");
        string path = new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (path.Length == 0) return BackendText.Get("padCapture.winUsbInterfaceUnavailable");

        var file = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero);
        if (file.IsInvalid) return BackendText.Get("padCapture.createFileFailed", Marshal.GetLastWin32Error());
        if (!WinUsb_Initialize(file, out var winusb)) { int e = Marshal.GetLastWin32Error(); file.Dispose(); return BackendText.Get("padCapture.winUsbInitializeFailed", e); }
        lock (_handleLock) { _file = file; _winusb = winusb; }

        byte* ifd = stackalloc byte[9];
        if (!WinUsb_QueryInterfaceSettings(winusb, 0, ifd)) return BackendText.Get("padCapture.queryInterfaceFailed", Marshal.GetLastWin32Error());
        byte* pi = stackalloc byte[12];
        byte outPipe = 0;
        for (byte e = 0; e < ifd[4]; e++)
        {
            if (!WinUsb_QueryPipe(winusb, 0, e, pi) || *(int*)pi != 3 /*interrupt*/) continue;
            if ((pi[4] & 0x80) != 0) { if (inPipe == 0) inPipe = pi[4]; }
            else if (outPipe == 0) outPipe = pi[4];
        }
        if (inPipe == 0) return BackendText.Get("padCapture.inputEndpointMissing", ifd[4]);
        lock (_handleLock) _outPipe = outPipe;
        return null;
    }

    /// <summary>
    /// LED del mando: jugador 1-4 (como hace xusb22 al conectarlo). Algunos mandos no mandan
    /// informes sin él; luego se pone el que asigna XInput en la sesión del puesto.
    /// </summary>
    private void SetLed(byte player) => Write([0x01, 0x03, (byte)(0x06 + Math.Min(player, (byte)3))]);

    private byte _led = 0xFF;

    private void OnFeedback(uint pad, byte large, byte small, byte led)
    {
        lock (_handleLock) if (_pad != pad) return;
        Write([0x00, 0x08, 0x00, large, small, 0x00, 0x00, 0x00]);
        if (led != _led && led < 4) { _led = led; SetLed(led); }
    }

    private void Write(byte[] data)
    {
        lock (_handleLock)
        {
            if (_winusb == IntPtr.Zero || _outPipe == 0) return;
            fixed (byte* p = data) WinUsb_WritePipe(_winusb, _outPipe, p, data.Length, out _, IntPtr.Zero);
        }
    }

    private void DropPad()
    {
        uint? pad;
        lock (_handleLock) { pad = _pad; _pad = null; }
        if (pad is { } p) _pads.Destroy(p);
    }

    private void Close()
    {
        lock (_handleLock)
        {
            if (_winusb != IntPtr.Zero) { WinUsb_Free(_winusb); _winusb = IntPtr.Zero; }
            _file?.Dispose();
            _file = null;
            _outPipe = 0;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _pads.Feedback -= OnFeedback;
        lock (_handleLock)
            if (_winusb != IntPtr.Zero)
                for (byte p = 0x81; p < 0x90; p++) WinUsb_AbortPipe(_winusb, p);
        _thread.Join(2000);
        DropPad();
        Close();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid g, string device, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid g, string device, char[] buffer, int len, int flags);
    [DllImport("winusb", SetLastError = true)] private static extern bool WinUsb_Initialize(SafeFileHandle h, out IntPtr w);
    [DllImport("winusb", SetLastError = true)] private static extern bool WinUsb_Free(IntPtr w);
    [DllImport("winusb", SetLastError = true)] private static extern bool WinUsb_QueryInterfaceSettings(IntPtr w, byte alt, byte* desc);
    [DllImport("winusb", SetLastError = true)] private static extern bool WinUsb_QueryPipe(IntPtr w, byte alt, byte idx, byte* info);
    [DllImport("winusb", SetLastError = true)] private static extern bool WinUsb_ReadPipe(IntPtr w, byte pipe, byte* buf, int len, out int read, IntPtr ov);
    [DllImport("winusb", SetLastError = true)] private static extern bool WinUsb_WritePipe(IntPtr w, byte pipe, byte* buf, int len, out int written, IntPtr ov);
    [DllImport("winusb", SetLastError = true)] private static extern bool WinUsb_AbortPipe(IntPtr w, byte pipe);
}

/// <summary>
/// Mandos de un puesto encendido: los captura al encender y, si alguno no estaba (un inalámbrico
/// dormido: su receptor no publica interfaces de mando hasta que despierta), en cuanto aparece
/// en su puerto. Al apagar suelta los mandos virtuales y devuelve cada interfaz a su driver.
/// </summary>
internal sealed class SeatPadCapture : IDisposable
{
    private readonly string _owner;
    private readonly uint _session;
    private readonly HashSet<string> _pending;
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _captured = [];
    private readonly List<Xbox360PadReader> _readers = [];
    private readonly object _lock = new();
    private ViGEmPads? _client;
    private IDisposable? _watch;
    private bool _disposed;
    private int _scheduled;

    public SeatPadCapture(string owner, uint windowsSession, IEnumerable<string> usbPorts)
    {
        _owner = owner;
        _session = windowsSession;
        _pending = new HashSet<string>(usbPorts, StringComparer.OrdinalIgnoreCase);
    }

    private string OwnerKey => $"seat:{_owner}:{_session}";

    /// <summary>Puertos cuyo mando ya está fuera de Windows (los demás siguen con HidHide y ACL).</summary>
    public IReadOnlyCollection<string> CapturedPorts { get { lock (_lock) return _done.ToList(); } }

    public void Start()
    {
        // Antes del primer mando virtual: sin esto nacería con el filtro de Steam, que lo publica
        // a todas las sesiones por encima de HidHide (ver SteamXboxFilter).
        SteamXboxFilter.Engage(OwnerKey);
        TryPending();
        lock (_lock)
        {
            if (_disposed || _pending.Count == 0) return;
            Logger.Log(BackendText.Get("padCapture.pending", _owner, _pending.Count));
            _watch = GamepadSessionIsolation.Cm.WatchEnumeration((id, _) =>
            {
                // Hilo del sistema: solo se agenda. Varias llegadas seguidas → una pasada.
                if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
                Task.Run(async () =>
                {
                    await Task.Delay(500);   // que terminen de aparecer las interfaces del compuesto
                    Interlocked.Exchange(ref _scheduled, 0);
                    TryPending();
                });
            });
            // Red de seguridad: si una pasada llega a medias (el receptor aún publicando
            // interfaces, otra parte del servicio cambiando su driver…), el aviso de llegada ya no
            // se repite y el mando se quedaba en Windows, legible desde la consola (Steam).
            _retry = new System.Threading.Timer(_ => TryPending(), null, RetryEvery, RetryEvery);
        }
    }

    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(3);
    private System.Threading.Timer? _retry;

    private void TryPending()
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var port in _pending.ToList())
            {
                try
                {
                    if (!TryCapture(port)) return;
                }
                catch (Exception ex)
                {
                    // Se queda pendiente: la próxima pasada lo vuelve a intentar.
                    Logger.Warning(BackendText.Get("padCapture.captureRetry", _owner, port, ex.Message));
                }
            }
            if (_pending.Count == 0)
            {
                _watch?.Dispose(); _watch = null;
                _retry?.Dispose(); _retry = null;
            }
        }
    }

    /// <summary>Captura el mando de un puerto si está. False si no hay ViGEmBus (no tiene sentido seguir).</summary>
    private bool TryCapture(string port)
    {
        if (PadCapture.FindTargets(port) is not var (pads, others)) return true;
        _client ??= ViGEmPads.Open();
        if (_client is null)
        {
            Logger.Warning(BackendText.Get("padCapture.vigemUnavailable", _owner, port));
            return false;
        }
        // Primero lo que no es el mando (teclado del botón de captura…), luego el mando.
        foreach (var node in others.Concat(pads))
        {
            if (_captured.Contains(node, StringComparer.OrdinalIgnoreCase)) continue;
            if (UsbCapture.IsCaptured(node) || UsbCapture.Capture(node)) _captured.Add(node);
            else Logger.Warning(BackendText.Get("padCapture.notCaptured", _owner, node));
        }
        var ready = pads.Where(p => _captured.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (ready.Count == 0) return true;   // el mando no pasó a WinUSB: sigue pendiente
        foreach (var pad in ready)
            if (!_readers.Any(r => string.Equals(r.InstanceId, pad, StringComparison.OrdinalIgnoreCase)))
                _readers.Add(new Xbox360PadReader(pad, _session, _owner, _client));
        _pending.Remove(port);
        _done.Add(port);
        Logger.Log(BackendText.Get("padCapture.captured", _owner, port, ready.Count, others.Count, _session));
        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _watch?.Dispose();
            _retry?.Dispose();
            foreach (var r in _readers) try { r.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }
            // El mando a su driver (xusb22) y las demás interfaces al suyo.
            // Uno desconectado (mando dormido) no se puede tocar ahora: queda anotado y UsbCapture
            // lo devuelve en cuanto vuelva a aparecer.
            foreach (var node in _captured) UsbCapture.Release(node);
            if (_captured.Count > 0)
                Logger.Log(BackendText.Get("padCapture.restored", _owner, _captured.Count));
            SteamXboxFilter.Release(OwnerKey);
        }
    }
}
