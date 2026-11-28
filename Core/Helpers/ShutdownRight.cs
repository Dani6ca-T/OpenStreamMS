using OpenStreamMS.Services;
using OpenStreamMS.Services.Localization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// Un puesto exclusivo no puede apagar ni reiniciar el equipo (solo la consola); una sesión
/// remota o un puesto no exclusivo sí. Windows da "Apagar el sistema" (SeShutdownPrivilege) al
/// grupo Usuarios, y los usuarios aislados (osms-*) son usuarios estándar: aunque NoClose les
/// quite los botones, podrían apagar con <c>shutdown /s</c>. El derecho es por cuenta, no por
/// sesión, así que mientras haya un puesto exclusivo se le quita al grupo Usuarios y se da
/// explícitamente a cada usuario aislado que sí puede (<see cref="SetForUser"/>).
/// Administradores (la consola) lo conserva siempre. Vale desde el siguiente inicio de sesión
/// de cada usuario (los privilegios van en el token). Se anota en disco si se quitó al grupo,
/// para devolverlo cuando ya no haya puestos exclusivos o al desinstalar.
/// </summary>
internal static class ShutdownRight
{
    private const string Privilege = "SeShutdownPrivilege";
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "shutdown-right-removed");

    /// <summary>
    /// Antes de que el usuario aislado inicie sesión: si puede apagar, se le da el derecho a él
    /// (no depende del grupo Usuarios); si no, se le quita y se retira también al grupo.
    /// </summary>
    public static void SetForUser(string username, bool allow)
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(username).Translate(typeof(SecurityIdentifier));
            using (var policy = LsaPolicy.Open())
            {
                if (allow) policy.Add(sid, Privilege);
                else if (policy.Has(sid, Privilege)) policy.Remove(sid, Privilege);
            }
            if (!allow) RestrictToAdministrators();
            Logger.Log(BackendText.Get(allow ? "shutdownRight.userAllowed" : "shutdownRight.userDenied", username));
        }
        catch (Exception ex)
        {
            Logger.Warning(BackendText.Get("shutdownRight.setUserFailed", username, ex.Message));
        }
    }

    /// <summary>Quita "Apagar el sistema" al grupo Usuarios (idempotente).</summary>
    private static void RestrictToAdministrators()
    {
        try
        {
            using var policy = LsaPolicy.Open();
            policy.Add(Admins, Privilege);              // que la consola nunca se quede sin poder apagar
            if (!policy.Has(Users, Privilege)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            File.WriteAllText(JournalPath, DateTime.Now.ToString("O"));
            policy.Remove(Users, Privilege);
            Logger.Log(BackendText.Get("shutdownRight.groupRestricted"));
        }
        catch (Exception ex)
        {
            Logger.Warning(BackendText.Get("shutdownRight.restrictGroupFailed", ex.Message));
        }
    }

    /// <summary>Devuelve el derecho al grupo Usuarios si OpenStreamMS se lo quitó (sin puestos exclusivos o al desinstalar).</summary>
    public static void Restore()
    {
        if (!File.Exists(JournalPath)) return;
        try
        {
            using var policy = LsaPolicy.Open();
            policy.Add(Users, Privilege);
            File.Delete(JournalPath);
            Logger.Log(BackendText.Get("shutdownRight.groupRestored"));
        }
        catch (Exception ex)
        {
            Logger.Warning(BackendText.Get("shutdownRight.restoreGroupFailed", ex.Message));
        }
    }
}
