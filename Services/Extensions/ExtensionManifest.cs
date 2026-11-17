using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenStreamMS.Services.Extensions;

// ── Manifiesto de una extensión (osms-extension.json) ───────────────────────────
//
// Un repositorio de GitHub (o una carpeta dentro de él) con este fichero en la raíz del
// paquete. El formato está documentado en docs/extensiones.md; aquí solo se valida.

/// <summary>Contenido de <c>osms-extension.json</c>.</summary>
public sealed record ExtensionManifest
{
    /// <summary>Versión del formato. Solo existe la 1.</summary>
    public int     Schema      { get; init; } = 1;
    /// <summary>Identificador único: minúsculas, cifras y guiones (carpeta de instalación, rutas de la API).</summary>
    public string  Id          { get; init; } = "";
    public string  Name        { get; init; } = "";
    public string? Description { get; init; }
    public string? Author      { get; init; }
    public string? Homepage    { get; init; }
    public string? License     { get; init; }
    /// <summary>Texto que se enseña antes de instalar (licencias, riesgos, requisitos).</summary>
    public string? Notice      { get; init; }
    /// <summary>Versión mínima de OpenStreamMS (x.y.z), si la necesita.</summary>
    public string? MinOsmsVersion { get; init; }

    public ExtensionComponent? Component { get; init; }
    public ExtensionHooks?     Hooks     { get; init; }
    public List<ExtensionApp>? Apps      { get; init; }
    public ExtensionPanel?     Panel     { get; init; }

    /// <summary>Capacidades que declara, para enseñarlas antes de instalar.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Capabilities =>
        new[]
        {
            Component is not null ? "component" : null,
            Hooks is not null && Hooks.Any ? "hooks" : null,
            Apps is { Count: > 0 } ? "apps" : null,
            Panel is not null ? "panel" : null,
        }.OfType<string>().ToList();
}

/// <summary>Algo que se instala en Windows: scripts de instalar, desinstalar y detectar.</summary>
public sealed record ExtensionComponent
{
    /// <summary>Script (PowerShell) que instala. Ruta relativa al paquete.</summary>
    public string  Install   { get; init; } = "";
    /// <summary>Script que desinstala. Si falta, desinstalar solo borra los ficheros de la extensión.</summary>
    public string? Uninstall { get; init; }
    /// <summary>Script que dice si está instalado (código 0 = sí). Si falta, cuenta lo que diga el registro.</summary>
    public string? Detect    { get; init; }
    /// <summary>Pide reiniciar Windows al terminar.</summary>
    public bool    Reboot    { get; init; }
    /// <summary>Tiempo máximo de cada script, en segundos (por defecto 300).</summary>
    public int     TimeoutSec { get; init; } = 300;
}

/// <summary>Scripts que se lanzan con el ciclo de vida de sesiones y puestos.</summary>
public sealed record ExtensionHooks
{
    public string? SessionStart { get; init; }
    public string? SessionStop  { get; init; }
    public string? SeatStart    { get; init; }
    public string? SeatStop     { get; init; }
    /// <summary>"system" (por defecto) o "user": en la sesión de Windows y con el usuario de la sesión.</summary>
    public string  RunAs        { get; init; } = "system";
    /// <summary>Tiempo máximo de cada script, en segundos (por defecto 60).</summary>
    public int     TimeoutSec   { get; init; } = 60;

    [JsonIgnore]
    public bool Any => SessionStart is not null || SessionStop is not null || SeatStart is not null || SeatStop is not null;

    public string? For(ExtensionEvent e) => e switch
    {
        ExtensionEvent.SessionStart => SessionStart,
        ExtensionEvent.SessionStop  => SessionStop,
        ExtensionEvent.SeatStart    => SeatStart,
        ExtensionEvent.SeatStop     => SeatStop,
        _ => null,
    };
}

public enum ExtensionEvent { SessionStart, SessionStop, SeatStart, SeatStop }

/// <summary>
/// Una entrada para la lista de apps de Moonlight de cada sesión. Los campos son los de
/// apps.json de Sunshine; <c>{ext}</c> en cualquier texto se cambia por la carpeta de la extensión.
/// </summary>
public sealed record ExtensionApp
{
    public string        Name      { get; init; } = "";
    public string?       Cmd       { get; init; }
    public List<string>? Detached  { get; init; }
    public List<ExtensionPrepCmd>? PrepCmd { get; init; }
    public string?       ImagePath { get; init; }
    public string?       WorkingDir { get; init; }
    public bool?         AutoDetach { get; init; }
}

public sealed record ExtensionPrepCmd
{
    public string? Do       { get; init; }
    public string? Undo     { get; init; }
    public bool    Elevated { get; init; }
}

/// <summary>Pestaña propia en el panel: una página estática del paquete, aislada en un iframe.</summary>
public sealed record ExtensionPanel
{
    /// <summary>Título de la pestaña.</summary>
    public string Title { get; init; } = "";
    /// <summary>Página de entrada, relativa al paquete (por defecto panel/index.html).</summary>
    public string Entry { get; init; } = "panel/index.html";
}

// ── Catálogo (osms-catalog.json) ───────────────────────────────────────────────

/// <summary>Contenido de <c>osms-catalog.json</c>: una lista de extensiones con su repositorio.</summary>
public sealed record ExtensionCatalog
{
    public int     Schema      { get; init; } = 1;
    public string  Name        { get; init; } = "";
    public string? Description { get; init; }
    public List<CatalogEntry> Extensions { get; init; } = [];
}

