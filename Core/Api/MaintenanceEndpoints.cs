using OpenStreamMS.Core.Helpers;

namespace OpenStreamMS.Core.Api;

public record ChangeOperationModeRequest(string OperationMode, long Revision, bool ConfirmNormal = false);
public static class MaintenanceEndpoints
{
    public static object Status(ModeConfiguration store) => Status(store.Read());
    private static object Status(ModeState state) => new {
        activeMode = StartupSafetyPolicy.Read().Mode.ToString(), configuredMode = state.Policy.Mode.ToString(),
        nextMode = (state.PendingMode ?? state.Policy.Mode).ToString(), revision = state.Revision,
        restartRequired = (state.PendingMode ?? state.Policy.Mode) != StartupSafetyPolicy.Read().Mode,
        diagnostic = StartupSafetyPolicy.Read().Diagnostic ?? state.Policy.Diagnostic,
        systemChangesAllowed = !StartupSafetyPolicy.Read().RestrictedStartup
    };
    public static bool RequestAllowed(string method, string path)
    {
        path = path.TrimEnd('/').ToLowerInvariant();
        if (path == "/api/settings/operation-mode") return method is "GET" or "PUT";
        if (path == "/api/settings/logging") return method == "PUT";
        if (path.Contains("/identify/") || path.Contains("/sunshine") || path.EndsWith("/clients") || path.EndsWith("/pairings")) return false;
        return method is "GET" or "HEAD" or "OPTIONS";
    }
    public static IApplicationBuilder UseMaintenancePolicy(this IApplicationBuilder app) => app.Use(async (context, next) => {
        try {
            if (context.Request.Path.StartsWithSegments("/api") && StartupSafetyPolicy.Read().RestrictedStartup &&
                !RequestAllowed(context.Request.Method, context.Request.Path.Value ?? ""))
                throw new MaintenanceModeException(context.Request.Path.Value ?? "API");
            await next();
        } catch (MaintenanceModeException) {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { code = MaintenanceModeException.Code, message = "Operation blocked by Safe Mode." });
        }
    });
    public static IEndpointRouteBuilder MapMaintenanceApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/operation-mode", (ModeConfiguration store) => Status(store));
        app.MapPut("/api/settings/operation-mode", (ChangeOperationModeRequest request, ModeConfiguration store) => {
            // Authentication middleware must run before these routes. Local Tray bypass never includes settings.
            if (!Enum.TryParse<OperationMode>(request.OperationMode, false, out var mode) || !Enum.IsDefined(mode))
                return Results.BadRequest(new { code = "invalid_operation_mode" });
            try {
                var state = store.Request(mode, request.Revision, request.ConfirmNormal);
                OpenStreamMS.Services.Logger.Warning($"[Maintenance] Requested {mode}; revision {state.Revision}. Effective mode remains {StartupSafetyPolicy.Read().Mode} until SCM restart.");
                return Results.Ok(Status(state));
            } catch (ArgumentException ex) { return Results.BadRequest(new { code = ex.Message }); }
            catch (InvalidOperationException) { return Results.Conflict(new { code = "policy_conflict" }); }
            catch (IOException) { return Results.Conflict(new { code = "policy_busy" }); }
        });
        return app;
    }
}
