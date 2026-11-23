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

/// <param name="PowerAllowed">Ajustes permite apagar y reiniciar el PC desde el HUD.</param>
public record HudResponse(HudPc Pc, IReadOnlyList<HudSession> Sessions, bool PowerAllowed);

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
                return TypedResults.Ok(Build(caller, sessions, seats, metrics) with { PowerAllowed = cfg.HudPowerAllowed });
            })
           .WithTags("HUD")
           .WithSummary("Estado para el HUD de una sesión: uso del equipo, sesiones en marcha y sus dispositivos.");

        app.MapPost("/api/hud/power/{action}", (string action, HttpContext ctx, StreamSessionService sessions, ServiceConfig cfg) =>
            {
                if (!cfg.HudPowerAllowed) return Results.StatusCode(StatusCodes.Status403Forbidden);
                uint caller = ctx.Items[CallerSessionItem] is uint ws ? ws : 0;
                var who = sessions.GetAll().FirstOrDefault(s => s.State == SessionState.Running && s.RdpSessionId == caller)?.Name
                          ?? $"Windows {caller}";
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
           .WithSummary("Apagar, reiniciar o cancelar desde el HUD o las apps de Moonlight (solo si Ajustes lo permite).");
        return app;
    }

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
