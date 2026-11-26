using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Updates;

namespace OpenStreamMS.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    static readonly byte[] Setup = Encoding.ASCII.GetBytes(new string('x', 300_000));
    static readonly string SetupSha = Convert.ToHexStringLower(SHA256.HashData(Setup));
    const string DownloadUrl = "https://github.com/OpenstreamCo/OpenStreamMultiSeat/releases/download/v2.3.0/OpenStreamMS-Setup-2.3.0.exe";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "osms-update-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    sealed class Fake(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public readonly List<Uri> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Requests) Requests.Add(r.RequestUri!);
            return answer(r);
        }
    }

    static string Releases(string? digest = null, string url = DownloadUrl, long? size = null) => $$"""
        [ { "tag_name": "v2.3.0", "prerelease": false, "draft": false,
            "html_url": "https://github.com/OpenstreamCo/OpenStreamMultiSeat/releases/tag/v2.3.0",
            "assets": [ { "name": "OpenStreamMS-Setup-2.3.0.exe", "size": {{size ?? Setup.Length}},
                          "browser_download_url": "{{url}}"{{(digest is null ? "" : $", \"digest\": \"{digest}\"")}} } ] } ]
        """;

    static async Task<UpdateChecker> Checker(string releases, string current = "2.2.0", bool dev = false)
    {
        var gh = new Fake(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releases) }));
        var c = new UpdateChecker(new ServiceConfig(), gh, current, dev);
        await c.CheckAsync(force: true);
        return c;
    }

    /// <summary>github.com redirige a release-assets.githubusercontent.com, que sirve el fichero.</summary>
    static Fake Downloads(byte[] body, string redirectHost = "release-assets.githubusercontent.com") => new(r =>
    {
        if (r.RequestUri!.Host == "github.com")
        {
            var res = new HttpResponseMessage(HttpStatusCode.Found);
            res.Headers.Location = new Uri($"https://{redirectHost}/file?sig=1");
            return Task.FromResult(res);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    });

    static async Task<UpdateInstallStatus> WaitUntilDone(UpdateInstaller i)
    {
        for (int n = 0; n < 200; n++)
        {
            var s = i.Status;
            if (s.State is "installing" or "failed") return s;
            await Task.Delay(25);
        }
        return i.Status;
    }

    [Fact]
    public async Task Downloads_checks_and_runs_the_installer_silently()
    {
        var checker = await Checker(Releases("sha256:" + SetupSha));
        Assert.True(checker.Status().Latest!.Installable);
        var dl = Downloads(Setup);
        ProcessStartInfo? launched = null;
        var inst = new UpdateInstaller(checker, dl, _dir, psi => { launched = psi; return true; });

        Assert.Null(inst.Start("2.3.0"));
        var st = await WaitUntilDone(inst);

        Assert.Equal("installing", st.State);
        Assert.NotNull(launched);
        var exe = Path.Combine(_dir, "OpenStreamMS-Setup-2.3.0.exe");
        Assert.Equal(exe, launched!.FileName);
        Assert.Equal(Setup, File.ReadAllBytes(exe));
        Assert.Contains("/VERYSILENT", launched.Arguments);
        Assert.Contains("/NORESTART", launched.Arguments);
        Assert.Contains("/TASKS=\"startservice\"", launched.Arguments);
        Assert.Contains(st.LogPath!, launched.Arguments);
        Assert.Equal(["github.com", "release-assets.githubusercontent.com"], dl.Requests.Select(u => u.Host));
        Assert.False(File.Exists(exe + ".part"));
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_published_hash_is_not_run()
    {
        var checker = await Checker(Releases("sha256:" + new string('0', 64)));
        bool launched = false;
        var inst = new UpdateInstaller(checker, Downloads(Setup), _dir, _ => launched = true);

        Assert.Null(inst.Start("2.3.0"));
        var st = await WaitUntilDone(inst);

        Assert.Equal("failed", st.State);
        Assert.Equal("hash", st.Error);
        Assert.False(launched);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.exe"));
    }

    [Fact]
    public async Task A_shorter_download_is_rejected()
    {
        var checker = await Checker(Releases("sha256:" + SetupSha));
        var inst = new UpdateInstaller(checker, Downloads(Setup[..1000]), _dir, _ => true);
        Assert.Null(inst.Start("2.3.0"));
        var st = await WaitUntilDone(inst);
        Assert.Equal("failed", st.State);
        Assert.Equal("size", st.Error);
    }

    [Fact]
    public async Task Redirects_to_other_servers_are_not_followed()
    {
        var checker = await Checker(Releases("sha256:" + SetupSha));
        var dl = Downloads(Setup, redirectHost: "evil.example");
        var inst = new UpdateInstaller(checker, dl, _dir, _ => true);

        Assert.Null(inst.Start("2.3.0"));
        var st = await WaitUntilDone(inst);

        Assert.Equal("failed", st.State);
        Assert.Equal("download", st.Error);
        Assert.DoesNotContain(dl.Requests, u => u.Host == "evil.example");
    }

    [Fact]
    public async Task Only_the_announced_newer_version_can_be_installed()
    {
        var ok = await Checker(Releases("sha256:" + SetupSha));
        var inst = new UpdateInstaller(ok, Downloads(Setup), _dir, _ => true);
        Assert.Equal("notavailable", inst.Start("2.2.0"));
        Assert.Equal("notavailable", inst.Start(null));

        var upToDate = await Checker(Releases("sha256:" + SetupSha), current: "2.3.0");
        Assert.Equal("notavailable", new UpdateInstaller(upToDate, Downloads(Setup), _dir, _ => true).Start("2.3.0"));

        var dev = await Checker(Releases("sha256:" + SetupSha), current: "1.0.5-dev", dev: true);
        Assert.Equal("devbuild", new UpdateInstaller(dev, Downloads(Setup), _dir, _ => true).Start("2.3.0"));
    }

    [Theory]
    [InlineData(null, DownloadUrl)]                                                                       // sin SHA-256
    [InlineData("sha256:abc", DownloadUrl)]                                                               // SHA-256 roto
    [InlineData("sha256:" + "ab" + "cdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "https://example.com/OpenStreamMS-Setup-2.3.0.exe")]
    public async Task Releases_without_a_verifiable_installer_are_only_linked(string? digest, string url)
    {
        var checker = await Checker(Releases(digest, url));
        Assert.True(checker.Status().UpdateAvailable);
        Assert.False(checker.Status().Latest!.Installable);
        Assert.Equal("noinstaller", new UpdateInstaller(checker, Downloads(Setup), _dir, _ => true).Start("2.3.0"));
    }

    [Fact]
    public async Task A_second_update_is_refused_while_one_is_running()
    {
        var checker = await Checker(Releases("sha256:" + SetupSha));
        var gate = new TaskCompletionSource();
        var dl = new Fake(async r =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup) };
        });
        var inst = new UpdateInstaller(checker, dl, _dir, _ => true);

        Assert.Null(inst.Start("2.3.0"));
        Assert.Equal("downloading", inst.Status.State);
        Assert.Equal("busy", inst.Start("2.3.0"));
        gate.SetResult();
        Assert.Equal("installing", (await WaitUntilDone(inst)).State);
    }
}
