using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Localization;

namespace OpenStreamMS.Services.Updates;

/// <param name="State">idle, downloading, verifying, installing o failed.</param>
/// <param name="Version">Versión que se está instalando.</param>
/// <param name="Progress">Descarga, 0-100.</param>
/// <param name="Error">Código del fallo (download, size, hash, launch…) si State es failed.</param>
/// <param name="LogPath">Log del instalador (Inno Setup), para mirar si algo falla.</param>
public sealed record UpdateInstallStatus(string State, string? Version, int Progress, string? Error, string? LogPath);

/// <summary>
/// Actualizar desde el panel: descarga el instalador de la release, comprueba tamaño y SHA-256
/// con lo que publica GitHub y lo ejecuta en silencio. El instalador para el servicio (y con él
/// este proceso), instala y lo vuelve a arrancar; el panel espera a que vuelva con la versión nueva.
/// Solo la versión que UpdateChecker da como más nueva: nada de versiones a elegir ni de bajar.
/// </summary>
public sealed class UpdateInstaller
{
    public const long MaxInstallerBytes = 512L * 1024 * 1024;

    static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    readonly UpdateChecker _checker;
    readonly HttpClient _http;
    readonly string _dir;
    readonly Func<ProcessStartInfo, bool> _launch;
    readonly object _lock = new();
    UpdateInstallStatus _status = new("idle", null, 0, null, null);

