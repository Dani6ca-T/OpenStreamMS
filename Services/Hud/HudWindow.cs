using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OpenStreamMS.Services.Hud;

/// <summary>
/// HUD de una sesión (<c>OpenStreamMS.exe --hud</c>): ventana a pantalla completa encima de todo,
/// oculta hasta que se pide con Ctrl+Alt+H o con Guía + Back en un mando XInput de la sesión. Corre
/// dentro de la sesión de Windows, así que se ve igual en el monitor de un puesto y en Moonlight.
/// Muestra <c>/hud.html</c> del servicio en un WebView2 que solo se crea al abrirlo y se libera al
/// rato de cerrarlo (cada sesión tiene el suyo: no gasta memoria mientras nadie lo usa).
/// <para>
/// WebView2 no se transparenta sobre otras ventanas en un formulario normal, así que el fondo
/// translúcido es una captura de la pantalla de justo antes de abrirlo (<c>/hud-backdrop.jpg</c>,
/// servida desde aquí), que la página desenfoca y tiñe con el tema.
/// </para>
/// </summary>
internal sealed class HudWindow : Form
{
    private const int HotkeyId = 0x4F53;                 // "OS"
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private const ushort XINPUT_GUIDE = 0x0400, XINPUT_BACK = 0x0020, XINPUT_START = 0x0010;
    private const ushort XINPUT_UP = 0x0001, XINPUT_DOWN = 0x0002, XINPUT_LEFT = 0x0004, XINPUT_RIGHT = 0x0008;
    private const ushort XINPUT_A = 0x1000, XINPUT_B = 0x2000;
    private const short StickDeadZone = 20000;
    /// <summary>Mantener una dirección repite el movimiento: primero tras este tiempo y luego cada <see cref="NavRepeat"/>.</summary>
    private const long NavRepeatDelayMs = 400, NavRepeat = 150;
    /// <summary>Back + Start (Share + Options) hay que mantenerlos: juntos se pulsan sin querer en algunos juegos.</summary>
    private static readonly TimeSpan HoldBackStart = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromMinutes(2);

    private readonly Uri _page;
    private readonly System.Threading.Timer _padTimer;
    private readonly System.Windows.Forms.Timer _release;
    private readonly Dictionary<string, PadCombo> _combos = new();
    private readonly HidPads _hidPads = new();

    /// <summary>Estado de las combinaciones de un mando entre una lectura y la siguiente.</summary>
    private sealed class PadCombo
    {
        public bool GuideBack;
        public long BackStartSince;   // TickCount64 desde que se mantienen; 0 = sueltos
        public bool BackStartFired;
        public HidPads.Buttons Nav;   // botones de navegación pulsados en la lectura anterior
        public long NavSince, NavLastRepeat;
    }
    /// <summary>El HUD está a la vista (se lee desde el hilo de los mandos).</summary>
    private volatile bool _shown;
    private WebView2? _web;
    private CoreWebView2Environment? _env;
    private byte[]? _backdrop;
    private int _backdropVersion;
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
        _padTimer = new System.Threading.Timer(_ => PollPads(), null, 500, 40);
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
        CaptureBackdrop(screen);   // antes de mostrarse: lo que hay debajo
        Bounds = screen;

