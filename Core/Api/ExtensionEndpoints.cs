using Microsoft.AspNetCore.StaticFiles;
using OpenStreamMS.Services.Extensions;

namespace OpenStreamMS.Core.Api;

public record ExtensionInstallRequest(string Token, string Sha256, bool Replace = false);
public record ExtensionEnableRequest(bool Enabled);
public record CatalogAddRequest(string Url);
public record ExtensionLogResponse(string[] Lines);

public static class ExtensionEndpoints
{
    public static IEndpointRouteBuilder MapExtensionApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/extensions").WithTags("Extensions");

        g.MapGet("", (ExtensionManager m) => TypedResults.Ok(m.GetAll()))
         .WithSummary("Extensiones instaladas y operación en curso");

        g.MapGet("/operation/log", (ExtensionManager m) => TypedResults.Ok(new ExtensionLogResponse(m.GetOperationLog())))
         .WithSummary("Salida de la última instalación o desinstalación");

        g.MapPost("/preview", (PreviewRequest req, ExtensionManager m, CancellationToken ct) =>
                Guard(async () => Results.Ok(await m.PreviewAsync(req, ct))))
         .WithSummary("Descargar y comprobar una extensión sin instalarla")
         .WithDescription("""
            Acepta un enlace de GitHub (owner/repo, …/tree/ref/carpeta, …/releases/tag/x) o una
            entrada de catálogo. Devuelve lo que hace, la versión, el commit y el SHA-256 del
            paquete, y un token para instalar exactamente eso.
            """);

        g.MapPost("/install", (ExtensionInstallRequest req, ExtensionManager m) =>
                Guard(() => { m.StartInstall(req.Token, req.Sha256, req.Replace); return Task.FromResult(Results.Accepted("/api/extensions")); }))
         .WithSummary("Instalar lo previsualizado (el SHA-256 debe coincidir)");

        g.MapPost("/{id}/uninstall", (string id, ExtensionManager m) =>
                Guard(() => { m.StartUninstall(id); return Task.FromResult(Results.Accepted("/api/extensions")); }))
         .WithSummary("Desinstalar una extensión");

        g.MapPost("/{id}/enabled", (string id, ExtensionEnableRequest req, ExtensionManager m) =>
                Guard(() => { m.SetEnabled(id, req.Enabled); return Task.FromResult(Results.NoContent()); }))
         .WithSummary("Activar o desactivar ganchos, apps y pestaña (el componente sigue instalado)");

        g.MapPost("/{id}/detect", (string id, ExtensionManager m) =>
                Guard(() => Task.FromResult(Results.Ok(new { installed = m.Recheck(id) }))))
         .WithSummary("Volver a comprobar si el componente está instalado");

        g.MapGet("/{id}/update", (string id, ExtensionManager m, CancellationToken ct) =>
                Guard(async () => Results.Ok(await m.CheckUpdateAsync(id, ct))))
         .WithSummary("Buscar una versión nueva (devuelve la vista previa si la hay)");

        g.MapGet("/{id}/log", (string id, ExtensionManager m) => TypedResults.Ok(new ExtensionLogResponse(m.GetLog(id))))
         .WithSummary("Registro de scripts de la extensión");

        // Iconos: imágenes de terceros. Con "sandbox" y nosniff, un SVG abierto fuera de un <img>
        // no puede ejecutar nada con el origen del panel.
        g.MapGet("/{id}/icon", (string id, HttpContext ctx, ExtensionManager m) =>
        {
            var file = m.ResolveIconFile(id);
            if (file is null) return Results.NotFound();
            IconHeaders(ctx);
            return Results.File(file, ExtensionFormat.IconContentType(file));
        }).WithSummary("Icono de una extensión instalada");

        g.MapGet("/catalogs/icon", async (string url, int index, HttpContext ctx, ExtensionManager m, CancellationToken ct) =>
        {
            var icon = await m.GetCatalogIconAsync(url, index, ct);
            if (icon is not { } i) return Results.NotFound();
            IconHeaders(ctx);
            return Results.Bytes(i.Bytes, i.ContentType);
        }).WithSummary("Icono de una entrada de catálogo (se descarga de GitHub y se guarda una hora)");

        g.MapGet("/catalogs", (bool? refresh, ExtensionManager m, CancellationToken ct) =>
                Guard(async () => Results.Ok(await m.GetCatalogsAsync(refresh == true, ct))))
         .WithSummary("Catálogos y sus extensiones");

        g.MapPost("/catalogs", (CatalogAddRequest req, ExtensionManager m, CancellationToken ct) =>
                Guard(async () => Results.Ok(await m.AddCatalogAsync(req.Url, ct))))
         .WithSummary("Añadir un catálogo (repositorio con osms-catalog.json)");

        g.MapDelete("/catalogs", (string url, ExtensionManager m) =>
                Guard(() => { m.RemoveCatalog(url); return Task.FromResult(Results.NoContent()); }))
         .WithSummary("Quitar un catálogo");

        // Pestañas: ficheros estáticos del paquete, con un token en la ruta (ver ResolvePanelFile).
        // La CSP "sandbox" aísla la página aunque se abra fuera del iframe del panel.
        var types = new FileExtensionContentTypeProvider();
        app.MapGet("/ext/{id}/{token}/{**path}", (string id, string token, string? path, HttpContext ctx, ExtensionManager m) =>
        {
            var file = m.ResolvePanelFile(id, token, path ?? "");
            if (file is null) return Results.NotFound();
            var h = ctx.Response.Headers;
            h["X-Frame-Options"] = "SAMEORIGIN";
            h["Content-Security-Policy"] =
                "sandbox allow-scripts allow-forms allow-popups allow-downloads; frame-ancestors 'self'";
            h["Cache-Control"] = "no-cache";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            if (!types.TryGetContentType(file, out var type)) type = "application/octet-stream";
            return Results.File(file, type);
        }).ExcludeFromDescription();

        return app;
    }

    static void IconHeaders(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        h["X-Content-Type-Options"] = "nosniff";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        // La URL lleva la versión (?v=), así que puede guardarse.
        h["Cache-Control"] = "private, max-age=86400";
    }

    /// <summary>Errores esperables → 400/409/502 con el mensaje (ya traducido), no un 500.</summary>
    static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ExtensionException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (GitHubException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
        catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
        catch (ExtensionFormatException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (TaskCanceledException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout); }
    }
}
