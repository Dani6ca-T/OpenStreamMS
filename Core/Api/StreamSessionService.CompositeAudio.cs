using System.Collections.Concurrent;
using System.Diagnostics;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;

namespace OpenStreamMS.Core.Api;

/// <summary>
/// Sonido de las sesiones compuestas: su Sunshine solo oye la sesión del teclado. Por cada otro
/// miembro hay un <c>--audio-relay send</c> en su sesión y un <c>--audio-relay play</c> en la del
/// teclado (<see cref="Services.Audio.AudioRelay"/>), así Moonlight recibe el sonido de todos.
/// </summary>
public partial class StreamSessionService
{
    private sealed record AudioRelayProcs(uint OwnerSession, uint MemberSession, int PlayPid, int SendPid);

    /// <summary>Compuesta → miembro → sus dos procesos de reenvío.</summary>
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, AudioRelayProcs>> _compositeAudio = new();

    /// <summary>
    /// Deja un reenvío en marcha por cada miembro que no es el del teclado. Si un proceso murió o el
    /// miembro (o el teclado) cambió de sesión de Windows, se rehace. Desde el arranque y desde la vigilancia.
    /// </summary>
    private void EnsureCompositeAudio(StreamSession c)
    {
        var ownerId = c.CompositeInput ?? c.Composite![0].Session;
        if (!_sessions.TryGetValue(ownerId, out var owner) || owner.RdpSessionId == 0) return;
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe)) return;

        var relays = _compositeAudio.GetOrAdd(c.Id, _ => new());
        var members = c.Composite!
            .Select(slot => _sessions.GetValueOrDefault(slot.Session))
            .Where(m => m is not null && m.Id != owner.Id && m.State == SessionState.Running &&
                        m.RdpSessionId != 0 && m.RdpSessionId != owner.RdpSessionId)
            .Select(m => m!)
            .ToList();

        foreach (var gone in relays.Keys.Where(id => members.All(m => m.Id != id)).ToList())
            if (relays.TryRemove(gone, out var old)) KillRelay(old);

        foreach (var m in members)
        {
            if (relays.TryGetValue(m.Id, out var r) && r.OwnerSession == owner.RdpSessionId &&
                r.MemberSession == m.RdpSessionId && Alive(r.PlayPid) && Alive(r.SendPid))
                continue;
            if (r is not null) KillRelay(r);

            var pipe = $"osms-audio-{Guid.NewGuid():N}";
            try
            {
                var launcher = new ProcessInSession();
                int play = launcher.LaunchInSession(owner.RdpSessionId, exe, $"--audio-relay play {pipe}");
                int send = launcher.LaunchInSession(m.RdpSessionId, exe, $"--audio-relay send {pipe}");
                relays[m.Id] = new AudioRelayProcs(owner.RdpSessionId, m.RdpSessionId, play, send);
                Logger.Log(BackendText.Get("compositeAudio.forwarded", c.Name, m.Name, owner.Name), c.Id);
            }
            catch (Exception ex)
            {
                relays.TryRemove(m.Id, out _);
                Logger.Warning(BackendText.Get("compositeAudio.forwardFailed", c.Name, m.Name, ex.Message), c.Id);
            }
        }
    }

    private void StopCompositeAudio(StreamSession c)
    {
        if (!_compositeAudio.TryRemove(c.Id, out var relays)) return;
        foreach (var r in relays.Values) KillRelay(r);
    }

    private static void KillRelay(AudioRelayProcs r)
    {
        foreach (var pid in new[] { r.SendPid, r.PlayPid })
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); }
            catch { /* ya no existe */ }
        }
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