    /// <param name="dir">Carpeta de descargas (por defecto %ProgramData%\OpenStreamMS\updates).</param>
    /// <param name="launch">Arranca el instalador (los tests lo sustituyen).</param>
    public UpdateInstaller(UpdateChecker checker, HttpMessageHandler? handler = null, string? dir = null,
                           Func<ProcessStartInfo, bool>? launch = null)
    {
        _checker = checker;
        _dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "updates");
        _launch = launch ?? (psi => Process.Start(psi) is not null);
        // Las redirecciones se siguen a mano para comprobar a qué servidor llevan.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenStreamMS", checker.Current?.ToString() ?? "0"));
        _status = LastAttempt() ?? _status;
    }

    /// <summary>
    /// Al arrancar el servicio: si hace poco se lanzó el instalador de una versión más nueva y
    /// seguimos en la de antes, la instalación no acabó (el instalador la deshace y vuelve a
    /// arrancar el servicio). El panel, que espera a la versión nueva, lo enseña con el log.
    /// </summary>
    UpdateInstallStatus? LastAttempt()
    {
        try
        {
            var log = new DirectoryInfo(_dir).EnumerateFiles("setup-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (log is null || DateTime.UtcNow - log.LastWriteTimeUtc > TimeSpan.FromHours(1)) return null;
            var version = Path.GetFileNameWithoutExtension(log.Name)["setup-".Length..];
            if (AppVersion.Parse(version) is not { } v || _checker.Current is not { } cur || !(v > cur)) return null;
            return new UpdateInstallStatus("failed", version, 0, "aborted", log.FullName);
        }
        catch { return null; }   // sin carpeta (nunca se actualizó) o sin permiso
    }

    public UpdateInstallStatus Status { get { lock (_lock) return _status; } }

    void Set(UpdateInstallStatus s) { lock (_lock) _status = s; }

    /// <summary>
    /// Empieza a instalar <paramref name="version"/> en segundo plano. Devuelve null si ha
    /// empezado, o el motivo para no hacerlo: busy, devbuild, notavailable o noinstaller.
    /// </summary>
    public string? Start(string? version)
    {
        var st = _checker.Status();
        var latest = st.Latest;
        if (st.DevBuild) return "devbuild";
        if (!st.UpdateAvailable || latest is null || version is null || latest.Version != version.Trim()) return "notavailable";
        if (latest.Installer is not { } asset) return "noinstaller";
        lock (_lock)
        {
            if (_status.State is "downloading" or "verifying" or "installing") return "busy";
            _status = new UpdateInstallStatus("downloading", latest.Version, 0, null, null);
        }
        _ = Task.Run(() => RunAsync(latest.Version, asset));
        return null;
    }

    async Task RunAsync(string version, UpdateAsset asset)
    {
        string? log = null;
        try
        {
            Directory.CreateDirectory(_dir);
            SecureFile.RestrictToAdmins(_dir);   // lo que se ejecuta como SYSTEM: que nadie más lo pueda cambiar
            foreach (var old in Directory.EnumerateFiles(_dir, "OpenStreamMS-Setup-*"))
                try { File.Delete(old); } catch { /* en uso o ya borrado: da igual */ }

            var file = Path.Combine(_dir, asset.Name);
            var part = file + ".part";
            Logger.Info(BackendText.Get("updates.downloading", asset.Name, asset.Size / (1024 * 1024)));
            var sha = await DownloadAsync(asset, part, version);

            Set(new UpdateInstallStatus("verifying", version, 100, null, null));
            if (!string.Equals(sha, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateInstallException("hash", BackendText.Get("updates.hashMismatch", sha, asset.Sha256));
            File.Move(part, file, overwrite: true);
            SecureFile.RestrictToAdmins(file);

            log = Path.Combine(_dir, $"setup-{version}.log");
            var psi = new ProcessStartInfo(file)
            {
                // Sin preguntas ni reinicio; solo la tarea de arrancar el servicio (sin icono en el
                // escritorio). El instalador para este servicio y lo vuelve a arrancar al acabar.
                Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=\"startservice\" /LOG=\"{log}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _dir,
            };
            Set(new UpdateInstallStatus("installing", version, 100, null, log));
            Logger.Warning(BackendText.Get("updates.installing", version, log));
            if (!_launch(psi)) throw new UpdateInstallException("launch", BackendText.Get("updates.installerNotStarted"));
        }
        catch (Exception ex)
        {
            var code = ex is UpdateInstallException u ? u.Code : ex is HttpRequestException or TaskCanceledException or IOException ? "download" : "launch";
            Logger.Error(BackendText.Get("updates.installFailed", version, code, ex.Message));
            Set(new UpdateInstallStatus("failed", version, 0, code, log));
            try { File.Delete(Path.Combine(_dir, asset.Name + ".part")); } catch { }
        }
    }

    async Task<string> DownloadAsync(UpdateAsset asset, string path, string version)
    {
        var uri = new Uri(asset.Url);
        HttpResponseMessage? res = null;
        for (int hop = 0; ; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
                throw new UpdateInstallException("download", BackendText.Get("updates.serverNotAllowed", uri.Host));
            res = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } next && hop < 5)
            {
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                res.Dispose();
                continue;
            }
            break;
        }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw new UpdateInstallException("download", BackendText.Get("updates.httpStatus", (int)res.StatusCode));
            if (res.Content.Headers.ContentLength is { } len && len != asset.Size)
                throw new UpdateInstallException("size", BackendText.Get("updates.sizeMismatch", len, asset.Size));

            using var sha = SHA256.Create();
            await using var src = await res.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buf = new byte[1 << 16];
            long total = 0;
            int lastPct = -1, n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                total += n;
                if (total > asset.Size) throw new UpdateInstallException("size", BackendText.Get("updates.sizeExceeded"));
                sha.TransformBlock(buf, 0, n, null, 0);
                await dst.WriteAsync(buf.AsMemory(0, n));
                int pct = (int)(total * 100 / asset.Size);
                if (pct != lastPct) { lastPct = pct; Set(new UpdateInstallStatus("downloading", version, pct, null, null)); }
            }
            if (total != asset.Size) throw new UpdateInstallException("size", BackendText.Get("updates.sizeMismatch", total, asset.Size));
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexStringLower(sha.Hash!);
        }
    }
}

public sealed class UpdateInstallException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
