using System.Reflection;

namespace OpenStreamMS.Tests;

public class DiagnosticsControllersTests
{
    [Fact]
    public void Controllers_ListsHidHideFiltersAndControllerTrees()
    {
        var type = typeof(OpenStreamMS.Core.Api.StreamSessionService).Assembly
            .GetType("OpenStreamMS.Core.Helpers.DiagnosticsBundle", throwOnError: true)!;
        var method = type.GetMethod("Controllers", BindingFlags.NonPublic | BindingFlags.Static)!;

        var text = (string)method.Invoke(null, null)!;

        Assert.Contains("== Filtro de HidHide por clase (UpperFilters) ==", text);
        Assert.Contains("XboxComposite (Xbox One/GIP)", text);
        Assert.Contains("== Configuración de HidHide (registro) ==", text);
        Assert.Contains("== Mandos conectados (árbol completo de cada uno) ==", text);
        Assert.Contains("== Otros nodos de la pila de mandos de Windows ==", text);
    }
}
