using System.Collections.Concurrent;
using System.Diagnostics;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;
using OpenStreamMS.Services.Localization;
using OpenStreamMS.Services.Mosaic;

namespace OpenStreamMS.Core.Api;

/// <summary>
/// Teclado y ratón de las sesiones compuestas: su Sunshine corre en la sesión de Windows del miembro
/// del teclado (<see cref="StreamSession.CompositeInput"/>) y mete allí la entrada de Moonlight. Desde
/// el HUD se pueden pasar a otro miembro: se escribe <see cref="MosaicLayout.InputFileName"/> junto a
/// su sunshine.conf (sesión de Windows y su hueco del lienzo) y en ese miembro corre un
/// <c>--input-relay</c> (<see cref="Services.Input.InputRelay"/>) que recibe los eventos de Sunshine.
/// Si ese miembro se reinicia o se para, la entrada vuelve a la sesión del teclado mientras tanto.
/// </summary>
public partial class StreamSessionService
{
    private sealed record InputRelayProc(Guid Member, uint WindowsSession, int Pid);

    /// <summary>Compuesta → miembro elegido desde el HUD (sin entrada: el del teclado).</summary>
    private readonly ConcurrentDictionary<Guid, Guid> _compositeInputTarget = new();
    /// <summary>Compuesta → su proceso de reenvío en el miembro elegido.</summary>
    private readonly ConcurrentDictionary<Guid, InputRelayProc> _compositeInputRelay = new();

    /// <summary>Miembro que recibe ahora el teclado y el ratón de la compuesta, o null si no está en marcha.</summary>
    public Guid? CompositeInputTarget(Guid compositeId)
    {
        if (!_sessions.TryGetValue(compositeId, out var c) || !c.IsComposite || c.State != SessionState.Running) return null;
        var owner = c.CompositeInput ?? c.Composite![0].Session;
        return EffectiveInputTarget(c, owner) is { } t ? t.Id : owner;
    }

    /// <summary>
    /// Pasa el teclado y el ratón de una compuesta en marcha a uno de sus miembros (el del teclado
    /// los devuelve a su sitio). Vuelve enseguida: Sunshine lo nota en menos de un segundo.
    /// </summary>
    public void SetCompositeInputTarget(Guid compositeId, Guid memberId)
    {
        if (!_sessions.TryGetValue(compositeId, out var c) || !c.IsComposite || c.State != SessionState.Running ||
            c.Composite!.All(s => s.Session != memberId) ||
            !_sessions.TryGetValue(memberId, out var m) || m.State != SessionState.Running || m.RdpSessionId == 0)
            throw new InvalidOperationException(RequestCulture.T("error.request"));

        var owner = c.CompositeInput ?? c.Composite![0].Session;
        if (memberId == owner) _compositeInputTarget.TryRemove(c.Id, out _);
        else _compositeInputTarget[c.Id] = memberId;
        Logger.Log($"[{c.Name}] Teclado y ratón a '{m.Name}' (sesión de Windows {m.RdpSessionId}).", c.Id);
        EnsureCompositeInput(c);
    }

    /// <summary>Miembro elegido si está en marcha en otra sesión de Windows que la del teclado; si no, null.</summary>
    private StreamSession? EffectiveInputTarget(StreamSession c, Guid owner)
    {
        if (!_compositeInputTarget.TryGetValue(c.Id, out var id) || id == owner) return null;
        if (!_sessions.TryGetValue(id, out var m) || m.State != SessionState.Running || m.RdpSessionId == 0 ||
            m.CompositeOf != c.Id) return null;
        if (_sessions.TryGetValue(owner, out var o) && o.RdpSessionId == m.RdpSessionId) return null;
        return m;
    }

    /// <summary>
    /// Deja el reenvío y <see cref="MosaicLayout.InputFileName"/> como pide el miembro elegido. Desde el
    /// arranque, la vigilancia y el HUD.
    /// </summary>
    private void EnsureCompositeInput(StreamSession c)
    {
        var ownerId = c.CompositeInput ?? c.Composite![0].Session;
        var target = EffectiveInputTarget(c, ownerId);
        var path = Path.Combine(SunshineConfigDir(c.Id), MosaicLayout.InputFileName);

        if (target is null)
        {
            StopInputRelay(c.Id);
            DeleteInputFile(c, path);
            return;
        }

        // El proceso primero: Sunshine abre su tubería en cuanto ve el archivo.
        if (!_compositeInputRelay.TryGetValue(c.Id, out var relay) || relay.Member != target.Id ||
            relay.WindowsSession != target.RdpSessionId || !Alive(relay.Pid))
        {
            StopInputRelay(c.Id);
            var exe = Environment.ProcessPath;
            if (exe is null || !File.Exists(exe)) return;
            try
            {
                int pid = new ProcessInSession().LaunchInSession(target.RdpSessionId, exe, "--input-relay", asSystem: true);
                _compositeInputRelay[c.Id] = new InputRelayProc(target.Id, target.RdpSessionId, pid);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[{c.Name}] No se pudo pasar el teclado y el ratón a '{target.Name}': {ex.Message}", c.Id);
                DeleteInputFile(c, path);
                return;
            }
        }

        var slot = c.Composite!.First(s => s.Session == target.Id);
        var text = MosaicLayout.RenderInputTarget(target.RdpSessionId, slot.Area, c.RdpWidth, c.RdpHeight);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[{c.Name}] No se pudo escribir {MosaicLayout.InputFileName}: {ex.Message}", c.Id);
        }
    }

    private static void DeleteInputFile(StreamSession c, string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Logger.Warning($"[{c.Name}] No se pudo borrar {MosaicLayout.InputFileName}: {ex.Message}", c.Id); }
    }

    private void StopInputRelay(Guid compositeId)
    {
        if (!_compositeInputRelay.TryRemove(compositeId, out var r)) return;
        try { using var p = Process.GetProcessById(r.Pid); p.Kill(); }
        catch { /* ya no existe */ }
    }

    /// <summary>La compuesta se para: la entrada vuelve a la sesión del teclado y se olvida la elección.</summary>
    private void StopCompositeInput(StreamSession c, bool restarting)
    {
        StopInputRelay(c.Id);
        if (!restarting) _compositeInputTarget.TryRemove(c.Id, out _);
        DeleteInputFile(c, Path.Combine(SunshineConfigDir(c.Id), MosaicLayout.InputFileName));
    }
}