public sealed record CatalogEntry
{
    /// <summary>Repositorio (owner/repo o URL de GitHub).</summary>
    public string  Repo        { get; init; } = "";
    /// <summary>Carpeta del repositorio donde está la extensión, si no es la raíz.</summary>
    public string? Path        { get; init; }
    /// <summary>Etiqueta o commit fijado. Sin él, la última release (o la rama principal).</summary>
    public string? Ref         { get; init; }
    /// <summary>SHA-256 esperado del paquete. Si no coincide, no se instala.</summary>
    public string? Sha256      { get; init; }
    public string? Id          { get; init; }
    public string? Name        { get; init; }
    public string? Description { get; init; }
    public string? Notice      { get; init; }
    public List<string>? Tags  { get; init; }
}

// ── Lectura y validación ──────────────────────────────────────────────────────

public sealed class ExtensionFormatException(string message) : Exception(message);

public static partial class ExtensionFormat
{
    public const string ManifestFile = "osms-extension.json";
    public const string CatalogFile  = "osms-catalog.json";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,46}[a-z0-9])?$")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string? id) => id is not null && IdRegex().IsMatch(id);

    public static ExtensionManifest ParseManifest(string json)
    {
        ExtensionManifest? m;
        try { m = JsonSerializer.Deserialize<ExtensionManifest>(json, Json); }
        catch (JsonException ex) { throw new ExtensionFormatException($"{ManifestFile}: {ex.Message}"); }
        if (m is null) throw new ExtensionFormatException($"{ManifestFile}: empty");
        Validate(m);
        return m;
    }

    public static ExtensionCatalog ParseCatalog(string json)
    {
        ExtensionCatalog? c;
        try { c = JsonSerializer.Deserialize<ExtensionCatalog>(json, Json); }
        catch (JsonException ex) { throw new ExtensionFormatException($"{CatalogFile}: {ex.Message}"); }
        if (c is null) throw new ExtensionFormatException($"{CatalogFile}: empty");
        if (c.Schema != 1) throw new ExtensionFormatException($"{CatalogFile}: unsupported schema {c.Schema}");
        foreach (var e in c.Extensions)
        {
            if (!GitHubRef.TryParse(e.Repo, out _))
                throw new ExtensionFormatException($"{CatalogFile}: invalid repository '{e.Repo}'");
            if (e.Path is not null && !IsSafeRelativePath(e.Path))
                throw new ExtensionFormatException($"{CatalogFile}: invalid path '{e.Path}'");
            if (e.Sha256 is not null && !IsSha256(e.Sha256))
                throw new ExtensionFormatException($"{CatalogFile}: invalid sha256 for '{e.Repo}'");
        }
        return c;
    }

    public static void Validate(ExtensionManifest m)
    {
        void Fail(string what) => throw new ExtensionFormatException($"{ManifestFile}: {what}");

        if (m.Schema != 1) Fail($"unsupported schema {m.Schema}");
        if (!IsValidId(m.Id)) Fail($"invalid id '{m.Id}' (lowercase letters, digits and hyphens, up to 48)");
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 80) Fail("name is required (up to 80 characters)");
        if (m.MinOsmsVersion is not null && !Version.TryParse(m.MinOsmsVersion, out _)) Fail("invalid minOsmsVersion");
        if (m.Homepage is not null && !IsHttpsUrl(m.Homepage)) Fail("homepage must be https");
        if (m.Capabilities.Count == 0) Fail("declares nothing to do (component, hooks, apps or panel)");

        void Script(string? path, string field)
        {
            if (path is null) return;
            if (!IsSafeRelativePath(path) || !path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                Fail($"{field}: must be a relative path to a .ps1 in the package");
        }

        if (m.Component is { } c)
        {
            if (string.IsNullOrWhiteSpace(c.Install)) Fail("component.install is required");
            Script(c.Install, "component.install");
            Script(c.Uninstall, "component.uninstall");
            Script(c.Detect, "component.detect");
            if (c.TimeoutSec is < 10 or > 3600) Fail("component.timeoutSec must be between 10 and 3600");
        }
        if (m.Hooks is { } h)
        {
            Script(h.SessionStart, "hooks.sessionStart");
            Script(h.SessionStop, "hooks.sessionStop");
            Script(h.SeatStart, "hooks.seatStart");
            Script(h.SeatStop, "hooks.seatStop");
            if (h.RunAs is not ("system" or "user")) Fail("hooks.runAs must be system or user");
            if (h.TimeoutSec is < 1 or > 600) Fail("hooks.timeoutSec must be between 1 and 600");
        }
        if (m.Apps is { } apps)
        {
            if (apps.Count > 20) Fail("apps: 20 at most");
            foreach (var a in apps)
                if (string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 60) Fail("apps[].name is required (up to 60 characters)");
            if (apps.Select(a => a.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != apps.Count)
                Fail("apps: duplicate names");
        }
        if (m.Panel is { } p)
        {
            if (string.IsNullOrWhiteSpace(p.Title) || p.Title.Length > 30) Fail("panel.title is required (up to 30 characters)");
            if (!IsSafeRelativePath(p.Entry) || !p.Entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                Fail("panel.entry must be a relative path to an .html in the package");
        }
    }

    /// <summary>Ruta relativa sin salir del paquete: sin raíz, sin "..", sin unidad ni ADS.</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 200) return false;
        if (path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\')) return false;
        var parts = path.Split('/', '\\');
        return parts.All(p => p.Length > 0 && p != "." && p != ".." && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    public static bool IsSha256(string? s) =>
        s is { Length: 64 } && s.All(Uri.IsHexDigit);

    static bool IsHttpsUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>¿Cumple la versión mínima? Una versión mínima ilegible no bloquea (ya se validó).</summary>
    public static bool SupportsVersion(ExtensionManifest m, Version current) =>
        m.MinOsmsVersion is null || !Version.TryParse(m.MinOsmsVersion, out var min) || current >= min;
}
