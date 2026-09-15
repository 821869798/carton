using System;
using System.Collections.Generic;

namespace carton.GUI.Services;

/// <summary>
/// Recognises installs owned by a system package manager (.deb / .rpm / AUR) and maps the
/// distro to what the user should actually run.
///
/// Those packages deliberately ship without <c>carton-helper</c>, which already turns the
/// in-app updater off (see <see cref="IAppUpdateService.SupportsDirectPortableUpdates"/>);
/// this type only decides what to tell the user instead of "download it from the releases
/// page", which is a dead end for a package-managed build.
///
/// Note the difference between the two cases below: Carton publishes no apt/dnf repository,
/// so `apt upgrade carton` / `dnf upgrade carton` would just fail - for those the instruction
/// is to install the newly downloaded package over the current one. Arch has an online source
/// (the AUR), so there the concrete upgrade command is correct.
///
/// Kept free of IO and Avalonia types so carton.GUI.Tests can link this file the way
/// DelayText and TestingTagRefCounts are linked: callers pass the app directory and the
/// contents of /etc/os-release.
/// </summary>
internal static class PackageManagedInstall
{
    // Packages install the application under /usr (deb/rpm: /usr/lib/carton, with
    // /usr/bin/carton as a symlink). Portable and AppImage builds never live there.
    internal static bool IsSystemInstall(string? appDirectory)
        => !string.IsNullOrWhiteSpace(appDirectory)
           && appDirectory.StartsWith("/usr/", StringComparison.Ordinal);

    /// <summary>
    /// Package manager for the distribution described by <paramref name="osRelease"/> (a
    /// /etc/os-release body), or null when the distribution is not recognised.
    /// <c>Manager</c> is a display name; <c>UpgradeCommand</c> is set only when the
    /// distribution has an online source that can perform the upgrade itself.
    /// </summary>
    internal static (string Manager, string? UpgradeCommand)? ResolveHint(string? osRelease)
    {
        var ids = ParseIds(osRelease);
        if (ids.Count == 0)
        {
            return null;
        }

        if (Overlaps(ids, ArchIds))
        {
            // AUR packages are upgraded by an AUR helper, not by pacman alone.
            return ("AUR", "yay -Syu");
        }

        if (Overlaps(ids, DebianIds))
        {
            return ("apt", null);
        }

        if (Overlaps(ids, SuseIds))
        {
            return ("zypper", null);
        }

        if (Overlaps(ids, RpmIds))
        {
            return ("dnf", null);
        }

        return null;
    }

    // ID_LIKE carries the family for derivatives (Ubuntu -> debian, CachyOS -> arch,
    // Tumbleweed -> opensuse suse), so both keys are collected.
    private static HashSet<string> ParseIds(string? osRelease)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(osRelease))
        {
            return ids;
        }

        foreach (var line in osRelease.Split('\n'))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            if (!key.Equals("ID", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("ID_LIKE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in trimmed[(separator + 1)..].Trim().Trim('"', '\'').Split(' ', '\t'))
            {
                if (value.Length > 0)
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    private static bool Overlaps(HashSet<string> ids, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (ids.Contains(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] ArchIds =
        ["arch", "manjaro", "endeavouros", "cachyos", "garuda", "artix"];

    private static readonly string[] DebianIds =
        ["debian", "ubuntu", "linuxmint", "pop", "raspbian", "kali", "zorin"];

    private static readonly string[] SuseIds =
        ["opensuse", "opensuse-leap", "opensuse-tumbleweed", "sles", "suse"];

    private static readonly string[] RpmIds =
        ["fedora", "rhel", "centos", "rocky", "almalinux", "ol", "amzn"];
}
