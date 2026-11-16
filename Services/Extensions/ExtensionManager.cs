using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Localization;

namespace OpenStreamMS.Services.Extensions;

/// <summary>Error que se enseña al usuario tal cual (ya traducido).</summary>
public sealed class ExtensionException(string message) : Exception(message);

// ── Estado guardado (extensions\state.json) ─────────────────────────────────────

public sealed class ExtensionState
{
    /// <summary>Ya se añadió el catálogo oficial una vez: si el usuario lo quita, no vuelve.</summary>
    public bool Seeded { get; set; }
    public List<CatalogRef> Catalogs { get; set; } = [];
    public List<InstalledExtension> Installed { get; set; } = [];
}

public sealed record CatalogRef(string Url, DateTime AddedAt);

public sealed record ExtensionSource(
    string  Owner,
    string  Repo,
    string? Path,
    // Lo que se pidió: null = última release (o rama principal); etiqueta, rama o commit.
    string? RequestedRef,
    // Etiqueta de la release o commit instalado.
    string  Ref,
    string  Commit,
    string  Sha256,
    bool    IsRelease,
    string  VersionLabel,
    string? AssetName)
{
    public GitHubRef Repository => new(Owner, Repo, RequestedRef, Path);
    public string Slug => $"{Owner}/{Repo}";
}

public sealed class InstalledExtension
{
    public string  Id          { get; set; } = "";
    public string  Name        { get; set; } = "";
    public ExtensionSource Source { get; set; } = null!;
    /// <summary>Catálogo del que se instaló: las actualizaciones siguen lo que fije el catálogo.</summary>
    public string? CatalogUrl  { get; set; }
    public DateTime InstalledAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool    Enabled     { get; set; } = true;
    public bool    RebootPending { get; set; }
}

// ── Lo que ve el panel ──────────────────────────────────────────────────────────

public sealed record PreviewRequest(string Source, string? Path = null, string? Ref = null, string? CatalogUrl = null);

public sealed record ExtensionPreview(
    string Token, string Id, string Name, string? Description, string? Author, string? Homepage, string? License,
    string? Notice, string? CatalogNotice, IReadOnlyList<string> Capabilities,
    string Repo, string RepoUrl, string? Path, string Version, string Commit, bool IsRelease, DateTime? PublishedAt,
    string Sha256, bool Sha256Pinned, string? CatalogUrl,
    string? InstalledVersion, bool ReplacesOtherSource,
    string? HookRunAs, IReadOnlyList<string> Hooks, IReadOnlyList<string> Apps, string? PanelTitle,
    bool HasComponent, bool Compatible, string? MinOsmsVersion);

public sealed record ExtensionInfo(
    string Id, string Name, string? Description, string? Author, string? Homepage, string? Notice,
    string Repo, string RepoUrl, string? Path, string Version, string Commit, bool IsRelease, string Sha256,
    string? CatalogUrl, DateTime InstalledAt, DateTime? UpdatedAt, bool Enabled, bool RebootPending,
    IReadOnlyList<string> Capabilities, bool? ComponentInstalled,
    string? HookRunAs, IReadOnlyList<string> Hooks, IReadOnlyList<string> Apps,
    string? PanelTitle, string? PanelUrl, bool Broken);

public sealed record ExtensionOperation(
    string Kind, string Id, string Name, string State, string? Message, bool RebootRequired,
    DateTime StartedAt, DateTime? EndedAt);

public sealed record ExtensionsResponse(
    IReadOnlyList<ExtensionInfo> Installed, ExtensionOperation? Operation, bool Busy, string OsmsVersion);

public sealed record CatalogView(
    string Url, string Name, string? Description, bool Official, string? Error,
    IReadOnlyList<CatalogEntryView> Extensions, DateTime? FetchedAt);

public sealed record CatalogEntryView(
    string Repo, string RepoUrl, string? Path, string? Ref, bool Sha256Pinned, string? Id, string Name,
    string? Description, string? Notice, IReadOnlyList<string> Tags, string? InstalledId);

public sealed record UpdateCheck(string Id, string CurrentVersion, string? LatestVersion, bool UpToDate, ExtensionPreview? Preview);

/// <summary>Contexto que reciben los ganchos (como variables de entorno OSMS_*).</summary>
public sealed record HookContext(
    Guid? SessionId, string? SessionName, uint WindowsSessionId, string? WindowsUser, bool Local,
    Guid? SeatId = null, string? SeatName = null);

/// <summary>
/// Extensiones: se instalan desde GitHub (un enlace o un catálogo), se fijan a un commit y a
/// un SHA-256, y pueden instalar un componente, lanzar scripts con el ciclo de vida de
/// sesiones y puestos, añadir apps a Moonlight y tener su pestaña en el panel.
/// Todo lo que ejecutan va como SYSTEM (o con el usuario de la sesión, si lo piden para
/// los ganchos): instalar una es confiar en su autor, y el panel lo dice antes de instalar.
/// </summary>
public sealed class ExtensionManager
{
    public const string OfficialCatalog = "https://github.com/OpenstreamCo/osms-extensions";

