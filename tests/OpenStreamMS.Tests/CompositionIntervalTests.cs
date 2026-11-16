using OpenStreamMS.Core.Helpers;
using Xunit;

namespace OpenStreamMS.Tests;

public class CompositionIntervalTests
{
    [Theory]
    [InlineData(60, 4)]     // 250 Hz: Sunshine marca el ritmo
    [InlineData(120, 4)]
    [InlineData(240, 4)]    // 250 Hz alcanza a 240 fps
    [InlineData(250, 4)]
    [InlineData(300, 3)]    // 334 Hz
    [InlineData(400, 2)]    // 500 Hz
    [InlineData(500, 2)]    // techo de Windows
    [InlineData(100, 10)]   // 1000/fps entero: compone a los fps
    public void Smooth(int fps, int expectedMs) =>
        Assert.Equal(expectedMs, RdpSessionCreator.CompositionIntervalMs(fps));

    [Fact]
    public void FpsAboveCeilingIsClamped() =>
        Assert.Equal(2, RdpSessionCreator.CompositionIntervalMs(1000));
}
