using Microsoft.Win32;
using OpenStreamMS.Services;
using OpenStreamMS.Services.Localization;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// La opción de Windows "una sesión por usuario" (<c>fSingleSessionPerUser</c>, la casilla
/// "Single session per user" de RDPConf y la directiva "Restringir a los usuarios de Servicios
/// de Escritorio remoto a una única sesión"). Windows la trae a 1: al iniciar sesión, un usuario
/// que ya tiene sesión vuelve a la suya en vez de abrir otra.
/// <para>
/// Un puesto o un stream con el mismo usuario que la consola necesita una segunda sesión para
/// ese usuario, y con 1 termsrv le devolvería la de consola. Para ese caso se pone a 0 solo
/// mientras se conecta (<see cref="Suspend"/>) y se deja como estaba en cuanto la sesión existe:
/// el valor solo cuenta al iniciar o reconectar una sesión. Dejarlo a 0 de forma permanente,
/// como hacían las versiones anteriores, rompe el cambio rápido de usuario en la consola
/// (A → B → A abre otra sesión de A y deja la anterior colgada).
/// </para>
/// <para>
/// El valor original se anota en el registro antes de cambiarlo, así que una caída en medio
/// se repara al arrancar el servicio o al desinstalarlo (<see cref="RestoreSaved"/>).
/// </para>
/// </summary>
internal static class SingleSessionPerUser
{
    private const string TsKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string Name = "fSingleSessionPerUser";
    private const string SavedKey = @"SOFTWARE\OpenStreamMS\SingleSessionPerUser";
    /// <summary>Valor que había antes de ponerlo a 0 (-1 = no existía).</summary>
    private const string SavedOriginal = "Original";
    /// <summary>Ya se deshizo el 0 permanente que dejaban las versiones anteriores.</summary>
    private const string SavedMigrated = "LegacyZeroUndone";
    private const int Missing = -1;

    private static readonly object _lock = new();
    private static int _holders;

    /// <summary>
    /// Permite una segunda sesión del mismo usuario hasta que se libere el resultado. No hace
    /// nada si ya está a 0 (lo eligió el usuario) o si una directiva la fuerza (la directiva
    /// gana al valor normal; solo se avisa).
    /// </summary>
    public static IDisposable Suspend()
    {
        lock (_lock)
        {
            if (ForcedByPolicy() is int forced && forced != 0)
            {
                Logger.Warning(BackendText.Get("singleSession.policyForcedWarning", Name, forced, PolicyKey));
                return Released.Instance;
            }

            if (_holders == 0)
            {
                int current = ReadCurrent();
                if (current == 0) return Released.Instance;
                if (!Save(current)) return Released.Instance;   // sin anotarlo no se toca
                if (!Write(0)) { DeleteSaved(); return Released.Instance; }
                Logger.Log(BackendText.Get("singleSession.suspended", Name, Describe(current)));
            }
            _holders++;
            return new Holder();
        }
    }

    /// <summary>
    /// Al arrancar el servicio o al desinstalarlo: deja el valor como estaba si una caída lo
    /// dejó a 0. La primera vez, además, deshace el 0 que dejaban las versiones anteriores.
    /// </summary>
    public static void RestoreSaved()
    {
        lock (_lock)
        {
            if (_holders > 0) return;
            try
            {
                using var saved = Registry.LocalMachine.CreateSubKey(SavedKey, writable: true);
                if (saved.GetValue(SavedOriginal) is int original)
                {
                    Restore(original);
                }
                else if (saved.GetValue(SavedMigrated) is null && ReadCurrent() == 0)
                {
                    // Hasta esta versión se ponía a 0 en cada conexión y no se devolvía nunca.
                    // No se sabe qué había antes: se vuelve al valor de Windows. Quien lo quiera
                    // a 0 puede volver a ponerlo; desde ahora se respeta.
                    if (Write(1))
                        Logger.Log(BackendText.Get("singleSession.legacyRestored", Name));
                }
                saved.SetValue(SavedMigrated, 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.Warning(BackendText.Get("singleSession.restoreFailed", Name, ex.Message));
            }
        }
    }

    private static void Release()
    {
        lock (_lock)
        {
            if (_holders == 0 || --_holders > 0) return;
            try
            {
                using var saved = Registry.LocalMachine.OpenSubKey(SavedKey);
                if (saved?.GetValue(SavedOriginal) is int original) Restore(original);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[RDP] No se pudo restaurar {Name}: {ex.Message}");
            }
        }
    }

    private static void Restore(int original)
    {
        bool ok = original == Missing ? DeleteValue() : Write(original);
        if (!ok) return;   // se reintenta en el siguiente arranque
        DeleteSaved();
        Logger.Log(BackendText.Get("singleSession.restored", Name, Describe(original)));
    }

    private static int ReadCurrent()
    {
        using var key = Registry.LocalMachine.OpenSubKey(TsKey);
        return key?.GetValue(Name) is int value ? value : Missing;
    }

    private static int? ForcedByPolicy()
    {
        try
        {
            using var policy = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return policy?.GetValue(Name) as int?;
        }
        catch { return null; }
    }

    private static bool Save(int original)
    {
        try
        {
            using var saved = Registry.LocalMachine.CreateSubKey(SavedKey, writable: true);
            saved.SetValue(SavedOriginal, original, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(BackendText.Get("singleSession.saveFailed", Name, ex.Message));
            return false;
        }
    }

    private static void DeleteSaved()
    {
        try
        {
            using var saved = Registry.LocalMachine.OpenSubKey(SavedKey, writable: true);
            saved?.DeleteValue(SavedOriginal, throwOnMissingValue: false);
        }
        catch (Exception ex) { Logger.Warning(BackendText.Get("singleSession.deleteSavedFailed", Name, ex.Message)); }
    }

    private static bool Write(int value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(TsKey, writable: true);
            key.SetValue(Name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(BackendText.Get("singleSession.writeFailed", Name, value, ex.Message));
            return false;
        }
    }

    private static bool DeleteValue()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TsKey, writable: true);
            key?.DeleteValue(Name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(BackendText.Get("singleSession.deleteValueFailed", Name, ex.Message));
            return false;
        }
    }

    private static string Describe(int value) => value == Missing ? BackendText.Get("singleSession.noValue") : value.ToString();

    private sealed class Holder : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Release();
        }
    }

    private sealed class Released : IDisposable
    {
        public static readonly Released Instance = new();
        public void Dispose() { }
    }
}
