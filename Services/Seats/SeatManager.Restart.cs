using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Localization;
using OpenStreamMS.Services.Mosaic;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Reiniciar la sesión de un puesto encendido (desde su HUD) sin apagar el puesto: el presentador
/// de la consola sigue abierto con el logo y "Reconectando…" en su monitor, la sesión se reinicia,
/// y al volver se le conecta un agente nuevo (texturas nuevas al mismo presentador), se ajusta su
/// resolución y se rehacen sus mandos y la reserva de sus dispositivos. Los ratones y teclados del
/// puesto siguen capturados: su entrada va al agente que haya en cada momento.
/// </summary>
public sealed partial class SeatManager
{
    private static readonly TimeSpan SessionRestartTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Puestos cuya sesión se está reiniciando: ni la vigilancia ni el corte del agente los apagan.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _sessionRestarting = new();

    /// <summary>Puesto encendido que muestra esa sesión, o null.</summary>
    public Seat? RunningSeatOf(Guid sessionId) =>
        _seats.Values.FirstOrDefault(s => s.SessionId == sessionId && s.State is SeatState.Running or SeatState.Starting);

    /// <summary>Reinicia la sesión del puesto sin apagarlo. Vuelve al momento; el resto va en segundo plano.</summary>
    public void RestartSession(Guid seatId)
    {
        var seat = Require(seatId);
        if (seat.State != SeatState.Running || seat.SessionId is not { } sid || sessions.Get(sid) is not { } session ||
            !_activeSeats.TryGetValue(seat.Id, out var active))
            throw new InvalidOperationException(BackendText.Get("error.request"));
        if (!_sessionRestarting.TryAdd(seat.Id, 0))
            return;   // ya se está reiniciando
        _ = Task.Run(() => DoRestartSession(seat, session, active));
    }

    private void DoRestartSession(Seat seat, StreamSession session, ActiveSeatSession active)
    {
        try
        {
            Logger.Log($"[Seats] '{seat.Name}': se reinicia su sesión '{session.Name}' sin apagar el puesto.");
            ShowWaiting(seat, session, active);

            // Mandos y reserva de dispositivos: van atados a la sesión de Windows de ahora.
            try { active.Pads?.Dispose(); } catch { }
            active.Pads = null;
            if (_reservedPorts.TryRemove(seat.Id, out var reserved))
                foreach (var port in reserved) SeatDeviceAccess.Free(port);

            sessions.Restart(session.Id);
            WaitSessionBack(session);

            // Sunshine vuelve a arrancar con la sesión: en modo exclusivo se pausa otra vez.
            if (seat.ExclusiveSession)
                sessions.PauseSunshine(session.Id, $"puesto '{seat.Name}'");

            // La sesión vuelve a su resolución propia: al tamaño de las texturas del presentador.
            string? mic = MicrophoneFor(seat);
            var hz = seat.Monitor is { } mon && ResolveMode(mon, session) is { } m ? m.Hz : session.RdpFrameRate;
            if (active.Width != session.RdpWidth || active.Height != session.RdpHeight || hz != session.RdpFrameRate || mic is not null)
            {
                Status(seat, $"Ajustando la sesión a {active.Width}x{active.Height} @ {hz} Hz…");
                sessions.ReconnectRdp(session.Id, active.Width, active.Height, hz, mic);
            }

            ReserveDevices(seat, session.RdpSessionId);
            bool requiresLogin = RequiresLogin(seat, session);
            var agent = LaunchAgent(seat, session.RdpSessionId, AgentAsSystem, agentArgs: "");
            if (agent.Width != active.Width || agent.Height != active.Height)
            {
                // El presentador tiene el tamaño de antes: se rehace el puesto entero.
                agent.Channel.Dispose();
                agent.Server.Dispose();
                throw new InvalidOperationException($"la sesión volvió a {agent.Width}x{agent.Height} en vez de {active.Width}x{active.Height}");
            }
            if (requiresLogin) LockSession(seat, session);

            // Texturas del agente nuevo → el mismo presentador.
            var dup = agent.Handles.Select(h => HandleBroker.DuplicateHandleTo(agent.Pid, new IntPtr(h), active.HostPid).ToInt64()).ToArray();
            var oldChannel = active.AgentChannel;
            var oldServer = active.AgentServer;
            active.AgentServer = agent.Server;
            active.AgentChannel = agent.Channel;
            active.AgentPid = agent.Pid;
            try { oldChannel.Dispose(); } catch { }
            try { oldServer.Dispose(); } catch { }
            active.HostChannel.SendJson(new JsonObject
            {
                ["op"] = "reattach",
                ["width"] = agent.Width,
                ["height"] = agent.Height,
                ["handles"] = new JsonArray(dup.Select(h => (JsonNode)h).ToArray()),
            });
            StartAgentBridge(active, seat);

            CaptureGamepads(seat, active, session.RdpSessionId);
            if (_guideUsers.TryRemove(seat.Id, out var oldGuide)) GuideButtonGuard.Release(oldGuide);
            var guideUsers = GuideButtonGuard.UsersFor(session.RdpSessionId);
            GuideButtonGuard.Engage(guideUsers);
            _guideUsers[seat.Id] = guideUsers;

            seat.StatusDetail = "Activo: imagen, audio y periféricos conectados.";
            Interlocked.Increment(ref _generation);   // que GamepadSessionIsolation reasigne los mandos
            Logger.Log($"[Seats] '{seat.Name}': sesión reiniciada; el puesto sigue en la sesión de Windows {session.RdpSessionId}.");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Seats] '{seat.Name}': no se pudo reiniciar su sesión sin apagarlo ({ex.Message}); se rehace el puesto.");
            _sessionRestarting.TryRemove(seat.Id, out _);
            try
            {
                Stop(seat.Id, restarting: true);
                Start(seat.Id);
            }
            catch (Exception ex2) { Logger.Error($"[Seats] '{seat.Name}': no se pudo volver a encender: {ex2.Message}"); }
            return;
        }
        _sessionRestarting.TryRemove(seat.Id, out _);
    }

    /// <summary>Logo y "Reconectando…" en el monitor del puesto mientras su sesión no está.</summary>
    private void ShowWaiting(Seat seat, StreamSession session, ActiveSeatSession active)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"seat-wait-{seat.Id:N}.bgra");
            File.WriteAllBytes(path, WaitingImage.Render(active.Width, active.Height, session.Name, BackendText.Get("composite.reconnecting")));
            active.HostChannel.SendJson(new JsonObject { ["op"] = "waiting", ["path"] = path });
            Status(seat, "Reiniciando la sesión…");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seats] '{seat.Name}': sin imagen de espera: {ex.Message}");
        }
    }

    /// <summary>Espera a que la sesión vuelva a estar en marcha tras el reinicio.</summary>
    private static void WaitSessionBack(StreamSession session)
    {
        var deadline = DateTime.UtcNow + SessionRestartTimeout;
        // Primero que deje de estar en marcha (el reinicio va en segundo plano)…
        while (session.State == SessionState.Running && DateTime.UtcNow < deadline) Thread.Sleep(200);
        // …y luego que vuelva.
        while (!(session.State == SessionState.Running && session.RdpSessionId > 0))
        {
            if (session.State is SessionState.Error or SessionState.Stopped)
                throw new InvalidOperationException(session.ErrorMessage ?? "la sesión no volvió a arrancar");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("la sesión tarda demasiado en volver");
            Thread.Sleep(500);
        }
    }
}