        _previousForeground = GetForegroundWindow();
        EnsureWeb();
        Show();
        _shown = true;
        TopMost = true;
        ForceForeground(Handle);
        _web?.Focus();
        _web?.CoreWebView2?.PostWebMessageAsJson($"{{\"type\":\"open\",\"backdrop\":{_backdropVersion}}}");
    }

    private void HideHud()
    {
        if (!Visible) return;
        _shown = false;
        _web?.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"close\"}");
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
            var env = _env ??= await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
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
            string? msg = null;
            try { msg = e.TryGetWebMessageAsString(); } catch (ArgumentException) { /* no era texto */ }
            if (msg == "close") HideHud();
        };
        // Solo la página del HUD: nada de navegar a otro sitio desde aquí.
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ||
                !string.Equals(u.Authority, _page.Authority, StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        // Un 401 (la sesión dejó de estar en marcha) no debe abrir un diálogo de usuario y contraseña
        // encima del juego: la página ya enseña el error.
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;

        // El fondo lo sirve el HUD (la captura no sale de la sesión).
        var backdropUrl = new Uri(_page, "/hud-backdrop.jpg").AbsoluteUri;
        core.AddWebResourceRequestedFilter(backdropUrl + "*", CoreWebView2WebResourceContext.Image);
        core.WebResourceRequested += (_, e) =>
        {
            if (_backdrop is not { } jpg || _env is null) return;
            e.Response = _env.CreateWebResourceResponse(new MemoryStream(jpg), 200, "OK",
                "Content-Type: image/jpeg\r\nCache-Control: no-store");
        };
        core.Navigate(_page.AbsoluteUri);
    }

    /// <summary>
    /// Captura la pantalla a media resolución (se va a desenfocar) para el fondo translúcido. Si
    /// falla (escritorio seguro, sin acceso), el HUD sale con el fondo del tema.
    /// </summary>
    private void CaptureBackdrop(Rectangle screen)
    {
        try
        {
            using var full = new Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
            using var half = new Bitmap(full, Math.Max(1, screen.Width / 2), Math.Max(1, screen.Height / 2));
            using var ms = new MemoryStream();
            var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var prms = new System.Drawing.Imaging.EncoderParameters(1);
            prms.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
            half.Save(ms, jpeg, prms);
            _backdrop = ms.ToArray();
        }
        catch (Exception ex)
        {
            _backdrop = null;
            Logger.Warning($"[HUD] Sin captura para el fondo: {ex.Message}");
        }
        _backdropVersion++;
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

    /// <summary>
    /// Guía + Back (PS + Share) abre o cierra al momento; Back + Start (Share + Options) al
    /// mantenerlos <see cref="HoldBackStart"/>, que no depende de que el cliente envíe el botón Guía
    /// (muchos se lo quedan). Mandos XInput (Xbox 360) y de PlayStation por HID.
    /// Con el HUD a la vista, la cruceta (o el stick izquierdo), A y B lo manejan: se leen aquí y no
    /// con la API Gamepad de la página, que en la sesión no ve los mandos (HidHide no deja abrirlos
    /// al WebView2 y solo los atiende con la ventana enfocada y tras una pulsación).
    /// </summary>
    private void PollPads()
    {
        bool toggle = false, closeHud = false;
        var nav = new List<string>();
        long now = Environment.TickCount64;
        var seen = new HashSet<string>();

        void Check(string key, bool guide, bool back, bool start, HidPads.Buttons buttons)
        {
            seen.Add(key);
            if (!_combos.TryGetValue(key, out var c)) _combos[key] = c = new PadCombo();
            Navigate(c, buttons & ~(HidPads.Buttons.Guide | HidPads.Buttons.Back | HidPads.Buttons.Start), now, nav, ref closeHud);
            bool guideBack = guide && back;
            if (guideBack && !c.GuideBack) toggle = true;
            c.GuideBack = guideBack;

            if (back && start && !guide)
            {
                if (c.BackStartSince == 0) c.BackStartSince = now;
                else if (!c.BackStartFired && now - c.BackStartSince >= (long)HoldBackStart.TotalMilliseconds)
                {
                    c.BackStartFired = true;
                    toggle = true;
                }
            }
            else { c.BackStartSince = 0; c.BackStartFired = false; }
        }

        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetStateEx(i, out var st) != 0) continue;
            ushort b = st.Gamepad.wButtons;
            var g = st.Gamepad;
            var buttons = HidPads.Buttons.None;
            if ((b & XINPUT_UP) != 0 || g.sThumbLY > StickDeadZone) buttons |= HidPads.Buttons.Up;
            if ((b & XINPUT_DOWN) != 0 || g.sThumbLY < -StickDeadZone) buttons |= HidPads.Buttons.Down;
            if ((b & XINPUT_LEFT) != 0 || g.sThumbLX < -StickDeadZone) buttons |= HidPads.Buttons.Left;
            if ((b & XINPUT_RIGHT) != 0 || g.sThumbLX > StickDeadZone) buttons |= HidPads.Buttons.Right;
            if ((b & XINPUT_A) != 0) buttons |= HidPads.Buttons.A;
            if ((b & XINPUT_B) != 0) buttons |= HidPads.Buttons.B;
            Check($"x{i}", (b & XINPUT_GUIDE) != 0, (b & XINPUT_BACK) != 0, (b & XINPUT_START) != 0, buttons);
        }
        foreach (var (key, s) in _hidPads.States)
            Check(key, s.HasFlag(HidPads.Buttons.Guide), s.HasFlag(HidPads.Buttons.Back), s.HasFlag(HidPads.Buttons.Start), s);

        foreach (var gone in _combos.Keys.Where(k => !seen.Contains(k)).ToList()) _combos.Remove(gone);
        if (!IsHandleCreated) return;
        if (toggle) BeginInvoke(Toggle);
        else if (closeHud) BeginInvoke(HideHud);
        else if (nav.Count > 0) BeginInvoke(() =>
        {
            if (!Visible || _web?.CoreWebView2 is not { } core) return;
            foreach (var n in nav) core.PostWebMessageAsJson($"{{\"type\":\"pad\",\"button\":\"{n}\"}}");
        });
    }

    /// <summary>
    /// Pulsaciones nuevas de A, B y las direcciones (que se repiten al mantenerlas). Se sigue el
    /// estado también con el HUD oculto: lo que ya estaba pulsado al abrirlo no cuenta.
    /// </summary>
    private void Navigate(PadCombo c, HidPads.Buttons now, long tick, List<string> nav, ref bool closeHud)
    {
        var pressed = now & ~c.Nav;
        const HidPads.Buttons dirs = HidPads.Buttons.Up | HidPads.Buttons.Down | HidPads.Buttons.Left | HidPads.Buttons.Right;
        bool repeat = false;
        if ((now & dirs) == 0 || (pressed & dirs) != 0) c.NavSince = c.NavLastRepeat = tick;
        else if (tick - c.NavSince >= NavRepeatDelayMs && tick - c.NavLastRepeat >= NavRepeat)
        {
            c.NavLastRepeat = tick;
            repeat = true;
        }
        c.Nav = now;
        if (!_shown) return;

        if (pressed.HasFlag(HidPads.Buttons.B)) { closeHud = true; return; }
        if (pressed.HasFlag(HidPads.Buttons.A)) nav.Add("a");
        var move = repeat ? now : pressed;
        if (move.HasFlag(HidPads.Buttons.Up)) nav.Add("up");
        else if (move.HasFlag(HidPads.Buttons.Down)) nav.Add("down");
        else if (move.HasFlag(HidPads.Buttons.Left)) nav.Add("left");
        else if (move.HasFlag(HidPads.Buttons.Right)) nav.Add("right");
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
            _hidPads.Dispose();
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
