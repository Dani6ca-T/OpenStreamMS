using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenStreamMS.Services.Extensions;

/// <summary>Un repositorio de GitHub, con rama/etiqueta y carpeta opcionales.</summary>
public sealed partial record GitHubRef(string Owner, string Repo, string? Ref = null, string? Path = null)
{
    public string Slug => $"{Owner}/{Repo}";
    public string Url  => $"https://github.com/{Owner}/{Repo}";

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex OwnerRegex();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoRegex();
    [GeneratedRegex(@"^[A-Za-z0-9._/-]{1,200}$")]
    private static partial Regex RefRegex();

    /// <summary>
    /// Acepta <c>owner/repo</c>, <c>github.com/owner/repo(.git)</c>,
    /// <c>…/tree/&lt;ref&gt;/&lt;carpeta&gt;</c> y <c>…/releases/tag/&lt;etiqueta&gt;</c>.
    /// Solo github.com: nada de otros servidores.
    /// </summary>
    public static bool TryParse(string? input, out GitHubRef result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();

        if (s.Contains("://"))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps ||
                !(u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  u.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) ||
                !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || u.Port != 443)
                return false;
            s = Uri.UnescapeDataString(u.AbsolutePath);
        }
        else if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            s = s["github.com/".Length..];
        }

        var parts = s.Trim('/').Split('/');
        if (parts.Length < 2) return false;
        var owner = parts[0];
        var repo  = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (!OwnerRegex().IsMatch(owner) || !RepoRegex().IsMatch(repo) || repo is "." or "..") return false;

        string? @ref = null, path = null;
        if (parts.Length == 2) { }
        else if (parts.Length >= 4 && parts[2] == "tree")
        {
            // La rama puede llevar "/" y no se distingue de la carpeta: se toma el primer
            // segmento como ref. Para ramas con "/" está el campo ref del catálogo.
            @ref = parts[3];
            if (parts.Length > 4) path = string.Join('/', parts[4..]);
        }
        else if (parts.Length == 5 && parts[2] == "releases" && parts[3] == "tag")
        {
            @ref = parts[4];
        }
        else return false;

        if (@ref is not null && !IsValidRef(@ref)) return false;
        if (path is not null && !ExtensionFormat.IsSafeRelativePath(path)) return false;
        result = new GitHubRef(owner, repo, @ref, path);
        return true;
    }

    public static bool IsValidRef(string r) =>
        RefRegex().IsMatch(r) && !r.Contains("..") && !r.StartsWith('/') && !r.EndsWith('/');
}

/// <summary>Versión concreta resuelta: commit fijo y de dónde se baja el paquete.</summary>
public sealed record ResolvedSource(
    GitHubRef Repo,
    // Etiqueta de la release, o el commit si no hay release.
    string    Ref,
    string    Commit,
    bool      IsRelease,
    string    VersionLabel,
    DateTime? PublishedAt,
    string    PackageUrl,
    // Fichero de la release usado como paquete; null = el código del commit.
    string?   AssetName);

public sealed class GitHubException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Acceso a GitHub sin credenciales (60 peticiones por hora y por IP: de sobra para esto).
/// Solo se habla con api.github.com, github.com y sus servidores de descargas.
/// </summary>
public sealed class GitHubSource
{
    /// <summary>Nombre del fichero de una release que, si existe, es el paquete de la extensión.</summary>
    public const string PackageAsset = "osms-extension.zip";

    public const long MaxPackageBytes   = 300L * 1024 * 1024;
    public const long MaxExtractedBytes = 1024L * 1024 * 1024;
    public const int  MaxEntries        = 20_000;

