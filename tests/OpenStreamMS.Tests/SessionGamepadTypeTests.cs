using OpenStreamMS.Core.Api;
using OpenStreamMS.Services.Sunshine;
using Xunit;

namespace OpenStreamMS.Tests;

/// <summary>El tipo de mando de cada sesión (auto, x360, ds4) se elige desde el panel y va a su sunshine.conf.</summary>
public sealed class SessionGamepadTypeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osms-gptype-" + Guid.NewGuid().ToString("N"));
    private string Exe => Path.Combine(_dir, "sunshine.exe");
    private string Conf => Path.Combine(_dir, "config", "sunshine.conf");

    public SessionGamepadTypeTests() => Directory.CreateDirectory(Path.Combine(_dir, "config"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData(" X360 ", "x360")]
    [InlineData("DS4", "ds4")]
    [InlineData("xone", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyKnownTypesAreAccepted(string? value, string? expected) =>
        Assert.Equal(expected, StreamSession.NormalizeGamepadType(value));

    [Fact]
    public void ChosenTypeReplacesTheOneInSunshineConf()
    {
        File.WriteAllLines(Conf, ["gamepad = ds4"]);
        SunshineConfigurator.WriteConfig(Exe, new(StreamPort: 47989, Gamepad: "x360"));
        Assert.Equal("x360", SunshineConfigurator.ReadConfigValue(Exe, SunshineConfigurator.GamepadKey));
    }

    [Fact]
    public void WithoutTypeSunshineConfIsNotTouched()
    {
        File.WriteAllLines(Conf, ["gamepad = ds4"]);
        SunshineConfigurator.WriteConfig(Exe, new(StreamPort: 47989));
        Assert.Equal("ds4", SunshineConfigurator.ReadConfigValue(Exe, SunshineConfigurator.GamepadKey));
    }
}
