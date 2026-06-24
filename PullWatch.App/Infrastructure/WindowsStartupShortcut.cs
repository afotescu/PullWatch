using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PullWatch;

public sealed class WindowsStartupShortcut : IWindowsStartupShortcut
{
    private const string ShortcutFileName = "PullWatch.lnk";
    private const int PathBufferLength = 32767;

    public Task SyncAsync(StartupSettings settings)
    {
        if (!settings.StartWithWindows)
        {
            DeleteShortcut();
            return Task.CompletedTask;
        }

        var executablePath = GetExecutablePath();
        var shortcutPath = GetShortcutPath();
        var existingShortcut = InspectShortcut(shortcutPath);
        var action = StartupShortcutOwnership.Decide(
            settings.StartWithWindows,
            existingShortcut,
            executablePath,
            ApplicationVersion.Current
        );

        if (action == StartupShortcutOwnershipAction.WriteCurrentShortcut)
        {
            CreateShortcut(shortcutPath, executablePath);
        }

        return Task.CompletedTask;
    }

    private static void CreateShortcut(string shortcutPath, string executablePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        object? shellLinkObject = null;

        try
        {
            shellLinkObject = new ShellLink();
            var shellLink = (IShellLinkW)shellLinkObject;
            shellLink.SetPath(executablePath);
            shellLink.SetArguments(ApplicationLaunchArguments.WindowsStartup);
            shellLink.SetWorkingDirectory(
                Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
            );
            shellLink.SetDescription("Start PullWatch when Windows starts.");
            shellLink.SetIconLocation(executablePath, 0);

            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally
        {
            if (shellLinkObject is not null)
            {
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    private static StartupShortcutInspection InspectShortcut(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
        {
            return StartupShortcutInspection.Missing;
        }

        object? shellLinkObject = null;

        try
        {
            shellLinkObject = new ShellLink();
            ((IPersistFile)shellLinkObject).Load(shortcutPath, 0);

            var shellLink = (IShellLinkW)shellLinkObject;
            var targetPath = new StringBuilder(PathBufferLength);
            shellLink.GetPath(targetPath, targetPath.Capacity, IntPtr.Zero, 0);

            var resolvedTargetPath = targetPath.ToString();
            var targetExists =
                !string.IsNullOrWhiteSpace(resolvedTargetPath) && File.Exists(resolvedTargetPath);
            var executableInspection = targetExists
                ? InspectExecutable(resolvedTargetPath)
                : TargetExecutableInspection.Missing;

            return new StartupShortcutInspection(
                true,
                resolvedTargetPath,
                targetExists,
                executableInspection.Version,
                executableInspection.IsPullWatch
            );
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or COMException)
        {
            return new StartupShortcutInspection(true, null, false, null, false);
        }
        finally
        {
            if (shellLinkObject is not null)
            {
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    private static TargetExecutableInspection InspectExecutable(string executablePath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            var version = !string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
                ? versionInfo.ProductVersion
                : versionInfo.FileVersion;

            return new TargetExecutableInspection(
                version,
                IsPullWatchExecutable(executablePath, versionInfo)
            );
        }
        catch (Exception exception)
            when (exception
                    is ArgumentException
                        or FileNotFoundException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            return TargetExecutableInspection.Missing;
        }
    }

    private static bool IsPullWatchExecutable(string executablePath, FileVersionInfo versionInfo)
    {
        return string.Equals(
                Path.GetFileName(executablePath),
                "PullWatch.exe",
                StringComparison.OrdinalIgnoreCase
            )
            || string.Equals(
                versionInfo.ProductName,
                "PullWatch",
                StringComparison.OrdinalIgnoreCase
            )
            || string.Equals(
                versionInfo.FileDescription,
                "PullWatch",
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static void DeleteShortcut()
    {
        var shortcutPath = GetShortcutPath();

        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }
    }

    private static string GetShortcutPath()
    {
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

        if (string.IsNullOrWhiteSpace(startupFolder))
        {
            throw new InvalidOperationException("Windows Startup folder could not be found.");
        }

        return Path.Combine(startupFolder, ShortcutFileName);
    }

    private static string GetExecutablePath()
    {
        var executablePath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("PullWatch executable path could not be found.");
        }

        return Path.GetFullPath(executablePath);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    private sealed record TargetExecutableInspection(string? Version, bool IsPullWatch)
    {
        public static TargetExecutableInspection Missing { get; } = new(null, false);
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            uint fFlags
        );

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cchMaxName
        );

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cchMaxPath
        );

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cchMaxPath
        );

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cchIconPath,
            out int piIcon
        );

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
