using OpenStreamMS.Services.Mosaic;
using OpenStreamMS.Services.Seats;
using Xunit;

namespace OpenStreamMS.Tests;

public class MosaicLayoutTests
{
    [Fact]
    public void Templates_NeverOverlapAndFitTheGrid()
    {
        foreach (var (id, areas) in MosaicLayout.Templates)
        {
            Assert.True(MosaicLayout.Disjoint(areas), id);
            Assert.InRange(areas.Length, MosaicLayout.MinMembers, MosaicLayout.MaxMembers);
            Assert.Equal(SeatAreas.AllCells, areas.Aggregate(0, (used, a) => used | SeatAreas.Cells(a)));
        }
    }

    [Fact]
    public void Templates_OfferTheExpectedLayouts()
    {
        Assert.Equal([SeatScreenArea.Left, SeatScreenArea.Right], MosaicLayout.Templates["sideBySide"]);
        Assert.Equal([SeatScreenArea.Top, SeatScreenArea.Bottom], MosaicLayout.Templates["stacked"]);
        Assert.Equal(4, MosaicLayout.Templates["quad"].Length);
        Assert.Equal([SeatScreenArea.Left, SeatScreenArea.TopRight, SeatScreenArea.BottomRight], MosaicLayout.Templates["bigLeft"]);
        Assert.Equal([SeatScreenArea.Top, SeatScreenArea.BottomLeft, SeatScreenArea.BottomRight], MosaicLayout.Templates["bigTop"]);
    }

    [Theory]
    [InlineData(new[] { SeatScreenArea.Left, SeatScreenArea.TopLeft }, false)]      // la mitad pisa el cuarto
    [InlineData(new[] { SeatScreenArea.Top, SeatScreenArea.Left }, false)]
    [InlineData(new[] { SeatScreenArea.Full, SeatScreenArea.BottomRight }, false)]
    [InlineData(new[] { SeatScreenArea.Left, SeatScreenArea.TopRight }, true)]      // con un hueco libre abajo a la derecha
    [InlineData(new[] { SeatScreenArea.TopLeft, SeatScreenArea.BottomRight }, true)]
    public void Disjoint_DetectsOverlaps(SeatScreenArea[] areas, bool expected) =>
        Assert.Equal(expected, MosaicLayout.Disjoint(areas));

    [Theory]
    [InlineData(SeatScreenArea.Full, 1920, 1080)]
    [InlineData(SeatScreenArea.Left, 960, 1080)]
    [InlineData(SeatScreenArea.Top, 1920, 540)]
    [InlineData(SeatScreenArea.TopLeft, 960, 540)]
    [InlineData(SeatScreenArea.BottomRight, 960, 540)]
    public void PartSize_SplitsTheCanvas(SeatScreenArea area, int w, int h) =>
        Assert.Equal((w, h), MosaicLayout.PartSize(area, 1920, 1080));

    [Fact]
    public void PartSize_GivesTheOddPixelToTheRightAndBottom()
    {
        // Mismo reparto que Sunshine (mosaic_rect) y que la pantalla dividida de los puestos.
        Assert.Equal((960, 540), MosaicLayout.PartSize(SeatScreenArea.TopLeft, 1921, 1081));
        Assert.Equal((961, 541), MosaicLayout.PartSize(SeatScreenArea.BottomRight, 1921, 1081));
    }

    [Fact]
    public void Render_ListsCanvasAndSlotsInOrder()
    {
        var text = MosaicLayout.Render(2560, 1440, [(7u, SeatScreenArea.Left), (9u, SeatScreenArea.TopRight), (12u, SeatScreenArea.BottomRight)]);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("#", lines[0]);
        Assert.Equal(["canvas 2560 1440", "7 Left", "9 TopRight", "12 BottomRight"], lines[1..]);
    }

    [Fact]
    public void Render_SkipsSlotsWithoutWindowsSession()
    {
        // Un miembro parado: su hueco queda en negro.
        var lines = MosaicLayout.Render(1920, 1080, [(7u, SeatScreenArea.Top), (0u, SeatScreenArea.Bottom)])
                                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["canvas 1920 1080", "7 Top"], lines[1..]);
    }

    [Fact]
    public void Render_KeepsSlotWithWaitingImageWhileItsSessionRestarts()
    {
        // Un miembro reiniciándose: su hueco sigue en el mosaico (sesión 0) con su imagen de espera.
        var lines = MosaicLayout.Render(3840, 2160, [(7u, SeatScreenArea.Top, @"C:\Program Files\x\osms-wait-0.bgra"),
                                                     (0u, SeatScreenArea.Bottom, @"C:\Program Files\x\osms-wait-1.bgra"),
                                                     (0u, SeatScreenArea.TopLeft, null)])
                                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["canvas 3840 2160",
                      @"7 Top C:\Program Files\x\osms-wait-0.bgra",
                      @"0 Bottom C:\Program Files\x\osms-wait-1.bgra"], lines[1..]);
    }

    [Fact]
    public void WaitingImage_HasHeaderAndPixels()
    {
        var img = WaitingImage.Render(64, 36, "Sala 2", "Reconectando…");
        Assert.Equal("OSWP"u8.ToArray(), img[..4]);
        Assert.Equal(64, BitConverter.ToInt32(img, 4));
        Assert.Equal(36, BitConverter.ToInt32(img, 8));
        Assert.Equal(12 + 64 * 36 * 4, img.Length);
        Assert.Equal(255, img[12 + 3]);   // opaca
    }

    [Fact]
    public void Render_UsesLfOnly() =>
        Assert.DoesNotContain('\r', MosaicLayout.Render(1920, 1080, [(1u, SeatScreenArea.Left), (2u, SeatScreenArea.Right)]));
}
