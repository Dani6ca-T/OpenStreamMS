using Microsoft.AspNetCore.Http.HttpResults;
using OpenStreamMS.Services.Mosaic;
using OpenStreamMS.Services.Seats;

namespace OpenStreamMS.Core.Api;

/// <summary>Hueco de una sesión compuesta en la API.</summary>
/// <param name="Session">Sesión RDP del hueco.</param>
/// <param name="Area">Parte de la pantalla: Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft o BottomRight.</param>
public record CompositeSlotRequest(Guid Session, SeatScreenArea Area);

/// <summary>Crear o editar una sesión compuesta.</summary>
/// <param name="Name">Nombre (el que se ve en Moonlight).</param>
/// <param name="Slots">De 2 a 4 huecos sin pisarse, en orden: el mando N va al hueco N.</param>
/// <param name="Input">Sesión del teclado, el ratón y el audio; null = la del primer hueco.</param>
/// <param name="Width">Ancho del stream.</param>
/// <param name="Height">Alto del stream.</param>
/// <param name="FrameRate">Fps del stream.</param>
/// <param name="Enabled">Arrancarla con el servicio.</param>
/// <param name="SunshineStreamPort">Puerto base de su Sunshine; 0 o ausente = el primero libre.</param>
/// <param name="StreamProtocol">Protocolos de su Sunshine.</param>
public record CompositeRequest(
    string Name,
    IReadOnlyList<CompositeSlotRequest> Slots,
    Guid? Input = null,
    int Width = 1920,
    int Height = 1080,
    int FrameRate = 60,
    bool Enabled = false,
    int SunshineStreamPort = 0,
    StreamProtocol StreamProtocol = StreamProtocol.Both);

/// <summary>Plantilla de huecos que ofrece el panel.</summary>
public record CompositeTemplate(string Id, IReadOnlyList<SeatScreenArea> Areas);

/// <summary>
/// API de las sesiones compuestas. Crear y editar van aquí; arrancar, parar, borrar, logs y
/// emparejamiento son los de cualquier sesión (<c>/api/sessions/{id}/...</c>).
/// </summary>
public static class CompositeEndpoints
{
    public static IEndpointRouteBuilder MapCompositeApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/composites").WithTags("Composite sessions");

        group.MapGet("/templates", () => TypedResults.Ok(
                MosaicLayout.Templates.Select(t => new CompositeTemplate(t.Key, t.Value))))
             .WithName("GetCompositeTemplates")
             .WithSummary("Plantillas de huecos de las sesiones compuestas");

        group.MapPost("/", Create)
             .WithName("CreateComposite")
             .WithSummary("Crear una sesión compuesta")
             .WithDescription("""
                Junta en un stream propio las pantallas de 2 a 4 sesiones RDP con captura RemoteIdd,
                cada una en su hueco. Tiene su propio Sunshine (puertos, emparejamiento y nombre en
                Moonlight). Al arrancarla se arrancan las sesiones paradas.
                """);

        group.MapPut("/{id:guid}", Update)
             .WithName("UpdateComposite")
             .WithSummary("Editar una sesión compuesta parada");

        group.MapPost("/{id:guid}/steam/{action}", Steam)
             .WithName("CompositeSteam")
             .WithSummary("Abrir o cerrar Steam Big Picture en todas las sesiones de una compuesta")
             .WithDescription("""
                `action` = `open` o `close`. Lo llama la app Steam de la compuesta
                (Scripts\osms-composite-steam.ps1) desde su Sunshine; sin credenciales solo se acepta
                desde un proceso de la sesión de Windows de esa compuesta.
                """);

        return app;
    }

    private static Results<Ok<int>, BadRequest<string>, NotFound> Steam(Guid id, string action, StreamSessionService svc)
    {
        bool? open = action.ToLowerInvariant() switch { "open" => true, "close" => false, _ => null };
        if (open is null) return TypedResults.BadRequest("action: open | close");
        try { return TypedResults.Ok(svc.CompositeSteam(id, open.Value)); }
        catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        catch (InvalidOperationException ex) { return TypedResults.BadRequest(ex.Message); }
    }

    private static CompositeDefinition ToDefinition(CompositeRequest req) => new(
        req.Name ?? "",
        (req.Slots ?? []).Select(s => new CompositeSlot(s.Session, s.Area)).ToList(),
        req.Input, req.Width, req.Height, req.FrameRate, req.Enabled,
        req.SunshineStreamPort, req.StreamProtocol);

    private static Results<Created<SessionResponse>, BadRequest<string>, NotFound> Create(
        CompositeRequest req, StreamSessionService svc)
    {
        try
        {
            var s = svc.CreateComposite(ToDefinition(req));
            return TypedResults.Created($"/api/sessions/{s.Id}", SessionEndpoints.ToResponse(s));
        }
        catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        catch (InvalidOperationException ex) { return TypedResults.BadRequest(ex.Message); }
    }

    private static Results<Ok<SessionResponse>, BadRequest<string>, NotFound> Update(
        Guid id, CompositeRequest req, StreamSessionService svc)
    {
        try { return TypedResults.Ok(SessionEndpoints.ToResponse(svc.UpdateComposite(id, ToDefinition(req)))); }
        catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        catch (InvalidOperationException ex) { return TypedResults.BadRequest(ex.Message); }
    }
}
