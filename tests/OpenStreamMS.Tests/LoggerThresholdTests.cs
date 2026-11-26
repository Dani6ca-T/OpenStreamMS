using OpenStreamMS.Services;

namespace OpenStreamMS.Tests;

public class LoggerThresholdTests
{
    [Theory]
    [InlineData(5_000L)]              // recién arrancado el equipo
    [InlineData(86_400_000L)]         // un día
    [InlineData(long.MaxValue / 2)]
    public void Processes_other_than_the_service_read_the_published_level_the_first_time(long now)
    {
        // Antes: now - long.MinValue se desbordaba a negativo y no se leía nunca (#101).
        Assert.True(Logger.DueForRefresh(now, long.MinValue));
    }

    [Fact]
    public void Then_every_ten_seconds()
    {
        Assert.False(Logger.DueForRefresh(15_000, 10_000));
        Assert.True(Logger.DueForRefresh(20_001, 10_000));
    }
}
