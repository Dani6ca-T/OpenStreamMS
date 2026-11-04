using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenStreamMS.Core.Helpers;

public enum OperationMode { Normal, Safe }
public sealed class MaintenanceModeException(string operation) : InvalidOperationException(operation)
{
    public const string Code = "maintenance_mode";
}

/// <summary>Immutable for the lifetime of a process. Pending changes activate only at SCM startup.</summary>
public sealed record StartupSafetyPolicy(OperationMode Mode, string? Diagnostic = null)
{
    private static readonly object Sync = new();
    private static StartupSafetyPolicy? _current;
    private static bool _clientRuntime;
    public bool RestrictedStartup => Mode == OperationMode.Safe;
    public bool AutomaticRdpRepairEnabled => !RestrictedStartup;
    public static StartupSafetyPolicy Read()
    {
        lock (Sync)
        {
            if (_clientRuntime) return ReadClient();
            if (_current is not null) return _current;
            _clientRuntime = true;
            return ReadClient();
        }
    }
    private static StartupSafetyPolicy ReadClient()
    {
        var policy = new ModeConfiguration(AppContext.BaseDirectory).Read().Policy;
        var runtime = RuntimeMode.Read(AppContext.BaseDirectory);
        // A live service restriction also binds privileged CLI clients. An unreadable private
        // file is expected for ordinary Tray users, who consume only the service-owned status.
        return ResolveClient(policy, runtime, RuntimeMode.OwnServiceMayBeRunning);
    }
    internal static StartupSafetyPolicy ResolveClient(StartupSafetyPolicy policy, StartupSafetyPolicy runtime, Func<bool> serviceMayBeRunning)
    {
        if (policy.Diagnostic == "policy_access_denied") return runtime;
        if (policy.RestrictedStartup) return policy;
        if (runtime.Diagnostic is null) return runtime;
        return serviceMayBeRunning() ? new(OperationMode.Safe, "runtime_unavailable") : policy;
    }
    public static void Initialize(bool scmStartup)
    {
        lock (Sync)
        {
            if (!scmStartup) { _clientRuntime = true; _current = null; return; }
            var store = new ModeConfiguration(AppContext.BaseDirectory);
            try { _current = (scmStartup ? store.ActivatePending() : store.Read()).Policy; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _current = new(OperationMode.Safe, "policy_activation_failed"); }
            _clientRuntime = false;
        }
    }
    public static void RequireNormal(string operation)
    {
        if (Read().RestrictedStartup) throw new MaintenanceModeException(operation);
    }
    public static bool CommandAllowed(string command, OperationMode mode) => mode == OperationMode.Normal ||
        command is "--run" or "--tray" or "--open" or "--help" or "-h" or "--prepare-safe-mode" or "--check-maintenance";
}

public sealed record ModeState(StartupSafetyPolicy Policy, OperationMode? PendingMode, long Revision);
public sealed record ModeDocument(int Version, OperationMode OperationMode, OperationMode? PendingMode, long Revision);

