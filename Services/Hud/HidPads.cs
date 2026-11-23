using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenStreamMS.Services.Hud;

/// <summary>
/// Botones de los mandos de PlayStation (DualShock 4, el que crea Sunshine con ViGEm, y DualSense)
/// de la sesión, leídos por HID: XInput no los ve. HidHide solo deja abrir los de esta sesión de
/// Windows, así que no se cuelan los de otros puestos. Solo informes USB (los del mando virtual y
/// los de los mandos con cable); un mando por Bluetooth usa otro formato y se ignora.
/// </summary>
internal sealed class HidPads : IDisposable
{
    /// <summary>Botones que le importan al HUD.</summary>
    [Flags]
    public enum Buttons
    {
        None = 0, Guide = 1, Back = 2, Start = 4,
        A = 8, B = 16, Up = 32, Down = 64, Left = 128, Right = 256,   // cruz, círculo, cruceta o stick
    }

    private const ushort SonyVid = 0x054C;
    private static readonly HashSet<ushort> Ds4Pids = [0x05C4, 0x09CC, 0x0BA0];
    private static readonly HashSet<ushort> DualSensePids = [0x0CE6, 0x0DF2];

    private readonly ConcurrentDictionary<string, Reader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _rescan;
    private volatile bool _disposed;

    public HidPads() => _rescan = new System.Threading.Timer(_ => Rescan(), null, 0, 3000);

    /// <summary>Botones pulsados de cada mando conectado, por su ruta.</summary>
    public IEnumerable<(string Key, Buttons State)> States => _readers.Select(kv => (kv.Key, kv.Value.State));

    private void Rescan()
    {
        if (_disposed) return;
        try
        {
            foreach (var path in HidDevicePaths())
            {
                if (_readers.ContainsKey(path)) continue;
                var r = Reader.TryOpen(path);
                if (r is null) continue;
                if (_readers.TryAdd(path, r))
                {
                    Logger.Log($"[HUD] Mando {(r.DualSense ? "DualSense" : "DualShock 4")} detectado: Share + Options (1 s) o PS + Share abre el HUD.");
                    r.Start(() => { if (_readers.TryRemove(path, out var gone)) gone.Dispose(); });
                }
                else r.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[HUD] No se pudieron buscar mandos HID: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _rescan.Dispose();
        foreach (var r in _readers.Values) r.Dispose();
        _readers.Clear();
    }

    private sealed class Reader : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly FileStream _stream;
        private readonly int _reportLength;
        public bool DualSense { get; }
        public volatile Buttons State;

        private Reader(SafeFileHandle handle, int reportLength, bool dualSense)
        {
            _handle = handle;
            _reportLength = reportLength;
            DualSense = dualSense;
            _stream = new FileStream(handle, FileAccess.Read, 0, false);
        }

        public static Reader? TryOpen(string path)
        {
            var h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }   // de otra sesión (HidHide) o en uso exclusivo
            try
            {
                var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(h, ref attrs) || attrs.VendorID != SonyVid) { h.Dispose(); return null; }
                bool ds4 = Ds4Pids.Contains(attrs.ProductID), dualSense = DualSensePids.Contains(attrs.ProductID);
                if (!ds4 && !dualSense) { h.Dispose(); return null; }
                if (!HidD_GetPreparsedData(h, out var pp)) { h.Dispose(); return null; }
                try
                {
                    if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS || caps.UsagePage != 0x01 ||
                        caps.Usage is not (0x05 or 0x04) || caps.InputReportByteLength < 11)
                    { h.Dispose(); return null; }
                    return new Reader(h, caps.InputReportByteLength, dualSense);
                }
                finally { HidD_FreePreparsedData(pp); }
            }
            catch { h.Dispose(); return null; }
        }

        public void Start(Action onGone) =>
            new Thread(() =>
            {
                var buf = new byte[_reportLength];
                try
                {
                    while (true)
                    {
                        int n = _stream.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        if (n >= 11 && buf[0] == 0x01) State = Parse(buf);
                    }
                }
                catch { /* desconectado */ }
                onGone();
            }) { IsBackground = true, Name = "HUD HID" }.Start();

        /// <summary>
        /// Informe USB 0x01. Sticks en los bytes 1-2 (X, Y); DS4: cruceta y botones en los bytes 5-7;
        /// DualSense: en los bytes 8-10.
        /// </summary>
        private Buttons Parse(byte[] r)
        {
            int b0 = DualSense ? r[8] : r[5], b1 = DualSense ? r[9] : r[6], b2 = DualSense ? r[10] : r[7];
            var s = Buttons.None;
            switch (b0 & 0x0F)   // cruceta: 0 = arriba y en el sentido del reloj; 8 = suelta
            {
                case 0: s |= Buttons.Up; break;
                case 1: s |= Buttons.Up | Buttons.Right; break;
                case 2: s |= Buttons.Right; break;
                case 3: s |= Buttons.Down | Buttons.Right; break;
                case 4: s |= Buttons.Down; break;
                case 5: s |= Buttons.Down | Buttons.Left; break;
                case 6: s |= Buttons.Left; break;
                case 7: s |= Buttons.Up | Buttons.Left; break;
            }
            if (r[1] < 40) s |= Buttons.Left;  else if (r[1] > 215) s |= Buttons.Right;
            if (r[2] < 40) s |= Buttons.Up;    else if (r[2] > 215) s |= Buttons.Down;
            if ((b0 & 0x20) != 0) s |= Buttons.A;       // cruz
            if ((b0 & 0x40) != 0) s |= Buttons.B;       // círculo
            if ((b1 & 0x10) != 0) s |= Buttons.Back;    // Share / Create
            if ((b1 & 0x20) != 0) s |= Buttons.Start;   // Options
            if ((b2 & 0x01) != 0) s |= Buttons.Guide;   // PS
            return s;
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { }
            _handle.Dispose();
        }
    }

    // ── SetupAPI / HID ───────────────────────────────────────────────────────

    private static IEnumerable<string> HidDevicePaths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) yield break;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);   // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (!string.IsNullOrEmpty(path) &&
                        path.Contains("vid_054c", StringComparison.OrdinalIgnoreCase))
                        yield return path;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                      NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                      NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
}
