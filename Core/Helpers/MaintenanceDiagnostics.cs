using System.Text.Json;

namespace OpenStreamMS.Core.Helpers;

public sealed record ResidualMaintenanceState(string UsbCapture, int? UsbCount, string ConsoleLock)
{
    public bool NeedsAttention => UsbCapture != "clear" || ConsoleLock != "clear";
}

/// <summary>Reads recovery journals only. A journal is evidence of pending work,
/// not proof of the current device/registry state. Never invokes recovery helpers.</summary>
public static class MaintenanceDiagnostics
{
    public static ResidualMaintenanceState Read() => ReadFromDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS"));

    internal static ResidualMaintenanceState ReadFromDirectory(string directory)
    {
        (string state, int? count) usb = ("unknown", null);
        var console = "unknown";
        try
        {
            using var document = ReadJournal(Path.Combine(directory, "usb-capture.json"));
            if (document is null) usb = ("clear", 0);
            else if (document.RootElement.ValueKind == JsonValueKind.Array &&
                     document.RootElement.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
            {
                int count = document.RootElement.GetArrayLength();
                usb = (count == 0 ? "clear" : "pending", count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        try
        {
            using var document = ReadJournal(Path.Combine(directory, "console-lock.json"));
            if (document is null) console = "clear";
            else if (document.RootElement.ValueKind == JsonValueKind.Object &&
                     document.RootElement.TryGetProperty("Sid", out var sid) && sid.ValueKind == JsonValueKind.String &&
                     document.RootElement.TryGetProperty("Previous", out var previous) &&
                     previous.ValueKind is JsonValueKind.Number or JsonValueKind.Null)
                console = "pending";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        return new(usb.state, usb.count, console);
    }

    private static JsonDocument? ReadJournal(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024) throw new InvalidDataException("Recovery journal exceeds diagnostic limit.");
            return JsonDocument.Parse(stream);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
