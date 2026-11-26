using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Updates;

namespace OpenStreamMS.Tests;

/// <summary>Las rutas de /api/updates tal como las monta Program.cs, con GitHub simulado.</summary>
public class UpdateEndpointsTests
{
    sealed class FakeGitHub : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[ { "tag_name": "v2.3.0", "prerelease": false, "draft": false, "html_url": "https://github.com/OpenstreamCo/OpenStreamMultiSeat/releases/tag/v2.3.0" } ]""",
                                            Encoding.UTF8, "application/json"),
            });
        }
    }

    record Status(string CurrentVersion, bool DevBuild, bool CheckEnabled, Release? Latest, bool UpdateAvailable, string Dismissed, string? Error);
    record Release(string Version, string Url);

    static async Task<(WebApplication App, HttpClient Client, FakeGitHub GitHub)> Start(bool checkEnabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var cfg = new ServiceConfig { CheckForUpdates = checkEnabled };
        var github = new FakeGitHub();
        builder.Services.AddSingleton(cfg);
        var checker = new UpdateChecker(cfg, github, "2.2.0+abc", devBuild: false);
        builder.Services.AddSingleton(checker);
        builder.Services.AddSingleton(new UpdateInstaller(checker, github, Path.Combine(Path.GetTempPath(), "osms-update-ep-" + Guid.NewGuid().ToString("N")), _ => false));
        var app = builder.Build();
        app.MapUpdateApi();
        await app.StartAsync();
        return (app, app.GetTestClient(), github);
    }

    [Fact]
    public async Task Panel_gets_the_installed_version_and_the_new_release()
    {
        var (app, client, github) = await Start(checkEnabled: true);
        await using var _ = app;

        var st = await client.GetFromJsonAsync<Status>("/api/updates");
        Assert.Equal("2.2.0", st!.CurrentVersion);
        Assert.True(st.UpdateAvailable);
        Assert.Equal("2.3.0", st.Latest!.Version);
        Assert.Equal("", st.Dismissed);
        Assert.Equal(1, github.Calls);

        await client.GetFromJsonAsync<Status>("/api/updates");   // caché: no vuelve a GitHub
        Assert.Equal(1, github.Calls);
    }

    [Fact]
    public async Task With_the_check_off_only_check_now_goes_to_github()
    {
        var (app, client, github) = await Start(checkEnabled: false);
        await using var _ = app;

        var st = await client.GetFromJsonAsync<Status>("/api/updates");
        Assert.False(st!.CheckEnabled);
        Assert.Null(st.Latest);
        Assert.Equal(0, github.Calls);

        var res = await client.PostAsync("/api/updates/check", null);
        res.EnsureSuccessStatusCode();
        st = await res.Content.ReadFromJsonAsync<Status>();
        Assert.True(st!.UpdateAvailable);
        Assert.Equal(1, github.Calls);
    }

    [Fact]
    public async Task Install_refuses_releases_without_a_verifiable_installer()
    {
        var (app, client, _) = await Start(checkEnabled: true);
        await using var _ = app;

        var idle = await client.GetFromJsonAsync<InstallStatus>("/api/updates/install");
        Assert.Equal("idle", idle!.State);

        await client.GetAsync("/api/updates");   // la release simulada no trae instalador con SHA-256
        var res = await client.PostAsJsonAsync("/api/updates/install", new { version = "2.3.0" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("noinstaller", await res.Content.ReadAsStringAsync());
    }

    record InstallStatus(string State, string? Version);

    [Fact]
    public async Task Settings_reject_a_dismissed_value_that_is_not_a_version()
    {
        var (app, client, _) = await Start(checkEnabled: true);
        await using var _ = app;

        var bad = await client.PutAsJsonAsync("/api/updates/settings", new { dismissed = "<script>" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Sin cambios no se guarda nada en service.config.json.
        var same = await client.PutAsJsonAsync("/api/updates/settings", new { checkForUpdates = true, dismissed = "" });
        same.EnsureSuccessStatusCode();
        Assert.True((await same.Content.ReadFromJsonAsync<Status>())!.CheckEnabled);
    }
}
