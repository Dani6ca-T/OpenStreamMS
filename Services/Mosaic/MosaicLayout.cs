using System.Text;
using OpenStreamMS.Services.Seats;

namespace OpenStreamMS.Services.Mosaic;

/// <summary>
/// Reparto de una sesión compuesta: plantillas de huecos, tamaño de cada hueco y el fichero
/// <c>osms-mosaic.txt</c> que lee Sunshine. Los huecos son las partes de la pantalla dividida de
/// los puestos (<see cref="SeatScreenArea"/>), celdas de una cuadrícula de 2×2, y se calculan igual
/// (<see cref="SeatMonitor.Region"/>); Sunshine (<c>parse_mosaic_layout</c>, <c>mosaic_rect</c>)
/// reparte el lienzo con las mismas celdas.
/// </summary>
public static class MosaicLayout
{
    /// <summary>Sesiones como máximo en una compuesta.</summary>
    public const int MaxMembers = 4;
    /// <summary>Sesiones como mínimo en una compuesta.</summary>
    public const int MinMembers = 2;

    /// <summary>Nombre del fichero de control, junto a <c>sunshine.conf</c> de la compuesta.</summary>
    public const string FileName = "osms-mosaic.txt";
    /// <summary>Sesión que recibe el teclado y el ratón (session_input.cpp de Sunshine), si no es la del teclado.</summary>
    public const string InputFileName = "osms-input.txt";

    /// <summary>
    /// Plantillas que ofrece el panel, con sus huecos en orden (el mando N va al hueco N). Una
    /// compuesta puede dejar huecos sin sesión (salen en negro), pero no inventar otros.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, SeatScreenArea[]> Templates =
        new Dictionary<string, SeatScreenArea[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["sideBySide"] = [SeatScreenArea.Left, SeatScreenArea.Right],
            ["stacked"]    = [SeatScreenArea.Top, SeatScreenArea.Bottom],
            ["quad"]       = [SeatScreenArea.TopLeft, SeatScreenArea.TopRight, SeatScreenArea.BottomLeft, SeatScreenArea.BottomRight],
            ["bigLeft"]    = [SeatScreenArea.Left, SeatScreenArea.TopRight, SeatScreenArea.BottomRight],
            ["bigTop"]     = [SeatScreenArea.Top, SeatScreenArea.BottomLeft, SeatScreenArea.BottomRight],
        };

    /// <summary>True si ningún par de huecos comparte celda de la cuadrícula.</summary>
    public static bool Disjoint(IEnumerable<SeatScreenArea> areas)
    {
        int used = 0;
        foreach (var a in areas)
        {
            int cells = SeatAreas.Cells(a);
            if ((used & cells) != 0) return false;
            used |= cells;
        }
        return true;
    }

    /// <summary>Tamaño de una parte en un lienzo de <paramref name="width"/>×<paramref name="height"/>.</summary>
    public static (int Width, int Height) PartSize(SeatScreenArea area, int width, int height)
    {
        var (_, _, w, h) = new SeatMonitor { Area = area }.Region(0, 0, width, height);
        return (w, h);
    }

    /// <summary>
    /// Contenido de <c>osms-mosaic.txt</c>: el lienzo y una línea por hueco con sesión de Windows
    /// ("&lt;id de sesión de Windows&gt; &lt;parte&gt;"). Un hueco sin sesión (0) no se escribe y sale en negro.
    /// </summary>
    public static string Render(int canvasWidth, int canvasHeight, IReadOnlyList<(uint WindowsSession, SeatScreenArea Area)> parts) =>
        Render(canvasWidth, canvasHeight, parts.Select(p => (p.WindowsSession, p.Area, (string?)null)).ToList());

    /// <summary>
    /// Igual, con la imagen de espera de cada hueco tras la parte ("&lt;sesión&gt; &lt;parte&gt; &lt;ruta&gt;"):
    /// Sunshine la pone mientras esa sesión no da imagen. Un hueco sin sesión de Windows se escribe
    /// con la sesión 0 si tiene imagen (se queda en su sitio, con el logo, mientras su sesión se
    /// reinicia); sin imagen no se escribe. Un Sunshine sin esta función ignora la ruta.
    /// </summary>
    public static string Render(int canvasWidth, int canvasHeight, IReadOnlyList<(uint WindowsSession, SeatScreenArea Area, string? Waiting)> parts)
    {
        var sb = new StringBuilder();
        sb.Append("# OpenStreamMS mosaic: <Windows session id> <part> [<waiting image>]\n");
        sb.Append($"canvas {canvasWidth} {canvasHeight}\n");
        foreach (var (session, area, waiting) in parts)
        {
            if (session == 0 && string.IsNullOrEmpty(waiting)) continue;
            sb.Append($"{session} {area}");
            if (!string.IsNullOrEmpty(waiting)) sb.Append(' ').Append(waiting);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Contenido de <see cref="InputFileName"/>: "&lt;sesión de Windows&gt; &lt;x&gt; &lt;y&gt; &lt;ancho&gt;
    /// &lt;alto&gt; &lt;ancho del lienzo&gt; &lt;alto del lienzo&gt;", con el hueco en píxeles del lienzo
    /// (Sunshine lleva ahí la posición absoluta del ratón).
    /// </summary>
    public static string RenderInputTarget(uint windowsSession, SeatScreenArea area, int canvasWidth, int canvasHeight)
    {
        var (x, y, w, h) = new SeatMonitor { Area = area }.Region(0, 0, canvasWidth, canvasHeight);
        return "# OpenStreamMS: <Windows session id> <x> <y> <width> <height> <canvas width> <canvas height>\n" +
               $"{windowsSession} {x} {y} {w} {h} {canvasWidth} {canvasHeight}\n";
    }
}
