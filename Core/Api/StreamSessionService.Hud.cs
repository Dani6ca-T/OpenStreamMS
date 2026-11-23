using System.Collections.Concurrent;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;

namespace OpenStreamMS.Core.Api;

/// <summary>
/// HUD de las sesiones (<c>OpenStreamMS.exe --hud</c>, <see cref="Services.Hud.HudWindow"/>): uno en la
/// sesión de Windows de cada sesión en marcha, que es también la de los puestos que la usan. Se
/// vigila desde <see cref="MonitorAll"/>: si falta (sesión nueva, reconexión, se cerró) se lanza.
/// </summary>
public partial class StreamSessionService
{
    private static readonly TimeSpan HudRetry = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<uint, DateTime> _hudLaunched = new();

    /// <summary>Mutex que mantiene abierto el HUD de una sesión de Windows (Global: lo ve el servicio).</summary>
    public static string HudMutexName(uint windowsSession) => $"Global\\OpenStreamMS_Hud_{windowsSession}";

    /// <summary>Sesiones de Windows con HUD: las de las sesiones en marcha (no la consola ni las compuestas, que van en la de un miembro).</summary>
    public IReadOnlyCollection<uint> HudWindowsSessions() =>
        _sessions.Values
            .Where(s => s.State == SessionState.Running && s.RdpSessionId > 0 && !s.Local && !s.IsComposite)
            .Select(s => s.RdpSessionId)
            .ToHashSet();

    /// <summary>true si <paramref name="windowsSession"/> es la de una sesión en marcha (la que puede usar el HUD).</summary>
    public bool IsRunningWindowsSession(uint windowsSession) =>
        windowsSession != 0 && _sessions.Values.Any(s => s.State == SessionState.Running && s.RdpSessionId == windowsSession);

    private void EnsureHuds()
    {
        var wanted = HudWindowsSessions();
        foreach (var ws in _hudLaunched.Keys.Where(ws => !wanted.Contains(ws)).ToList())
            _hudLaunched.TryRemove(ws, out _);   // el HUD se cierra con su sesión de Windows

        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe)) return;
        foreach (var ws in wanted)
        {
            if (HudRunning(ws)) continue;
            if (_hudLaunched.TryGetValue(ws, out var last) && DateTime.UtcNow - last < HudRetry) continue;
            _hudLaunched[ws] = DateTime.UtcNow;
            try
            {
                int pid = new ProcessInSession().LaunchInSession(ws, exe, "--hud", visible: true);
                Logger.Log($"[HUD] Lanzado en la sesión de Windows {ws} (PID {pid}): Ctrl+Alt+H o Guía + Back.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[HUD] No se pudo lanzar en la sesión de Windows {ws}: {ex.Message}");
            }
        }
    }

    private static bool HudRunning(uint windowsSession)
    {
        try
        {
            if (!Mutex.TryOpenExisting(HudMutexName(windowsSession), out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }   // existe, aunque no podamos abrirlo
    }
}
