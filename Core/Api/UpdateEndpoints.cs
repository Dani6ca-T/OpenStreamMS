using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Updates;

namespace OpenStreamMS.Core.Api;

/// <param name="CheckForUpdates">Mirar sola en GitHub si hay versión nueva al abrir el panel.</param>
/// <param name="Dismissed">Versión que no se vuelve a anunciar en la barra ("" = ninguna).</param>
public record UpdateSettingsRequest(bool? CheckForUpdates, string? Dismissed);

/// <summary>Versión instalada y aviso de versiones nuevas (Acerca de y barra del panel).</summary>
public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/updates").WithTags("Updates");

        group.MapGet("/", async (UpdateChecker u) => TypedResults.Ok(await u.CheckAsync()))
             .WithSummary("Versión instalada y la última publicada. Mira en GitHub como mucho cada 6 h, y solo si está activado.");

        group.MapPost("/check", async (UpdateChecker u) => TypedResults.Ok(await u.CheckAsync(force: true)))
             .WithSummary("Mira ahora en GitHub si hay versión nueva, aunque la comprobación automática esté desactivada.");

        group.MapPut("/settings", (UpdateSettingsRequest req, ServiceConfig cfg, UpdateChecker u) =>
             {
                 var dismissed = req.Dismissed?.Trim();
                 if (dismissed is not null && dismissed.Length > 0 && AppVersion.Parse(dismissed) is null)
                     return Results.BadRequest();
                 bool changed = false;
                 if (req.CheckForUpdates is { } on && on != cfg.CheckForUpdates) { cfg.CheckForUpdates = on; changed = true; }
                 if (dismissed is not null && dismissed != cfg.UpdateDismissed) { cfg.UpdateDismissed = dismissed; changed = true; }
                 if (changed) cfg.Save();
                 return Results.Ok(u.Status());
             })
             .WithSummary("Activa o desactiva la comprobación automática, o deja de anunciar una versión en la barra.");

        return app;
    }
}
