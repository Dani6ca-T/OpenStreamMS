using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenStreamMS.Services.Extensions;
using OpenStreamMS.Services.Sunshine;

namespace OpenStreamMS.Tests;

public class ExtensionFormatTests
{
    [Theory]
    [InlineData("owner/repo", "owner", "repo", null, null)]
    [InlineData("https://github.com/Owner/Repo", "Owner", "Repo", null, null)]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo", null, null)]
    [InlineData("github.com/owner/repo/", "owner", "repo", null, null)]
    [InlineData("https://github.com/owner/repo/tree/main/extensions/termwrap", "owner", "repo", "main", "extensions/termwrap")]
    [InlineData("https://github.com/owner/repo/releases/tag/v1.2.0", "owner", "repo", "v1.2.0", null)]
    public void GitHubRef_ParsesSupportedLinks(string input, string owner, string repo, string? @ref, string? path)
    {
        Assert.True(GitHubRef.TryParse(input, out var r));
        Assert.Equal((owner, repo, @ref, path), (r.Owner, r.Repo, r.Ref, r.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("repo")]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com.evil.example/owner/repo")]
    [InlineData("https://github.com/owner/repo?x=1")]
    [InlineData("https://github.com/owner/repo/tree/main/../../x")]
    [InlineData("https://github.com/owner/repo/blob/main/file.txt")]
    [InlineData("-owner/repo")]
    public void GitHubRef_RejectsAnythingElse(string input) =>
        Assert.False(GitHubRef.TryParse(input, out _));

    [Theory]
    [InlineData("install.ps1", true)]
    [InlineData("scripts/install.ps1", true)]
    [InlineData("scripts\\install.ps1", true)]
    [InlineData("../install.ps1", false)]
    [InlineData("a/../../b.ps1", false)]
    [InlineData("/etc/x", false)]
    [InlineData("C:\\Windows\\x.ps1", false)]
    [InlineData("file.ps1:stream", false)]
    [InlineData("a//b", false)]
    public void SafeRelativePath(string path, bool ok) =>
        Assert.Equal(ok, ExtensionFormat.IsSafeRelativePath(path));

    const string Full = """
        {
          "schema": 1, "id": "demo-ext", "name": "Demo", "author": "Someone",
          "component": { "install": "install.ps1", "uninstall": "uninstall.ps1", "detect": "detect.ps1" },
          "hooks": { "sessionStart": "hooks/start.ps1", "runAs": "user" },
          "apps": [ { "name": "Demo app", "cmd": "{ext}\\demo.exe" } ],
          "panel": { "title": "Demo", "entry": "panel/index.html" }
        }
        """;

    [Fact]
    public void Manifest_ParsesAndListsCapabilities()
    {
        var m = ExtensionFormat.ParseManifest(Full);
        Assert.Equal("demo-ext", m.Id);
        Assert.Equal(["component", "hooks", "apps", "panel"], m.Capabilities);
        Assert.Equal("user", m.Hooks!.RunAs);
        Assert.Equal("hooks/start.ps1", m.Hooks.For(ExtensionEvent.SessionStart));
    }

    [Theory]
    [InlineData("""{ "id": "Bad_Id", "name": "x", "apps": [ { "name": "a" } ] }""")]
    [InlineData("""{ "id": "ok", "name": "", "apps": [ { "name": "a" } ] }""")]
    [InlineData("""{ "id": "ok", "name": "x" }""")]
    [InlineData("""{ "id": "ok", "name": "x", "component": { "install": "../evil.ps1" } }""")]
    [InlineData("""{ "id": "ok", "name": "x", "component": { "install": "install.exe" } }""")]
    [InlineData("""{ "id": "ok", "name": "x", "hooks": { "sessionStart": "a.ps1", "runAs": "admin" } }""")]
    [InlineData("""{ "id": "ok", "name": "x", "apps": [ { "name": "a" }, { "name": "A" } ] }""")]
    [InlineData("""{ "id": "ok", "name": "x", "panel": { "title": "t", "entry": "C:/x.html" } }""")]
    [InlineData("""{ "schema": 2, "id": "ok", "name": "x", "apps": [ { "name": "a" } ] }""")]
    [InlineData("""not json""")]
    public void Manifest_RejectsInvalid(string json) =>
        Assert.Throws<ExtensionFormatException>(() => ExtensionFormat.ParseManifest(json));

    [Fact]
    public void Catalog_ValidatesEntries()
    {
        var c = ExtensionFormat.ParseCatalog("""
            { "name": "Mine", "extensions": [
              { "repo": "owner/a" },
              { "repo": "https://github.com/owner/b", "path": "ext/one", "ref": "v1", "sha256": "%%" }
            ] }
            """.Replace("%%", new string('a', 64)));
        Assert.Equal(2, c.Extensions.Count);
        Assert.Throws<ExtensionFormatException>(() => ExtensionFormat.ParseCatalog("""{ "extensions": [ { "repo": "nope" } ] }"""));
        Assert.Throws<ExtensionFormatException>(() => ExtensionFormat.ParseCatalog("""{ "extensions": [ { "repo": "o/r", "sha256": "123" } ] }"""));
    }

    [Fact]
    public void MinVersion_IsChecked()
    {
        var m = ExtensionFormat.ParseManifest("""{ "id": "x", "name": "x", "minOsmsVersion": "2.0.0", "apps": [ { "name": "a" } ] }""");
        Assert.False(ExtensionFormat.SupportsVersion(m, new Version(1, 9, 9)));
        Assert.True(ExtensionFormat.SupportsVersion(m, new Version(2, 0, 0)));
    }
}

public class ExtensionPackageTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("osms-ext-test-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string Zip(params (string Name, string Content)[] entries)
    {
        var file = Path.Combine(_dir, $"{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
        return file;
    }

    [Fact]
    public void Extract_StripsGitHubTopFolder()
    {
        var zip = Zip(("owner-repo-abc123/osms-extension.json", "{}"), ("owner-repo-abc123/scripts/a.ps1", "x"));
        var dest = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        GitHubSource.Extract(zip, dest);
        Assert.True(File.Exists(Path.Combine(dest, "osms-extension.json")));
        Assert.True(File.Exists(Path.Combine(dest, "scripts", "a.ps1")));
    }

    [Fact]
    public void Extract_TakesOnlyTheSubfolder()
    {
        var zip = Zip(("top/README.md", "r"), ("top/exts/one/osms-extension.json", "{}"), ("top/exts/two/osms-extension.json", "{}"));
        var dest = Directory.CreateDirectory(Path.Combine(_dir, "sub")).FullName;
        GitHubSource.Extract(zip, dest, "exts/one");
        Assert.Equal(["osms-extension.json"], Directory.GetFiles(dest).Select(Path.GetFileName));
        Assert.Throws<ExtensionFormatException>(() =>
            GitHubSource.Extract(zip, Directory.CreateDirectory(Path.Combine(_dir, "none")).FullName, "exts/three"));
    }

    [Fact]
    public void Extract_RejectsPathsOutsideTheFolder()
    {
        var zip = Zip(("a.txt", "a"), ("../evil.txt", "x"));
        var dest = Directory.CreateDirectory(Path.Combine(_dir, "slip")).FullName;
        Assert.Throws<ExtensionFormatException>(() => GitHubSource.Extract(zip, dest));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }
}

public class ExtensionAppsTests
{
    static JsonObject App(string name, string? ext = null)
    {
        var o = new JsonObject { ["name"] = name, ["cmd"] = name + ".exe" };
        if (ext is not null) o[SunshineConfigurator.ExtensionAppKey] = ext;
        return o;
    }

    [Fact]
    public void Merge_ReplacesExtensionAppsAndKeepsTheUsers()
    {
        var apps = new JsonArray(App("Desktop"), App("Old", "gone"), App("Playnite"), App("Steam Big Picture"));
        var ext = new List<(string, JsonObject)> { ("playnite", App("Playnite")) };

        Assert.True(SunshineConfigurator.MergeExtensionApps(apps, ext));
        var names = apps.Select(a => a!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Desktop", "Steam Big Picture", "Playnite"], names);
        Assert.Equal("playnite", apps[2]![SunshineConfigurator.ExtensionAppKey]!.GetValue<string>());

        // Otra vez lo mismo: no cambia nada (no se reescribe apps.json en cada arranque).
        Assert.False(SunshineConfigurator.MergeExtensionApps(apps, ext));

        // Sin extensiones: se quitan las suyas y se quedan las del usuario.
        Assert.True(SunshineConfigurator.MergeExtensionApps(apps, []));
        Assert.Equal(["Desktop", "Steam Big Picture"], apps.Select(a => a!["name"]!.GetValue<string>()));
    }
}

/// <summary>Flujo completo contra un GitHub simulado: vista previa, instalación, apps, catálogo y desinstalación.</summary>
public class ExtensionManagerTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("osms-ext-mgr-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    const string Commit = "0123456789abcdef0123456789abcdef01234567";

    sealed class FakeGitHub(Dictionary<string, (HttpStatusCode, byte[])> routes) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var key = req.RequestUri!.GetLeftPart(UriPartial.Path);
            Requests.Add(key);
            var (status, body) = routes.TryGetValue(key, out var r) ? r : (HttpStatusCode.NotFound, "{}"u8.ToArray());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] PackageZip(string manifest)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in new[]
                     {
                         ($"owner-demo-{Commit[..7]}/osms-extension.json", manifest),
                         ($"owner-demo-{Commit[..7]}/README.md", "demo"),
                     })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    (ExtensionManager Manager, FakeGitHub GitHub, string Sha) Create(string? catalogSha = null)
    {
        var pkg = PackageZip("""{ "id": "demo", "name": "Demo", "author": "Tester", "apps": [ { "name": "Demo app", "cmd": "{ext}\\demo.exe" } ] }""");
        var sha = Convert.ToHexStringLower(SHA256.HashData(pkg));
        var catalog = $$"""{ "name": "Test catalog", "extensions": [ { "repo": "owner/demo", "id": "demo", "sha256": "{{catalogSha ?? sha}}" } ] }""";
        var routes = new Dictionary<string, (HttpStatusCode, byte[])>
        {
            ["https://api.github.com/repos/owner/demo"] = (HttpStatusCode.OK, Json("""{ "default_branch": "main" }""")),
            ["https://api.github.com/repos/owner/demo/commits/main"] = (HttpStatusCode.OK, Json($$"""{ "sha": "{{Commit}}" }""")),
            [$"https://api.github.com/repos/owner/demo/zipball/{Commit}"] = (HttpStatusCode.OK, pkg),
            ["https://api.github.com/repos/owner/catalog/contents/osms-catalog.json"] =
                (HttpStatusCode.OK, Json($$"""{ "type": "file", "content": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes(catalog))}}" }""")),
        };
        var gh = new FakeGitHub(routes);
        return (new ExtensionManager(_root, new GitHubSource(gh), new Version(1, 6, 0)), gh, sha);
    }

    static async Task WaitIdle(ExtensionManager m)
    {
        for (int i = 0; i < 200 && m.IsBusy; i++) await Task.Delay(25);
        Assert.False(m.IsBusy);
    }

    [Fact]
    public async Task PreviewInstallAppsUninstall()
    {
        var (m, gh, sha) = Create();

        // El catálogo oficial se añade una vez.
        Assert.Contains(ExtensionManager.OfficialCatalog, m.CatalogUrls());

        var p = await m.PreviewAsync(new PreviewRequest("https://github.com/owner/demo"));
        Assert.Equal("demo", p.Id);
        Assert.Equal(sha, p.Sha256);
        Assert.Equal(Commit, p.Commit);
        Assert.False(p.IsRelease);
        Assert.False(p.Sha256Pinned);
        Assert.Equal(["apps"], p.Capabilities);
        Assert.Empty(m.GetAll().Installed);   // la vista previa no instala nada

        // Con un hash distinto del que se vio, no se instala.
        Assert.Throws<ExtensionException>(() => m.StartInstall(p.Token, new string('0', 64), false));

        m.StartInstall(p.Token, sha, false);
        await WaitIdle(m);
        var op = m.GetAll().Operation!;
        Assert.True(op.State == "ok", op.Message);

        var x = Assert.Single(m.GetAll().Installed);
        Assert.Equal(("demo", Commit, sha), (x.Id, x.Commit, x.Sha256));
        Assert.True(File.Exists(Path.Combine(m.PackageDir("demo"), "osms-extension.json")));

        var (extId, app) = Assert.Single(m.GetSunshineApps());
        Assert.Equal("demo", extId);
        Assert.Equal(Path.Combine(m.PackageDir("demo"), "demo.exe"), app["cmd"]!.GetValue<string>());

        m.SetEnabled("demo", false);
        Assert.Empty(m.GetSunshineApps());

        // El token ya se usó.
        Assert.Throws<ExtensionException>(() => m.StartInstall(p.Token, sha, false));

        m.StartUninstall("demo");
        await WaitIdle(m);
        Assert.Empty(m.GetAll().Installed);
        Assert.False(Directory.Exists(Path.Combine(_root, "demo")));
        Assert.All(gh.Requests, u => Assert.StartsWith("https://api.github.com/", u));
    }

    [Fact]
    public async Task CatalogPinnedHash_MustMatch()
    {
        var (good, _, _) = Create();
        await good.AddCatalogAsync("https://github.com/owner/catalog");
        var p = await good.PreviewAsync(new PreviewRequest("owner/demo", CatalogUrl: "https://github.com/owner/catalog"));
        Assert.True(p.Sha256Pinned);

        Directory.Delete(_root, true);
        Directory.CreateDirectory(_root);
        var (bad, _, _) = Create(catalogSha: new string('f', 64));
        await bad.AddCatalogAsync("https://github.com/owner/catalog");
        await Assert.ThrowsAsync<ExtensionException>(() =>
            bad.PreviewAsync(new PreviewRequest("owner/demo", CatalogUrl: "https://github.com/owner/catalog")));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(_root, "_staging")).Any());
    }

    [Fact]
    public async Task PanelFiles_NeedTheToken()
    {
        var (m, _, _) = Create();
        Assert.Null(m.ResolvePanelFile("demo", "x", "panel/index.html"));
        await Task.CompletedTask;
    }
}
