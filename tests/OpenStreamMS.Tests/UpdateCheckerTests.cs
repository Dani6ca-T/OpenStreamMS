using System.Net;
using System.Text;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Updates;

namespace OpenStreamMS.Tests;

public class UpdateCheckerTests
{
    sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            return Task.FromResult(answer(request));
        }
    }

    static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    const string Releases = """
        [
          { "tag_name": "v2.4.0-beta.1", "prerelease": true,  "draft": false, "html_url": "https://github.com/OpenstreamCo/OpenStreamMultiSeat/releases/tag/v2.4.0-beta.1", "published_at": "2026-11-26T10:00:00Z" },
          { "tag_name": "v2.5.0",        "prerelease": false, "draft": true,  "html_url": "https://github.com/OpenstreamCo/OpenStreamMultiSeat/releases/tag/v2.5.0" },
          { "tag_name": "v2.3.0",        "prerelease": false, "draft": false, "html_url": "https://github.com/OpenstreamCo/OpenStreamMultiSeat/releases/tag/v2.3.0", "published_at": "2026-11-25T10:00:00Z" },
          { "tag_name": "v2.2.0",        "prerelease": false, "draft": false, "html_url": "https://evil.example/x", "published_at": "2026-11-24T10:00:00Z" },
          { "tag_name": "nightly",       "prerelease": true,  "draft": false, "html_url": "https://github.com/OpenstreamCo/OpenStreamMultiSeat/releases/tag/nightly" }
        ]
        """;

    static (UpdateChecker Checker, FakeGitHub Http, ServiceConfig Cfg, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance)
        Make(string current, bool dev = false, string body = Releases, HttpStatusCode status = HttpStatusCode.OK)
    {
        var cfg = new ServiceConfig();
        var http = new FakeGitHub(_ => status == HttpStatusCode.OK ? Json(body) : new HttpResponseMessage(status));
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        Func<DateTimeOffset> clock = () => now;
        var checker = new UpdateChecker(cfg, http, current, dev, clock);
        return (checker, http, cfg, clock, d => now += d);
    }

    [Theory]
    [InlineData("2.2.0", "2.2.0", 0)]
    [InlineData("v2.3.0", "2.2.9", 1)]
    [InlineData("2.10.0", "2.9.0", 1)]
    [InlineData("2.3.0", "2.3.0-beta.1", 1)]
    [InlineData("2.3.0-beta.2", "2.3.0-beta.10", -1)]
    [InlineData("2.3.0-beta.1", "2.3.0-alpha.5", 1)]
    [InlineData("2.3.0-rc.1", "2.3.0-rc.1.1", -1)]
    [InlineData("2.2.0+abc123", "2.2.0", 0)]
    [InlineData("2.2", "2.2.0", 0)]
    public void Versions_compare_like_semver(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(AppVersion.Parse(a)!.CompareTo(AppVersion.Parse(b))));
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("")]
    [InlineData("2.3.0-")]
    [InlineData(null)]
    public void Invalid_versions_are_rejected(string? text) => Assert.Null(AppVersion.Parse(text));

    [Fact]
    public async Task Stable_install_sees_the_newest_stable_release_only()
    {
        var (u, http, _, _, _) = Make("2.2.0+0123abcd");
        var st = await u.CheckAsync();

        Assert.Equal("2.2.0", st.CurrentVersion);
        Assert.True(st.UpdateAvailable);
        Assert.Equal("2.3.0", st.Latest!.Version);          // ni la beta ni el borrador 2.5.0
        Assert.False(st.Latest.Prerelease);
        Assert.Equal("https://github.com/OpenstreamCo/OpenStreamMultiSeat/releases/tag/v2.3.0", st.Latest.Url);
        Assert.Null(st.Error);
        Assert.Equal("repos/OpenstreamCo/OpenStreamMultiSeat/releases?per_page=30", http.Last!.RequestUri!.PathAndQuery.TrimStart('/'));
        Assert.Contains("OpenStreamMS/2.2.0", http.Last.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Beta_install_also_sees_newer_betas()
    {
        var (u, _, _, _, _) = Make("2.3.0");
        Assert.False((await u.CheckAsync()).UpdateAvailable);

        var (beta, _, _, _, _) = Make("2.3.0-beta.3");
        var st = await beta.CheckAsync();
        Assert.True(st.UpdateAvailable);
        Assert.Equal("2.4.0-beta.1", st.Latest!.Version);
        Assert.True(st.Latest.Prerelease);
    }

    [Fact]
    public async Task Release_links_outside_the_repository_are_not_trusted()
    {
        var (u, _, _, _, _) = Make("2.1.0", body: """[ { "tag_name": "v2.2.0", "prerelease": false, "draft": false, "html_url": "https://evil.example/x" } ]""");
        var st = await u.CheckAsync();
        Assert.Equal("https://github.com/OpenstreamCo/OpenStreamMultiSeat/releases/tag/v2.2.0", st.Latest!.Url);
    }

    [Fact]
    public async Task Dev_builds_never_announce_updates()
    {
        var (u, _, _, _, _) = Make("1.0.123-dev", dev: true);
        var st = await u.CheckAsync();
        Assert.True(st.DevBuild);
        Assert.Equal("2.3.0", st.Latest!.Version);
        Assert.False(st.UpdateAvailable);
    }

    [Fact]
    public async Task Results_are_cached_for_six_hours_and_forced_checks_are_spaced()
    {
        var (u, http, _, _, advance) = Make("2.2.0");
        await u.CheckAsync();
        await u.CheckAsync();
        Assert.Equal(1, http.Calls);

        await u.CheckAsync(force: true);       // recién mirado: no repite
        Assert.Equal(1, http.Calls);
        advance(TimeSpan.FromSeconds(30));
        await u.CheckAsync(force: true);
        Assert.Equal(2, http.Calls);

        advance(TimeSpan.FromHours(5));
        await u.CheckAsync();
        Assert.Equal(2, http.Calls);
        advance(TimeSpan.FromHours(1.1));
        await u.CheckAsync();
        Assert.Equal(3, http.Calls);
    }

    [Fact]
    public async Task Disabled_check_does_not_go_to_github_unless_asked()
    {
        var (u, http, cfg, _, _) = Make("2.2.0");
        cfg.CheckForUpdates = false;
        var st = await u.CheckAsync();
        Assert.Equal(0, http.Calls);
        Assert.Null(st.Latest);
        Assert.False(st.CheckEnabled);

        st = await u.CheckAsync(force: true);
        Assert.Equal(1, http.Calls);
        Assert.True(st.UpdateAvailable);
    }

    [Fact]
    public async Task Failures_are_reported_and_retried_sooner()
    {
        var (u, http, _, _, advance) = Make("2.2.0", status: HttpStatusCode.InternalServerError);
        var st = await u.CheckAsync();
        Assert.Equal("github", st.Error);
        Assert.False(st.UpdateAvailable);

        advance(TimeSpan.FromMinutes(10));
        await u.CheckAsync();
        Assert.Equal(1, http.Calls);
        advance(TimeSpan.FromMinutes(6));
        await u.CheckAsync();
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task Rate_limit_is_told_apart()
    {
        var cfg = new ServiceConfig();
        var http = new FakeGitHub(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("x-ratelimit-remaining", "0");
            return r;
        });
        var u = new UpdateChecker(cfg, http, "2.2.0", false);
        Assert.Equal("ratelimit", (await u.CheckAsync()).Error);
    }
}
