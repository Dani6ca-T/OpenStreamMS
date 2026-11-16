using OpenStreamMS.Services.Gamepad;
using Xunit;

namespace OpenStreamMS.Tests;

public class OsmsPadInstanceTests
{
    [Theory]
    // ID real de un mando creado en la sesión 1 (Windows 11, 2026-11-14)
    [InlineData(@"HID\VID_045E&PID_0B12&IG_00\1&36444BD7&0&OSMS_S1_P0", 1u, 0)]
    [InlineData(@"HID\VID_045E&PID_0B12&IG_00\1&abc&0&osms_s12_p15", 12u, 15)]
    public void ReadsSessionAndPad(string instanceId, uint session, int pad)
    {
        var m = GamepadSessionIsolation.OsmsPadInstanceRegex().Match(instanceId);
        Assert.True(m.Success);
        Assert.Equal(session, uint.Parse(m.Groups[1].Value));
        Assert.Equal(pad, int.Parse(m.Groups[2].Value));
    }

    [Theory]
    [InlineData(@"HID\VID_045E&PID_0B12&IG_00\2&26D203C3&0&0000")]              // colección HID hija
    [InlineData(@"USB\VID_045E&PID_028E\OSMS_S1_P0_X")]                          // no acaba en el patrón
    [InlineData(@"ROOT\SYSTEM\0005")]
    public void IgnoresOtherNodes(string instanceId) =>
        Assert.False(GamepadSessionIsolation.OsmsPadInstanceRegex().IsMatch(instanceId));
}
