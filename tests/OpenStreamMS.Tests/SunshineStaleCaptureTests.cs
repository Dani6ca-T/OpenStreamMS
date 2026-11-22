using OpenStreamMS.Services.Sunshine;
using Xunit;

namespace OpenStreamMS.Tests;

/// <summary>
/// Si RemoteIdd sale de RDP-Tcp (pendiente de reiniciar, o el arranque lo quita), el
/// "capture = remoteidd" de un arranque anterior no puede quedarse en sunshine.conf: Sunshine
/// no encontraría frames y el stream no arrancaría (issue #69, error 503).
/// </summary>
public sealed class SunshineStaleCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osms-capture-" + Guid.NewGuid().ToString("N"));
    private string Exe => Path.Combine(_dir, "sunshine.exe");
    private string Conf => Path.Combine(_dir, "config", "sunshine.conf");

    public SunshineStaleCaptureTests() => Directory.CreateDirectory(Path.Combine(_dir, "config"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void StaleRemoteIddIsRemovedWhenCaptureIsAuto()
    {
        File.WriteAllLines(Conf, ["capture = remoteidd", "encoder = amdvce"]);
        SunshineConfigurator.WriteConfig(Exe, new(StreamPort: 47989));
        Assert.Null(SunshineConfigurator.ReadConfigValue(Exe, "capture"));
        Assert.Equal("amdvce", SunshineConfigurator.ReadConfigValue(Exe, "encoder"));
    }

    [Fact]
    public void RemoteIddIsKeptWhenStillInUse()
    {
        File.WriteAllLines(Conf, ["capture = remoteidd"]);
        SunshineConfigurator.WriteConfig(Exe, new(StreamPort: 47989, Capture: "remoteidd"));
        Assert.Equal("remoteidd", SunshineConfigurator.ReadConfigValue(Exe, "capture"));
    }

    [Fact]
    public void RemoteIddIsReplacedByExplicitCapture()
    {
        File.WriteAllLines(Conf, ["capture = remoteidd"]);
        SunshineConfigurator.WriteConfig(Exe, new(StreamPort: 47989, Capture: "wgc"));
        Assert.Equal("wgc", SunshineConfigurator.ReadConfigValue(Exe, "capture"));
    }

    [Fact]
    public void OtherCaptureChosenByUserIsKept()
    {
        File.WriteAllLines(Conf, ["capture = ddx"]);
        SunshineConfigurator.WriteConfig(Exe, new(StreamPort: 47989));
        Assert.Equal("ddx", SunshineConfigurator.ReadConfigValue(Exe, "capture"));
    }
}
