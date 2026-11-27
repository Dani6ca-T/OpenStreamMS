using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;
using OpenStreamMS.Services.Localization;
using OpenStreamMS.Services.Mosaic;
using OpenStreamMS.Services.Seats;
using System.Collections.Concurrent;

namespace OpenStreamMS.Core.Api;

/// <summary>Lo que define una sesión compuesta (crear y editar).</summary>
/// <param name="Name">Nombre de la compuesta (y el que publica su Sunshine).</param>
/// <param name="Slots">Huecos con su sesión, en orden: el mando N va al hueco N.</param>
/// <param name="Input">Sesión que recibe teclado y ratón y de la que sale el audio; null = la del primer hueco.</param>
/// <param name="Width">Ancho del lienzo (la resolución que recibe el cliente).</param>
/// <param name="Height">Alto del lienzo.</param>
/// <param name="FrameRate">Fps del stream y de las sesiones mientras están en la compuesta.</param>
/// <param name="Enabled">Arrancarla con el servicio.</param>
/// <param name="StreamPort">Puerto base de su Sunshine; 0 = el primero libre.</param>
/// <param name="Protocol">Protocolos de su Sunshine.</param>
/// <param name="GamepadType">Mando que emula su Sunshine: auto, x360 o ds4; null = DualShock 4 al crear, sin cambios al editar.</param>
public sealed record CompositeDefinition(
    string Name, IReadOnlyList<CompositeSlot> Slots, Guid? Input,
    int Width, int Height, int FrameRate, bool Enabled,
    int StreamPort = 0, StreamProtocol Protocol = StreamProtocol.Both,
    string? GamepadType = null);

/// <summary>
/// Sesiones compuestas: una sesión sin Windows propio que junta en un stream las pantallas de 2 a
/// 4 sesiones RDP, cada una en su hueco.
/// <list type="bullet">
/// <item>Al arrancarla se arrancan las sesiones paradas, se pone en pausa el Sunshine de cada una
/// (nadie más lee su pantalla), se reconectan al tamaño de su hueco y se lanza el Sunshine propio de
/// la compuesta en la sesión de Windows de <see cref="StreamSession.CompositeInput"/>, que compone
/// todas con <c>osms-mosaic.txt</c> (<see cref="MosaicLayout"/>). Por eso teclado, ratón y audio
/// son los de esa sesión.</item>
/// <item>Al pararla, cada sesión vuelve a su tamaño y a su Sunshine, y se paran las que arrancó ella.</item>
/// <item>Mandos: el mando N que crea su Sunshine va a la sesión del hueco N (<see cref="CompositePadTarget"/>).</item>
/// </list>
/// Las sesiones siguen siendo normales: pueden estar en varias compuestas, pero solo en una en marcha.
/// </summary>
public partial class StreamSessionService
{
    private static readonly TimeSpan CompositeMemberTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Miembros que arrancó cada compuesta en marcha: se paran al pararla.</summary>
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _compositeStarted = new();
    /// <summary>Tamaño RDP que tiene ahora cada miembro de cada compuesta, para reconectar solo lo que cambia.</summary>
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, (int W, int H)>> _compositeApplied = new();
    private int _compositeGeneration;

    /// <summary>Sube cada vez que una compuesta arranca, para o cambia la sesión de Windows de un miembro.</summary>
    public int CompositeGeneration => Volatile.Read(ref _compositeGeneration);

    // ── Crear y editar ───────────────────────────────────────────────────────

