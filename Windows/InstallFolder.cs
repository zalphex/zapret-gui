using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

internal static class InstallFolder
{
    private const string Key = @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretGUI";
    internal static readonly string Previous = (string)Registry.GetValue(Key, "RuntimeRoot", null);
    internal static readonly string OldRoot = Previous ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "zapretgui");
    internal static readonly string OldDesktop = Previous ?? Path.Combine(OldRoot, "Desktop");
    private static readonly SecurityIdentifier Admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemId = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    private static bool Trusted(IdentityReference id)
    {
        return id.Equals(Admin) || id.Equals(SystemId) || id.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    }

    private static bool IsProtected(DirectoryInfo dir, bool runtime)
    {
        var acl = dir.GetAccessControl();
        var rights = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles;
        if (dir.Parent != null) rights |= FileSystemRights.Delete;
        if (runtime) rights |= FileSystemRights.Write;
        if ((dir.Attributes & FileAttributes.ReparsePoint) == 0 && Trusted(acl.GetOwner(typeof(SecurityIdentifier))) &&
            !acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
                rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & rights) != 0 && !Trusted(rule.IdentityReference))) return true;
        return false;
    }

    private static void CheckDirectory(DirectoryInfo dir, bool runtime)
    {
        if (IsProtected(dir, runtime)) return;
        throw new IOException("Windows разрешает подмену файлов программы в этой папке. Выбери отдельную защищённую папку на нужном диске. Путь: " + dir.FullName);
    }

    internal static void Prepare(string path)
    {
        var dir = new DirectoryInfo(path);
        if (dir.Name != "zapretgui.runtime" || new DriveInfo(dir.Root.FullName).DriveType != DriveType.Fixed)
            throw new IOException("Выбери папку на локальном постоянном диске.");
        PrepareSelected(dir.Parent);
        if (!dir.Exists)
        {
            dir.Create(ProtectedAcl());
        }
        CheckDirectory(dir, true);
        if (!File.Exists(Path.Combine(dir.FullName, ".install-root")) && dir.EnumerateFileSystemInfos().Any())
            throw new IOException("Подпапка zapretgui.runtime уже занята. Выбери другую папку.");
        Disk.Write(Path.Combine(dir.FullName, ".install-root"), new byte[0]);
    }

    private static void PrepareSelected(DirectoryInfo selected)
    {
        if (selected.Parent == null)
        {
            CheckDirectory(selected, false);
            return;
        }
        EnsureParent(selected.Parent);
        if (!selected.Exists) selected.Create(ProtectedAcl());
        if (IsProtected(selected, false)) return;
        if ((selected.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Выбранная папка является ссылкой: " + selected.FullName);

        if (selected.EnumerateFileSystemInfos().Any())
            throw new IOException("В выбранной папке уже есть файлы. Выбери новую подпапку на этом же диске, чтобы сохранить их.");
        string path = selected.FullName;
        string saved = Path.Combine(selected.Parent.FullName, "zapretgui-before-install-" + Guid.NewGuid().ToString("N"));
        Directory.Move(path, saved);
        try
        {
            Directory.CreateDirectory(path, ProtectedAcl());
            CheckDirectory(new DirectoryInfo(path), true);
        }
        catch
        {
            if (!Directory.Exists(path)) Directory.Move(saved, path);
            throw;
        }
        if (!Directory.EnumerateFileSystemEntries(saved).Any()) Directory.Delete(saved);
    }

    private static DirectorySecurity ProtectedAcl()
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(Admin);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { Admin, SystemId })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    private static void EnsureParent(DirectoryInfo dir)
    {
        if (dir.Parent != null) EnsureParent(dir.Parent);
        if (!dir.Exists) dir.Create(ProtectedAcl());
        CheckDirectory(dir, false);
    }

}