    static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com", "github.com", "codeload.github.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com", "raw.githubusercontent.com",
    };

    readonly HttpClient _http;

    public GitHubSource(HttpMessageHandler? handler = null)
    {
        var h = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // las redirecciones se siguen a mano, comprobando el destino
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenStreamMS", "1"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    // ── Resolver una versión ───────────────────────────────────────────────

    /// <summary>
    /// Con ref: esa etiqueta, rama o commit. Sin ref: la última release y, si el repositorio
    /// no tiene, el último commit de la rama principal. Siempre se fija el commit.
    /// </summary>
    public async Task<ResolvedSource> ResolveAsync(GitHubRef repo, CancellationToken ct = default)
    {
        JsonElement? release = null;
        if (repo.Ref is null)
        {
            release = await GetJsonOrNullAsync($"repos/{repo.Slug}/releases/latest", ct);
        }
        else
        {
            release = await GetJsonOrNullAsync($"repos/{repo.Slug}/releases/tags/{Uri.EscapeDataString(repo.Ref)}", ct);
        }

        string refName;
        if (release is { } rel)
        {
            refName = rel.GetProperty("tag_name").GetString() ?? throw new GitHubException("release without a tag");
        }
        else if (repo.Ref is not null)
        {
            refName = repo.Ref;
        }
        else
        {
            var info = await GetJsonOrNullAsync($"repos/{repo.Slug}", ct)
                       ?? throw new GitHubException($"{repo.Slug}: repository not found", HttpStatusCode.NotFound);
            refName = info.GetProperty("default_branch").GetString() ?? "main";
        }

        var commitJson = await GetJsonOrNullAsync($"repos/{repo.Slug}/commits/{Uri.EscapeDataString(refName)}", ct)
                         ?? throw new GitHubException($"{repo.Slug}: '{refName}' doesn't exist", HttpStatusCode.NotFound);
        var commit = commitJson.GetProperty("sha").GetString()!;

        // Una release con un osms-extension.zip lo usa como paquete (puede llevar binarios
        // compilados). Si no, el código del commit. Con carpeta (catálogos con varias
        // extensiones en un repositorio) siempre el código: el zip sería de otra cosa.
        if (release is { } r && repo.Path is null &&
            r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), PackageAsset, StringComparison.OrdinalIgnoreCase)) continue;
                var url = a.GetProperty("browser_download_url").GetString()!;
                return new ResolvedSource(repo with { Ref = refName }, refName, commit, true, refName,
                                          PublishedAt(r), url, PackageAsset);
            }
        }

        var isRelease = release is not null;
        var label = isRelease ? refName : (refName == commit ? commit[..7] : $"{refName}@{commit[..7]}");
        return new ResolvedSource(repo with { Ref = isRelease ? refName : commit }, isRelease ? refName : commit, commit,
                                  isRelease, label, release is { } rr ? PublishedAt(rr) : null,
                                  $"https://api.github.com/repos/{repo.Slug}/zipball/{commit}", null);
    }

    static DateTime? PublishedAt(JsonElement rel) =>
        rel.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(p.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// <summary>Un fichero de texto del repositorio (en su rama principal si no se da ref).</summary>
    public async Task<string> GetFileAsync(GitHubRef repo, string path, CancellationToken ct = default)
    {
        var url = $"repos/{repo.Slug}/contents/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}" +
                  (repo.Ref is null ? "" : $"?ref={Uri.EscapeDataString(repo.Ref)}");
        var json = await GetJsonOrNullAsync(url, ct)
                   ?? throw new GitHubException($"{repo.Slug} has no {path}", HttpStatusCode.NotFound);
        if (json.ValueKind != JsonValueKind.Object || json.GetProperty("type").GetString() != "file")
            throw new GitHubException($"{repo.Slug}: {path} isn't a file");
        var b64 = json.GetProperty("content").GetString() ?? "";
        var bytes = Convert.FromBase64String(b64.Replace("\n", ""));
        if (bytes.Length > 1024 * 1024) throw new GitHubException($"{path}: too large");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Un fichero pequeño del repositorio, en bytes, desde raw.githubusercontent.com (no gasta
    /// del límite por hora de la API). Sin ref, la rama principal. Null si no existe.
    /// </summary>
    public async Task<byte[]?> GetRawFileAsync(GitHubRef repo, string path, long maxBytes, CancellationToken ct = default)
    {
        static string Escape(string p) => string.Join('/', p.Split('/').Select(Uri.EscapeDataString));
        var url = $"https://raw.githubusercontent.com/{repo.Slug}/{Escape(repo.Ref ?? "HEAD")}/{Escape(path)}";
        using var resp = await SendFollowingRedirectsAsync(url, ct, allowNotFound: true);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.Content.Headers.ContentLength > maxBytes) throw new GitHubException($"{path}: too large");

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var buf = new MemoryStream();
        var chunk = new byte[16384];
        int n;
        while ((n = await src.ReadAsync(chunk, ct)) > 0)
        {
            if (buf.Length + n > maxBytes) throw new GitHubException($"{path}: too large");
            buf.Write(chunk, 0, n);
        }
        return buf.ToArray();
    }

    // ── Descargar y descomprimir ────────────────────────────────────────────

    /// <summary>Descarga el paquete a <paramref name="file"/> y devuelve su SHA-256 (hex, minúsculas).</summary>
    public async Task<string> DownloadAsync(string url, string file, CancellationToken ct = default)
    {
        using var resp = await SendFollowingRedirectsAsync(url, ct);
        if (resp.Content.Headers.ContentLength is > MaxPackageBytes)
            throw new GitHubException("package too large");

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            total += n;
            if (total > MaxPackageBytes) throw new GitHubException("package too large");
            sha.AppendData(buf, 0, n);
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>
    /// Descomprime el paquete en <paramref name="dest"/>. Si todo cuelga de una sola carpeta
    /// (los zip de código de GitHub: <c>owner-repo-sha/…</c>) se quita. Con
    /// <paramref name="subPath"/>, solo esa carpeta. Ninguna entrada puede salir de dest.
    /// </summary>
    public static void Extract(string zipFile, string dest, string? subPath = null)
    {
        using var zip = ZipFile.OpenRead(zipFile);
        var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (files.Count == 0) throw new ExtensionFormatException("empty package");
        if (zip.Entries.Count > MaxEntries) throw new ExtensionFormatException("too many files in the package");

        static string Norm(string p) => p.Replace('\\', '/');
        var tops = files.Select(e => Norm(e.FullName).Split('/')[0]).Distinct().ToList();
        string prefix = tops.Count == 1 && files.All(e => Norm(e.FullName).Contains('/')) ? tops[0] + "/" : "";
        if (subPath is not null) prefix += subPath.Trim('/').Replace('\\', '/') + "/";

        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        long total = 0;
        int written = 0;
        foreach (var e in files)
        {
            var name = Norm(e.FullName);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rel = name[prefix.Length..];
            if (!ExtensionFormat.IsSafeRelativePath(rel))
                throw new ExtensionFormatException($"path not allowed in the package: {e.FullName}");
            var target = Path.GetFullPath(Path.Combine(root, rel));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ExtensionFormatException($"path not allowed in the package: {e.FullName}");
            total += e.Length;
            if (total > MaxExtractedBytes) throw new ExtensionFormatException("package too large when extracted");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: false);
            written++;
        }
        if (written == 0)
            throw new ExtensionFormatException(subPath is null ? "empty package" : $"folder '{subPath}' isn't in the package");
    }

    // ── HTTP ─────────────────────────────────────────────────────────────

    async Task<JsonElement?> GetJsonOrNullAsync(string apiPath, CancellationToken ct)
    {
        using var resp = await SendFollowingRedirectsAsync($"https://api.github.com/{apiPath}", ct, allowNotFound: true);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    async Task<HttpResponseMessage> SendFollowingRedirectsAsync(string url, CancellationToken ct, bool allowNotFound = false)
    {
        var uri = new Uri(url);
        for (int hop = 0; hop < 6; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
                throw new GitHubException($"host not allowed: {uri.Host}");

            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                resp.Dispose();
                continue;
            }
            if (resp.IsSuccessStatusCode) return resp;
            if (allowNotFound && resp.StatusCode == HttpStatusCode.NotFound) return resp;

            var status = resp.StatusCode;
            var limited = status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
                          resp.Headers.TryGetValues("x-ratelimit-remaining", out var rem) && rem.FirstOrDefault() == "0";
            resp.Dispose();
            throw new GitHubException(limited
                ? "GitHub rate limit reached; try again in a while"
                : $"GitHub answered {(int)status} for {uri.AbsolutePath}", status);
        }
        throw new GitHubException("too many redirects");
    }
}