    public StreamSession CreateComposite(CompositeDefinition def)
    {
        var (slots, input) = ValidateComposite(null, def);
        var suggested = SunshinePorts.Suggest(_sessions.Values.ToList());
        var session = new StreamSession
        {
            Name               = def.Name.Trim(),
            Composite          = slots,
            CompositeInput     = input,
            SunshineExePath    = ResolveExePath(null),
            RdpWidth           = def.Width  > 0 ? def.Width  : 1920,
            RdpHeight          = def.Height > 0 ? def.Height : 1080,
            RdpFrameRate       = NormalizeRdpFrameRate(def.FrameRate),
            Enabled            = def.Enabled,
            GamepadType        = StreamSession.NormalizeGamepadType(def.GamepadType) ?? StreamSession.DefaultGamepadType,
            Capture            = "remoteidd",
            StreamProtocol     = def.Protocol,
            SunshineStreamPort = def.StreamPort > 0 ? def.StreamPort : suggested.SunshineStreamPort,
            WebRtcPort         = suggested.WebRtcPort,
            WebRtcMediaPortMin = suggested.WebRtcMediaPortMin,
            WebRtcMediaPortMax = suggested.WebRtcMediaPortMax,
        };
        EnsurePortsAvailable(session, _sessions.Values);
        _sessions[session.Id] = session;
        Logger.Log(BackendText.Get("composite.created", session.Name, session.Id, string.Join(", ", slots.Select(s => $"{_sessions[s.Session].Name} ({s.Area})"))));
        SaveSessions();
        return session;
    }

    /// <summary>Cambia una compuesta parada (en marcha habría que recolocar sesiones con clientes conectados).</summary>
    public StreamSession UpdateComposite(Guid id, CompositeDefinition def)
    {
        var session = RequireSession(id);
        if (!session.IsComposite) throw new KeyNotFoundException();
        if (session.State is SessionState.Starting or SessionState.Running or SessionState.Stopping)
            throw new InvalidOperationException(RequestCulture.T("error.stopSession"));

        var (slots, input) = ValidateComposite(id, def);
        session.Name           = def.Name.Trim();
        session.Composite      = slots;
        session.CompositeInput = input;
        session.RdpWidth       = def.Width  > 0 ? def.Width  : session.RdpWidth;
        session.RdpHeight      = def.Height > 0 ? def.Height : session.RdpHeight;
        session.RdpFrameRate   = NormalizeRdpFrameRate(def.FrameRate);
        session.Enabled        = def.Enabled;
        if (StreamSession.NormalizeGamepadType(def.GamepadType) is { } gp && gp != session.GamepadType)
        {
            session.GamepadType = gp;
            WriteGamepadType(session);
        }
        session.StreamProtocol = def.Protocol;
        if (def.StreamPort > 0) session.SunshineStreamPort = def.StreamPort;
        EnsurePortsAvailable(session, _sessions.Values);
        _sunshine.TryRemove(id, out _);   // el siguiente arranque escribe la configuración nueva
        Logger.Log(BackendText.Get("composite.updated", session.Name), id);
        SaveSessions();
        return session;
    }

