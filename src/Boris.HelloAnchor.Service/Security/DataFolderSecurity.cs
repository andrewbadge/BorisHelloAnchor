// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Security.AccessControl;
using System.Security.Principal;
using Boris.HelloAnchor.Core;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace Boris.HelloAnchor.Service.Security;

/// <summary>
/// Protects <c>%ProgramData%\Boris\HelloAnchor</c> against tampering by standard users (SPEC §6.4, §8.2).
/// </summary>
/// <remarks>
/// <para>
/// The default <c>%ProgramData%</c> ACL lets any user create a sub-folder, and whoever creates it becomes the
/// owner (who can always rewrite its ACL). A standard user could therefore pre-create our folder or
/// <c>config.json</c> before install and later edit the configuration that elevated code reads. They could
/// also plant hard links or junctions so that SYSTEM writes (log files, ACL changes) land somewhere else.
/// </para>
/// <para>
/// Two defences:
/// <list type="bullet">
/// <item><see cref="Enforce"/> runs at every service start (as SYSTEM) and resets ownership and ACLs, removing
/// links, junctions and foreign-owned files.</item>
/// <item><see cref="IsTrusted"/> is checked right before honouring <c>AllowSystemTokenFallback</c>.</item>
/// </list>
/// The installer also applies the same ACL, but it cannot re-secure a pre-existing <c>config.json</c>
/// because <c>NeverOverwrite</c> skips that component, hence the runtime enforcement.
/// </para>
/// </remarks>
internal static class DataFolderSecurity
{
    /// <summary>
    /// Protected DACL for our folders: SYSTEM and Administrators full control, Users read &amp; execute,
    /// inherited by children; owner Administrators.
    /// </summary>
    internal const string FolderSddl = "O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    /// <summary>Access-mask bits that let a principal change a file or folder or its security.</summary>
    private const int WriteMask =
        (int)(FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
              FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
              FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) |
        0x10000000 /* GENERIC_ALL */ | 0x40000000 /* GENERIC_WRITE */;

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>NT SERVICE\TrustedInstaller.</summary>
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>
    /// Re-secures the data folders and files. Returns a list of actions/problems for the caller to log
    /// (logging isn't available yet: this runs before the file logger opens its first file).
    /// </summary>
    public static List<string> Enforce()
    {
        var messages = new List<string>();

        // SYSTEM holds these privileges but they are disabled by default. They let us take ownership of an
        // object whose DACL doesn't grant us anything (e.g. a folder a user pre-created and locked down).
        if (!Privileges.TryEnable(Privileges.TakeOwnership) | !Privileges.TryEnable(Privileges.Restore))
        {
            messages.Add("Could not enable SeTakeOwnershipPrivilege/SeRestorePrivilege; folder hardening may be incomplete.");
        }

        try
        {
            // Folders, outermost first: %ProgramData%\Boris, ...\HelloAnchor, ...\HelloAnchor\logs.
            SecureFolder(HelloAnchorPaths.CompanyDataDirectory, protect: true, messages);
            SecureFolder(HelloAnchorPaths.DataDirectory, protect: true, messages);
            SecureFolder(HelloAnchorPaths.LogDirectory, protect: false, messages);

            // Every file we (or a SYSTEM agent) will read or append to.
            SecureFiles(HelloAnchorPaths.DataDirectory, messages);
            SecureFiles(HelloAnchorPaths.LogDirectory, messages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            messages.Add($"Folder hardening failed: {ex.Message}");
        }

        return messages;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the data folder and config file are owned by SYSTEM/Administrators,
    /// grant no write-type rights to anyone else, and are not links (SPEC §6.4).
    /// </summary>
    /// <param name="problem">Why the check failed.</param>
    public static bool IsTrusted(out string problem)
    {
        try
        {
            foreach (var folder in new[] { HelloAnchorPaths.CompanyDataDirectory, HelloAnchorPaths.DataDirectory })
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists)
                {
                    problem = $"'{folder}' does not exist.";
                    return false;
                }

                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    problem = $"'{folder}' is a junction or symbolic link.";
                    return false;
                }

                if (!IsTrustedDescriptor(info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), folder, out problem))
                {
                    return false;
                }
            }