    /// <summary>La instancia del servicio (los ganchos se llaman desde sitios sin inyección).</summary>
    public static ExtensionManager? Instance { get; private set; }

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "extensions");

    readonly string _root;
    readonly GitHubSource _github;
    readonly Version _osmsVersion;
    readonly object _lock = new();
    readonly ConcurrentDictionary<string, Pending> _pending = new();
    readonly ConcurrentDictionary<string, ExtensionManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, bool?> _componentInstalled = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> _panelTokens = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (DateTime At, CatalogView View, ExtensionCatalog? Catalog)> _catalogCache =
        new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _opLog = [];
    ExtensionState _state;
    ExtensionOperation? _op;
    int _busy;

    sealed record Pending(string Token, ResolvedSource Source, string Sha256, ExtensionManifest Manifest,
                          string StagingDir, string? CatalogUrl, string? RequestedRef, DateTime Expires);

    public ExtensionManager() : this(DefaultRoot, new GitHubSource(), null) { Instance = this; }

    public ExtensionManager(string root, GitHubSource github, Version? osmsVersion)
    {
        _root = root;
        _github = github;
        _osmsVersion = osmsVersion ?? typeof(ExtensionManager).Assembly.GetName().Version ?? new Version(0, 0);
        Directory.CreateDirectory(_root);
        SecureRoot(_root);
        _state = LoadState();
        if (!_state.Seeded)
        {
            _state.Seeded = true;
            if (!_state.Catalogs.Any(c => SameUrl(c.Url, OfficialCatalog)))
                _state.Catalogs.Insert(0, new CatalogRef(OfficialCatalog, DateTime.UtcNow));
            SaveState();
        }
        foreach (var ext in _state.Installed) LoadManifest(ext.Id);
        TryDelete(Path.Combine(_root, "_staging"));
    }

    string StatePath => Path.Combine(_root, "state.json");
    string ExtDir(string id) => Path.Combine(_root, id);
    public string PackageDir(string id) => Path.Combine(ExtDir(id), "pkg");
    string LogDir(string id) => Path.Combine(ExtDir(id), "logs");

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    // ── Lectura ─────────────────────────────────────────────────────────

    public ExtensionsResponse GetAll()
    {
        List<InstalledExtension> list;
        lock (_lock) list = [.. _state.Installed];
        return new ExtensionsResponse(list.Select(ToInfo).ToList(), _op, IsBusy, _osmsVersion.ToString(3));
    }

    public ExtensionInfo? Get(string id)
    {
        InstalledExtension? e;
        lock (_lock) e = _state.Installed.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return e is null ? null : ToInfo(e);
    }

    public string[] GetOperationLog()
    {
        lock (_opLog) return [.. _opLog];
    }

    public string[] GetLog(string id, int lines = 300)
    {
        var dir = LogDir(id);
        if (!Directory.Exists(dir)) return [];
        var all = new List<string>();
        foreach (var f in new DirectoryInfo(dir).GetFiles("*.log").OrderBy(f => f.LastWriteTimeUtc))
        {
            try { all.AddRange(File.ReadLines(f.FullName).Select(l => $"[{f.Name}] {l}")); } catch { }
        }
        return all.Count > lines ? all[^lines..].ToArray() : all.ToArray();
    }

    ExtensionInfo ToInfo(InstalledExtension e)
    {
        _manifests.TryGetValue(e.Id, out var m);
        var s = e.Source;
        string? panelUrl = null;
        if (m?.Panel is { } p && e.Enabled)
            panelUrl = $"/ext/{e.Id}/{PanelToken(e.Id)}/{p.Entry.Replace('\\', '/')}";
        return new ExtensionInfo(
            e.Id, m?.Name ?? e.Name, m?.Description, m?.Author, m?.Homepage, m?.Notice,
            s.Slug, $"https://github.com/{s.Slug}" + (s.Path is null ? "" : $"/tree/{s.Commit}/{s.Path}"),
            s.Path, s.VersionLabel, s.Commit, s.IsRelease, s.Sha256,
            e.CatalogUrl, e.InstalledAt, e.UpdatedAt, e.Enabled, e.RebootPending,
            m?.Capabilities ?? [], m?.Component is null ? null : _componentInstalled.GetValueOrDefault(e.Id),
            m?.Hooks?.Any == true ? m.Hooks.RunAs : null, HookNames(m), m?.Apps?.Select(a => a.Name).ToList() ?? [],
            m?.Panel?.Title, panelUrl, m is null);
    }

    static List<string> HookNames(ExtensionManifest? m) =>
        m?.Hooks is not { } h ? [] :
        Enum.GetValues<ExtensionEvent>().Where(e => h.For(e) is not null).Select(e => e.ToString()).ToList();

    // ── Vista previa (descarga y comprueba sin instalar nada) ─────────────────

    public async Task<ExtensionPreview> PreviewAsync(PreviewRequest req, CancellationToken ct = default)
    {
        if (!GitHubRef.TryParse(req.Source, out var repo))
            throw Err("ext.invalidSource", req.Source);
        if (req.Path is not null)
        {
            if (!ExtensionFormat.IsSafeRelativePath(req.Path)) throw Err("ext.invalidSource", req.Path);
            repo = repo with { Path = req.Path.Trim('/') };
        }
        if (req.Ref is not null)
        {
            if (!GitHubRef.IsValidRef(req.Ref)) throw Err("ext.invalidSource", req.Ref);
            repo = repo with { Ref = req.Ref };
        }

        // Desde un catálogo manda lo que diga el catálogo (versión fijada y hash), no el cliente.
        string? expectedSha = null, catalogNotice = null, catalogId = null;
        if (req.CatalogUrl is not null)
        {
            var (_, catalog) = await LoadCatalogAsync(req.CatalogUrl, refresh: false, ct);
            var entry = catalog?.Extensions.FirstOrDefault(x =>
                            GitHubRef.TryParse(x.Repo, out var r) &&
                            r.Slug.Equals(repo.Slug, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(x.Path?.Trim('/'), repo.Path, StringComparison.OrdinalIgnoreCase))
                        ?? throw Err("ext.catalogEntryMissing", repo.Slug);
            repo = repo with { Ref = entry.Ref };
            expectedSha = entry.Sha256?.ToLowerInvariant();
            catalogNotice = entry.Notice;
            catalogId = entry.Id;
        }

        var resolved = await _github.ResolveAsync(repo, ct);

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var staging = Path.Combine(_root, "_staging", token);
        Directory.CreateDirectory(staging);
        try
        {
            var zip = Path.Combine(staging, "package.zip");
            var sha = await _github.DownloadAsync(resolved.PackageUrl, zip, ct);
            if (expectedSha is not null && sha != expectedSha)
                throw Err("ext.catalogHashMismatch", expectedSha, sha);

            var pkg = Path.Combine(staging, "pkg");
            Directory.CreateDirectory(pkg);
            try { GitHubSource.Extract(zip, pkg, resolved.AssetName is null ? repo.Path : null); }
            catch (ExtensionFormatException ex) { throw Err("ext.invalidPackage", ex.Message); }
            File.Delete(zip);

            var manifestFile = Path.Combine(pkg, ExtensionFormat.ManifestFile);
            if (!File.Exists(manifestFile)) throw Err("ext.noManifest", ExtensionFormat.ManifestFile);
            ExtensionManifest m;
            try { m = ExtensionFormat.ParseManifest(await File.ReadAllTextAsync(manifestFile, ct)); }
            catch (ExtensionFormatException ex) { throw Err("ext.invalidPackage", ex.Message); }
            CheckFilesExist(m, pkg);
            if (catalogId is not null && !catalogId.Equals(m.Id, StringComparison.Ordinal))
                throw Err("ext.idMismatch", catalogId, m.Id);

            InstalledExtension? current;
            lock (_lock) current = _state.Installed.FirstOrDefault(x => x.Id == m.Id);
            bool otherSource = current is not null &&
                               !(current.Source.Slug.Equals(resolved.Repo.Slug, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(current.Source.Path, repo.Path, StringComparison.OrdinalIgnoreCase));

            var pending = new Pending(token, resolved, sha, m, staging, req.CatalogUrl, repo.Ref ?? req.Ref,
                                      DateTime.UtcNow.AddMinutes(30));
            _pending[token] = pending;
            PurgeExpiredPending();

            return new ExtensionPreview(
                token, m.Id, m.Name, m.Description, m.Author, m.Homepage, m.License, m.Notice, catalogNotice,
                m.Capabilities, resolved.Repo.Slug, resolved.Repo.Url, repo.Path, resolved.VersionLabel, resolved.Commit,
                resolved.IsRelease, resolved.PublishedAt, sha, expectedSha is not null, req.CatalogUrl,
                current?.Source.VersionLabel, otherSource,
                m.Hooks?.Any == true ? m.Hooks.RunAs : null, HookNames(m), m.Apps?.Select(a => a.Name).ToList() ?? [],
                m.Panel?.Title, m.Component is not null, ExtensionFormat.SupportsVersion(m, _osmsVersion), m.MinOsmsVersion);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    static void CheckFilesExist(ExtensionManifest m, string pkg)
    {
        IEnumerable<string?> files =
        [
            m.Component?.Install, m.Component?.Uninstall, m.Component?.Detect,
            m.Hooks?.SessionStart, m.Hooks?.SessionStop, m.Hooks?.SeatStart, m.Hooks?.SeatStop,
            m.Panel?.Entry,
        ];
        foreach (var f in files.OfType<string>())
            if (!File.Exists(Path.Combine(pkg, f)))
                throw Err("ext.invalidPackage", $"missing file: {f}");
    }

    void PurgeExpiredPending()
    {
        foreach (var (k, p) in _pending)
            if (p.Expires < DateTime.UtcNow && _pending.TryRemove(k, out _))
                TryDelete(p.StagingDir);
    }

    // ── Instalar, desinstalar, activar ───────────────────────────────────

    /// <summary>
    /// Instala lo que se previsualizó con <paramref name="token"/>. El SHA-256 que confirma el
    /// usuario tiene que ser el que vio: si no, no se instala nada.
    /// </summary>
    public void StartInstall(string token, string sha256, bool replace)
    {
        if (!_pending.TryGetValue(token, out var p) || p.Expires < DateTime.UtcNow)
            throw Err("ext.previewExpired");
        if (!string.Equals(p.Sha256, sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw Err("ext.hashMismatch");
        if (!ExtensionFormat.SupportsVersion(p.Manifest, _osmsVersion))
            throw Err("ext.incompatible", p.Manifest.MinOsmsVersion ?? "?", _osmsVersion.ToString(3));

        InstalledExtension? current;
        lock (_lock) current = _state.Installed.FirstOrDefault(x => x.Id == p.Manifest.Id);
        if (current is not null && !replace &&
            !(current.Source.Slug.Equals(p.Source.Repo.Slug, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(current.Source.Path, p.Source.Repo.Path, StringComparison.OrdinalIgnoreCase)))
            throw Err("ext.otherSource", p.Manifest.Id, current.Source.Slug);

        BeginOperation(current is null ? "install" : "update", p.Manifest.Id, p.Manifest.Name);
        _pending.TryRemove(token, out _);
        _ = Task.Run(() => RunOperation(() => Install(p, current)));
    }

    void Install(Pending p, InstalledExtension? current)
    {
        var m = p.Manifest;
        var dir = ExtDir(m.Id);
        var pkg = PackageDir(m.Id);
        var old = Path.Combine(dir, "pkg.old");
        Directory.CreateDirectory(dir);
        TryDelete(old);
        bool hadOld = Directory.Exists(pkg);
        if (hadOld) Directory.Move(pkg, old);

        try
        {
            Directory.Move(Path.Combine(p.StagingDir, "pkg"), pkg);
            OpLog($"{m.Name} {p.Source.VersionLabel} ({p.Source.Repo.Slug}@{p.Source.Commit[..7]}, sha256 {p.Sha256})");

            bool reboot = false;
            if (m.Component is { } c)
            {
                OpLog($"→ {c.Install}");
                var code = RunScriptAsSystem(m.Id, pkg, c.Install, "install",
                    BaseEnv(m.Id, "install", current?.Source.VersionLabel), c.TimeoutSec);
                if (code is not (0 or 3010))
                    throw Err("ext.scriptFailed", c.Install, code?.ToString() ?? "timeout");
                reboot = code == 3010 || c.Reboot;
            }

            var entry = new InstalledExtension
            {
                Id = m.Id,
                Name = m.Name,
                Source = new ExtensionSource(p.Source.Repo.Owner, p.Source.Repo.Repo, p.Source.Repo.Path,
                    p.RequestedRef, p.Source.Ref, p.Source.Commit, p.Sha256, p.Source.IsRelease,
                    p.Source.VersionLabel, p.Source.AssetName),
                CatalogUrl = p.CatalogUrl,
                InstalledAt = current?.InstalledAt ?? DateTime.UtcNow,
                UpdatedAt = current is null ? null : DateTime.UtcNow,
                Enabled = current?.Enabled ?? true,
                RebootPending = reboot,
            };
            lock (_lock)
            {
                _state.Installed.RemoveAll(x => x.Id == m.Id);
                _state.Installed.Add(entry);
                SaveState();
            }
            _manifests[m.Id] = m;
            _panelTokens.TryRemove(m.Id, out _);
            if (m.Component is not null) _componentInstalled[m.Id] = Detect(m.Id, m);
            TryDelete(old);
            EndOperation("ok", null, reboot);
        }
        catch
        {
            // Vuelta atrás: el paquete anterior sigue siendo el instalado.
            TryDelete(pkg);
            if (hadOld && Directory.Exists(old)) Directory.Move(old, pkg);
            throw;
        }
        finally
        {
            TryDelete(p.StagingDir);
        }
    }

    public void StartUninstall(string id)
    {
        var e = Find(id) ?? throw Err("ext.notFound", id);
        BeginOperation("uninstall", e.Id, e.Name);
        _ = Task.Run(() => RunOperation(() =>
        {
            _manifests.TryGetValue(e.Id, out var m);
            if (m?.Component?.Uninstall is { } script)
            {
                OpLog($"→ {script}");
                var code = RunScriptAsSystem(e.Id, PackageDir(e.Id), script, "uninstall",
                    BaseEnv(e.Id, "uninstall", e.Source.VersionLabel), m.Component.TimeoutSec);
                if (code is not (0 or 3010))
                    throw Err("ext.scriptFailed", script, code?.ToString() ?? "timeout");
            }
            lock (_lock)
            {
                _state.Installed.RemoveAll(x => x.Id == e.Id);
                SaveState();
            }
            _manifests.TryRemove(e.Id, out _);
            _componentInstalled.TryRemove(e.Id, out _);
            _panelTokens.TryRemove(e.Id, out _);
            TryDelete(ExtDir(e.Id));
            EndOperation("ok", null, false);
        }));
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            var e = _state.Installed.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw Err("ext.notFound", id);
            e.Enabled = enabled;
            SaveState();
        }
        if (!enabled) _panelTokens.TryRemove(id, out _);
    }

    /// <summary>Vuelve a pasar el script de detección (si lo tiene).</summary>
    public bool? Recheck(string id)
    {
        var e = Find(id) ?? throw Err("ext.notFound", id);
        if (!_manifests.TryGetValue(e.Id, out var m) || m.Component is null) return null;
        var v = Detect(e.Id, m);
        _componentInstalled[e.Id] = v;
        return v;
    }

    /// <summary>Al arrancar el servicio: estado de los componentes, sin bloquear el arranque.</summary>
    public void DetectAllInBackground() => _ = Task.Run(() =>
    {
        foreach (var e in Snapshot())
            if (_manifests.TryGetValue(e.Id, out var m) && m.Component is not null)
                _componentInstalled[e.Id] = Detect(e.Id, m);
    });

    bool? Detect(string id, ExtensionManifest m)
    {
        if (m.Component?.Detect is not { } script) return true;
        try
        {
            var code = RunScriptAsSystem(id, PackageDir(id), script, "detect", BaseEnv(id, "detect", null), 60);
            return code is null ? null : code == 0;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Ext:{id}] Detección: {ex.Message}");
            return null;
        }
    }

    // ── Actualizaciones ─────────────────────────────────────────────────

    public async Task<UpdateCheck> CheckUpdateAsync(string id, CancellationToken ct = default)
    {
        var e = Find(id) ?? throw Err("ext.notFound", id);
        var s = e.Source;
        if (e.CatalogUrl is not null)
            await LoadCatalogAsync(e.CatalogUrl, refresh: true, ct);
        var req = new PreviewRequest(s.Slug, s.Path, e.CatalogUrl is null ? s.RequestedRef : null, e.CatalogUrl);

        // Si ya se sabe que es el mismo commit, no hace falta bajar nada.
        if (e.CatalogUrl is null)
        {
            var resolved = await _github.ResolveAsync(s.Repository, ct);
            if (resolved.Commit == s.Commit)
                return new UpdateCheck(e.Id, s.VersionLabel, resolved.VersionLabel, true, null);
        }
        var preview = await PreviewAsync(req, ct);
        if (preview.Sha256 == s.Sha256)
        {
            if (_pending.TryRemove(preview.Token, out var p)) TryDelete(p.StagingDir);
            return new UpdateCheck(e.Id, s.VersionLabel, preview.Version, true, null);
        }
        return new UpdateCheck(e.Id, s.VersionLabel, preview.Version, false, preview);
    }

    // ── Catálogos ───────────────────────────────────────────────────────

    public IReadOnlyList<string> CatalogUrls()
    {
        lock (_lock) return _state.Catalogs.Select(c => c.Url).ToList();
    }

    public async Task<IReadOnlyList<CatalogView>> GetCatalogsAsync(bool refresh, CancellationToken ct = default)
    {
        var tasks = CatalogUrls().Select(u => LoadCatalogAsync(u, refresh, ct)).ToList();
        var results = await Task.WhenAll(tasks);
        return results.Select(r => r.View).ToList();
    }

    public async Task<CatalogView> AddCatalogAsync(string url, CancellationToken ct = default)
    {
        var norm = NormalizeCatalogUrl(url) ?? throw Err("ext.invalidSource", url);
        lock (_lock)
            if (_state.Catalogs.Any(c => SameUrl(c.Url, norm))) throw Err("ext.catalogExists");
        var (view, catalog) = await LoadCatalogAsync(norm, refresh: true, ct);
        if (catalog is null) throw new ExtensionException(view.Error ?? norm);
        lock (_lock)
        {
            _state.Catalogs.Add(new CatalogRef(norm, DateTime.UtcNow));
            SaveState();
        }
        return view;
    }

    public void RemoveCatalog(string url)
    {
        lock (_lock)
        {
            if (_state.Catalogs.RemoveAll(c => SameUrl(c.Url, url)) == 0) throw Err("ext.catalogNotFound");
            SaveState();
        }
        _catalogCache.TryRemove(url, out _);
    }

    async Task<(CatalogView View, ExtensionCatalog? Catalog)> LoadCatalogAsync(string url, bool refresh, CancellationToken ct)
    {
        if (!refresh && _catalogCache.TryGetValue(url, out var c) && c.At > DateTime.UtcNow.AddMinutes(-10))
            return (c.View, c.Catalog);

        bool official = SameUrl(url, OfficialCatalog);
        try
        {
            if (!GitHubRef.TryParse(url, out var repo)) throw Err("ext.invalidSource", url);
            var path = repo.Path is null ? ExtensionFormat.CatalogFile : $"{repo.Path}/{ExtensionFormat.CatalogFile}";
            var json = await _github.GetFileAsync(repo with { Path = null }, path, ct);
            var catalog = ExtensionFormat.ParseCatalog(json);

            List<InstalledExtension> installed = Snapshot();
            var entries = catalog.Extensions.Select(x =>
            {
                GitHubRef.TryParse(x.Repo, out var r);
                var inst = installed.FirstOrDefault(i =>
                    i.Source.Slug.Equals(r.Slug, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.Source.Path, x.Path?.Trim('/'), StringComparison.OrdinalIgnoreCase));
                return new CatalogEntryView(r.Slug, r.Url + (x.Path is null ? "" : $"/tree/{x.Ref ?? "HEAD"}/{x.Path.Trim('/')}"),
                    x.Path?.Trim('/'), x.Ref, x.Sha256 is not null, x.Id, x.Name ?? x.Id ?? r.Repo, x.Description, x.Notice,
                    x.Tags ?? [], inst?.Id);
            }).ToList();
            var view = new CatalogView(url, string.IsNullOrWhiteSpace(catalog.Name) ? repo.Slug : catalog.Name,
                                       catalog.Description, official, null, entries, DateTime.UtcNow);
            _catalogCache[url] = (DateTime.UtcNow, view, catalog);
            return (view, catalog);
        }
        catch (Exception ex) when (ex is GitHubException or ExtensionFormatException or ExtensionException or HttpRequestException or TaskCanceledException)
        {
            var view = new CatalogView(url, url, null, official, ex.Message, [], null);
            return (view, null);
        }
    }

    static string? NormalizeCatalogUrl(string url)
    {
        if (!GitHubRef.TryParse(url, out var r)) return null;
        return r.Ref is null && r.Path is null ? r.Url : $"{r.Url}/tree/{r.Ref ?? "HEAD"}{(r.Path is null ? "" : "/" + r.Path)}";
    }

    static bool SameUrl(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // ── Ganchos ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lanza los scripts de las extensiones activas para este evento. Los de arranque van en
    /// segundo plano; los de parada se esperan (como mucho 15 s en total) para que corran
    /// antes de cerrar la sesión de Windows.
    /// </summary>
    public void FireHooks(ExtensionEvent ev, HookContext ctx)
    {
        var targets = Snapshot()
            .Where(e => e.Enabled && _manifests.TryGetValue(e.Id, out var m) && m.Hooks?.For(ev) is not null)
            .Select(e => (Ext: e, Manifest: _manifests[e.Id]))
            .ToList();
        if (targets.Count == 0) return;

        var tasks = targets.Select(t => Task.Run(() => RunHook(t.Ext, t.Manifest, ev, ctx))).ToArray();
        if (ev is ExtensionEvent.SessionStop or ExtensionEvent.SeatStop)
        {
            try { Task.WaitAll(tasks, TimeSpan.FromSeconds(15)); } catch { }
        }
    }

    public static void Fire(ExtensionEvent ev, HookContext ctx)
    {
        try { Instance?.FireHooks(ev, ctx); }
        catch (Exception ex) { Logger.Warning($"[Ext] Ganchos {ev}: {ex.Message}"); }
    }

    void RunHook(InstalledExtension e, ExtensionManifest m, ExtensionEvent ev, HookContext ctx)
    {
        var h = m.Hooks!;
        var script = h.For(ev)!;
        var env = BaseEnv(e.Id, ev.ToString(), e.Source.VersionLabel);
        env["OSMS_EVENT"]              = ev.ToString();
        env["OSMS_SESSION_ID"]         = ctx.SessionId?.ToString() ?? "";
        env["OSMS_SESSION_NAME"]       = ctx.SessionName ?? "";
        env["OSMS_WINDOWS_SESSION_ID"] = ctx.WindowsSessionId.ToString();
        env["OSMS_WINDOWS_USER"]       = ctx.WindowsUser ?? "";
        env["OSMS_LOCAL"]              = ctx.Local ? "1" : "0";
        env["OSMS_SEAT_ID"]            = ctx.SeatId?.ToString() ?? "";
        env["OSMS_SEAT_NAME"]          = ctx.SeatName ?? "";
        try
        {
            int? code;
            if (h.RunAs == "user")
            {
                if (ctx.WindowsSessionId == 0)
                {
                    WriteLog(e.Id, "hooks", $"{ev}: no Windows session, skipped");
                    return;
                }
                var pis = new ProcessInSession();
                pis.LaunchInSession(ctx.WindowsSessionId, PowerShellExe,
                    PowerShellArgs(Path.Combine(PackageDir(e.Id), script)), env,
                    waitForExit: true, waitTimeoutMs: (uint)h.TimeoutSec * 1000);
                code = pis.ExitCode is { } c ? (int)c : null;
            }
            else
            {
                code = RunScriptAsSystem(e.Id, PackageDir(e.Id), script, "hooks", env, h.TimeoutSec);
            }
            WriteLog(e.Id, "hooks", $"{ev} ({ctx.SessionName ?? ctx.SeatName}): exit {code?.ToString() ?? "timeout"}");
        }
        catch (Exception ex)
        {
            WriteLog(e.Id, "hooks", $"{ev}: {ex.Message}");
            Logger.Warning($"[Ext:{e.Id}] Gancho {ev}: {ex.Message}");
        }
    }

    // ── Apps de Sunshine ────────────────────────────────────────────────

    /// <summary>Entradas de apps.json de las extensiones activas, con <c>{ext}</c> ya sustituido.</summary>
    public IReadOnlyList<(string ExtensionId, JsonObject App)> GetSunshineApps()
    {
        var result = new List<(string, JsonObject)>();
        foreach (var e in Snapshot().Where(e => e.Enabled))
        {
            if (!_manifests.TryGetValue(e.Id, out var m) || m.Apps is null) continue;
            var dir = PackageDir(e.Id);
            string R(string s) => s.Replace("{ext}", dir, StringComparison.OrdinalIgnoreCase);
            foreach (var a in m.Apps)
            {
                var o = new JsonObject { ["name"] = a.Name.Trim() };
                if (a.Cmd is not null) o["cmd"] = R(a.Cmd);
                if (a.WorkingDir is not null) o["working-dir"] = R(a.WorkingDir);
                if (a.ImagePath is not null) o["image-path"] = R(a.ImagePath);
                if (a.AutoDetach is { } ad) o["auto-detach"] = ad;
                if (a.Detached is { Count: > 0 }) o["detached"] = new JsonArray(a.Detached.Select(d => (JsonNode)R(d)).ToArray());
                if (a.PrepCmd is { Count: > 0 })
                    o["prep-cmd"] = new JsonArray(a.PrepCmd.Select(p => (JsonNode)new JsonObject
                    {
                        ["do"] = R(p.Do ?? ""), ["undo"] = R(p.Undo ?? ""), ["elevated"] = p.Elevated,
                    }).ToArray());
                result.Add((e.Id, o));
            }
        }
        return result;
    }

    // ── Pestañas del panel ──────────────────────────────────────────────

    string PanelToken(string id) =>
        _panelTokens.GetOrAdd(id, _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));

    /// <summary>
    /// Fichero de la pestaña de una extensión. La ruta lleva un token aleatorio que solo da el
    /// panel autenticado: el iframe está aislado (origen opaco) y no manda la cookie de sesión.
    /// </summary>
    public string? ResolvePanelFile(string id, string token, string path)
    {
        var e = Find(id);
        if (e is null || !e.Enabled || !_manifests.TryGetValue(e.Id, out var m) || m.Panel is null) return null;
        if (!_panelTokens.TryGetValue(e.Id, out var expected) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(token ?? "")))
            return null;
        if (!ExtensionFormat.IsSafeRelativePath(path)) return null;
        var root = Path.GetFullPath(PackageDir(e.Id)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, path));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    // ── Ejecutar scripts ────────────────────────────────────────────────

    static string PowerShellExe => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    static string PowerShellArgs(string script) =>
        $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"";

    Dictionary<string, string> BaseEnv(string id, string action, string? previousVersion) => new()
    {
        ["OSMS_EXTENSION_ID"]  = id,
        ["OSMS_EXTENSION_DIR"] = PackageDir(id),
        ["OSMS_ACTION"]        = action,
        ["OSMS_PREVIOUS_VERSION"] = previousVersion ?? "",
        ["OSMS_VERSION"]       = _osmsVersion.ToString(3),
    };

    /// <summary>Script como SYSTEM con la salida en logs\&lt;log&gt;.log. Devuelve el código, o null si se agotó el tiempo.</summary>
    int? RunScriptAsSystem(string id, string pkgDir, string script, string log, Dictionary<string, string> env, int timeoutSec)
    {
        var full = Path.Combine(pkgDir, script);
        var psi = new ProcessStartInfo(PowerShellExe, PowerShellArgs(full))
        {
            WorkingDirectory       = pkgDir,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        foreach (var (k, v) in env) psi.Environment[k] = v;

        WriteLog(id, log, $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} {script}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe");
        p.OutputDataReceived += (_, a) => { if (a.Data is not null) { WriteLog(id, log, a.Data); OpLogIfActive(id, a.Data); } };
        p.ErrorDataReceived  += (_, a) => { if (a.Data is not null) { WriteLog(id, log, "! " + a.Data); OpLogIfActive(id, "! " + a.Data); } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutSec * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            WriteLog(id, log, $"--- timeout ({timeoutSec} s)");
            return null;
        }
        p.WaitForExit();
        WriteLog(id, log, $"--- exit {p.ExitCode}");
        return p.ExitCode;
    }

    readonly object _logLock = new();

    void WriteLog(string id, string log, string line)
    {
        try
        {
            lock (_logLock)
            {
                var dir = LogDir(id);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, log + ".log");
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
                    File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch { }
    }

    // ── Operación en curso ──────────────────────────────────────────────

    void BeginOperation(string kind, string id, string name)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw Err("ext.busy");
        lock (_opLog) _opLog.Clear();
        _op = new ExtensionOperation(kind, id, name, "running", null, false, DateTime.UtcNow, null);
        Logger.Log($"[Ext] {kind} {id}");
    }

    void RunOperation(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            var msg = ex is ExtensionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            OpLog("! " + msg);
            Logger.Error($"[Ext] {_op?.Kind} {_op?.Id}: {msg}");
            EndOperation("error", msg, false);
        }
    }

    void EndOperation(string state, string? message, bool reboot)
    {
        if (_op is { } op) _op = op with { State = state, Message = message, RebootRequired = reboot, EndedAt = DateTime.UtcNow };
        Volatile.Write(ref _busy, 0);
    }

    void OpLog(string line)
    {
        lock (_opLog)
        {
            _opLog.Add(line);
            if (_opLog.Count > 400) _opLog.RemoveAt(0);
        }
    }

    void OpLogIfActive(string id, string line)
    {
        if (IsBusy && _op?.Id == id) OpLog(line);
    }

    // ── Estado en disco ─────────────────────────────────────────────────

    List<InstalledExtension> Snapshot()
    {
        lock (_lock) return [.. _state.Installed];
    }

    InstalledExtension? Find(string id)
    {
        lock (_lock) return _state.Installed.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    void LoadManifest(string id)
    {
        try
        {
            var file = Path.Combine(PackageDir(id), ExtensionFormat.ManifestFile);
            _manifests[id] = ExtensionFormat.ParseManifest(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Ext:{id}] No se pudo leer el manifiesto: {ex.Message}");
        }
    }

    ExtensionState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<ExtensionState>(File.ReadAllText(StatePath), ExtensionFormat.Json) ?? new();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Ext] No se pudo leer {StatePath}: {ex.Message}");
            try { File.Copy(StatePath, $"{StatePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
        }
        return new();
    }

    void SaveState()
    {
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, ExtensionFormat.Json));
        File.Move(tmp, StatePath, overwrite: true);
    }

    /// <summary>
    /// Lo que hay aquí se ejecuta como SYSTEM: nadie más que SYSTEM y Administradores puede
    /// escribir (si no, cualquier usuario del PC, como los de los puestos, escalaría
    /// privilegios cambiando un script). Los usuarios leen y ejecutan: los ganchos con
    /// runAs "user" y las apps de Moonlight corren con el usuario de la sesión.
    /// </summary>
    static void SecureRoot(string root)
    {
        if (!System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem) return;
        try
        {
            const System.Security.AccessControl.InheritanceFlags all =
                System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            var ds = new System.Security.AccessControl.DirectorySecurity();
            ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            void Allow(System.Security.Principal.WellKnownSidType sid, System.Security.AccessControl.FileSystemRights rights) =>
                ds.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(sid, null), rights, all,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            Allow(System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl);
            Allow(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl);
            Allow(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute);
            new DirectoryInfo(root).SetAccessControl(ds);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Ext] No se pudieron proteger los permisos de {root}: {ex.Message}");
        }
    }

    static void TryDelete(string dir)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch { Thread.Sleep(300); }
        }
    }

    static ExtensionException Err(string key, params object[] args) => new(RequestCulture.T(key, args));
}
