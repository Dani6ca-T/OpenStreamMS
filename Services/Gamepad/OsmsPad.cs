using System.Runtime.InteropServices;

namespace OpenStreamMS.Services.Gamepad;

/// <summary>
/// Driver de mandos propio de OpenStreamMS (Native/OsmsPad): mandos Xbox Series sobre VHF, que
/// Windows pone en XInput con su filtro xinputhid. Con él instalado, Sunshine crea los mandos
/// con <c>gamepad_driver = osmspad</c> en vez de con ViGEmBus, y cada mando lleva en su ID de
/// instancia la sesión de Windows que lo creó (<see cref="GamepadSessionIsolation"/>).
/// </summary>
internal static class OsmsPad
{
    /// <summary>Interfaz del driver (GUID_DEVINTERFACE_OSMSPAD de OsmsPadIoctl.h).</summary>
    private static readonly Guid InterfaceGuid = new("6b1a3c52-8e0f-4c7b-9d2a-5f3e1b7c9a40");

    private const int CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    private const int CR_SUCCESS = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid interfaceClass,
                                                                 string? deviceId, int flags);

    /// <summary>True si el driver está instalado y arrancado (su interfaz está presente).</summary>
    public static bool IsInstalled()
    {
        try
        {
            var guid = InterfaceGuid;
            return CM_Get_Device_Interface_List_SizeW(out int length, ref guid, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) == CR_SUCCESS
                   && length > 1;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}
