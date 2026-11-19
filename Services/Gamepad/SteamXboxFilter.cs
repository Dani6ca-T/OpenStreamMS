using Microsoft.Win32;
using OpenStreamMS.Core.Helpers;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenStreamMS.Services.Gamepad;

/// <summary>
/// <c>steamxbox.sys</c> (el "Xbox Extended Feature Support Driver" que instala Steam) es filtro
/// inferior de las clases de mandos (XnaComposite, HIDClass, XboxComposite): va debajo de HidHide
/// en la pila de cada mando Xbox y publica sus datos por <c>\Device\nvagipmanControl</c>, que
/// puede abrir cualquier usuario y no mira la sesión. El Steam de cualquier sesión (la consola
/// incluida) leía así el mando de un puesto o de un stream aunque HidHide y la ACL se lo negaran:
/// en su log sale como <c>path = nvagipman:…</c>.
/// <para>
/// Un filtro de clase no se puede quitar a un solo dispositivo, pero Windows monta la pila al
/// arrancar cada nodo. Mientras haya mandos que aislar en otras sesiones se quita
/// <see cref="FilterName"/> de los <c>LowerFilters</c> de esas clases: los mandos virtuales que se
/// enchufen entonces nacen sin él. Los mandos ya conectados (los de la consola) lo conservan;
/// uno que se conecte mientras tanto se queda sin las funciones extra de Steam (palancas de un
/// Elite…) hasta reconectarlo. Cuando ya no queda nada que aislar se deja como estaba. Lo de
/// antes se guarda en el registro, así que sobrevive a una caída del servicio.
/// </para>
/// </summary>
internal static class SteamXboxFilter
{
    public const string FilterName = "steamxbox";

    private static readonly string[] Classes =
    [
        "{d61ca365-5af4-4486-998b-9db4734c6ca3}",   // XnaComposite (Xbox 360 / XUSB, también los X360 de ViGEm)
        "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}",   // HIDClass
        "{05f5cfe2-4733-4950-a6bb-07aad01a3a84}",   // XboxComposite (Xbox One / GIP)
    ];
    private const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\";
    private const string SavedKey = @"SOFTWARE\OpenStreamMS\SteamXboxFilter";

    private static readonly object _lock = new();
    private static readonly HashSet<string> _owners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alguien necesita los mandos nuevos sin el filtro (un puesto, una sesión de stream).</summary>
    public static void Engage(string owner)
    {
        lock (_lock)
        {
            _owners.Add(owner);
            Remove();
        }
    }

    /// <summary>Ese dueño ya no lo necesita; con el último se restaura.</summary>
    public static void Release(string owner)
    {
        lock (_lock)
        {
            if (!_owners.Remove(owner) || _owners.Count > 0) return;
            Restore();
        }
    }

    /// <summary>
    /// Justo antes de enchufar un mando virtual: Steam puede haber vuelto a poner el filtro
    /// (al actualizarse o al abrir sus ajustes de mando) y el mando nacería con él.
    /// </summary>
    public static void EnsureRemoved()
    {
        lock (_lock)
            if (_owners.Count > 0) Remove();
    }

    /// <summary>Al arrancar el servicio o al desinstalarlo: deja como estaba lo que quedara de una caída.</summary>
    public static void RestoreSaved()
    {
        lock (_lock)
            if (_owners.Count == 0) Restore();
    }

    private static void Remove()
    {
        try
        {
            using var saved = Registry.LocalMachine.CreateSubKey(SavedKey, writable: true);
            var removed = new List<string>();
            foreach (var cls in Classes)
            {
                using var key = Registry.LocalMachine.OpenSubKey(ClassRoot + cls, writable: true);
                if (key?.GetValue("LowerFilters") is not string[] current ||
                    !current.Contains(FilterName, StringComparer.OrdinalIgnoreCase)) continue;
                // Si ya estaba guardado (el servicio se cayó con el filtro quitado, o Steam lo volvió
                // a poner) se conserva lo de la primera vez.
                if (saved.GetValue(cls) is null) saved.SetValue(cls, current, RegistryValueKind.MultiString);
                var rest = current.Where(f => !f.Equals(FilterName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (rest.Length == 0) key.DeleteValue("LowerFilters", throwOnMissingValue: false);
                else key.SetValue("LowerFilters", rest, RegistryValueKind.MultiString);
                removed.Add(cls);
            }
            if (removed.Count > 0)
                Logger.Log($"[Gamepad] Filtro {FilterName} de Steam quitado de {removed.Count} clase(s) de mandos mientras haya mandos aislados " +
                           "en otras sesiones: los mandos que se conecten ahora no se publican a todas las sesiones por \\Device\\nvagipmanControl.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo quitar el filtro {FilterName} de Steam: {ex.Message}. " +
                           "El Steam de otras sesiones (la consola incluida) puede seguir leyendo los mandos aislados.");
        }
    }

    private static void Restore()
    {
        try
        {
            using var saved = Registry.LocalMachine.OpenSubKey(SavedKey, writable: true);
            if (saved is null) return;
            int restored = 0;
            foreach (var cls in saved.GetValueNames())
            {
                if (saved.GetValue(cls) is string[] previous)
                {
                    using var key = Registry.LocalMachine.OpenSubKey(ClassRoot + cls, writable: true);
                    if (key is not null)
                    {
                        // Lo de antes, más lo que haya añadido otro instalador mientras tanto.
                        var current = key.GetValue("LowerFilters") as string[] ?? [];
                        var merged = previous.Concat(current.Where(f => !previous.Contains(f, StringComparer.OrdinalIgnoreCase))).ToArray();
                        key.SetValue("LowerFilters", merged, RegistryValueKind.MultiString);
                        restored++;
                    }
                }
                saved.DeleteValue(cls, throwOnMissingValue: false);
            }
            if (restored > 0)
                Logger.Log($"[Gamepad] Filtro {FilterName} de Steam restaurado en {restored} clase(s) de mandos (no quedan mandos aislados en otras sesiones).");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo restaurar el filtro {FilterName} de Steam: {ex.Message}. Se reintentará en el próximo arranque del servicio.");
        }
    }

    /// <summary>La pila del nodo lleva el filtro (DEVPKEY_Device_Stack contiene <c>\Driver\steamxbox</c>).</summary>
    public static bool InStack(string instanceId)
    {
        var key = new DEVPROPKEY { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 14 };   // DEVPKEY_Device_Stack
        if (CM_Locate_DevNodeW(out int inst, instanceId, 0) != 0) return false;
        int size = 0;
        CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);
        if (size <= 2) return false;
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) != 0) return false;
        return Encoding.Unicode.GetString(buf, 0, size).Split('\0')
                       .Any(d => d.EndsWith(@"\" + FilterName, StringComparison.OrdinalIgnoreCase));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(int devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref int size, int flags);
}
