using OpenStreamMS.Services.Localization;
using OpenStreamMS.Services.Session;
using Xunit;

namespace OpenStreamMS.Tests;

/// <summary>
/// Si el arranque quita RemoteIdd de RDP-Tcp (RDP-Tcp no escucha con su proveedor: en Windows
/// cliente, lo que permite varias sesiones no deja cargar proveedores que no son de Microsoft,
/// issue #80), el panel y el diagnóstico tienen que poder decir por qué después de reiniciar.
/// </summary>
public sealed class RemoteIddOffTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osms-riddoff-" + Guid.NewGuid().ToString("N"));
    private string Off => Path.Combine(_dir, "remoteidd-off.json");
    private string Install => Path.Combine(_dir, "remoteidd-install.json");

    public RemoteIddOffTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Install, "{}");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void ReasonIsReadBackWhileRemoteIddIsOutOfRdpTcp()
    {
        RdpWrapperManager.WriteRemoteIddOff(Off, "rdpTcpNotListening");
        Assert.Equal("rdpTcpNotListening", RdpWrapperManager.ReadRemoteIddOff(Off, Install, remoteIddActive: false));
    }

    [Fact]
    public void NoReasonWhenRemoteIddIsBackInRdpTcp()
    {
        // Una actualización lo reinstala: se está probando otra vez, no está desactivado.
        RdpWrapperManager.WriteRemoteIddOff(Off, "rdpTcpNotListening");
        Assert.Null(RdpWrapperManager.ReadRemoteIddOff(Off, Install, remoteIddActive: true));
    }

    [Fact]
    public void NoReasonWhenRemoteIddIsUninstalled()
    {
        RdpWrapperManager.WriteRemoteIddOff(Off, "rdpTcpNotListening");
        File.Delete(Install);
        Assert.Null(RdpWrapperManager.ReadRemoteIddOff(Off, Install, remoteIddActive: false));
    }

    [Fact]
    public void NoReasonWithoutFileOrWithBrokenFile()
    {
        Assert.Null(RdpWrapperManager.ReadRemoteIddOff(Off, Install, remoteIddActive: false));
        File.WriteAllText(Off, "{not json");
        Assert.Null(RdpWrapperManager.ReadRemoteIddOff(Off, Install, remoteIddActive: false));
    }

    [Theory]
    [InlineData("rdpTcpNotListening")]
    [InlineData("notLoaded")]
    [InlineData("singleSession")]
    public void EveryReasonHasTextInThePanelLanguage(string reason)
    {
        foreach (var lang in new[] { "en", "es", "zh", "zh_TW", "pt_BR", "de", "ja" })
        {
            using var _ = RequestCulture.Set(lang);
            var reasonText = BackendText.GetForCulture(lang, "rdpwrap." + reason);
            var text = RdpWrapperManager.RemoteIddOffText(reason);
            Assert.DoesNotContain("rdpwrap.", text);
            Assert.Contains(reasonText, text);
            Assert.Equal(BackendText.GetForCulture(lang, "rdpwrap.remoteIddOff", reasonText), text);
        }
    }
}
