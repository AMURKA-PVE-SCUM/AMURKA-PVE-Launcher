using System.Diagnostics;
using Amurka.Shared.IO;
using AmurkaInstaller.Core.Domain.Enums;

namespace AmurkaInstaller.Core.Domain.Models;

/// <summary>
/// Domain model encapsulating installer configuration, execution mode, and user options.
/// </summary>
public sealed class InstallerOptions
{
    public InstallerMode Mode { get; init; }
    public string? TargetPath { get; init; }
    public bool CreateDesktopShortcut { get; init; } = true;
    public bool CreateStartMenuShortcut { get; init; } = true;
    public bool IsSilent { get; init; }

    public InstallerOptions(
        InstallerMode mode,
        string? targetPath = null,
        bool createDesktopShortcut = true,
        bool createStartMenuShortcut = true,
        bool isSilent = false)
    {
        Mode = mode;
        TargetPath = targetPath;
        CreateDesktopShortcut = createDesktopShortcut;
        CreateStartMenuShortcut = createStartMenuShortcut;
        IsSilent = isSilent;
    }

    /// <summary>
    /// Parses command-line arguments into a validated <see cref="InstallerOptions"/> instance.
    /// Safe against process handle leaks.
    /// </summary>
    public static InstallerOptions Parse(string[] args, string? processNameOverride = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        InstallerMode mode = InstallerMode.Install;
        string? targetPath = null;
        bool desktop = true;
        bool startMenu = true;
        bool silent = false;

        string? procName = processNameOverride;
        if (string.IsNullOrWhiteSpace(procName))
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                procName = current.ProcessName;
            }
            catch (InvalidOperationException)
            {
                procName = null;
            }
        }

        if (string.Equals(procName, "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            mode = InstallerMode.Uninstall;
        }

        foreach (string arg in args)
        {
            if (arg.Equals("/UNINSTALL", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-uninstall", StringComparison.OrdinalIgnoreCase))
            {
                mode = InstallerMode.Uninstall;
            }
            else if (arg.Equals("/UPDATE", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--update", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-update", StringComparison.OrdinalIgnoreCase))
            {
                mode = InstallerMode.Update;
            }
            else if (arg.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase))
            {
                string rawDir = arg["/DIR=".Length..].Trim('\"');
                var validated = PathSecurityGuard.ValidateInstallDirectory(rawDir);
                if (validated.IsSuccess)
                {
                    targetPath = validated.Value;
                }
            }
            else if (arg.Equals("/SILENT", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
            }
            else if (arg.Equals("/NODESKTOP", StringComparison.OrdinalIgnoreCase))
            {
                desktop = false;
            }
            else if (arg.Equals("/NOSTARTMENU", StringComparison.OrdinalIgnoreCase))
            {
                startMenu = false;
            }
        }

        return new InstallerOptions(mode, targetPath, desktop, startMenu, silent);
    }
}
