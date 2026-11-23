using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;
using OpenStreamMS.Services.Gamepad;
using OpenStreamMS.Services.Metrics;
using OpenStreamMS.Services.Seats;

namespace OpenStreamMS.Core.Api;

/// <summary>Uso del equipo entero.</summary>
public record HudPc(double? Cpu, double? Gpu, double? GpuEncode, double MemUsedGb, double MemTotalGb);

/// <summary>Un dispositivo en el HUD.</summary>
/// <param name="Kind">gamepad, keyboard, mouse u other.</param>
/// <param name="Label">Nombre del dispositivo físico; vacío en los mandos de Moonlight.</param>
/// <param name="Source">seat (del puesto), session (asignado a la sesión) o moonlight (creado por su Sunshine).</param>
/// <param name="Number">Número del mando de Moonlight (1, 2…); 0 en los demás.</param>
public record HudDevice(string Kind, string Label, string Source, int Number);

/// <summary>Una sesión en marcha en el HUD.</summary>
/// <param name="IsMine">Es la sesión desde la que se abre el HUD.</param>
/// <param name="Seat">Puesto encendido que la muestra; null si solo va por stream.</param>
/// <param name="Composite">Sesión compuesta en marcha que la usa; null si ninguna.</param>
public record HudSession(Guid Id, string Name, bool IsMine, string? Seat, string? Composite,
                         double? Cpu, double? Gpu, double? GpuEncode, string? Encoder, bool Software,
                         IReadOnlyList<HudDevice> Devices);

/// <param name="PowerAllowed">La sesión desde la que se abre el HUD puede apagar y reiniciar el PC.</param>
/// <param name="Theme">Tema del panel (theme.js).</param>
/// <param name="Accent">Color de acento de Windows, para el tema "windows"; null si no se sabe.</param>
/// <param name="LightTheme">Windows en modo claro, para el tema "windows"; null si no se sabe.</param>
/// <param name="RestartBlocked">Por qué no se puede reiniciar la sesión desde el HUD (keyboard, seat); null si se puede.</param>
/// <param name="RestartBlockedBy">Compuesta o puesto que lo impide.</param>
public record HudResponse(HudPc Pc, IReadOnlyList<HudSession> Sessions, bool PowerAllowed,
                          string Theme = "system", string? Accent = null, bool? LightTheme = null,
                          string? RestartBlocked = null, string? RestartBlockedBy = null);

/// <summary>
/// API del HUD (<c>/hud.html</c> dentro de cada sesión). Sin credenciales: el middleware solo la deja
/// pasar a procesos de la sesión de Windows de una sesión en marcha (<c>IsHudRequest</c>).
/// </summary>
public static class HudEndpoints
{
    /// <summary>Clave de HttpContext.Items con la sesión de Windows de quien llama.</summary>
    public const string CallerSessionItem = "hud.windowsSession";

