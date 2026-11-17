using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Core.Helpers;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace OpenStreamMS.Services.Gamepad;

/// <summary>
/// Aísla los mandos por sesión de Windows con <see cref="HidHide"/>:
/// <list type="bullet">
/// <item>cada mando virtual de ViGEmBus solo es visible en la sesión cuyo Sunshine lo creó;</item>
/// <item>los mandos físicos solo son visibles en la sesión de consola (el PC del host).</item>
/// </list>
/// <para>
/// Los mandos son dispositivos de toda la máquina: el Steam de cada sesión (host
/// incluido) los ve y se los queda. Aquí cada nodo de mando (clases HID, XUSB y Xbox
/// One, las que filtra HidHide) se añade a su lista negra con una sesión
/// (<c>HID\...!N</c>): HidHide deniega abrirlo a los procesos de cualquier otra sesión.
/// </para>
/// <para>
/// HidHide decide al abrir el dispositivo, así que la entrada tiene que estar antes de
/// que otro Steam lo abra: se escucha la enumeración de dispositivos
/// (CM_Register_Notification) y se oculta cada nodo en cuanto aparece. Si llega tarde
/// (ya arrancado), se reinicia ese nodo HID para cerrar los handles ya abiertos.
/// </para>
/// <para>
/// Qué Sunshine creó cada mando de ViGEmBus se deduce del log: Sunshine escribe
/// "Gamepad N will be ..." justo antes de enchufarlo en ViGEmBus. Los de OsmsPad (driver
/// propio, Native/OsmsPad) no hace falta adivinarlos: el driver pone en su ID de instancia la
/// sesión de Windows de quien lo creó (<c>...OSMS_S&lt;sesión&gt;_P&lt;n&gt;</c>). Del log solo
/// se saca su número en ese Sunshine, para la sesión compuesta.
/// </para>
/// <para>
/// Sesión compuesta: el mando N que crea su Sunshine va a la sesión del hueco N (N del log).
/// Si un miembro cambia de sesión de Windows, los mandos ya creados se reasignan.
/// </para>
/// </summary>
public sealed partial class GamepadSessionIsolation : BackgroundService
{
    private const string ViGEmBusHardwareId = @"Nefarius\ViGEmBus\Gen1";
    /// <summary>Dispositivo raíz de OsmsPad: sus hijos directos son los mandos, como en un bus ViGEm.</summary>
    private const string OsmsPadHardwareId = @"ROOT\OSMSPAD";
    private static readonly TimeSpan Fallback      = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BusRescan     = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MatchBefore   = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MatchAfter    = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AssignTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HidHideRetry  = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PhysicalRescan = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\]: \w+: Gamepad (\d+) will be", RegexOptions.Multiline)]
    private static partial Regex GamepadCreatedRegex();

    /// <summary>"OsmsPad controller P created for gamepad N": mando P del driver = mando N de ese Sunshine.</summary>
    [GeneratedRegex(@"OsmsPad controller (\d+) created for gamepad (\d+)")]
    private static partial Regex OsmsPadCreatedRegex();

    /// <summary>Sesión de Windows y número de mando en el ID de instancia de un mando OsmsPad.</summary>
    [GeneratedRegex(@"OSMS_S(\d+)_P(\d+)$", RegexOptions.IgnoreCase)]
    internal static partial Regex OsmsPadInstanceRegex();

    private readonly StreamSessionService _sessions;
    private readonly Seats.SeatManager _seats;
    private int _compositeGeneration = -1;

    private sealed class Pad
    {
        public required string InstanceId { get; init; }
        public DateTime FirstSeen { get; init; }
        public Guid? Session;
        public string SessionName = "";
        public uint WindowsSession;
        /// <summary>Sesión cuyo Sunshine lo creó y su número en ese Sunshine (-1 si no se sabe).</summary>
        public Guid? Creator;
        public int Index = -1;
        public bool GaveUp;
        public readonly HashSet<string> Hidden    = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Restarted = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LogCursor
    {
        public long Offset;
        public byte[] Head = [];
        public readonly List<(DateTime At, int Index)> Pending = [];   // creaciones aún sin mando asignado
        public readonly Dictionary<int, int> OsmsPadIndex = [];          // mando del driver → mando de este Sunshine
    }

    private readonly Dictionary<string, Pad>     _pads    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, LogCursor> _cursors = [];
    private readonly Channel<(string Id, bool Enumerated)> _arrivals = Channel.CreateUnbounded<(string, bool)>(new() { SingleReader = true });
    private List<string> _buses = [];
    private DateTime _lastBusScan = DateTime.MinValue;
    private SafeFileHandle? _hidHide;
    private DateTime _lastHidHideTry = DateTime.MinValue;
    private bool _hidHideWarned;

    /// <summary>
    /// HidHide está instalado y su driver aísla por sesión. False hasta que se abre: el panel
    /// lo muestra para que un mando "asignado" que se ve en todas las sesiones no pase inadvertido.
    /// </summary>
    public static bool SessionJailSupported { get; private set; }

    // Mandos físicos ocultados → sesión (de consola, o del puesto que los reclama) con la que se ocultaron
    private readonly Dictionary<string, uint> _physical = new(StringComparer.OrdinalIgnoreCase);
    private uint _console = NoSession;
    private DateTime _lastPhysicalScan = DateTime.MinValue;
    private int _seatGeneration = -1;
    private string _streamPads = "";
    private const uint NoSession = 0xFFFFFFFF;

    /// <summary>
    /// Interfaces de teclado o ratón de un mando asignado fuera de la consola, pasadas a WinUSB
    /// por este servicio (ver <see cref="SyncSideInputs"/>).
    /// </summary>
    private readonly HashSet<string> _sideInputs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Nodos de mandos físicos vistos alguna vez → su puerto USB, guardado en disco. Un mando
    /// inalámbrico que se duerme desaparece y vuelve a aparecer al despertar; si su "jail" de
    /// HidHide no estuviera ya puesto con la sesión correcta, la consola (Steam) lo abría en ese
    /// instante, antes de que este servicio lo viera. Con esto la entrada se mantiene y se
    /// reasigna (puesto encendido o apagado) también mientras el mando no está.
    /// </summary>
    private readonly Dictionary<string, string> _knownNodes = LoadKnownNodes();
    /// <summary>Sesión con la que está puesto el jail de cada nodo conocido ausente.</summary>
    private readonly Dictionary<string, uint> _absent = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string KnownNodesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "gamepad-nodes.json");

    public GamepadSessionIsolation(StreamSessionService sessions, Seats.SeatManager seats)
    {
        _sessions = sessions;
        _seats    = seats;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DisableControllerToVKMapping();

        using var notification = Cm.WatchEnumeration((id, enumerated) => _arrivals.Writer.TryWrite((id, enumerated)));
        if (notification is null)
            Logger.Warning("[Gamepad] No se pudo escuchar la llegada de dispositivos; solo se revisarán cada segundo.");

        // Todo el trabajo en este bucle (un solo hilo): llegadas al instante y una
        // revisión periódica para lo que se escape (mandos previos, asignaciones pendientes).
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(Fallback);
                try
                {
                    var dev = await _arrivals.Reader.ReadAsync(wait.Token);
                    do { Handle(dev.Id, dev.Enumerated); } while (_arrivals.Reader.TryRead(out dev));
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    Scan();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warning($"[Gamepad] Error vigilando mandos: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            ReleaseSideInputs();
            _hidHide?.Dispose();
        }
    }

    /// <summary>
    /// Abre HidHide si aún no lo está (se reintenta cada <see cref="HidHideRetry"/>: puede
    /// instalarse con el servicio ya en marcha), lo activa y limpia entradas antiguas.
    /// </summary>
    private bool EnsureHidHide()
    {
        if (_hidHide is not null) return true;
        if (DateTime.UtcNow - _lastHidHideTry < HidHideRetry) return false;
        _lastHidHideTry = DateTime.UtcNow;

        var h = HidHide.Open();
        if (h is null)
        {
            if (!_hidHideWarned)
            {
                _hidHideWarned = true;
                Logger.Warning("[Gamepad] HidHide no está instalado: los mandos serán visibles en todas las sesiones.");
            }
            return false;
        }
        try
        {
            if (!HidHide.GetActive(h))
            {
                HidHide.SetActive(h, true);
                Logger.Log("[Gamepad] HidHide activado.");
            }
        }
        catch (Win32Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo configurar HidHide: {ex.Message}");
            h.Dispose();
            return false;
        }
        _hidHide = h;
        // Una versión sin "jail" de sesión trata "instancia!N" como un id que no existe: el mando
        // no se oculta a nadie, y sin este aviso falla en silencio.
        var version = HidHide.DriverVersion();
        SessionJailSupported = version is null || version >= HidHide.MinSessionJailVersion;
        if (SessionJailSupported)
            Logger.Log($"[Gamepad] HidHide {version?.ToString() ?? "(versión desconocida)"} disponible: mandos aislados por sesión.");
        else
            Logger.Warning($"[Gamepad] HidHide {version} no aísla por sesión (hace falta {HidHide.MinSessionJailVersion} o posterior): " +
                           "los mandos serán visibles en todas las sesiones. Actualiza HidHide.");
        LogHidHideExceptions(h);
        RemoveStaleJails();
        return true;
    }

    /// <summary>
    /// La lista blanca de HidHide (y su modo inverso) se salta el "jail" de sesión: avisa de
    /// qué programas pueden leer los mandos de cualquier sesión.
    /// </summary>
    private static void LogHidHideExceptions(SafeFileHandle h)
    {
        try
        {
            var apps = HidHide.GetWhitelist(h);
            bool inverse = HidHide.GetInverse(h);
            Logger.Log($"[Gamepad] HidHide: modo inverso {(inverse ? "activado" : "desactivado")}; " +
                       $"lista blanca ({apps.Count}): {(apps.Count == 0 ? "vacía" : string.Join(" | ", apps))}.");
            if (!inverse && apps.Count > 0)
                Logger.Warning("[Gamepad] Los programas de la lista blanca de HidHide ven los mandos de todas las sesiones, " +
                               "aunque estén asignados a otra.");
        }
        catch (Win32Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo leer la lista blanca de HidHide: {ex.Message}");
        }
    }

    /// <summary>
    /// ControllerToVKMapping (mando → teclas de navegación de Windows) lee los mandos
    /// de todas las sesiones y rompe el aislamiento. Se desactiva.
    /// </summary>
    private static void DisableControllerToVKMapping()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\ControllerToVKMapping");
            if (key.GetValue("Enabled") is int v && v == 0) return;
            key.SetValue("Enabled", 0, RegistryValueKind.DWord);
            Logger.Log("[Gamepad] ControllerToVKMapping desactivado (ignora el aislamiento de mandos por sesión).");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo desactivar ControllerToVKMapping: {ex.Message}");
        }
    }

    private List<string> Buses()
    {
        if (DateTime.UtcNow - _lastBusScan > BusRescan || _buses.Count == 0)
        {
            _buses = FindViGEmBuses();
            _lastBusScan = DateTime.UtcNow;
        }
        return _buses;
    }

    /// <summary>
    /// Un nodo enumerado o arrancado: si es un mando ViGEm o cuelga de uno, se oculta a
    /// las demás sesiones; si es un mando físico, a todas menos la de consola. Una
    /// enumeración es un dispositivo nuevo aunque repita instance id (Sunshine reenchufa
    /// mandos): se olvida lo que se sabía de él.
    /// </summary>
    private void Handle(string instanceId, bool enumerated)
    {
        if (!EnsureHidHide()) return;
        if (enumerated)
        {
            _pads.Remove(instanceId);
            foreach (var p in _pads.Values) p.Hidden.Remove(instanceId);
            _physical.Remove(instanceId);
        }

        if (ViGEmPadOf(instanceId) is { } padId)
        {
            var pad = Track(padId, _sessions.GetRunningSunshineSessions());
            if (pad.Session is not null) Hide(pad);
        }
        else if (Cm.IsGameController(instanceId))
        {
            HidePhysical([instanceId]);
        }
    }

    /// <summary>El mando ViGEm (hijo directo de un bus) del que cuelga el nodo, o null.</summary>
    private string? ViGEmPadOf(string instanceId)
    {
        string child = instanceId;
        for (string? parent = Cm.Parent(child); parent is not null; child = parent, parent = Cm.Parent(parent))
            if (Buses().Contains(parent, StringComparer.OrdinalIgnoreCase)) return child;
        return null;
    }

    private void Scan()
    {
        if (!EnsureHidHide()) return;
        ScanPhysical();

        var running = _sessions.GetRunningSunshineSessions();
        if (running.Count == 0 && _pads.Count == 0) return;

        // Mandos presentes = hijos directos de cada bus ViGEm
        var present = Buses().SelectMany(b => Cm.Children(b)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _pads.Values.Where(p => !present.Contains(p.InstanceId)).ToList())
        {
            _pads.Remove(gone.InstanceId);
            Unhide(gone);
        }
        foreach (var id in _cursors.Keys.Where(id => running.All(s => s.Id != id)).ToList())
            _cursors.Remove(id);

        foreach (var id in present)
        {
            var pad = Track(id, running);
            if (pad.Session is not null) Hide(pad);
        }

        int generation = _sessions.CompositeGeneration;
        if (generation != _compositeGeneration)
        {
            _compositeGeneration = generation;
            foreach (var pad in _pads.Values) Retarget(pad);
        }
    }

    private Pad Track(string padId,
        IReadOnlyList<(Guid Id, string Name, uint RdpSessionId, string SunshineLog)> running)
    {
        var now = DateTime.Now;
        if (!_pads.TryGetValue(padId, out var pad))
            _pads[padId] = pad = new Pad { InstanceId = padId, FirstSeen = now };

        if (pad.Session is null && !pad.GaveUp)
        {
            foreach (var s in running) ReadGamepadEvents(s.Id, s.SunshineLog);
            TryAssign(pad, running, now);
        }
        return pad;
    }

    // ── Asignación mando → sesión ────────────────────────────────────────────

    private void TryAssign(Pad pad,
        IReadOnlyList<(Guid Id, string Name, uint RdpSessionId, string SunshineLog)> running, DateTime now)
    {
        if (OsmsPadInstanceRegex().Match(pad.InstanceId) is { Success: true } osms)
        {
            AssignOsmsPad(pad, running, now,
                          uint.Parse(osms.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                          int.Parse(osms.Groups[2].ValueSpan, CultureInfo.InvariantCulture));
            return;
        }

        Guid? owner = null;
        (DateTime At, int Index) best = (DateTime.MaxValue, -1);
        foreach (var (sessionId, cursor) in _cursors)
        {
            foreach (var e in cursor.Pending)
            {
                if (e.At < pad.FirstSeen - MatchBefore || e.At > pad.FirstSeen + MatchAfter) continue;
                if (e.At < best.At) { best = e; owner = sessionId; }
            }
        }

        if (owner is { } o)
        {
            _cursors[o].Pending.Remove(best);
            pad.Index = best.Index;
        }
        else if (running.Count == 1)
            owner = running[0].Id;                    // sin línea en el log, pero solo hay una sesión
        else if (now - pad.FirstSeen < AssignTimeout)
            return;                                   // aún puede llegar la línea del log
        else
        {
            pad.GaveUp = true;
            Logger.Warning($"[Gamepad] No se pudo saber qué sesión creó el mando {pad.InstanceId}; queda visible en todas.");
            return;
        }

        var session = running.FirstOrDefault(s => s.Id == owner);
        if (session.Id == Guid.Empty) return;         // la sesión del log ya no está en marcha
        AssignTo(pad, session);
    }

    /// <summary>
    /// Mando de OsmsPad: la sesión de Windows viene en su ID. Si es la de una sesión de
    /// OpenStreamMS en marcha, se trata como un mando de su Sunshine (sesión compuesta incluida); si no
    /// (p. ej. OsmsPadTest), se aísla igualmente en esa sesión de Windows.
    /// </summary>
    private void AssignOsmsPad(Pad pad,
        IReadOnlyList<(Guid Id, string Name, uint RdpSessionId, string SunshineLog)> running,
        DateTime now, uint windowsSession, int driverPad)
    {
        // La compuesta corre en la sesión de Windows de uno de sus miembros: de las dos, la que
        // creó el mando es la que lo tiene en su log; sin log aún, la compuesta.
        var candidates = running.Where(s => s.RdpSessionId == windowsSession).ToList();
        var session = candidates.FirstOrDefault(s => _cursors.TryGetValue(s.Id, out var c) && c.OsmsPadIndex.ContainsKey(driverPad));
        if (session.Id == Guid.Empty)
            session = candidates.FirstOrDefault(s => _sessions.Get(s.Id)?.IsComposite == true);
        if (session.Id == Guid.Empty)
            session = candidates.FirstOrDefault();
        if (session.Id == Guid.Empty)
        {
            pad.Session = Guid.Empty;
            pad.SessionName = $"Windows {windowsSession}";
            pad.WindowsSession = windowsSession;
            Logger.Log($"[Gamepad] Mando OsmsPad {pad.InstanceId} → sesión de Windows {windowsSession} (no es de ninguna sesión de OpenStreamMS)");
            return;
        }
        // El número del mando en su Sunshine solo hace falta para la sesión compuesta: se espera un poco
        // a que salga en el log y, si no, el mando se queda en la sesión que lo creó.
        if (_cursors.TryGetValue(session.Id, out var cursor) && cursor.OsmsPadIndex.TryGetValue(driverPad, out int index))
            pad.Index = index;
        else if (now - pad.FirstSeen < MatchAfter)
            return;
        AssignTo(pad, session);
    }

    private void AssignTo(Pad pad, (Guid Id, string Name, uint RdpSessionId, string SunshineLog) session)
    {
        Guid owner = session.Id;
        pad.Creator = owner;

        // Sesión compuesta: el mando N es de la sesión del hueco N.
        if (_sessions.CompositePadTarget(session.Id, pad.Index) is { } target && target != session.Id &&
            _sessions.Get(target) is { RdpSessionId: > 0 } member)
        {
            pad.Session = target;
            pad.SessionName = member.Name;
            pad.WindowsSession = member.RdpSessionId;
            Logger.Log($"[Gamepad] Mando {pad.Index} de '{session.Name}' ({pad.InstanceId}) → sesión '{member.Name}' (sesión de Windows {member.RdpSessionId})", target);
            return;
        }
        pad.Session = owner;
        pad.SessionName = session.Name;
        pad.WindowsSession = session.RdpSessionId;
        Logger.Log($"[Gamepad] Mando {pad.InstanceId} → sesión '{session.Name}' (sesión de Windows {session.RdpSessionId})", owner);
    }

    /// <summary>
    /// La sesión compuesta cambió: el mando va a la sesión que le toca ahora (o vuelve a la que lo creó)
    /// y, si cambia, se oculta de nuevo con la sesión nueva y se reinicia para cerrar los handles
    /// que tuviera abiertos la anterior.
    /// </summary>
    private void Retarget(Pad pad)
    {
        if (pad.Creator is not { } creator || pad.Session is null) return;
        var target = _sessions.CompositePadTarget(creator, pad.Index) ?? creator;
        if (_sessions.Get(target) is not { RdpSessionId: > 0 } session) return;
        if (target == pad.Session && session.RdpSessionId == pad.WindowsSession) return;

        if (pad.Hidden.Count > 0 && !UpdateJails(add: [], remove: pad.Hidden)) return;
        pad.Hidden.Clear();
        pad.Restarted.Clear();
        pad.Session = target;
        pad.SessionName = session.Name;
        pad.WindowsSession = session.RdpSessionId;
        Logger.Log($"[Gamepad] Mando {pad.Index} ({pad.InstanceId}) pasa a la sesión '{session.Name}' (sesión de Windows {session.RdpSessionId}) por un cambio en la sesión compuesta.", target);
        Hide(pad);
    }

    private void ReadGamepadEvents(Guid sessionId, string logPath)
    {
        if (!_cursors.TryGetValue(sessionId, out var cur))
            _cursors[sessionId] = cur = new LogCursor();
        try
        {
            if (!File.Exists(logPath)) return;
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = ReadHead(fs, 256);
            if (cur.Offset > fs.Length || (cur.Offset > 0 && !head.AsSpan(0, Math.Min(head.Length, cur.Head.Length)).SequenceEqual(cur.Head)))
                cur.Offset = 0;                       // log rotado al relanzar Sunshine
            if (fs.Length == cur.Offset) return;

            fs.Seek(cur.Offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8, false, 4096, leaveOpen: true);
            string chunk = sr.ReadToEnd();
            cur.Offset = fs.Length;
            cur.Head = head;

            var cutoff = DateTime.Now - MatchBefore - AssignTimeout;
            foreach (Match m in OsmsPadCreatedRegex().Matches(chunk))
                cur.OsmsPadIndex[int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture)] =
                    int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
            foreach (Match m in GamepadCreatedRegex().Matches(chunk))
            {
                if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) && t >= cutoff)
                    cur.Pending.Add((t, int.TryParse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out var n) ? n : -1));
            }
            cur.Pending.RemoveAll(e => e.At < cutoff);
        }
        catch (IOException) { /* Sunshine escribiendo; se reintenta en la siguiente pasada */ }
    }

    private static byte[] ReadHead(FileStream fs, int count)
    {
        var buf = new byte[count];
        fs.Seek(0, SeekOrigin.Begin);
        int n = fs.ReadAtLeast(buf, count, throwOnEndOfStream: false);
        return n == count ? buf : buf[..n];
    }

    // ── HidHide ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Oculta a las demás sesiones los nodos del mando que filtra HidHide (el propio mando
    /// si es XUSB y sus hijos HID). Un nodo que ya estaba arrancado pudo abrirse desde otra
    /// sesión (HidHide no revoca handles), así que se reinicia una vez para cerrarlos.
    /// </summary>
    private void Hide(Pad pad)
    {
        var nodes = Cm.Descendants(pad.InstanceId).Prepend(pad.InstanceId)
                      .Where(n => Cm.IsHidHideClass(n) && !pad.Hidden.Contains(n))
                      .ToList();
        if (nodes.Count == 0) return;

        var started = nodes.Where(Cm.IsOpenableAndStarted).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!UpdateJails(add: nodes.Select(n => HidHide.JailEntry(n, pad.WindowsSession)), remove: nodes)) return;

        foreach (var node in nodes)
        {
            pad.Hidden.Add(node);
            if (started.Contains(node) && pad.Restarted.Add(node))
            {
                try
                {
                    DevSetup.Restart(node);
                    Logger.Log($"[Gamepad] {node} visible solo en la sesión '{pad.SessionName}' ({pad.WindowsSession}); reiniciado para cerrar handles de otras sesiones.", pad.Session);
                }
                catch (Win32Exception ex)
                {
                    Logger.Warning($"[Gamepad] {node} ocultado tarde y no se pudo reiniciar: {ex.Message}. " +
                                   "Un proceso de otra sesión que ya lo tuviera abierto lo sigue viendo.");
                }
            }
            else
            {
                Logger.Log($"[Gamepad] {node} visible solo en la sesión '{pad.SessionName}' ({pad.WindowsSession}).", pad.Session);
            }
        }
    }

    /// <summary>
    /// Revisa los mandos físicos presentes: al cambiar la sesión de consola (otro usuario
    /// en el PC, cambio rápido de usuario) se vuelven a ocultar con la nueva sesión.
    /// </summary>
    private void ScanPhysical()
    {
        uint console = WTSGetActiveConsoleSessionId();
        if (console == NoSession) return;   // transición de sesión: se reintenta luego

        // Un puesto encendido o apagado, o una sesión de stream con mandos que arranca, para o
        // cambia sus mandos, cambia a qué sesión va cada mando: fuerza una pasada.
        int seatGen = _seats.Generation;
        string streamPads = _sessions.GamepadAssignmentSignature();
        bool assignmentsChanged = seatGen != _seatGeneration || streamPads != _streamPads;
        if (console == _console && !assignmentsChanged && DateTime.UtcNow - _lastPhysicalScan < PhysicalRescan) return;
        _lastPhysicalScan = DateTime.UtcNow;
        _seatGeneration = seatGen;
        _streamPads = streamPads;

        if (console != _console)
        {
            if (_console != NoSession)
                Logger.Log($"[Gamepad] La sesión de consola pasa de {_console} a {console}: se reasignan los mandos físicos.");
            _console = console;
        }
        foreach (var gone in _physical.Keys.Where(id => !Cm.Exists(id)).ToList())
            _physical.Remove(gone);

        HidePhysical(WithHidHideChildren(Cm.GameControllers().Where(id => ViGEmPadOf(id) is null)));
        JailAbsent();
        SyncSideInputs();
        SyncGuideButton();
    }

    /// <summary>
    /// Algunos mandos (GameSir, 8BitDo…) traen en el mismo USB una interfaz de teclado o ratón:
    /// el botón de captura o los atajos mandan teclas por ahí. Windows lleva los teclados y
    /// ratones físicos a la sesión de consola desde el kernel, donde HidHide no llega, así que
    /// con el mando asignado a otra sesión esas teclas acababan en la consola (captura de
    /// pantalla, Game Bar…). Mientras el mando va a otra sesión, esas interfaces pasan a WinUSB
    /// con <see cref="Seats.UsbCapture"/> (anotado en disco: si el servicio se cae, se deshace al
    /// arrancar) y vuelven a su driver cuando el mando vuelve a la consola.
    /// </summary>
    private void SyncSideInputs()
    {
        var ports = _physical.Where(p => p.Value != _console && Cm.Exists(p.Key))
                             .Select(p => DeviceTopology.UsbPortOf(p.Key))
                             .OfType<string>()
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Una interfaz ya en WinUSB no tiene teclado debajo y FindTargets no la devuelve:
        // las capturadas siguen queriéndose mientras su mando siga asignado.
        var want = ports.SelectMany(Seats.UsbCapture.FindTargets)
                        .Concat(_sideInputs.Where(n => DeviceTopology.UsbPortOf(n) is { } p && ports.Contains(p)))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var node in _sideInputs.Where(n => !want.Contains(n)).ToList())
        {
            // Desenchufado: sigue apuntada; al volver se decide con el mando ya presente.
            if (!Cm.Exists(node)) continue;
            _sideInputs.Remove(node);
            if (Seats.UsbCapture.Release(node))
                Logger.Log($"[Gamepad] {node} (teclado o ratón del mando) vuelve a su driver: el mando está en la consola.");
        }

        foreach (var node in want)
        {
            // Ya en WinUSB: nuestra, o de un puesto que la tiene como teclado o ratón propio.
            if (Seats.UsbCapture.IsCaptured(node)) continue;
            if (Seats.UsbCapture.Capture(node))
            {
                _sideInputs.Add(node);
                Logger.Log($"[Gamepad] {node} (teclado o ratón del mando) apartado mientras el mando está en otra sesión: " +
                           "sus teclas (p. ej. el botón de captura) ya no llegan a la consola.");
            }
        }
    }

    private void ReleaseSideInputs()
    {
        foreach (var node in _sideInputs)
            if (Cm.Exists(node)) Seats.UsbCapture.Release(node);
        _sideInputs.Clear();
        Seats.GuideButtonGuard.Release(_guideSids.ToList());
        _guideSids.Clear();
    }

    /// <summary>Usuarios a los que este servicio apagó la Game Bar del botón Guía.</summary>
    private readonly HashSet<string> _guideSids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// El botón Guía no pasa por HidHide: Windows lo lee desde un servicio del sistema y abre la
    /// Game Bar en todas las sesiones. Los puestos ya lo apagan mientras están encendidos
    /// (<see cref="Seats.GuideButtonGuard"/>); aquí se hace lo mismo mientras un mando físico
    /// vaya a una sesión de stream: para el usuario de esa sesión y el de la consola.
    /// </summary>
    private void SyncGuideButton()
    {
        var want = _physical.Where(p => p.Value != _console && Cm.Exists(p.Key))
                            .Select(p => p.Value)
                            .Distinct()
                            .SelectMany(Seats.GuideButtonGuard.UsersFor)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var add = want.Except(_guideSids, StringComparer.OrdinalIgnoreCase).ToList();
        var remove = _guideSids.Except(want, StringComparer.OrdinalIgnoreCase).ToList();
        if (add.Count > 0) Seats.GuideButtonGuard.Engage(add);
        if (remove.Count > 0) Seats.GuideButtonGuard.Release(remove);
        _guideSids.ExceptWith(remove);
        _guideSids.UnionWith(add);
    }

    /// <summary>
    /// Cada mando con todos los nodos de las clases que filtra HidHide de su mismo USB físico.
    /// Un mando puede publicar la entrada también por colecciones HID que Windows no marca como
    /// mando (p. ej. "definidas por el fabricante"), hijas del mando (Xbox One, issue #11) o en
    /// otra interfaz del compuesto (GameSir: MI_02 junto al XUSB de MI_00): sin ocultarlas, otras
    /// sesiones (Steam en la consola) lo leían por ahí aunque el nodo del mando estuviera oculto.
    /// Se dejan fuera las ramas con teclado o ratón: esas no las para HidHide (van a la consola
    /// desde el kernel) y se apartan a WinUSB en <see cref="SyncSideInputs"/>.
    /// </summary>
    private static List<string> WithHidHideChildren(IEnumerable<string> controllers) =>
        controllers
            .SelectMany(id => SameUsbDevice(id).Where(n => Cm.IsHidHideClass(n) && !HasKeyboardOrMouse(n)).Prepend(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Nodos del mismo USB físico que el mando; solo sus hijos si no cuelga de un USB.</summary>
    private static List<string> SameUsbDevice(string controller) =>
        DeviceTopology.UsbDeviceRootOf(controller) is { } root ? Cm.Descendants(root) : Cm.Descendants(controller);

    private static bool HasKeyboardOrMouse(string node) =>
        Cm.Descendants(node).Prepend(node).Any(n => Cm.ClassGuidOf(n) is { } g &&
            (g.Equals(MouseClassGuid, StringComparison.OrdinalIgnoreCase) || g.Equals(KeyboardClassGuid, StringComparison.OrdinalIgnoreCase)));

    private const string MouseClassGuid    = "{4d36e96f-e325-11ce-bfc1-08002be10318}";
    private const string KeyboardClassGuid = "{4d36e96b-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Deja cada mando físico visible solo en su sesión destino: la del puesto que lo reclama
    /// (por puerto USB), o la de consola si ningún puesto lo tiene asignado.
    /// <para>
    /// HidHide decide al abrir y no revoca handles: un mando arrancado que cambia de destino
    /// (puesto encendido o apagado, otra sesión de consola) se reinicia para cerrar los que
    /// tenga la sesión anterior; si no, Steam o el juego de esa sesión lo siguen leyendo.
    /// Uno visto por primera vez solo se reinicia si otra sesión pudo abrirlo antes (hay
    /// sesiones de stream o puestos encendidos).
    /// </para>
    /// </summary>
    private void HidePhysical(IReadOnlyList<string> ids)
    {
        if (_console == NoSession) _console = WTSGetActiveConsoleSessionId();
        if (_console == NoSession) return;

        // Destino de cada mando: la sesión del puesto que lo reclama, o la consola.
        var nodes = ids.Select(id => (Id: id, Target: TargetSessionFor(id)))
                       .Where(n => !_physical.TryGetValue(n.Id, out var s) || s != n.Target)
                       .ToList();
        if (nodes.Count == 0) return;

        bool otherSessions = _sessions.GetRunningSunshineSessions().Count > 0 || _seats.AnyRunning;
        // Se reinicia el nodo más alto de cada rama que cambia: reiniciarlo reinicia también sus
        // hijos y cierra los handles de todos. Solo las hojas no basta: Steam y XInput abren el
        // XUSB (MI_00) de un Xbox 360 aunque tenga debajo su colección HID (IG_00), y ese handle
        // abierto antes del cambio seguía leyendo el mando en la consola.
        var changed = nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var started = nodes.Where(n => Cm.IsStarted(n.Id) && !HasAncestorIn(n.Id, changed))
                           .Select(n => n.Id)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!UpdateJails(add: nodes.Select(n => HidHide.JailEntry(n.Id, n.Target)), remove: nodes.Select(n => n.Id))) return;

        Remember(nodes.Select(n => n.Id));
        foreach (var (node, target) in nodes)
        {
            _absent.Remove(node);
            bool moved = _physical.TryGetValue(node, out var previous);
            _physical[node] = target;
            Logger.Log(target == _console
                ? $"[Gamepad] Mando físico {node} visible solo en la sesión de consola ({target})."
                : $"[Gamepad] Mando físico {node} asignado a un puesto o sesión de stream: visible solo en la sesión {target}.");
            if (!started.Contains(node) || !(moved || otherSessions)) continue;
            try
            {
                // Al cambiar de sesión, si alguien lo tiene abierto (Steam) se recicla el puerto: el
                // jail no revoca handles. Al aparecer no: lo abriría enseguida la sesión destino y se
                // entraría en bucle.
                bool ok = moved ? DevSetup.RestartOrCycle(node, $"para que la sesión {previous} deje de leerlo")
                                : DevSetup.Restart(node);
                if (moved && ok) Logger.Log($"[Gamepad] {node} reiniciado: la sesión {previous} deja de leerlo.");
            }
            catch (Win32Exception ex)
            {
                Logger.Warning($"[Gamepad] No se pudo reiniciar {node}: {ex.Message}. " +
                               "La sesión que ya lo tuviera abierto lo sigue viendo hasta que lo cierre.");
            }
        }
    }

    private static bool HasAncestorIn(string node, HashSet<string> set)
    {
        for (var p = Cm.Parent(node); p is not null; p = Cm.Parent(p))
            if (set.Contains(p)) return true;
        return false;
    }

    /// <summary>
    /// Sesión de Windows a la que debe ir un mando físico: la de su puesto encendido; si no, la de
    /// la sesión de stream en marcha que lo tenga asignado; si no, la consola.
    /// </summary>
    private uint TargetSessionFor(string node) =>
        DeviceTopology.UsbPortOf(node) is { } usb ? TargetSessionForPort(usb) : _console;

    private uint TargetSessionForPort(string usb)
    {
        if (_seats.TryGetSeatWindowsSession(usb, out var seatSession)) return seatSession;
        if (_sessions.TryGetGamepadWindowsSession(usb, out var streamSession)) return streamSession;
        return _console;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Quita de HidHide todas las entradas "jail" (las de sesión que pone este servicio) y
    /// deja las del usuario. Para la desinstalación, con el servicio ya parado.
    /// </summary>
    public static void ClearJails()
    {
        using var h = HidHide.Open();
        if (h is null) return;
        try
        {
            var list = HidHide.GetBlacklist(h);
            var kept = list.Where(e => HidHide.JailedInstance(e) is null).ToList();
            if (kept.Count != list.Count) HidHide.SetBlacklist(h, kept);
        }
        catch (Win32Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudieron quitar las entradas de sesión de HidHide: {ex.Message}");
        }
    }

    /// <summary>Quita de la lista negra los nodos de un mando que ya no existe.</summary>
    private void Unhide(Pad pad)
    {
        if (pad.Hidden.Count > 0 && UpdateJails(add: [], remove: pad.Hidden))
            Logger.Log($"[Gamepad] Mando {pad.InstanceId} retirado; sus nodos HID salen de la lista negra de HidHide.", pad.Session);
    }

    /// <summary>
    /// Al arrancar: quita las entradas "jail" de dispositivos que ya no existen (de una
    /// ejecución anterior que no pudo limpiar). Las entradas sin sesión son del usuario.
    /// </summary>
    private void RemoveStaleJails()
    {
        // Los nodos conocidos de mandos físicos se quedan: JailAbsent los pone en su sesión.
        var stale = HidHide.GetBlacklist(_hidHide!)
                           .Select(HidHide.JailedInstance)
                           .OfType<string>()
                           .Where(id => !Cm.Exists(id) && !_knownNodes.ContainsKey(id))
                           .ToList();
        if (stale.Count > 0 && UpdateJails(add: [], remove: stale))
            Logger.Log($"[Gamepad] Quitadas {stale.Count} entradas antiguas de la lista negra de HidHide.");
    }

    /// <summary>
    /// Mantiene el jail de los nodos conocidos que ahora no están (mando dormido o desenchufado)
    /// con la sesión a la que irían por su puerto: al reaparecer, HidHide ya los oculta a las
    /// demás sesiones desde la primera apertura.
    /// </summary>
    private void JailAbsent()
    {
        if (_console == NoSession) return;
        var changes = _knownNodes
            .Where(k => !Cm.Exists(k.Key))
            .Select(k => (Id: k.Key, Target: TargetSessionForPort(k.Value)))
            .Where(n => !_absent.TryGetValue(n.Id, out var s) || s != n.Target)
            .ToList();
        if (changes.Count == 0) return;
        if (!UpdateJails(add: changes.Select(n => HidHide.JailEntry(n.Id, n.Target)), remove: changes.Select(n => n.Id))) return;
        foreach (var (id, target) in changes)
        {
            bool known = _absent.ContainsKey(id);
            _absent[id] = target;
            if (known)
                Logger.Log($"[Gamepad] Mando físico {id} (no conectado ahora) pasa a la sesión {target}: lo estará en cuanto aparezca.");
        }
    }

    /// <summary>Anota los nodos con su puerto USB (los que cuelgan de un USB) y guarda si hay nuevos.</summary>
    private void Remember(IEnumerable<string> nodes)
    {
        bool changed = false;
        foreach (var id in nodes)
        {
            if (DeviceTopology.UsbPortOf(id) is not { } port) continue;
            if (_knownNodes.TryGetValue(id, out var old) && old.Equals(port, StringComparison.OrdinalIgnoreCase)) continue;
            _knownNodes[id] = port;
            changed = true;
        }
        if (!changed) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KnownNodesPath)!);
            File.WriteAllText(KnownNodesPath, JsonSerializer.Serialize(_knownNodes));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo guardar {KnownNodesPath}: {ex.Message}");
        }
    }

    private static Dictionary<string, string> LoadKnownNodes()
    {
        try
        {
            if (File.Exists(KnownNodesPath) &&
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KnownNodesPath)) is { } d)
                return new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo leer {KnownNodesPath}: {ex.Message}");
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lectura-modificación-escritura de la lista negra: quita las entradas jail de
    /// <paramref name="remove"/> y añade <paramref name="add"/>. El resto (las del usuario) se respeta.
    /// </summary>
    private bool UpdateJails(IEnumerable<string> add, IEnumerable<string> remove)
    {
        try
        {
            var drop = remove.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = HidHide.GetBlacklist(_hidHide!)
                              .Where(e => HidHide.JailedInstance(e) is not { } id || !drop.Contains(id))
                              .Concat(add)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();
            HidHide.SetBlacklist(_hidHide!, list);
            return true;
        }
        catch (Win32Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo actualizar la lista negra de HidHide: {ex.Message}");
            return false;
        }
    }

    /// <summary>Buses de ViGEm y dispositivos raíz de OsmsPad: sus hijos directos son mandos.</summary>
    private static List<string> FindViGEmBuses() =>
        Cm.DeviceIds(@"ROOT\SYSTEM")
          .Where(id => Cm.HardwareIds(id).Any(h => h.Equals(ViGEmBusHardwareId, StringComparison.OrdinalIgnoreCase) ||
                                                    h.Equals(OsmsPadHardwareId, StringComparison.OrdinalIgnoreCase)))
          .ToList();

    // ── Interop ──────────────────────────────────────────────────────────────

    internal static class Cm
    {
        private const int CR_SUCCESS = 0;
        private const int CM_LOCATE_DEVNODE_NORMAL = 0;
        private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
        private const int CM_GETIDLIST_FILTER_PRESENT = 0x100;
        private const int CM_DRP_HARDWAREID = 0x2;
        private const uint DN_STARTED = 0x8;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out int parent, int devInst, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Child(out int child, int devInst, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Sibling(out int sibling, int devInst, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, int devInst, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(int devInst, char[] buffer, int len, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out int len, string? filter, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, int len, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(int devInst, int property, out int type, byte[]? buffer, ref int len, int flags);

        // ── Notificación de enumeración ──────────────────────────────────────
        private const int CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES = 0x2;
        private const int CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE = 2;
        private const int CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED = 4;
        private const int CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED = 5;
        private const int FilterSize = 416;   // sizeof(CM_NOTIFY_FILTER) en x64

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int NotifyCallback(IntPtr hNotify, IntPtr context, int action, IntPtr eventData, int eventDataSize);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Register_Notification(IntPtr filter, IntPtr context, NotifyCallback callback, out IntPtr handle);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Unregister_Notification(IntPtr handle);

        private sealed class Registration(IntPtr handle, NotifyCallback callback) : IDisposable
        {
            private NotifyCallback? _keepAlive = callback;   // el delegado no puede recolectarse mientras esté registrado
            public void Dispose()
            {
                if (_keepAlive is null) return;
                CM_Unregister_Notification(handle);
                _keepAlive = null;
            }
        }

        /// <summary>
        /// Avisa con el instance id de cada dispositivo que se enumera (true) o arranca. El aviso
        /// llega en un hilo del sistema: <paramref name="onDevice"/> debe ser rápido.
        /// </summary>
        public static IDisposable? WatchEnumeration(Action<string, bool> onDevice)
        {
            NotifyCallback callback = (_, _, action, data, _) =>
            {
                if ((action == CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED || action == CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED)
                    && data != IntPtr.Zero && Marshal.PtrToStringUni(data + 8) is { Length: > 0 } id)
                    onDevice(id, action == CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED);
                return 0;
            };

            IntPtr filter = Marshal.AllocHGlobal(FilterSize);
            try
            {
                for (int i = 0; i < FilterSize; i++) Marshal.WriteByte(filter, i, 0);
                Marshal.WriteInt32(filter, 0, FilterSize);
                Marshal.WriteInt32(filter, 4, CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES);
                Marshal.WriteInt32(filter, 8, CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE);
                return CM_Register_Notification(filter, IntPtr.Zero, callback, out var handle) == CR_SUCCESS
                    ? new Registration(handle, callback)
                    : null;
            }
            finally { Marshal.FreeHGlobal(filter); }
        }

        public static IEnumerable<string> DeviceIds(string? filter,
            int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT)
        {
            if (CM_Get_Device_ID_List_SizeW(out int len, filter, flags) != CR_SUCCESS || len <= 1) return [];
            var buf = new char[len];
            if (CM_Get_Device_ID_ListW(filter, buf, len, flags) != CR_SUCCESS) return [];
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        public static IEnumerable<string> HardwareIds(string deviceId) => RegistryStrings(deviceId, CM_DRP_HARDWAREID);

        private static string[] RegistryStrings(string deviceId, int property)
        {
            if (CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return [];
            int len = 0;
            CM_Get_DevNode_Registry_PropertyW(inst, property, out _, null, ref len, 0);
            if (len == 0) return [];
            var buf = new byte[len];
            if (CM_Get_DevNode_Registry_PropertyW(inst, property, out _, buf, ref len, 0) != CR_SUCCESS) return [];
            return Encoding.Unicode.GetString(buf, 0, len).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private const int CM_DRP_ADDRESS = 0x1D;

        /// <summary>Dirección del nodo en su bus (para un USB, el número de puerto del hub).</summary>
        public static uint? Address(string deviceId)
        {
            if (CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return null;
            var buf = new byte[4];
            int len = 4;
            return CM_Get_DevNode_Registry_PropertyW(inst, CM_DRP_ADDRESS, out _, buf, ref len, 0) == CR_SUCCESS
                ? BitConverter.ToUInt32(buf, 0) : null;
        }

        public static string? Parent(string deviceId) =>
            CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS
            && CM_Get_Parent(out int parent, inst, 0) == CR_SUCCESS
                ? Id(parent)
                : null;

        /// <summary>
        /// Arrancado y sin hijos: es el nodo que abren las aplicaciones (la colección HID, o
        /// el XUSB si no tiene hijo HID). Reiniciarlo cierra sus handles sin tocar el resto.
        /// </summary>
        public static bool IsOpenableAndStarted(string deviceId) => IsStarted(deviceId) && Children(deviceId).Count == 0;

        public static bool IsStarted(string deviceId) =>
            CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS
            && CM_Get_DevNode_Status(out uint status, out _, inst, 0) == CR_SUCCESS
            && (status & DN_STARTED) != 0;

        private const int CM_DRP_COMPATIBLEIDS = 0x3;
        private const int CM_DRP_CLASSGUID = 0x9;
        private const int CM_GETIDLIST_FILTER_CLASS = 0x200;

        // Clases que filtra HidHide (su instalador lo registra como UpperFilter de ellas)
        private const string HidClass           = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";
        private const string XnaCompositeClass  = "{d61ca365-5af4-4486-998b-9db4734c6ca3}";   // Xbox 360 / XUSB
        private const string XboxCompositeClass = "{05f5cfe2-4733-4950-a6bb-07aad01a3a84}";   // Xbox One / GIP

        private static string? ClassGuid(string deviceId) =>
            RegistryStrings(deviceId, CM_DRP_CLASSGUID).FirstOrDefault();

        public static string? ClassGuidOf(string deviceId) => ClassGuid(deviceId);

        public static bool IsHidHideClass(string deviceId) =>
            ClassGuid(deviceId) is { } c &&
            (c.Equals(HidClass, StringComparison.OrdinalIgnoreCase) ||
             c.Equals(XnaCompositeClass, StringComparison.OrdinalIgnoreCase) ||
             c.Equals(XboxCompositeClass, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Mando o joystick: un nodo XUSB/Xbox One, o una colección HID de juego
        /// (id compatible HID_DEVICE_SYSTEM_GAME). Teclados y ratones no lo son.
        /// </summary>
        public static bool IsGameController(string deviceId) => ClassGuid(deviceId) switch
        {
            { } c when c.Equals(XnaCompositeClass, StringComparison.OrdinalIgnoreCase)
                    || c.Equals(XboxCompositeClass, StringComparison.OrdinalIgnoreCase) => true,
            { } c when c.Equals(HidClass, StringComparison.OrdinalIgnoreCase) =>
                RegistryStrings(deviceId, CM_DRP_COMPATIBLEIDS)
                    .Any(id => id.Equals("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };

        public static IEnumerable<string> CompatibleIds(string deviceId) => RegistryStrings(deviceId, CM_DRP_COMPATIBLEIDS);

        /// <summary>Mandos presentes (de cualquier origen) en las clases que filtra HidHide.</summary>
        public static IEnumerable<string> GameControllers() =>
            new[] { HidClass, XnaCompositeClass, XboxCompositeClass }
                .SelectMany(c => DeviceIds(c, CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT))
                .Where(IsGameController);

        public static bool Exists(string deviceId) =>
            CM_Locate_DevNodeW(out _, deviceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;

        public static List<string> Children(string deviceId)
        {
            var result = new List<string>();
            if (CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return result;
            if (CM_Get_Child(out int child, inst, 0) != CR_SUCCESS) return result;
            do
            {
                if (Id(child) is { } id) result.Add(id);
            }
            while (CM_Get_Sibling(out child, child, 0) == CR_SUCCESS);
            return result;
        }

        public static List<string> Descendants(string deviceId)
        {
            var result = new List<string>();
            foreach (var c in Children(deviceId))
            {
                result.Add(c);
                result.AddRange(Descendants(c));
            }
            return result;
        }

        private const int CM_DRP_DEVICEDESC   = 0x1;
        private const int CM_DRP_SERVICE      = 0x5;
        private const int CM_DRP_FRIENDLYNAME = 0xD;
        private const int CM_DRP_UPPERFILTERS = 0x12;
        private const int CM_DRP_LOWERFILTERS = 0x13;

        /// <summary>Todos los nodos presentes.</summary>
        public static IEnumerable<string> AllPresent() => DeviceIds(null, CM_GETIDLIST_FILTER_PRESENT);

        /// <summary>Servicio (driver de función) del nodo, o null.</summary>
        public static string? ServiceOf(string deviceId) => RegistryStrings(deviceId, CM_DRP_SERVICE).FirstOrDefault();

        /// <summary>
        /// Una línea con lo que hace falta para saber por dónde llega un mando a una sesión:
        /// nombre, clase, driver, filtros y estado (o código de problema).
        /// </summary>
        public static string Describe(string deviceId)
        {
            string name = RegistryStrings(deviceId, CM_DRP_FRIENDLYNAME).FirstOrDefault()
                       ?? RegistryStrings(deviceId, CM_DRP_DEVICEDESC).FirstOrDefault() ?? "";
            int semi = name.LastIndexOf(';');
            if (semi >= 0) name = name[(semi + 1)..];   // "@oem12.inf,%desc%;Nombre"
            string cls = ClassGuid(deviceId) switch
            {
                null => "-",
                var c when c.Equals(HidClass, StringComparison.OrdinalIgnoreCase) => "HID",
                var c when c.Equals(XnaCompositeClass, StringComparison.OrdinalIgnoreCase) => "XUSB (Xbox 360)",
                var c when c.Equals(XboxCompositeClass, StringComparison.OrdinalIgnoreCase) => "XboxComposite (Xbox One/GIP)",
                var c => c,
            };
            string state = "?";
            if (CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS &&
                CM_Get_DevNode_Status(out uint status, out uint problem, inst, 0) == CR_SUCCESS)
                state = problem != 0 ? $"problema {problem}" : (status & DN_STARTED) != 0 ? "arrancado" : "sin arrancar";
            string Join(int prop) => string.Join(",", RegistryStrings(deviceId, prop)) is { Length: > 0 } v ? v : "-";
            return $"{deviceId} | {name} | clase {cls} | driver {ServiceOf(deviceId) ?? "-"} | " +
                   $"filtros sup. {Join(CM_DRP_UPPERFILTERS)} inf. {Join(CM_DRP_LOWERFILTERS)} | {state}";
        }

        /// <summary>
        /// Dispositivo físico del que cuelga un nodo: se sube mientras el padre no sea un hub
        /// USB, un bus o la raíz (máximo 8 niveles).
        /// </summary>
        public static string RootOf(string deviceId)
        {
            string current = deviceId;
            for (int i = 0; i < 8 && Parent(current) is { } parent; i++)
            {
                if (IsBusOrHub(parent)) break;
                current = parent;
            }
            return current;
        }

        private static bool IsBusOrHub(string deviceId) =>
            deviceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase) ||
            deviceId.StartsWith(@"PCI", StringComparison.OrdinalIgnoreCase) ||
            deviceId.StartsWith(@"ACPI", StringComparison.OrdinalIgnoreCase) ||
            deviceId.StartsWith(@"ROOT", StringComparison.OrdinalIgnoreCase) ||
            deviceId.StartsWith(@"HTREE", StringComparison.OrdinalIgnoreCase) ||
            deviceId.StartsWith(@"BTH", StringComparison.OrdinalIgnoreCase) ||
            HardwareIds(deviceId).Any(h => h.StartsWith(@"USB\Class_09", StringComparison.OrdinalIgnoreCase));

        private static string? Id(int devInst)
        {
            var buf = new char[512];
            return CM_Get_Device_IDW(devInst, buf, buf.Length, 0) == CR_SUCCESS
                ? new string(buf).TrimEnd('\0')
                : null;
        }
    }

    internal static class DevSetup
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_PROPCHANGE_PARAMS
        {
            public int HeaderSize; public int InstallFunction;   // SP_CLASSINSTALL_HEADER
            public int StateChange; public int Scope; public int HwProfile;
        }

        private const int DIF_PROPERTYCHANGE = 0x12;
        private const int DICS_PROPCHANGE = 3;
        private const int DICS_FLAG_GLOBAL = 1;

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS p, int size);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, IntPtr p);

        private const int DI_NEEDRESTART = 0x80;
        private const int DI_NEEDREBOOT = 0x100;

        /// <summary>
        /// Reinicia el dispositivo (DIF_PROPERTYCHANGE), como "deshabilitar y habilitar". Devuelve
        /// false si Windows lo aplazó al próximo arranque: alguien lo tiene abierto y no lo suelta
        /// (Steam), así que sigue funcionando con sus handles. Ver <see cref="RestartOrCycle"/>.
        /// </summary>
        public static bool Restart(string instanceId)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == new IntPtr(-1)) throw new Win32Exception();
            try
            {
                var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref d))
                    throw new Win32Exception();
                var p = new SP_PROPCHANGE_PARAMS
                {
                    HeaderSize = 8, InstallFunction = DIF_PROPERTYCHANGE,
                    StateChange = DICS_PROPCHANGE, Scope = DICS_FLAG_GLOBAL, HwProfile = 0
                };
                if (!SetupDiSetClassInstallParamsW(set, ref d, ref p, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()) ||
                    !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref d))
                    throw new Win32Exception();
                return !NeedsReboot(set, ref d);
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static bool NeedsReboot(IntPtr set, ref SP_DEVINFO_DATA d)
        {
            // SP_DEVINSTALL_PARAMS_W: cbSize, Flags, … DriverPath[260]; solo se leen los dos primeros campos.
            int size = IntPtr.Size == 8 ? 584 : 556;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.WriteInt32(buf, size);
                if (!SetupDiGetDeviceInstallParamsW(set, ref d, buf)) return false;
                return (Marshal.ReadInt32(buf, 4) & (DI_NEEDRESTART | DI_NEEDREBOOT)) != 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Reinicia el nodo; si Windows lo aplaza porque alguien lo tiene abierto, desconecta y
        /// reconecta su puerto USB (como desenchufarlo): eso no se puede rechazar y cierra todos
        /// los handles. Devuelve false si no se consiguió ninguna de las dos cosas.
        /// </summary>
        public static bool RestartOrCycle(string instanceId, string why)
        {
            if (Restart(instanceId)) return true;
            if (UsbPort.Cycle(instanceId, out var detail))
            {
                Logger.Log($"[Gamepad] {instanceId}: alguien lo tenía abierto y Windows aplazó el reinicio; " +
                           $"se ha desconectado y reconectado su puerto USB ({detail}) {why}.");
                return true;
            }
            Logger.Warning($"[Gamepad] {instanceId}: Windows aplazó el reinicio (lo tiene abierto otro proceso) y no se pudo " +
                           $"reciclar su puerto USB ({detail}). Lo sigue leyendo quien ya lo tuviera abierto hasta que lo cierre.");
            return false;
        }
    }

    /// <summary>Desconexión y reconexión por software del puerto de un hub (IOCTL_USB_HUB_CYCLE_PORT).</summary>
    internal static class UsbPort
    {
        private const uint IOCTL_USB_HUB_CYCLE_PORT = 0x220444;
        private static readonly Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid g, string device, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_ListW(ref Guid g, string device, char[] buffer, int len, int flags);

        /// <summary>Recicla el puerto del hub donde está el dispositivo USB del que cuelga el nodo.</summary>
        public static bool Cycle(string instanceId, out string detail)
        {
            if (DeviceTopology.UsbDeviceRootOf(instanceId) is not { } usb) { detail = "no cuelga de un USB"; return false; }
            if (Cm.Parent(usb) is not { } hub || Cm.Address(usb) is not { } port) { detail = $"sin hub ni puerto para {usb}"; return false; }
            var g = HubInterface;
            if (CM_Get_Device_Interface_List_SizeW(out int len, ref g, hub, 0) != 0 || len < 2) { detail = $"{hub} no es un hub"; return false; }
            var chars = new char[len];
            if (CM_Get_Device_Interface_ListW(ref g, hub, chars, len, 0) != 0) { detail = $"{hub} no es un hub"; return false; }
            string path = new string(chars).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            using var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) { detail = $"abrir {hub}: error {Marshal.GetLastWin32Error()}"; return false; }
            var buf = new byte[8];
            BitConverter.GetBytes(port).CopyTo(buf, 0);
            bool ok = DeviceIoControl(h, IOCTL_USB_HUB_CYCLE_PORT, buf, 8, buf, 8, out _, IntPtr.Zero);
            detail = ok ? $"{hub}, puerto {port}" : $"{hub}, puerto {port}: error {Marshal.GetLastWin32Error()}";
            return ok;
        }
    }
}
