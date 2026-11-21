using OpenStreamMS.Services.Sunshine;
using Xunit;

namespace OpenStreamMS.Tests;

/// <summary>
/// La 2.0.0 forzaba gamepad_driver = osmspad (mandos Xbox Series por xinputhid, que pierden
/// XInput en las sesiones de stream, issue #69). Al actualizar vuelve a vigembus una sola vez.
/// </summary>
public sealed class SunshineGamepadDriverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osms-gpdrv-" + Guid.NewGuid().ToString("N"));
    private string Exe => Path.Combine(_dir, "sunshine.exe");
    private string Conf => Path.Combine(_dir, "config", "sunshine.conf");

    public SunshineGamepadDriverTests() => Directory.CreateDirectory(Path.Combine(_dir, "config"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly SunshineConfigurator.SunshineConfig Cfg = new(StreamPort: 47989);

    [Fact]
    public void OsmsPadFromTwoZeroIsRevertedToViGEm()
    {
        File.WriteAllLines(Conf, ["gamepad_driver = osmspad", "gamepad = ds4"]);
        SunshineConfigurator.WriteConfig(Exe, Cfg);
        Assert.Equal("vigembus", SunshineConfigurator.ReadConfigValue(Exe, "gamepad_driver"));
        Assert.Equal("ds4", SunshineConfigurator.ReadConfigValue(Exe, "gamepad"));
    }

    [Fact]
    public void OsmsPadChosenLaterIsKept()
    {
        File.WriteAllLines(Conf, ["gamepad_driver = osmspad"]);
        SunshineConfigurator.WriteConfig(Exe, Cfg);

        // El usuario lo vuelve a elegir desde el panel de Sunshine: ya no se toca.
        var lines = File.ReadAllLines(Conf).Select(l => l.StartsWith("gamepad_driver") ? "gamepad_driver = osmspad" : l);
        File.WriteAllLines(Conf, lines);
        SunshineConfigurator.WriteConfig(Exe, Cfg);
        Assert.Equal("osmspad", SunshineConfigurator.ReadConfigValue(Exe, "gamepad_driver"));
    }

    [Fact]
    public void NewInstanceGetsViGEmAndDs4()
    {
        SunshineConfigurator.WriteConfig(Exe, Cfg);
        Assert.Equal("vigembus", SunshineConfigurator.ReadConfigValue(Exe, "gamepad_driver"));
        Assert.Equal("ds4", SunshineConfigurator.ReadConfigValue(Exe, "gamepad"));
    }

    [Fact]
    public void OtherDriverIsNotTouched()
    {
        File.WriteAllLines(Conf, ["gamepad_driver = virtualhid"]);
        SunshineConfigurator.WriteConfig(Exe, Cfg);
        Assert.Equal("virtualhid", SunshineConfigurator.ReadConfigValue(Exe, "gamepad_driver"));
    }
}
