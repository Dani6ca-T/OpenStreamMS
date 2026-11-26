using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using OpenStreamMS.Core.Helpers;

namespace OpenStreamMS.Services.Updates;

/// <summary>
/// Versión x.y.z[-pre] de OpenStreamMS. Las releases se etiquetan vX.Y.Z o vX.Y.Z-beta.N
/// (build-release.yml) y el ejecutable lleva la misma en InformationalVersion (más "+commit").
/// Se ordena como SemVer: 2.3.0-beta.1 &lt; 2.3.0.
/// </summary>
public sealed record AppVersion(Version Core, string Pre) : IComparable<AppVersion>
{
    public bool IsPrerelease => Pre.Length > 0;

    public static AppVersion? Parse(string? text)
    {
        var s = text?.Trim() ?? "";
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        int dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre  = dash >= 0 ? s[(dash + 1)..] : "";
        if (dash >= 0 && pre.Length == 0) return null;
        if (!Version.TryParse(core, out var v)) return null;
        return new AppVersion(new Version(v.Major, v.Minor, Math.Max(v.Build, 0)), pre);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        int c = Core.CompareTo(other.Core);
        if (c != 0) return c;
        // Sin sufijo va después de cualquier beta de la misma versión.
        if (Pre.Length == 0 || other.Pre.Length == 0) return Math.Sign(other.Pre.Length.CompareTo(Pre.Length));
        var a = Pre.Split('.');
        var b = other.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = int.TryParse(a[i], out var ai), bn = int.TryParse(b[i], out var bi);
            c = an && bn ? ai.CompareTo(bi)
              : an ? -1 : bn ? 1
              : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => IsPrerelease ? $"{Core.ToString(3)}-{Pre}" : Core.ToString(3);
}

/// <summary>Instalador de una release (OpenStreamMS-Setup-x.y.z.exe) con el SHA-256 que publica GitHub.</summary>
public sealed record UpdateAsset(string Name, string Url, long Size, string Sha256);

/// <param name="Version">Versión de la release, sin la "v" de la etiqueta.</param>
/// <param name="Url">Página de la release en GitHub (notas e instalador).</param>
/// <param name="Installer">Instalador que se puede descargar y comprobar; null si la release no lo trae.</param>
public sealed record UpdateRelease(string Version, string Url, DateTimeOffset? PublishedAt, bool Prerelease,
                                   [property: System.Text.Json.Serialization.JsonIgnore] UpdateAsset? Installer = null)
{
    /// <summary>Se puede instalar desde el panel (hay instalador con su SHA-256).</summary>
    public bool Installable => Installer is not null;
}

/// <param name="CurrentVersion">Versión instalada.</param>
/// <param name="DevBuild">Compilación de desarrollo (Debug o CI sin etiqueta): no se anuncian actualizaciones.</param>
/// <param name="CheckEnabled">Si se mira sola en GitHub al abrir el panel.</param>
/// <param name="Latest">Última release publicada que le corresponde (null si no se ha mirado o falló).</param>
/// <param name="UpdateAvailable">Latest es más nueva que la instalada.</param>
/// <param name="Dismissed">Versión que se pidió no volver a anunciar en la barra.</param>
/// <param name="CheckedAt">Cuándo se miró por última vez.</param>
/// <param name="Error">"offline", "ratelimit" o "github" si la última comprobación falló.</param>
public sealed record UpdateStatus(
    string CurrentVersion, bool DevBuild, bool CheckEnabled, UpdateRelease? Latest, bool UpdateAvailable,
    string Dismissed, DateTimeOffset? CheckedAt, string? Error);

/// <summary>
/// Mira en las releases de GitHub si hay una versión nueva. Solo cuando el panel lo pide (no hay
/// tarea en segundo plano), con caché de 6 h, y nada si el administrador lo desactiva. A GitHub
/// solo llega la IP y "OpenStreamMS/versión" como agente.
/// </summary>
public sealed class UpdateChecker
{
    public const string Repo = "OpenstreamCo/OpenStreamMultiSeat";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

    static readonly TimeSpan CacheFor      = TimeSpan.FromHours(6);
    static readonly TimeSpan ErrorCacheFor = TimeSpan.FromMinutes(15);
    static readonly TimeSpan ForcedMinGap  = TimeSpan.FromSeconds(20);

    readonly ServiceConfig _cfg;
    readonly HttpClient _http;
    readonly Func<DateTimeOffset> _now;
    readonly SemaphoreSlim _gate = new(1, 1);

    UpdateRelease? _latest;
    DateTimeOffset? _checkedAt;
    string? _error;

    public AppVersion? Current { get; }
    public string CurrentVersion { get; }
    public bool DevBuild { get; }

