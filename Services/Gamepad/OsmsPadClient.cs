using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenStreamMS.Services.Localization;

namespace OpenStreamMS.Services.Gamepad;

/// <summary>Estado de un mando con la forma de XInput (botones XINPUT_GAMEPAD, ejes con signo).</summary>
internal record struct PadState(ushort Buttons, byte LeftTrigger, byte RightTrigger,
                                short ThumbLX, short ThumbLY, short ThumbRX, short ThumbRY);

/// <summary>
/// Cliente del driver OsmsPad (Native/OsmsPad, contrato en OsmsPadIoctl.h): crea mandos Xbox
/// virtuales en la sesión de Windows que se le pida, les pasa el estado y devuelve la vibración
/// que piden los juegos. Cerrar el cliente borra sus mandos.
/// </summary>
internal sealed class OsmsPadClient : IDisposable
{
    private static readonly Guid InterfaceGuid = new("6b1a3c52-8e0f-4c7b-9d2a-5f3e1b7c9a40");
    private const uint ProtocolVersion = 1;
    private const uint ProfileXboxSeries = 1;

    private const uint DeviceType = 0x8A5B;
    private const uint FileReadData = 1, FileWriteData = 2;
    private static uint Ctl(uint function, uint access) => (DeviceType << 16) | (access << 14) | (function << 2);
    private static readonly uint IoctlCreate   = Ctl(0x801, FileReadData | FileWriteData);
    private static readonly uint IoctlInput    = Ctl(0x802, FileWriteData);
    private static readonly uint IoctlDestroy  = Ctl(0x803, FileWriteData);
    private static readonly uint IoctlFeedback = Ctl(0x804, FileReadData);

    private readonly SafeFileHandle _handle;
    private readonly Thread _feedbackThread;
    private volatile bool _disposed;
    private readonly ManualResetEvent _feedbackDone = new(false);

    /// <summary>Vibración pedida por un juego: mando, motor grande y pequeño (0..65535).</summary>
    public event Action<uint, ushort, ushort>? Feedback;

    private OsmsPadClient(SafeFileHandle handle)
    {
        _handle = handle;
        _feedbackThread = new Thread(FeedbackLoop) { IsBackground = true, Name = "OsmsPad feedback" };
        _feedbackThread.Start();
    }

    /// <summary>Abre el driver; null si no está instalado.</summary>
    public static OsmsPadClient? Open()
    {
        if (InterfacePath() is not { } path) return null;
        var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero);
        if (h.IsInvalid)
        {
            Logger.Warning(BackendText.Get("osmsPad.openFailed", Marshal.GetLastWin32Error()));
            h.Dispose();
            return null;
        }
        // Sin BindHandle: las peticiones overlapped se esperan con su propio evento.
        return new OsmsPadClient(h);
    }

    /// <summary>Crea un mando en esa sesión de Windows. Devuelve su número y su ID de instancia.</summary>
    public (uint Pad, string InstanceId) Create(uint windowsSession)
    {
        var inBuf = new byte[12];
        BitConverter.GetBytes(ProtocolVersion).CopyTo(inBuf, 0);
        BitConverter.GetBytes(ProfileXboxSeries).CopyTo(inBuf, 4);
        BitConverter.GetBytes(windowsSession).CopyTo(inBuf, 8);
        var outBuf = new byte[104];
        Ioctl(IoctlCreate, inBuf, outBuf);
        uint pad = BitConverter.ToUInt32(outBuf, 0);
        string id = System.Text.Encoding.Unicode.GetString(outBuf, 8, 96).Split('\0')[0];
        return (pad, id);
    }

    public void Input(uint pad, in PadState s)
    {
        var b = new byte[16];
        BitConverter.GetBytes(pad).CopyTo(b, 0);
        BitConverter.GetBytes(s.Buttons).CopyTo(b, 4);
        b[6] = s.LeftTrigger;
        b[7] = s.RightTrigger;
        BitConverter.GetBytes(s.ThumbLX).CopyTo(b, 8);
        BitConverter.GetBytes(s.ThumbLY).CopyTo(b, 10);
        BitConverter.GetBytes(s.ThumbRX).CopyTo(b, 12);
        BitConverter.GetBytes(s.ThumbRY).CopyTo(b, 14);
        Ioctl(IoctlInput, b, null);
    }

    public void Destroy(uint pad)
    {
        try { Ioctl(IoctlDestroy, BitConverter.GetBytes(pad), null); }
        catch (Win32Exception) { /* ya no existía */ }
    }

    /// <summary>Una petición FEEDBACK pendiente cada vez: el driver la completa con la siguiente vibración.</summary>
    private void FeedbackLoop()
    {
        var buf = new byte[12];
        using var ev = new ManualResetEvent(false);
        try
        {
            while (!_disposed)
            {
                var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
                ev.Reset();
                unsafe
                {
                    fixed (byte* p = buf)
                    {
                        bool ok = DeviceIoControl(_handle, IoctlFeedback, null, 0, p, buf.Length, out int got, &ov);
                        if (!ok)
                        {
                            int err = Marshal.GetLastWin32Error();
                            if (err != 997 /*IO_PENDING*/) { if (!_disposed) Thread.Sleep(500); continue; }
                            ev.WaitOne();
                            if (!GetOverlappedResult(_handle, &ov, out got, false)) { if (!_disposed) Thread.Sleep(100); continue; }
                        }
                        if (got >= 12)
                            Feedback?.Invoke(BitConverter.ToUInt32(buf, 0), BitConverter.ToUInt16(buf, 4), BitConverter.ToUInt16(buf, 6));
                    }
                }
            }
        }
        catch (ObjectDisposedException) { }
        finally { _feedbackDone.Set(); }
    }

    private unsafe void Ioctl(uint code, byte[] input, byte[]? output)
    {
        using var ev = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* i = input)
        fixed (byte* o = output)
        {
            int got;
            if (!DeviceIoControl(_handle, code, i, input.Length, o, output?.Length ?? 0, out got, &ov))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 997) throw new Win32Exception(err);
                if (!GetOverlappedResult(_handle, &ov, out got, true)) throw new Win32Exception();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelIoEx(_handle, IntPtr.Zero);
        _feedbackDone.WaitOne(2000);
        _handle.Dispose();   // cerrar el handle borra todos sus mandos
        _feedbackDone.Dispose();
    }

    private static string? InterfacePath()
    {
        var g = InterfaceGuid;
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref g, null, 0) != 0 || len < 2) return null;
        var chars = new char[len];
        if (CM_Get_Device_Interface_ListW(ref g, null, chars, len, 0) != 0) return null;
        string path = new string(chars).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return path.Length > 0 ? path : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle h, uint code, byte* inBuf, int inSize, byte* outBuf, int outSize, out int returned, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov, out int transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid g, string? device, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid g, string? device, char[] buffer, int len, int flags);
}
