using System.Diagnostics;
using OpenStreamMS.Services;
using OpenStreamMS.Services.Localization;

namespace OpenStreamMS.Services.Session;

/// <summary>
/// Si el equipo admite varias sesiones de Windows a la vez. OpenStreamMS no instala ni configura
/// nada para conseguirlo: solo mira si el equipo ya lo permite.
/// </summary>
public record RdpWrapperStatus(
    /// <summary>true si el equipo admite varias sesiones a la vez (verde en el panel).</summary>
    bool    Allowed,
    /// <summary>
    /// Por qué: <c>"server"</c> (Windows Server con Host de sesión de Escritorio remoto),
    /// <c>"multiSessionEdition"</c> (Windows Enterprise multisesión), <c>"thirdParty"</c> (el
    /// servicio de Escritorio remoto usa otro componente instalado por el usuario),
    /// <c>"observed"</c> (ahora mismo hay varias sesiones activas) o null.
    /// </summary>
    string? Source,
    /// <summary>
    /// El componente del usuario está configurado pero el servicio de Escritorio remoto aún no lo
    /// ha cargado (se carga con la primera conexión o al reiniciar Windows).
    /// </summary>
    bool    Pending,
    /// <summary>true en Windows Server.</summary>
    bool    IsServer,
    /// <summary>Windows Server con el rol Host de sesión de Escritorio remoto. Null fuera de Server.</summary>
    bool?   SessionHostRole,
    /// <summary>Modo de licencias RDS: <c>"perUser"</c>, <c>"perDevice"</c>, <c>"notConfigured"</c> o null.</summary>
    string? LicensingMode,
    /// <summary>Sesiones de usuario activas ahora mismo.</summary>
    int     ActiveSessions,
    /// <summary>Edición de Windows (EditionID del registro).</summary>
    string? Edition,
    /// <summary>true si RDP-Tcp pasa por el proveedor de RemoteIdd (captura remoteidd disponible).</summary>
    bool    RemoteIdd,
    /// <summary>Versión de <c>termsrv.dll</c> del sistema (file version).</summary>
    string? TermsrvVersion);

/// <summary>
/// Multi-RDP: varias sesiones de Windows a la vez. Oficialmente es Windows Server con el rol Host
/// de sesión de Escritorio remoto y CAL de RDS. En Windows cliente (Home/Pro) depende de lo que
/// el usuario haya instalado por su cuenta y bajo su responsabilidad; OpenStreamMS no instala,
/// cambia ni quita nada de eso.
/// </summary>
public sealed class RdpWrapperManager
{
    private const string StockServiceDll = @"%SystemRoot%\System32\termsrv.dll";

    public RdpWrapperStatus GetStatus()
    {
        var isServer = IsServer();
        var edition  = ReadEdition();
        var rdsh     = isServer ? IsSessionHostInstalled() : (bool?)null;
        var thirdParty = !isServer && !IsStockServiceDll(ReadServiceDll());
        var loaded   = thirdParty ? IsServiceDllLoaded() : null;
        var active   = SafeCountActiveSessions();

        string? source =
            rdsh == true                                                             ? "server" :
            string.Equals(edition, "ServerRdsh", StringComparison.OrdinalIgnoreCase) ? "multiSessionEdition" :
            thirdParty                                                               ? "thirdParty" :
            active >= 2                                                              ? "observed" :
            null;
        // Componente del usuario configurado pero sin cargar: TermService parado (en Windows
        // cliente arranca con la primera conexión) o con la DLL anterior hasta reiniciar. Si ya
        // hay varias sesiones activas, funciona aunque no se haya podido mirar.
        var pending = source == "thirdParty" && loaded != true && active < 2;

        return new RdpWrapperStatus(
            Allowed:         source is not null && !pending,
            Source:          source,
            Pending:         pending,
            IsServer:        isServer,
            SessionHostRole: rdsh,
            LicensingMode:   isServer ? ReadLicensingMode() : null,
            ActiveSessions:  active,
            Edition:         edition,
            RemoteIdd:       IsRemoteIddActive(),
            TermsrvVersion:  ReadTermsrvVersion());
    }

    // ── Arranque: RemoteIdd ──────────────────────────────────────────────────

    /// <summary>Proveedor de protocolo de RemoteIdd (Native/RemoteIdd/Provider/install.ps1).</summary>
    public const string RemoteIddProviderClsid = "{8B0F3C51-2D7A-4F6E-A1C9-5E4D2B7F0A36}";
    private const string RdpTcpKey     = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private const string RdpCoreTsClsid = "{5828227c-20cf-4408-b73f-73ab70b8849f}";

