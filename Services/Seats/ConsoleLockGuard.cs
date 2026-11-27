using OpenStreamMS.Services.Localization;
using Microsoft.Win32;
using OpenStreamMS.Core.Helpers;
using System.Security.Principal;
using System.Text.Json;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Los monitores de los puestos son pantallas del escritorio de la consola: si la consola se
/// bloquea (Win+L, Ctrl+Alt+Supr → Bloquear), Windows pasa al escritorio seguro, pone la
/// pantalla de bloqueo en todos sus monitores y los presentadores dejan de verse. Las sesiones
/// de los puestos siguen vivas, pero parecen bloqueadas, y nada puede pintarse encima.
///
/// Mientras haya un puesto encendido se quita "Bloquear" al usuario de la consola con la
/// directiva DisableLockWorkstation. Es una directiva de USUARIO: solo cuenta en su HKCU (la
/// copia en HKLM que pone RdpSessionCreator.PreventSessionLock no la respeta Windows).
/// El valor anterior se anota en disco y se restaura al apagar el último puesto, o en el
/// siguiente arranque del servicio si este se cayó con un puesto encendido.
/// </summary>
internal static class ConsoleLockGuard
{
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableLockWorkstation";
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "console-lock.json");
    private static readonly object _lock = new();

    private sealed record Saved(string Sid, string User, int? Previous);

    /// <summary>Impide bloquear la consola (idempotente).</summary>
    public static void Engage()
    {
        lock (_lock)
        {
            if (File.Exists(JournalPath)) return;   // ya aplicado
            try
            {
                if (!ConsoleSession.TryGetUser(out _, out var domain, out var user))
                {
                    Logger.Warning(BackendText.Get("consoleLock.noConsoleUser"));
                    return;
                }
                string account = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
                string sid = new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;
                using var key = Registry.Users.CreateSubKey($@"{sid}\{PolicyKey}", writable: true)
                    ?? throw new InvalidOperationException(BackendText.Get("consoleLock.userHiveNotLoaded"));
                int? previous = key.GetValue(ValueName) is int v ? v : null;

                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
                File.WriteAllText(JournalPath, JsonSerializer.Serialize(new Saved(sid, account, previous)));
                key.SetValue(ValueName, 1, RegistryValueKind.DWord);
                Logger.Log(BackendText.Get("consoleLock.engaged", account));
            }
            catch (Exception ex)
            {
                Logger.Warning(BackendText.Get("consoleLock.engageFailed", ex.Message));
            }
        }
    }

    /// <summary>Devuelve el bloqueo de la consola a como estaba (sin efecto si no se aplicó).</summary>
    public static void Release()
    {
        lock (_lock)
        {
            if (!File.Exists(JournalPath)) return;
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(JournalPath))
                    ?? throw new InvalidDataException(BackendText.Get("consoleLock.journalEmpty"));
                using var key = Registry.Users.OpenSubKey($@"{saved.Sid}\{PolicyKey}", writable: true);
                if (key is null && Registry.Users.OpenSubKey(saved.Sid) is null)
                {
                    // El usuario no tiene sesión (hive descargado): se reintenta en el próximo arranque.
                    Logger.Warning(BackendText.Get("consoleLock.userNotLoggedOn", saved.User));
                    return;
                }
                if (saved.Previous is { } prev) key?.SetValue(ValueName, prev, RegistryValueKind.DWord);
                else key?.DeleteValue(ValueName, throwOnMissingValue: false);
                File.Delete(JournalPath);
                Logger.Log(BackendText.Get("consoleLock.released", saved.User));
            }
            catch (Exception ex)
            {
                Logger.Warning(BackendText.Get("consoleLock.releaseFailed", ex.Message));
            }
        }
    }
}
