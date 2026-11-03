namespace OpenStreamMS.Core.Helpers;

// This deployment has no updater module and no writable switch for system repair.
// Every process restart keeps these restrictions, even without a policy JSON file.
public sealed class StartupSafetyPolicy
{
    public bool RestrictedStartup => true;
    public bool AutomaticRdpRepairEnabled => false;
    public static StartupSafetyPolicy Read() => new();
}