    /// <summary>true si RDP-Tcp pasa por el proveedor de RemoteIdd.</summary>
    public static bool IsRemoteIddActive()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RdpTcpKey);
            return string.Equals(key?.GetValue("LoadableProtocol_Object") as string, RemoteIddProviderClsid,
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Al arrancar, antes de levantar puestos: deja en el log si el equipo admite varias sesiones
    /// y, si RemoteIdd está puesto en RDP-Tcp pero RDP-Tcp no escucha (Windows cliente sin nada
    /// que admita su proveedor, o un Windows Update que lo rompió), quita RemoteIdd de RDP-Tcp
    /// para no dejar el equipo sin Escritorio remoto. Los puestos con captura remoteidd arrancan
    /// entonces con la captura de siempre. Para volver a RemoteIdd: install.ps1.
    /// </summary>
    public void EnsureMultiRdp(CancellationToken ct)
    {
        try { EnsureMultiRdpCore(ct); }
        finally { MultiRdpCheckedSource.TrySetResult(); }
    }

    private static readonly TaskCompletionSource MultiRdpCheckedSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Se completa cuando <see cref="EnsureMultiRdp"/> ha terminado. Los puestos esperan a esto
    /// antes de autoarrancar: si no, arrancan con captura remoteidd y un instante después se quita
    /// RemoteIdd, y Sunshine se queda sin pantalla.
    /// </summary>
    public static Task MultiRdpChecked => MultiRdpCheckedSource.Task;

    /// <summary>
    /// RemoteIdd instalado después de que arrancara TermService: install.ps1 no reinicia TermService
    /// (se quedaba colgado al pararse), así que el proveedor entra al reiniciar el equipo. Mientras
    /// tanto no está roto, está pendiente: ni se usa ni se deshace.
    /// </summary>
    public static bool IsRemoteIddPendingRestart()
    {
        try
        {
            var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                     "OpenStreamMS", "remoteidd-install.json");
            if (!IsRemoteIddActive() || !File.Exists(state)) return false;
            var pid = GetServicePid("TermService");
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            return File.GetLastWriteTime(state) > p.StartTime;
        }
        catch { return false; }
    }

    /// <summary>RemoteIdd puesto en RDP-Tcp y ya cargado (no pendiente de reiniciar el equipo).</summary>
    public static bool IsRemoteIddUsable() => IsRemoteIddActive() && !IsRemoteIddPendingRestart();

    private void EnsureMultiRdpCore(CancellationToken ct)
    {
        var st = GetStatus();
        if (st.Allowed)
            Logger.Log($"[MultiRdp] Varias sesiones a la vez: sí ({st.Source}). Estado: {st}");
        else if (st.Pending)
            Logger.Warning($"[MultiRdp] Varias sesiones a la vez: pendiente de que se cargue el servicio de Escritorio remoto. Estado: {st}");
        else
            Logger.Warning($"[MultiRdp] Este equipo no admite varias sesiones a la vez: solo funciona una sesión RDP. Estado: {st}");

        if (!IsRemoteIddActive()) return;
        if (IsRemoteIddPendingRestart())
        {
            Logger.Error("[MultiRdp] RemoteIdd instalado y pendiente de reiniciar el equipo: hasta entonces los " +
                         "puestos usan la captura de siempre.");
            return;
        }
        // En el arranque del PC TermService puede tardar en abrir el listener.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string? problem;
        while ((problem = RemoteIddProblem()) is not null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            Thread.Sleep(2000);
        if (problem is null)
        {
            Logger.Log("[MultiRdp] RDP-Tcp escuchando con el proveedor de RemoteIdd: RemoteIdd disponible.");
            return;
        }
        Logger.Error($"[MultiRdp] RemoteIdd sin funcionar ({problem}). Se quita RemoteIdd de RDP-Tcp.");
        LeaveRemoteIdd();
        ApplyTermServiceChange();
        Logger.Log($"[MultiRdp] RemoteIdd fuera de RDP-Tcp. Estado: {GetStatus()}");
    }

    /// <summary>Null si el equipo admite varias sesiones y RDP-Tcp escucha; si no, el motivo.</summary>
    private string? RemoteIddProblem()
    {
        var st = GetStatus();
        if (st.Pending)           return BackendText.Get("rdpwrap.notLoaded");
        if (!st.Allowed)          return BackendText.Get("rdpwrap.singleSession");
        if (!IsRdpTcpListening()) return BackendText.Get("rdpwrap.rdpTcpNotListening");
        return null;
    }

    private static bool IsRdpTcpListening()
    {
        int port = 3389;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RdpTcpKey);
            if (key?.GetValue("PortNumber") is int p && p > 0) port = p;
        }
        catch { }
        return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(e => e.Port == port);
    }

    /// <summary>
    /// Quita el proveedor de RemoteIdd de RDP-Tcp. El driver y la DLL del proveedor se quedan
    /// instalados; uninstall.ps1 los quita.
    /// </summary>
    private static void LeaveRemoteIdd()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RdpTcpKey, writable: true);
        if (key is null) return;
        var inner = key.GetValue("OsmsInnerProtocol_Object") as string;
        if (string.IsNullOrWhiteSpace(inner)) inner = RdpCoreTsClsid;
        key.SetValue("LoadableProtocol_Object", inner, Microsoft.Win32.RegistryValueKind.String);
        Logger.Log($"[MultiRdp] RDP-Tcp devuelto a {inner} (RemoteIdd fuera).");
    }

    /// <summary>
    /// Reinicia TermService para que cargue el cambio, salvo con sesiones remotas o de puesto
    /// abiertas: pararlo así puede dejarlo a medias (StopPending) y colgar el apagado. Entonces el
    /// cambio queda en el registro y se aplica en el próximo arranque del equipo.
    /// </summary>
    private static void ApplyTermServiceChange()
    {
        var live = OpenStreamMS.Core.Helpers.RdpSessionCreator.DescribeNonConsoleUserSessions();
        if (live.Count > 0)
        {
            Logger.Log("[MultiRdp] " + BackendText.Get("rdpwrap.openSessionsPreventRestart", string.Join(", ", live)));
            return;
        }
        using var sc = new System.ServiceProcess.ServiceController("TermService");
        if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
        {
            sc.Stop(); // ServiceController para antes los dependientes (UmRdpService).
            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }
        sc.Start();
        sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
        Logger.Log("[MultiRdp] " + BackendText.Get("rdpwrap.termServiceRestarted"));
    }

    // ── Detección ────────────────────────────────────────────────────────────

    /// <summary>ProductType del registro: WinNT es Windows cliente; ServerNT y LanmanNT, Server.</summary>
    private static bool IsServer()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            var type = key?.GetValue("ProductType") as string;
            return type is not null && !type.Equals("WinNT", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string? ReadEdition()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("EditionID") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Rol Host de sesión de Escritorio remoto: con él, TSAppCompat = 1. Sin el rol, Windows Server
    /// solo admite dos sesiones de administración.
    /// </summary>
    private static bool IsSessionHostInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
            return key?.GetValue("TSAppCompat") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Modo de licencias RDS: primero la directiva de grupo, luego la configuración local.</summary>
    private static string? ReadLicensingMode()
    {
        int? mode = null;
        try
        {
            using (var gpo = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"))
                if (gpo?.GetValue("LicensingMode") is int g) mode = g;
            if (mode is null)
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\RCM\Licensing Core"))
                    if (key?.GetValue("LicensingMode") is int l) mode = l;
        }
        catch { }
        return mode switch { 2 => "perDevice", 4 => "perUser", null => null, _ => "notConfigured" };
    }

    private static string? ReadServiceDll()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TermService\Parameters");
            return key?.GetValue("ServiceDll", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch { return null; }
    }

    private static bool IsStockServiceDll(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var full  = Environment.ExpandEnvironmentVariables(value);
        var stock = Environment.ExpandEnvironmentVariables(StockServiceDll);
        return string.Equals(Path.GetFullPath(full), Path.GetFullPath(stock), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mira si la DLL de ServiceDll está en los módulos del svchost de TermService: que el
    /// registro apunte a ella no basta, hasta reiniciar el servicio sigue cargada la anterior.
    /// Null si TermService está parado o no se puede leer el proceso.
    /// </summary>
    private static bool? IsServiceDllLoaded()
    {
        try
        {
            var value = ReadServiceDll();
            if (value is null) return null;
            var dll = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
            var pid = GetServicePid("TermService");
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            foreach (ProcessModule m in p.Modules)
                if (string.Equals(m.FileName, dll, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        catch { return null; }
    }

    private static int SafeCountActiveSessions()
    {
        try { return OpenStreamMS.Core.Helpers.RdpSessionCreator.CountActiveUserSessions(); }
        catch { return 0; }
    }

    private static uint GetServicePid(string name)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return 0;
        try
        {
            var svc = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return 0;
            try
            {
                var buf = new byte[36]; // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(svc, 0 /* SC_STATUS_PROCESS_INFO */, buf, buf.Length, out _)) return 0;
                const uint SERVICE_RUNNING = 4;
                return BitConverter.ToUInt32(buf, 4) == SERVICE_RUNNING ? BitConverter.ToUInt32(buf, 28) : 0;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private const uint SC_MANAGER_CONNECT   = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr svc, int infoLevel, byte[] buffer, int size, out int needed);
    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr h);

    private static string? ReadTermsrvVersion()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "termsrv.dll");
            if (!File.Exists(path)) return null;
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch { return null; }
    }
}
