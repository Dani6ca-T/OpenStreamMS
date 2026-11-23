using System.Text.Json.Nodes;
using OpenStreamMS.Services.Sunshine;
using Xunit;

namespace OpenStreamMS.Tests;

/// <summary>
/// La app Steam de una sesión compuesta abre y cierra Big Picture en todas sus sesiones a través
/// del servicio, no solo en la sesión de su Sunshine.
/// </summary>
public sealed class CompositeSteamEntryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osms-compsteam-" + Guid.NewGuid().ToString("N"));
    private string Exe => Path.Combine(_dir, "sunshine.exe");
    private string Apps => Path.Combine(_dir, "config", "apps.json");
    private string Script => Path.Combine(_dir, SunshineConfigurator.CompositeSteamScript);
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public CompositeSteamEntryTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        File.WriteAllText(Script, "");
        File.WriteAllText(Path.Combine(_dir, "assets", "apps.json"), """
            {"apps":[
              {"name":"Desktop","image-path":"desktop.png"},
              {"name":"Steam Big Picture","cmd":"steam://open/bigpicture",
               "prep-cmd":[{"do":"","undo":"steam://close/bigpicture"}]}
            ]}
            """);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private JsonObject Steam() =>
        JsonNode.Parse(File.ReadAllText(Apps))!["apps"]!.AsArray()
            .Select(a => a!.AsObject()).Single(a => a["name"]!.GetValue<string>() == "Steam Big Picture");

    [Fact]
    public void SteamOpensAndClosesThroughTheService()
    {
        SunshineConfigurator.ConfigureCompositeSteamEntry(Exe, Id, Script);
        var steam = Steam();
        Assert.Contains($"-Composite {Id} -Action open", steam["cmd"]!.GetValue<string>());
        Assert.Contains($"-Composite {Id} -Action close", steam["prep-cmd"]![0]!["undo"]!.GetValue<string>());
    }

    [Fact]
    public void IsIdempotentAndSurvivesTheNormalSteamEntry()
    {
        SunshineConfigurator.ConfigureCompositeSteamEntry(Exe, Id, Script);
        SunshineConfigurator.ConfigureSteamEntry(Exe);        // lo que hace SunshineManager al arrancar
        SunshineConfigurator.ConfigureCompositeSteamEntry(Exe, Id, Script);
        var steam = Steam();
        Assert.Contains("-Action open", steam["cmd"]!.GetValue<string>());
        Assert.Single(steam["prep-cmd"]!.AsArray());
    }

    [Fact]
    public void OtherAppsAreNotTouched()
    {
        SunshineConfigurator.ConfigureCompositeSteamEntry(Exe, Id, Script);
        var desktop = JsonNode.Parse(File.ReadAllText(Apps))!["apps"]![0]!.AsObject();
        Assert.Null(desktop["cmd"]);
    }
}