    public UpdateChecker(ServiceConfig cfg, HttpMessageHandler? handler = null, string? currentVersion = null,
                         bool? devBuild = null, Func<DateTimeOffset>? now = null)
    {
        _cfg = cfg;
        _now = now ?? (() => DateTimeOffset.UtcNow);

        currentVersion ??= Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                           ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Current = AppVersion.Parse(currentVersion);
        CurrentVersion = Current?.ToString() ?? currentVersion;
#if DEBUG
        DevBuild = devBuild ?? true;
#else
        // La CI marca las compilaciones sin etiqueta como 1.0.N-dev (build-release.yml).
        DevBuild = devBuild ?? (Current is null || Current.Pre.Split('.').Contains("dev", StringComparer.OrdinalIgnoreCase));
#endif

        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenStreamMS", Current?.ToString() ?? "0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>Lo que se sabe ahora, sin ir a GitHub.</summary>
    public UpdateStatus Status()
    {
        var latest = _latest;
        bool newer = !DevBuild && Current is not null && latest is not null &&
                     AppVersion.Parse(latest.Version) is { } lv && lv > Current;
        return new UpdateStatus(CurrentVersion, DevBuild, _cfg.CheckForUpdates, latest, newer,
                                _cfg.UpdateDismissed, _checkedAt, _error);
    }

    /// <summary>
    /// Mira en GitHub si toca. Sin force: solo con la comprobación activada y pasadas 6 h (15 min
    /// si falló). Con force ("Buscar ahora"): siempre, salvo dos veces seguidas en 20 s.
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(bool force = false)
    {
        if (!force && !_cfg.CheckForUpdates) return Status();
        await _gate.WaitAsync();
        try
        {
            var now = _now();
            var wait = force ? ForcedMinGap : _error is null ? CacheFor : ErrorCacheFor;
            if (_checkedAt is { } at && now - at < wait) return Status();

            try
            {
                _latest = await FetchLatestAsync();
                _error = null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                          or InvalidOperationException or KeyNotFoundException or UpdateCheckException)
            {
                _error = ex switch
                {
                    UpdateCheckException { RateLimited: true } => "ratelimit",
                    UpdateCheckException or JsonException or InvalidOperationException or KeyNotFoundException => "github",
                    _ => "offline",
                };
                Logger.Warning($"[Updates] No se pudo mirar si hay versión nueva en GitHub ({_error}): {ex.Message}");
            }
            _checkedAt = now;
            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Release más nueva que le corresponde a esta instalación: solo las estables, salvo que la
    /// instalada sea una beta (entonces también las betas posteriores). Sin borradores.
    /// </summary>
    async Task<UpdateRelease?> FetchLatestAsync()
    {
        using var res = await _http.GetAsync($"repos/{Repo}/releases?per_page=30");
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
            res.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0")
            throw new UpdateCheckException("GitHub rate limit", rateLimited: true);
        if (!res.IsSuccessStatusCode)
            throw new UpdateCheckException($"GitHub answered {(int)res.StatusCode}");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        bool wantPre = Current is { IsPrerelease: true } && !DevBuild;

        UpdateRelease? best = null;
        AppVersion? bestVersion = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            bool pre = r.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            var tag = r.GetProperty("tag_name").GetString();
            if (AppVersion.Parse(tag) is not { } v) continue;
            if ((pre || v.IsPrerelease) && !wantPre) continue;
            if (bestVersion is not null && !(v > bestVersion)) continue;

            var url = r.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (url is null || !url.StartsWith($"https://github.com/{Repo}/", StringComparison.OrdinalIgnoreCase))
                url = $"{ReleasesPage}/tag/{Uri.EscapeDataString(tag!)}";
            DateTimeOffset? published = r.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String &&
                                        DateTimeOffset.TryParse(pa.GetString(), out var when) ? when : null;
            best = new UpdateRelease(v.ToString(), url, published, pre || v.IsPrerelease, FindInstaller(r, v));
            bestVersion = v;
        }
        return best;
    }

    /// <summary>
    /// El OpenStreamMS-Setup-&lt;versión&gt;.exe de la release, solo si GitHub da su SHA-256 y se
    /// descarga de este repositorio. Sin eso el panel solo ofrece la página de la release.
    /// </summary>
    static UpdateAsset? FindInstaller(JsonElement release, AppVersion version)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var name = $"OpenStreamMS-Setup-{version}.exe";
        foreach (var a in assets.EnumerateArray())
        {
            if (!string.Equals(a.TryGetProperty("name", out var n) ? n.GetString() : null, name, StringComparison.OrdinalIgnoreCase)) continue;
            var url    = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
            long size  = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            if (url is null || !url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            var sha = digest["sha256:".Length..].ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return null;
            if (size <= 0 || size > UpdateInstaller.MaxInstallerBytes) return null;
            return new UpdateAsset(name, url, size, sha);
        }
        return null;
    }
}

public sealed class UpdateCheckException(string message, bool rateLimited = false) : Exception(message)
{
    public bool RateLimited { get; } = rateLimited;
}