/// <summary>No Windows repair capabilities. A durable anchor makes a missing/corrupt policy fail closed.</summary>
public sealed class ModeConfiguration
{
    public const string FileName = "operation-mode.json";
    public const string AnchorName = "operation-mode.required";
    private readonly string _directory;
    private readonly Action<string> _protect;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        Converters = { new JsonStringEnumConverter<OperationMode>(allowIntegerValues: false) } };
    public ModeConfiguration(string directory) : this(directory, AtomicProtectedFile.Protect) { }
    internal ModeConfiguration(string directory, Action<string> protect)
    { _directory = Path.GetFullPath(directory); _protect = protect; }
    private string PathFor(string name) => Path.Combine(_directory, name);
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    public ModeState Read()
    {
        try
        {
            if (!Exists(PathFor(FileName))) return Exists(PathFor(AnchorName))
                ? new(new(OperationMode.Safe, "policy_missing"), null, 0)
                : new(new(OperationMode.Normal), null, 0); // existing upstream installations
            var text = File.ReadAllText(PathFor(FileName));
            var document = JsonSerializer.Deserialize<ModeDocument>(text, Json);
            using var parsed = JsonDocument.Parse(text);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a policy object");
            if (parsed.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
                throw new InvalidDataException("Duplicate policy keys");
            if (document is null || document.Version != 1 || document.Revision < 1 ||
                !parsed.RootElement.TryGetProperty("OperationMode", out _) ||
                !Enum.IsDefined(document.OperationMode) ||
                (document.PendingMode is { } pending && !Enum.IsDefined(pending)))
                throw new InvalidDataException("Unsupported policy");
            return new(new(document.OperationMode), document.PendingMode, document.Revision);
        }
        catch (UnauthorizedAccessException) { return new(new(OperationMode.Safe, "policy_access_denied"), null, 0); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        { return new(new(OperationMode.Safe, "policy_unreadable"), null, 0); }
    }
    private IDisposable Lock()
    {
        Directory.CreateDirectory(_directory);
        var lockPath = PathFor("operation-mode.lock");
        using (var initialize = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            _protect(lockPath);
        return new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private ModeState Write(OperationMode mode, OperationMode? pending, long revision)
    {
        // Anchor FIRST: an interrupted first write must not fall back to legacy Normal.
        AtomicProtectedFile.Write(PathFor(AnchorName), "1", _protect);
        AtomicProtectedFile.Write(PathFor(FileName), JsonSerializer.Serialize(new ModeDocument(1, mode, pending, revision), Json), _protect);
        return new(new(mode), pending, revision);
    }
    public ModeState PrepareSafe()
    { using var held = Lock(); var state = Read(); return Write(OperationMode.Safe, null, state.Revision + 1); }
    public ModeState Request(OperationMode desired, long expectedRevision, bool confirmNormal)
    {
        if (!Enum.IsDefined(desired)) throw new ArgumentException("Unknown operation mode");
        using var held = Lock(); var state = Read();
        if (state.Revision != expectedRevision) throw new InvalidOperationException("policy_conflict");
        if (desired == OperationMode.Normal && !confirmNormal) throw new ArgumentException("normal_confirmation_required");
        // Committed mode stays unchanged. Other CLI processes cannot bypass a pending Safe→Normal restart.
        return Write(state.Policy.Mode, desired == state.Policy.Mode ? null : desired, state.Revision + 1);
    }
    public ModeState ActivatePending()
    {
        using var held = Lock(); var state = Read();
        return state.PendingMode is { } next ? Write(next, null, state.Revision + 1) : state;
    }
}

/// <summary>Non-secret, read-only service status for unprivileged Tray/seat processes.
/// The writer ACL is SYSTEM/Administrators; the PID AND creation time prevent stale PID reuse.
/// It is never used by the service to select its own mode.</summary>
public static class RuntimeMode
{
    public const string FileName = "operation-mode.runtime.json";
    internal static bool OwnServiceMayBeRunning()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\OpenStreamMS");
            var image = (key?.GetValue("ImagePath") as string)?.TrimStart('"');
            var ownExe = Path.Combine(AppContext.BaseDirectory, "OpenStreamMS.exe");
            if (image is null || !image.StartsWith(ownExe, StringComparison.OrdinalIgnoreCase)) return false;
            using var service = new System.ServiceProcess.ServiceController("OpenStreamMS");
            return service.Status != System.ServiceProcess.ServiceControllerStatus.Stopped;
        }
        catch { return true; } // uncertainty must not grant CLI/native capabilities
    }
    public static void Publish(StartupSafetyPolicy policy)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var text = JsonSerializer.Serialize(new { ActiveMode = policy.Mode.ToString(), ProcessId = process.Id,
            StartedAtUtcTicks = process.StartTime.ToUniversalTime().Ticks });
        AtomicProtectedFile.Write(Path.Combine(AppContext.BaseDirectory, FileName), text, path => {
            AtomicProtectedFile.Protect(path);
            var acl = new FileInfo(path).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
        });
    }
    public static StartupSafetyPolicy Read(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, FileName)));
            var root = document.RootElement;
            if (!Enum.TryParse<OperationMode>(root.GetProperty("ActiveMode").GetString(), out var mode) || !Enum.IsDefined(mode))
                throw new InvalidDataException();
            using var process = System.Diagnostics.Process.GetProcessById(root.GetProperty("ProcessId").GetInt32());
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != root.GetProperty("StartedAtUtcTicks").GetInt64())
                throw new InvalidDataException();
            return new(mode);
        }
        catch { return new(OperationMode.Safe, "runtime_unavailable"); }
    }
}

public static class AtomicProtectedFile
{
    public static void Protect(string path)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl); // fail explicitly; never persist an unprotected policy
    }
    public static void Write(string path, string content) => Write(path, content, Protect);
    internal static void Write(string path, string content, Action<string> protect)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (File.Create(temporary)) { }
            protect(temporary);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                writer.Write(content); writer.Flush(); stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