    private (List<CompositeSlot> Slots, Guid Input) ValidateComposite(Guid? selfId, CompositeDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Name))
            throw new InvalidOperationException(RequestCulture.T("error.compositeName"));
        var slots = def.Slots.Where(s => s.Session != Guid.Empty).ToList();
        if (slots.Count < MosaicLayout.MinMembers || slots.Count > MosaicLayout.MaxMembers)
            throw new InvalidOperationException(RequestCulture.T("error.compositeCount"));
        if (slots.Select(s => s.Session).Distinct().Count() != slots.Count)
            throw new InvalidOperationException(RequestCulture.T("error.compositeRepeated"));
        if (!MosaicLayout.Disjoint(slots.Select(s => s.Area)))
            throw new InvalidOperationException(RequestCulture.T("error.compositeOverlap"));

        foreach (var slot in slots)
        {
            if (!_sessions.TryGetValue(slot.Session, out var member) || member.Id == selfId)
                throw new KeyNotFoundException();
            if (member.Local || member.IsComposite || !WantsRemoteIdd(member))
                throw new InvalidOperationException(RequestCulture.T("error.compositeMember", member.Name));
        }
        var input = def.Input is { } i && slots.Any(s => s.Session == i) ? i : slots[0].Session;
        return (slots, input);
    }

    /// <summary>Compuestas que usan la sesión <paramref name="id"/>.</summary>
    private IEnumerable<StreamSession> CompositesUsing(Guid id) =>
        _sessions.Values.Where(s => s.Composite?.Any(slot => slot.Session == id) == true);

    // ── Arrancar y parar ─────────────────────────────────────────────────────

    /// <summary>Arranque de una compuesta (desde <see cref="DoStart"/>). Si falla, deja los miembros como estaban.</summary>
    private void StartComposite(StreamSession c, int generation)
    {
        var slots = c.Composite!;
        var members = slots.Select(s => _sessions.TryGetValue(s.Session, out var m) ? m
                                        : throw new InvalidOperationException(RequestCulture.T("error.compositeMissing")))
                           .ToList();

        // Que ninguna esté ya ocupada: en otra compuesta, en un puesto en exclusiva o con su propio cliente.
        foreach (var m in members)
        {
            if (m.CompositeOf is { } other && other != c.Id && _sessions.TryGetValue(other, out var o))
                throw new InvalidOperationException(RequestCulture.T("error.compositeBusy", m.Name, o.Name));
            if (IsSunshinePaused(m.Id) && m.CompositeOf != c.Id)
                throw new InvalidOperationException(RequestCulture.T("error.compositeSeat", m.Name));
            if (m.ClientsMoonlight + m.ClientsWebRtc is > 0)
                throw new InvalidOperationException(RequestCulture.T("error.compositeStreaming", m.Name));
        }

        var started = _compositeStarted.GetOrAdd(c.Id, _ => []);
        var applied = _compositeApplied.GetOrAdd(c.Id, _ => new());
        try
        {
            c.StartPhase = "members";
            foreach (var m in members.Where(m => m.State is not (SessionState.Running or SessionState.Starting)))
            {
                Logger.Log(BackendText.Get("composite.startingMember", c.Name, m.Name), c.Id);
                Start(m.Id);
                lock (started) started.Add(m.Id);
            }
            foreach (var m in members) WaitCompositeMember(c, m);

            foreach (var m in members)
            {
                PauseSunshine(m.Id, $"sesión compuesta '{c.Name}'");
                m.CompositeOf = c.Id;
                applied[m.Id] = (m.RdpWidth, m.RdpHeight);
            }
            Interlocked.Increment(ref _compositeGeneration);

            c.StartPhase = "layout";
            ApplyComposite(c);

            var owner = _sessions[c.CompositeInput ?? slots[0].Session];
            c.RdpSessionId = owner.RdpSessionId;
            c.StartPhase = "sunshine";
            var user = ResolveUser(owner);
            EnsureSunshineInstance(c, user.Domain, user.Username);
            try { Services.Sunshine.SunshineConfigurator.ConfigureCompositeSteamEntry(GetSunshineInstanceExe(c.Id), c.Id); }
            catch (Exception ex) { Logger.Warning(BackendText.Get("composite.steamConfigFailed", c.Name, ex.Message), c.Id); }
            var result = GetOrCreateSunshineManager(c).EnsureRunning(c.RdpSessionId);
            c.SunshinePid = result.Pid;
            if (result.Status == Services.Sunshine.SunshineRunStatus.LaunchFailed)
                throw new Exception(result.Message ?? RequestCulture.T("error.request"));

            bool running;
            lock (c.LifecycleLock)
            {
                // Detenida o sustituida por otra orden mientras arrancaba.
                running = StartStillWanted(c, generation);
                if (running)
                {
                    c.State     = SessionState.Running;
                    c.StartedAt = DateTime.UtcNow;
                }
            }
            if (!running)
            {
                Logger.Log(BackendText.Get("composite.startAborted", c.Name), c.Id);
                return;
            }
            Logger.Log(BackendText.Get("composite.started", c.Name, string.Join(", ", members.Select(m => m.Name)), owner.Name, c.RdpSessionId), c.Id);
            EnsureCompositeAudio(c);
            EnsureCompositeInput(c);
        }
        catch
        {
            ReleaseComposite(c);
            throw;
        }
    }

    private void WaitCompositeMember(StreamSession c, StreamSession m)
    {
        var deadline = DateTime.UtcNow + CompositeMemberTimeout;
        while (m.State != SessionState.Running || m.RdpSessionId == 0)
        {
            if (c.State != SessionState.Starting)
                throw new InvalidOperationException(RequestCulture.T("error.request"));
            if (m.State is SessionState.Error or SessionState.Stopped || DateTime.UtcNow > deadline)
                throw new InvalidOperationException(RequestCulture.T("error.compositeMemberStart", m.Name));
            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// Deja cada miembro en marcha en su hueco: reconecta los que cambian de tamaño y escribe el
    /// fichero de Sunshine (después, porque una reconexión puede cambiar la sesión de Windows).
    /// </summary>
    private void ApplyComposite(StreamSession c)
    {
        var applied = _compositeApplied.GetOrAdd(c.Id, _ => new());
        foreach (var slot in c.Composite!)
        {
            if (!_sessions.TryGetValue(slot.Session, out var m) || m.State != SessionState.Running || m.RdpSessionId == 0)
                continue;
            var target = MosaicLayout.PartSize(slot.Area, c.RdpWidth, c.RdpHeight);
            if (applied.GetValueOrDefault(m.Id) == target) continue;
            ReconnectRdp(m.Id, target.Width, target.Height, c.RdpFrameRate);
            applied[m.Id] = target;
            Logger.Log(BackendText.Get("composite.logMemberAssignedToSlot", c.Name, m.Name, slot.Area, target.Width, target.Height), c.Id);
        }
        WriteCompositeFile(c);
        Interlocked.Increment(ref _compositeGeneration);
    }

    private void WriteCompositeFile(StreamSession c)
    {
        var parts = c.Composite!
            .Select((slot, i) =>
            {
                _sessions.TryGetValue(slot.Session, out var m);
                uint ws = m is { State: SessionState.Running } ? m.RdpSessionId : 0u;
                return (ws, slot.Area, EnsureWaitingImage(c, i, slot.Area, m?.Name ?? ""));
            })
            .ToList();
        var path = Path.Combine(SunshineConfigDir(c.Id), MosaicLayout.FileName);
        var text = MosaicLayout.Render(c.RdpWidth, c.RdpHeight, parts);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Suelta los miembros de una compuesta que se para (o que no llegó a arrancar): cada uno
    /// vuelve a su tamaño y a su Sunshine, y se paran los que arrancó ella.
    /// </summary>
    /// <param name="restarting">Reinicio de la compuesta: los miembros que arrancó siguen en marcha y siguen siendo suyos.</param>
    private void ReleaseComposite(StreamSession c, bool restarting = false)
    {
        StopCompositeAudio(c);
        StopCompositeInput(c, restarting);
        try { File.Delete(Path.Combine(SunshineConfigDir(c.Id), MosaicLayout.FileName)); }
        catch (Exception ex) { Logger.Warning(BackendText.Get("composite.deleteFileFailed", c.Name, MosaicLayout.FileName, ex.Message), c.Id); }

        _compositeApplied.TryRemove(c.Id, out var applied);
        HashSet<Guid>? started = null;
        if (restarting) started = null;
        else _compositeStarted.TryRemove(c.Id, out started);
        foreach (var slot in c.Composite ?? [])
        {
            if (!_sessions.TryGetValue(slot.Session, out var m) || (m.CompositeOf is { } of && of != c.Id)) continue;
            bool ownedByUs = m.CompositeOf == c.Id;
            m.CompositeOf = null;
            if (started is not null && started.Contains(m.Id))
            {
                Logger.Log(BackendText.Get("composite.stoppingMember", c.Name, m.Name), c.Id);
                if (ownedByUs) ResumeSunshine(m.Id);
                if (m.State is SessionState.Running or SessionState.Starting)
                    _ = Task.Run(() => { try { Stop(m.Id); } catch { } });
                continue;
            }
            if (!ownedByUs) continue;
            if (m.State == SessionState.Running && applied?.GetValueOrDefault(m.Id) is { } size &&
                size != (m.RdpWidth, m.RdpHeight))
            {
                try { ReconnectRdp(m.Id, m.RdpWidth, m.RdpHeight, m.RdpFrameRate); }
                catch (Exception ex) { Logger.Warning(BackendText.Get("composite.restoreResolutionFailed", c.Name, m.Name, ex.Message), c.Id); }
            }
            ResumeSunshine(m.Id);
        }
        Interlocked.Increment(ref _compositeGeneration);
    }

    // ── Vigilancia (desde MonitorAll, cada 5 s) ──────────────────────────────

    /// <summary>
    /// Mantiene las compuestas en marcha: si se para la sesión del teclado, se para la compuesta;
    /// si cambia su sesión de Windows, su Sunshine se relanza en la nueva; un miembro que vuelve a
    /// arrancar se pone otra vez en pausa y en su hueco.
    /// </summary>
    private void WatchComposites()
    {
        foreach (var c in _sessions.Values.Where(s => s.IsComposite && s.State == SessionState.Running).ToList())
        {
            try
            {
                var ownerId = c.CompositeInput ?? c.Composite![0].Session;
                if (!_sessions.TryGetValue(ownerId, out var owner) || owner.State != SessionState.Running || owner.RdpSessionId == 0)
                {
                    Logger.Warning(BackendText.Get("composite.keyboardStopped", c.Name), c.Id);
                    _ = Task.Run(() => { try { Stop(c.Id); } catch { } });
                    continue;
                }

                EnsureCompositeAudio(c);

                bool changed = false;
                foreach (var slot in c.Composite!)
                {
                    if (!_sessions.TryGetValue(slot.Session, out var m) || m.State != SessionState.Running) continue;
                    // Arrancó de nuevo (parada a mano y vuelta a arrancar): su Sunshine propio fuera.
                    if (m.CompositeOf != c.Id || m.SunshinePid != 0 || _sunshine.ContainsKey(m.Id))
                    {
                        if (_sunshine.TryRemove(m.Id, out var mgr))
                            try { mgr.Stop(); } catch { }
                        m.SunshinePid = 0;
                        PauseSunshine(m.Id, $"sesión compuesta '{c.Name}'");
                        m.CompositeOf = c.Id;
                        if (_compositeApplied.TryGetValue(c.Id, out var applied)) applied[m.Id] = (m.RdpWidth, m.RdpHeight);
                        changed = true;
                    }
                }
                if (changed) ApplyComposite(c);
                else WriteCompositeFile(c);   // una sesión de Windows que cambió o un miembro que se paró
                EnsureCompositeInput(c);       // el miembro con el teclado se reinició o cambió de sesión

                if (owner.RdpSessionId != c.RdpSessionId)
                {
                    Logger.Log(BackendText.Get("composite.keyboardSessionChanged", c.Name, owner.RdpSessionId), c.Id);
                    RelaunchSunshine(c, "cambio de sesión de Windows");
                    c.RdpSessionId = owner.RdpSessionId;
                    Interlocked.Increment(ref _compositeGeneration);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(BackendText.Get("composite.watchFailed", c.Name, ex.Message), c.Id);
            }
        }
    }

    // ── Imagen de espera de cada hueco ───────────────────────────────────────

    /// <summary>Lo que se dibujó en cada imagen de espera (ruta → tamaño y textos), para no rehacerla.</summary>
    private readonly ConcurrentDictionary<string, string> _waitingImages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Imagen que Sunshine pone en el hueco mientras su sesión se (re)inicia: logo, nombre y
    /// "Reconectando…", del tamaño del hueco. Se escribe en la carpeta de config de la compuesta.
    /// Null si no se pudo (el hueco sale en negro, como antes).
    /// </summary>
    private string? EnsureWaitingImage(StreamSession c, int index, SeatScreenArea area, string memberName)
    {
        try
        {
            var (w, h) = MosaicLayout.PartSize(area, c.RdpWidth, c.RdpHeight);
            if (w <= 0 || h <= 0) return null;
            var path = Path.Combine(SunshineConfigDir(c.Id), $"osms-wait-{index}.bgra");
            var subtitle = BackendText.Get("composite.reconnecting");
            var key = $"{w}x{h}|{memberName}|{subtitle}";
            if (File.Exists(path) && _waitingImages.TryGetValue(path, out var drawn) && drawn == key) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, WaitingImage.Render(w, h, memberName, subtitle));
            File.Move(tmp, path, overwrite: true);
            _waitingImages[path] = key;
            return path;
        }
        catch (Exception ex)
        {
            Logger.Warning(BackendText.Get("composite.waitingImageFailed", c.Name, index + 1, ex.Message), c.Id);
            return null;
        }
    }

    // ── Steam ────────────────────────────────────────────────────────────────

    /// <summary>true si <paramref name="windowsSession"/> es la sesión de Windows de la compuesta en marcha <paramref name="id"/>.</summary>
    public bool IsCompositeWindowsSession(Guid id, uint windowsSession) =>
        _sessions.TryGetValue(id, out var c) && c.IsComposite && c.State == SessionState.Running &&
        windowsSession != 0 && c.RdpSessionId == windowsSession;

    /// <summary>
    /// App Steam de la compuesta: abre (o cierra) Big Picture en cada sesión de sus huecos, no solo
    /// en la del teclado, donde corre su Sunshine. Devuelve en cuántas sesiones se lanzó.
    /// </summary>
    public int CompositeSteam(Guid id, bool open)
    {
        if (!_sessions.TryGetValue(id, out var c) || !c.IsComposite) throw new KeyNotFoundException();
        if (c.State != SessionState.Running) throw new InvalidOperationException(RequestCulture.T("error.request"));

        int launched = 0;
        foreach (var windowsSession in c.Composite!
                     .Select(slot => _sessions.TryGetValue(slot.Session, out var m) && m.State == SessionState.Running ? m.RdpSessionId : 0u)
                     .Where(s => s != 0)
                     .Distinct())
        {
            try
            {
                if (SteamIsolation.OpenOrCloseBigPicture(windowsSession, open)) launched++;
            }
            catch (Exception ex)
            {
                Logger.Warning(BackendText.Get("composite.steamActionFailed", c.Name, BackendText.Get(open ? "composite.steamOpen" : "composite.steamClose"), windowsSession, ex.Message), c.Id);
            }
        }
        Logger.Log(BackendText.Get("composite.steamActionDone", c.Name, BackendText.Get(open ? "composite.steamOpen" : "composite.steamClose"), launched), c.Id);
        return launched;
    }

    // ── Mandos ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sesión a la que va el mando número <paramref name="index"/> que creó el Sunshine de
    /// <paramref name="creator"/>: la del hueco N si <paramref name="creator"/> es una compuesta en
    /// marcha y ese hueco tiene sesión en marcha; si no, la del teclado. Null si no es una compuesta.
    /// </summary>
    public Guid? CompositePadTarget(Guid creator, int index)
    {
        if (!_sessions.TryGetValue(creator, out var c) || !c.IsComposite || c.State != SessionState.Running)
            return null;
        var slots = c.Composite!;
        if (index >= 0 && index < slots.Count && _sessions.TryGetValue(slots[index].Session, out var m) &&
            m.State == SessionState.Running && m.RdpSessionId > 0)
            return m.Id;
        return c.CompositeInput ?? slots[0].Session;
    }
}
