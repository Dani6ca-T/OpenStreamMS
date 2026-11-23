using System.Text;
using OpenStreamMS.Services.Drivers;
using Xunit;

namespace OpenStreamMS.Tests;

/// <summary>
/// Los instaladores de ViGEmBus y HidHide son de Advanced Installer: con los argumentos de
/// WiX Burn ("/quiet /install") muestran "Invalid command line" y no instalan nada (issue #80).
/// </summary>
public sealed class DriverInstallerArgsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osms-drvargs-" + Guid.NewGuid().ToString("N"));

    public DriverInstallerArgsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>Ejecutable PE mínimo con esas secciones (solo cabeceras).</summary>
    private string FakePe(params string[] sections)
    {
        const int pe = 0x80, optionalSize = 0xF0;
        var bytes = new byte[pe + 24 + optionalSize + 40 * sections.Length + 16];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(pe).CopyTo(bytes, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, pe);
        BitConverter.GetBytes((ushort)sections.Length).CopyTo(bytes, pe + 6);
        BitConverter.GetBytes((ushort)optionalSize).CopyTo(bytes, pe + 20);
        for (int i = 0; i < sections.Length; i++)
            Encoding.ASCII.GetBytes(sections[i]).CopyTo(bytes, pe + 24 + optionalSize + 40 * i);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void AdvancedInstallerBootstrapperGetsExenoui()
    {
        var exe = FakePe(".text", ".rdata", ".data", ".rsrc", ".reloc");
        Assert.Equal(NefariusDriverManager.AdvancedInstallerArgs, NefariusDriverManager.SilentInstallArgs(exe));
    }

    [Fact]
    public void WixBurnBundleGetsQuietInstall()
    {
        var exe = FakePe(".text", ".rdata", ".data", ".wixburn", ".rsrc", ".reloc");
        Assert.Equal(NefariusDriverManager.WixBurnArgs, NefariusDriverManager.SilentInstallArgs(exe));
    }

    [Fact]
    public void SectionNamesAreRead()
    {
        var exe = FakePe(".text", ".wixburn");
        Assert.Equal([".text", ".wixburn"], NefariusDriverManager.PeSectionNames(exe));
    }

    [Fact]
    public void UnreadableFileFallsBackToAdvancedInstaller()
    {
        var notPe = Path.Combine(_dir, "broken.exe");
        File.WriteAllText(notPe, "not an executable");
        Assert.Equal(NefariusDriverManager.AdvancedInstallerArgs, NefariusDriverManager.SilentInstallArgs(notPe));
        Assert.Equal(NefariusDriverManager.AdvancedInstallerArgs,
                     NefariusDriverManager.SilentInstallArgs(Path.Combine(_dir, "missing.exe")));
    }
}