    public static IEndpointRouteBuilder MapHudApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/hud", (HttpContext ctx, StreamSessionService sessions, SeatManager seats, SystemMetrics metrics, ServiceConfig cfg) =>
            {
                uint caller = ctx.Items[CallerSessionItem] is uint ws ? ws : 0;
                var (accent, light) = cfg.PanelTheme == "windows" ? WindowsAppearance.Read() : (null, null);
                var me = CallerSession(caller, sessions);
                var (blocked, by) = me is null ? ("none", null) : RestartBlock(me, sessions, seats);
                return TypedResults.Ok(Build(caller, sessions, seats, metrics) with
                {
                    PowerAllowed     = me?.AllowPower == true,
                    Theme            = cfg.PanelTheme,
                    Accent           = accent,
                    LightTheme       = light,
                    RestartBlocked   = blocked,
                    RestartBlockedBy = by,
                });
            })
           .WithTags("HUD")
           .WithSummary("Estado para el HUD de una sesión: uso del equipo, sesiones en marcha y sus dispositivos.");

        app.MapPost("/api/hud/session/restart", (HttpContext ctx, StreamSessionService sessions, SeatManager seats) =>
            {
                uint caller = ctx.Items[CallerSessionItem] is uint ws ? ws : 0;
                var me = CallerSession(caller, sessions);
                if (me is null) return Results.NotFound();
                var (blocked, by) = RestartBlock(me, sessions, seats);
                if (blocked is not null)
                    return Results.Conflict(new { blocked, by });
                Logger.Warning($"[HUD] '{me.Name}' se reinicia desde su HUD.", me.Id);
                // Con un puesto encendido, el puesto sigue: logo en su monitor y agente nuevo al volver.
                if (seats.RunningSeatOf(me.Id) is { State: SeatState.Running } seat) seats.RestartSession(seat.Id);
                else sessions.Restart(me.Id);
                return Results.Accepted();
            })
           .WithTags("HUD")
           .WithSummary("Reinicia la sesión desde la que se abre el HUD (no la del teclado de una compuesta en marcha ni la de un puesto encendido).");

        app.MapPost("/api/hud/power/{action}", (string action, HttpContext ctx, StreamSessionService sessions) =>
            {
                uint caller = ctx.Items[CallerSessionItem] is uint ws ? ws : 0;
                var session = CallerSession(caller, sessions);
                if (session?.AllowPower != true)
                {
                    Logger.Warning($"[HUD] Apagar/reiniciar rechazado desde '{session?.Name ?? $"Windows {caller}"}': la sesión no tiene permiso.");
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                var who = session.Name;
                switch (action.ToLowerInvariant())
                {
                    case "restart":
                    case "shutdown":
                        bool restart = action.Equals("restart", StringComparison.OrdinalIgnoreCase);
                        Logger.Warning($"[HUD] {(restart ? "Reinicio" : "Apagado")} del PC pedido desde '{who}'.");
                        return PowerEndpoints.RunShutdown(restart, new PowerActionRequest(PowerDelaySeconds));
                    case "cancel":
                        Logger.Log($"[HUD] Apagado/reinicio cancelado desde '{who}'.");
                        return PowerEndpoints.Cancel();
                    default:
                        return Results.BadRequest("action: restart | shutdown | cancel");
                }
            })
           .WithTags("HUD")
           .WithSummary("Apagar, reiniciar o cancelar desde el HUD o las apps de Moonlight (solo si la sesión que llama tiene permiso).");
        return app;
    }

    /// <summary>
    /// Si la sesión no se puede reiniciar desde su HUD: "keyboard" si es la del teclado de una
    /// compuesta en marcha (su Sunshine corre en esa sesión de Windows: se cortaría el stream de
    /// todos) o "seat" si la muestra un puesto que aún está arrancando. Un miembro que no es el del
    /// teclado sí, y la sesión de un puesto encendido también: su hueco o su monitor enseñan el logo
    /// y "Reconectando" hasta que vuelve.
    /// </summary>
    internal static (string? Blocked, string? By) RestartBlock(StreamSession me, StreamSessionService sessions, SeatManager seats)
    {
        var composite = sessions.GetAll().FirstOrDefault(c =>
            c.IsComposite && c.State is SessionState.Running or SessionState.Starting &&
            (c.CompositeInput ?? c.Composite?.FirstOrDefault()?.Session) == me.Id);
        if (composite is not null) return ("keyboard", composite.Name);
        // Un puesto que todavía arranca no tiene aún su presentador: hay que esperar a que termine.
        var starting = seats.GetAll().FirstOrDefault(s => s.SessionId == me.Id && s.State == SeatState.Starting);
        if (starting is not null) return ("seat", starting.Name);
        return (null, null);
    }

    /// <summary>Sesión en marcha (no compuesta) de esa sesión de Windows: la de quien llama.</summary>
    private static StreamSession? CallerSession(uint windowsSession, StreamSessionService sessions) =>
        windowsSession == 0 ? null : sessions.GetAll().FirstOrDefault(s =>
            s.State == SessionState.Running && !s.IsComposite && s.RdpSessionId == windowsSession);

    /// <summary>Margen antes de apagar: deja ver el aviso y cancelar.</summary>
    public const int PowerDelaySeconds = 10;

    internal static HudResponse Build(uint callerWindowsSession, StreamSessionService sessions, SeatManager seats, SystemMetrics metrics)
    {
        var m = metrics.Get();
        var pads = GamepadSessionIsolation.Current?.VirtualPads ?? [];
        var runningSeats = seats.GetAll().Where(s => s.State == SeatState.Running).ToList();
        var all = sessions.GetAll().ToList();

        var list = all
            .Where(s => s.State == SessionState.Running && s.RdpSessionId > 0 && !s.IsComposite && !s.Local)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s =>
            {
                var seat = runningSeats.FirstOrDefault(x => x.SessionId == s.Id);
                var devices = new List<HudDevice>();
                if (seat is not null)
                    devices.AddRange(seat.Devices.Select(d => new HudDevice(
                        d.Kind.ToString().ToLowerInvariant(), d.Label, "seat", 0)));
                devices.AddRange(s.Gamepads.Select(g => new HudDevice("gamepad", g.Label, "session", 0)));
                devices.AddRange(pads
                    .Where(p => p.Index >= 0 && p.Session == s.Id)
                    .OrderBy(p => p.Index)
                    .Select(p => new HudDevice("gamepad", "", "moonlight", p.Index + 1)));

                m.Sessions.TryGetValue(s.Id, out var sm);
                var composite = s.CompositeOf is { } c ? all.FirstOrDefault(x => x.Id == c)?.Name : null;
                return new HudSession(s.Id, s.Name, s.RdpSessionId == callerWindowsSession, seat?.Name, composite,
                                      sm?.Cpu, sm?.Gpu, sm?.GpuEncode, sm?.Encoder, sm?.Software ?? false, devices);
            })
            .ToList();

        return new HudResponse(new HudPc(m.Cpu, m.Gpu, m.GpuEncode, m.MemUsedGb, m.MemTotalGb), list, false);
    }
}