            var config = new FileInfo(HelloAnchorPaths.ConfigFile);
            if (config.Exists)
            {
                if (config.Attributes.HasFlag(FileAttributes.ReparsePoint) || TryGetLinkCount(config.FullName) is not 1)
                {
                    problem = $"'{config.FullName}' is a link.";
                    return false;
                }

                if (!IsTrustedDescriptor(config.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), config.FullName, out problem))
                {
                    return false;
                }
            }

            problem = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            problem = $"Could not read security: {ex.Message}";
            return false;
        }
    }

    /// <summary>Checks owner and ACEs of one object.</summary>
    private static bool IsTrustedDescriptor(FileSystemSecurity security, string path, out string problem)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrustedPrincipal(owner))
        {
            problem = $"'{path}' is owned by {owner?.Value ?? "nobody"}.";
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            // Inherit-only ACEs don't apply to this object itself.
            if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            if (((int)rule.FileSystemRights & WriteMask) != 0 && !IsTrustedPrincipal((SecurityIdentifier)rule.IdentityReference))
            {
                problem = $"'{path}' grants write access to {rule.IdentityReference.Value}.";
                return false;
            }
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>SYSTEM, Administrators and TrustedInstaller are the only principals allowed to modify our data.</summary>
    private static bool IsTrustedPrincipal(SecurityIdentifier sid) =>
        sid == System || sid == Administrators || sid == TrustedInstaller;

    /// <summary>
    /// Makes sure <paramref name="path"/> is a real folder (not a junction) owned by Administrators, with
    /// either the protected <see cref="FolderSddl"/> DACL or a clean inherited one.
    /// </summary>
    private static void SecureFolder(string path, bool protect, List<string> messages)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // A junction could redirect our writes anywhere. Delete the link itself (never its target).
            info.Delete();
            messages.Add($"Removed junction/symlink at '{path}'.");
        }

        Directory.CreateDirectory(path);

        // Owner first: once we own it we implicitly have WRITE_DAC, whatever the current DACL says.
        var ownerOnly = new DirectorySecurity();
        ownerOnly.SetOwner(Administrators);
        info.SetAccessControl(ownerOnly);

        var access = new DirectorySecurity();
        if (protect)
        {
            access.SetSecurityDescriptorSddlForm(FolderSddl, AccessControlSections.Access);
        }
        else
        {
            // No explicit ACEs, inheritance on: effective ACL is exactly what the parent grants.
            access.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        }

        info.SetAccessControl(access);
    }

    /// <summary>
    /// Resets every file directly inside <paramref name="folder"/> to Administrators ownership with inherited
    /// ACLs. Links are removed rather than re-secured, because changing a hard link's ACL changes its target's.
    /// </summary>
    private static void SecureFiles(string folder, List<string> messages)
    {
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || TryGetLinkCount(file.FullName) is not 1)
            {
                Quarantine(file, "is a link (or could not be inspected)", messages);
                continue;
            }

            try
            {
                var ownerOnly = new FileSecurity();
                ownerOnly.SetOwner(Administrators);
                file.SetAccessControl(ownerOnly);

                var access = new FileSecurity();
                access.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                file.SetAccessControl(access);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
            {
                Quarantine(file, $"could not be re-secured ({ex.Message})", messages);
            }
        }
    }

    /// <summary>
    /// Deletes a suspicious file. Deleting only needs FILE_DELETE_CHILD on the (now secured) parent folder,
    /// so it works even when the file's own DACL denies us. For a hard link, only the link name is removed.
    /// </summary>
    private static void Quarantine(FileInfo file, string reason, List<string> messages)
    {
        try
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
            messages.Add($"Deleted '{file.FullName}' because it {reason}.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            messages.Add($"'{file.FullName}' {reason} and could not be deleted: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the number of hard links to a file (1 for a normal file), or <see langword="null"/> if the file
    /// cannot be opened — which callers treat as suspicious.
    /// </summary>
    private static uint? TryGetLinkCount(string path)
    {
        try
        {
            // FileShare.* so we never block (or are blocked by) the logger that may have the file open.
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return PInvoke.GetFileInformationByHandle(handle, out var info) ? info.nNumberOfLinks : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
