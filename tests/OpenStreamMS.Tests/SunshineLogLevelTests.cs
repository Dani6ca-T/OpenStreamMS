using OpenStreamMS.Services;
using OpenStreamMS.Services.Sunshine;
using Xunit;

namespace OpenStreamMS.Tests;

/// <summary>
/// El aislamiento de mandos lee de sunshine.log las líneas de info que dicen qué sesión creó cada
/// mando y su número. Con el nivel por defecto (Error) Sunshine quedaba en min_log_level = 4 y no
/// las escribía: mandos visibles en todas las sesiones y, en la compuesta, todos a la primera.
/// </summary>
public sealed class SunshineLogLevelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osms-loglvl-" + Guid.NewGuid().ToString("N"));
    private string Exe => Path.Combine(_dir, "sunshine.exe");

    public SunshineLogLevelTests() => Directory.CreateDirectory(Path.Combine(_dir, "config"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData(LogThreshold.Info)]
    [InlineData(LogThreshold.Warning)]
    [InlineData(LogThreshold.Error)]
    [InlineData(LogThreshold.Off)]
    public void SunshineAlwaysLogsGamepadCreation(LogThreshold threshold) =>
        Assert.Equal(2, SunshineConfigurator.ToSunshineLogLevel(threshold));

    [Fact]
    public void OldErrorLevelIsRaisedToInfo()
    {
        File.WriteAllLines(Path.Combine(_dir, "config", "sunshine.conf"), ["min_log_level = 4"]);
        SunshineConfigurator.WriteConfig(Exe, new(StreamPort: 47989,
            MinLogLevel: SunshineConfigurator.ToSunshineLogLevel(LogThreshold.Error)));
        Assert.Equal("2", SunshineConfigurator.ReadConfigValue(Exe, "min_log_level"));
    }
}
