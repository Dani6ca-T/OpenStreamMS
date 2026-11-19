using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using OpenStreamMS.Core.Helpers;
using System.ServiceProcess;

namespace OpenStreamMS.Services.Gamepad;

/// <summary>
/// Mandos Xbox 360 virtuales de ViGEmBus (driver <c>xusb22</c>, como un mando con cable) para
/// entregar en otra sesión un mando físico capturado por <see cref="PadCapture"/>.
/// <para>
/// No se usa OsmsPad: su perfil Xbox Series va por <c>xinputhid</c>, que solo atiende las
/// peticiones XInput de la sesión de consola (desde la sesión de un puesto contesta
/// ERROR_DEVICE_NOT_CONNECTED a todo, aunque el dispositivo se abra). <c>xusb22</c> sí responde
/// en cualquier sesión, y el aislamiento lo pone HidHide (cada mando se reclama para su sesión
/// con <see cref="GamepadSessionIsolation.ClaimNextViGEmPad"/> antes de enchufarlo).
/// </para>
/// </summary>
internal sealed class ViGEmPads : IDisposable
{
    private readonly ViGEmClient _client;
    private readonly Dictionary<uint, IXbox360Controller> _pads = [];
    private readonly object _lock = new();
    private uint _next;

    /// <summary>Vibración (motor grande, motor pequeño, 0-255) y LED que el juego pide a un mando.</summary>
    public event Action<uint, byte, byte, byte>? Feedback;

    private ViGEmPads(ViGEmClient client) => _client = client;

    /// <summary>True si el servicio de ViGEmBus está instalado: sin él no hay dónde entregar el mando.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController("ViGEmBus");
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Conecta con ViGEmBus; null (y aviso en el log) si no se puede.</summary>
    public static ViGEmPads? Open()
    {
        try { return new ViGEmPads(new ViGEmClient()); }
        catch (Exception ex)
        {
            Logger.Warning($"[PadCapture] No se pudo abrir ViGEmBus: {ex.Message}");
            return null;
        }
    }

    /// <summary>Enchufa un mando nuevo, reclamado antes para esa sesión de Windows.</summary>
    public uint Create(uint windowsSession, string owner)
    {
        var pad = _client.CreateXbox360Controller();
        pad.AutoSubmitReport = false;
        uint id;
        lock (_lock) { id = ++_next; _pads[id] = pad; }
        pad.FeedbackReceived += (_, e) => Feedback?.Invoke(id, e.LargeMotor, e.SmallMotor, e.LedNumber);
        GamepadSessionIsolation.ClaimNextViGEmPad(windowsSession, owner);
        try { pad.Connect(); }
        catch
        {
            lock (_lock) _pads.Remove(id);
            throw;
        }
        return id;
    }

    public void Input(uint id, in PadState s)
    {
        IXbox360Controller? pad;
        lock (_lock) _pads.TryGetValue(id, out pad);
        if (pad is null) return;
        pad.SetButtonsFull(s.Buttons);
        pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.LeftTrigger);
        pad.SetSliderValue(Xbox360Slider.RightTrigger, s.RightTrigger);
        pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.ThumbLX);
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, s.ThumbLY);
        pad.SetAxisValue(Xbox360Axis.RightThumbX, s.ThumbRX);
        pad.SetAxisValue(Xbox360Axis.RightThumbY, s.ThumbRY);
        pad.SubmitReport();
    }

    public void Destroy(uint id)
    {
        IXbox360Controller? pad;
        lock (_lock) { if (!_pads.Remove(id, out pad)) return; }
        try { pad.Disconnect(); } catch { /* ya desenchufado */ }
    }

    public void Dispose()
    {
        List<uint> ids;
        lock (_lock) ids = _pads.Keys.ToList();
        foreach (var id in ids) Destroy(id);
        _client.Dispose();
    }
}
