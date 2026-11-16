using OpenStreamMS.Services.Session;

namespace OpenStreamMS.Core.Api;

public static class RdpWrapperEndpoints
{
    public static IEndpointRouteBuilder MapRdpWrapperApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rdpwrap").WithTags("Multi-RDP");

        group.MapGet("/status", (RdpWrapperManager mgr) =>
                TypedResults.Ok(mgr.GetStatus()))
             .WithName("GetRdpWrapperStatus")
             .WithSummary("Si el equipo admite varias sesiones a la vez")
             .WithDescription("""
                Solo lectura: OpenStreamMS no instala ni configura nada para tener varias sesiones.
                allowed es true con Windows Server y el rol Host de sesión de Escritorio remoto,
                con Windows Enterprise multisesión, si el servicio de Escritorio remoto usa un
                componente que ha instalado el usuario, o si ahora mismo hay varias sesiones activas.
                """);

        return app;
    }
}
