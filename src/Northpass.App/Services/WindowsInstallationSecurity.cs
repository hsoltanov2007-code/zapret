using System.Security.AccessControl;
using System.Security.Principal;
using Northpass.Services.Installation;

namespace Northpass.Desktop;

// Does not change UAC, AV, firewall, boot configuration or driver signing policy.
public sealed class WindowsInstallationSecurity : IInstallationSecurity
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-Flowseal");
    public void PrepareRoot(string root)
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Engine installation requires Windows x64.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator rights are required to install and initialize the engine. Relaunch Northpass through its Windows UAC prompt.");
        SafeArchive.NoLinks(root);
        if (Directory.Exists(root)) { ValidateDirectory(root); return; }
        var parent = Path.GetDirectoryName(root)!;
        ValidateAccess(new DirectoryInfo(parent).GetAccessControl(), requireProtected: false);
        Directory.CreateDirectory(root);
        ProtectDirectory(root);
    }
    private static DirectorySecurity DirectoryAcl()
    {
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(Administrators);
        foreach (var sid in new[] { Administrators, SystemAccount, Users })
            acl.AddAccessRule(new FileSystemAccessRule(sid, sid == Users ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }
    public void ProtectDirectory(string directory)
    {
        SafeArchive.NoLinks(directory);
        new DirectoryInfo(directory).SetAccessControl(DirectoryAcl());
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            SafeArchive.NoLinks(entry);
            if (Directory.Exists(entry)) ProtectDirectory(entry); else ProtectFile(entry);
        }
    }
    public void ProtectFile(string file)
    {
        SafeArchive.NoLinks(file);
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(Administrators);
        foreach (var sid in new[] { Administrators, SystemAccount, Users })
            acl.AddAccessRule(new FileSystemAccessRule(sid, sid == Users ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(acl);
    }
    public void ValidateDirectory(string directory)
    {
        SafeArchive.NoLinks(directory);
        ValidateAccess(new DirectoryInfo(directory).GetAccessControl(), requireProtected: true);
    }
    public void ValidateFile(string file)
    {
        SafeArchive.NoLinks(file);
        ValidateAccess(new FileInfo(file).GetAccessControl(), requireProtected: true);
    }
    private static void ValidateAccess(FileSystemSecurity acl, bool requireProtected)
    {
        var owner = (SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))!;
        // Program Files is normally owned by TrustedInstaller; it is a trusted system identity.
        bool trustedOwner = owner == Administrators || owner == SystemAccount || !requireProtected && owner.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        if (!trustedOwner || requireProtected && !acl.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The engine directory owner or ACL is unsafe. Restore a protected installation with administrator rights.");
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & write) == 0 || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            string sid = rule.IdentityReference.Value;
            if (sid == Administrators.Value || sid == SystemAccount.Value || !requireProtected && sid == owner.Value) continue;
            throw new UnauthorizedAccessException("Untrusted users can modify the engine installation. Setup refuses to run binaries from it.");
        }
    }
}
