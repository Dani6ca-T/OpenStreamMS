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
    public static string Render(int canvasWidth, int canvasHeight, IReadOnlyList<(uint WindowsSession, SeatScreenArea Area)> parts)
    {
        var sb = new StringBuilder();
        sb.Append("# OpenStreamMS mosaic: <Windows session id> <part>\n");
        sb.Append($"canvas {canvasWidth} {canvasHeight}\n");
        foreach (var (session, area) in parts)
            if (session > 0) sb.Append($"{session} {area}\n");
        return sb.ToString();
    }
}
