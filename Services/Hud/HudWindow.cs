using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OpenStreamMS.Services.Hud;

/// <summary>
/// HUD de una sesión (<c>OpenStreamMS.exe --hud</c>): ventana encima de todo, oculta hasta que se
/// pide con Ctrl+Alt+H o con Guía + Back en un mando XInput de la sesión. Corre dentro de la sesión
/// de Windows, así que se ve igual en el monitor de un puesto y en Moonlight. Muestra
/// <c>/hud.html</c> del servicio en un WebView2 que solo se crea al abrirlo y se libera al rato de
/// cerrarlo (cada sesión tiene el suyo: no gasta memoria mientras nadie lo usa).
/// </summary>
internal sealed class HudWindow : Form
{
    private const int HotkeyId = 0x4F53;                 // "OS"
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private const ushort XINPUT_GUIDE = 0x0400, XINPUT_BACK = 0x0020;
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromMinutes(2);

    private readonly Uri _page;
    private readonly System.Threading.Timer _padTimer;
    private readonly System.Windows.Forms.Timer _release;
    private readonly bool[] _comboDown = new bool[4];
    private WebView2? _web;
    private IntPtr _previousForeground;
    private bool _hotkey;

    internal HudWindow(string baseUrl)
    {
        _page = new Uri(new Uri(baseUrl), "/hud.html");

        Text            = "OpenStreamMS HUD";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        BackColor       = Color.FromArgb(16, 17, 22);
        KeyPreview      = true;

        _release = new System.Windows.Forms.Timer { Interval = (int)ReleaseAfter.TotalMilliseconds };
        _release.Tick += (_, _) => { _release.Stop(); if (!Visible) ReleaseWeb(); };

        // XInput se lee en otro hilo (no depende del foco): Guía + Back abre o cierra.
        _padTimer = new System.Threading.Timer(_ => PollPads(), null, 500, 60);
    }

    /// <summary>La ventana nace oculta: solo existe para el atajo y para dueña del WebView2.</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkey = RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.H);
        if (!_hotkey) Logger.Warning($"[HUD] No se pudo registrar Ctrl+Alt+H (error {Marshal.GetLastWin32Error()}); queda Guía + Back.");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId) Toggle();
        base.WndProc(ref m);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { HideHud(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private void Toggle()
    {
        if (Visible) HideHud();
        else ShowHud();
    }

    private void ShowHud()
    {
        _release.Stop();
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        int w = Math.Clamp(screen.Width * 2 / 3, Math.Min(640, screen.Width), 1400);
        int h = Math.Clamp(screen.Height * 3 / 4, Math.Min(420, screen.Height), 900);
        Bounds = new Rectangle(screen.X + (screen.Width - w) / 2, screen.Y + (screen.Height - h) / 2, w, h);

        _previousForeground = GetForegroundWindow();
        EnsureWeb();
        Show();
        TopMost = true;
        ForceForeground(Handle);
        _web?.Focus();
        _web?.CoreWebView2?.PostWebMessageAsString("open");
    }

    private void HideHud()
    {
        if (!Visible) return;
        _web?.CoreWebView2?.PostWebMessageAsString("close");
        Hide();
        // El juego recupera el foco donde estaba.
        if (_previousForeground != IntPtr.Zero) ForceForeground(_previousForeground);
        _previousForeground = IntPtr.Zero;
        _release.Start();
    }

    private void EnsureWeb()
    {
        if (_web is not null) return;
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);
        _ = InitWebAsync(_web);
    }

    private async Task InitWebAsync(WebView2 web)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenStreamMS", "WebView2-HUD");
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[HUD] WebView2 no disponible: {ex.Message}");
            return;
        }
        if (web != _web) return;   // se liberó mientras arrancaba

        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled               = false;
        core.Settings.IsStatusBarEnabled               = false;
        core.Settings.AreDefaultContextMenusEnabled    = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled             = false;
        core.WebMessageReceived += (_, e) =>
        {
            if (e.TryGetWebMessageAsString() == "close") HideHud();
        };
        // Solo la página del HUD: nada de navegar a otro sitio desde aquí.
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ||
                !string.Equals(u.Authority, _page.Authority, StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.Navigate(_page.AbsoluteUri);
    }

    private void ReleaseWeb()
    {
        if (_web is null) return;
        var web = _web;
        _web = null;
        Controls.Remove(web);
        web.Dispose();
    }

    // ── Mandos ───────────────────────────────────────────────────────────────

    private void PollPads()
    {
        bool toggle = false;
        for (uint i = 0; i < 4; i++)
        {
            bool combo = XInputGetStateEx(i, out var st) == 0 &&
                         (st.Gamepad.wButtons & (XINPUT_GUIDE | XINPUT_BACK)) == (XINPUT_GUIDE | XINPUT_BACK);
            if (combo && !_comboDown[i]) toggle = true;
            _comboDown[i] = combo;
        }
        if (toggle && IsHandleCreated) BeginInvoke(Toggle);
    }

    // ── Foco ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Windows solo deja pasar al frente a quien tiene la última entrada. Con Ctrl+Alt+H la tenemos,
    /// con el mando no: se engancha un momento la cola de entrada de la ventana de delante.
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _padTimer.Dispose();
            _release.Dispose();
            if (_hotkey && IsHandleCreated) UnregisterHotKey(Handle, HotkeyId);
            ReleaseWeb();
        }
        base.Dispose(disposing);
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    /// <summary>XInputGetState con el botón Guía (ordinal 100 de xinput1_4.dll; la normal lo oculta).</summary>
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
