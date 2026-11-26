using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Updates;

namespace OpenStreamMS.Core.Api;

/// <param name="CheckForUpdates">Mirar sola en GitHub si hay versión nueva al abrir el panel.</param>
/// <param name="Dismissed">Versión que no se vuelve a anunciar en la barra ("" = ninguna).</param>
public record UpdateSettingsRequest(bool? CheckForUpdates, string? Dismissed);

/// <param name="Version">Versión que el panel enseña como nueva; tiene que ser la última publicada.</param>
public record InstallUpdateRequest(string? Version);

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

        group.MapPost("/install", (InstallUpdateRequest req, UpdateInstaller installer) =>
                 installer.Start(req.Version) is { } error
                     ? Results.Conflict(new { error })
                     : Results.Accepted("/api/updates/install", installer.Status))
             .WithSummary("Descarga, comprueba (tamaño y SHA-256 de GitHub) e instala en silencio la versión nueva. " +
                          "El servicio se reinicia: se cortan las sesiones y los puestos en marcha.");

        group.MapGet("/install", (UpdateInstaller installer) => TypedResults.Ok(installer.Status))
             .WithSummary("Cómo va la actualización desde el panel.");

        return app;
    }
}
